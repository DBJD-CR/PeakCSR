using RankedPeak.Models;
using RankedPeak.Services;

namespace PeakCSR.Practice
{
    // 把练习局登记进 RANKED 的 RunGate
    //
    // 为什么要动 RunGate:
    //   RunGate.TracksCurrentRun 决定 RaceTimer 是否采集计时与分段。
    //   练习局必须返回 true, 否则游戏内根本看不到用时与分段 (需求 1)。
    //   但 Mode 必须选一个不会被当成正式成绩的值, GhostTraining 天然满足
    //   (RANKED 自己在 RunUploadService 里就把 GhostTraining 排除在排行榜外)。
    //
    // 注意: Select() 会顺带 ModQuarantine.Engage(), 这是期望行为, 练习期间
    // 同样不应该让其它模组改写地图内容, 否则练出来的数据没有参考价值。
    public static class PracticeRunGate
    {
        public static bool Arm(int levelIndex)
        {
            try
            {
                // levelIndex < 0 时不做地图真实性校验 (离线场景可能还没确定地图)
                bool ok = levelIndex >= 0
                    ? RankedRunGate.Select(GameMode.GhostTraining, levelIndex)
                    : RankedRunGate.Select(GameMode.GhostTraining, MapCatalog.CurrentLevelIndex);
                if (!ok)
                {
                    PcsrLog.LogWarning("[PeakCSR] RunGate 拒绝了练习局登记: " + RankedRunGate.LastRefusal);
                }
                return ok;
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] RunGate 登记异常: " + ex.Message);
                return false;
            }
        }

        public static void Disarm()
        {
            try
            {
                RankedRunGate.Disarm();
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] RunGate 撤销异常: " + ex.Message);
            }
        }
    }
}
