using System.Collections;
using Peak.Network;
using Photon.Pun;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace PeakCSR
{
    // 对局收尾统一出口
    //
    // 回主菜单的正确姿势来自游戏本体的被踢流程 (KickedState.Enter):
    //   Player.LeaveCurrentGame() -> SceneManager.LoadScene("Title")
    // 不能用 EndScreen.ReturnToAirport: 它只加载 Airport 场景,
    // 而 Airport 是联机枢纽, 玩家会停在那里而不是主菜单。
    public sealed class MatchTeardownService : MonoBehaviour
    {
        private static MatchTeardownService _instance;

        // 已发起返回主菜单, 防止同帧内多条路径重复触发场景切换
        public static bool AutoReturnRequested { get; private set; }

        // 返回主菜单后要自动开始匹配 (战报"再来一局"按钮用)
        private static bool _queueAfterReturn;
        private static bool _queueRoutineRunning;

        // 每次场景切换后重置标志: 否则第二局结束后就再也不会自动收尾了
        private static string _lastSceneName = "";

        public static void Initialize()
        {
            if (_instance != null) return;
            GameObject go = new GameObject("PeakCSR_MatchTeardown");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<MatchTeardownService>();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }
            _instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            string name = scene.name ?? "";
            if (!string.IsNullOrEmpty(_lastSceneName) && name != _lastSceneName)
            {
                // 场景真的换了: 上一次的收尾标志作废
                AutoReturnRequested = false;
            }
            _lastSceneName = name;

            // 记录"本局真的进过一次地图": 只有进过地图之后, 离开地图才是硬掉线证据
            if (IsMatchSceneName(name))
            {
                try
                {
                    MatchCoordinator mc = MatchCoordinator.Instance;
                    MatchSession s = mc != null ? mc.CurrentSession : null;
                    if (s != null && !s.EnteredMatchSceneOnce)
                    {
                        s.EnteredMatchSceneOnce = true;
                        s.EnteredMatchSceneAt = Time.realtimeSinceStartup;
                        PcsrLog.LogInfo("[PeakCSR] 已进入对局地图场景 " + name + "。");
                    }
                }
                catch (System.Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 记录地图场景进入失败: " + ex.Message);
                }
            }

            // 对局中被切出地图场景 = 这一局已经救不回来了, 立刻作废
            // 战报在 UI 宿主还没销毁时正常弹出，正常完赛回主菜单不受影响
            if (!IsMatchSceneName(name))
            {
                try
                {
                    MatchCoordinator mc = MatchCoordinator.Instance;
                    if (mc != null) mc.OnLeftMatchScene(name);
                }
                catch (System.Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 离场作废检查失败: " + ex.Message);
                }
            }

            // 到达主菜单后才允许开始新匹配: RaceService 需要在主菜单场景里才可用
            if (_queueAfterReturn && IsMainMenuScene(name))
            {
                _queueAfterReturn = false;
                MatchTeardownService host = _instance;
                if (host != null && !_queueRoutineRunning)
                {
                    host.StartCoroutine(host.QueueAfterMenuReady());
                }
            }
        }

        private IEnumerator QueueAfterMenuReady()
        {
            _queueRoutineRunning = true;
            // 主菜单 UI 与 RANKED 服务初始化都要等一帧以上, 太急会读到 ServiceAvailable == false
            yield return null;
            yield return null;
            yield return new WaitForSecondsRealtime(0.35f);

            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc != null && !mc.Gateway.InRace)
            {
                PcsrLog.LogInfo("[PeakCSR] 已回到主菜单，沿用上一把配置自动开始匹配。");
                mc.Queue1v1();
            }
            _queueRoutineRunning = false;
        }

        // 只认 Level_* 为对局地图 (与 RANKED 的 LiveChat 可见性判定同源)
        public static bool IsMatchSceneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name.StartsWith("Level_", System.StringComparison.Ordinal);
        }

        public static bool IsMainMenuScene(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return name == "Title" || name == "MainMenu";
        }

        // 当前是否仍停留在对局地图场景
        //
        // 用途: 判断"玩家是否已经彻底离开这一局"。
        // 掉线/被踢/读图失败都会把人送回 Title, 一旦离开就不可能再回来:
        //   1) 开赛后服务端永久拒绝 join (Race already started)
        //   2) RaceProgress / RaceTimer / maxProgressPointReached 全是本地静态状态
        //   3) Photon 房间已离开, ClientTimeout 后无法重入原房间
        // 所以"不在对局场景"是比连接探测更硬、更早、更可靠的作废信号。
        public static bool IsInMatchScene()
        {
            try
            {
                return IsMatchSceneName(SceneManager.GetActiveScene().name);
            }
            catch
            {
                return false;
            }
        }

        // 真正的"回到主菜单"实现
        // queueNextMatch: true 时到达主菜单后自动开始匹配 (沿用偏好配置)
        public static void ReturnToMainMenu(bool queueNextMatch)
        {
            AutoReturnRequested = true;
            _queueAfterReturn = queueNextMatch;

            // 断线时原版载图协程会永久挂起, LoadingScreen 因为 DontDestroyOnLoad
            // 会跨场景盖在主菜单上。切场景前先把它清掉。
            PhotonResilience.ForceKillLoadingScreen();

            try
            {
                MatchCoordinator mc = MatchCoordinator.Instance;
                if (mc != null)
                {
                    // 先退排位房: 否则 Photon 还挂在旧房间里, 场景切换会被 NetworkConnector 拦回。
                    // LeaveMatch 内部已对 "过渡动作在飞" 做了串行化处理。
                    mc.LeaveMatch();
                }
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 收尾时退出排位房失败: " + ex.Message);
            }

            // 离开 Photon 房间, 但保留与 Master Server 的连接
            try
            {
                ConnectionService conn = GameHandler.GetService<ConnectionService>();
                if (conn != null) conn.StateMachine.SwitchState<DefaultConnectionState>();

                if (PhotonNetwork.InRoom)
                {
                    PhotonNetwork.LeaveRoom();
                }
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 离开 Photon 房间失败，回退到完整断开: " + ex.Message);
                try { Player.LeaveCurrentGame(); } catch { }
            }

            // 已经离开对局时 EnterPlayMode 会残留 SceneSwitchingStatus, 清掉避免 Title 卡加载
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
                PcsrLog.LogInfo("[PeakCSR] 返回主菜单 (Title)" + (queueNextMatch ? "，到达后自动继续匹配。" : "。"));
                Dropper();
                SceneManager.LoadScene("Title");
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 加载主菜单失败: " + ex.Message);
                try
                {
                    EndScreen screen = Object.FindAnyObjectByType<EndScreen>();
                    if (screen != null) screen.ReturnToAirport();
                }
                catch
                {
                }
            }

            // 回到主菜单后强制拉一次云端数据
            try
            {
                LadderSyncService ladder = LadderSyncService.Instance;
                if (ladder != null && ladder.IsCloudConfigured())
                {
                    // 本局已经把最常玩难度/模式的统计推进了一格, 先把它推上去再拉
                    ladder.PushDerivedPreferences();
                    ladder.RefreshLeaderboard(true);
                    ladder.RefreshHistory(true);
                    ladder.SyncMyCloudProfile();
                }
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 返回主菜单后刷新云端数据失败: " + ex.Message);
            }
        }

        // 场景切换前把 UI 状态收干净, 否则 Title 上会残留面板
        //
        // 注意: 战报窗口故意不关
        private static void Dropper()
        {
            try
            {
                PcsrMenuWindow window = PcsrMenuWindow.Instance;
                if (window != null) window.Close();
            }
            catch
            {
            }
        }

        // 协程宿主还没创建时 (极早期) 也要能落地: 用静态入口兜一层
        public static void EnsureInitialized()
        {
            if (_instance == null) Initialize();
        }
    }
}
