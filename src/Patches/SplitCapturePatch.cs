using System;
using HarmonyLib;
using RankedPeak.Services;
using RankedPeak.Timer;

namespace PeakCSR
{
    // 直接监听本机点灯事件, 作为最可靠的分段来源
    [HarmonyPatch(typeof(MountainProgressHandler), "SetSegmentComplete")]
    public static class SplitCapturePatch
    {
        public static void Postfix(int segment)
        {
            try
            {
                // 静态闸门 (护栏 2): 只在 PCSR 对局内生效
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;

                MatchCoordinator coordinator = MatchCoordinator.Instance;
                MatchSession session = coordinator != null ? coordinator.CurrentSession : null;
                if (session == null) return;

                long ms = RaceTimer.Instance != null ? RaceTimer.Instance.CurrentElapsedMs : 0;
                if (ms <= 0) return;

                string steamId = coordinator.Gateway.MySteamId;
                // 只在第一次写入, 绝不覆盖、绝不填充 (SegmentTracker 语义)
                session.Splits.Observe(steamId, segment, ms);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 分段打卡采集异常: " + ex.Message);
            }
        }
    }
}
