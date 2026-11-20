using UnityEngine;

namespace PeakCSR
{
    // Photon/Live 双信号健康监测、45s 断线观察窗、延迟退房队列
    // (原 MatchController.TickConnectivityWatch / TickDisconnectObserver / TickRetireLobby 收口)
    //
    // 关键: 掉线时 RANKED 未必立刻把 CurrentRace 置空 (socket 掉线但 HTTP 轮询仍能拿到旧快照),
    // 于是观察窗永远不会启动, 胜负判定还在拿"自己掉线前最后一帧"的数据判胜负,
    // 对手那边看到的则是"我不动了" -> 双方各自结算、各自 +- 分。这里必须主动检测本机连接。
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

            // 断线确认: 先冻结胜负判定并进入观察窗。
            // 不在这一刻直接作废, 是因为网络瞬断十几秒后恢复的情况很常见,
            // 一断就作废会把本来能打完的好局白白扔掉; 观察窗到期仍未恢复才真正作废。
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
            return true;
        }

        public bool IsLocalConnectivityHealthy(MatchSession session)
        {
            // Photon 是强信号: 游戏本体已经掉线, 对局不可能继续
            bool photonOk = PhotonConnectivityProbe.IsPhotonHealthy();

            // RANKED 实时 socket 是弱信号: 只有"本局确实连上过"之后掉线才算数,
            // 否则开局建连慢会被误判成掉线, 把好局作废掉。
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

        // ===== 断线观察窗 =====
        // race 消失不等于判负: 掉线/被踢/换房/socket 看门狗都会让 CurrentRace 变 null
        public void BeginDisconnectObservation(MatchSession session, string reason)
        {
            if (session.ObservingDisconnect) return;
            session.ObservingDisconnect = true;
            session.DisconnectObserveUntil = Time.realtimeSinceStartup + MatchSession.DisconnectObserveSeconds;
            session.DisconnectReason = reason ?? "";
            session.StatusMessage = CsrLoc.T("status.reconnecting", Mathf.CeilToInt(MatchSession.DisconnectObserveSeconds));
            PcsrLog.LogWarning("[PeakCSR] 对局连接疑似中断 (" + session.DisconnectReason + ")，进入 " + MatchSession.DisconnectObserveSeconds + " 秒观察窗，暂不结算。");
            session.Notify();
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
                if (race != null && race.Status != "finished" && IsLocalConnectivityHealthy(session))
                {
                    session.ObservingDisconnect = false;
                    session.DisconnectReason = "";
                    ClearDisconnectFreeze(session);
                    PcsrLog.LogInfo("[PeakCSR] 观察窗内连接已恢复，继续对局，不做任何结算。");
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
            PcsrLog.LogWarning("[PeakCSR] 观察窗到期仍未恢复，本局作废（双方不计分、不计胜负）。");
            _owner.Settlement.ConcludeVoid(session, CsrLoc.T("status.void_disconnect", session.DisconnectReason));
        }

        // 观察窗内连接恢复时解除冻结, 让对局可以正常继续
        public void ClearDisconnectFreeze(MatchSession session)
        {
            session.DisconnectConfirmed = false;
            session.ConnectivityDownSince = -1f;
            session.NextConnectivityCheck = 0f;
        }
    }
}
