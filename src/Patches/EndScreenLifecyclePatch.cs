using System;
using HarmonyLib;

namespace PeakCSR
{
    // 修复先完赛玩家在原版 EndScreen 点击 Next 后永久卡在"等待所有玩家……"无继续按钮的问题
    // 注意: 必须严格限定为 PCSR 对局, 否则会误伤普通 RANKED 8 人 LiveRace (那本来就需要等齐所有人)
    [HarmonyPatch(typeof(EndScreen), "Next")]
    public static class EndScreenLifecyclePatch
    {
        public static bool Prefix(EndScreen __instance)
        {
            try
            {
                // 静态闸门 (护栏 2): 只在 PCSR 对局内生效
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return true;

                if (__instance != null && __instance.WaitingForPlayersUI != null)
                {
                    __instance.WaitingForPlayersUI.gameObject.SetActive(false);
                }
                if (__instance != null && __instance.buttons != null)
                {
                    __instance.buttons.SetActive(true);
                }
                if (__instance != null)
                {
                    EventBus.PublishEndScreenClosed();
                    MatchTeardownService.EnsureInitialized();

                    // 关键: 本局若还没给出结论, 必须先结算再走人
                    MatchCoordinator mc = MatchCoordinator.Instance;
                    bool settledNow = mc != null && mc.TrySettlePendingBeforeExit();

                    // 刚结算完并成功弹出战报: 留在结算场景让玩家先看结果,
                    // 回主菜单由战报上的按钮负责
                    if (settledNow && PostMatchReportView.Instance != null
                        && PostMatchReportView.Instance.IsOpen)
                    {
                        return false;
                    }

                    // PCSR 对局统一回主菜单, 不回机场
                    MatchTeardownService.ReturnToMainMenu(false);
                    return false;
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] EndScreen.Next 防卡死补丁异常: " + ex.Message);
            }
            return true;
        }
    }
}
