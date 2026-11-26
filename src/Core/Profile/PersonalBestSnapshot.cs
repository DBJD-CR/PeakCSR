using System;

namespace PeakCSR
{
    // 某个玩家 PB 的只读快照 (供 UI 直接取)
    //
    // 存在的理由: 档案卡可能在看"别人"的 PB, 过去直接从单例台账取数,
    // 结果看别人档案时显示的是自己的 PB。把"给谁看"显式化成快照后,
    // UI 无法再误取到别人的数据。
    public sealed class PersonalBestSnapshot
    {
        public static readonly PersonalBestSnapshot Empty = new PersonalBestSnapshot(new PersonalBestLedger());

        private readonly PersonalBestLedger _ledger;

        public PersonalBestSnapshot(PersonalBestLedger ledger)
        {
            _ledger = ledger ?? new PersonalBestLedger();
        }

        public int Count { get { return _ledger.Count; } }

        public long AllPeakMs { get { return _ledger.TimeOrZero(PbKey.AllPeak); } }

        public long AscentMs(int ascent) { return _ledger.TimeOrZero(PbKey.Ascent(ascent)); }

        public long SprintMs(int biome) { return _ledger.TimeOrZero(PbKey.SprintBiome(biome)); }

        public long SegmentMs(int biome) { return _ledger.TimeOrZero(PbKey.BiomeSegment(biome)); }

        // ===== 达成时间 (Unix 毫秒, 0 表示无记录) =====

        public long AllPeakAtMs { get { return _ledger.AchievedAtOrZero(PbKey.AllPeak); } }

        public long AscentAtMs(int ascent) { return _ledger.AchievedAtOrZero(PbKey.Ascent(ascent)); }

        public long SprintAtMs(int biome) { return _ledger.AchievedAtOrZero(PbKey.SprintBiome(biome)); }

        public long SegmentAtMs(int biome) { return _ledger.AchievedAtOrZero(PbKey.BiomeSegment(biome)); }

        // 最佳可能时间: 常规全山路线每层取最短候选之和
        // 返回 0 表示关键赛段还没刷出来, 无法计算
        public long BestPossibleMs
        {
            get { return BptCalculator.ComputeBestPossible(SegmentMs); }
        }

        // BPT 算不出来时缺的是哪一层 (-1 表示不缺)
        public int MissingBptLayer
        {
            get
            {
                int layer;
                BptCalculator.ComputeBestPossible(SegmentMs, out layer);
                return layer;
            }
        }

        public string DescribeMissingBptLayer(bool chinese)
        {
            int layer = MissingBptLayer;
            return layer < 0 ? "" : BptCalculator.DescribeLayer(layer, chinese);
        }

        public string DescribeBptRoute(bool chinese)
        {
            return BptCalculator.Describe(SegmentMs, chinese);
        }
    }
}
