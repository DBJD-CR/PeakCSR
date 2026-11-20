namespace PeakCSR
{
    // 各阶段实现: 阶段只承载"该状态下每帧要驱动什么",
    // 跨阶段的正交任务 (连接监测/投票/握手/结算) 由各子系统承担。

    // 空阶段: Countdown / Running / Finished 无阶段内专属轮询
    public sealed class IdlePhase : IMatchPhase
    {
        public void Enter(MatchSession session) { }
        public void Tick(MatchSession session) { }
        public void Exit(MatchSession session) { }
    }

    // SearchingLobby: 主轮询大厅队列
    public sealed class SearchingPhase : IMatchPhase
    {
        private readonly MatchCoordinator _owner;
        public SearchingPhase(MatchCoordinator owner) { _owner = owner; }
        public void Enter(MatchSession session) { }
        public void Tick(MatchSession session) { _owner.Matchmaker.TickQueuePoll(session); }
        public void Exit(MatchSession session) { }
    }

    // WaitingOpponent: 双方各建一房时的收敛轮询
    public sealed class WaitingPhase : IMatchPhase
    {
        private readonly MatchCoordinator _owner;
        public WaitingPhase(MatchCoordinator owner) { _owner = owner; }
        public void Enter(MatchSession session) { }
        public void Tick(MatchSession session) { _owner.Matchmaker.TickConvergePoll(session); }
        public void Exit(MatchSession session) { }
    }

    // LoadingLevel: 加载看门狗
    public sealed class LoadingPhase : IMatchPhase
    {
        private readonly MatchCoordinator _owner;
        public LoadingPhase(MatchCoordinator owner) { _owner = owner; }
        public void Enter(MatchSession session) { }
        public void Tick(MatchSession session) { _owner.TickLoadingWatchdog(session); }
        public void Exit(MatchSession session) { }
    }
}
