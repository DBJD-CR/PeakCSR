using System;
using System.Reflection;
using HarmonyLib;
using PeakCSR.Practice;
using Photon.Realtime;
using UnityEngine;

namespace PeakCSR
{
    // 联机韧性: 只做"降低掉线概率"与"掉线后干净收场"
    //
    // 明确不做的事 (已用实测日志证伪):
    //   1) 不在载图前等待 Photon 进房
    //      NetworkConnector 是场景内对象, 它的 Start() 才触发 CreateRoom。
    //      载图前房间根本不存在, 等待只会白等满额超时再退化载图, 纯负优化。
    //   2) 不做 ReconnectAndRejoin 自动回房
    //      服务端 status 进入 running 后永久拒绝 join (实测回执"竞速已经开始了");
    //      Character / RaceTimer / 进度点全是本地运行时状态, 重新进去也接不上;
    //      且 rejoin 成功会触发原版 OnLeftRoom -> LoadScene(rejoinScene = "Title"),
    //      反而把还在跑图的玩家直接踢回主菜单。
    //      结论: 一局一旦掉线就是不可恢复的, 只应作废并干净退场。
    public static class PhotonResilience
    {
        // 玩家掉线后在房间里保留 actor 的时长 (毫秒)
        // 房主侧房间保活: 网络抖一下不至于让对局房间瞬间蒸发
        public const int PlayerTimeToLiveMs = 120000;

        // 房间无人在线时的保留时长, 让房主闪断期间房间不消失
        public const int EmptyRoomTimeToLiveMs = 120000;

        // 断线判定阈值 (毫秒). Photon 默认 10000
        // 这是唯一直接减少"误判掉线次数"的参数, 弱网下放宽很有价值
        public const int DisconnectTimeoutMs = 15000;

        // 遮罩超过这个时长还挂着且 Photon 不健康, 就认定载图协程已经挂死
        public const float LoadingScreenStuckSeconds = 60f;

        // 是否处于 PCSR 赛局相关流程
        //
        // 这里刻意不用 State >= LoadingLevel 判定:
        // 房主建房时 HostRoomOptions 先于 RACE 状态回填触发, 用状态判断会漏掉最关键的一次。
        public static bool IsCsrSession()
        {
            try
            {
                // 练习态走自己的生命周期, 不套用排位会话的断线保活/作废语义
                if (PracticeRunFlag.IsActive) return false;
                MatchCoordinator mc = MatchCoordinator.Instance;
                if (mc == null) return false;
                MatchSession s = mc.CurrentSession;
                if (s == null) return false;
                if (s.State == CsMatchState.Idle || s.State == CsMatchState.Finished) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool _tuned;

        // 应用一次性传输层参数. 幂等, 可重复调用
        public static void ApplyTransportTuning()
        {
            if (_tuned) return;
            try
            {
                // 切到后台时继续保活 60 秒: Alt+Tab / 最小化不再被误判掉线
                Photon.Pun.PhotonNetwork.KeepAliveInBackground = 60f;

                LoadBalancingClient client = Photon.Pun.PhotonNetwork.NetworkingClient;
                if (client != null && client.LoadBalancingPeer != null)
                {
                    client.LoadBalancingPeer.DisconnectTimeout = DisconnectTimeoutMs;
                    client.LoadBalancingPeer.QuickResendAttempts = 5;
                }
                _tuned = true;
                PcsrLog.LogInfo("[PeakCSR] Photon 传输层加固完成 (断线阈值 " + DisconnectTimeoutMs
                    + "ms, 后台保活 60s, 快速重传 5 次)。");
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] Photon 传输层加固失败 (不影响对局): " + ex.Message);
            }
        }

        // ===== 加载遮罩兜底 =====
        //
        // 原版 LoadingRoutine 只有在所有 process 跑完才会销毁遮罩,
        // 而 LoadSceneProcess 里的 while 循环没有超时保护。
        // Photon 一断, LevelLoadingProgress 永远到不了 1, 协程永久挂起;
        // 又因为 LoadingScreen.Awake 调了 DontDestroyOnLoad, 遮罩跨场景存活,
        // 于是"已经回主菜单了但画面还糊着"。
        //
        // 只有当 Photon 确实不健康时才动手, 正常慢载图一律交给原版。
        private static float _loadingStuckSince = -1f;

        public static void TickLoadingScreenWatchdog()
        {
            bool loading;
            try { loading = LoadingScreenHandler.loading; }
            catch { return; }

            if (!loading)
            {
                _loadingStuckSince = -1f;
                return;
            }

            if (_loadingStuckSince < 0f)
            {
                _loadingStuckSince = Time.realtimeSinceStartup;
                return;
            }

            if (Time.realtimeSinceStartup - _loadingStuckSince < LoadingScreenStuckSeconds) return;

            if (!IsCsrSession())
            {
                _loadingStuckSince = -1f;
                return;
            }

            // 正常慢载图: Photon 健康说明协程没死, 交给原版
            if (PhotonConnectivityProbe.IsPhotonHealthy())
            {
                _loadingStuckSince = -1f;
                return;
            }

            _loadingStuckSince = -1f;
            PcsrLog.LogWarning("[PeakCSR] Photon 已断开且加载遮罩挂起超过 "
                + (int)LoadingScreenStuckSeconds + "s，强制清理遮罩。");
            ForceKillLoadingScreen();
        }

        public static void ForceKillLoadingScreen()
        {
            try
            {
                if (!LoadingScreenHandler.loading) return;
            }
            catch
            {
                return;
            }

            try { LoadingScreenHandler.KillCurrentLoadingScreen(); }
            catch (Exception ex) { PcsrLog.LogWarning("[PeakCSR] 销毁遮罩失败: " + ex.Message); }

            // 复位 private static bool _loading, 否则后续 Load() 全部被拒
            // (原版会直接报 "Tried to load while already loading!")
            try
            {
                FieldInfo flag = typeof(LoadingScreenHandler)
                    .GetField("_loading", BindingFlags.Static | BindingFlags.NonPublic);
                if (flag != null) flag.SetValue(null, false);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 复位 loading 标志失败: " + ex.Message);
            }
        }
    }

    // 给原版 Photon 房间补上 PlayerTtl / EmptyRoomTtl
    //
    // 原版 HostRoomOptions() 只填了 3 个字段, 两个 TTL 都留在默认值 0,
    // 等于"谁断线谁立刻从房间消失"。给房间加保活可以让短暂抖动不至于
    // 让对手立刻看到本机从参与者列表蒸发(那会直接触发作废判定)。
    [HarmonyPatch(typeof(Peak.Network.NetworkingUtilities), "HostRoomOptions")]
    public static class RoomOptionsResiliencePatch
    {
        public static void Postfix(ref RoomOptions __result)
        {
            if (__result == null) return;
            if (!PhotonResilience.IsCsrSession()) return;
            try
            {
                __result.PlayerTtl = PhotonResilience.PlayerTimeToLiveMs;
                __result.EmptyRoomTtl = PhotonResilience.EmptyRoomTimeToLiveMs;
                PcsrLog.LogInfo("[PeakCSR] 房间已设置断线保活: PlayerTtl="
                    + __result.PlayerTtl + "ms, EmptyRoomTtl=" + __result.EmptyRoomTtl + "ms。");
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 写入房间 TTL 失败: " + ex.Message);
            }
        }
    }

    // 对局中 Photon 异常断开: 不做自动回房, 只把这场判成干净作废
    //
    // 原版 OnDisconnected 会弹"未能连接至 Photon 网络"并停在游戏内,
    // 遮罩也可能永久挂起。这里先让 PCSR 自己收场(作废+清遮罩),
    // 再交还原版走它自己的断线提示与回主菜单逻辑。
    [HarmonyPatch(typeof(NetworkConnector), "OnDisconnected")]
    public static class PhotonDisconnectVoidPatch
    {
        public static void Prefix(DisconnectCause cause)
        {
            try
            {
                if (Photon.Pun.PhotonNetwork.OfflineMode) return;
                // 练习态不参与排位作废: 离线/在线切换的瞬间 OfflineMode 可能已置 false
                if (PracticeRunFlag.IsActive) return;
                if (PracticeHandoffGuard.IsSwitchingToRanked) return;
                // 玩家主动离开不参与
                if (cause == DisconnectCause.DisconnectByClientLogic) return;
                if (!PhotonResilience.IsCsrSession()) return;

                MatchCoordinator mc = MatchCoordinator.Instance;
                MatchSession session = mc != null ? mc.CurrentSession : null;
                if (session == null) return;
                if (session.MatchVoided || session.VoidSubmitted) return;
                // 还没进过地图就断开: 交给原版, 此时还没有"正在跑的对局"
                if (!session.EnteredMatchSceneOnce) return;

                PcsrLog.LogWarning("[PeakCSR] 对局中 Photon 断开 (" + cause
                    + ")，本局不可续接，直接作废。");
                PhotonResilience.ForceKillLoadingScreen();
                if (session.State != CsMatchState.Finished)
                {
                    mc.Settlement.ConcludeVoid(session, CsrLoc.T("status.void_left_map"));
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] Photon 断线收场处理异常: " + ex.Message);
            }
        }
    }
}
