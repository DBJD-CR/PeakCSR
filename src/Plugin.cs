using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace PeakCSR
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("com.rankedpeak.mod", BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.peakcsr.ranked1v1";
        public const string PluginName = "PeakCSR";
        public const string PluginVersion = "0.4.1";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        public ConfigEntry<KeyCode> ToggleMenuKey;
        public ConfigEntry<int> InitialElo;
        public ConfigEntry<bool> PreferChineseUi;
        public ConfigEntry<string> CloudApiUrl;

        // 日志窗口: 启动游戏时自动拉一个独立终端实时 tail BepInEx/LogOutput.log
        public ConfigEntry<bool> LogWindowAutoOpen;
        public ConfigEntry<bool> LogWindowFilterPeakCsr;
        public ConfigEntry<KeyCode> LogWindowHotkey;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ToggleMenuKey = Config.Bind("General", "ToggleMenuKey", KeyCode.F10, "开关 PeakCSR 1v1 排位面板快捷键");
            PreferChineseUi = Config.Bind("General", "PreferChineseUi", true, "默认使用简体中文界面文案 (i18n)");
            InitialElo = Config.Bind("Rating", "InitialElo", 1000, "初始 ELO 分数");
            CloudApiUrl = Config.Bind("Cloud", "CloudApiUrl", "https://peakcsr-ladder.3405689090.workers.dev", "Cloudflare Worker 全服天梯 API 地址 (例如 https://peakcsr-ladder.xxx.workers.dev)");

            LogWindowAutoOpen = Config.Bind("Logging", "AutoOpenLogWindow", true, "启动游戏时自动打开一个实时日志终端窗口 (tail BepInEx/LogOutput.log)");
            LogWindowFilterPeakCsr = Config.Bind("Logging", "LogWindowFilterPeakCsr", true, "日志窗口只显示含 [PeakCSR] 的行, 关掉则显示完整日志");
            // 组合键需在代码里判定 Ctrl 修饰, 这里只存主键
            LogWindowHotkey = Config.Bind("Logging", "LogWindowHotkey", KeyCode.F9, "手动开关实时日志窗口的快捷键 (需同时按住 Ctrl, 即 Ctrl + 该键)");

            CsrLoc.Initialize(PreferChineseUi.Value);
            EloCalculator.Load(InitialElo.Value);

            GameObject go = new GameObject("PeakCSR_Runtime");
            DontDestroyOnLoad(go);
            go.AddComponent<MatchController>();
            go.AddComponent<CloudLeaderboardService>();
            go.AddComponent<PeakCsrHud>();
            go.AddComponent<PcsrMenuWindow>();

            // 逐个补丁挂载, 单个失败不影响整体 (RANKED 更新后个别补丁目标可能改名)
            _harmony = new Harmony(PluginGuid);
            try
            {
                _harmony.PatchAll();
            }
            catch (Exception ex)
            {
                Log.LogWarning("[PeakCSR] PatchAll 部分失败, 尝试逐个挂载: " + ex.Message);
                PatchIndividually();
            }

            Log.LogInfo(PluginName + " v" + PluginVersion + " 加载成功！按 " + ToggleMenuKey.Value + " 键或点击主菜单右侧【PCSR 1v1】木牌即可打开排位中心喵~");
            Log.LogInfo("[PeakCSR] 按 Ctrl+" + LogWindowHotkey.Value + " 可开关实时日志窗口；日志文件: " + LogTailWindow.LogFilePath);

            if (LogWindowAutoOpen.Value)
            {
                LogTailWindow.OpenDefault();
            }
        }

        private void Update()
        {
            // 日志文件可能比插件晚生成, 这里驱动重试
            LogTailWindow.Tick();

            // Ctrl + 主键 组合触发, 避免与游戏内普通按键冲突
            bool ctrlHeld = Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);
            if (ctrlHeld && LogWindowHotkey != null && Input.GetKeyDown(LogWindowHotkey.Value))
            {
                if (LogTailWindow.IsRunning)
                {
                    LogTailWindow.Close();
                    Log.LogInfo("[PeakCSR] 已关闭实时日志窗口。");
                }
                else
                {
                    LogTailWindow.OpenDefault();
                }
            }
        }

        // 逐个 PatchAll, 保证一个补丁类失败不会拖垮其它功能
        private void PatchIndividually()
        {
            Type[] types = typeof(Plugin).Assembly.GetTypes();
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                try
                {
                    if (!HasHarmonyPatch(type)) continue;
                    _harmony.CreateClassProcessor(type).Patch();
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[PeakCSR] 补丁挂载失败 " + type.Name + ": " + ex.Message);
                }
            }
        }

        private static bool HasHarmonyPatch(Type type)
        {
            object[] any = type.GetCustomAttributes(false);
            for (int i = 0; i < any.Length; i++)
            {
                if (any[i] is HarmonyPatch) return true;
            }
            return false;
        }

        private void OnDestroy()
        {
            LogTailWindow.Close();
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}