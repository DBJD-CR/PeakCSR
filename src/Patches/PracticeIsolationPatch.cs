using HarmonyLib;
using RankedPeak.Models;
using RankedPeak.Services;

namespace PeakCSR.Practice
{
    // 练习局隔离护栏
    //
    // 练习也要有计时和分段 (需求 1), 但绝不能污染排位数据:
    //   1) RunStorageService.AddRun 会更新 PB 并写入历史, 练习局必须跳过
    //   2) RaceService.ReportFinish 会把成绩发给服务端, 练习局也要拦
    //   3) RaceService.LeaveWhenBackInMenu 在切换期会把刚 join 的房间退掉, 必须闸住
    //
    // 用前缀拦截而非后置修正, 因为这几处一旦执行就有不可逆的写盘/网络副作用。

    // 练习局不写本地 PB 与历史
    [HarmonyPatch(typeof(RunStorageService), "AddRun")]
    public static class PracticeSkipStoragePatch
    {
        public static bool Prefix(RunRecord record, ref long deltaToPreviousPbMs)
        {
            if (!PracticeRunFlag.IsActive) return true;
            deltaToPreviousPbMs = 0L;
            PcsrLog.LogInfo("[PeakCSR] 练习局成绩不写入本地 PB / 历史 (用时 "
                + (record != null ? record.TotalMs.ToString() : "?") + "ms)。");
            return false;
        }
    }

    // 练习局不上报云端
    [HarmonyPatch(typeof(RaceService), "ReportFinish")]
    public static class PracticeSkipReportPatch
    {
        public static bool Prefix()
        {
            if (!PracticeRunFlag.IsActive) return true;
            PcsrLog.LogInfo("[PeakCSR] 练习局成绩不上报云端。");
            return false;
        }
    }

    // 练习切换期不要把刚 join 到的排位房当成"离开对局世界"而退掉
    [HarmonyPatch(typeof(RaceService), "LeaveWhenBackInMenu")]
    public static class PracticeKeepJoinedRacePatch
    {
        public static bool Prefix()
        {
            return !PracticeHandoffGuard.IsSwitchingToRanked;
        }
    }

    // 练习交棒窗口标记: 从"匹配成功开始切换"到"排位 join 完成"之间为 true
    public static class PracticeHandoffGuard
    {
        private static bool _switching;

        public static bool IsSwitchingToRanked
        {
            get { return _switching; }
        }

        public static void BeginSwitch()
        {
            _switching = true;
        }

        public static void EndSwitch()
        {
            _switching = false;
        }
    }
}
