using System;
using System.Collections.Generic;

namespace PeakCSR.Practice
{
    // ===== 领域端口 =====
    //
    // 领域层只声明"需要什么能力", 具体由基础设施层对接游戏本体与 RANKED。
    // 这样练习编排逻辑可以在不启动 Unity 的前提下被理解与推演。

    // 启动一场离线单人爬图 (原版"离线游玩"链路)
    public interface IPracticeRoomLauncher
    {
        bool IsBusy { get; }

        // onLevelEntered: 地图场景与计时器真正就绪后回调一次
        // onFailed: 启动失败 (环境不支持 / 载图异常)
        // 返回 false 表示当场就拒绝启动
        bool Launch(PracticeRuleset ruleset, Action onLevelEntered, Action<string> onFailed);

        // 中止尚在进行中的启动流程 (不负责回滚场景)
        void Abort();
    }

    // 大厅探针: 无会话, 只做一次拉取并筛出可加入的对手房
    public interface IPracticeLobbyProbe
    {
        bool Busy { get; }

        // excludeInviteCode: 刚被判定失效的房号, 本轮筛选中排除
        void Fetch(PracticeRuleset ruleset, string excludeInviteCode,
            Action<List<PracticeMatchCandidate>> onDone, Action<string> onError);
    }

    // 收尾导航: 回主菜单 + 把 Photon 从离线模式拉回在线
    public interface IPracticeNavigator
    {
        void ReturnToMainMenu();

        bool IsInMainMenu();

        // onReady: 已恢复在线连接, 可以接续真实匹配
        // onFailed: 重连失败, 携带可展示的原因
        void ReconnectOnline(Action onReady, Action<string> onFailed);
    }

    // 提示通道: 左下角系统消息与面板刷新
    public interface IPracticeNotifier
    {
        void PushChat(string richTextBody);

        void NotifyStateChanged();
    }
}
