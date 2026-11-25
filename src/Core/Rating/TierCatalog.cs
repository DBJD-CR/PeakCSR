using System;

namespace PeakCSR
{
    // 表驱动段位定义 (取代原 EloCalculator 中 8 个方法重复手写的 if (elo >= 1800) 阶梯)
    public static class TierCatalog
    {
        public struct TierDef
        {
            public int Floor;          // 段位下限 (tenderfoot 用 0 表示 -inf)
            public int SubDivFloor;    // 细分 I/II/III 的基准分
            public int SubDivSpan;     // 细分区间宽度
            public int ProgFloor;      // 进度条下限
            public int ProgCeil;       // 进度条上限
            public string NameKey;     // 完整段位名 locKey
            public string ShortKey;    // 简短段位名 locKey
            public string VanillaKey;  // 原版 RankedMenuIcons 兼容标识
            public string DarkHex;     // 深色背景高亮色
            public string LightHex;    // 浅色羊皮纸高对比色
        }

        private static readonly TierDef[] Table = new TierDef[]
        {
            new TierDef { Floor = 1800, SubDivFloor = 1800, SubDivSpan = 300, ProgFloor = 1800, ProgCeil = 2100,
                NameKey = "tier.sherpa", ShortKey = "tier.short.sherpa", VanillaKey = "SHERPA",
                DarkHex = "33E6FF", LightHex = "006B8F" },   // 夏尔巴: 璀璨青蓝 / 深邃青蓝
            new TierDef { Floor = 1550, SubDivFloor = 1550, SubDivSpan = 250, ProgFloor = 1550, ProgCeil = 1800,
                NameKey = "tier.alpinist", ShortKey = "tier.short.alpinist", VanillaKey = "ALPINIST",
                DarkHex = "FFD933", LightHex = "B87700" },   // 登山家: 耀眼钻金 / 皇家暗金
            new TierDef { Floor = 1350, SubDivFloor = 1350, SubDivSpan = 200, ProgFloor = 1350, ProgCeil = 1550,
                NameKey = "tier.climber", ShortKey = "tier.short.climber", VanillaKey = "CLIMBER",
                DarkHex = "45E680", LightHex = "1E7E45" },   // 攀岩者: 翡翠绿 / 祖母深绿
            new TierDef { Floor = 1150, SubDivFloor = 1150, SubDivSpan = 200, ProgFloor = 1150, ProgCeil = 1350,
                NameKey = "tier.hiker", ShortKey = "tier.short.hiker", VanillaKey = "HIKER",
                DarkHex = "FF9E44", LightHex = "B85414" },   // 徒步者: 琥珀金橙 / 暖铜赤橙
            new TierDef { Floor = 950, SubDivFloor = 950, SubDivSpan = 200, ProgFloor = 950, ProgCeil = 1150,
                NameKey = "tier.scout", ShortKey = "tier.short.scout", VanillaKey = "SCOUT",
                DarkHex = "8CC8FF", LightHex = "2E5C8A" },   // 侦察兵: 精钢冰蓝 / 钢青色
            new TierDef { Floor = 0, SubDivFloor = 650, SubDivSpan = 300, ProgFloor = 750, ProgCeil = 950,
                NameKey = "tier.tenderfoot", ShortKey = "tier.short.tenderfoot", VanillaKey = "SCOUT",
                DarkHex = "A6ADB8", LightHex = "5A6270" }    // 初学者: 黑煤岩灰 / 炭灰色
        };

        // 本地化解析器由 Boot 层注入 (绑定 CsrLoc.T), Core 不直接依赖 UI 词典
        private static Func<string, string> _localizer = delegate(string key) { return key; };

        public static void SetLocalizer(Func<string, string> localizer)
        {
            if (localizer != null) _localizer = localizer;
        }

        public static TierDef GetDef(int elo)
        {
            for (int i = 0; i < Table.Length; i++)
            {
                if (elo >= Table[i].Floor) return Table[i];
            }
            return Table[Table.Length - 1];
        }

        // 获取基础大段位多语言名称
        public static string GetTierBaseName(int elo)
        {
            return _localizer(GetDef(elo).NameKey);
        }

        // 计算段位内 I / II / III 级细分 (1 为最高小段，3 为初入小段)
        public static string GetTierSubDivisionRoman(int elo)
        {
            TierDef def = GetDef(elo);
            double ratio = (double)(Math.Max(def.SubDivFloor, elo) - def.SubDivFloor) / def.SubDivSpan;
            if (ratio >= 0.666) return "I";
            if (ratio >= 0.333) return "II";
            return "III";
        }

        // 获取带 I / II / III 细分的完整段位名称
        public static string GetTierName(int elo)
        {
            return GetTierBaseName(elo) + " " + GetTierSubDivisionRoman(elo);
        }

        // 获取简短段位+小段位名称，用于天梯列表紧凑展示
        public static string GetTierShortLabel(int elo)
        {
            return _localizer(GetDef(elo).ShortKey) + " " + GetTierSubDivisionRoman(elo);
        }

        // 获取当前小段位内部进度 (0.0 ~ 1.0)，用于赛后动画进度条
        public static float GetTierProgress01(int elo, out int tierFloor, out int tierCeil)
        {
            TierDef def = GetDef(elo);
            tierFloor = def.ProgFloor;
            tierCeil = def.ProgCeil;
            float t = (float)(elo - def.ProgFloor) / Math.Max(1, def.ProgCeil - def.ProgFloor);
            if (t < 0f) t = 0f;
            if (t > 1f) t = 1f;
            return t;
        }

        // 获取原版 RankedMenuIcons 兼容的 Tier 标识
        public static string GetVanillaTierKey(int elo)
        {
            return GetDef(elo).VanillaKey;
        }

        // 深色背景下的段位高亮色 (IMGUI 与深色赛后战报使用)
        public static string GetTierHexColor(int elo)
        {
            return GetDef(elo).DarkHex;
        }

        // 浅色羊皮纸背景下的段位高对比度配色 (天梯表格与木牌专用)
        public static string GetTierHexColorOnLight(int elo)
        {
            return GetDef(elo).LightHex;
        }
    }
}
