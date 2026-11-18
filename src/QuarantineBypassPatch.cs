using System;
using System.Collections.Generic;
using HarmonyLib;
using RankedPeak.Live;
using RankedPeak.Services;
using RankedPeak.Ui;
using RankedPeak.Ui.Native;

namespace PeakCSR
{
    // 1. 将自身 GUID 加入 ModAllowlistService 白名单判定，防止在 LiveRace 期间被 ModQuarantine 卸载
    [HarmonyPatch(typeof(ModAllowlistService), "IsAllowed")]
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

    // 2. 拦截 LiveChatOverlay.OnEntry，静默吞掉 PeakCSR 内部握手协议包，避免污染玩家聊天框
    [HarmonyPatch(typeof(LiveChatOverlay), "OnEntry")]
    public static class SilentHandshakeChatPatch
    {
        public static bool Prefix(ChatEntry entry)
        {
            if (entry != null && !string.IsNullOrEmpty(entry.Body) && entry.Body.StartsWith(MatchController.HandshakePrefix, StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }
    }

    // 3. 在原版大厅浏览器中为包含 PeakCSR 魔数的房间自动打上 [PCSR 1v1] 专属标识
    [HarmonyPatch(typeof(RaceLobbyBrowser), "SetEntries")]
    public static class LobbyBrowserTagPatch
    {
        public static void Prefix(List<RaceLobbyEntry> entries)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                RaceLobbyEntry entry = entries[i];
                if (entry != null && MatchController.IsPeakCsrMagicMs(entry.AutoEndMs))
                {
                    if (!string.IsNullOrEmpty(entry.HostName) && !entry.HostName.StartsWith("[PCSR 1v1] ", StringComparison.Ordinal))
                    {
                        entry.HostName = "[PCSR 1v1] " + entry.HostName;
                    }
                }
            }
        }
    }
}