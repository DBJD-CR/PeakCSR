using System;
using HarmonyLib;
using RankedPeak.Services;
using RankedPeak.Timer;

namespace PeakCSR
{
    // 分段打卡采集
    //
    // 挂在 MountainProgressHandler.CheckProgress:
    //   它是每帧 Update 都会跑的进度检查, 内部会把 maxProgressPointReached 推到当前真实值,
    //   单群系/全山都一定经过这里。我们只在这个值发生变化(前进)时记一次时间戳
    [HarmonyPatch(typeof(MountainProgressHandler), "CheckProgress")]
    public static class SplitCapturePatch
    {
        // 上一个已记录的 maxProgressPointReached (每个对局/每次进度重置都要清)
        private static int _lastRecorded = -1;

        public static void ResetTracking()
        {
            _lastRecorded = -1;
        }

        public static void Postfix()
        {
            try
            {
                // 静态闸门: 只在 PCSR 对局内生效
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;

                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null) return;

                int reached = handler.maxProgressPointReached;
                if (reached < 0) return;
                if (reached <= _lastRecorded) return;

                MatchCoordinator coordinator = MatchCoordinator.Instance;
                MatchSession session = coordinator != null ? coordinator.CurrentSession : null;
                if (session == null) return;

                RaceTimer timer = RaceTimer.Instance;
                if (timer == null) return;
                // 计时器还没真正开始跑 (苏醒动画 / 开赛前) 时不记, 也不推进游标
                if (timer.State != TimerState.Running && timer.State != TimerState.Finished) return;

                long ms = timer.CurrentElapsedMs;
                if (ms <= 0) return;

                string steamId = coordinator.Gateway.MySteamId;
                // 只记第一次到达, 绝不覆盖、绝不填充
                session.Splits.Observe(steamId, reached, ms);
                _lastRecorded = reached;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 分段打卡采集异常: " + ex.Message);
            }
        }
    }

    // 换局/重进地图时把采集游标清干净 (否则新一局会因为 reached 没涨而漏记首段)
    [HarmonyPatch(typeof(MountainProgressHandler), "InitProgressPoints")]
    public static class SplitCaptureResetPatch
    {
        public static void Postfix()
        {
            SplitCapturePatch.ResetTracking();
        }
    }
}
