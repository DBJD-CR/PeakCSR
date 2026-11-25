using System;

namespace PeakCSR
{
    // PB 类别: 与 player_pbs.category 云端列一一对应
    public enum PbCategory
    {
        // 标准全山 (主展示字段)
        AllPeak = 0,
        // 天阶 N (subKey = 天阶号)
        Ascent = 1,
        // 单群系冲刺 (subKey = 群系索引)
        SprintBiome = 2,
        // 群系赛段 (subKey = 群系索引, 覆盖任意模式)
        BiomeSegment = 3
    }

    // PB 唯一键: category + subKey
    //
    // 赛段 PB 的段名与群系一一对应 (BiomeSegmenter 会把相邻同名进度点合并成一段),
    // 所以 subKey 直接用群系索引就够了, 不需要再带一个段名字符串。
    public struct PbKey : IEquatable<PbKey>
    {
        public PbCategory Category;
        public int SubKey;

        public PbKey(PbCategory category, int subKey)
        {
            Category = category;
            SubKey = subKey;
        }

        public static PbKey AllPeak { get { return new PbKey(PbCategory.AllPeak, 0); } }

        public static PbKey Ascent(int ascent) { return new PbKey(PbCategory.Ascent, ascent); }

        public static PbKey SprintBiome(int biome) { return new PbKey(PbCategory.SprintBiome, biome); }

        public static PbKey BiomeSegment(int biome) { return new PbKey(PbCategory.BiomeSegment, biome); }

        public bool Equals(PbKey other)
        {
            return Category == other.Category && SubKey == other.SubKey;
        }

        public override bool Equals(object obj)
        {
            return obj is PbKey && Equals((PbKey)obj);
        }

        public override int GetHashCode()
        {
            return ((int)Category << 16) ^ (SubKey & 0xFFFF);
        }

        public override string ToString()
        {
            return ((int)Category) + ":" + SubKey;
        }
    }

    // 单条最佳成绩
    public sealed class PersonalBest
    {
        public PbKey Key;
        public long TimeMs;
        public int LevelIndex;
        public int Seed;
        public int Ascent;
        public PcsrRuleFormat Format;
        public long AchievedAtMs;

        public bool IsValid { get { return TimeMs > 0; } }
    }
}
