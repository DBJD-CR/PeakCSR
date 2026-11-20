using System;

namespace PeakCSR
{
    // 统一玩家档案实体 (合并旧 EloCalculator 静态字段 / CloudProfile / CloudRankEntry)
    // 分赛制战绩用 ModeRecord[3] 数组承载, 按枚举下标访问
    public sealed class PlayerRecord
    {
        public int Elo = 1000;
        public int PeakElo = 1000;
        public int SeedElo = 1000;
        public bool SeededFromVanilla;

        public int Wins;
        public int Losses;
        public int Draws;
        public int Streak;

        // 分赛制战绩 (只统计计分赛): 0=全山 1=高难 2=单群系
        public ModeRecord[] Modes = new ModeRecord[3];

        // 玩家自选 / 自动推导的偏好
        // FavoriteMap 存群系索引 (见 PcsrBiomes), -1 表示未设置
        public int FavoriteMap = -1;
        public int FavoriteAscent = -999;
        public int FavoriteFormat = -1;

        // 难度/模式不手选, 直接按打过的局数统计推导
        // 下标 = ascent + 1, 对应 -1..8
        public readonly int[] AscentGames = new int[10];
        public readonly int[] FormatGames = new int[3];

        public int TotalGames { get { return Wins + Losses + Draws; } }

        public int WinRatePercent
        {
            get
            {
                int total = Wins + Losses;
                if (total <= 0) return 0;
                return (int)Math.Round(Wins * 100.0 / total);
            }
        }

        private static int ModeIndex(McsrRuleFormat format)
        {
            int idx = (int)format;
            if (idx < 0) idx = 0;
            if (idx > 2) idx = 2;
            return idx;
        }

        public int ModeWins(McsrRuleFormat format) { return Modes[ModeIndex(format)].Wins; }
        public int ModeLosses(McsrRuleFormat format) { return Modes[ModeIndex(format)].Losses; }
        public int ModeGames(McsrRuleFormat format) { return Modes[ModeIndex(format)].Games; }

        public int ModeWinRatePercent(McsrRuleFormat format)
        {
            ModeRecord m = Modes[ModeIndex(format)];
            int t = m.Wins + m.Losses;
            if (t <= 0) return 0;
            return (int)Math.Round(m.Wins * 100.0 / t);
        }

        // 最常玩难度: 取场次最多的天阶, 没有记录则回退到已存的旧值
        public int FavoriteAscentEffective
        {
            get
            {
                int best = -999;
                int bestGames = 0;
                for (int i = 0; i < AscentGames.Length; i++)
                {
                    if (AscentGames[i] > bestGames)
                    {
                        bestGames = AscentGames[i];
                        best = i - 1;
                    }
                }
                if (bestGames > 0) return best;
                return FavoriteAscent;
            }
        }

        // 最常玩模式: 同理取场次最多的赛制
        public int FavoriteFormatEffective
        {
            get
            {
                int best = -1;
                int bestGames = 0;
                for (int i = 0; i < FormatGames.Length; i++)
                {
                    if (FormatGames[i] > bestGames)
                    {
                        bestGames = FormatGames[i];
                        best = i;
                    }
                }
                if (bestGames > 0) return best;
                return FavoriteFormat;
            }
        }

        public void SetFavoriteMap(int biomeIndex, int biomeCount)
        {
            if (biomeIndex < -1) biomeIndex = -1;
            if (biomeIndex > biomeCount - 1) biomeIndex = biomeCount - 1;
            FavoriteMap = biomeIndex;
        }

        // 计分赛结果落账 (含胜负/连胜/分赛制/偏好统计, 与旧 BumpModeStat 系列行为一致)
        public void ApplyRatedResult(McsrRuleFormat format, int ascent, double actualScore, int delta)
        {
            Elo += delta;
            if (Elo < EloEngine.EloClampMin) Elo = EloEngine.EloClampMin;
            if (Elo > EloEngine.EloClampMax) Elo = EloEngine.EloClampMax;
            if (Elo > PeakElo) PeakElo = Elo;

            if (actualScore > 0.75)
            {
                Wins++;
                Streak = Streak >= 0 ? Streak + 1 : 1;
                BumpModeStat(format, true);
            }
            else if (actualScore < 0.25)
            {
                Losses++;
                Streak = Streak <= 0 ? Streak - 1 : -1;
                BumpModeStat(format, false);
            }
            else
            {
                Draws++;
                Streak = 0;
                BumpModeGameOnly(format);
            }
            // 最常玩难度/模式完全由真实对局推导, 不需要玩家手选
            BumpPreferenceStats(format, ascent);
        }

        // 练习赛: 分数变动但不计入胜负与分赛制战绩
        public void ApplyUnratedResult(int delta)
        {
            Elo += delta;
            if (Elo < EloEngine.EloClampMin) Elo = EloEngine.EloClampMin;
            if (Elo > EloEngine.EloClampMax) Elo = EloEngine.EloClampMax;
            if (Elo > PeakElo) PeakElo = Elo;
        }

        private void BumpModeStat(McsrRuleFormat format, bool won)
        {
            ModeRecord m = Modes[ModeIndex(format)];
            if (won) m.Wins++; else m.Losses++;
            m.Games++;
            Modes[ModeIndex(format)] = m;
        }

        private void BumpModeGameOnly(McsrRuleFormat format)
        {
            ModeRecord m = Modes[ModeIndex(format)];
            m.Games++;
            Modes[ModeIndex(format)] = m;
        }

        // 每打完一局计一次, 用于推导"最常玩难度"和"最常玩模式"
        private void BumpPreferenceStats(McsrRuleFormat format, int ascent)
        {
            int ascentIdx = RoomMagicCodec.ClampAscent(ascent) + 1;
            if (ascentIdx >= 0 && ascentIdx < AscentGames.Length)
            {
                AscentGames[ascentIdx]++;
            }
            int fmtIdx = ModeIndex(format);
            FormatGames[fmtIdx]++;
        }

        // 云端权威分数覆盖 (只动分数, 不动战绩口径)
        // 云端在上报"等待对手/待上游核验"时响应体里没有 elo 字段,
        // 解析层会给出 0。0 绝不是一个合法权威分, 必须整条丢弃
        public bool ApplyCloudAuthoritativeElo(int cloudElo)
        {
            if (!IsPlausibleCloudElo(cloudElo))
            {
                PcsrLog.LogWarning("[PeakCSR] 云端权威分不可信 (" + cloudElo + ")，本次不落地，保留本地分数 " + Elo + "。");
                return false;
            }
            Elo = Math.Max(EloEngine.EloClampMin, Math.Min(EloEngine.EloClampMax, cloudElo));
            if (Elo > PeakElo) PeakElo = Elo;
            return true;
        }

        // 云端权威分合法性判定: 0 / 负数 / 超出地上限 都不是真实分数
        public static bool IsPlausibleCloudElo(int cloudElo)
        {
            if (cloudElo <= 0) return false;
            if (cloudElo > EloEngine.EloClampMax) return false;
            return true;
        }

        // 云端权威档案覆盖 (含分赛制战绩)
        // draws: 云端平局数, 传 -1 表示未拿到权威档案 (本地计分赛不会产生平局, 保留现值)
        // 返回 false 表示云端分数不可信, 整份档案未落地
        public bool ApplyCloudAuthoritativeState(
            int elo, int peak, int wins, int losses, int draws, int streak,
            int winsAllPeak, int lossesAllPeak,
            int winsHardcore, int lossesHardcore,
            int winsSprint, int lossesSprint,
            int favoriteMap, int favoriteAscent, int favoriteFormat,
            int biomeCount)
        {
            // 分数不可信时整份档案都不采纳
            if (!IsPlausibleCloudElo(elo))
            {
                PcsrLog.LogWarning("[PeakCSR] 云端权威档案分数不可信 (" + elo + ")，整份档案本次不落地。");
                return false;
            }
            Elo = Math.Max(EloEngine.EloClampMin, Math.Min(EloEngine.EloClampMax, elo));
            PeakElo = Math.Max(Elo, peak);
            Wins = Math.Max(0, wins);
            Losses = Math.Max(0, losses);
            // 云端 wins/losses 被重写时, 本地残留的 draws 会让 TotalGames 多算一局,
            // 使定级赛提前一局结束, 所以必须与胜负一起同步
            if (draws >= 0) Draws = Math.Max(0, draws);
            Streak = streak;

            Modes[0] = new ModeRecord(Math.Max(0, winsAllPeak), Math.Max(0, lossesAllPeak),
                Math.Max(0, winsAllPeak) + Math.Max(0, lossesAllPeak));
            Modes[1] = new ModeRecord(Math.Max(0, winsHardcore), Math.Max(0, lossesHardcore),
                Math.Max(0, winsHardcore) + Math.Max(0, lossesHardcore));
            Modes[2] = new ModeRecord(Math.Max(0, winsSprint), Math.Max(0, lossesSprint),
                Math.Max(0, winsSprint) + Math.Max(0, lossesSprint));

            if (favoriteMap >= 0 && favoriteMap < biomeCount) FavoriteMap = favoriteMap;
            if (favoriteAscent > -900) FavoriteAscent = favoriteAscent;
            if (favoriteFormat >= 0) FavoriteFormat = favoriteFormat;
            return true;
        }

        // 本地存档签名篡改时清零 (与旧行为一致: 回到初始分并清空统计)
        public void ResetToDefault(int defaultElo)
        {
            Elo = defaultElo;
            PeakElo = defaultElo;
            SeedElo = defaultElo;
            SeededFromVanilla = false;
            Wins = 0;
            Losses = 0;
            Draws = 0;
            Streak = 0;
            Modes[0] = new ModeRecord();
            Modes[1] = new ModeRecord();
            Modes[2] = new ModeRecord();
            FavoriteMap = -1;
            FavoriteAscent = -999;
            FavoriteFormat = -1;
            Array.Clear(AscentGames, 0, AscentGames.Length);
            Array.Clear(FormatGames, 0, FormatGames.Length);
        }
    }
}
