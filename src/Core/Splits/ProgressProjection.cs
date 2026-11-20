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
        public static float Compute(ProgressPointInfo[] pts, float centerZ, int routeStartIndex, int routeEndIndex)
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

            // 关键: 索引只由"真实位置"决定, 不使用 maxProgressPointReached。
            // 与游戏 CheckReached 同判据 (Center.z > point.z), 但这里只取插值区间。
            int reached = -1;
            for (int i = 0; i <= last; i++)
            {
                if (!pts[i].HasPosition) continue;
                if (centerZ > pts[i].Z) reached = i;
            }

            // 还没越过第一个点: 位于起点之前的引道, 视为 0
            if (reached < 0) return 0f;
            // 已越过最后一个点: 真正到顶
            if (reached >= last) return 1f;

            // 段内插值: 用 Z 比例, 与 RANKED 的 num6 同义但索引取自真实位置
            float span = pts[reached + 1].Z - pts[reached].Z;
            float frac;
            if (!pts[reached + 1].HasPosition || span <= 0.5f)
            {
                // 两点 Z 重合/倒序 (PEAK 之后的折返): 无法插值, 保守取段末
                frac = 1f;
            }
            else
            {
                frac = (centerZ - pts[reached].Z) / span;
                if (frac < 0f) frac = 0f;
                if (frac > 1f) frac = 1f;
            }

            float absolute = reached + frac;              // 0..last 的连续绝对位置
            float total = spanEnd - spanStart;
            if (total <= 0f) total = last;
            float result = (absolute - spanStart) / total;
            if (result < 0f) result = 0f;
            if (result > 1f) result = 1f;
            return result;
        }
    }
}
