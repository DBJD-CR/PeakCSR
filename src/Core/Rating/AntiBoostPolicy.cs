using System;

namespace PeakCSR
{
    // 近期交手台账抽象: 由 Ladder 层提供内存实现, Core 保持零外部依赖
    public interface IEncounterLedger
    {
        // 清理过期记录后返回与该对手的近期交手次数 (不含本次)
        int PruneAndCount(string opponentSteamId, long nowMinutesUtc);
        // 登记一次交手
        void Record(string opponentSteamId, long nowMinutesUtc);
    }

    // 防刷分 (Anti-Boost) 权重判定 (原 EloCalculator.EvaluateAntiBoostFactor 纯逻辑化)
    // 判定顺序与旧版严格一致: 未计分 → 自战 → 分差 → 无对手 → 频次
    public static class AntiBoostPolicy
    {
        private static readonly RatingRules R = RatingRules.Instance;

        public static double Evaluate(
            bool isRated,
            string selfSteamId,
            string opponentSteamId,
            int opponentElo,
            int myElo,
            IEncounterLedger ledger,
            out string warningReasonKey)
        {
            warningReasonKey = null;
            if (!isRated)
            {
                warningReasonKey = "antiboost.unrated";
                return 0.0;
            }

            if (!string.IsNullOrEmpty(opponentSteamId) && opponentSteamId == selfSteamId)
            {
                warningReasonKey = "antiboost.self";
                return 0.0;
            }

            if (string.IsNullOrEmpty(opponentSteamId))
            {
                return 1.0;
            }

            // 交手频次必须先查，登记与查询放在同一步, 保证无论后面命中哪条规则, 这次交手都被记进台账
            long nowMinutes = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            int recentCount = ledger.PruneAndCount(opponentSteamId, nowMinutes);
            ledger.Record(opponentSteamId, nowMinutes);

            if (recentCount >= R.AntiBoost.MaxRecentAgainstSameOpponent)
            {
                warningReasonKey = "antiboost.limit";
                return 0.0;
            }

            int gap = myElo - opponentElo;
            if (gap < 0) gap = -gap;
            if (gap > R.AntiBoost.GapThreshold)
            {
                warningReasonKey = "antiboost.gap";
                return R.AntiBoost.GapWeight;
            }

            if (recentCount == 2)
            {
                warningReasonKey = "antiboost.half";
                return R.AntiBoost.RepeatWeight;
            }
            return 1.0;
        }
    }
}
