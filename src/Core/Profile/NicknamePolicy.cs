using System.Text;
using System.Text.RegularExpressions;

namespace PeakCSR
{
    // 统一昵称白名单清洗与长度校验 (原 PcsrMenuWindow.SanitizeNickname 收口)
    // 与 cloudflare 端 nickname.js 保持同一规则: 剥 TMP 富文本 → 白名单过滤 → 压缩空白 → 长度钳制
    public static class NicknamePolicy
    {
        public const int MaxLen = 16;
        public const int MinLen = 2;

        private static readonly Regex RichTagRegex = new Regex(
            "</?(?:align|allcaps|alpha|b|br|color|cspace|font|gradient|i|indent|line-height|line-indent|link|lowercase|margin|mark|material|mspace|nobr|noparse|page|pos|quad|rotate|s|size|smallcaps|space|sprite|strikethrough|style|sub|sup|u|uppercase|voffset|width)(?:[=\\s][^<>]*)?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex MultiSpaceRegex = new Regex("\\s{2,}", RegexOptions.Compiled);

        public static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = RichTagRegex.Replace(raw, "");
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsControl(c) || c == '|')
                {
                    sb.Append(' ');
                    continue;
                }
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.' || c == '·')
                {
                    sb.Append(c);
                }
            }
            s = MultiSpaceRegex.Replace(sb.ToString().Trim(), " ");
            if (s.Length > MaxLen) s = s.Substring(0, MaxLen).Trim();
            if (s.Length > 0 && s.Length < MinLen) return "";
            return s;
        }
    }
}
