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
        public const string PluginVersion = "0.2.2";

        internal static ManualLogSource Log;
        internal static Plugin Instance;

        public ConfigEntry<KeyCode> ToggleMenuKey;
        public ConfigEntry<int> InitialElo;
        public ConfigEntry<bool> PreferChineseUi;
        public ConfigEntry<string> CloudApiUrl;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            ToggleMenuKey = Config.Bind("General", "ToggleMenuKey", KeyCode.F10, "开关 PeakCSR 1v1 排位面板快捷键");
            PreferChineseUi = Config.Bind("General", "PreferChineseUi", true, "默认使用简体中文界面文案 (i18n)");
            InitialElo = Config.Bind("Rating", "InitialElo", 1000, "初始 ELO 分数");
            CloudApiUrl = Config.Bind("Cloud", "CloudApiUrl", "https://peakcsr-ladder.3405689090.workers.dev", "Cloudflare Worker 全服天梯 API 地址 (例如 https://peakcsr-ladder.xxx.workers.dev)");

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
            if (_harmony != null)
            {
                _harmony.UnpatchSelf();
            }
        }
    }
}