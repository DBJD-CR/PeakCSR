using System;
using HarmonyLib;
using RankedPeak.Services;
using RankedPeak.Timer;

namespace PeakCSR
{
    // 本地赛段采集 (双挂点)
    //
    // 挂点 A: Campfire.Light_Rpc
    //   这是游戏本体唯一的段推进入口 (updateSegment 为 true 时 GoToSegment(advanceToSegment)),
    //   也是迷你跑的终点入口 (IsMiniRunEndingCampfire 时 EndGame)。
    //   事件驱动, 不受索引跳变污染, 是本地分段的权威来源。
    //
    // 挂点 B: MountainProgressHandler.CheckProgress
    //   补充来源: 有些段没有篝火只有进度点; 只在索引真的前进时记一条。

    [HarmonyPatch(typeof(MountainProgressHandler), "CheckProgress")]
    public static class SplitCapturePatch
    {
        private static int _lastRecorded = -1;
        private static bool _baselineCaptured;

        // 新一局开始: 清游标, 但基线要等计时器真正跑起来才抓
        public static void ResetTracking()
        {
            _lastRecorded = -1;
            _baselineCaptured = false;
        }

        public static void Postfix()
        {
            try
            {
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;

                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null) return;

                int reached = handler.maxProgressPointReached;
                if (reached < 0) return;

                MatchCoordinator coordinator = MatchCoordinator.Instance;
                MatchSession session = coordinator != null ? coordinator.CurrentSession : null;
                if (session == null) return;

                RaceTimer timer = RaceTimer.Instance;
                if (timer == null) return;
                // 计时器还没真正开始跑 (苏醒动画 / 开赛前) 时不记, 也不推进游标
                if (timer.State != TimerState.Running && timer.State != TimerState.Finished) return;

                // 基线: 第一次进入 Running 时, 把当前索引当作"起点", 不产生赛段记录。
                if (!_baselineCaptured)
                {
                    _baselineCaptured = true;
                    _lastRecorded = reached;
                    return;
                }

                if (reached <= _lastRecorded) return;

                long ms = timer.CurrentElapsedMs;
                if (ms <= 0) return;

                string steamId = coordinator.Gateway.MySteamId;
                if (string.IsNullOrEmpty(steamId)) return;

                string key = ResolveSegmentKey(coordinator, reached);
                session.Splits.ObserveLocalSegment(steamId, reached, key, ms);
                _lastRecorded = reached;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 分段打卡采集异常: " + ex.Message);
            }
        }

        // 段名一律取真实进度点的 title
        internal static string ResolveSegmentKey(MatchCoordinator coordinator, int absoluteIndex)
        {
            try
            {
                IRankedPeakGateway gw = coordinator != null ? coordinator.Gateway : null;
                if (gw == null) return "";
                ProgressPointInfo[] pts = gw.GetProgressPoints();
                if (pts == null || absoluteIndex < 0 || absoluteIndex >= pts.Length) return "";
                return BiomeSegmenter.NormalizeTitle(pts[absoluteIndex].Title);
            }
            catch
            {
                return "";
            }
        }

        // 供篝火挂点复用: 找一个 Segment 对应的"段终点进度点索引"
        internal static int ResolveBoundaryIndex(MatchCoordinator coordinator, int absoluteIndexHint)
        {
            return absoluteIndexHint < 0 ? 0 : absoluteIndexHint;
        }
    }

    // 换局/重进地图时把采集游标清干净
    [HarmonyPatch(typeof(MountainProgressHandler), "InitProgressPoints")]
    public static class SplitCaptureResetPatch
    {
        public static void Postfix()
        {
            SplitCapturePatch.ResetTracking();
            try
            {
                MatchCoordinator coordinator = MatchCoordinator.Instance;
                MatchSession session = coordinator != null ? coordinator.CurrentSession : null;
                if (session != null) session.InvalidateRouteSpan();
            }
            catch
            {
            }
        }
    }

    // 篝火事件挂点: 本地赛段的权威来源
    [HarmonyPatch(typeof(Campfire), "Light_Rpc")]
    public static class CampfireSplitPatch
    {
        // Prefix 记下"马上就要完成的段" (GoToSegment 会改变 currentSegment)
        private static string _pendingKey;
        private static int _pendingBoundary;
        private static bool _pendingValid;
        private static bool _pendingIsMiniRunEnd;

        public static void Prefix(Campfire __instance, bool updateSegment)
        {
            _pendingValid = false;
            _pendingKey = null;
            _pendingBoundary = 0;
            _pendingIsMiniRunEnd = false;

            try
            {
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;
                if (__instance == null) return;

                // 迷你跑终点篝火: 点亮即 EndGame, 它就是本局路线的终点段
                if (IsMiniRunEnding(__instance))
                {
                    _pendingIsMiniRunEnd = true;
                    _pendingValid = true;
                    _pendingKey = ResolveRouteEndKey();
                    _pendingBoundary = ResolveRouteEndBoundary();
                    return;
                }

                // updateSegment == false 的篝火 (LightWithoutReveal / 复活点) 不推进段, 不记段
                if (!updateSegment) return;

                // 段名 = 即将被完成的那个段
                int currentSegment = (int)MapHandler.CurrentSegmentNumber;
                string key = ResolveSegmentNameByIndex(currentSegment);
                if (string.IsNullOrEmpty(key)) return;

                _pendingValid = true;
                _pendingKey = key;
                _pendingBoundary = currentSegment;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 篝火分段 Prefix 异常: " + ex.Message);
            }
        }

        public static void Postfix(Campfire __instance)
        {
            try
            {
                if (!_pendingValid) return;
                string key = _pendingKey;
                int boundary = _pendingBoundary;
                bool isEnd = _pendingIsMiniRunEnd;
                _pendingValid = false;
                if (string.IsNullOrEmpty(key)) return;

                MatchCoordinator coordinator = MatchCoordinator.Instance;
                if (coordinator == null) return;
                MatchSession session = coordinator.CurrentSession;
                if (session == null) return;

                RaceTimer timer = RaceTimer.Instance;
                if (timer == null || timer.State != TimerState.Running) return;
                long ms = timer.CurrentElapsedMs;
                if (ms <= 0) return;

                string steamId = coordinator.Gateway.MySteamId;
                if (string.IsNullOrEmpty(steamId)) return;

                session.Splits.ObserveCampfire(steamId, key, boundary, ms);
                PcsrLog.LogInfo("[PeakCSR] 篝火赛段记录: " + key + " @ " + ms + "ms"
                    + (isEnd ? " (本局终点)" : ""));

                // 终点篝火: 顺手把完赛用时定下来, 后续胜负仲裁用它
                if (isEnd && session.SelfPeakMs <= 0)
                {
                    session.SelfPeakMs = ms;
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 篝火分段 Postfix 异常: " + ex.Message);
            }
        }

        // Campfire.IsMiniRunEndingCampfire 是 private, 用反射读; 失败按"不是终点"处理
        private static bool IsMiniRunEnding(Campfire campfire)
        {
            try
            {
                System.Reflection.MethodInfo m = typeof(Campfire).GetMethod("IsMiniRunEndingCampfire",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (m == null) return false;
                object r = m.Invoke(campfire, null);
                return r is bool && (bool)r;
            }
            catch
            {
                return false;
            }
        }

        private static string ResolveSegmentNameByIndex(int segmentIndex)
        {
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return "";
                if (segmentIndex < 0 || segmentIndex >= handler.progressPoints.Length) return "";
                return BiomeSegmenter.NormalizeTitle(handler.progressPoints[segmentIndex].title);
            }
            catch
            {
                return "";
            }
        }

        private static string ResolveRouteEndKey()
        {
            try
            {
                MatchCoordinator coordinator = MatchCoordinator.Instance;
                MatchSession session = coordinator != null ? coordinator.CurrentSession : null;
                int route = session != null ? session.Route : 0;

                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return "";

                IRankedPeakGateway gw = coordinator != null ? coordinator.Gateway : null;
                int start;
                int end;
                if (gw != null && gw.TryResolveRouteSpanFor(route, out start, out end))
                {
                    if (end >= 0 && end < handler.progressPoints.Length)
                    {
                        return BiomeSegmenter.NormalizeTitle(handler.progressPoints[end].title);
                    }
                }
                return "";
            }
            catch
            {
                return "";
            }
        }

        private static int ResolveRouteEndBoundary()
        {
            try
            {
                MatchCoordinator coordinator = MatchCoordinator.Instance;
                MatchSession session = coordinator != null ? coordinator.CurrentSession : null;
                int route = session != null ? session.Route : 0;
                IRankedPeakGateway gw = coordinator != null ? coordinator.Gateway : null;
                int start;
                int end;
                if (gw != null && gw.TryResolveRouteSpanFor(route, out start, out end)) return end;
            }
            catch
            {
            }
            return 0;
        }
    }
}
