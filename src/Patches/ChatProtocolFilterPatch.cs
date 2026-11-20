using System;
using HarmonyLib;
using RankedPeak.Live;
using RankedPeak.Ui;

namespace PeakCSR
{
    // 拦截 LiveChatOverlay.OnEntry，静默吞掉 PeakCSR 内部握手协议包
    // (原 SilentHandshakeChatPatch 的拦截职责; 系统消息推送已迁移至 RankedPeakGateway.PushSystemChat)
    [HarmonyPatch(typeof(LiveChatOverlay), "OnEntry")]
    public static class ChatProtocolFilterPatch
    {
        public static bool Prefix(ChatEntry entry)
        {
            if (entry != null && !string.IsNullOrEmpty(entry.Body)
                && entry.Body.StartsWith(PcsrProtocol.PacketPrefix, StringComparison.Ordinal))
            {
                return false;
            }
            return true;
        }
    }
}
