namespace PeakCSR
{
    // 房间魔数契约 (护栏 1: 线上新老客户端互认 PCSR 房间的唯一依据, 公式 100% 不可变更)
    // AutoEndMs = 30000 + 赛制下标 * 10 + (天阶 - MinAscent)
    public static class RoomMagicCodec
    {
        public const int BaseAutoEndMagicMs = 30000;
        public const int MagicSpan = 40;
        public const int MinAscent = -1;
        public const int MaxAscent = 8;

        public static int ClampAscent(int ascent)
        {
            if (ascent < MinAscent) return MinAscent;
            if (ascent > MaxAscent) return MaxAscent;
            return ascent;
        }

        // 难度档位 -1..8 编码为 0..9 塞进魔数个位
        public static int EncodeAscent(int ascent)
        {
            return ClampAscent(ascent) - MinAscent;
        }

        public static int DecodeAscent(int code)
        {
            int c = code < 0 ? 0 : (code > 9 ? 9 : code);
            return c + MinAscent;
        }

        public static int GetMagicAutoEndMs(McsrRuleFormat format, int ascent)
        {
            int fmtIdx = (int)format;
            if (fmtIdx < 0) fmtIdx = 0;
            if (fmtIdx > 2) fmtIdx = 2;
            return BaseAutoEndMagicMs + fmtIdx * 10 + EncodeAscent(ascent);
        }

        public static bool IsCsrMagicMs(int autoEndMs)
        {
            return autoEndMs >= BaseAutoEndMagicMs && autoEndMs <= BaseAutoEndMagicMs + MagicSpan;
        }

        // 从魔数还原赛制与难度
        public static void ParseMagicAutoEndMs(int autoEndMs, out McsrRuleFormat format, out int ascent)
        {
            int offset = autoEndMs - BaseAutoEndMagicMs;
            if (offset < 0) offset = 0;
            if (offset > MagicSpan) offset = MagicSpan;
            int f = offset / 10;
            if (f > 2) f = 2;
            format = (McsrRuleFormat)f;
            ascent = DecodeAscent(offset % 10);
        }

        // 用 天阶 + 路线 反推房间赛制: 天阶 0/-1 且无路线 = 全山, 有路线 = 单群系, 其余高难天阶
        public static bool TryDecodeRuleset(int ascent, int route, out McsrRuleFormat format, out int decodedAscent)
        {
            decodedAscent = ClampAscent(ascent);
            if (route > 0)
            {
                format = McsrRuleFormat.BiomeSprint;
                return true;
            }
            if (ascent >= 1)
            {
                format = McsrRuleFormat.HardcoreAscent4;
                return true;
            }
            format = McsrRuleFormat.StandardAllPeak;
            return true;
        }
    }
}
