using System;
using HarmonyLib;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // 修正 RANKED 原生 RaceProgress 的进度跳变与结算失真
    //
    // 治本点 (不是平滑遮盖):
    //   RANKED 的 RawProgress() 用 maxProgressPointReached 这个离散索引定区间:
    //       return (maxProgressPointReached + 段内插值) / last
    //   而 maxProgressPointReached 会被 MapHandler 切段协程里的
    //       SetSegmentComplete(startSegment + 1)
    //   提前推高。索引一涨, 段内插值就按"新段"重算, 于是进度凭空 +1/last
    //   (全山约 25 点 → 正好 4%)。单群系更糟: _singleSegment 直接返回段内插值,
    //   开局站在段末就是 100%, 越过该点后崩回 0%。
    //
    //   本补丁改用 ProgressProjection: 索引完全由玩家真实 Center.z 决定,
    //   切段/点灯/传送都不会改变"我已经走到哪", 进度因此天然连续。
    //   raw 只在几何计算不可用 (进度点缺失/坐标为 0) 时作为兜底,
    //   绝不会与几何值取 max (旧实现开赛固定 +6% 就是取 max + cap 造成的)。
    //
    //   另外两件事保持不变:
    //     1) 单调不减: 任何偶发回退都不外泄 (对手实时帧同步也吃这个值)
    //     2) 只有确认登顶才置 1.0: 修掉"结算显示登顶前一帧 81.4%"
    [HarmonyPatch(typeof(RaceProgress), "Current")]
    public static class ProgressMonotonicPatch
    {
        public static void Postfix(ref float __result)
        {
            try
            {
                // 静态闸门: 只在 PCSR 对局内生效, 不污染普通 RANKED 与单人速通
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;
                if (__result < 0f) return;

                bool finished = HasSelfClimbedToPeak();
                float geometric = ComputeGeometricProgress();
                float value = geometric >= 0f ? geometric : __result;

                __result = ProgressMonotonicState.Correct(value, finished);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 进度修正补丁异常: " + ex.Message);
            }
        }

        // 唯一允许把进度强制成 1.0 的条件: 服务端确认"我自己"已经完赛。
        // 不能用 race.Status == finished / Session.State == Finished:
        // 掉线作废、对手先登顶、AutoEndMs 超时收尾都会满足那两个条件,
        // 那些场景下我的进度点可能一个都没点, 却会被显示成 100%。
        private static bool HasSelfClimbedToPeak()
        {
            try
            {
                RaceService svc = RaceService.Instance;
                if (svc == null) return false;
                RaceParticipant self = svc.Self;
                if (self == null) return false;
                return self.State == "finished";
            }
            catch
            {
                return false;
            }
        }

        // 按真实 Z 位置算连续进度; 任何前提不满足都返回 -1 让调用方回退到 raw
        private static float ComputeGeometricProgress()
        {
            try
            {
                Character local = Character.localCharacter;
                if (local == null) return -1f;

                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return -1f;
                ProgressPointEx[] pts = ProgressPointEx.From(handler.progressPoints);
                if (pts.Length < 2) return -1f;

                MatchCoordinator coordinator = MatchCoordinator.Instance;
                MatchSession session = coordinator != null ? coordinator.CurrentSession : null;
                int route = session != null ? session.Route : 0;

                // 单群系: 只在该路线的进度点区间内归一, 否则分母取整座山会严重偏小
                // 全山: 从 0 到最后一个点
                int spanStart = 0;
                int spanEnd = -1;
                if (coordinator != null)
                {
                    IRankedPeakGateway gw = coordinator.Gateway;
                    int resolvedStart;
                    int resolvedEnd;
                    if (gw != null && gw.TryResolveRouteSpanFor(route, out resolvedStart, out resolvedEnd))
                    {
                        spanStart = resolvedStart;
                        spanEnd = resolvedEnd;
                    }
                }

                ProgressPointInfo[] info = new ProgressPointInfo[pts.Length];
                for (int i = 0; i < pts.Length; i++)
                {
                    info[i].HasPosition = pts[i].HasPosition;
                    info[i].Z = pts[i].Z;
                    info[i].IsNadir = pts[i].IsNadir;
                    info[i].Biome = pts[i].BiomeOrdinal;
                    info[i].Title = pts[i].Title;
                    info[i].BiomeRaw = pts[i].BiomeRaw;
                }

                // 段终点优先取出口篝火
                IRankedPeakGateway gateway = coordinator != null ? coordinator.Gateway : null;
                Func<int, float?> campfireLookup = null;
                if (gateway != null)
                {
                    campfireLookup = delegate(int boundaryIdx)
                    {
                        float z;
                        if (gateway.TryGetCampfireZ(boundaryIdx, out z)) return (float?)z;
                        return null;
                    };
                }

                return ProgressProjection.Compute(info, local.Center.z, spanStart, spanEnd, campfireLookup);
            }
            catch
            {
                return -1f;
            }
        }
    }

    // MountainProgressHandler.ProgressPoint 是公有嵌套类型, 这里做一个轻量投影避免反射开销
    internal struct ProgressPointEx
    {
        public bool HasPosition;
        public float Z;
        public bool IsNadir;
        // 群系在本次山体里的出现序号 (0 起, 相邻同名群系视为同一群系)
        public int BiomeOrdinal;
        // 段名真值
        public string Title;
        // 群系枚举原始整数值, 用于反查该点属于本次地图的第几个 Segment
        public int BiomeRaw;

        public static ProgressPointEx[] From(Array points)
        {
            if (points == null) return new ProgressPointEx[0];
            int len = points.Length;
            ProgressPointEx[] arr = new ProgressPointEx[len];

            // 用群系枚举值的"出现顺序"编号: RANKED 已按地图实际群系顺序重排 progressPoints,
            // 所以相同 biome 必然连续出现, 序号可以直接按变化累加。
            int ordinal = -1;
            object lastBiome = null;
            for (int i = 0; i < len; i++)
            {
                object p = points.GetValue(i);
                ProgressPointEx item = new ProgressPointEx();
                if (p != null)
                {
                    MountainProgressHandler.ProgressPoint pt = p as MountainProgressHandler.ProgressPoint;
                    if (pt != null)
                    {
                        item.Title = pt.title ?? "";
                        item.BiomeRaw = (int)pt.biome;
                        item.IsNadir = string.Equals(pt.title, "NADIR", StringComparison.OrdinalIgnoreCase);
                        if (!Equals(pt.biome, lastBiome))
                        {
                            ordinal++;
                            lastBiome = pt.biome;
                        }
                        item.BiomeOrdinal = ordinal < 0 ? 0 : ordinal;
                        if (pt.transform != null)
                        {
                            item.HasPosition = true;
                            item.Z = pt.transform.position.z;
                        }
                    }
                    else
                    {
                        item.BiomeOrdinal = ordinal < 0 ? 0 : ordinal;
                    }
                }
                else
                {
                    item.BiomeOrdinal = ordinal < 0 ? 0 : ordinal;
                }
                arr[i] = item;
            }
            return arr;
        }
    }

    // RANKED 在开赛瞬间 (ReleaseWhenStartReached) 会调用 RaceProgress.Reset(),
    // 而我们的单调缓存在 StartNewSession 时才重置, 两者临界点不一致。
    // 这里把两个重置对齐, 避免"开赛前采样"被当成"开赛后基准"。
    [HarmonyPatch(typeof(RaceProgress), "Reset")]
    public static class ProgressResetSyncPatch
    {
        public static void Prefix()
        {
            try
            {
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;
                ProgressMonotonicState.Reset();
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 进度基准同步异常: " + ex.Message);
            }
        }
    }
}
