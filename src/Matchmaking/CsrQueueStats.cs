namespace PeakCSR
{
    // 实时队列情报快照 (原 MatchController.CsrQueueStats 原样迁移)
    //
    // 口径统一说明: 全部字段都是"人数"语义, 不再有房间数概念。
    // 一个 2 人排位房里的两个玩家, 在匹配中阶段算 2 人匹配中, 在激战中阶段算 2 人激战中。
    public class CsrQueueStats
    {
        // 正在玩 PCSR 的总人数 (匹配中 + 激战中)
        public int OnlinePlayers;
        // 匹配中人数 (还在 lobby 等对手, 含自己)
        public int WaitingPlayers;
        // 激战中人数 (已开打/载图/倒计时的房内玩家, 含自己)
        public int ActivePlayers;

        // ===== 分赛制人数 =====
        // 该赛制下的总人数 (匹配中 + 激战中)
        public int StdPlayers;
        public int AscPlayers;
        public int SprPlayers;
        // 该赛制下匹配中的人数
        public int StdWaiting;
        public int AscWaiting;
        public int SprWaiting;
        // 该赛制下激战中的人数
        public int StdActive;
        public int AscActive;
        public int SprActive;

        public float UpdatedAt;
        // 24 小时制刷新时间戳 (UTC 毫秒), 供 UI 显示"最近刷新 HH:mm:ss"
        public long RefreshedAtUtcMs;
        public bool HasData;

        public string RefreshedClock
        {
            get { return CloudLadderClient.FormatRefreshClock(RefreshedAtUtcMs); }
        }
    }
}
