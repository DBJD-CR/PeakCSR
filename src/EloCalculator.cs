using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using BepInEx;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    public static class EloCalculator
    {
        private static readonly string SaveDir = Path.Combine(Paths.PluginPath, "PeakCSR");
        private static readonly string SavePath = Path.Combine(SaveDir, "elo_profile.json");
        private const string IntegritySalt = "PEAKCSR_1V1_SALT_2026";
        private const int MaxRecentMatchesAgainstSameOpponent = 3;
        private const long OpponentWindowMinutes = 30;

        private static int _currentElo = 1000;
        private static int _peakElo = 1000;
        private static int _wins;
        private static int _losses;
        private static int _draws;
        private static int _streak;

        private static readonly Dictionary<string, List<long>> _recentOpponents = new Dictionary<string, List<long>>();

        public static int CurrentElo { get { return _currentElo; } }
        public static int PeakElo { get { return _peakElo; } }
        public static int Wins { get { return _wins; } }
        public static int Losses { get { return _losses; } }
        public static int Draws { get { return _draws; } }
        public static int Streak { get { return _streak; } }

        public static event Action OnRatingChanged;

        // 获取段位多语言名称
        public static string GetTierName(int elo)
        {
            if (elo >= 1800) return CsrLoc.T("tier.sherpa");
            if (elo >= 1550) return CsrLoc.T("tier.alpinist");
            if (elo >= 1350) return CsrLoc.T("tier.climber");
            if (elo >= 1150) return CsrLoc.T("tier.hiker");
            if (elo >= 950) return CsrLoc.T("tier.scout");
            return CsrLoc.T("tier.tenderfoot");
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

        public static string GetTierHexColor(int elo)
        {
            if (elo >= 1800) return "33E6FF";
            if (elo >= 1550) return "FFD933";
            if (elo >= 1350) return "55E673";
            if (elo >= 1150) return "E0944D";
            if (elo >= 950) return "D9E0EB";
            return "999999";
        }

        // 天阶 0~8 对应的手工定制难度倍率表
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

        // 计算模式与天阶难度 ELO 倍率：全山顶峰(A0)为基准 1.00x，天阶 1~8 按定制表递增，单群系冲刺乘 0.22x（约 3~5 分）
        public static double GetModeMultiplier(McsrRuleFormat format, int ascent)
        {
            int clampedAscent = Mathf.Clamp(ascent, 0, 8);
            double ascentMult = AscentMultipliers[clampedAscent];
            if (format == McsrRuleFormat.BiomeSprint)
            {
                return Math.Round(0.22 * ascentMult, 2);
            }
            return ascentMult;
        }

        public static void Load(int defaultElo)
        {
            _currentElo = defaultElo;
            _peakElo = defaultElo;
            try
            {
                if (!File.Exists(SavePath))
                {
                    Save();
                    return;
                }

                string json = File.ReadAllText(SavePath);
                int elo = ReadInt(json, "elo", defaultElo);
                int peak = ReadInt(json, "peakElo", elo);
                int wins = ReadInt(json, "wins", 0);
                int losses = ReadInt(json, "losses", 0);
                int draws = ReadInt(json, "draws", 0);
                int streak = ReadInt(json, "streak", 0);
                string sig = ReadString(json, "sig");

                string expectedSig = ComputeSignature(elo, peak, wins, losses, draws);
                if (!string.IsNullOrEmpty(sig) && !string.Equals(sig, expectedSig, StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.Log.LogWarning("[PeakCSR] 检测到本地 ELO 存档签名校验不一致，已重置为初始分数。");
                    _currentElo = defaultElo;
                    _peakElo = defaultElo;
                    _wins = 0;
                    _losses = 0;
                    _draws = 0;
                    _streak = 0;
                    Save();
                    return;
                }

                _currentElo = Mathf.Clamp(elo, 100, 5000);
                _peakElo = Mathf.Max(_currentElo, peak);
                _wins = Mathf.Max(0, wins);
                _losses = Mathf.Max(0, losses);
                _draws = Mathf.Max(0, draws);
                _streak = streak;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 读取本地 ELO 存档失败，使用默认分数: " + ex.Message);
            }
        }

        // 校验是否存在短时间同对手刷分行为，返回衰减系数 (0.0 ~ 1.0)
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
                return 0.35;
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

        public static int ComputeDelta(int myElo, int opponentElo, double actualScore, int kFactor, double weightFactor)
        {
            double expected = 1.0 / (1.0 + Math.Pow(10.0, (opponentElo - myElo) / 400.0));
            int raw = (int)Math.Round(kFactor * (actualScore - expected) * weightFactor);
            if (weightFactor > 0.01 && raw == 0)
            {
                raw = actualScore >= 0.5 ? 1 : -1;
            }
            return raw;
        }

        public static int RecordMatchResult(string opponentSteamId, int opponentElo, double actualScore, McsrRuleFormat format, int ascent, bool isRated, out string antiBoostNote)
        {
            double antiBoostWeight = EvaluateAntiBoostFactor(opponentSteamId, opponentElo, isRated, out antiBoostNote);
            double modeMult = GetModeMultiplier(format, ascent);
            double totalWeight = antiBoostWeight * modeMult;

            // 全图默认基准 K=36（平局分差下胜一场约 +18 分；单群系 0.25x 约 +4~5 分；高难天阶最高可达 +29 分）
            int totalGames = _wins + _losses + _draws;
            int baseK = totalGames < 10 ? 40 : (_currentElo >= 1650 ? 28 : 36);

            int delta = ComputeDelta(_currentElo, opponentElo, actualScore, baseK, totalWeight);
            _currentElo = Mathf.Max(100, _currentElo + delta);
            if (_currentElo > _peakElo) _peakElo = _currentElo;

            if (isRated)
            {
                if (actualScore > 0.75)
                {
                    _wins++;
                    _streak = _streak >= 0 ? _streak + 1 : 1;
                }
                else if (actualScore < 0.25)
                {
                    _losses++;
                    _streak = _streak <= 0 ? _streak - 1 : -1;
                }
                else
                {
                    _draws++;
                    _streak = 0;
                }
            }

            Save();
            if (OnRatingChanged != null) OnRatingChanged();
            return delta;
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
            _currentElo = Mathf.Clamp(elo, 100, 5000);
            _peakElo = Mathf.Max(_currentElo, peak);
            _wins = Mathf.Max(0, wins);
            _losses = Mathf.Max(0, losses);
            _streak = streak;
            Save();
            if (OnRatingChanged != null) OnRatingChanged();
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(SaveDir);
                string sig = ComputeSignature(_currentElo, _peakElo, _wins, _losses, _draws);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"elo\": " + _currentElo + ",");
                sb.AppendLine("  \"peakElo\": " + _peakElo + ",");
                sb.AppendLine("  \"wins\": " + _wins + ",");
                sb.AppendLine("  \"losses\": " + _losses + ",");
                sb.AppendLine("  \"draws\": " + _draws + ",");
                sb.AppendLine("  \"streak\": " + _streak + ",");
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

        private static string ComputeSignature(int elo, int peak, int wins, int losses, int draws)
        {
            string steamId = "LOCAL";
            try { steamId = SteamIdentity.GetSteamId64() ?? "LOCAL"; } catch { }
            string raw = steamId + ":" + elo + ":" + peak + ":" + wins + ":" + losses + ":" + draws + ":" + IntegritySalt;
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