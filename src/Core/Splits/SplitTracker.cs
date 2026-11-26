using System;
using System.Collections.Generic;

namespace PeakCSR
{
    // 赛段记录来源, 数值越大优先级越高
    public enum SplitSource
    {
        // 进度点索引投影 (对手侧唯一来源, 我方仅作无篝火时的兜底)
        ProgressPoint = 0,
        // 篝火点亮事件, 本机权威, 零误差
        Campfire = 1,
        // 真正完赛时刻 (直升机倒计时结束 / 迷你跑终点 EndGame)
        Completion = 2
    }

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
        // 来源优先级, 用于同槽位覆盖判定
        public SplitSource Source;
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

        // 终段边界 = 段模型最后一段的 LastPointIdx (顶峰已并入前一段)
        // 该槽位只有完赛信号能写, 进度点最多写到它前面那一段
        private int _finalBoundaryIndex = -1;
        // 终段之前最后一个进度点段的边界; 单段路线为 -1 (终段前没有任何进度点段)
        private int _preFinalBoundaryIndex = -1;
        // 终段的真实段名 (THE KILN / THE CITADEL 之类, 不会是 PEAK)
        private string _finalSegmentKey = "";

        public void Clear()
        {
            _splits.Clear();
            _lastIndex.Clear();
            _routeStartIndex = -1;
            _routeEndIndex = -1;
            _finalBoundaryIndex = -1;
            _preFinalBoundaryIndex = -1;
            _finalSegmentKey = "";
        }

        // 开局时登记本局路线的绝对区间
        public void SetRoute(int routeStartIndex, int routeEndIndex)
        {
            _routeStartIndex = routeStartIndex;
            _routeEndIndex = routeEndIndex;
            // 换路线时旧游标全部作废, 否则新局第一段会因为"索引没涨"而漏记
            _lastIndex.Clear();
            // 终段边界属于上一张图的段模型, 必须一并作废
            _finalBoundaryIndex = -1;
            _preFinalBoundaryIndex = -1;
            _finalSegmentKey = "";
        }

        public int RouteStartIndex { get { return _routeStartIndex; } }
        public int RouteEndIndex { get { return _routeEndIndex; } }

        // 登记终段边界 (路线区间确定后由 MatchSession 调一次)
        public void SetFinalBoundary(int finalBoundaryIndex, int preFinalBoundaryIndex, string finalKey)
        {
            _finalBoundaryIndex = finalBoundaryIndex;
            _preFinalBoundaryIndex = preFinalBoundaryIndex;
            _finalSegmentKey = string.IsNullOrEmpty(finalKey) ? "" : finalKey;
        }

        // 终段槽位索引, -1 表示尚未登记
        public int FinalBoundaryIndex { get { return _finalBoundaryIndex; } }
        public string FinalSegmentKey { get { return _finalSegmentKey; } }

        // 把"进度点推出的完成槽位"钳到合法范围
        //
        // 返回 false 表示这一格不允许进度点写:
        //   - 落在终段槽位上 (终段只认完赛信号)
        //   - 单段路线没有"终段之前的进度点段", 整条路线的进度点槽位都被终段占用
        //   - 落在本局路线区间之外 (跳图残值)
        public bool TryClampProgressBoundary(int completedIndex, out int clamped)
        {
            clamped = completedIndex;
            if (completedIndex < 0) return false;
            if (_finalBoundaryIndex >= 0 && completedIndex >= _finalBoundaryIndex)
            {
                if (_preFinalBoundaryIndex < 0) return false;
                clamped = _preFinalBoundaryIndex;
            }
            if (_routeStartIndex >= 0 && clamped < _routeStartIndex) return false;
            if (_routeEndIndex >= 0 && clamped > _routeEndIndex) return false;
            return clamped >= 0;
        }

        // ===== 本地权威采集 (篝火事件驱动) =====

        // 点亮篝火时由 CampfireSplitPatch 调用
        // boundaryIndex: 该篝火对应的"段终点进度点索引", 用于与对手侧数据对齐
        public void ObserveCampfire(string steamId, string segmentKey, int boundaryIndex, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (string.IsNullOrEmpty(segmentKey)) return;
            if (elapsedMs <= 0) return;
            if (boundaryIndex < 0) boundaryIndex = 0;
            Write(steamId, boundaryIndex, segmentKey, elapsedMs, false, SplitSource.Campfire);
        }

        // 真正完赛时刻写入终段, 这是终段唯一的权威来源
        // boundaryIndex 传 -1 时取本局登记的终段边界; segmentKey 传空时取终段名
        public void ObserveCompletion(string steamId, string segmentKey, int boundaryIndex, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (elapsedMs <= 0) return;
            if (boundaryIndex < 0) boundaryIndex = _finalBoundaryIndex;
            if (boundaryIndex < 0) return;
            if (string.IsNullOrEmpty(segmentKey)) segmentKey = _finalSegmentKey;
            if (string.IsNullOrEmpty(segmentKey)) return;
            Write(steamId, boundaryIndex, segmentKey, elapsedMs, false, SplitSource.Completion);
        }

        // 本地进度点首次到达 (分段的一个补充来源: 有些段没有篝火, 只有进度点)
        //
        // 注意: GoToSegment 完成后 SetSegmentComplete 会把 maxProgressPointReached
        // 推到"下一段", 这条记录代表的是"进入下一段"的时刻, 不是"离开本段"。
        // 它只能作为篝火缺失时的兜底, 优先级低于篝火。
        public void ObserveLocalSegment(string steamId, int absoluteIndex, string segmentKey, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (absoluteIndex < 0) return;
            if (elapsedMs <= 0) return;
            Write(steamId, absoluteIndex, segmentKey, elapsedMs, false, SplitSource.ProgressPoint);
        }

        // 本地跳帧回填: 中间漏掉的段用同一帧时间标记, UI 上带 ~
        public void ObserveLocalSegmentEstimated(string steamId, int absoluteIndex, string segmentKey, long elapsedMs)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            if (absoluteIndex < 0) return;
            if (elapsedMs <= 0) return;
            Write(steamId, absoluteIndex, segmentKey, elapsedMs, true, SplitSource.ProgressPoint);
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

            // 首帧只登记基线, 不产生任何分段记录
            int lastIdx;
            if (!_lastIndex.TryGetValue(steamId, out lastIdx))
            {
                _lastIndex[steamId] = pointIndex;
                return;
            }

            if (pointIndex <= lastIdx) return;
            _lastIndex[steamId] = pointIndex;

            // maxProgressPointReached = N 表示"已进入第 N 段", 因此完成的是第 N-1 段
            // 终段 (含并入的顶峰) 一律不在这里写: 进度点判据是纯几何高度,
            // 玩家越过顶峰点位后还要等直升机倒计时才完赛, 两者差约半分钟
            int lastCompleted;
            if (!TryClampProgressBoundary(pointIndex - 1, out lastCompleted)) return;

            // lastIdx 是上一次的 pointIndex, 也就是本次第一个待补的槽位
            int from = lastIdx;
            if (from < 0) from = 0;
            for (int idx = from; idx <= lastCompleted; idx++)
            {
                if (inRoute != null && !inRoute(idx)) continue;
                string key = keyAt(idx);
                if (string.IsNullOrEmpty(key)) continue;
                bool estimated = (idx != lastCompleted);
                Write(steamId, idx, key, liveTimeMs, estimated, SplitSource.ProgressPoint);
            }
        }

        // 仅推进游标, 不产生赛段记录 (旧接口, 目前无调用方)
        //
        // 注意: 这里的 segmentsLit 是"整座山的绝对索引", 单群系从第 3 个群系开局时
        // 一上来就是 10+, 与 progressPoints 下标不是同一套坐标。若将来要恢复使用,
        // 必须先从整山索引换算到本局路线内的索引, 不能直接当进度点下标用
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

        private void Write(string steamId, int index, string key, long elapsedMs, bool estimated, SplitSource source)
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
                // 高优先级已存在: 低优先级不得覆盖 (篝火 > 进度点)
                if (existing.Source > source) return;

                if (existing.Source == source)
                {
                    // 同源: 精确记录不被估算值覆盖, 估算值可被精确值修正
                    if (!existing.Estimated) return;
                    if (estimated) return;
                }
                // existing.Source < source: 篝火改写进度点占位, 直接覆盖

                existing.ElapsedMs = elapsedMs;
                existing.Estimated = estimated;
                existing.Source = source;
                if (!string.IsNullOrEmpty(key)) existing.Key = key;
                existing.AbsoluteIndex = index;
                map[index] = existing;
                return;
            }

            SplitEntry entry = new SplitEntry();
            entry.Key = key;
            entry.ElapsedMs = elapsedMs;
            entry.Estimated = estimated;
            entry.Source = source;
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
