using System;

namespace PeakCSR
{
    // 进度点的轻量投影 (避免 Core 层反射游戏类型)
    public struct ProgressPointInfo
    {
        public bool HasPosition;
        public float Z;
        public bool IsNadir;
        // 该点所属群系在本次地图中的序号 (用于按群系统计段数)
        public int Biome;
        // 段名真值
        public string Title;
        // 群系枚举原始值, 用于反查该点属于本次地图的第几个 Segment
        public int BiomeRaw;
    }

    // 进度几何计算 (纯函数, 不持有任何状态)
    //
    // 为什么需要自己算:
    //   RANKED 的 RawProgress 用 maxProgressPointReached 这个"离散索引"来定区间:
    //       return (num4 + num6) / last
    //   而 maxProgressPointReached 会被 MapHandler 切段协程里的
    //       SetSegmentComplete(startSegment + 1)
    //   提前推高 —— 此时玩家其实还没走到那个点的 Z 位置, 于是:
    //       num4 从 k 变 k+1, 同时 num6 按"新段"从 0 重新算
    //   结果进度凭空 +1/last。全山约 25 个点, 正好 4%。
    //   单群系更严重: _singleSegment 只返回 num6 (段内锯齿),
    //   开局站在段末 → 100%, 越过该点后立刻崩到 0%。
    //
    // 本类改为"以玩家真实 Z 在相邻进度点之间做连续插值", 索引只由位置决定,
    // 因此切段/传送/点灯都不会改变已到达的位置, 进度天然连续。
    public static class ProgressProjection
    {
        // 计算连续进度。
        // pts:              进度点数组 (按山体顺序, 已剔除尾部辅助 NADIR)
        // centerZ:          本机角色 Center.z
        // routeStartIndex:  本局路线的起点进度点索引 (全山传 0)
        // routeEndIndex:    本局路线的终点进度点索引 (全山传 last)
        // 返回 0..1; 无可用点时返回 -1
        //
        // 注意: 这个重载不接受篝火坐标, 终点会退化为"下一个进度点"
        // 需要精确到 100% 收敛的调用方请用带 CampfireZLookup 的重载
        public static float Compute(ProgressPointInfo[] pts, float centerZ, int routeStartIndex, int routeEndIndex)
        {
            return Compute(pts, centerZ, routeStartIndex, routeEndIndex, null);
        }

        // campfireZAt: (段终点进度点索引) -> 该处出口篝火 Z, 取不到返回 null
        public static float Compute(
            ProgressPointInfo[] pts, float centerZ,
            int routeStartIndex, int routeEndIndex,
            Func<int, float?> campfireZAt)
        {
            if (pts == null || pts.Length < 2) return -1f;

            // 尾部 NADIR 之外的辅助点不参与分母计算 (与 RANKED 的 last 语义对齐)
            int last = pts.Length - 1;
            while (last > 0 && pts[last].IsNadir) last--;
            if (last < 1) return -1f;

            int spanStart = routeStartIndex < 0 ? 0 : routeStartIndex;
            if (spanStart > last) spanStart = last;
            int spanEnd = routeEndIndex < 0 ? last : routeEndIndex;
            if (spanEnd > last) spanEnd = last;
            if (spanEnd <= spanStart) spanEnd = last;

            // 段终点坐标: 优先篝火, 退化时才用进度点
            float endZ = ResolveEndZ(pts, spanEnd, campfireZAt, last);

            // 关键: 索引只由"真实位置"决定, 不使用 maxProgressPointReached。
            // 与游戏 CheckReached 同判据 (Center.z > point.z), 但这里只取插值区间。
            int reached = -1;
            int positionedCount = 0;
            for (int i = 0; i <= last; i++)
            {
                if (!pts[i].HasPosition) continue;
                positionedCount++;
                if (centerZ > pts[i].Z) reached = i;
            }

            // 几何不可用, 交给调用方回退到 raw
            if (positionedCount == 0) return -1f;

            // 还没越过第一个点: 位于起点之前的引道, 视为 0
            if (reached < 0) return 0f;

            float startZ = pts[spanStart].HasPosition ? pts[spanStart].Z : pts[0].Z;
            // 已经越过整个路线终点段: 真正到顶
            if (reached >= spanEnd && centerZ >= endZ) return 1f;

            // 段内插值: 每段用自己的 [点Z, 段终点Z] 区间
            float absolute = 0f;
            float totalSpan = 0f;
            float acc = 0f;
            for (int i = spanStart; i <= spanEnd; i++)
            {
                float segStartZ = PickZ(pts, i, startZ);
                float segEndZ = (i == spanEnd) ? endZ : PickZ(pts, i + 1, segStartZ);
                float segSpan = segEndZ - segStartZ;
                if (segSpan <= 0.5f) segSpan = 0.5f;
                totalSpan += segSpan;

                if (centerZ >= segStartZ)
                {
                    // 玩家已进入 (或越过) 这一段: 累加整段, 最后一小段做插值
                    float localFrac = (centerZ - segStartZ) / segSpan;
                    if (localFrac > 1f) localFrac = 1f;
                    if (i == spanEnd)
                    {
                        acc += localFrac * segSpan;
                    }
                    else
                    {
                        acc += segSpan;
                    }
                }
                // centerZ < segStartZ 时后续段一律未到达, 不再累加
            }

            absolute = acc;
            if (totalSpan <= 0.01f) return 0f;
            float result = absolute / totalSpan;
            if (result < 0f) result = 0f;
            if (result > 1f) result = 1f;
            return result;
        }

        private static float PickZ(ProgressPointInfo[] pts, int index, float fallback)
        {
            if (index < 0 || index >= pts.Length) return fallback;
            return pts[index].HasPosition ? pts[index].Z : fallback;
        }

        // 段终点 Z: 篝火优先, 取不到才回退到进度点
        private static float ResolveEndZ(ProgressPointInfo[] pts, int spanEnd, Func<int, float?> campfireZAt, int last)
        {
            if (campfireZAt != null)
            {
                try
                {
                    float? fireZ = campfireZAt(spanEnd);
                    if (fireZ.HasValue) return fireZ.Value;
                }
                catch
                {
                }
            }
            if (spanEnd < pts.Length && pts[spanEnd].HasPosition) return pts[spanEnd].Z;
            return pts[last].HasPosition ? pts[last].Z : 0f;
        }
    }
}
