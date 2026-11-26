using System.Collections.Generic;

namespace PeakCSR
{
    // PB 台账纯领域逻辑: 只做"更优才覆盖"与查询, 不碰 IO / UI / 网络
    // 本地与云端共用同一套合并口径, 保证两边算出来的 PB 完全一致
    public sealed class PersonalBestLedger
    {
        // 通用合理性下限: 防止 0 值与异常跳变污染 PB
        public const long MinPlausibleMs = 30L * 1000L;
        // 全山 PB 的物理下限: 5 分钟以内不可能是真成绩，与云端保持一致
        public const long MinAllPeakMs = 5L * 60L * 1000L;
        // 天阶 PB 的下限, 只拦明显不可能的值
        public const long MinAscentPbMs = 2L * 60L * 1000L;
        // 合理性上限: 单局超过 6 小时视为脏数据
        public const long MaxPlausibleMs = 6L * 60L * 60L * 1000L;

        private readonly Dictionary<PbKey, PersonalBest> _map = new Dictionary<PbKey, PersonalBest>();

        public int Count { get { return _map.Count; } }

        public IReadOnlyDictionary<PbKey, PersonalBest> All { get { return _map; } }

        public bool TryGet(PbKey key, out PersonalBest pb)
        {
            return _map.TryGetValue(key, out pb);
        }

        // 查不到返回 0, 便于 UI 直接判 "未设置"
        public long TimeOrZero(PbKey key)
        {
            PersonalBest pb;
            return _map.TryGetValue(key, out pb) && pb != null ? pb.TimeMs : 0L;
        }

        // 达成时间 (Unix 毫秒); 查不到返回 0
        public long AchievedAtOrZero(PbKey key)
        {
            PersonalBest pb;
            return _map.TryGetValue(key, out pb) && pb != null ? pb.AchievedAtMs : 0L;
        }

        // 单条 PB 是否可信 (UI 与采集共用同一判据)
        public static bool IsPlausible(long timeMs)
        {
            return timeMs >= MinPlausibleMs && timeMs <= MaxPlausibleMs;
        }

        // 按类别取下限: 整局成绩有更强的物理约束, 赛段则沿用通用下限
        public static long MinTimeFor(PbCategory category)
        {
            if (category == PbCategory.AllPeak) return MinAllPeakMs;
            if (category == PbCategory.Ascent) return MinAscentPbMs;
            return MinPlausibleMs;
        }

        // 带类别的可信判定
        public static bool IsPlausible(PbKey key, long timeMs)
        {
            return timeMs >= MinTimeFor(key.Category) && timeMs <= MaxPlausibleMs;
        }

        // 更优才覆盖 (严格小于), 同毫秒保留先达成的那条
        // 返回 true 表示本次真的刷新了记录
        public bool UpsertIfBetter(PersonalBest candidate)
        {
            if (candidate == null || !candidate.IsValid) return false;
            if (!IsPlausible(candidate.Key, candidate.TimeMs)) return false;

            PersonalBest existing;
            if (_map.TryGetValue(candidate.Key, out existing) && existing != null && existing.TimeMs > 0)
            {
                if (candidate.TimeMs >= existing.TimeMs) return false;
            }

            _map[candidate.Key] = candidate;
            return true;
        }

        // 从存档 / 云端恢复时直接落位, 不做更优判定 (来源可信由调用方保证)
        public void Restore(PersonalBest pb)
        {
            if (pb == null || !pb.IsValid) return;
            _map[pb.Key] = pb;
        }

        public void Clear()
        {
            _map.Clear();
        }
    }
}
