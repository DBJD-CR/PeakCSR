using System;
using System.Collections.Generic;

namespace PeakCSR
{
    // 玩家可选的国籍目录 (排列顺序即下拉框展示顺序)
    //
    // 主权口径 (重要):
    //   只列主权国家。港澳台统一归一为中国 (CN)
    //   同理不列 UN/EU/XK/AQ 这类非主权实体
    public static class PcsrCountries
    {
        // 未设置: 空串表示玩家没选国籍, 榜单不显示旗帜
        public const string Unset = "";
        // 主权归一的目标国家
        public const string SovereignHome = "CN";

        public struct Country
        {
            public string Code;
            public string Zh;
            public string En;

            public Country(string code, string zh, string en)
            {
                Code = code;
                Zh = zh;
                En = en;
            }
        }

        // 顺序: 本国置顶, 其后按亚太 / 北美 / 拉美 / 欧洲 / 中东非 大致分组,
        // 下方注释里标出分组边界, 便于后续增补
        public static readonly Country[] All =
        {
            new Country("CN", "中国",         "China"),

            // ---- 亚太 ----
            new Country("JP", "日本",         "Japan"),
            new Country("KR", "韩国",         "South Korea"),
            new Country("SG", "新加坡",       "Singapore"),
            new Country("MY", "马来西亚",     "Malaysia"),
            new Country("TH", "泰国",         "Thailand"),
            new Country("VN", "越南",         "Vietnam"),
            new Country("ID", "印度尼西亚",   "Indonesia"),
            new Country("PH", "菲律宾",       "Philippines"),
            new Country("IN", "印度",         "India"),
            new Country("AU", "澳大利亚",     "Australia"),
            new Country("NZ", "新西兰",       "New Zealand"),

            // ---- 北美 ----
            new Country("US", "美国",         "United States"),
            new Country("CA", "加拿大",       "Canada"),
            new Country("MX", "墨西哥",       "Mexico"),

            // ---- 拉美 ----
            new Country("BR", "巴西",         "Brazil"),
            new Country("AR", "阿根廷",       "Argentina"),
            new Country("CL", "智利",         "Chile"),

            // ---- 欧洲 ----
            new Country("GB", "英国",         "United Kingdom"),
            new Country("IE", "爱尔兰",       "Ireland"),
            new Country("DE", "德国",         "Germany"),
            new Country("FR", "法国",         "France"),
            new Country("NL", "荷兰",         "Netherlands"),
            new Country("BE", "比利时",       "Belgium"),
            new Country("CH", "瑞士",         "Switzerland"),
            new Country("AT", "奥地利",       "Austria"),
            new Country("IT", "意大利",       "Italy"),
            new Country("ES", "西班牙",       "Spain"),
            new Country("PT", "葡萄牙",       "Portugal"),
            new Country("PL", "波兰",         "Poland"),
            new Country("CZ", "捷克",         "Czechia"),
            new Country("SE", "瑞典",         "Sweden"),
            new Country("NO", "挪威",         "Norway"),
            new Country("DK", "丹麦",         "Denmark"),
            new Country("FI", "芬兰",         "Finland"),
            new Country("IS", "冰岛",         "Iceland"),
            new Country("UA", "乌克兰",       "Ukraine"),
            new Country("RU", "俄罗斯",       "Russia"),

            // ---- 中东 / 非洲 ----
            new Country("TR", "土耳其",       "Turkey"),
            new Country("AE", "阿联酋",       "United Arab Emirates"),
            new Country("SA", "沙特阿拉伯",   "Saudi Arabia"),
            new Country("IL", "以色列",       "Israel"),
            new Country("ZA", "南非",         "South Africa"),
            new Country("EG", "埃及",         "Egypt"),
            new Country("NG", "尼日利亚",     "Nigeria")
        };

        public static int Count { get { return All.Length; } }

        // 主权归一表: 这些代码要么是主权国家的一部分, 要么不是主权实体,
        // 一律折到 SovereignHome。键统一用大写。
        private static readonly Dictionary<string, string> SovereignAlias =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "TW", SovereignHome },   // 台湾 -> 中国
            { "HK", SovereignHome },   // 香港 -> 中国
            { "MO", SovereignHome },   // 澳门 -> 中国
            { "UN", Unset },           // 联合国: 非主权实体
            { "EU", Unset },           // 欧盟: 非主权实体
            { "XK", Unset },           // 科索沃: 地位未定, 不单列
            { "AQ", Unset }            // 南极洲: 无主权归属
        };

        // 把任意输入收敛到"目录里真实存在的代码"或 Unset
        // 折返表命中的直接给出目标; 命中的目标是 Unset 的表示不显示
        public static string Normalize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return Unset;
            string code = raw.Trim().ToUpperInvariant();
            // 只接受两位字母, 挡住注入与脏数据
            if (code.Length != 2) return Unset;
            if (code[0] < 'A' || code[0] > 'Z' || code[1] < 'A' || code[1] > 'Z') return Unset;

            string alias;
            if (SovereignAlias.TryGetValue(code, out alias)) return alias;

            return IndexOf(code) >= 0 ? code : Unset;
        }

        // 是否是需要折返的非主权代码 (云端校验用, 与 Normalize 同口径)
        public static bool IsSovereignAlias(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            return SovereignAlias.ContainsKey(code.Trim());
        }

        public static string CodeAt(int index)
        {
            if (index < 0 || index >= All.Length) return Unset;
            return All[index].Code;
        }

        public static int IndexOf(string code)
        {
            if (string.IsNullOrEmpty(code)) return -1;
            for (int i = 0; i < All.Length; i++)
            {
                if (string.Equals(All[i].Code, code, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return -1;
        }

        public static string Label(string code, bool chinese)
        {
            int idx = IndexOf(code);
            if (idx < 0) return "";
            return chinese ? All[idx].Zh : All[idx].En;
        }

        // 下拉框选项: 首项固定为"未设置", 其后才是国家
        public static string[] Labels(bool chinese, string unsetText)
        {
            string[] result = new string[All.Length + 1];
            result[0] = unsetText ?? "";
            for (int i = 0; i < All.Length; i++)
            {
                string name = chinese ? All[i].Zh : All[i].En;
                // 选项前缀国家码, 让纯文本模式下也能看出归属
                result[i + 1] = All[i].Code + "  " + name;
            }
            return result;
        }

        // 下拉框索引 <-> 国家码 (索引 0 = 未设置)
        public static int ToDropdownIndex(string code)
        {
            int idx = IndexOf(Normalize(code));
            return idx < 0 ? 0 : idx + 1;
        }

        public static string FromDropdownIndex(int index)
        {
            if (index <= 0) return Unset;
            return CodeAt(index - 1);
        }
    }
}
