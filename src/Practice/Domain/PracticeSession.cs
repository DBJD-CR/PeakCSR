using UnityEngine;

namespace PeakCSR.Practice
{
    // 交棒切换的三步: 先回主菜单, 再重连, 最后把房号交给排位链路
    // LoadScene 是异步的, 同一帧里既回城又重连必然失败;
    // join 结果同样是异步的, 等排位协调器回报成功才算是交棒完成
    public enum SwitchStep
    {
        None,
        ReturningToMenu,
        Reconnecting,
        // 已发起 join, 等待排位协调器回报最终结果
        HandoffRequested
    }

    // 练习单局实体
    //
    // 与 MatchSession 平级但互不相干: 字段全部围绕"离线爬图 + 后台探针 + 交棒",
    // 不含对手 ELO / 结算 / 胜负仲裁等任何排位概念。
    public sealed class PracticeSession
    {
        public PracticePhase Phase = PracticePhase.Inactive;
        public PracticeRuleset Ruleset;
        public string StatusMessage = "";

        // 离线地图就绪时刻
        public float StartedAt = -1f;

        // 后台探针起算时刻, 面板据此显示"已匹配用时"
        public float QueueStartedAt = -1f;

        // 匹配到对手的时刻, 过渡倒计时从这里起算
        public float MatchFoundAt = -1f;

        // 进入 Switching 的时刻, 重连/场景切换超时判定从这里起算
        public float SwitchStartedAt = -1f;

        public PracticeMatchCandidate Candidate;

        // 最近被判定失效的候选房号: 服务端会短暂保留残房, 必须主动排除
        public string RejectedInviteCode = "";

        // 交棒切换的当前步骤 (仅 Switching 阶段有意义)
        public SwitchStep SwitchPhase = SwitchStep.None;

        // 已匹配用时 (秒): 与排位面板的 QueueElapsedSeconds 语义一致
        public float QueueElapsedSeconds
        {
            get
            {
                if (QueueStartedAt < 0f) return 0f;
                return Mathf.Max(0f, Time.realtimeSinceStartup - QueueStartedAt);
            }
        }

        // 剩余过渡秒数 (仅 MatchFound 阶段有意义)
        public float SecondsUntilSwitch(float countdownSeconds)
        {
            if (MatchFoundAt < 0f) return 0f;
            float remain = countdownSeconds - (Time.realtimeSinceStartup - MatchFoundAt);
            return remain < 0f ? 0f : remain;
        }

        public void Reset()
        {
            Phase = PracticePhase.Inactive;
            StatusMessage = "";
            StartedAt = -1f;
            QueueStartedAt = -1f;
            MatchFoundAt = -1f;
            SwitchStartedAt = -1f;
            SwitchPhase = SwitchStep.None;
            Candidate = new PracticeMatchCandidate();
            RejectedInviteCode = "";
        }
    }
}
