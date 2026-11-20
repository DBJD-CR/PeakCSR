using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RankedPeak.Live;
using RankedPeak.Services;
using RankedPeak.Ui;
using RankedPeak.Ui.Native;
using UnityEngine;

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

    // 2. 拦截 LiveChatOverlay.OnEntry，静默吞掉 PeakCSR 内部握手协议包，并提供向左下角聊天框注入高亮系统提示的能力
    [HarmonyPatch(typeof(LiveChatOverlay), "OnEntry")]
    public static class SilentHandshakeChatPatch
    {
        private static MethodInfo _onEntryMethod;

        public static bool Prefix(ChatEntry entry)
        {
            if (entry != null && !string.IsNullOrEmpty(entry.Body) && entry.Body.StartsWith(MatchController.HandshakePrefix, StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }

        public static void PushSystemChatMessage(string richTextBody)
        {
            if (string.IsNullOrEmpty(richTextBody)) return;
            try
            {
                LiveChatOverlay overlay = UnityEngine.Object.FindAnyObjectByType<LiveChatOverlay>();
                if (overlay == null) return;
                if (_onEntryMethod == null)
                {
                    _onEntryMethod = typeof(LiveChatOverlay).GetMethod("OnEntry", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (_onEntryMethod != null)
                {
                    ChatEntry sysEntry = new ChatEntry
                    {
                        Kind = ChatEntryKind.System,
                        Body = richTextBody,
                        ReceivedAt = Time.realtimeSinceStartup,
                        Seen = false
                    };
                    _onEntryMethod.Invoke(overlay, new object[] { sysEntry });
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 推送聊天框系统消息失败: " + ex.Message);
            }
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

    // 4. 修复先完赛玩家在原版 EndScreen 点击 Next 后永久卡在“等待所有玩家……”无继续按钮的问题
    // 注意: 必须严格限定为 PCSR 对局, 否则会误伤普通 RANKED 8 人 LiveRace (那本来就需要等齐所有人)
    [HarmonyPatch(typeof(EndScreen), "Next")]
    public static class EndScreenNextUnstickPatch
    {
        public static bool Prefix(EndScreen __instance)
        {
            try
            {
                if (!MatchController.IsCurrentPeakCsrMatch()) return true;

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
                    __instance.ReturnToAirport();
                    return false;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] EndScreen.Next 防卡死补丁异常: " + ex.Message);
            }
            return true;
        }
    }
}