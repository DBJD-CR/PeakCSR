using System;
using System.Collections;
using Photon.Pun;
using RankedPeak.Services;
using RankedPeak.Timer;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zorro.Core;

namespace PeakCSR.Practice
{
    // 离线单人局启动器
    //
    // 走的是游戏本体自己的离线游玩链路 (MainMenu.StartSoloSession 的等价实现):
    //   PhotonNetwork.Disconnect() -> OfflineMode = true -> 加载 Airport (networked: false)
    // 之后 NetworkConnector.Start() 检测到 OfflineMode 会 JoinDummyRoom() 建一个纯本地房间。
    //
    // 为什么不用排位那条 RaceService.LoadRaceLevel:
    // 那条路径最终落到 MainMenuMainPage.StartMultiplayerLobby(), 而它第一行就
    //   if (PhotonNetwork.OfflineMode) return;
    // 离线模式下根本无法开始在线对局。反过来, 离线链路也根本不需要任何服务器,
    // 因此天然规避 Photon 掉线白爬。
    public sealed class PracticeOfflineLauncher : IPracticeRoomLauncher
    {
        // 从发起离线到计时器就绪的最长等待; 超时视为启动失败
        private const float ReadyTimeoutSeconds = 120f;

        private readonly MonoBehaviour _host;
        private PracticeRuleset _ruleset;
        private Action _onLevelEntered;
        private Action<string> _onFailed;
        private bool _busy;

        // 启动代际: 每个协程只认自己启动时的代际, 代际不匹配就退出, 避免两个启动流程并存抢着载机场
        private int _generation;

        public PracticeOfflineLauncher(MonoBehaviour coroutineHost)
        {
            _host = coroutineHost;
        }

        public bool IsBusy { get { return _busy; } }

        public bool Launch(PracticeRuleset ruleset, Action onLevelEntered, Action<string> onFailed)
        {
            if (_busy) return false;
            if (_host == null)
            {
                if (onFailed != null) onFailed("缺少协程宿主");
                return false;
            }

            _ruleset = ruleset;
            _onLevelEntered = onLevelEntered;
            _onFailed = onFailed;
            _busy = true;
            int gen = ++_generation;

            try
            {
                _host.StartCoroutine(LaunchRoutine(gen));
                return true;
            }
            catch (Exception ex)
            {
                _busy = false;
                PcsrLog.LogWarning("[PeakCSR] 启动离线练习局失败: " + ex.Message);
                if (_onFailed != null) _onFailed(ex.Message);
                return false;
            }
        }

        // 取消启动/退出练习
        // 无论是否处于 busy 都要推进代际: 已就绪的旧协程可能还有收尾动作要拦
        public void Abort()
        {
            _generation++;
            if (!_busy) return;
            _busy = false;
            _onLevelEntered = null;
            _onFailed = null;
            PracticeRunFlag.Release();
            PracticeRunGate.Disarm();
        }

        private bool IsCurrent(int gen)
        {
            return gen == _generation;
        }

        private IEnumerator LaunchRoutine(int gen)
        {
            if (!IsCurrent(gen)) yield break;
            // 0. 先退出遗留排位房
            try
            {
                RaceService rs = RaceService.Instance;
                if (rs != null && rs.CurrentRace != null)
                {
                    PcsrLog.LogInfo("[PeakCSR] 练习启动前退出遗留排位房 [" + rs.CurrentRace.InviteCode + "]。");
                    rs.LeaveRace();
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 清理遗留排位房失败 (继续练习): " + ex.Message);
            }

            // 1. 断开在线 Photon, 切到离线模式
            PcsrLog.LogInfo("[PeakCSR] 正在切换到离线单人模式...");
            PhotonNetwork.IsMessageQueueRunning = true;
            bool suppressModal = PracticePhotonGuard.SuppressDisconnectModal();
            try { PhotonNetwork.Disconnect(); }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 断开 Photon 异常 (继续离线): " + ex.Message);
            }

            float disconnectDeadline = Time.realtimeSinceStartup + 10f;
            while (PhotonNetwork.IsConnected && Time.realtimeSinceStartup < disconnectDeadline)
            {
                if (!IsCurrent(gen))
                {
                    PracticePhotonGuard.ReleaseDisconnectModal(suppressModal);
                    yield break;
                }
                yield return null;
            }
            if (!IsCurrent(gen))
            {
                PracticePhotonGuard.ReleaseDisconnectModal(suppressModal);
                yield break;
            }
            PhotonNetwork.OfflineMode = true;
            // OfflineMode 已就位后再释放: 此刻主菜单的条件里 !OfflineMode 不成立, 不会误弹
            PracticePhotonGuard.ReleaseDisconnectModal(suppressModal);
            PracticeRunFlag.Engage();

            // 2. 把练习模式赛制套进 RunSettings (难度 / 单群系路线)
            try { RunConfigService.ApplyCustomRun(_ruleset.Ascent, _ruleset.Route); }
            catch (Exception ex) { PcsrLog.LogWarning("[PeakCSR] 应用练习赛制失败: " + ex.Message); }


            // 3. 加载机场 (离线态), 机场里的登机口才是真正进入地图的入口
            PcsrLog.LogInfo("[PeakCSR] 加载机场 (离线)...");
            try
            {
                LoadingScreenHandler handler = RetrievableResourceSingleton<LoadingScreenHandler>.Instance;
                if (handler == null)
                {
                    Fail("LoadingScreenHandler 不可用", gen);
                    yield break;
                }
                // 载图前最后一道取消检查: 一旦已被取代就绝不发起场景切换
                if (!IsCurrent(gen)) yield break;
                handler.Load(
                    LoadingScreen.LoadingScreenType.Basic,
                    null,
                    handler.LoadSceneProcess("Airport", false, true));
            }
            catch (Exception ex)
            {
                Fail("加载机场失败: " + ex.Message, gen);
                yield break;
            }

            // 4. 机场就绪后通过登机口启动单人局
            float airportDeadline = Time.realtimeSinceStartup + 60f;
            while (SceneManager.GetActiveScene().name != "Airport" && Time.realtimeSinceStartup < airportDeadline)
            {
                if (!IsCurrent(gen)) yield break;
                yield return null;
            }
            if (!IsCurrent(gen)) yield break;
            if (SceneManager.GetActiveScene().name != "Airport")
            {
                Fail("等待机场场景超时", gen);
                yield break;
            }
            // 机场内部还需要一帧以上完成服务初始化
            yield return null;
            yield return null;
            if (!IsCurrent(gen)) yield break;

            // 5. 现在 NextLevelService 已就绪, 地图槽位可校验, 登记计时才生效
            PracticeRunGate.Arm(ResolveCurrentLevelIndex());

            PcsrLog.LogInfo("[PeakCSR] 机场就绪, 通过登机口开始单人登机...");
            bool started = false;
            try
            {
                AirportCheckInKiosk kiosk = UnityEngine.Object.FindAnyObjectByType<AirportCheckInKiosk>();
                if (kiosk != null)
                {
                    kiosk.StartGame(_ruleset.Ascent);
                    started = true;
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 登机口启动失败, 尝试直接载图: " + ex.Message);
            }

            if (!started)
            {
                // 退化路径: 机场物件缺失时直接用 MapCatalog 载入本地地图
                string err;
                if (!LaunchDirectFallback(out err))
                {
                    Fail(err, gen);
                    yield break;
                }
            }

            // 6. 等待计时器真正进入可采集状态
            float readyDeadline = Time.realtimeSinceStartup + ReadyTimeoutSeconds;
            while (Time.realtimeSinceStartup < readyDeadline)
            {
                if (!IsCurrent(gen)) yield break;
                if (IsTimerReady())
                {
                    _busy = false;
                    PcsrLog.LogInfo("[PeakCSR] 练习局已就绪, 开始计时。");
                    Action done = _onLevelEntered;
                    _onLevelEntered = null;
                    _onFailed = null;
                    if (done != null) done();
                    yield break;
                }
                yield return new WaitForSecondsRealtime(0.25f);
            }

            Fail("等待练习地图就绪超时", gen);
        }

        // 读取当前将要用到的地图槽位索引, 供 RunGate 做真实性校验
        private static int ResolveCurrentLevelIndex()
        {
            try
            {
                NextLevelService svc = GameHandler.GetService<NextLevelService>();
                if (svc != null) return svc.NextLevelIndexOrFallback;
            }
            catch
            {
            }
            try { return MapCatalog.CurrentLevelIndex; }
            catch { return -1; }
        }

        // 计时器进入 Running 或 WaitingForInput 即视为地图与角色都已就绪
        private static bool IsTimerReady()
        {
            try
            {
                RaceTimer timer = RaceTimer.Instance;
                if (timer == null) return false;
                if (timer.State != TimerState.WaitingForInput && timer.State != TimerState.Running)
                {
                    return false;
                }
                return Character.localCharacter != null;
            }
            catch
            {
                return false;
            }
        }

        // 兜底: 不经机场直接载入本地地图 (纯离线)
        private bool LaunchDirectFallback(out string error)
        {
            error = "";
            try
            {
                int levelIndex = MapCatalog.CurrentLevelIndex;
                string sceneName = MapCatalog.GetSceneName(levelIndex);
                if (string.IsNullOrEmpty(sceneName))
                {
                    error = "无法解析地图场景名";
                    return false;
                }
                LoadingScreenHandler handler = RetrievableResourceSingleton<LoadingScreenHandler>.Instance;
                if (handler == null)
                {
                    error = "LoadingScreenHandler 不可用";
                    return false;
                }
                handler.Load(
                    LoadingScreen.LoadingScreenType.Basic,
                    null,
                    handler.LoadSceneProcess(sceneName, false, true));
                PcsrLog.LogInfo("[PeakCSR] 退化直连载图: " + sceneName);
                return true;
            }
            catch (Exception ex)
            {
                error = "直连载图失败: " + ex.Message;
                return false;
            }
        }

        private void Fail(string reason)
        {
            Fail(reason, -1);
        }

        // gen >= 0 时只有代际仍然有效才上报, 避免旧协程把新一轮练习判失败
        private void Fail(string reason, int gen)
        {
            if (gen >= 0 && !IsCurrent(gen)) return;
            _busy = false;
            PcsrLog.LogWarning("[PeakCSR] 启动练习局失败: " + reason);
            PracticeRunFlag.Release();
            PracticeRunGate.Disarm();
            Action<string> fail = _onFailed;
            _onLevelEntered = null;
            _onFailed = null;
            if (fail != null) fail(reason);
        }
    }
}
