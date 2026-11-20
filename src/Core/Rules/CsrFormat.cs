using System;

namespace PeakCSR
{
    // 赛制/难度命名与时间格式化桥 (Core 内不直接依赖 UI 词典, 由 Boot 注入 CsrLoc 实现)
    public static class MatchNaming
    {
        private static Func<int, string> _ascentName = delegate { return "?"; };
        private static Func<int, string> _ascentShort = delegate { return "?"; };
        private static Func<McsrRuleFormat, int, string> _formatFull = delegate { return "?"; };

        public static void Bind(
            Func<int, string> ascentName,
            Func<int, string> ascentShort,
            Func<McsrRuleFormat, int, string> formatFull)
        {
            if (ascentName != null) _ascentName = ascentName;
            if (ascentShort != null) _ascentShort = ascentShort;
            if (formatFull != null) _formatFull = formatFull;
        }

        // 难度档位显示名 (走本地化, 支持菜鸟新人/标准顶峰/天阶 1~8)
        public static string GetAscentDisplayName(int ascent)
        {
            return _ascentName(ascent);
        }

        public static string GetAscentShortName(int ascent)
        {
            return _ascentShort(ascent);
        }

        public static string GetFormatDisplayName(McsrRuleFormat format, int ascent)
        {
            return _formatFull(format, ascent);
        }
    }

    // 统一时间格式化 (纯函数)
    public static class CsrFormat
    {
        // 不足 1 小时用 mm:ss.fff, 超过 1 小时用 h:mm:ss.fff
        public static string FormatClock(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
            {
                return ((int)ts.TotalHours) + ":" + ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00") + "." + ts.Milliseconds.ToString("000");
            }
            return ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00") + "." + ts.Milliseconds.ToString("000");
        }

        public static string FormatClockMs(long ms)
        {
            return FormatClock(ms / 1000.0);
        }

        // 排队/计时用的 mm:ss 短格式
        public static string FormatElapsed(float seconds)
        {
            if (float.IsNaN(seconds) || seconds < 0f) seconds = 0f;
            int total = (int)Math.Floor(seconds);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }
    }
}
