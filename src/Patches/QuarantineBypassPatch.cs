using HarmonyLib;

namespace PeakCSR
{
    // 将自身 GUID 加入 ModAllowlistService 白名单判定，防止在 LiveRace 期间被 ModQuarantine 卸载
    [HarmonyPatch(typeof(RankedPeak.Services.ModAllowlistService), "IsAllowed")]
    public static class QuarantineBypassPatch
    {
        public static void Postfix(string id, ref bool __result)
        {
            if (id == Plugin.PluginGuid)
            {
                __result = true;
            }
        }
    }
}
