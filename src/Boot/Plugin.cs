using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace PeakCSR
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.rankedpeak.mod", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.peakcsr.ranked1v1";
        public const string PluginName = "PeakCSR";
        // 版本号由构建脚本从 manifest.json 自动生成 (PluginVersion.g.cs), 不再手工双处同步
        public const string PluginVersion = PluginVersionConstant.Version;

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        public BepInEx.Configuration.ConfigEntry<KeyCode> ToggleMenuKey;
        public BepInEx.Configuration.ConfigEntry<int> InitialElo;
        public BepInEx.Configuration.ConfigEntry<bool> PreferChineseUi;
        public BepInEx.Configuration.ConfigEntry<string> CloudApiUrl;

        // 日志窗口: 启动游戏时自动拉一个独立终端实时 tail BepInEx/LogOutput.log
        public BepInEx.Configuration.ConfigEntry<bool> LogWindowAutoOpen;
        public BepInEx.Configuration.ConfigEntry<bool> LogWindowFilterPeakCsr;
        public BepInEx.Configuration.ConfigEntry<KeyCode> LogWindowHotkey;

        private HarmonyLib.Harmony _harmony;
        private ModConfig _config;
        private MatchCoordinator _coordinator;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            _config = new ModConfig(Config);
            ToggleMenuKey = _config.ToggleMenuKey;
            PreferChineseUi = _config.PreferChineseUi;
            InitialElo = _config.InitialElo;
            CloudApiUrl = _config.CloudApiUrl;
            LogWindowAutoOpen = _config.LogWindowAutoOpen;
            LogWindowFilterPeakCsr = _config.LogWindowFilterPeakCsr;
            LogWindowHotkey = _config.LogWindowHotkey;

            CsrLoc.Initialize(PreferChineseUi.Value);
            PcsrLog.Initialize(Logger);
            TierCatalog.SetLocalizer(CsrLoc.T);
            // Core 层零 Boot 依赖: 进度修正的警告输出经此注入
            ProgressMonotonicState.WarnSink = message => PcsrLog.LogWarning(message);
            MatchNaming.Bind(
                ascent => CsrLoc.T(ascent <= -1 ? "ascent.name.tenderfoot" : (ascent == 0 ? "ascent.name.peak" : "ascent.name.numbered"), ascent),
                ascent => CsrLoc.T(ascent <= -1 ? "ascent.short.tenderfoot" : (ascent == 0 ? "ascent.short.peak" : "ascent.short.numbered"), ascent),
                (format, ascent) =>
                {
                    string diff = MatchNaming.GetAscentDisplayName(ascent);
                    switch (format)
                    {
                        case McsrRuleFormat.HardcoreAscent4:
                            return CsrLoc.T("format.hardcore.full", diff);
                        case McsrRuleFormat.BiomeSprint:
                            return CsrLoc.T("format.sprint.full", diff);
                        default:
                            return CsrLoc.T("format.standard.full", diff);
                    }
                });

            // 本地档案: Newtonsoft + 签名 + 旧版平铺存档自动迁移 (护栏 3)
            LocalProfileStore store = new LocalProfileStore();
            PlayerRecord record = new PlayerRecord();
            store.Load(record, InitialElo.Value);
            PlayerProfileBridge profile = new PlayerProfileBridge(record, store);
            HookVanillaRatingSeed(profile);

            // 对局编排: 双轨 Tick 管道由本组件驱动
            _coordinator = new MatchCoordinator(new RankedPeakGateway(), profile);

            GameObject go = new GameObject("PeakCSR_Runtime");
            DontDestroyOnLoad(go);

            // 天梯同步服务 (MonoBehaviour, 负责云端协程)
            CloudLadderClient cloudClient = new CloudLadderClient(null);
            LadderSyncService ladder = go.AddComponent<LadderSyncService>();
            ladder.Initialize(cloudClient, profile);
            // 结算上报通道: 协调器必须持有天梯服务引用, 否则战报永远不会提交
            _coordinator.Ladder = ladder;

            // UI 宿主 (旧 IMGUI PeakCsrHud 已删除, F10 热键统一在本组件 Update 处理)
            go.AddComponent<PcsrMenuWindow>();
            go.AddComponent<PostMatchReportView>();
            go.AddComponent<ProfileCardView>();

            // 逐个补丁挂载 + 自检报告 (单个失败不影响整体, RANKED 更新后个别补丁目标可能改名)
            _harmony = new HarmonyLib.Harmony(PluginGuid);
            PatchRegistry.PatchAll(_harmony);

            PcsrLog.LogInfo(PluginName + " v" + PluginVersion + " 加载成功！按 " + ToggleMenuKey.Value + " 键或点击主菜单右侧【PCSR 1v1】木牌即可打开排位中心喵~");
            PcsrLog.LogInfo("[PeakCSR] 按 Ctrl+" + LogWindowHotkey.Value + " 可开关实时日志窗口；日志文件: " + LogTailWindow.LogFilePath);

            if (LogWindowAutoOpen.Value)
            {
                LogTailWindow.OpenDefault();
            }
        }

        private void Update()
        {
            // 对局编排双轨管道: 全局服务 (队列心跳/延迟退房) 永远驱动, 活动会话按需驱动
            if (_coordinator != null)
            {
                _coordinator.TickGlobalServices();
                _coordinator.TickActiveSession();
            }

            // F10 开关排位中心 (迁移自旧 PeakCsrHud 的热键; 现在是真正的开/关切换)
            if (ToggleMenuKey != null && Input.GetKeyDown(ToggleMenuKey.Value))
            {
                PcsrMenuWindow.ToggleWindow();
            }

            // 日志文件可能比插件晚生成, 这里驱动重试
            LogTailWindow.Tick();

            // Ctrl + 主键 组合触发, 避免与游戏内普通按键冲突
            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrlHeld && LogWindowHotkey != null && Input.GetKeyDown(LogWindowHotkey.Value))
            {
                if (LogTailWindow.IsRunning)
                {
                    LogTailWindow.Close();
                    PcsrLog.LogInfo("[PeakCSR] 已关闭实时日志窗口。");
                }
                else
                {
                    LogTailWindow.OpenDefault();
                }
            }
        }

        // 原版种子分软折算 (原 EloCalculator.HookVanillaRatingSeed 迁移)
        private bool _vanillaHooked;

        private void HookVanillaRatingSeed(PlayerProfileBridge profile)
        {
            if (!_vanillaHooked)
            {
                _vanillaHooked = true;
                RankedPeak.Services.RatingService.RatingUpdated += rating => OnVanillaRatingUpdated(profile, rating);
            }
            try
            {
                if (RankedPeak.Services.RatingService.Instance != null
                    && RankedPeak.Services.RatingService.Instance.CurrentRating != null)
                {
                    OnVanillaRatingUpdated(profile, RankedPeak.Services.RatingService.Instance.CurrentRating);
                }
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 原版种子分读取失败: " + ex.Message);
            }
        }

        private void OnVanillaRatingUpdated(PlayerProfileBridge profile, RankedPeak.Models.PlayerRating rating)
        {
            PlayerRecord rec = profile.Record;
            if (rec.SeededFromVanilla) return;
            if (rec.Wins > 0 || rec.Losses > 0 || rec.Draws > 0) return;
            if (rating == null || !rating.Ranked || rating.Elo <= 0) return;
            if (string.Equals(rating.Tier, "UNRANKED", System.StringComparison.OrdinalIgnoreCase)) return;

            int softSeed = EloEngine.ComputeSoftResetSeedElo(rating.Elo);
            rec.SeedElo = softSeed;
            rec.Elo = softSeed;
            rec.PeakElo = softSeed;
            rec.SeededFromVanilla = true;
            profile.Save();
            PcsrLog.LogInfo("[PeakCSR] 已根据原版 RANKED 历史分数 (" + rating.Elo + " ELO) 软折算初始化 PCSR 种子分: " + softSeed + " ELO");
            EventBus.PublishRatingChanged();
        }

        private void OnDestroy()
        {
            LogTailWindow.Close();
            if (_coordinator != null)
            {
                _coordinator.Dispose();
            }
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}
