using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // SYN/ACK 握手重发、30s 宽限期满移出原版误入玩家
    // (原 MatchController.Update 内的握手段逻辑收口)
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

            // 只有"连接确实已就绪 + 已进入房间并稳定 3 秒 + 对手仍在 joined 状态"三闸齐全才允许踢人
            // 之前是进房就起 10 秒倒计时且不看 socket 状态, socket 重连窗口期内 SYN 全丢就会误踢真人
            if (session.State == CsMatchState.WaitingOpponent && !session.OpponentVerified && _gw.IsHost)
            {
                bool connReady = _gw.LiveConnected;
                bool stableInRoom = session.OpponentJoinedTime > 0f && Time.realtimeSinceStartup - session.OpponentJoinedTime > 3f;
                bool oppIdle = IsOpponentStillJoined(_gw);
                // 宽限期从 socket 真正可用的时刻起算
                bool graceExpired = connReady
                    && session.LiveConnectedSince > 0f
                    && Time.realtimeSinceStartup - session.LiveConnectedSince > MatchSession.HandshakeGraceSeconds;
                if (connReady && stableInRoom && oppIdle && graceExpired)
                {
                    PcsrLog.LogInfo("[PeakCSR] 握手超时 " + MatchSession.HandshakeGraceSeconds + "s，移出未安装 PeakCSR 的误入玩家: " + session.OpponentName);
                    session.StatusMessage = CsrLoc.T("status.kick_vanilla", session.OpponentName);
                    string kickId = session.OpponentSteamId;
                    string kickName = session.OpponentName;
                    session.ResetOpponentInfo();
                    _gw.RemoveParticipant(kickId, kickName, null);
                }
            }
        }

        public void SendHandshakePacket(MatchSession session, string kind)
        {
            string ratedFlag = session.IsRatedMatch ? "1" : "0";
            int myElo = 1000;
            MatchCoordinator coord = MatchCoordinator.Instance;
            if (coord != null && coord.Profile != null) myElo = coord.Profile.CurrentElo;
            string msg = PcsrProtocol.PacketPrefix + kind + "|" + myElo + "|"
                + Plugin.PluginVersion + "|" + (int)session.Format + "|" + ratedFlag;
            _gw.SendChat(msg);
        }

        // 对手是否仍然在房间里且尚未开赛 (而不是已经跑起来或已离开)
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
                    return p.State == "joined" || p.State == "race_ready";
                }
            }
            catch
            {
            }
            return false;
        }
    }
}
