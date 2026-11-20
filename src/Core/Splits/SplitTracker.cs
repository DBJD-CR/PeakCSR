using System;
using System.Collections.Generic;

namespace PeakCSR
{
    // 一条赛段记录
    public struct SplitEntry
    {
        // 段名真值
        public string Key;
        // 到达该段终点时的累计用时
        public long ElapsedMs;
        // true 表示这段时间是"跳帧回填"出来的, UI 上用 ~ 标记
        public bool Estimated;
        // 该段终点在本局山体里的绝对进度点索引
        public int AbsoluteIndex;
    }

    // PCSR 自建分段采集器
    //
    // 数据来源有两类, 口径统一到"段名 + 累计用时":
    //   1) 本地权威: Campfire.Light_Rpc 事件 (SplitCapturePatch 转发), 零误差
    //   2) 对手投影: 进度点索引 (Flags >> 4) + LiveTimeMs, 每秒一帧, 精度 ≤1 帧
    //
    // 为什么不用 RANKED 的 LiveSplitTrack:
    //   它的 Observe(steamId, segmentsLit, timeMs) 是 while (count < segmentsLit) list.Add(timeMs),
    //   缺几段就用同一个时间补齐, 产生一串相同的假时间; 而且 segmentsLit 是"整座山的绝对索引",
    //   单群系从第 3 个群系开局时一上来就是 10+, 真正属于本局的那一段被埋在前面这些填充里

    public sealed class SplitTracker
    {
        // steamId -> 有序段记录 (按绝对进度点索引排序, 便于裁剪)
        private readonly Dictionary<string, SortedDictionary<int, SplitEntry>> _splits =
            new Dictionary<string, SortedDictionary<int, SplitEntry>>();

        // 每个玩家已经处理到的绝对进度点索引 (用于判"是否真的前进了")
        private readonly Dictionary<string, int> _lastIndex =
            new Dictionary<string, int>();

        // 本局路线的绝对进度点区间
        private int _routeStartIndex = -1;
        private int _routeEndIndex = -1;

        public void Clear()
        {
            _splits.Clear();
            _lastIndex.Clear();
            _routeStartIndex = -1;
            _routeEndIndex = -1;
        }

        // 开局时登记本局路线的绝对区间
        public void SetRoute(int routeStartIndex, int routeEndIndex)
        {
            _routeStartIndex = routeStartIndex;
            _routeEndIndex = routeEndIndex;
            // 换路线时旧游标全部作废, 否则新局第一段会因为"索引没涨"而漏记
            _lastIndex.Clear();
        }

        public int RouteStartIndex { get { return _routeStartIndex; } }
        public int RouteEndIndex { get { return _routeEndIndex; } }

        // ===== 本地权威采集 (篝火事件驱动) =====

        // 点亮篝火时由 CampfireSplitPatch 调用
        // boundaryIndex: 该篝火对应的"段终点进度点索引", 用于与对手侧数据对齐
        public void ObserveCampfire(string steamId, string segmentKey, int boundaryIndex, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (string.IsNullOrEmpty(segmentKey)) return;
            if (elapsedMs <= 0) return;
            if (boundaryIndex < 0) boundaryIndex = 0;
            Write(steamId, boundaryIndex, segmentKey, elapsedMs, false);
        }

        // 本地进度点首次到达 (分段的一个补充来源: 有些段没有篝火, 只有进度点)
        public void ObserveLocalSegment(string steamId, int absoluteIndex, string segmentKey, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (absoluteIndex < 0) return;
            if (elapsedMs <= 0) return;
            Write(steamId, absoluteIndex, segmentKey, elapsedMs, false);
        }

        // ===== 对手投影采集 (进度点索引 + 帧时间) =====

        // progress01 用来解 Flags >> 4 == 0 的歧义:
        // maxProgressPointReached 初值是 -1, 被 clamp 到 0 后,
        // "一个点都没到"和"刚到第一个点"在协议层完全一样,
        // 必须结合进度值才能区分
        public void ObservePeerFrame(
            string steamId, int pointIndex, float progress01, long liveTimeMs,
            Func<int, string> keyAt, Func<int, bool> inRoute)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (pointIndex < 0) return;
            if (liveTimeMs <= 0) return;
            // pointIndex == 0 且进度为 0: 玩家真的还没动, 不能记成"已通过第一段"
            if (pointIndex == 0 && progress01 <= 0.001f) return;
            if (keyAt == null) return;

            int lastIdx;
            _lastIndex.TryGetValue(steamId, out lastIdx);

            if (pointIndex <= lastIdx) return;
            _lastIndex[steamId] = pointIndex;

            // 从上一个已处理索引 +1 开始补到当前索引。
            // 跳帧时中间的段用当前帧时间回填并标 Estimated
            int from = lastIdx + 1;
            if (from < 0) from = 0;
            for (int idx = from; idx <= pointIndex; idx++)
            {
                if (inRoute != null && !inRoute(idx)) continue;
                string key = keyAt(idx);
                if (string.IsNullOrEmpty(key)) continue;
                bool estimated = (idx != pointIndex);
                Write(steamId, idx, key, liveTimeMs, estimated);
            }
        }

        public void ObserveSegmentsLit(string steamId, int segmentsLit, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (segmentsLit < 0) return;
            if (elapsedMs <= 0) return;
            int absoluteIndex = segmentsLit;
            int lastIdx;
            _lastIndex.TryGetValue(steamId, out lastIdx);
            if (absoluteIndex <= lastIdx) return;
            _lastIndex[steamId] = absoluteIndex;
        }

        private void Write(string steamId, int index, string key, long elapsedMs, bool estimated)
        {
            SortedDictionary<int, SplitEntry> map;
            if (!_splits.TryGetValue(steamId, out map))
            {
                map = new SortedDictionary<int, SplitEntry>();
                _splits[steamId] = map;
            }

            SplitEntry existing;
            if (map.TryGetValue(index, out existing))
            {
                // 已有一条精确记录时不覆盖
                if (!existing.Estimated) return;
                if (estimated) return;
                existing.ElapsedMs = elapsedMs;
                existing.Estimated = false;
                if (!string.IsNullOrEmpty(key)) existing.Key = key;
                existing.AbsoluteIndex = index;
                map[index] = existing;
                return;
            }

            SplitEntry entry = new SplitEntry();
            entry.Key = key;
            entry.ElapsedMs = elapsedMs;
            entry.Estimated = estimated;
            entry.AbsoluteIndex = index;
            map[index] = entry;
        }

        public int CountFor(string steamId)
        {
            SortedDictionary<int, SplitEntry> map;
            if (string.IsNullOrEmpty(steamId) || !_splits.TryGetValue(steamId, out map)) return 0;
            return map.Count;
        }

        public bool HasAnyRecord(string steamId)
        {
            SortedDictionary<int, SplitEntry> map;
            return !string.IsNullOrEmpty(steamId) && _splits.TryGetValue(steamId, out map) && map.Count > 0;
        }

        // 导出本局路线内的段记录 (按索引升序)
        public List<SplitEntry> Export(string steamId)
        {
            List<SplitEntry> result = new List<SplitEntry>();
            SortedDictionary<int, SplitEntry> map;
            if (string.IsNullOrEmpty(steamId) || !_splits.TryGetValue(steamId, out map)) return result;

            int start = _routeStartIndex < 0 ? 0 : _routeStartIndex;
            int end = _routeEndIndex;

            foreach (KeyValuePair<int, SplitEntry> kv in map)
            {
                if (kv.Key < start) continue;
                if (end >= 0 && kv.Key > end) continue;
                result.Add(kv.Value);
            }
            return result;
        }

        // 取某段 (按路线内相对序号) 的记录
        public bool TryGetRelative(string steamId, int relativeIndex, out SplitEntry entry)
        {
            entry = new SplitEntry();
            List<SplitEntry> list = Export(steamId);
            if (relativeIndex < 0 || relativeIndex >= list.Count) return false;
            entry = list[relativeIndex];
            return true;
        }
    }
}
