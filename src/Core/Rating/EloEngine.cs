using System;

namespace PeakCSR
{
    // ELO 纯函数引擎 (原 EloCalculator 静态算分逻辑收口, 零 UnityEngine 依赖)
    // 所有参数取自 RatingRules (shared/rating-rules.json), 口径与 Cloudflare Worker 完全一致
    public static class EloEngine
    {
        private static readonly RatingRules R = RatingRules.Instance;

        // 以下边框值一律实时读规则 (shared/rating-rules.json)
        public static int MinSeedElo { get { return R.Seed.Min; } }
        public static int MaxSeedElo { get { return R.Seed.Max; } }
        public static int PlacementMatchesCount { get { return R.PlacementMatches; } }
        public static int EloClampMin { get { return R.Elo.ClampMin; } }
        public static int EloClampMax { get { return R.Elo.ClampMax; } }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        private static float Clamp01(float v)
        {
            if (v < 0f) return 0f;
            if (v > 1f) return 1f;
            return v;
        }

        // 天阶 0~8 对应的手工定制难度倍率表 (A-1 菜鸟新人单独按 0.75x 处理)
        public static double GetAscentMultiplier(int ascent)
        {
            int clamped = Clamp(ascent, -1, 8);
            if (clamped < 0) return R.TenderfootMultiplier;
            double[] table = R.AscentMultipliers;
            if (table == null || clamped >= table.Length) return 1.0;
            return table[clamped];
        }

        // 计算模式与天阶难度 ELO 倍率：菜鸟新人(A-1)为 0.75x，全山顶峰(A0)为基准 1.00x，
        // 天阶 1~8 递增，单群系冲刺乘 0.22x
        public static double GetModeMultiplier(PcsrRuleFormat format, int ascent)
        {
            double ascentMult = GetAscentMultiplier(ascent);
            if (format == PcsrRuleFormat.BiomeSprint)
            {
                // 与 Worker 的 Math.round(x*100)/100 同口径: .xx5 一律进位
                return Math.Round(R.BiomeSprintFactor * ascentMult, 2, MidpointRounding.AwayFromZero);
            }
            return ascentMult;
        }

        // 获胜保底分: 全山/硬核 8 x 天阶倍率(四舍五入), 单群系 2 x 天阶倍率(向下取整)
        // 这是一个"绝对下限", 不再被赛制倍率二次乘掉
        //   全山:  A-1 → +6,  A0 → +8,  A2 → +9,  A5 → +11, A8 → +14
        //   单群系: A-1 → +1,  A0 → +2,  A6 → +3,  A8 → +3
        // 1650+ 高端分段先把保底钳制到 12, 避免顶端出现刷分情况
        public static int GetMinWinDelta(PcsrRuleFormat format, int ascent, int currentElo)
        {
            double ascentMult = GetAscentMultiplier(ascent);
            int baseMin = format == PcsrRuleFormat.BiomeSprint ? R.WinFloorSection.BiomeSprint : R.WinFloorSection.FullPeak;
            double scaled = baseMin * ascentMult;
            int result = format == PcsrRuleFormat.BiomeSprint
                ? (int)Math.Floor(scaled)
                : (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (currentElo >= R.HighFloor.Threshold)
            {
                if (result > R.HighFloor.WinCap) result = R.HighFloor.WinCap;
            }
            if (result < 1) result = 1;
            return result;
        }

        public static int GetBaseKFactor(int totalGames, int currentElo)
        {
            if (totalGames < R.PlacementMatches) return R.K.Placement;
            if (currentElo >= R.HighFloor.Threshold) return R.K.HighElo;
            return R.K.Regular;
        }

        // 软折算公式：1000 + (原版ELO - 1000) * 0.5，限制在 850 ~ 1500 之间
        public static int ComputeSoftResetSeedElo(int vanillaElo)
        {
            int soft = (int)Math.Round(R.Seed.VanillaBase + (vanillaElo - R.Seed.VanillaBase) * R.Seed.VanillaWeight,
                MidpointRounding.AwayFromZero);
            if (soft < R.Seed.Min) soft = R.Seed.Min;
            if (soft > R.Seed.Max) soft = R.Seed.Max;
            return soft;
        }

        // 计算攀爬完成度与高度差修正 (MCSR 理念：定级赛与常规赛根据爬升完成度与双方进度差给予补偿)
        public static int ComputeProgressModifier(
            double actualScore,
            float selfProgress01,
            float oppProgress01,
            PcsrRuleFormat format,
            int ascent,
            bool isPlacement,
            double antiBoostWeight)
        {
            if (antiBoostWeight <= 0.01) return 0;

            float myP = Clamp01(selfProgress01);
            float oppP = Clamp01(oppProgress01);
            float diff = myP - oppP;

            RatingRules.ProgressCompensation pc = R.Progress;
            double scale = format == PcsrRuleFormat.BiomeSprint ? pc.SprintScale : 1.0;
            if (ascent < 0) scale *= pc.TenderfootScale;

            double bonus = 0.0;
            if (actualScore > 0.75)
            {
                // 胜方：若大幅领先对手登顶 (如对手还在半山腰)，额外奖励最多 +4 分 (定级 +6)
                double leadMax = isPlacement ? pc.LeadBonus.Placement : pc.LeadBonus.Regular;
                bonus = Math.Max(0.0, diff) * leadMax * scale;
            }
            else if (actualScore < 0.25)
            {
                // 败方补偿：定级期重点看完成度！爬到 80%~100% 惜败与 5% 暴毙天差地别
                // 1) 自身完成度减免：爬过 30% 后按完成度减免扣分，定级期最高可补回 +12 分，常规期最高 +6 分
                double completionReliefMax = isPlacement ? pc.CompletionRelief.Placement : pc.CompletionRelief.Regular;
                double completionFactor = Clamp01((float)((myP - pc.CompletionStart) / pc.CompletionSpan));
                double relief = completionFactor * completionReliefMax;

                // 2) 双方高度差修正：若败方惜败（进度差很小甚至领先时暴毙），少扣；若被完全碾压（落后 >60%）且定级期完不成 20%，略微多扣
                double gapTerm = diff * (isPlacement ? pc.GapTerm.Placement : pc.GapTerm.Regular);
                bonus = (relief + gapTerm) * scale;
            }

            // 与 JS Math.round 完全同口径
            int rounded = JsRound(bonus * antiBoostWeight);
            int maxCap = isPlacement ? pc.Cap.Placement : pc.Cap.Regular;
            if (rounded < -maxCap) rounded = -maxCap;
            if (rounded > maxCap) rounded = maxCap;
            return rounded;
        }

        // 计算最终 ELO 变动
        //   weightFactor    : antiBoostWeight * modeMultiplier, 只作用于基础分 (不污染保底)
        //   antiBoostWeight : 仅用于判断是否应当施加保底 (练习模式、自建小号、防刷分时不施加)
        //   minWinDelta     : GetMinWinDelta 预先算好的绝对保底下限 (只对胜方生效)
        //
        // 败方不做任何扣分兜底: 公式算出多少就扣多少。
        // 唯一保留的败方守卫生效于"因分差过大反而算出加分"这种荒谬情况, 见 rawBase >= 0 分支。
        public static int ComputeDelta(
            int myElo,
            int opponentElo,
            double actualScore,
            int kFactor,
            double weightFactor,
            double antiBoostWeight,
            int minWinDelta,
            int progressBonus,
            out int baseDeltaOut)
        {
            double expected = 1.0 / (1.0 + Math.Pow(10.0, (opponentElo - myElo) / R.Elo.ScaleDenominator));
            // 同上: 与 Worker 的 Math.round 对齐 (含负数半值向正无穷)
            int rawBase = JsRound(kFactor * (actualScore - expected) * weightFactor);

            if (antiBoostWeight > 0.01)
            {
                if (actualScore > 0.75)
                {
                    if (rawBase < minWinDelta) rawBase = minWinDelta;
                }
                else if (actualScore < 0.25 && rawBase >= 0)
                {
                    // 输了却算出加分: 钳成 -1。这不是"扣分兜底", 只是禁止败方加分
                    rawBase = -1;
                }
            }

            baseDeltaOut = rawBase;
            int total = rawBase + progressBonus;

            // 保底只给胜方, 且是绝对下限: 进度补偿为负也不能低于它
            if (antiBoostWeight > 0.01 && actualScore > 0.75 && total < minWinDelta)
            {
                total = minWinDelta;
            }
            else if (antiBoostWeight > 0.01 && actualScore < 0.25 && total > 0)
            {
                // 败方不得因进度补偿而净加分 (这不是扣分兜底, 只是禁止输着加分)
                total = 0;
            }

            return total;
        }

        // JS Math.round 的等价实现: floor(x + 0.5)。
        // 只用于需要与 Worker 逐分对齐的取整点, 不要拿它替换业务里明确要求四舍五入的地方。
        private static int JsRound(double value)
        {
            return (int)Math.Floor(value + 0.5);
        }
    }
}
