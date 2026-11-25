using System;

namespace PeakCSR.Practice
{
    // 离线/在线切换期间的断线弹窗压制
    //
    // 游戏本体的 MainMenuPageHandler.Update 在
    //   ClientState == Disconnected && !OfflineMode && !ConnectingToNetwork
    // 且没有 IsDisconnectingForOfflineMode 状态时会弹出
    // "未能连接至 Photon 网络 / 重试 / 离线游玩" 对话框。
    //
    // 原版 MainMenu.DisconnectForOfflineMode 正是靠登记这个状态来屏蔽它的。
    // 练习进出离线模式的两个窗口期必须同样登记:
    //   1) 练习启动: Disconnect() 之后到 OfflineMode = true 之前
    //   2) 练习结束: OfflineMode = false 之后到 ConnectToNetwork 真正连上之前
    // 不登记就会出现"自己已经在重连了但还弹框"的干扰。
    internal static class PracticePhotonGuard
    {
        // 登记压制状态。返回 true 表示由本次调用真正添加, 需要配对释放。
        public static bool SuppressDisconnectModal()
        {
            try
            {
                if (!GameHandler.TryGetStatus<IsDisconnectingForOfflineMode>(out IsDisconnectingForOfflineMode _))
                {
                    GameHandler.AddStatus<IsDisconnectingForOfflineMode>(new IsDisconnectingForOfflineMode());
                }
                return true;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 登记离线切换状态失败: " + ex.Message);
                return false;
            }
        }

        // 释放压制状态。added 为 false 时什么都不做, 避免误清别人登记的标记。
        public static void ReleaseDisconnectModal(bool added)
        {
            if (!added) return;
            try { GameHandler.ClearStatus<IsDisconnectingForOfflineMode>(); }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 清除离线切换状态失败: " + ex.Message);
            }
        }
    }
}
