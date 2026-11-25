using System.Collections.Generic;
using HarmonyLib;
using RankedPeak.Services;
using RankedPeak.Ui.Native;

namespace PeakCSR
{
    // 在原版大厅浏览器中为包含 PeakCSR 魔数的房间自动打上 [PCSR 1v1] 专属标识
    [HarmonyPatch(typeof(RaceLobbyBrowser), "SetEntries")]
    public static class LobbyBrowserTagPatch
    {
        public static void Prefix(List<RaceLobbyEntry> entries)
        {
            if (entries == null) return;
            for (int i = 0; i < entries.Count; i++)
            {
                RaceLobbyEntry entry = entries[i];
                if (entry != null && RoomMagicCodec.IsCsrMagicMs(entry.AutoEndMs))
                {
                    if (!string.IsNullOrEmpty(entry.HostName) && !entry.HostName.StartsWith("[PCSR 1v1] ", System.StringComparison.Ordinal))
                    {
                        entry.HostName = "[PCSR 1v1] " + entry.HostName;
                    }
                }
            }
        }
    }
}
