using Photon.Pun;
using UnityEngine;

namespace PeakCSR.Practice
{
    // 练习模式门面 (组合根 + MonoBehaviour 驱动)
    //
    // 职责边界:
    //   1) 装配各端口实现 (启动器/探针/导航/通知) 并注入编排器
    //   2) 每帧驱动编排器 Tick, 并负责练习结束后把 Photon 拉回在线
    //   3) 向 UI 暴露只读视图
    // 业务规则全部在 PracticeOrchestrator 里, 这里不做对局状态判断
    public sealed class PracticeCoordinator : MonoBehaviour
    {
        private static PracticeCoordinator _instance;
        public static PracticeCoordinator Instance { get { return _instance; } }

        private PracticeOrchestrator _orchestrator;
        private IPracticeNavigator _navigator;

        // 离线模式是否由练习模式自己开启
        // 只有我们自己开的才负责关: 玩家主动选"离线游玩"时不能被打扰
        private bool _offlineEngagedByPractice;

        private bool _restoreRunning;
        private float _nextRestoreAttempt;

        // ===== UI 只读视图 =====
        public bool IsActive
        {
            get { return _orchestrator != null && _orchestrator.IsActive; }
        }

        public PracticePhase Phase
        {
            get { return _orchestrator != null ? _orchestrator.Phase : PracticePhase.Inactive; }
        }

        public string StatusMessage
        {
            get { return _orchestrator != null ? _orchestrator.StatusMessage : ""; }
        }

        public string LastError
        {
            get { return _orchestrator != null ? _orchestrator.LastError : ""; }
        }

        public float QueueElapsedSeconds
        {
            get { return _orchestrator != null ? _orchestrator.QueueElapsedSeconds : 0f; }
        }

        // 命中候选房的房主名 (HUD 展示用)
        public string FoundHostName
        {
            get { return _orchestrator != null ? _orchestrator.FoundHostName : ""; }
        }

        public PracticeRuleset Ruleset
        {
            get { return _orchestrator != null ? _orchestrator.Ruleset : new PracticeRuleset(); }
        }

        // 游戏内抬头可见的用时文本; 非练习运行期返回空串
        public string InGameClockText
        {
            get { return _orchestrator != null ? _orchestrator.InGameClockText : ""; }
        }

        // 当前探针的实时进度文案 (搜索/校验/回报/失败重试都在里面)
        // HUD 的状态行以它为主, 为空时才退回按阶段取的固定文案
        public string LiveProgressText
        {
            get { return _orchestrator != null ? _orchestrator.StatusMessage : ""; }
        }

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            // 兜底: 无论走到哪一步, 销毁时必须把全局标记清干净
            try
            {
                PracticeRunFlag.Release();
                PracticeRunGate.Disarm();
                PracticeHandoffGuard.EndSwitch();
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 销毁练习协调器时清理标记失败: " + ex.Message);
            }
            _restoreRunning = false;
            _offlineEngagedByPractice = false;
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        // 由 Plugin 装配: gateway 用于大厅探针与系统消息
        public void Initialize(IRankedPeakGateway gateway, MatchCoordinator matchCoordinator)
        {
            if (_orchestrator != null) return;

            IPracticeRoomLauncher launcher = new PracticeOfflineLauncher(this);
            IPracticeLobbyProbe probe = new PracticeLobbyProbe(gateway);
            IPracticeNotifier notifier = new PracticeNotifier(gateway);
            _navigator = new PracticeNavigator(delegate
            {
                // 回主菜单前把练习 UI 状态收干净
                if (_orchestrator != null) _orchestrator.Tick();
            });

            _orchestrator = new PracticeOrchestrator(launcher, probe, _navigator, notifier);
            // 练习自然结束 (登顶/阵亡/启动失败) 也要把在线连接补回来
            _orchestrator.BindEnded(BeginRestoreOnline);
            // 交棒成功时在线已由交棒流程恢复, 交还恢复责任
            _orchestrator.BindHandoffSucceeded(delegate
            {
                _offlineEngagedByPractice = false;
                _restoreRunning = false;
            });

            // 交棒入口: 交给排位协调器复用成熟的 join 链路
            // 结果回调必须原样透传, 编排器据此决定收尾还是回退继续练习
            if (matchCoordinator != null)
            {
                _orchestrator.BindHandoff(delegate (string inviteCode, System.Action<bool, string> onResult)
                {
                    matchCoordinator.BeginMatchFromPractice(inviteCode, onResult);
                });
            }
        }

        // ===== UI 命令 =====

        public bool StartPractice(PcsrRuleFormat format, int ascent)
        {
            if (_orchestrator == null) return false;

            // 启动前先记下"进来时是不是在线":
            // 练习必然把 Photon 切到离线, 结束后要按这里的事实决定是否恢复
            // 用 IsConnected 而不是 OfflineMode 判断, 因为离线模式下 IsConnected 也可能为 true
            bool wasOnline = false;
            try
            {
                wasOnline = !PhotonNetwork.OfflineMode && PhotonNetwork.IsConnected;
            }
            catch
            {
                wasOnline = true;   // 读不到状态时按"在线"处理, 宁可多恢复一次
            }

            bool started = _orchestrator.StartPractice(format, ascent);
            if (started)
            {
                // 只有真的开始练习, 才由我们负责事后恢复在线
                _offlineEngagedByPractice = wasOnline;
            }
            return started;
        }


        // 主动结束练习。恢复在线由编排器的 RaiseEnded 回调统一触发
        public void StopPractice()
        {
            if (_orchestrator == null) return;
            _orchestrator.StopPractice();
        }

        // ===== 离线模式恢复 =====
        private void BeginRestoreOnline()
        {
            if (!_offlineEngagedByPractice) return;
            if (_restoreRunning) return;
            if (_navigator == null) return;

            _restoreRunning = true;
            _nextRestoreAttempt = 0f;
            PcsrLog.LogInfo("[PeakCSR] 练习结束, 准备恢复在线连接。");
            // 现在多半还在 Level_* 场景里, 先把人送回主菜单再重连
            EnsureBackToMenuForRestore();
        }

        // 练习自然结束 (登顶/阵亡) 时玩家仍留在地图, 必须主动回城
        private void EnsureBackToMenuForRestore()
        {
            if (_navigator == null) return;
            if (_navigator.IsInMainMenu()) return;

            PcsrLog.LogInfo("[PeakCSR] 练习已结束, 返回主菜单以恢复在线连接。");
            try { _navigator.ReturnToMainMenu(); }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 返回主菜单失败: " + ex.Message);
            }
        }

        private void TickRestoreOnline()
        {
            if (!_restoreRunning) return;
            if (_navigator == null)
            {
                _restoreRunning = false;
                return;
            }
            if (Time.realtimeSinceStartup < _nextRestoreAttempt) return;
            _nextRestoreAttempt = Time.realtimeSinceStartup + 2f;

            // 必须已经回到主菜单: 在地图里重连会被 NetworkConnector 拉进旧流程
            if (!_navigator.IsInMainMenu()) return;

            _restoreRunning = false;
            _offlineEngagedByPractice = false;
            PcsrLog.LogInfo("[PeakCSR] 已回到主菜单, 开始恢复在线连接。");

            _navigator.ReconnectOnline(
                delegate
                {
                    PcsrLog.LogInfo("[PeakCSR] 在线连接已恢复, 可以正常匹配。");
                    EventBus.PublishMatchStateChanged();
                },
                delegate (string err)
                {
                    PcsrLog.LogWarning("[PeakCSR] 恢复在线连接失败 (可重进主菜单重试): " + err);
                    EventBus.PublishMatchStateChanged();
                });
        }

        private void Update()
        {
            if (_orchestrator != null) _orchestrator.Tick();
            TickRestoreOnline();
        }
    }

    // 通知端口实现: 复用 RANKED 网关的左下角系统消息与状态刷新
    internal sealed class PracticeNotifier : IPracticeNotifier
    {
        private readonly IRankedPeakGateway _gw;

        public PracticeNotifier(IRankedPeakGateway gateway)
        {
            _gw = gateway;
        }

        public void PushChat(string richTextBody)
        {
            if (_gw == null) return;
            try { _gw.PushSystemChat(richTextBody); }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 推送系统消息失败: " + ex.Message);
            }
        }

        public void NotifyStateChanged()
        {
            EventBus.PublishMatchStateChanged();
        }
    }
}
