using System;
using System.Collections.Generic;
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
                // 分段采集闸门: 练习局也要记录 (练习期间一切照常计算)
                if (!MatchCoordinator.IsCurrentCsrRun()) return;

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

                // maxProgressPointReached = N 表示"已进入第 N 段", 完成的是第 N-1 段
                // 进度点最多写到"终段之前那一段", 终段只由完赛信号写
                int lastCompleted;
                if (!session.Splits.TryClampProgressBoundary(reached - 1, out lastCompleted)) return;

                // 跳帧时中间的段用同一帧时间回填
                int from = _lastRecorded;
                if (from < 0) from = 0;
                for (int idx = from; idx <= lastCompleted; idx++)
                {
                    string key = ResolveSegmentKey(coordinator, idx);
                    if (string.IsNullOrEmpty(key)) continue;
                    bool estimated = (idx != lastCompleted);
                    if (estimated)
                    {
                        session.Splits.ObserveLocalSegmentEstimated(steamId, idx, key, ms);
                    }
                    else
                    {
                        session.Splits.ObserveLocalSegment(steamId, idx, key, ms);
                    }
                }
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
                return ResolveCaptureKey(pts[absoluteIndex].Title);
            }
            catch
            {
                return "";
            }
        }

        // 采集专用段名: 与 NormalizeTitle 同口径, 但保留 NADIR
        internal static string ResolveCaptureKey(string raw)
        {
            string normalized = BiomeSegmenter.NormalizeTitle(raw);
            if (!string.IsNullOrEmpty(normalized)) return normalized;
            if (string.IsNullOrEmpty(raw)) return "";
            // NormalizeTitle 只对 NADIR 返回空串, 这里把它还原
            return raw.Trim().ToUpperInvariant() == "NADIR" ? "NADIR" : "";
        }

        // 读取当前山体的进度点总数 (越小越安全: 读不到就返回 0 表示跳过越界校验)
        internal static int ProgressPointCount(MatchCoordinator coordinator)
        {
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return 0;
                return handler.progressPoints.Length;
            }
            catch
            {
                return 0;
            }
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
                // 新地图的进度点/篝火全换了, 锚点缓存必须一起作废
                IRankedPeakGateway gw = coordinator != null ? coordinator.Gateway : null;
                if (gw != null) gw.InvalidateAnchorCache();
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
                // 篝火分段同样对练习局开放
                if (!MatchCoordinator.IsCurrentCsrRun()) return;
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

                // 推进到被并入终段的顶峰, 终段由真正的完赛信号(EndGame)写, 这里直接跳过。
                // 注意: 这条篝火完成的是"最后一个真实段"(熔炉/城塞), 它的时间
                // 必须等爬到顶点点信号棒 EndGame 才权威, 提前结算会把终段用时算短。
                if (__instance.advanceToSegment == Segment.Peak)
                {
                    PcsrLog.LogInfo("[PeakCSR] 已推进至顶峰段, 终段交由完赛信号写入");
                    return;
                }

                // 完成段号 = advanceToSegment - 1。
                // 证据: Campfire.Light_Rpc 调 MapHandler.GoToSegment(advanceToSegment),
                // 而 GoToSegment 先 currentSegment++ 再 SetSegmentComplete(startSegment+1),
                // 所以"刚完成的段"就是 advanceToSegment 的前一个。这条口径直接读
                // 篝火自带的公开字段, 不依赖 MapHandler 的瞬时状态, 也不会被
                // GetCurrentSegment() 在虚空段返回 Void 污染。
                int completed;
                int adv = (int)__instance.advanceToSegment;
                if (adv > 0 && adv <= (int)Segment.Peak)
                {
                    completed = adv - 1;
                }
                else
                {
                    // 兜底: 非标准推进, 退回 Prefix 时刻的当前段号
                    // (Light_Rpc 里还没 currentSegment++, 此刻的段号就是已完成段)
                    completed = (int)MapHandler.CurrentSegmentNumber;
                    if (completed < 0 || completed == (int)Segment.Void)
                    {
                        PcsrLog.LogInfo("[PeakCSR] 篝火位于虚空/非法段, 不计入赛段");
                        return;
                    }
                }

                string key = ResolveSegmentNameByIndex(completed);
                if (string.IsNullOrEmpty(key)) return;

                _pendingValid = true;
                _pendingKey = key;
                _pendingBoundary = completed;
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

                MatchCoordinator coordinator = MatchCoordinator.Instance;
                if (coordinator == null) return;
                MatchSession session = coordinator.CurrentSession;
                if (session == null) return;

                // 段号口径: 前缀捕获的 currentSegment 就是"刚完成的段"。
                // 证据链: MapHandler.GoToSegment 内部 SetSegmentComplete(startSegment + 1)
                // 直接拿 (旧 currentSegment + 1) 去索引 progressPoints, 说明
                // progressPoints 下标与 currentSegment 是同一套坐标。因此
                // progressPoints[currentSegment].title 天然就是该段名字
                // (RankedPeak 侧面板的 RunLifecyclePatches.CampfireLightPrefix 同源)。
                //
                // 千万不能用 maxProgressPointReached 反推: 它是几何驱动
                // (CheckReached 判 Center.z > 进度点.z), 站到本段出口篝火时
                // 几何上最多只推进到本段入口点, 减 1 之后整条系统性少一段,
                // 表现为段名整体上移一位、第一条之后的累计值全错、PB 全丢。
                //
                // 这里只做一件事: 把段号对齐到段模型的 LastPointIdx, 让战报
                // 能按 AbsoluteIndex 命中 (1:1 时等价于原值)。
                int normBoundary;
                string normKey;
                if (!isEnd && TryNormalizeCampfireBoundary(session, boundary, key, out normBoundary, out normKey))
                {
                    boundary = normBoundary;
                    key = normKey;
                }

                if (string.IsNullOrEmpty(key)) return;

                // 终段只认完赛信号: 若归一后落在终段(含并入的顶峰), 一律不在这里写
                if (!isEnd && IsFinalBoundary(session, boundary))
                {
                    PcsrLog.LogInfo("[PeakCSR] 篝火落在终段, 交由完赛信号写入: key=" + key);
                    return;
                }

                // 越界防线: 这类错位一旦发生就当场留痕, 不把脏 boundary 写入台账
                int pointCount = SplitCapturePatch.ProgressPointCount(coordinator);
                if (pointCount > 0 && (boundary < 0 || boundary >= pointCount))
                {
                    PcsrLog.LogWarning("[PeakCSR] 篝火赛段索引越界已丢弃: key=" + key
                        + " boundary=" + boundary + " 进度点数=" + pointCount);
                    return;
                }

                RaceTimer timer = RaceTimer.Instance;
                if (timer == null) return;
                // 迷你跑终点篝火会先 EndGame() 把计时器置为 Finished, 然后才回到本 Postfix,
                // 只认 Running 会把"登顶那一段"整条丢掉 (单群系因此永远没有本地分段)。
                // Finished 状态下 CurrentElapsedMs 就是 _finalMs, 正是完赛用时, 零误差
                if (timer.State != TimerState.Running && timer.State != TimerState.Finished) return;
                long ms = timer.CurrentElapsedMs;
                if (ms <= 0) return;

                string steamId = coordinator.Gateway.MySteamId;
                if (string.IsNullOrEmpty(steamId)) return;

                if (isEnd)
                {
                    // 迷你跑终点篝火点亮即 EndGame, 时间就是完赛用时, 走完赛级来源
                    session.Splits.ObserveCompletion(steamId, key, boundary, ms);
                    PcsrLog.LogInfo("[PeakCSR] 篝火赛段记录: " + key + " @ " + ms + "ms (本局终点)");
                    // 顺手把完赛用时定下来, 后续胜负仲裁用它
                    if (session.SelfPeakMs <= 0) session.SelfPeakMs = ms;
                }
                else
                {
                    session.Splits.ObserveCampfire(steamId, key, boundary, ms);
                    PcsrLog.LogInfo("[PeakCSR] 篝火赛段记录: " + key + " @ " + ms + "ms");
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

        // 把"篝火段号"对齐到本局段模型的 LastPointIdx
        //
        // 权威前提: progressPoints 下标 == MapHandler.currentSegment (见 Postfix 注释证据链),
        // 而 progressPoints[i].title 就是第 i 段的真名, 与 RANKED 侧面板完全同源。
        // 归一只是为了让战报能按 AbsoluteIndex == segments[i].LastPointIdx 命中:
        //   - 常规图每段一个进度点: segNo 恰好等于 LastPointIdx, 原样通过
        //   - 同一群系多个进度点被合并成一段: segNo 落在段内, 归一到该段末点
        //   - 段号与段模型索引空间整体漂移: 用段名唯一回查
        //   - 全部对不上: 保留原始段号(RANKED 口径), 并留痕便于复查
        private static bool TryNormalizeCampfireBoundary(
            MatchSession session, int segNo, string rawTitle, out int boundary, out string key)
        {
            boundary = segNo;
            key = rawTitle ?? "";
            if (segNo < 0) return false;
            if (session == null) return !string.IsNullOrEmpty(key);

            try
            {
                List<BiomeSegment> segs = session.BuildRouteSegments();
                if (segs == null || segs.Count == 0) return !string.IsNullOrEmpty(key);

                // 1) 段号落在某段的进度点区间内: 对齐到该段末点
                for (int i = 0; i < segs.Count; i++)
                {
                    if (segNo >= segs[i].FirstPointIdx && segNo <= segs[i].LastPointIdx)
                    {
                        boundary = segs[i].LastPointIdx;
                        if (!string.IsNullOrEmpty(segs[i].Key)) key = segs[i].Key;
                        return true;
                    }
                }

                // 2) 段号不在任何区间内: 用段名唯一回查
                string title = BiomeSegmenter.NormalizeTitle(rawTitle);
                if (!string.IsNullOrEmpty(title))
                {
                    int hit = -1;
                    int hits = 0;
                    for (int i = 0; i < segs.Count; i++)
                    {
                        if (!string.Equals(segs[i].Key, title, StringComparison.Ordinal)) continue;
                        hit = i;
                        hits++;
                    }
                    if (hits == 1)
                    {
                        boundary = segs[hit].LastPointIdx;
                        key = segs[hit].Key;
                        return true;
                    }
                }

                // 3) 对不上: 保留原始段号并留痕
                PcsrLog.LogWarning("[PeakCSR] 篝火段号未能对齐段模型: segNo=" + segNo
                    + " title=" + (rawTitle ?? "") + " 段数=" + segs.Count);
                return !string.IsNullOrEmpty(key);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 篝火段号归一异常: " + ex.Message);
                return !string.IsNullOrEmpty(key);
            }
        }

        // 判断某个段终点索引是否属于终段(含并入顶峰的段)
        private static bool IsFinalBoundary(MatchSession session, int boundary)
        {
            try
            {
                if (session == null) return false;
                List<BiomeSegment> segs = session.BuildRouteSegments();
                if (segs == null) return false;
                for (int i = 0; i < segs.Count; i++)
                {
                    if (segs[i].IsFinal && segs[i].LastPointIdx == boundary) return true;
                }
            }
            catch
            {
            }
            return false;
        }

        private static string ResolveSegmentNameByIndex(int segmentIndex)
        {
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return "";
                if (segmentIndex < 0 || segmentIndex >= handler.progressPoints.Length) return "";
                // 走采集专用口径: 天底要保留成 NADIR, 否则天底赛段 PB 采不到
                return SplitCapturePatch.ResolveCaptureKey(handler.progressPoints[segmentIndex].title);
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
                        return SplitCapturePatch.ResolveCaptureKey(handler.progressPoints[end].title);
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
