using System;
using System.Collections.Generic;

namespace PeakCSR
{
    // 一个群系赛段 (本局路线上的一段)
    //
    // Key 取自 progressPoints[i].title, 这是唯一可靠的段名来源。
    // 不能用 MapCatalog.SegmentBiomeName: 它是拿 biomeId 字符串下标做字符映射,
    // 而 biomeId 只有 4 个字符 (例如 SRAV), 进度点却有 6 个, 后半段的段名永远取不到,
    // 而且 'K' (THE KILN) 在它的 switch 里根本不存在。
    public struct BiomeSegment
    {
        // 稳定段名 (SHORE / TROPICS / ROOTS / ALPINE / MESA / CALDERA / THE KILN / GLOOM / PEAK)
        public string Key;
        // 该段在绝对进度点数组里的首个 / 末个索引
        public int FirstPointIdx;
        public int LastPointIdx;
        // 该段的起点 / 终点世界坐标 (终点优先取篝火坐标, 取不到才回退到下一个进度点)
        public bool HasStartPos;
        public float StartZ;
        public bool HasEndPos;
        public float EndZ;
        // 终点是否来自真实篝火 (false 表示回退到了下一进度点, 精度较差)
        public bool EndFromCampfire;
        // 是否为本局路线的终点段
        public bool IsFinal;
        // 该段在路线内的序号 (0 起)
        public int Ordinal;
    }

    // 把"进度点 + 篝火"投影成"群系赛段"
    //
    // 这是赛段统计的唯一权威口径, 本地与对手共用:
    //   - 段名一律取 progressPoints[i].title
    //   - 段终点优先取该段出口篝火的实际坐标, 这是登顶能到 100% 的关键
    //   - 单群系迷你跑整局只有 1 段: 起点由 JumpToSegment 决定, 终点就是迷你跑终点篝火
    public static class BiomeSegmenter
    {
        // 进度点输入 (由 Patch 层从游戏本体类型投影过来, Core 不直接引用游戏类型)
        public struct PointInput
        {
            public string Title;
            public bool HasPosition;
            public float Z;
            public bool IsNadir;
        }

        // 构建本局路线的赛段列表
        //
        // pts:            绝对进度点数组 (已按本局地图顺序排好, 含尾部 NADIR 等辅助点)
        // routeStartIdx:  本局起点进度点索引 (全山传 0)
        // routeEndIdx:    本局终点进度点索引 (全山传 -1 表示到最后一个有效点)
        // campfireZAt:    给定"段终点进度点索引 -> 篝火 Z", 取不到返回 false
        // campfireEndIdx: 迷你跑终点篝火对应的"段终点进度点索引"(-1 表示全山)
        public static List<BiomeSegment> Build(
            PointInput[] pts,
            int routeStartIdx,
            int routeEndIdx,
            Func<int, float?> campfireZAt)
        {
            List<BiomeSegment> result = new List<BiomeSegment>();
            if (pts == null || pts.Length < 2) return result;

            // 尾部 NADIR 等辅助点不参与段划分
            int last = pts.Length - 1;
            while (last > 0 && pts[last].IsNadir) last--;
            if (last < 1) return result;

            int startIdx = routeStartIdx < 0 ? 0 : routeStartIdx;
            if (startIdx > last) startIdx = last;
            int endIdx = routeEndIdx < 0 ? last : routeEndIdx;
            if (endIdx > last) endIdx = last;
            if (endIdx <= startIdx) endIdx = last;

            int ordinal = 0;
            for (int i = startIdx; i <= endIdx; i++)
            {
                string title = NormalizeTitle(pts[i].Title);
                // 段边界 = 段名真值点。这里每个点本身就是一个群系段起点,
                // 段数量因此与进度点数量一致 (单群系跳段后自然只剩 1 段)。
                int segLastIdx = i;
                // 相邻同名点合并: 同一群系可能出现多个进度点 (例如 Alpine 段内多个 Checkpoint),
                // 它们属于同一个赛段, 不能拆成多段。
                while (segLastIdx + 1 <= endIdx
                       && string.Equals(NormalizeTitle(pts[segLastIdx + 1].Title), title, StringComparison.Ordinal))
                {
                    segLastIdx++;
                }

                BiomeSegment seg = new BiomeSegment();
                seg.Key = string.IsNullOrEmpty(title) ? ("SEG" + ordinal) : title;
                seg.FirstPointIdx = i;
                seg.LastPointIdx = segLastIdx;
                seg.Ordinal = ordinal;
                seg.IsFinal = (segLastIdx >= endIdx);

                if (pts[i].HasPosition)
                {
                    seg.HasStartPos = true;
                    seg.StartZ = pts[i].Z;
                }

                // 终点坐标: 优先篝火。段终点进度点索引 = 下一段起点 (与旧实现一致),
                // 但坐标换成该处篝火的真实位置, 这样点亮篝火时进度正好收敛到 1.0。
                int boundaryIdx = segLastIdx + 1;
                float? fireZ = null;
                if (campfireZAt != null)
                {
                    try { fireZ = campfireZAt(boundaryIdx); }
                    catch { fireZ = null; }
                }
                if (fireZ.HasValue)
                {
                    seg.HasEndPos = true;
                    seg.EndZ = fireZ.Value;
                    seg.EndFromCampfire = true;
                }
                else if (boundaryIdx < pts.Length && pts[boundaryIdx].HasPosition)
                {
                    seg.HasEndPos = true;
                    seg.EndZ = pts[boundaryIdx].Z;
                    seg.EndFromCampfire = false;
                }
                else if (pts[segLastIdx].HasPosition)
                {
                    seg.HasEndPos = true;
                    seg.EndZ = pts[segLastIdx].Z;
                    seg.EndFromCampfire = false;
                }

                result.Add(seg);
                ordinal++;
                i = segLastIdx;
            }

            return result;
        }

        // 段名清洗: 进度点 title 是大写英文 (SHORE / THE KILN ...),
        // 去掉可能存在的本地化前缀, 统一成稳定的 key
        public static string NormalizeTitle(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = raw.Trim().ToUpperInvariant();
            if (s == "NADIR") return "";
            return s;
        }

        // 按进度值 0..1 找到所属段
        public static int IndexAtProgress(List<BiomeSegment> segs, float progress01)
        {
            if (segs == null || segs.Count == 0) return -1;
            if (progress01 <= 0f) return 0;
            if (progress01 >= 1f) return segs.Count - 1;

            float total = 0f;
            for (int i = 0; i < segs.Count; i++) total += Span(segs[i]);
            if (total <= 0.01f) return 0;

            float target = progress01 * total;
            float acc = 0f;
            for (int i = 0; i < segs.Count; i++)
            {
                acc += Span(segs[i]);
                if (target <= acc) return i;
            }
            return segs.Count - 1;
        }

        // 单个段的跨度 (用于把段权重折算成进度)
        public static float Span(BiomeSegment seg)
        {
            if (!seg.HasStartPos || !seg.HasEndPos) return 1f;
            float d = seg.EndZ - seg.StartZ;
            // 折返段 (终点 z 不比起点大) 无法用 z 跨度表达, 给一个最小权重避免除零
            return d > 0.5f ? d : 0.5f;
        }
    }
}
