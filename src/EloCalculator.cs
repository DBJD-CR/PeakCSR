using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using RankedPeak.Models;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    public struct EloSettlementDetail
    {
        public int OldElo;
        public int NewElo;
        public int TotalDelta;
        public int BaseDelta;
        public int ProgressBonus;
        // 保底 / 上限钳制带来的附加修正 (总变动 - 基础 - 进度补偿)
        public int FloorAdjust;
        public int MinWinDelta;
        public double ModeMultiplier;
        public double AntiBoostWeight;
        public bool IsPlacement;
        public string OldTierFull;
        public string NewTierFull;
        public string AntiBoostNote;
        // 断线 / 无法确认结果时置位: 不改变 ELO, 也不计入胜负
        public bool Void;
    }

    public static class EloCalculator
    {
        private static readonly string SaveDir = Path.Combine(Paths.PluginPath, "PeakCSR");
        private static readonly string SavePath = Path.Combine(SaveDir, "elo_profile.json");
        private const string IntegritySalt = "PEAKCSR_1V1_SALT_2026";
        private const int MaxRecentMatchesAgainstSameOpponent = 3;
        private const long OpponentWindowMinutes = 30;
        public const int MinSeedElo = 850;
        public const int MaxSeedElo = 1500;
        public const int PlacementMatchesCount = 10;
        public const double EloScaleDenominator = 550.0;
        // 菜鸟新人难度倍率
        public const double TenderfootMultiplier = 0.75;
        // 达到该分段后获胜保底减半, 避免天阶高倍率下"赢一局抵输七局"
        public const int HighEloFloorHalveThreshold = 1650;

        private static int _currentElo = 1000;
        private static int _peakElo = 1000;
        private static int _seedElo = 1000;
        private static bool _seededFromVanilla;
        private static bool _vanillaHooked;
        private static int _wins;
        private static int _losses;
        private static int _draws;
        private static int _streak;

        // 分赛制战绩 (只统计计分赛)
        private static int _winsAllPeak;
        private static int _lossesAllPeak;
        private static int _winsHardcore;
        private static int _lossesHardcore;
        private static int _winsSprint;
        private static int _lossesSprint;

        // 分赛制累计场次 (胜 + 负 + 平)
        private static int _gamesAllPeak;
        private static int _gamesHardcore;
        private static int _gamesSprint;

        // 玩家自选 / 自动推导的偏好
        // _favoriteMap 改存群系索引 (见 PcsrBiomes), -1 表示未设置
        private static int _favoriteMap = -1;
        private static int _favoriteAscent = -999;
        private static int _favoriteFormat = -1;

        // 难度/模式不手选, 直接按打过的局数统计推导
        private static readonly int[] _ascentGames = new int[10];   // 下标 = ascent + 1, 对应 -1..8
        private static readonly int[] _formatGames = new int[3];    // 0=全山, 1=高难, 2=单群系

        private static readonly Dictionary<string, List<long>> _recentOpponents = new Dictionary<string, List<long>>();

        public static int CurrentElo { get { return _currentElo; } }
        public static int PeakElo { get { return _peakElo; } }
        public static int SeedElo { get { return _seedElo; } }
        public static bool SeededFromVanilla { get { return _seededFromVanilla; } }
        public static int Wins { get { return _wins; } }
        public static int Losses { get { return _losses; } }
        public static int Draws { get { return _draws; } }
        public static int Streak { get { return _streak; } }
        public static int TotalGames { get { return _wins + _losses + _draws; } }
        public static int FavoriteMap { get { return _favoriteMap; } }

        // 最常玩难度: 取场次最多的天阶, 没有记录则回退到已存的旧值
        public static int FavoriteAscent
        {
            get
            {
                int best = -999;
                int bestGames = 0;
                for (int i = 0; i < _ascentGames.Length; i++)
                {
                    if (_ascentGames[i] > bestGames)
                    {
                        bestGames = _ascentGames[i];
                        best = i - 1;
                    }
                }
                if (bestGames > 0) return best;
                return _favoriteAscent;
            }
        }

        // 最常玩模式: 同理取场次最多的赛制
        public static int FavoriteFormat
        {
            get
            {
                int best = -1;
                int bestGames = 0;
                for (int i = 0; i < _formatGames.Length; i++)
                {
                    if (_formatGames[i] > bestGames)
                    {
                        bestGames = _formatGames[i];
                        best = i;
                    }
                }
                if (bestGames > 0) return best;
                return _favoriteFormat;
            }
        }

        public static int WinRatePercent
        {
            get
            {
                int total = _wins + _losses;
                return total > 0 ? Mathf.RoundToInt(_wins * 100f / total) : 0;
            }
        }

        public static int ModeWins(McsrRuleFormat format)
        {
            if (format == McsrRuleFormat.BiomeSprint) return _winsSprint;
            if (format == McsrRuleFormat.HardcoreAscent4) return _winsHardcore;
            return _winsAllPeak;
        }

        public static int ModeLosses(McsrRuleFormat format)
        {
            if (format == McsrRuleFormat.BiomeSprint) return _lossesSprint;
            if (format == McsrRuleFormat.HardcoreAscent4) return _lossesHardcore;
            return _lossesAllPeak;
        }

        public static int ModeGames(McsrRuleFormat format)
        {
            if (format == McsrRuleFormat.BiomeSprint) return _gamesSprint;
            if (format == McsrRuleFormat.HardcoreAscent4) return _gamesHardcore;
            return _gamesAllPeak;
        }

        public static int ModeWinRatePercent(McsrRuleFormat format)
        {
            int w = ModeWins(format);
            int l = ModeLosses(format);
            int t = w + l;
            return t > 0 ? Mathf.RoundToInt(w * 100f / t) : 0;
        }

        // 保存玩家自选的群系索引 (见 PcsrBiomes.All), -1 表示未设置
        public static void SetFavoriteMap(int biomeIndex)
        {
            _favoriteMap = Mathf.Clamp(biomeIndex, -1, PcsrBiomes.Count - 1);
            Save();
        }

        public static event Action OnRatingChanged;

        // 获取基础大段位多语言名称
        public static string GetTierBaseName(int elo)
        {
            if (elo >= 1800) return CsrLoc.T("tier.sherpa");
            if (elo >= 1550) return CsrLoc.T("tier.alpinist");
            if (elo >= 1350) return CsrLoc.T("tier.climber");
            if (elo >= 1150) return CsrLoc.T("tier.hiker");
            if (elo >= 950) return CsrLoc.T("tier.scout");
            return CsrLoc.T("tier.tenderfoot");
        }

        // 计算段位内 I / II / III 级细分 (1 为最高小段，3 为初入小段)
        public static string GetTierSubDivisionRoman(int elo)
        {
            if (elo >= 1800)
            {
                if (elo >= 2000) return "I";
                if (elo >= 1900) return "II";
                return "III";
            }
            int min;
            int span;
            if (elo >= 1550) { min = 1550; span = 250; }
            else if (elo >= 1350) { min = 1350; span = 200; }
            else if (elo >= 1150) { min = 1150; span = 200; }
            else if (elo >= 950) { min = 950; span = 200; }
            else { min = 650; span = 300; }

            double ratio = (double)(Mathf.Max(min, elo) - min) / span;
            if (ratio >= 0.666) return "I";
            if (ratio >= 0.333) return "II";
            return "III";
        }

        // 获取带 I / II / III 细分的完整段位名称
        public static string GetTierName(int elo)
        {
            return GetTierBaseName(elo) + " " + GetTierSubDivisionRoman(elo);
        }

        // 获取简短段位+小段位名称，用于天梯列表紧凑展示
        public static string GetTierShortLabel(int elo)
        {
            string shortBase;
            if (elo >= 1800) shortBase = CsrLoc.T("tier.short.sherpa");
            else if (elo >= 1550) shortBase = CsrLoc.T("tier.short.alpinist");
            else if (elo >= 1350) shortBase = CsrLoc.T("tier.short.climber");
            else if (elo >= 1150) shortBase = CsrLoc.T("tier.short.hiker");
            else if (elo >= 950) shortBase = CsrLoc.T("tier.short.scout");
            else shortBase = CsrLoc.T("tier.short.tenderfoot");
            return shortBase + " " + GetTierSubDivisionRoman(elo);
        }

        // 获取当前小段位内部进度 (0.0 ~ 1.0)，用于赛后动画进度条
        public static float GetTierProgress01(int elo, out int tierFloor, out int tierCeil)
        {
            if (elo >= 1800)
            {
                tierFloor = 1800;
                tierCeil = 2100;
                return Mathf.Clamp01((elo - 1800f) / 300f);
            }
            if (elo >= 1550) { tierFloor = 1550; tierCeil = 1800; }
            else if (elo >= 1350) { tierFloor = 1350; tierCeil = 1550; }
            else if (elo >= 1150) { tierFloor = 1150; tierCeil = 1350; }
            else if (elo >= 950) { tierFloor = 950; tierCeil = 1150; }
            else { tierFloor = 750; tierCeil = 950; }
            return Mathf.Clamp01((float)(elo - tierFloor) / Mathf.Max(1, tierCeil - tierFloor));
        }

        // 获取原版 RankedMenuIcons 兼容的 Tier 标识
        public static string GetVanillaTierKey(int elo)
        {
            if (elo >= 1800) return "SHERPA";
            if (elo >= 1550) return "ALPINIST";
            if (elo >= 1350) return "CLIMBER";
            if (elo >= 1150) return "HIKER";
            return "SCOUT";
        }

        // 深色背景下的段位高亮色 (IMGUI 与深色赛后战报使用)
        public static string GetTierHexColor(int elo)
        {
            if (elo >= 1800) return "33E6FF"; // 夏尔巴: 璀璨青蓝
            if (elo >= 1550) return "FFD933"; // 登山家: 耀眼钻金
            if (elo >= 1350) return "45E680"; // 攀岩者: 翡翠绿
            if (elo >= 1150) return "FF9E44"; // 徒步者: 琥珀金橙
            if (elo >= 950) return "8CC8FF";  // 侦察兵: 精钢冰蓝
            return "A6ADB8";                  // 初学者: 黑煤岩灰
        }

        // 浅色羊皮纸背景下的段位高对比度配色 (PcsrMenuWindow 天梯与木牌专用，彻底解决第一名变灰、第二名变绿的错位问题)
        public static string GetTierHexColorOnLight(int elo)
        {
            if (elo >= 1800) return "006B8F"; // 夏尔巴: 深邃青蓝
            if (elo >= 1550) return "B87700"; // 登山家: 皇家暗金
            if (elo >= 1350) return "1E7E45"; // 攀岩者: 祖母深绿
            if (elo >= 1150) return "B85414"; // 徒步者: 暖铜赤橙
            if (elo >= 950) return "2E5C8A";  // 侦察兵: 钢青色
            return "5A6270";                  // 初学者: 炭灰色
        }

        // 天阶 0~8 对应的手工定制难度倍率表 (A-1 菜鸟新人单独按 0.75x 处理)
        private static readonly double[] AscentMultipliers = new double[9]
        {
            1.00,
            1.06,
            1.12,
            1.18,
            1.20,
            1.35,
            1.50,
            1.60,
            1.75
        };

        public static double GetAscentMultiplier(int ascent)
        {
            int clamped = Mathf.Clamp(ascent, -1, 8);
            return clamped < 0 ? TenderfootMultiplier : AscentMultipliers[clamped];
        }

        // 计算模式与天阶难度 ELO 倍率：菜鸟新人(A-1)为 0.75x，全山顶峰(A0)为基准 1.00x，天阶 1~8 递增，单群系冲刺乘 0.22x
        public static double GetModeMultiplier(McsrRuleFormat format, int ascent)
        {
            double ascentMult = GetAscentMultiplier(ascent);
            if (format == McsrRuleFormat.BiomeSprint)
            {
                return Math.Round(0.22 * ascentMult, 2);
            }
            return ascentMult;
        }

        // 获胜保底分: 全山/硬核 8 x 天阶倍率(四舍五入), 单群系 2 x 天阶倍率(向下取整)
        // 这是一个"绝对下限", 不再被赛制倍率二次乘掉
        //   全山:  A-1 → +6,  A0 → +8,  A2 → +9,  A5 → +11, A8 → +14
        //   单群系: A-1 → +1,  A0 → +2,  A6 → +3,  A8 → +3
        // 1650+ 高端分段先把保底钳制到 12, 避免顶端出现刷分情况
        public const int HighEloFloorCap = 12;

        public static int GetMinWinDelta(McsrRuleFormat format, int ascent, int currentElo)
        {
            double ascentMult = GetAscentMultiplier(ascent);
            int baseMin = format == McsrRuleFormat.BiomeSprint ? 2 : 8;
            double scaled = baseMin * ascentMult;
            int result = format == McsrRuleFormat.BiomeSprint
                ? (int)Math.Floor(scaled)
                : (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
            if (currentElo >= HighEloFloorHalveThreshold)
            {
                result = Mathf.Min(result, HighEloFloorCap);
            }
            return Mathf.Max(1, result);
        }

        // 落败扣分上限 (负数): 单群系最多 -1, 其余最多 -2
        public static int GetMaxLossDelta(McsrRuleFormat format)
        {
            return format == McsrRuleFormat.BiomeSprint ? -1 : -2;
        }

        // 软折算公式：1000 + (原版ELO - 1000) * 0.5，限制在 850 ~ 1500 之间
        public static int ComputeSoftResetSeedElo(int vanillaElo)
        {
            int soft = (int)Math.Round(1000.0 + (vanillaElo - 1000.0) * 0.5);
            return Mathf.Clamp(soft, MinSeedElo, MaxSeedElo);
        }

        public static void Load(int defaultElo)
        {
            _currentElo = defaultElo;
            _peakElo = defaultElo;
            _seedElo = defaultElo;
            _seededFromVanilla = false;
            try
            {
                if (!File.Exists(SavePath))
                {
                    Save();
                }
                else
                {
                    LoadFromJson(defaultElo);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 读取本地 ELO 存档失败，使用默认分数: " + ex.Message);
            }

            HookVanillaRatingSeed();
        }

        private static void LoadFromJson(int defaultElo)
        {
            string json = File.ReadAllText(SavePath);
            int elo = ReadInt(json, "elo", defaultElo);
            int peak = ReadInt(json, "peakElo", elo);
            int seed = ReadInt(json, "seedElo", elo);
            int seededFlag = ReadInt(json, "seededFromVanilla", 0);
            int wins = ReadInt(json, "wins", 0);
            int losses = ReadInt(json, "losses", 0);
            int draws = ReadInt(json, "draws", 0);
            int streak = ReadInt(json, "streak", 0);

            int winsAllPeak = ReadInt(json, "winsAllPeak", 0);
            int lossesAllPeak = ReadInt(json, "lossesAllPeak", 0);
            int winsHardcore = ReadInt(json, "winsHardcore", 0);
            int lossesHardcore = ReadInt(json, "lossesHardcore", 0);
            int winsSprint = ReadInt(json, "winsSprint", 0);
            int lossesSprint = ReadInt(json, "lossesSprint", 0);
            int gamesAllPeak = ReadInt(json, "gamesAllPeak", 0);
            int gamesHardcore = ReadInt(json, "gamesHardcore", 0);
            int gamesSprint = ReadInt(json, "gamesSprint", 0);
            int favoriteMap = ReadInt(json, "favoriteMap", -1);
            int favoriteAscent = ReadInt(json, "favoriteAscent", -999);
            int favoriteFormat = ReadInt(json, "favoriteFormat", -1);

            string sig = ReadString(json, "sig");
            string expectedSig = ComputeSignature(elo, peak, wins, losses, draws,
                winsAllPeak + winsHardcore + winsSprint, lossesAllPeak + lossesHardcore + lossesSprint);

            if (!string.IsNullOrEmpty(sig) && !string.Equals(sig, expectedSig, StringComparison.OrdinalIgnoreCase))
            {
                Plugin.Log.LogWarning("[PeakCSR] 检测到本地 ELO 存档签名校验不一致，已重置为初始分数。");
                _currentElo = defaultElo;
                _peakElo = defaultElo;
                _seedElo = defaultElo;
                _seededFromVanilla = false;
                _wins = 0;
                _losses = 0;
                _draws = 0;
                _streak = 0;
                ClearModeStats();
                Save();
                return;
            }

            _currentElo = Mathf.Clamp(elo, 100, 5000);
            _peakElo = Mathf.Max(_currentElo, peak);
            _seedElo = Mathf.Clamp(seed, MinSeedElo, MaxSeedElo);
            _seededFromVanilla = seededFlag != 0;
            _wins = Mathf.Max(0, wins);
            _losses = Mathf.Max(0, losses);
            _draws = Mathf.Max(0, draws);
            _streak = streak;

            _winsAllPeak = Mathf.Max(0, winsAllPeak);
            _lossesAllPeak = Mathf.Max(0, lossesAllPeak);
            _winsHardcore = Mathf.Max(0, winsHardcore);
            _lossesHardcore = Mathf.Max(0, lossesHardcore);
            _winsSprint = Mathf.Max(0, winsSprint);
            _lossesSprint = Mathf.Max(0, lossesSprint);
            _gamesAllPeak = Mathf.Max(0, gamesAllPeak);
            _gamesHardcore = Mathf.Max(0, gamesHardcore);
            _gamesSprint = Mathf.Max(0, gamesSprint);
            // 旧存档里 favoriteMap 存的是地图槽位, 可能远大于群系数; 超范围一律当未设置
            _favoriteMap = (favoriteMap >= 0 && favoriteMap < PcsrBiomes.Count) ? favoriteMap : -1;
            _favoriteAscent = favoriteAscent;
            _favoriteFormat = favoriteFormat;

            ReadPreferenceStats(json);
        }

        // 从存档恢复"最常玩难度/模式"的场次统计
        private static void ReadPreferenceStats(string json)
        {
            Array.Clear(_ascentGames, 0, _ascentGames.Length);
            Array.Clear(_formatGames, 0, _formatGames.Length);

            for (int i = 0; i < _ascentGames.Length; i++)
            {
                _ascentGames[i] = Mathf.Max(0, ReadInt(json, "ascentGames_" + i, 0));
            }
            for (int i = 0; i < _formatGames.Length; i++)
            {
                _formatGames[i] = Mathf.Max(0, ReadInt(json, "formatGames_" + i, 0));
            }
        }

        private static void WritePreferenceStats(StringBuilder sb)
        {
            for (int i = 0; i < _ascentGames.Length; i++)
            {
                sb.AppendLine("  \"ascentGames_" + i + "\": " + _ascentGames[i] + ",");
            }
            for (int i = 0; i < _formatGames.Length; i++)
            {
                sb.AppendLine("  \"formatGames_" + i + "\": " + _formatGames[i] + ",");
            }
        }

        private static void ClearModeStats()
        {
            _winsAllPeak = 0;
            _lossesAllPeak = 0;
            _winsHardcore = 0;
            _lossesHardcore = 0;
            _winsSprint = 0;
            _lossesSprint = 0;
            _gamesAllPeak = 0;
            _gamesHardcore = 0;
            _gamesSprint = 0;
            _favoriteMap = -1;
            _favoriteAscent = -999;
            _favoriteFormat = -1;
            Array.Clear(_ascentGames, 0, _ascentGames.Length);
            Array.Clear(_formatGames, 0, _formatGames.Length);
        }

        private static void HookVanillaRatingSeed()
        {
            if (!_vanillaHooked)
            {
                _vanillaHooked = true;
                RatingService.RatingUpdated += OnVanillaRatingUpdated;
            }
            if (RatingService.Instance != null && RatingService.Instance.CurrentRating != null)
            {
                OnVanillaRatingUpdated(RatingService.Instance.CurrentRating);
            }
        }

        private static void OnVanillaRatingUpdated(PlayerRating rating)
        {
            if (_seededFromVanilla) return;
            if (_wins > 0 || _losses > 0 || _draws > 0) return;
            if (rating == null || !rating.Ranked || rating.Elo <= 0) return;
            if (string.Equals(rating.Tier, "UNRANKED", StringComparison.OrdinalIgnoreCase)) return;

            int softSeed = ComputeSoftResetSeedElo(rating.Elo);
            _seedElo = softSeed;
            _currentElo = softSeed;
            _peakElo = softSeed;
            _seededFromVanilla = true;
            Save();
            Plugin.Log.LogInfo("[PeakCSR] 已根据原版 RANKED 历史分数 (" + rating.Elo + " ELO) 软折算初始化 PCSR 种子分: " + softSeed + " ELO");
            if (OnRatingChanged != null) OnRatingChanged();
        }

        public static double EvaluateAntiBoostFactor(string opponentSteamId, int opponentElo, bool isRated, out string warningReason)
        {
            warningReason = null;
            if (!isRated)
            {
                warningReason = CsrLoc.T("antiboost.unrated");
                return 0.0;
            }

            string mySteamId = SteamIdentity.GetSteamId64();
            if (!string.IsNullOrEmpty(opponentSteamId) && opponentSteamId == mySteamId)
            {
                warningReason = CsrLoc.T("antiboost.self");
                return 0.0;
            }

            if (Mathf.Abs(_currentElo - opponentElo) > 650)
            {
                warningReason = CsrLoc.T("antiboost.gap");
                return 0.50;
            }

            if (string.IsNullOrEmpty(opponentSteamId))
            {
                return 1.0;
            }

            long nowMinutes = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            List<long> history;
            if (!_recentOpponents.TryGetValue(opponentSteamId, out history))
            {
                history = new List<long>();
                _recentOpponents[opponentSteamId] = history;
            }

            history.RemoveAll(t => nowMinutes - t > OpponentWindowMinutes);
            int recentCount = history.Count;
            history.Add(nowMinutes);

            if (recentCount >= MaxRecentMatchesAgainstSameOpponent)
            {
                warningReason = CsrLoc.T("antiboost.limit");
                return 0.0;
            }
            if (recentCount == 2)
            {
                warningReason = CsrLoc.T("antiboost.half");
                return 0.5;
            }
            return 1.0;
        }

        public static int GetBaseKFactor(int totalGames, int currentElo)
        {
            if (totalGames < PlacementMatchesCount) return 50;
            if (currentElo >= 1650) return 32;
            return 40;
        }

        // 计算攀爬完成度与高度差修正 (MCSR 理念：定级赛与常规赛根据爬升完成度与双方进度差给予补偿)
        public static int ComputeProgressModifier(
            double actualScore,
            float selfProgress01,
            float oppProgress01,
            McsrRuleFormat format,
            int ascent,
            bool isPlacement,
            double antiBoostWeight)
        {
            if (antiBoostWeight <= 0.01) return 0;

            float myP = Mathf.Clamp01(selfProgress01);
            float oppP = Mathf.Clamp01(oppProgress01);
            float diff = myP - oppP;

            double scale = format == McsrRuleFormat.BiomeSprint ? 0.35 : 1.0;
            if (ascent < 0) scale *= 0.75;

            double bonus = 0.0;
            if (actualScore > 0.75)
            {
                // 胜方：若大幅领先对手登顶 (如对手还在半山腰)，额外奖励最多 +4 分 (定级 +6)
                double leadMax = isPlacement ? 6.0 : 4.0;
                bonus = Math.Max(0.0, diff) * leadMax * scale;
            }
            else if (actualScore < 0.25)
            {
                // 败方补偿：定级期重点看完成度！爬到 80%~100% 惜败与 5% 暴毙天差地别
                // 1) 自身完成度减免：爬过 30% 后按完成度减免扣分，定级期最高可补回 +12 分，常规期最高 +6 分
                double completionReliefMax = isPlacement ? 12.0 : 6.0;
                double completionFactor = Mathf.Clamp01((myP - 0.30f) / 0.70f);
                double relief = completionFactor * completionReliefMax;

                // 2) 双方高度差修正：若败方惜败（进度差很小甚至领先时暴毙），少扣；若被完全碾压（落后 >60%）且定级期完不成 20%，略微多扣
                double gapTerm = diff * (isPlacement ? 6.0 : 3.5);
                bonus = (relief + gapTerm) * scale;
            }

            int rounded = (int)Math.Round(bonus * antiBoostWeight);
            int maxCap = isPlacement ? 14 : 7;
            return Mathf.Clamp(rounded, -maxCap, maxCap);
        }

        // 计算最终 ELO 变动
        //   weightFactor    : antiBoostWeight * modeMultiplier, 只作用于基础分 (不污染保底)
        //   antiBoostWeight : 仅用于判断是否应当施加保底/上限 (练习赛、自建小号、防刷分时不施加)
        //   minWinDelta     : GetMinWinDelta 预先算好的绝对保底下限
        //   maxLossDelta    : GetMaxLossDelta 预先算好的绝对扣分上限 (负数)
        public static int ComputeDelta(
            int myElo,
            int opponentElo,
            double actualScore,
            int kFactor,
            double weightFactor,
            double antiBoostWeight,
            int minWinDelta,
            int maxLossDelta,
            int progressBonus,
            out int baseDeltaOut)
        {
            double expected = 1.0 / (1.0 + Math.Pow(10.0, (opponentElo - myElo) / EloScaleDenominator));
            int rawBase = (int)Math.Round(kFactor * (actualScore - expected) * weightFactor);

            if (antiBoostWeight > 0.01)
            {
                if (actualScore > 0.75)
                {
                    rawBase = Math.Max(minWinDelta, rawBase);
                }
                else if (actualScore < 0.25 && rawBase >= 0)
                {
                    rawBase = -1;
                }
            }

            baseDeltaOut = rawBase;
            int total = rawBase + progressBonus;

            if (antiBoostWeight > 0.01)
            {
                if (actualScore > 0.75)
                {
                    // 保底是绝对下限: 进度补偿为负也不能低于它
                    total = Math.Max(minWinDelta, total);
                }
                else if (actualScore < 0.25)
                {
                    // 扣分上限是绝对上限: 无论基础分多低都不能超过它 (即不能扣得比上限更多)
                    total = Math.Max(maxLossDelta, Math.Min(0, total));
                }
            }

            return total;
        }

        public static EloSettlementDetail RecordMatchResultDetailed(
            string opponentSteamId,
            int opponentElo,
            double actualScore,
            McsrRuleFormat format,
            int ascent,
            bool isRated,
            float selfProgress01,
            float oppProgress01)
        {
            int oldElo = _currentElo;
            string oldTier = GetTierName(oldElo);
            string antiBoostNote;
            double antiBoostWeight = EvaluateAntiBoostFactor(opponentSteamId, opponentElo, isRated, out antiBoostNote);
            double modeMult = GetModeMultiplier(format, ascent);
            double totalWeight = antiBoostWeight * modeMult;

            int totalGames = _wins + _losses + _draws;
            bool isPlacement = totalGames < PlacementMatchesCount;
            int baseK = GetBaseKFactor(totalGames, _currentElo);

            int progressBonus = isRated
                ? ComputeProgressModifier(actualScore, selfProgress01, oppProgress01, format, ascent, isPlacement, antiBoostWeight)
                : 0;

            int minWinDelta = GetMinWinDelta(format, ascent, _currentElo);
            int maxLossDelta = GetMaxLossDelta(format);

            int baseDelta;
            int delta = isRated
                ? ComputeDelta(_currentElo, opponentElo, actualScore, baseK, totalWeight, antiBoostWeight,
                    minWinDelta, maxLossDelta, progressBonus, out baseDelta)
                : (baseDelta = 0);

            _currentElo = Mathf.Max(100, _currentElo + delta);
            if (_currentElo > _peakElo) _peakElo = _currentElo;

            if (isRated)
            {
                if (actualScore > 0.75)
                {
                    _wins++;
                    _streak = _streak >= 0 ? _streak + 1 : 1;
                    BumpModeStat(format, true);
                }
                else if (actualScore < 0.25)
                {
                    _losses++;
                    _streak = _streak <= 0 ? _streak - 1 : -1;
                    BumpModeStat(format, false);
                }
                else
                {
                    _draws++;
                    _streak = 0;
                    BumpModeGameOnly(format);
                }
                // 最常玩难度/模式完全由真实对局推导, 不需要玩家手选
                BumpPreferenceStats(format, ascent);
            }

            Save();
            if (OnRatingChanged != null) OnRatingChanged();

            return new EloSettlementDetail
            {
                OldElo = oldElo,
                NewElo = _currentElo,
                TotalDelta = delta,
                BaseDelta = baseDelta,
                ProgressBonus = progressBonus,
                FloorAdjust = delta - baseDelta - progressBonus,
                MinWinDelta = minWinDelta,
                ModeMultiplier = modeMult,
                AntiBoostWeight = antiBoostWeight,
                IsPlacement = isPlacement,
                OldTierFull = oldTier,
                NewTierFull = GetTierName(_currentElo),
                AntiBoostNote = antiBoostNote,
                Void = false
            };
        }

        // 断线 / 无法确认结果的作废局: 不改动 ELO, 也不计入胜负场次
        public static EloSettlementDetail RecordVoid(
            McsrRuleFormat format,
            int ascent,
            string reason)
        {
            return new EloSettlementDetail
            {
                OldElo = _currentElo,
                NewElo = _currentElo,
                TotalDelta = 0,
                BaseDelta = 0,
                ProgressBonus = 0,
                FloorAdjust = 0,
                MinWinDelta = GetMinWinDelta(format, ascent, _currentElo),
                ModeMultiplier = GetModeMultiplier(format, ascent),
                AntiBoostWeight = 1.0,
                IsPlacement = TotalGames < PlacementMatchesCount,
                OldTierFull = GetTierName(_currentElo),
                NewTierFull = GetTierName(_currentElo),
                AntiBoostNote = reason,
                Void = true
            };
        }

        private static void BumpModeStat(McsrRuleFormat format, bool won)
        {
            if (format == McsrRuleFormat.BiomeSprint)
            {
                if (won) _winsSprint++; else _lossesSprint++;
                _gamesSprint++;
            }
            else if (format == McsrRuleFormat.HardcoreAscent4)
            {
                if (won) _winsHardcore++; else _lossesHardcore++;
                _gamesHardcore++;
            }
            else
            {
                if (won) _winsAllPeak++; else _lossesAllPeak++;
                _gamesAllPeak++;
            }
        }

        private static void BumpModeGameOnly(McsrRuleFormat format)
        {
            if (format == McsrRuleFormat.BiomeSprint) _gamesSprint++;
            else if (format == McsrRuleFormat.HardcoreAscent4) _gamesHardcore++;
            else _gamesAllPeak++;
        }

        // 每打完一局计一次, 用于推导"最常玩难度"和"最常玩模式"
        private static void BumpPreferenceStats(McsrRuleFormat format, int ascent)
        {
            int ascentIdx = Mathf.Clamp(ascent, -1, 8) + 1;
            if (ascentIdx >= 0 && ascentIdx < _ascentGames.Length)
            {
                _ascentGames[ascentIdx]++;
            }
            int fmtIdx = Mathf.Clamp((int)format, 0, 2);
            _formatGames[fmtIdx]++;
        }

        public static int RecordMatchResult(string opponentSteamId, int opponentElo, double actualScore, McsrRuleFormat format, int ascent, bool isRated, out string antiBoostNote)
        {
            EloSettlementDetail detail = RecordMatchResultDetailed(opponentSteamId, opponentElo, actualScore, format, ascent, isRated, actualScore > 0.5 ? 1f : 0f, actualScore > 0.5 ? 0f : 1f);
            antiBoostNote = detail.AntiBoostNote;
            return detail.TotalDelta;
        }

        public static void ApplyCloudAuthoritativeElo(int cloudElo)
        {
            _currentElo = Mathf.Clamp(cloudElo, 100, 5000);
            if (_currentElo > _peakElo) _peakElo = _currentElo;
            Save();
            if (OnRatingChanged != null) OnRatingChanged();
        }

        public static void ApplyCloudAuthoritativeState(int elo, int peak, int wins, int losses, int streak)
        {
            ApplyCloudAuthoritativeState(elo, peak, wins, losses, streak,
                _winsAllPeak, _lossesAllPeak, _winsHardcore, _lossesHardcore, _winsSprint, _lossesSprint,
                _favoriteMap, _favoriteAscent, _favoriteFormat);
        }

        // 云端权威档案覆盖 (含分赛制战绩)
        public static void ApplyCloudAuthoritativeState(
            int elo, int peak, int wins, int losses, int streak,
            int winsAllPeak, int lossesAllPeak,
            int winsHardcore, int lossesHardcore,
            int winsSprint, int lossesSprint,
            int favoriteMap, int favoriteAscent, int favoriteFormat)
        {
            _currentElo = Mathf.Clamp(elo, 100, 5000);
            _peakElo = Mathf.Max(_currentElo, peak);
            _wins = Mathf.Max(0, wins);
            _losses = Mathf.Max(0, losses);
            _streak = streak;

            _winsAllPeak = Mathf.Max(0, winsAllPeak);
            _lossesAllPeak = Mathf.Max(0, lossesAllPeak);
            _winsHardcore = Mathf.Max(0, winsHardcore);
            _lossesHardcore = Mathf.Max(0, lossesHardcore);
            _winsSprint = Mathf.Max(0, winsSprint);
            _lossesSprint = Mathf.Max(0, lossesSprint);
            _gamesAllPeak = _winsAllPeak + _lossesAllPeak;
            _gamesHardcore = _winsHardcore + _lossesHardcore;
            _gamesSprint = _winsSprint + _lossesSprint;

            if (favoriteMap >= 0 && favoriteMap < PcsrBiomes.Count) _favoriteMap = favoriteMap;
            if (favoriteAscent > -900) _favoriteAscent = favoriteAscent;
            if (favoriteFormat >= 0) _favoriteFormat = favoriteFormat;

            // 云端同样会把推导用的统计列带回来, 但场次口径以本地为准, 这里只兜底灌入
            if (_ascentGames[Mathf.Clamp(favoriteAscent, -1, 8) + 1] == 0 && favoriteAscent > -900)
            {
                _ascentGames[Mathf.Clamp(favoriteAscent, -1, 8) + 1] = 1;
            }
            if (favoriteFormat >= 0 && favoriteFormat <= 2 && _formatGames[favoriteFormat] == 0)
            {
                _formatGames[favoriteFormat] = 1;
            }

            Save();
            if (OnRatingChanged != null) OnRatingChanged();
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(SaveDir);
                string sig = ComputeSignature(_currentElo, _peakElo, _wins, _losses, _draws,
                    _winsAllPeak + _winsHardcore + _winsSprint,
                    _lossesAllPeak + _lossesHardcore + _lossesSprint);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"elo\": " + _currentElo + ",");
                sb.AppendLine("  \"peakElo\": " + _peakElo + ",");
                sb.AppendLine("  \"seedElo\": " + _seedElo + ",");
                sb.AppendLine("  \"seededFromVanilla\": " + (_seededFromVanilla ? 1 : 0) + ",");
                sb.AppendLine("  \"wins\": " + _wins + ",");
                sb.AppendLine("  \"losses\": " + _losses + ",");
                sb.AppendLine("  \"draws\": " + _draws + ",");
                sb.AppendLine("  \"streak\": " + _streak + ",");
                sb.AppendLine("  \"winsAllPeak\": " + _winsAllPeak + ",");
                sb.AppendLine("  \"lossesAllPeak\": " + _lossesAllPeak + ",");
                sb.AppendLine("  \"winsHardcore\": " + _winsHardcore + ",");
                sb.AppendLine("  \"lossesHardcore\": " + _lossesHardcore + ",");
                sb.AppendLine("  \"winsSprint\": " + _winsSprint + ",");
                sb.AppendLine("  \"lossesSprint\": " + _lossesSprint + ",");
                sb.AppendLine("  \"gamesAllPeak\": " + _gamesAllPeak + ",");
                sb.AppendLine("  \"gamesHardcore\": " + _gamesHardcore + ",");
                sb.AppendLine("  \"gamesSprint\": " + _gamesSprint + ",");
                sb.AppendLine("  \"favoriteMap\": " + _favoriteMap + ",");
                sb.AppendLine("  \"favoriteAscent\": " + _favoriteAscent + ",");
                sb.AppendLine("  \"favoriteFormat\": " + _favoriteFormat + ",");
                WritePreferenceStats(sb);
                sb.AppendLine("  \"sig\": \"" + sig + "\",");
                sb.AppendLine("  \"updatedUtc\": \"" + DateTime.UtcNow.ToString("O") + "\"");
                sb.Append("}");
                File.WriteAllText(SavePath, sb.ToString());
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[PeakCSR] 保存本地 ELO 存档失败: " + ex.Message);
            }
        }

        private static string ComputeSignature(int elo, int peak, int wins, int losses, int draws, int modeWins, int modeLosses)
        {
            string steamId = "LOCAL";
            try { steamId = SteamIdentity.GetSteamId64() ?? "LOCAL"; } catch { }
            string raw = steamId + ":" + elo + ":" + peak + ":" + wins + ":" + losses + ":" + draws
                + ":" + modeWins + ":" + modeLosses + ":" + IntegritySalt;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static int ReadInt(string json, string key, int fallback)
        {
            string pattern = "\"" + key + "\"\\s*:\\s*(-?\\d+)";
            Match m = Regex.Match(json, pattern);
            int val;
            if (m.Success && int.TryParse(m.Groups[1].Value, out val))
            {
                return val;
            }
            return fallback;
        }

        private static string ReadString(string json, string key)
        {
            string pattern = "\"" + key + "\"\\s*:\\s*\"([^\"]*)\"";
            Match m = Regex.Match(json, pattern);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
