namespace PeakCSR
{
    // 房间魔数契约 (线上新老客户端互认 PCSR 房间的唯一依据)
    //
    // offset 分配:
    //   0..29  = 计分房, offset = 赛制下标 * 10 + (天阶 - MinAscent)
    //   30..59 = 非计分房, offset = 30 + 赛制下标 * 10 + (天阶 - MinAscent)
    //
    // 编进魔数后 rated 成为房间属性, 匹配池天然按它隔离
    //
    // 兼容性: 旧客户端 IsCsrMagicMs 只认 30030..30070, 看到非计分房会走
    // TryDecodeRuleset 兜底并因赛制对不上而拒绝加入 —— 降级是安全的
    public static class RoomMagicCodec
    {
        public const int BaseAutoEndMagicMs = 30000;
        public const int RatedOffsetSpan = 30;
        public const int MagicSpan = 60;
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

        // rated 为 true 时落在 0..29 (与旧客户端完全兼容), false 时落在 30..59
        public static int GetMagicAutoEndMs(PcsrRuleFormat format, int ascent, bool rated = true)
        {
            int fmtIdx = (int)format;
            if (fmtIdx < 0) fmtIdx = 0;
            if (fmtIdx > 2) fmtIdx = 2;
            int offset = fmtIdx * 10 + EncodeAscent(ascent);
            if (!rated) offset += RatedOffsetSpan;
            return BaseAutoEndMagicMs + offset;
        }

        public static bool IsCsrMagicMs(int autoEndMs)
        {
            return autoEndMs >= BaseAutoEndMagicMs && autoEndMs < BaseAutoEndMagicMs + MagicSpan;
        }

        // 从魔数还原赛制、难度与计分属性
        public static void ParseMagicAutoEndMs(int autoEndMs, out PcsrRuleFormat format, out int ascent, out bool rated)
        {
            int offset = autoEndMs - BaseAutoEndMagicMs;
            if (offset < 0) offset = 0;
            if (offset >= MagicSpan) offset = MagicSpan - 1;
            rated = offset < RatedOffsetSpan;
            if (!rated) offset -= RatedOffsetSpan;
            int f = offset / 10;
            if (f > 2) f = 2;
            format = (PcsrRuleFormat)f;
            ascent = DecodeAscent(offset % 10);
        }

        // 兼容旧签名: 只要赛制与难度, 不关心计分属性
        public static void ParseMagicAutoEndMs(int autoEndMs, out PcsrRuleFormat format, out int ascent)
        {
            bool rated;
            ParseMagicAutoEndMs(autoEndMs, out format, out ascent, out rated);
        }

        // 用 天阶 + 路线 反推房间赛制: 天阶 0/-1 且无路线 = 全山, 有路线 = 单群系, 其余高难天阶
        public static bool TryDecodeRuleset(int ascent, int route, out PcsrRuleFormat format, out int decodedAscent)
        {
            decodedAscent = ClampAscent(ascent);
            if (route > 0)
            {
                format = PcsrRuleFormat.BiomeSprint;
                return true;
            }
            if (ascent >= 1)
            {
                format = PcsrRuleFormat.HardcoreAscent4;
                return true;
            }
            format = PcsrRuleFormat.StandardAllPeak;
            return true;
        }
    }
}
