namespace PeakCSR
{
    // 单赛制战绩值对象 (ModeRecord[3] 按下标 0/1/2 访问, 消灭 WinsAllPeak 等 9 个平铺字段)
    public struct ModeRecord
    {
        public int Wins;
        public int Losses;
        // 累计场次 (胜 + 负 + 平)
        public int Games;

        public ModeRecord(int wins, int losses, int games)
        {
            Wins = wins;
            Losses = losses;
            Games = games;
        }
    }
}
