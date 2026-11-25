using UnityEngine;

namespace PeakCSR
{
    // Photon/Live 双信号健康监测、45s 断线观察窗、延迟退房队列
    // (原 MatchController.TickConnectivityWatch / TickDisconnectObserver / TickRetireLobby 收口)
    //
    // 关键: 掉线时 RANKED 未必立刻把 CurrentRace 置空 (socket 掉线但 HTTP 轮询仍能拿到旧快照),
    // 于是观察窗永远不会启动, 胜负判定还在拿"自己掉线前最后一帧"的数据判胜负,
    // 对手那边看到的则是"我不动了" -> 双方各自结算、各自 +- 分。这里必须主动检测本机连接
    public sealed class ConnectivityGuardian
    {
        private readonly IRankedPeakGateway _gw;
        private readonly MatchCoordinator _owner;

        public ConnectivityGuardian(IRankedPeakGateway gw, MatchCoordinator owner)
        {
            _gw = gw;
            _owner = owner;
        }

        // 延迟退房必须在状态判断之前驱动:
        // 作废后 _state 立刻变成 Finished, 如果放在后面就永远不会被执行。
        public void TickRetireLobby(MatchSession session)
        {
            if (session == null) return;
            if (session.RetireLobbyAt < 0f) return;
            if (Time.realtimeSinceStartup < session.RetireLobbyAt) return;

            try
            {
                // 可用性检查放在清除 deadline 之前
                if (_gw.ServiceAvailable == false) return;
                RankedPeak.Services.RaceInfo race = _gw.CurrentRace;
                if (race == null) return;

                session.RetireLobbyAt = -1f;
                if (race.Status == "finished") return;
                _gw.LeaveRace();
                PcsrLog.LogInfo("[PeakCSR] 作废局已退出房间，等待服务端回收。");
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 作废局退出房间失败（多半已断网，可忽略）: " + ex.Message);
                session.RetireLobbyAt = Time.realtimeSinceStartup + MatchSession.VoidRetryIntervalSeconds;
            }
        }

        // ===== 本机连接健康检测 =====
        public void TickConnectivityWatch(MatchSession session)
        {
            if (!IsConnectivityRelevant(session))
            {
                session.ConnectivityDownSince = -1f;
                session.WatchedPhase = CsMatchState.Idle;
                return;
            }

            // 阶段切换 (加载 -> 倒计时 -> 跑图) 时重新起算, 避免把建连/切场景的抖动算成掉线
            if (session.State != session.WatchedPhase)
            {
                session.WatchedPhase = session.State;
                session.PhaseEnteredAt = Time.realtimeSinceStartup;
                session.ConnectivityDownSince = -1f;
                return;
            }

            float settle = session.State == CsMatchState.LoadingLevel
                ? MatchSession.ConnectivitySettleLoadingSeconds
                : MatchSession.ConnectivitySettleRunningSeconds;
            if (Time.realtimeSinceStartup - session.PhaseEnteredAt < settle) return;

            // 最硬的掉线证据: 玩家进过对局地图, 现在却不在里面了
            // 开赛后服务端永久拒绝 join, 本地计时/进度/进度点全是运行时静态状态,
            // Photon 房间也已离开 —— 这种情况没有任何"重连回来接着打完"的可能,
            // 不该再空耗观察窗, 直接作废并把玩家送回主菜单看战报
            //
            // 关键前置: 必须真的进过一次地图
            if (session.EnteredMatchSceneOnce && !MatchTeardownService.IsInMatchScene())
            {
                if (session.DisconnectConfirmed) return;
                session.DisconnectConfirmed = true;

                // 已阵亡后离图是正常结局, 按判负收场而不是作废, 只有真的死了才算判负
                if (session.SelfDiedConcluded)
                {
                    PcsrLog.LogInfo("[PeakCSR] 玩家阵亡后离开对局地图，按判负结算。");
                    _owner.Settlement.ConcludeLocalLoss(session, CsrLoc.T("status.loss.dnf"));
                    return;
                }
                if (_owner.IsLocalDead() && _owner.Settlement.ConcludeSelfDeath(session))
                {
                    PcsrLog.LogInfo("[PeakCSR] 玩家阵亡后离开对局地图，按判负结算。");
                    return;
                }

                PcsrLog.LogWarning("[PeakCSR] 玩家已离开对局地图，无法再续局，立即作废本局。");
                _owner.Settlement.ConcludeVoid(session, CsrLoc.T("status.void_left_map"));
                return;
            }

            if (Time.realtimeSinceStartup < session.NextConnectivityCheck) return;
            session.NextConnectivityCheck = Time.realtimeSinceStartup + MatchSession.ConnectivityCheckIntervalSeconds;

            if (IsLocalConnectivityHealthy(session))
            {
                if (session.ConnectivityDownSince >= 0f)
                {
                    PcsrLog.LogInfo("[PeakCSR] 本机连接已恢复，撤销连接异常计时。");
                }
                session.ConnectivityDownSince = -1f;
                return;
            }

            if (session.ConnectivityDownSince < 0f)
            {
                session.ConnectivityDownSince = Time.realtimeSinceStartup;
                PcsrLog.LogWarning("[PeakCSR] 检测到本机对局连接异常，先观察 " + MatchSession.ConnectivityDownGraceSeconds + " 秒。");
                return;
            }

            if (Time.realtimeSinceStartup - session.ConnectivityDownSince < MatchSession.ConnectivityDownGraceSeconds) return;
            if (session.DisconnectConfirmed) return;

            // 断线确认: 先冻结胜负判定并进入观察窗
            // 不在这一刻直接作废, 是因为网络瞬断十几秒后恢复的情况很常见,
            // 一断就作废会把本来能打完的好局白白扔掉; 观察窗到期仍未恢复才真正作废
            session.DisconnectConfirmed = true;
            PcsrLog.LogWarning("[PeakCSR] 本机连接中断超过 " + MatchSession.ConnectivityDownGraceSeconds + "s，冻结胜负判定并进入观察窗。");
            _gw.PushSystemChat(CsrLoc.T("chat.void.announced"));
            BeginDisconnectObservation(session, "local connection lost");
        }

        // 只有真正进入对局阶段才需要看连接; 房间列表/握手阶段由各自动作处理
        private static bool IsConnectivityRelevant(MatchSession session)
        {
            if (session.State != CsMatchState.LoadingLevel
                && session.State != CsMatchState.Countdown
                && session.State != CsMatchState.Running) return false;
            if (string.IsNullOrEmpty(session.InviteCode)) return false;
            // 换图迁移中会主动退房再进新房, 期间场景必然短暂离开 Level_*,
            // 那不是掉线, 不能拿它当"已离开地图"的证据
            if (session.SwitchingRerollRoom) return false;
            return true;
        }

        public bool IsLocalConnectivityHealthy(MatchSession session)
        {
            // Photon 是强信号: 游戏本体已经掉线, 对局不可能继续
            bool photonOk = PhotonConnectivityProbe.IsPhotonHealthy();

            // RANKED 实时 socket 是弱信号: 只有"本局确实连上过"之后掉线才算数,
            // 否则开局建连慢会被误判成掉线, 把好局作废掉
            bool liveOk = true;
            if (_gw.LiveConnected)
            {
                session.LiveEverConnected = true;
            }
            else if (_gw.LiveActive && session.LiveEverConnected)
            {
                liveOk = false;
            }

            return photonOk && liveOk;
        }

        // ===== VOID 协议补发 =====
        //
        // 作废的那一刻本机 socket 往往正在抖动, 单次发送大概率丢掉,
        // 对手就会一直停在什么都收不到的状态里干等
        // 这里在短暂窗口内持续补发, 一旦发送成功就停
        // 发送失败完全无害, 所以整段都容错
        public void TickVoidResend(MatchSession session)
        {
            if (session == null) return;
            if (!session.VoidSubmitted) return;
            if (session.VoidBroadcasted) return;
            float now = Time.realtimeSinceStartup;
            if (now > session.VoidResendUntil) return;
            if (now < session.NextVoidResendAt) return;
            session.NextVoidResendAt = now + MatchSession.VoidResendIntervalSeconds;

            try
            {
                if (_gw.SendChat(PcsrProtocol.EncodeVoid()))
                {
                    session.VoidBroadcasted = true;
                    PcsrLog.LogInfo("[PeakCSR] 作废协议已送达对手。");
                }
            }
            catch
            {
            }
        }

        // ===== 房主侧对手移除兜底 (默认关闭) =====
        //
        // 现在只允许"我方已登顶 + 观察窗已到期 + 对手仍占着名额"这一种场景
        // 刻意用 static readonly 而非 const: const 会让编译器把后面的分支
        // 判成不可达并警告, 而这里需要保留完整实现以便需要时一键开启
        private static readonly bool EnableHostKickFallback = false;

        public void TickHostOpponentRemoval(MatchSession session)
        {
            // 默认关闭, 房间收尾由 VOID 退房链路负责
            if (EnableHostKickFallback) KickStaleOpponent(session);
        }

        private void KickStaleOpponent(MatchSession session)
        {
            if (session == null) return;
            if (session.OpponentRemovalAttempted) return;
            if (!session.DisconnectConfirmed) return;
            if (string.IsNullOrEmpty(session.OpponentSteamId)) return;
            if (!_gw.IsHost) return;

            // 观察窗还在走: 对方可能只是抖动
            if (session.ObservingDisconnect) return;
            if (Time.realtimeSinceStartup < session.DisconnectObserveUntil) return;

            // 本局已作废/已结算: 由 VOID 补发与延迟退房负责, 不需要踢人
            if (session.MatchVoided || session.VoidSubmitted) return;
            if (session.State != CsMatchState.Running) return;

            // 只有"我已经确定完赛、对手却仍占着名额"才需要清场
            // 否则一律留给观察窗到期走 ConcludeVoid, 双方都不计分
            if (session.SelfPeakMs <= 0) return;

            // 本机对局连接此刻不健康时也不踢
            if (!IsLocalConnectivityHealthy(session)) return;

            session.OpponentRemovalAttempted = true;
            try
            {
                _gw.RemoveParticipant(session.OpponentSteamId, session.OpponentName, delegate (string err)
                {
                    PcsrLog.LogInfo("[PeakCSR] 作废局移除掉线对手: " + (string.IsNullOrEmpty(err) ? "已提交" : err));
                });
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 移除掉线对手失败 (可忽略): " + ex.Message);
            }
        }

        // ===== 对手心跳停滞检测 =====
        //
        // 为什么必须有这一条:
        //   对手在本机掉线后, 服务端 race 会一直停在 running, participants 里对手仍然显示 running,
        //   AutoEndMs 也不会启动 (它只在有人正常 finished 之后才开始计时)。
        //   于是 EvaluateWinCondition 里 opp == null / opp.State == "dnf" 那些分支全都不命中,
        //   房主这边就变成"对手没了但完全不知道", 房间也永远不会被收走
        //
        //   唯一可靠的实时证据是 RANKED Live 每秒一帧的对手状态 (LiveState.ReceivedAt)。
        //   对手进程被切回主菜单时 socket 发不出 close, 服务端只能等自己的 ping 超时,
        //   所以也不能等 gone 消息 —— 必须自己按 ReceivedAt 计时
        //
        //   心跳不会因为玩家静止而停: LiveFrame 的差分里 TimeMs 取自 RaceTimer 的毫秒数,
        //   每 100ms 采样必变、必发, 所以 6 秒无更新只可能是对方真的不在了
        public void TickOpponentHeartbeat(MatchSession session)
        {
            if (session == null) return;

            if (!IsOpponentHeartbeatRelevant(session))
            {
                session.OpponentHeartbeatStaleSince = -1f;
                return;
            }
            if (!PhotonConnectivityProbe.IsLiveConnected()
                || !PhotonConnectivityProbe.IsLiveActive())
            {
                session.OpponentHeartbeatStaleSince = -1f;
                return;
            }

            // 从没连上过实时通道时无法判断: IsOpponentHeartbeatFresh 会返回 true,
            // 那个 true 表示"证据不足", 不能反过来当成"对手还在"来用
            if (!session.LiveEverConnected)
            {
                session.OpponentHeartbeatStaleSince = -1f;
                return;
            }

            // 开赛初期对手可能还没开始发状态, 避开这段抖动
            if (Time.realtimeSinceStartup - session.PhaseEnteredAt < MatchSession.ConnectivitySettleRunningSeconds)
            {
                session.OpponentHeartbeatStaleSince = -1f;
                return;
            }

            // 每帧只探测一次槽位状态, 后面所有判定都复用它
            PhotonConnectivityProbe.OpponentSlotState slot =
                PhotonConnectivityProbe.ProbeOpponentSlot(session.OpponentSteamId);
            if (slot == PhotonConnectivityProbe.OpponentSlotState.Present)
            {
                session.OpponentSlotSeenAt = Time.realtimeSinceStartup;
            }

            if (IsHeartbeatFreshWithSlot(session, slot))
            {
                if (session.OpponentHeartbeatStaleSince >= 0f)
                {
                    PcsrLog.LogInfo("[PeakCSR] 对手实时心跳已恢复，撤销掉线观察计时。");
                }
                session.OpponentHeartbeatStaleSince = -1f;
                session.OpponentHeartbeatStaleProbeCount = 0;
                session.NextHeartbeatStaleCheck = Time.realtimeSinceStartup;
                return;
            }

            // PING 仍在小额更新时, 说明对手进程还在 (可能只是卡读图/静止),
            // 不能仅凭 LiveState 心跳判定掉线
            if (session.OpponentPingCapable
                && session.OpponentLastPacketAt > 0f
                && Time.realtimeSinceStartup - session.OpponentLastPacketAt < MatchSession.OpponentPingStaleSeconds)
            {
                session.OpponentHeartbeatStaleSince = -1f;
                session.OpponentHeartbeatStaleProbeCount = 0;
                return;
            }

            if (session.OpponentHeartbeatStaleSince < 0f)
            {
                session.OpponentHeartbeatStaleSince = Time.realtimeSinceStartup;
                session.OpponentHeartbeatStaleProbeCount = 0;
                session.NextHeartbeatStaleCheck = Time.realtimeSinceStartup;
                // 单帧不新鲜很常见, 先记下起点, 连续超标才升级为观察窗
                return;
            }

            if (Time.realtimeSinceStartup - session.OpponentHeartbeatStaleSince < MatchSession.OpponentHeartbeatStaleSeconds) return;

            // 采样计数按时间节流
            if (Time.realtimeSinceStartup < session.NextHeartbeatStaleCheck) return;
            session.NextHeartbeatStaleCheck = Time.realtimeSinceStartup
                + MatchSession.OpponentHeartbeatStaleProbeIntervalSeconds;
            session.OpponentHeartbeatStaleProbeCount++;
            if (session.OpponentHeartbeatStaleProbeCount < MatchSession.OpponentHeartbeatStaleProbes) return;

            // PING 通道同样静默才算真掉线
            if (session.OpponentPingCapable
                && session.OpponentLastPacketAt > 0f
                && Time.realtimeSinceStartup - session.OpponentLastPacketAt < MatchSession.OpponentPingStaleSeconds)
            {
                session.OpponentHeartbeatStaleSince = -1f;
                session.OpponentHeartbeatStaleProbeCount = 0;
                return;
            }

            // 最后一道确认: 只对"槽位从表里消失"这一种情况做去抖
            //
            // 两种掉线形态必须区别对待:
            //   a) 槽位 Absent: 对方进程没了/被服务端移除。可能是 socket 抖动
            //      导致表短暂清空, 所以要求"消失时间够久"才认
            //   b) 槽位 Present 但帧长期不更新: 半开连接 (TCP 未断但对方不再发)
            //      这是强证据, 绝不能被去抖拦住 —— 否则对手掉线永远检测不到
            if (slot == PhotonConnectivityProbe.OpponentSlotState.Absent
                && session.OpponentSlotSeenAt > 0f
                && Time.realtimeSinceStartup - session.OpponentSlotSeenAt < MatchSession.OpponentHeartbeatStaleSeconds)
            {
                // 槽位刚消失不久: 可能只是 socket 抖动, 再等一轮
                return;
            }

            // 已确认对手掉线: 冻结胜负判定并进入观察窗
            session.DisconnectConfirmed = true;
            PcsrLog.LogWarning("[PeakCSR] 对手实时心跳与 PING 通道同时停滞超过"
                + MatchSession.OpponentHeartbeatStaleSeconds
                + "s 且服务端仍显示 running，判定为对手掉线，冻结胜负判定并进入观察窗。");
            BeginDisconnectObservation(session, "opponent heartbeat stale");
        }

        // ===== 幽灵开赛防护 =====
        public void TickGhostStartGuard(MatchSession session)
        {
            if (session == null) return;
            if (session.OpponentVerified) return;
            if (session.MatchVoided || session.VoidSubmitted) return;
            if (session.DisconnectConfirmed) return;
            if (string.IsNullOrEmpty(session.InviteCode)) return;
            if (session.State != CsMatchState.LoadingLevel && session.State != CsMatchState.Countdown)
            {
                // 离开这两个阶段就重置计时, 下次进入重新给宽限
                session.GhostGuardSince = -1f;
                return;
            }

            float now = Time.realtimeSinceStartup;
            if (session.GhostGuardSince < 0f)
            {
                session.GhostGuardSince = now;
                return;
            }

            // 宽限期: 对手可能只是载图慢, 给它完整的握手窗口
            if (now - session.GhostGuardSince < MatchSession.HandshakeGraceSeconds) return;

            // 对手仍有实时证据 (心跳新鲜 / 协议包在到达) 说明它确实进了局,
            // 只是握手包丢了, 交给握手重发处理, 不能判定成幽灵开赛
            if (session.OpponentLastPacketAt > 0f
                && now - session.OpponentLastPacketAt < MatchSession.OpponentPingStaleSeconds)
            {
                return;
            }
            if (!string.IsNullOrEmpty(session.OpponentSteamId)
                && _gw.IsOpponentHeartbeatFresh(session.OpponentSteamId))
            {
                return;
            }

            PcsrLog.LogWarning("[PeakCSR] 已进入 " + session.State + " 但对手超过 "
                + MatchSession.HandshakeGraceSeconds + "s 仍无任何实时证据 (房间 "
                + session.InviteCode + ")，判定为幽灵开赛，本局作废并退回匹配。");
            _gw.PushSystemChat(CsrLoc.T("chat.void.ghost_start"));
            _owner.Settlement.ConcludeVoid(session, CsrLoc.T("status.void_ghost_start"));
        }

        // 只在"本局确实在跑、双方都还没定结果"时才有意义
        private static bool IsOpponentHeartbeatRelevant(MatchSession session)
        {
            if (session.State != CsMatchState.Running) return false;
            if (session.DisconnectConfirmed || session.MatchVoided || session.VoidSubmitted) return false;
            if (session.ObservingDisconnect) return false;
            // 我方已登顶: 名次已定, 对手掉线与否都不该把这一局改判成作废, 交给仲裁/收尾
            if (session.SelfPeaked) return false;
            // 对手已经明确完赛/阵亡: 已有确定性结论, 心跳停了也不影响
            if (session.OppPeakMs > 0 || session.OppDiedReceived) return false;
            if (string.IsNullOrEmpty(session.OpponentSteamId)) return false;
            if (string.IsNullOrEmpty(session.InviteCode)) return false;
            if (session.SwitchingRerollRoom) return false;
            return true;
        }

        // ===== 断线观察窗 =====
        // race 消失不等于判负: 掉线/被踢/换房/socket 看门狗都会让 CurrentRace 变 null
        // requireLiveEvidence: 本局从未建立过实时通道时置 true
        // 此时 Photon 健康不能当作"恢复"证据 (Photon 本来就没断),
        // 只有实时通道真的连上 (LiveEverConnected) 才允许撤销观察窗
        public void BeginDisconnectObservation(MatchSession session, string reason, bool requireLiveEvidence = false)
        {
            if (session.ObservingDisconnect) return;
            session.ObservingDisconnect = true;
            session.ObservationNeedsLiveEvidence = requireLiveEvidence;
            session.DisconnectObserveUntil = Time.realtimeSinceStartup + MatchSession.DisconnectObserveSeconds;
            session.DisconnectReason = reason ?? "";
            session.StatusMessage = CsrLoc.T("status.reconnecting", Mathf.CeilToInt(MatchSession.DisconnectObserveSeconds));
            PcsrLog.LogWarning("[PeakCSR] 对局连接疑似中断 (" + session.DisconnectReason + ")，进入 " + MatchSession.DisconnectObserveSeconds + " 秒观察窗，暂不结算。");
            session.Notify();
        }

        // 观察窗恢复判定的唯一实现: RaceUpdated 回调与 guardian tick 必须共用同一套证据
        // 返回 true 表示本次确实撤销了观察窗 (调用方不要再重复处理)
        public bool TryRecoverFromObservation(MatchSession session, RankedPeak.Services.RaceInfo race)
        {
            if (session == null || !session.ObservingDisconnect) return false;
            if (race == null) return false;
            // 已经收尾的房间不算"恢复": 那份快照属于旧局, 拿它续局会按旧数据结算
            if (race.Status == "finished") return false;
            if (session.EnteredMatchSceneOnce && !MatchTeardownService.IsInMatchScene()) return false;
            if (!IsRecoveryEvidenced(session)) return false;
            if (!IsLocalConnectivityHealthy(session)) return false;

            // 本机健康 + 服务端快照存在都不足以证明对端还在
            // 必须拿到对端自己的实时证据, 否则不放行
            if (!HasOpponentLivenessEvidence(session)) return false;

            // 反复撤销说明对端其实一直不在, 不能无限循环, 直接放行给到期判定
            if (session.DisconnectRecoverCount >= MatchSession.MaxDisconnectRecovers)
            {
                PcsrLog.LogWarning("[PeakCSR] 观察窗已撤销 " + session.DisconnectRecoverCount
                    + " 次仍反复中断，不再撤销，交由观察窗到期收场。");
                return false;
            }

            session.ObservingDisconnect = false;
            session.ObservationNeedsLiveEvidence = false;
            session.DisconnectReason = "";
            session.DisconnectRecoverCount++;
            ClearDisconnectFreeze(session);
            PcsrLog.LogInfo("[PeakCSR] 观察窗内连接已恢复，继续对局，不做任何结算。");
            _gw.PushSystemChat(CsrLoc.T("chat.void.recovered"));
            return true;
        }

        // 基于"已经探测好的槽位状态"判断心跳新鲜度
        private bool IsHeartbeatFreshWithSlot(MatchSession session, PhotonConnectivityProbe.OpponentSlotState slot)
        {
            if (slot == PhotonConnectivityProbe.OpponentSlotState.Unknown) return true;
            if (slot == PhotonConnectivityProbe.OpponentSlotState.Absent) return false;
            return _gw.IsOpponentHeartbeatFresh(session.OpponentSteamId);
        }

        // 对端存活证据: 实时心跳新鲜, 或最近收到过对端的 PCSR 协议包
        private bool HasOpponentLivenessEvidence(MatchSession session)
        {
            if (string.IsNullOrEmpty(session.OpponentSteamId)) return false;
            PhotonConnectivityProbe.OpponentSlotState slot =
                PhotonConnectivityProbe.ProbeOpponentSlot(session.OpponentSteamId);
            if (IsHeartbeatFreshWithSlot(session, slot)) return true;
            return session.OpponentLastPacketAt > 0f
                && Time.realtimeSinceStartup - session.OpponentLastPacketAt < MatchSession.OpponentPingStaleSeconds;
        }

        public void TickDisconnectObserver(MatchSession session)
        {
            if (session == null) return;
            if (!session.ObservingDisconnect) return;

            // 恢复: 必须连接真的恢复了才算, 只看到"还拿着旧 RaceInfo"不能撤销观察窗,
            // 否则掉线后会因为本地缓存快照而误判"已恢复", 进而继续按旧数据结算。
            try
            {
                RankedPeak.Services.RaceInfo race = _gw.CurrentRace;
                if (TryRecoverFromObservation(session, race))
                {
                    if (session.State != CsMatchState.Finished) session.SyncFromRaceInfo(race);
                    return;
                }
            }
            catch
            {
            }

            if (Time.realtimeSinceStartup < session.DisconnectObserveUntil)
            {
                session.StatusMessage = CsrLoc.T("status.reconnecting", Mathf.CeilToInt(session.DisconnectObserveUntil - Time.realtimeSinceStartup));
                return;
            }

            session.ObservingDisconnect = false;
            session.ObservationNeedsLiveEvidence = false;

            // 我方已经登顶完赛、对手却始终没有宣告也连不回来:
            // 这是"我先到"的确定性结论, 不该被判作废
            // 只有双方都无法确认结果时才走作废
            if (session.SelfPeakMs > 0 && session.OppPeakMs <= 0 && !session.MatchVoided)
            {
                PcsrLog.LogWarning("[PeakCSR] 观察窗到期，我方已登顶完毕且对手未宣告，判我方胜。");
                _owner.Settlement.ConcludeMatch(session, true,
                    CsrLoc.T("status.win.peak", CsrFormat.FormatClock(session.SelfPeakMs / 1000.0)));
                return;
            }

            PcsrLog.LogWarning("[PeakCSR] 观察窗到期仍未恢复，本局作废（双方不计分、不计胜负）。");
            // 作废播报放在这里: 只有真正到期才播, 避免抖动期误报
            _gw.PushSystemChat(CsrLoc.T("chat.void.announced"));
            _owner.Settlement.ConcludeVoid(session, CsrLoc.T("status.void_disconnect", session.DisconnectReason));
        }
        private bool IsRecoveryEvidenced(MatchSession session)
        {
            if (!session.ObservationNeedsLiveEvidence) return true;
            if (session.LiveEverConnected || _gw.LiveConnected) return true;
            // 观察窗内收到过对手的实时包: 这条证据比 socket 标志更硬
            return session.OpponentLastPacketAt > 0f
                && Time.realtimeSinceStartup - session.OpponentLastPacketAt < MatchSession.OpponentPingStaleSeconds;
        }

        // 观察窗内连接恢复时解除冻结, 让对局可以正常继续
        public void ClearDisconnectFreeze(MatchSession session)
        {
            session.DisconnectConfirmed = false;
            session.ConnectivityDownSince = -1f;
            session.NextConnectivityCheck = 0f;
            session.OpponentHeartbeatStaleSince = -1f;
            session.OpponentHeartbeatStaleProbeCount = 0;
            session.NextHeartbeatStaleCheck = 0f;
            // 房主侧移除对手只做一次, 撤销后必须复位, 否则下一次真掉线不会再补救
            session.OpponentRemovalAttempted = false;
        }
    }
}
