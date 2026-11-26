using System;
using Peak.Network;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakCSR.Practice
{
    // 练习态导航: 回主菜单 + 恢复在线 Photon
    //
    // 刻意不复用 MatchTeardownService.ReturnToMainMenu:
    // 那个实现会调 MatchCoordinator.LeaveMatch(), 而练习态根本没有 MatchSession,
    // 调用它只会污染排位状态机的收尾逻辑。
    public sealed class PracticeNavigator : IPracticeNavigator
    {
        private readonly Action _onBeforeReturn;

        // 回城到重连之间整段压着断线弹窗
        private bool _modalSuppressed;

        public PracticeNavigator(Action onBeforeReturn)
        {
            _onBeforeReturn = onBeforeReturn;
        }

        public bool IsInMainMenu()
        {
            try
            {
                string name = SceneManager.GetActiveScene().name;
                return name == "Title" || name == "MainMenu";
            }
            catch
            {
                return false;
            }
        }

        public void ReturnToMainMenu()
        {
            PcsrLog.LogInfo("[PeakCSR] 返回主菜单 (练习模式)。");
            // 从这里开始压住断线弹窗, 覆盖 回城 -> Title -> 重连 整段窗口
            if (!_modalSuppressed)
            {
                _modalSuppressed = PracticePhotonGuard.SuppressDisconnectModal();
            }
            try
            {
                // 场景切换前先清掉可能卡住的载图遮罩 (离线切在线时容易残留)
                PhotonResilience.ForceKillLoadingScreen();
            }
            catch
            {
            }

            if (_onBeforeReturn != null)
            {
                try { _onBeforeReturn(); }
                catch (Exception ex) { PcsrLog.LogWarning("[PeakCSR] 收尾回调异常: " + ex.Message); }
            }

            // 离线 dummy 房要先离开
            try
            {
                if (PhotonNetwork.InRoom) PhotonNetwork.LeaveRoom();
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 离开离线房失败 (可忽略): " + ex.Message);
            }

            // 清掉练习局的地图标记
            try { RankedPeak.Services.RankedGameLauncher.ClearForcedLevel(); }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 清理强制地图失败 (可忽略): " + ex.Message);
            }

            // 已经离开对局时可能残留 SceneSwitchingStatus, 不清会让 Title 卡载图
            try
            {
                if (GameHandler.TryGetStatus<SceneSwitchingStatus>(out SceneSwitchingStatus _))
                {
                    GameHandler.ClearStatus<SceneSwitchingStatus>();
                }
            }
            catch
            {
            }

            try
            {
                SceneManager.LoadScene("Title");
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 加载主菜单失败: " + ex.Message);
            }
        }

        public void ReconnectOnline(Action onReady, Action<string> onFailed)
        {
            // 必须在主菜单场景里重连: 场馆场景里重连会被 NetworkConnector 拉进旧流程
            if (!IsInMainMenu())
            {
                if (onFailed != null) onFailed("尚未回到主菜单");
                ReleaseModalSuppression();
                return;
            }

            PcsrLog.LogInfo("[PeakCSR] 正在从离线模式恢复在线连接...");
            // ReturnToMainMenu 已压制过就复用, 没走过回城流程则在这里补上
            if (!_modalSuppressed)
            {
                _modalSuppressed = PracticePhotonGuard.SuppressDisconnectModal();
            }
            try
            {
                PhotonNetwork.OfflineMode = false;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 关闭离线模式失败: " + ex.Message);
            }

            // 离线模式下 IsConnected 仍可能为 true, ConnectToNetwork 会提前 return,
            // 所以先等它真的断开, 再发起重连。
            ReconnectWaiter.Spawn(this, onReady, onFailed);
        }

        // 重连结束 (成功或失败) 由 ReconnectWaiter 回调, 统一放开压制。
        // 失败时也要放开: 让玩家能在原版弹窗里手动重试, 这是最后一道出口。
        internal void ReleaseModalSuppression()
        {
            if (!_modalSuppressed) return;
            PracticePhotonGuard.ReleaseDisconnectModal(true);
            _modalSuppressed = false;
        }

        // 用一个轻量 MonoBehaviour 承载等待断开与重连的分帧流程
        private sealed class ReconnectWaiter : MonoBehaviour
        {
            private const float DisconnectWaitSeconds = 8f;
            private const float ConnectWaitSeconds = 20f;

            private Action _onReady;
            private Action<string> _onFailed;
            private float _deadline;
            private int _stage;
            // 压制状态由 PracticeNavigator 统一持有, 这里只负责在收尾时通知它放开
            private PracticeNavigator _owner;

            // 由静态入口创建: 保证组件实例上带着回调, 而不是在外部 new 出来的空壳上
            public static void Spawn(PracticeNavigator owner, Action onReady, Action<string> onFailed)
            {
                GameObject go = new GameObject("PeakCSR_PracticeReconnect");
                DontDestroyOnLoad(go);
                ReconnectWaiter w = go.AddComponent<ReconnectWaiter>();
                w._owner = owner;
                w._onReady = onReady;
                w._onFailed = onFailed;
                w._deadline = Time.realtimeSinceStartup + DisconnectWaitSeconds;
            }

            private void OnDestroy()
            {
                // 兜底: 组件被外部销毁时也要放开压制, 否则后续真断线将永远不提示
                ReleaseSuppression();
            }

            private void ReleaseSuppression()
            {
                if (_owner != null) _owner.ReleaseModalSuppression();
            }

            private void Update()
            {
                if (_stage == 0)
                {
                    // 等 Photon 真正断开 (OfflineMode 切 false 后需要一拍才生效)
                    if (!PhotonNetwork.IsConnected || Time.realtimeSinceStartup > _deadline)
                    {
                        _stage = 1;
                        _deadline = Time.realtimeSinceStartup + ConnectWaitSeconds;
                        bool requested = false;
                        try
                        {
                            requested = NetworkingUtilities.ConnectToNetwork(OnConnectResult, true);
                        }
                        catch (Exception ex)
                        {
                            Fail("重连请求异常: " + ex.Message);
                            return;
                        }
                        if (!requested && !PhotonNetwork.IsConnected)
                        {
                            Fail("重连请求被拒绝");
                        }
                    }
                    return;
                }

                // 兜底: 重连成功但回调丢了, 到点后自己检查一次
                if (PhotonNetwork.IsConnected)
                {
                    Succeed();
                }
                else if (Time.realtimeSinceStartup > _deadline)
                {
                    Fail("重连超时");
                }
            }

            private void OnConnectResult(bool success)
            {
                if (success) Succeed();
                else Fail("网络连接失败");
            }

            private void Succeed()
            {
                if (_stage < 0) return;
                _stage = -1;
                PcsrLog.LogInfo("[PeakCSR] 在线连接已恢复。");
                ReleaseSuppression();
                Action ready = _onReady;
                _onReady = null;
                _onFailed = null;
                try { if (ready != null) ready(); }
                catch (Exception ex) { PcsrLog.LogWarning("[PeakCSR] 重连成功回调异常: " + ex.Message); }
                Destroy(gameObject);
            }

            private void Fail(string reason)
            {
                if (_stage < 0) return;
                _stage = -1;
                PcsrLog.LogWarning("[PeakCSR] 恢复在线连接失败: " + reason);
                // 失败后必须放开压制: 让玩家在原版弹窗里手动重试, 这是最后一道出口
                ReleaseSuppression();
                Action<string> fail = _onFailed;
                _onReady = null;
                _onFailed = null;
                try { if (fail != null) fail(reason); }
                catch { }
                Destroy(gameObject);
            }
        }
    }
}
