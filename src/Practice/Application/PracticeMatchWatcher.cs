namespace PeakCSR.Practice
{
    // 后台大厅轮询器 (无会话)
    //
    // 与 LobbyMatchmaker 的区别: 它不持有 MatchSession, 不发起 join, 不拆会话,
    // 只按固定节奏问一次"现在有没有符合赛制的空位房", 命中就上报给编排器
    public sealed class PracticeMatchWatcher
    {
        private const float PollIntervalSeconds = 5f;

        private readonly IPracticeLobbyProbe _probe;
        private float _nextPollAt;
        private bool _started;

        public PracticeMatchWatcher(IPracticeLobbyProbe probe)
        {
            _probe = probe;
        }

        public void Reset()
        {
            _nextPollAt = 0f;
            _started = false;
        }

        // 驱动一次轮询。本函数的返回值只表示"本帧是否真的发起了请求", 不代表命中。
        // excludeInviteCode: 已判定失效的房号, 本轮不再命中
        // onFound: 命中回调, 每次轮询最多触发一次
        // onError: 静默失败, 调用方按提示处理即可, 不应中断练习
        public bool Tick(PracticeRuleset ruleset, string excludeInviteCode,
            System.Action<PracticeMatchCandidate> onFound, System.Action<string> onError)
        {
            if (_probe == null) return false;
            if (_probe.Busy) return false;
            if (UnityEngine.Time.realtimeSinceStartup < _nextPollAt) return false;

            _nextPollAt = UnityEngine.Time.realtimeSinceStartup + PollIntervalSeconds;
            _started = true;

            _probe.Fetch(ruleset, excludeInviteCode,
                delegate (System.Collections.Generic.List<PracticeMatchCandidate> candidates)
                {
                    if (candidates == null || candidates.Count == 0) return;
                    // 只取第一个有效候选, 顺序由服务端返回顺序决定
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (!candidates[i].IsValid) continue;
                        PracticeMatchCandidate hit = candidates[i];
                        PcsrLog.LogInfo("[PeakCSR] 练习探针命中房间 [" + hit.InviteCode + "]。");
                        if (onFound != null) onFound(hit);
                        return;
                    }
                },
                delegate (string err)
                {
                    if (onError != null) onError(err);
                });

            return true;
        }

        public bool HasPolledOnce { get { return _started; } }
    }
}
