namespace PeakCSR
{
    // 排位中心布局常量单一事实源
    public static class MenuLayout
    {
        // ===== 窗口 =====
        public const float WindowWidth = 1300f;
        public const float WindowHeight = 1090f;
        public const float TitleBarHeight = 82f;
        public const float ColGap = 18f;
        public const float BoxPad = 24f;

        // ===== 左右分栏 =====
        public const float LeftX = 26f;
        public const float LeftW = 600f;
        public const float RightX = LeftX + LeftW + ColGap;
        public const float RightW = 630f;
        public const float FullW = LeftW + ColGap + RightW;

        // ===== 三行 =====
        // Row1: 左 赛制与房间 | 右 对局状态与实时队列 (高度不变)
        public const float Row1Y = 8f;
        public const float Row1H = 380f;

        // Row2: 左 实时匹配  | 右 历史战绩 (吃掉整列高度)
        public const float Row2Y = Row1Y + Row1H + ColGap;
        public const float Row2H = 250f;

        // Row3: 天梯速览 (领奖台 + 我的排名卡 + 完整榜按钮), 宽度收成单列
        public const float Row3Y = Row2Y + Row2H + ColGap;
        public const float Row3H = 280f;
        public const float Row3W = LeftW;
        // 右列历史战绩贯穿 Row2 与 Row3, 底边与天梯对齐
        public const float HistoryH = Row3Y + Row3H - Row2Y;
        public static float ContentBottomY { get { return Row3Y + Row3H; } }

        // ===== 独立天梯窗口 =====
        public const float BoardWindowWidth = 1300f;
        public const float BoardWindowHeight = 920f;
        public const float BoardTitleBarHeight = 82f;
    }
}
