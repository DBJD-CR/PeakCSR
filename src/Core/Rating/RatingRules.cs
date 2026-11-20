using System;
using System.IO;
using Newtonsoft.Json;

namespace PeakCSR
{
    // shared/rating-rules.json 的不可变配置模型 (单一事实源)
    // C# 经 EmbeddedResource 读取; Cloudflare Worker 直接 import 同一文件
    public sealed class RatingRules
    {
        public sealed class EloSection
        {
            public double ScaleDenominator;
            public int ClampMin;
            public int ClampMax;
        }

        public sealed class KFactors
        {
            public int Placement;
            public int Regular;
            public int HighElo;
        }

        public sealed class WinFloor
        {
            public int FullPeak;
            public int BiomeSprint;
        }

        public sealed class LossCap
        {
            public int FullPeak;
            public int BiomeSprint;
        }

        public sealed class HighEloFloor
        {
            public int Threshold;
            public int WinCap;
        }

        public sealed class ProgressCompensation
        {
            public double SprintScale;
            public double TenderfootScale;
            public Range LeadBonus;
            public Range CompletionRelief;
            public double CompletionStart;
            public double CompletionSpan;
            public Range GapTerm;
            public CapRange Cap;

            public sealed class Range
            {
                public double Placement;
                public double Regular;
            }

            public sealed class CapRange
            {
                public int Placement;
                public int Regular;
            }
        }

        public sealed class AntiBoostSection
        {
            public int GapThreshold;
            public double GapWeight;
            public int WindowMinutes;
            public int MaxRecentAgainstSameOpponent;
            public double RepeatWeight;
        }

        public sealed class SeedEloSection
        {
            public int Min;
            public int Max;
            public int VanillaBase;
            public double VanillaWeight;
        }

        public EloSection Elo;
        public double[] AscentMultipliers;
        public double TenderfootMultiplier;
        public double BiomeSprintFactor;
        public int PlacementMatches;
        [JsonProperty("kFactors")] public KFactors K;
        [JsonProperty("minWinDelta")] public WinFloor WinFloorSection;
        [JsonProperty("maxLossDelta")] public LossCap LossCapSection;
        [JsonProperty("highEloFloor")] public HighEloFloor HighFloor;
        [JsonProperty("progressCompensation")] public ProgressCompensation Progress;
        [JsonProperty("antiBoost")] public AntiBoostSection AntiBoost;
        [JsonProperty("seedElo")] public SeedEloSection Seed;

        private static RatingRules _instance;

        // 从程序集内嵌资源加载 (资源名由 csproj 的 EmbeddedResource LogicalName 固定)
        public static RatingRules Instance
        {
            get
            {
                if (_instance == null) _instance = LoadEmbedded();
                return _instance;
            }
        }

        private static RatingRules LoadEmbedded()
        {
            System.Reflection.Assembly asm = typeof(RatingRules).Assembly;
            using (Stream stream = asm.GetManifestResourceStream("PeakCSR.rating-rules.json"))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("Embedded resource 'PeakCSR.rating-rules.json' not found");
                }
                using (StreamReader reader = new StreamReader(stream))
                {
                    return JsonConvert.DeserializeObject<RatingRules>(reader.ReadToEnd());
                }
            }
        }
    }
}
