namespace PeakCSR
{
    // 对局状态机枚举 (值序与重构前完全一致; Idle 由"无会话"表达, 会话内永不出现 Idle)
    public enum CsMatchState
    {
        Idle,
        SearchingLobby,
        WaitingOpponent,
        LoadingLevel,
        Countdown,
        Running,
        Finished
    }
}
