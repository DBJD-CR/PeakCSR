namespace PeakCSR
{
    // 单局阶段接口 (Enter / Tick / Exit)
    public interface IMatchPhase
    {
        void Enter(MatchSession session);
        void Tick(MatchSession session);
        void Exit(MatchSession session);
    }
}
