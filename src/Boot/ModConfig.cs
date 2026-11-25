using BepInEx.Configuration;
using UnityEngine;

namespace PeakCSR
{
    // 强类型配置项封装 (原 Plugin 内直接 Bind 的字段集中管理)
    public sealed class ModConfig
    {
        public readonly ConfigEntry<KeyCode> ToggleMenuKey;
        public readonly ConfigEntry<int> InitialElo;
        public readonly ConfigEntry<bool> PreferChineseUi;
        public readonly ConfigEntry<string> CloudApiUrl;

        // 日志窗口: 启动游戏时自动拉一个独立终端实时 tail BepInEx/LogOutput.log
        public readonly ConfigEntry<bool> LogWindowAutoOpen;
        public readonly ConfigEntry<bool> LogWindowFilterPeakCsr;
        public readonly ConfigEntry<KeyCode> LogWindowHotkey;

        public ModConfig(ConfigFile file)
        {
            ToggleMenuKey = file.Bind("General", "ToggleMenuKey", KeyCode.F10, "开关 PeakCSR 1v1 排位面板快捷键");
            PreferChineseUi = file.Bind("General", "PreferChineseUi", true, "默认使用简体中文界面文案 (i18n)");
            InitialElo = file.Bind("Rating", "InitialElo", 1000, "初始 ELO 分数");
            CloudApiUrl = file.Bind("Cloud", "CloudApiUrl", "https://peakcsr-ladder.3405689090.workers.dev", "Cloudflare Worker 全服天梯 API 地址 (例如 https://peakcsr-ladder.xxx.workers.dev)");

            LogWindowAutoOpen = file.Bind("Logging", "AutoOpenLogWindow", true, "启动游戏时自动打开一个实时日志终端窗口 (tail BepInEx/LogOutput.log)");
            LogWindowFilterPeakCsr = file.Bind("Logging", "LogWindowFilterPeakCsr", true, "日志窗口只显示含 [PeakCSR] 的行, 关掉则显示完整日志");
            // 组合键需在代码里判定 Ctrl 修饰, 这里只存主键
            LogWindowHotkey = file.Bind("Logging", "LogWindowHotkey", KeyCode.F9, "手动开关实时日志窗口的快捷键 (需同时按住 Ctrl, 即 Ctrl + 该键)");
        }
    }
}
