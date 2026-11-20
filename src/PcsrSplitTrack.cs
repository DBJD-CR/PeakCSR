using System;
using System.Collections.Generic;
using RankedPeak.Services;

namespace PeakCSR
{
    // PCSR 自建分段采集器
    //
    // 为什么不用 RANKED 的 LiveSplitTrack:
    //   LiveSplitTrack.Observe(steamId, segmentsLit, timeMs) 的 segmentsLit 是 Flags >> 4,
    //   也就是"整座山的绝对点灯数"。单群系从第 3 个群系开局时它一上来就是 10+,
    //   而它的实现是 while (value.Count < segmentsLit) value.Add(timeMs) —— 缺几段就用
    //   同一个时间补齐, 于是出现一串相同的假时间 (例如 00:00.122), 真正属于本局的那一段
    //   被埋在前面这些填充里, 结算页按 i 取 SegmentBiomeName 自然对不上。
    //
    // 本类只记录"该绝对段索引第一次真正到达时的时间", 绝不填充,
    // 展示时按本局路线的起点裁剪并重编号, 缺的就老实显示"未记录"。
    public static class PcsrSplitTrack
    {
        // steamId -> (绝对段索引 -> 首次到达时的累计毫秒)
        private static readonly Dictionary<string, SortedDictionary<int, long>> _splits =
            new Dictionary<string, SortedDictionary<int, long>>();

        // 记录本局路线起点 (绝对段索引), 用于展示时裁剪
        private static int _routeStartIndex = -1;
        private static int _routeLength = -1;

        public static void Clear()
        {
            _splits.Clear();
            _routeStartIndex = -1;
            _routeLength = -1;
        }

        // 开局时登记本局路线的绝对起点与长度
        public static void SetRoute(int routeStartIndex, int routeLength)
        {
            _routeStartIndex = routeStartIndex;
            _routeLength = routeLength;
        }

        public static int RouteStartIndex { get { return _routeStartIndex; } }
        public static int RouteLength { get { return _routeLength; } }

        // 记录某个绝对段索引首次到达的时间 (只在第一次写入, 绝不覆盖、绝不填充)
        public static void Observe(string steamId, int absoluteSegmentIndex, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (absoluteSegmentIndex < 0) return;
            if (elapsedMs <= 0) return;

            SortedDictionary<int, long> map;
            if (!_splits.TryGetValue(steamId, out map))
            {
                map = new SortedDictionary<int, long>();
                _splits[steamId] = map;
            }
            if (!map.ContainsKey(absoluteSegmentIndex))
            {
                map[absoluteSegmentIndex] = elapsedMs;
            }
        }

        // 直连本地玩家: 通过 MountainProgressHandler.SetSegmentComplete 的 hook 调用,
        // 这是最可靠的分段来源 (比对手的 Flags 位域精确)
        public static void ObserveLocalSegment(int absoluteSegmentIndex, long elapsedMs)
        {
            string steamId = "";
            try { steamId = SteamIdentity.GetSteamId64(); } catch { }
            Observe(steamId, absoluteSegmentIndex, elapsedMs);
        }

        // 从玩家上报的绝对点灯数推断 (对手侧, 精度受限但不再伪造数据)
        // 只有当索引确实增加时才写入, 跳过的段留空
        public static void ObserveSegmentsLit(string steamId, int segmentsLit, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (segmentsLit <= 0) return;
            if (elapsedMs <= 0) return;

            SortedDictionary<int, long> map;
            if (!_splits.TryGetValue(steamId, out map))
            {
                map = new SortedDictionary<int, long>();
                _splits[steamId] = map;
            }
            // segmentsLit 表示"已点亮 segmentsLit 段", 对应的绝对索引是 segmentsLit - 1
            int absoluteIndex = segmentsLit - 1;
            if (absoluteIndex < 0) return;
            if (!map.ContainsKey(absoluteIndex))
            {
                map[absoluteIndex] = elapsedMs;
            }
        }

        public static int CountFor(string steamId)
        {
            SortedDictionary<int, long> map;
            if (string.IsNullOrEmpty(steamId) || !_splits.TryGetValue(steamId, out map)) return 0;
            return map.Count;
        }

        public static bool TryGet(string steamId, int absoluteSegmentIndex, out long elapsedMs)
        {
            elapsedMs = 0;
            SortedDictionary<int, long> map;
            if (string.IsNullOrEmpty(steamId) || !_splits.TryGetValue(steamId, out map)) return false;
            return map.TryGetValue(absoluteSegmentIndex, out elapsedMs);
        }

        // 导出本局路线的分段 (相对序号 0..n), 未到达的段返回 0
        // routeStartIndex / routeLength 为 -1 时表示"全山", 从索引 0 开始
        public static List<long> Export(string steamId, int routeStartIndex, int routeLength)
        {
            List<long> result = new List<long>();
            SortedDictionary<int, long> map = null;
            bool hasMap = false;
            if (!string.IsNullOrEmpty(steamId))
            {
                hasMap = _splits.TryGetValue(steamId, out map);
            }

            int start = routeStartIndex >= 0 ? routeStartIndex : 0;

            if (routeLength > 0)
            {
                for (int i = 0; i < routeLength; i++)
                {
                    long ms = 0;
                    if (hasMap && map != null) map.TryGetValue(start + i, out ms);
                    result.Add(ms);
                }
                return result;
            }

            // 全山: 取已记录到的最大索引
            if (!hasMap || map == null || map.Count == 0) return result;
            int maxIndex = 0;
            foreach (KeyValuePair<int, long> kv in map)
            {
                if (kv.Key > maxIndex) maxIndex = kv.Key;
            }
            for (int i = 0; i <= maxIndex; i++)
            {
                long ms = 0;
                map.TryGetValue(i, out ms);
                result.Add(ms);
            }
            return result;
        }

        // 该玩家是否真的点亮过任何一段 (用于判断是否需要显示空状态)
        public static bool HasAnyRecord(string steamId)
        {
            SortedDictionary<int, long> map;
            return !string.IsNullOrEmpty(steamId) && _splits.TryGetValue(steamId, out map) && map.Count > 0;
        }
    }
}
