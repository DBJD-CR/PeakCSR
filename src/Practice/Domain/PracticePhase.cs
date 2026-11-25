namespace PeakCSR.Practice
{
    // 练习模式阶段
    //
    // 与对局状态机 CsMatchState 完全独立: 练习期间根本不存在 MatchSession,
    // 因此不会触发 ConnectivityGuardian 的掉线判定 / 结算 / 上报等一整套排位副作用。
    public enum PracticePhase
    {
        // 未在练习
        Inactive,

        // 已发起离线单人局, 正在等待地图场景与计时器就绪
        Preparing,

        // 离线爬图中, 后台探针持续轮询大厅
        Running,

        // 已匹配到对手, 正在走回主菜单前的过渡倒计时
        MatchFound,

        // 正在回主菜单 / 重连 Photon / 把控制权交还给 MatchCoordinator
        Switching
    }
}
