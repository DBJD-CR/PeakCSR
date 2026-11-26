using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // SYN/ACK 握手重发、30s 宽限期满移出原版误入玩家
    // (原 MatchController.Update 内的握手段逻辑收口)
    //
    // 移出判据的条件:
    //   1) 本机实时通道确实连上了 (connReady)
    //   2) 对手进房后已稳定停留 3s 以上 (stableInRoom)
    //   3) 从"对手到达 / 连接就绪"较晚的那个时刻起, 满 HandshakeGraceSeconds
    //   4) 对手在这段时间内一个 PCSR 包都没到过 (channelSilent)
    public sealed class HandshakeNegotiator
    {
        private readonly IRankedPeakGateway _gw;
        private readonly MatchCoordinator _owner;

        public HandshakeNegotiator(IRankedPeakGateway gw, MatchCoordinator owner)
        {
            _gw = gw;
            _owner = owner;
        }

        public void Tick(MatchSession session)
        {
            if (string.IsNullOrEmpty(session.OpponentSteamId)) return;
            if (session.State != CsMatchState.WaitingOpponent
                && session.State != CsMatchState.LoadingLevel
                && session.State != CsMatchState.Countdown)
            {
                return;
            }

            if ((!session.OpponentVerified || !session.HandshakeAcked) && Time.realtimeSinceStartup >= session.NextHandshakeSendTime)
            {
                session.NextHandshakeSendTime = Time.realtimeSinceStartup + 1.5f;
                SendHandshakePacket(session, session.OpponentVerified ? "ACK" : "SYN");
            }

            if (session.State != CsMatchState.WaitingOpponent) return;
            if (session.OpponentVerified) return;
            if (!_gw.IsHost) return;

            // 刚被我们移出过同一名玩家: 冷却期内不再动手
            if (session.IsRecentlyKicked(session.OpponentSteamId)) return;

            bool connReady = _gw.LiveConnected;
            bool stableInRoom = session.OpponentJoinedTime > 0f
                && Time.realtimeSinceStartup - session.OpponentJoinedTime > 3f;
            bool oppIdle = IsOpponentStillJoined(_gw);

            // 宽限期从"对手到达与连接就绪中较晚的那个"起算
            float graceStart = connReady ? session.LiveConnectedSince : -1f;
            if (session.OpponentJoinedTime > graceStart) graceStart = session.OpponentJoinedTime;
            bool graceExpired = connReady
                && graceStart > 0f
                && Time.realtimeSinceStartup - graceStart > MatchSession.HandshakeGraceSeconds;

            // 对手只要还在发包就说明它装着 PCSR, 绝不能当成"未安装"踢掉
            bool channelSilent = !IsOpponentChannelAlive(session);

            if (!(connReady && stableInRoom && oppIdle && graceExpired && channelSilent)) return;

            // 反向护栏: 回读通道从未收到过任何 PCSR 包时, "对方沉默"同样可以由
            // 本机收包链路没建好来解释。这种局面下降级为只重发握手, 不踢人
            if (!session.EverReceivedPcsrPacket)
            {
                PcsrLog.LogWarning("[PeakCSR] 握手宽限已满, 但本机实时通道从未收到过任何 PCSR 包,"
                    + " 无法排除是本机收包链路未就绪, 暂不移出对手 (对手=" + session.OpponentName + ")。");
                return;
            }

            // 移出前留一份可排查现场: 这一行能直接区分"我没发出去"和"对方真没收到"
            PcsrLog.LogWarning("[PeakCSR] 握手宽限 " + MatchSession.HandshakeGraceSeconds
                + "s 内未收到对手任何 PCSR 包，移出房间。对手=" + session.OpponentName
                + " steamId=" + session.OpponentSteamId
                + " 已重发SYN=" + session.HandshakeSendCount + "次"
                + " 其中成功交给实时连接=" + session.HandshakeSendOkCount + "次"
                + " 本机Live=" + (_gw.LiveConnected ? "已连" : "未连")
                + " 房间=" + session.InviteCode);

            session.StatusMessage = CsrLoc.T("status.kick_vanilla", session.OpponentName);
            string kickId = session.OpponentSteamId;
            string kickName = session.OpponentName;
            session.BeginOpponentKick(kickId);
            session.ResetOpponentInfo();
            _gw.RemoveParticipant(kickId, kickName, delegate (string err)
            {
                // 移除没落地时立刻撤销冷却
                if (string.IsNullOrEmpty(err)) return;
                PcsrLog.LogWarning("[PeakCSR] 移出对手失败 (" + err + ")，撤销本次冷却以便重试。");
                session.RollbackOpponentKick();
            });
        }

        // 对手通道是否仍然活着 (收到过任意 PCSR 前缀包, 与能否解析无关)
        private static bool IsOpponentChannelAlive(MatchSession session)
        {
            return session.OpponentLastPacketAt > 0f
                && Time.realtimeSinceStartup - session.OpponentLastPacketAt < MatchSession.OpponentPingStaleSeconds;
        }

        public void SendHandshakePacket(MatchSession session, string kind)
        {
            string ratedFlag = session.IsRatedMatch ? "1" : "0";
            int myElo = 1000;
            MatchCoordinator coord = MatchCoordinator.Instance;
            if (coord != null && coord.Profile != null) myElo = coord.Profile.CurrentElo;
            string msg = PcsrProtocol.PacketPrefix + kind + "|" + myElo + "|"
                + Plugin.PluginVersion + "|" + (int)session.Format + "|" + ratedFlag;
            session.HandshakeSendCount++;
            if (_gw.SendChat(msg))
            {
                session.HandshakeSendOkCount++;
            }
        }

        // 对手是否仍然在房间里且尚未开赛 (而不是已经跑起来或已离开)
        //
        // 状态白名单只用来排除"已经进图/已离开"
        // 遇到未识别的新状态名时一律按"仍在大厅"处理
        public static bool IsOpponentStillJoined(IRankedPeakGateway gw)
        {
            try
            {
                RaceInfo race = gw.CurrentRace;
                if (race == null || race.Participants == null) return false;
                string mySteamId = gw.MySteamId;
                for (int i = 0; i < race.Participants.Count; i++)
                {
                    RaceParticipant p = race.Participants[i];
                    if (p == null || p.Removed || p.IsSpectator) continue;
                    if (p.SteamId == mySteamId) continue;
                    if (string.IsNullOrEmpty(p.State)) return true;
                    if (p.State == "running" || p.State == "finished" || p.State == "dnf") return false;
                    return true;
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
