namespace PeakCSR
{
    // 实时队列情报快照 (原 MatchController.CsrQueueStats 原样迁移)
    public class CsrQueueStats
    {
        public int WaitingRooms;
        public int ActiveRooms;
        public int OnlinePlayers;
        public int StdWaiting;
        public int AscWaiting;
        public int SprWaiting;

        // 分赛制人数 / 激战房数, 供排位中心细分面板展示
        public int StdPlayers;
        public int AscPlayers;
        public int SprPlayers;
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
