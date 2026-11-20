namespace PeakCSR
{
    // 集中定义深浅色板与 PcsrRichText 语义化颜色包装器 (方案 3.3.3)
    // 所有 hex 值与重构前的 UI 视觉完全一致, 词条/表格/战报统一从这里取色
    public static class PcsrColors
    {
        // ===== 状态语义色 (浅色羊皮纸底) =====
        public const string Positive = "1E7E45";    // 祖母深绿: 胜/加分/我方更快
        public const string Danger = "C62828";      // 警示红: 负/扣分/我方更慢
        public const string Warning = "FFD933";     // 钻金: 提示/投票
        public const string VoidTone = "5A6270";    // 炭灰: 作废/无数据
        public const string Muted = "8A93A5";       // 次级灰: 占位说明
        public const string AccentBlue = "0B5FA5";  // 强调蓝: 自己的 ELO/群系
        public const string VoidAmber = "FFCC66";   // 作废提示琥珀

        // ===== 深色背景语义色 (IMGUI 旧面板/深色战报) =====
        public const string PositiveBright = "55E673";
        public const string DangerBright = "FF6666";
        public const string NeutralGray = "AAAAAA";
        public const string Orange = "FF6B2B";
        public const string DownRed = "FF4444";

        // ===== 档案卡三赛制品牌色 =====
        public const string ModeStd = "0B5FA5";
        public const string ModeAsc = "7B4BC4";
        public const string ModeSpr = "C06A00";
        public const string StreakNeutral = "5A6270";

        // ===== 队列比例条 (RGB 浮点, 原 LiveQueuePanel 配色) =====
        public const string BarTrackHex = "D1D9D4";     // 0.82,0.85,0.83
        public const string BarFillHex = "296BE6";      // 0.16,0.42,0.9

        // ===== 天梯斑马纹/表头 =====
        public const string BoardHeaderBgHex = "D1DCDE"; // 0.82,0.86,0.87
        public const string BoardStripeHex = "DBE2E0";   // 0.86,0.89,0.88

        public static string Wrap(string hex, string content)
        {
            return "<color=#" + hex + ">" + content + "</color>";
        }

        public static string WrapBold(string hex, string content)
        {
            return "<color=#" + hex + "><b>" + content + "</b></color>";
        }
    }
}
