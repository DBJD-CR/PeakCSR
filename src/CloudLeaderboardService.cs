using System;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using RankedPeak.Services;
using UnityEngine;
using UnityEngine.Networking;

namespace PeakCSR
{
    public class CloudRankEntry
    {
        public int Rank;
        public string SteamId;
        public string PlayerName;
        // 玩家自定的 PCSR 昵称, 为空时回退到 Steam 昵称
        public string Nickname = "";
        public int Elo;
        public int Wins;
        public int Losses;
        public int Streak;
        // 分赛制战绩: 0 = 全山, 1 = 高难天阶, 2 = 单群系
        public int WinsAllPeak;
        public int LossesAllPeak;
        public int WinsHardcore;
        public int LossesHardcore;
        public int WinsSprint;
        public int LossesSprint;
        public int GamesAllPeak;
        public int GamesHardcore;
        public int GamesSprint;
        public int FavoriteMap = -1;
        public int FavoriteAscent = -999;
        public int FavoriteFormat = -1;

        public string DisplayName
        {
            get
            {
                string nick = PlayerNameSanitizer.Clean(Nickname);
                if (!string.IsNullOrEmpty(nick)) return nick;
                string steam = PlayerNameSanitizer.Clean(PlayerName);
                return string.IsNullOrEmpty(steam) ? "" : steam;
            }
        }
    }

    // 云端个人档案
    public class CloudProfile
    {
        public string SteamId = "";
        public string PlayerName = "";
        public string Nickname = "";
        public int Elo = 1000;
        public int PeakElo = 1000;
        public int Wins;
        public int Losses;
        public int Draws;
        public int Streak;
        public int WinsAllPeak;
        public int LossesAllPeak;
        public int WinsHardcore;
        public int LossesHardcore;
        public int WinsSprint;
        public int LossesSprint;
        public int GamesAllPeak;
        public int GamesHardcore;
        public int GamesSprint;
        public int FavoriteMap = -1;
        public int FavoriteAscent = -999;
        public int FavoriteFormat = -1;
        public int GlobalRank;
        public bool Loaded;

        public string DisplayName
        {
            get
            {
                string nick = PlayerNameSanitizer.Clean(Nickname);
                if (!string.IsNullOrEmpty(nick)) return nick;
                string steam = PlayerNameSanitizer.Clean(PlayerName);
                return string.IsNullOrEmpty(steam) ? "" : steam;
            }
        }

        public int TotalGames { get { return Wins + Losses + Draws; } }
        public int WinRatePercent
        {
            get
            {
                int total = Wins + Losses;
                return total > 0 ? Mathf.RoundToInt(Wins * 100f / total) : 0;
            }
        }
        public int ModeWinRatePercent(int format)
        {
            int w = format == 2 ? WinsSprint : (format == 1 ? WinsHardcore : WinsAllPeak);
            int l = format == 2 ? LossesSprint : (format == 1 ? LossesHardcore : LossesAllPeak);
            return (w + l) > 0 ? Mathf.RoundToInt(w * 100f / (w + l)) : 0;
        }
    }

    // 云端历史战绩行
    public class CsrHistoryEntry
    {
        public string MatchKey = "";
        public string InviteCode = "";
        public int Seed;
        public bool Won;
        public bool IsVoid;
        public string OpponentSteamId = "";
        public string OpponentName = "";
        public int Delta;
        public int EloBefore;
        public int EloAfter;
        public int LevelIndex;
        public string BiomeLabel = "";
        public int Ascent;
        public int Format;
        public long DurationMs;
        public float MyProgress;
        public float OppProgress;
        public long SettledAtMs;
    }

    // 基于 Cloudflare Worker + D1 的全服天梯同步与双端签名上报服务
    public class CloudLeaderboardService : MonoBehaviour
    {
        private const string SharedCloudSalt = "PEAKCSR_CLOUD_V1_2026";
        private const string DefaultWorkerUrl = "https://peakcsr-ladder.3405689090.workers.dev";
        // 与 PcsrMenuWindow 的天梯行数保持一致
        private const int LeaderboardFetchLimit = 8;
        public static CloudLeaderboardService Instance { get; private set; }

        private readonly List<CloudRankEntry> _entries = new List<CloudRankEntry>();
        private bool _isLoading;
        private string _cloudStatus = "";
        private long _lastRefreshedAtMs;

        private readonly List<CsrHistoryEntry> _history = new List<CsrHistoryEntry>();
        private bool _historyLoading;
        private CloudProfile _myProfile = new CloudProfile();
        private readonly Dictionary<string, CloudProfile> _profileCache = new Dictionary<string, CloudProfile>();

        public IReadOnlyList<CloudRankEntry> Entries { get { return _entries; } }
        public bool IsLoading { get { return _isLoading; } }
        public string CloudStatus { get { return _cloudStatus; } }
        public long LastRefreshedAtMs { get { return _lastRefreshedAtMs; } }
        public IReadOnlyList<CsrHistoryEntry> History { get { return _history; } }
        public bool HistoryLoading { get { return _historyLoading; } }
        public CloudProfile MyProfile { get { return _myProfile; } }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            RefreshLeaderboard();
            SyncMyCloudProfile();
        }

        public static bool IsCloudConfigured()
        {
            string url = BaseUrl();
            return !string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
        }

        // UnityWebRequest.result 是枚举, 不要硬编码魔法数字
        private static bool HasRequestFailed(UnityWebRequest req)
        {
            return req.result != UnityWebRequest.Result.Success;
        }

        private static string BaseUrl()
        {
            string raw = (Plugin.Instance != null && Plugin.Instance.CloudApiUrl != null) ? (Plugin.Instance.CloudApiUrl.Value ?? "").Trim() : "";
            if (string.IsNullOrEmpty(raw))
            {
                raw = DefaultWorkerUrl;
                if (Plugin.Instance != null && Plugin.Instance.CloudApiUrl != null)
                {
                    Plugin.Instance.CloudApiUrl.Value = DefaultWorkerUrl;
                }
            }
            int apiIdx = raw.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            if (apiIdx > 0)
            {
                raw = raw.Substring(0, apiIdx);
            }
            return raw.TrimEnd('/');
        }

        // 最近刷新时间, 统一按 UTC+8 (北京时间) 显示, 24 小时制 HH:mm:ss
        // 不用主机本地时区: 不同玩家时区不同会导致看板时间对不上, 云端的 refreshedAt 也统一是 UTC 毫秒
        private static readonly TimeSpan RefreshClockOffset = TimeSpan.FromHours(8);

        public static string FormatRefreshClock(long utcMs)
        {
            if (utcMs <= 0) return "--:--:--";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToOffset(RefreshClockOffset).ToString("HH:mm:ss");
            }
            catch
            {
                return "--:--:--";
            }
        }

        public void RefreshLeaderboard()
        {
            if (!IsCloudConfigured())
            {
                _cloudStatus = CsrLoc.T("cloud.unconfigured");
                return;
            }
            // 防止连点刷新导致多个协程并发覆盖 _entries
            if (_isLoading) return;
            StartCoroutine(FetchLeaderboardRoutine());
        }

        public void SyncMyCloudProfile()
        {
            if (!IsCloudConfigured()) return;
            StartCoroutine(FetchMyProfileRoutine());
        }

        // 拉取自己的云端历史战绩
        public void RefreshHistory(bool force = false)
        {
            if (!IsCloudConfigured()) return;
            if (_historyLoading) return;
            if (!force && _history.Count > 0) return;
            StartCoroutine(FetchHistoryRoutine());
        }

        // 拉取任意玩家档案 (天梯行点选查看)
        public void RequestProfile(string steamId, Action<CloudProfile> onDone)
        {
            if (string.IsNullOrEmpty(steamId))
            {
                if (onDone != null) onDone(null);
                return;
            }
            CloudProfile cached;
            if (_profileCache.TryGetValue(steamId, out cached) && cached != null)
            {
                if (onDone != null) onDone(cached);
                return;
            }
            if (!IsCloudConfigured())
            {
                if (onDone != null) onDone(null);
                return;
            }
            StartCoroutine(FetchProfileRoutine(steamId, onDone));
        }

        public CloudProfile CachedProfile(string steamId)
        {
            CloudProfile p;
            if (!string.IsNullOrEmpty(steamId) && _profileCache.TryGetValue(steamId, out p)) return p;
            return null;
        }

        // 上传玩家自选的偏好档案 (保留昵称不变, null 表示沿用云端已有的)
        public void PushFavoriteProfile(int favoriteMap, int favoriteAscent, int favoriteFormat)
        {
            PushProfile(favoriteMap, favoriteAscent, favoriteFormat, null, null);
        }

        // 修改 PCSR 昵称, 空串 = 沿用 Steam 昵称
        public void PushNickname(string nickname, int favoriteMap, int favoriteAscent, int favoriteFormat, Action<bool, string> onDone)
        {
            PushProfile(favoriteMap, favoriteAscent, favoriteFormat, nickname, onDone);
        }

        // nickname 传 null 表示本次不改昵称(用云端已有值), 传 "" 表示清空回退到 Steam 昵称
        public void PushProfile(int favoriteMap, int favoriteAscent, int favoriteFormat, string nickname, Action<bool, string> onDone)
        {
            if (!IsCloudConfigured())
            {
                if (onDone != null) onDone(false, "cloud_unconfigured");
                return;
            }
            string effectiveNick = nickname;
            if (effectiveNick == null)
            {
                // 不改昵称时必须带云端现值, 否则签名与 UPDATE 会把昵称写空
                effectiveNick = _myProfile != null ? (_myProfile.Nickname ?? "") : "";
            }
            StartCoroutine(PushProfileRoutine(favoriteMap, favoriteAscent, favoriteFormat, effectiveNick, onDone));
        }

        // 自己的档案卡入口: 云端没有档案时也要能打开(用于预先起昵称)
        public void RequestMyProfile(Action<CloudProfile> onDone)
        {
            string steamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(steamId))
            {
                if (onDone != null) onDone(null);
                return;
            }
            if (!IsCloudConfigured())
            {
                CloudProfile local = new CloudProfile
                {
                    SteamId = steamId,
                    PlayerName = PlayerNameSanitizer.Current("") ?? "",
                    Elo = EloCalculator.CurrentElo,
                    PeakElo = EloCalculator.PeakElo,
                    Wins = EloCalculator.Wins,
                    Losses = EloCalculator.Losses,
                    Streak = EloCalculator.Streak,
                    FavoriteMap = EloCalculator.FavoriteMap,
                    FavoriteAscent = EloCalculator.FavoriteAscent,
                    FavoriteFormat = EloCalculator.FavoriteFormat,
                    Loaded = false
                };
                if (onDone != null) onDone(local);
                return;
            }
            StartCoroutine(FetchProfileRoutine(steamId, p =>
            {
                if (p != null)
                {
                    if (onDone != null) onDone(p);
                    return;
                }
                // 云端还没有记录: 造一个本地骨架供界面展示, 保存昵称时服务端会自动建档
                CloudProfile local = new CloudProfile
                {
                    SteamId = steamId,
                    PlayerName = PlayerNameSanitizer.Current("") ?? "",
                    Elo = EloCalculator.CurrentElo,
                    PeakElo = EloCalculator.PeakElo,
                    Wins = EloCalculator.Wins,
                    Losses = EloCalculator.Losses,
                    Streak = EloCalculator.Streak,
                    FavoriteMap = EloCalculator.FavoriteMap,
                    FavoriteAscent = EloCalculator.FavoriteAscent,
                    FavoriteFormat = EloCalculator.FavoriteFormat,
                    Loaded = false
                };
                _profileCache[steamId] = local;
                if (onDone != null) onDone(local);
            }));
        }

        public void SubmitSignedMatchReport(
            string inviteCode,
            string opponentSteamId,
            string opponentName,
            int opponentElo,
            string winnerSteamId,
            int seed,
            int levelIndex,
            int ascent,
            McsrRuleFormat format,
            long durationMs,
            float myProgress01,
            float oppProgress01)
        {
            SubmitSignedMatchReport(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                seed, levelIndex, ascent, format, durationMs, myProgress01, oppProgress01, false, "");
        }

        // isVoid = true 时上报作废局 (断线无法确认结果), 双方都不加减分
        public void SubmitSignedMatchReport(
            string inviteCode,
            string opponentSteamId,
            string opponentName,
            int opponentElo,
            string winnerSteamId,
            int seed,
            int levelIndex,
            int ascent,
            McsrRuleFormat format,
            long durationMs,
            float myProgress01,
            float oppProgress01,
            bool isVoid,
            string voidReason)
        {
            if (!IsCloudConfigured() || string.IsNullOrEmpty(inviteCode) || string.IsNullOrEmpty(opponentSteamId)) return;
            StartCoroutine(ReportMatchRoutine(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                seed, levelIndex, ascent, (int)format, durationMs, myProgress01, oppProgress01, isVoid, voidReason));
        }

        private IEnumerator FetchMyProfileRoutine()
        {
            string steamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(steamId)) yield break;

            string url = BaseUrl() + "/api/profile?steamId=" + UnityWebRequest.EscapeURL(steamId);
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                if (HasRequestFailed(req)) yield break;
                try
                {
                    JObject root = JObject.Parse(req.downloadHandler.text);
                    JToken prof = root["profile"];
                    if (prof != null && prof.Type == JTokenType.Object)
                    {
                        CloudProfile p = ParseProfile(prof);
                        p.GlobalRank = root["rank"] != null && root["rank"].Type != JTokenType.Null
                            ? root["rank"].Value<int>() : 0;
                        p.Loaded = true;
                        _myProfile = p;
                        _profileCache[steamId] = p;

                        EloCalculator.ApplyCloudAuthoritativeState(
                            p.Elo, p.PeakElo, p.Wins, p.Losses, p.Streak,
                            p.WinsAllPeak, p.LossesAllPeak,
                            p.WinsHardcore, p.LossesHardcore,
                            p.WinsSprint, p.LossesSprint,
                            p.FavoriteMap, p.FavoriteAscent, p.FavoriteFormat);
                        Plugin.Log.LogInfo("[PeakCSR] 已从云端天梯同步权威档案: " + p.Elo + " ELO");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[PeakCSR] 解析云端个人档案失败: " + ex.Message);
                }
            }
        }

        private IEnumerator FetchProfileRoutine(string steamId, Action<CloudProfile> onDone)
        {
            CloudProfile result = null;
            string url = BaseUrl() + "/api/profile?steamId=" + UnityWebRequest.EscapeURL(steamId);
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                if (!HasRequestFailed(req))
                {
                    try
                    {
                        JObject root = JObject.Parse(req.downloadHandler.text);
                        JToken prof = root["profile"];
                        if (prof != null && prof.Type == JTokenType.Object)
                        {
                            result = ParseProfile(prof);
                            result.GlobalRank = root["rank"] != null && root["rank"].Type != JTokenType.Null
                                ? root["rank"].Value<int>() : 0;
                            result.Loaded = true;
                            _profileCache[steamId] = result;
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("[PeakCSR] 解析玩家档案失败: " + ex.Message);
                    }
                }
            }
            if (onDone != null) onDone(result);
        }

        private IEnumerator FetchLeaderboardRoutine()
        {
            _isLoading = true;
            _cloudStatus = CsrLoc.T("cloud.loading");
            string url = BaseUrl() + "/api/leaderboard?limit=" + LeaderboardFetchLimit;
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                _isLoading = false;
                if (HasRequestFailed(req))
                {
                    _cloudStatus = CsrLoc.T("cloud.error", req.error);
                    yield break;
                }
                try
                {
                    JObject root = JObject.Parse(req.downloadHandler.text);
                    if (root["refreshedAt"] != null && root["refreshedAt"].Type != JTokenType.Null)
                    {
                        _lastRefreshedAtMs = root["refreshedAt"].Value<long>();
                    }
                    else
                    {
                        _lastRefreshedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    }

                    JArray arr = root["entries"] as JArray;
                    _entries.Clear();
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Count; i++)
                        {
                            JToken item = arr[i];
                            _entries.Add(new CloudRankEntry
                            {
                                Rank = i + 1,
                                SteamId = Str(item, "steam_id"),
                                PlayerName = Str(item, "player_name", "Player"),
                                Nickname = Str(item, "nickname", ""),
                                Elo = Int(item, "elo", 1000),
                                Wins = Int(item, "wins", 0),
                                Losses = Int(item, "losses", 0),
                                Streak = Int(item, "streak", 0),
                                WinsAllPeak = Int(item, "wins_all_peak", 0),
                                LossesAllPeak = Int(item, "losses_all_peak", 0),
                                WinsHardcore = Int(item, "wins_hardcore", 0),
                                LossesHardcore = Int(item, "losses_hardcore", 0),
                                WinsSprint = Int(item, "wins_sprint", 0),
                                LossesSprint = Int(item, "losses_sprint", 0),
                                GamesAllPeak = Int(item, "games_all_peak", 0),
                                GamesHardcore = Int(item, "games_hardcore", 0),
                                GamesSprint = Int(item, "games_sprint", 0),
                                FavoriteMap = Int(item, "favorite_map", -1),
                                FavoriteAscent = Int(item, "favorite_ascent", -999),
                                FavoriteFormat = Int(item, "favorite_format", -1)
                            });
                        }
                    }
                    _cloudStatus = CsrLoc.T("cloud.updated", _entries.Count);
                }
                catch (Exception ex)
                {
                    _cloudStatus = CsrLoc.T("cloud.error", ex.Message);
                }
            }
        }

        private IEnumerator FetchHistoryRoutine()
        {
            string steamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(steamId)) yield break;
            _historyLoading = true;
            string url = BaseUrl() + "/api/history?limit=20&steamId=" + UnityWebRequest.EscapeURL(steamId);
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                _historyLoading = false;
                if (HasRequestFailed(req)) yield break;
                try
                {
                    JObject root = JObject.Parse(req.downloadHandler.text);
                    JArray arr = root["history"] as JArray;
                    _history.Clear();
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Count; i++)
                        {
                            JToken item = arr[i];
                            CsrHistoryEntry e = new CsrHistoryEntry
                            {
                                MatchKey = Str(item, "matchKey"),
                                InviteCode = Str(item, "inviteCode"),
                                Seed = Int(item, "seed", 0),
                                Won = Bool(item, "won"),
                                IsVoid = Bool(item, "isVoid"),
                                OpponentSteamId = Str(item, "opponentSteamId"),
                                Delta = Int(item, "delta", 0),
                                EloBefore = Int(item, "eloBefore", 0),
                                EloAfter = Int(item, "eloAfter", 0),
                                LevelIndex = Int(item, "levelIndex", 0),
                                Ascent = Int(item, "ascent", 0),
                                Format = Int(item, "format", 0),
                                DurationMs = Long(item, "durationMs", 0),
                                MyProgress = Float(item, "myProgress", 0f),
                                OppProgress = Float(item, "oppProgress", 0f),
                                SettledAtMs = Long(item, "settledAt", 0)
                            };
                            try { e.BiomeLabel = MapCatalog.GetBiomeLabel(e.LevelIndex) ?? ""; } catch { }
                            _history.Add(e);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[PeakCSR] 解析历史战绩失败: " + ex.Message);
                }
            }
        }

        private IEnumerator PushProfileRoutine(
            int favoriteMap, int favoriteAscent, int favoriteFormat,
            string nickname, Action<bool, string> onDone)
        {
            string steamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(steamId))
            {
                if (onDone != null) onDone(false, "no_steam_id");
                yield break;
            }

            // 签名顺序必须与 worker.js 的 handleProfileUpdate 完全一致
            string rawSigStr = steamId + "|" + favoriteMap + "|" + favoriteAscent + "|" + favoriteFormat
                + "|" + nickname + "|" + SharedCloudSalt;
            string sig = ComputeSha256Hex16(rawSigStr);

            JObject payload = new JObject
            {
                ["steamId"] = steamId,
                ["playerName"] = PlayerNameSanitizer.Current("") ?? "",
                // 首次建档时云端用它作为初始 ELO; 从原版 RANKED 软折算的种子分必须带上,
                // 否则新建档会被写死成 1000, 再回灌就把本地继承来的基础分覆盖掉了
                ["seedElo"] = Mathf.Clamp(EloCalculator.SeedElo, EloCalculator.MinSeedElo, EloCalculator.MaxSeedElo),
                ["nickname"] = nickname ?? "",
                ["favoriteMap"] = favoriteMap,
                ["favoriteAscent"] = favoriteAscent,
                ["favoriteFormat"] = favoriteFormat,
                ["signature"] = sig
            };

            string url = BaseUrl() + "/api/profile";
            byte[] bodyBytes = Encoding.UTF8.GetBytes(payload.ToString());
            bool ok = false;
            string errCode = "";
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (HasRequestFailed(req))
                {
                    errCode = ParseProfileError(req.downloadHandler != null ? req.downloadHandler.text : "") ?? req.error;
                }
                else
                {
                    ok = true;
                }
            }

            if (ok)
            {
                _profileCache.Remove(steamId);
                RefreshLeaderboard();
                SyncMyCloudProfile();
            }
            if (onDone != null) onDone(ok, errCode);
        }

        // 从错误响应里取出可读错误码, 便于界面翻译 (Nickname already taken / Nickname cooldown)
        private static string ParseProfileError(string body)
        {
            if (string.IsNullOrEmpty(body)) return null;
            try
            {
                JObject root = JObject.Parse(body);
                string err = root["error"] != null ? root["error"].ToString() : null;
                if (err == "Nickname cooldown" && root["cooldownHours"] != null)
                {
                    return "Nickname cooldown:" + root["cooldownHours"].Value<int>();
                }
                return err;
            }
            catch
            {
                return null;
            }
        }

        private IEnumerator ReportMatchRoutine(
            string inviteCode,
            string opponentSteamId,
            string opponentName,
            int opponentElo,
            string winnerSteamId,
            int seed,
            int levelIndex,
            int ascent,
            int format,
            long durationMs,
            float myProgress01,
            float oppProgress01,
            bool isVoid,
            string voidReason)
        {
            string mySteamId = SteamIdentity.GetSteamId64();
            string myName = PlayerNameSanitizer.Current();
            string cleanCode = inviteCode.Trim().ToUpperInvariant();
            int mySeedElo = Mathf.Clamp(EloCalculator.SeedElo, EloCalculator.MinSeedElo, EloCalculator.MaxSeedElo);
            int oppSeedElo = Mathf.Clamp(opponentElo, EloCalculator.MinSeedElo, EloCalculator.MaxSeedElo);

            // 种子分与作废标记必须纳入签名, 否则可被篡改用于抬高首次建档基数或伪造 void
            // 顺序必须与 worker.js 的 rawSigStr 完全一致
            string rawSigStr = cleanCode + "|" + mySteamId + "|" + opponentSteamId + "|" + (winnerSteamId ?? "")
                + "|" + seed + "|" + ascent + "|" + format
                + "|" + mySeedElo + "|" + oppSeedElo
                + "|" + (isVoid ? 1 : 0)
                + "|" + SharedCloudSalt;
            string sig = ComputeSha256Hex16(rawSigStr);

            JObject payload = new JObject
            {
                ["inviteCode"] = cleanCode,
                ["reporterSteamId"] = mySteamId,
                ["opponentSteamId"] = opponentSteamId,
                ["reporterName"] = myName,
                ["opponentName"] = opponentName,
                ["reporterSeedElo"] = mySeedElo,
                ["opponentSeedElo"] = oppSeedElo,
                ["winnerSteamId"] = winnerSteamId ?? "",
                ["seed"] = seed,
                ["levelIndex"] = levelIndex,
                ["ascent"] = ascent,
                ["format"] = format,
                ["durationMs"] = durationMs,
                // 进度补偿入参 (不参与签名, 云端会做 0~1 严格钳制)
                ["reporterProgress"] = Mathf.Clamp01(myProgress01),
                ["opponentProgress"] = Mathf.Clamp01(oppProgress01),
                ["isVoid"] = isVoid,
                ["voidReason"] = voidReason ?? "",
                ["signature"] = sig
            };

            // 最多尝试 2 次（第一次立即上报，若对方还没上报且原版后端状态还在落地，3 秒后自动二次确认触发结算）
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (attempt > 0) yield return new WaitForSecondsRealtime(3.0f);

                string url = BaseUrl() + "/api/report";
                byte[] bodyBytes = Encoding.UTF8.GetBytes(payload.ToString());
                using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
                {
                    req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "application/json");
                    req.timeout = 10;
                    yield return req.SendWebRequest();

                    if (!HasRequestFailed(req))
                    {
                        try
                        {
                            JObject resp = JObject.Parse(req.downloadHandler.text);
                            string status = resp["status"] != null ? resp["status"].ToString() : "";
                            bool respVoid = resp["isVoid"] != null && resp["isVoid"].Type != JTokenType.Null && resp["isVoid"].Value<bool>();
                            if (status == "settled" || status == "already_settled" || status == "settled_void")
                            {
                                int authElo = resp["elo"] != null ? resp["elo"].Value<int>() : EloCalculator.CurrentElo;
                                string verifyMethod = resp["verifyMethod"] != null ? resp["verifyMethod"].ToString() : "cloud";
                                if (respVoid)
                                {
                                    // 作废局: 云端分数不变, 直接用云端权威值校正本地
                                    EloCalculator.ApplyCloudAuthoritativeElo(authElo);
                                    _cloudStatus = CsrLoc.T("cloud.voided", authElo);
                                }
                                else
                                {
                                    EloCalculator.ApplyCloudAuthoritativeElo(authElo);
                                    _cloudStatus = CsrLoc.T("cloud.settled", authElo, verifyMethod);
                                }
                                Plugin.Log.LogInfo("[PeakCSR] 云端天梯权威结算完成 (" + verifyMethod + ") -> 当前 ELO: " + authElo);
                                RefreshLeaderboard();
                                RefreshHistory(true);
                                yield break;
                            }
                            _cloudStatus = CsrLoc.T("cloud.pending");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning("[PeakCSR] 解析云端战报响应失败: " + ex.Message);
                        }
                    }
                }
            }
        }

        private static CloudProfile ParseProfile(JToken prof)
        {
            CloudProfile p = new CloudProfile
            {
                SteamId = Str(prof, "steam_id"),
                PlayerName = Str(prof, "player_name", "Player"),
                Nickname = Str(prof, "nickname", ""),
                Elo = Int(prof, "elo", 1000),
                PeakElo = Int(prof, "peak_elo", 1000),
                Wins = Int(prof, "wins", 0),
                Losses = Int(prof, "losses", 0),
                Draws = Int(prof, "draws", 0),
                Streak = Int(prof, "streak", 0),
                WinsAllPeak = Int(prof, "wins_all_peak", 0),
                LossesAllPeak = Int(prof, "losses_all_peak", 0),
                WinsHardcore = Int(prof, "wins_hardcore", 0),
                LossesHardcore = Int(prof, "losses_hardcore", 0),
                WinsSprint = Int(prof, "wins_sprint", 0),
                LossesSprint = Int(prof, "losses_sprint", 0),
                GamesAllPeak = Int(prof, "games_all_peak", 0),
                GamesHardcore = Int(prof, "games_hardcore", 0),
                GamesSprint = Int(prof, "games_sprint", 0),
                FavoriteMap = Int(prof, "favorite_map", -1),
                FavoriteAscent = Int(prof, "favorite_ascent", -999),
                FavoriteFormat = Int(prof, "favorite_format", -1)
            };
            return p;
        }

        private static string Str(JToken t, string key, string fallback = "")
        {
            JToken v = t[key];
            return (v != null && v.Type != JTokenType.Null) ? v.ToString() : fallback;
        }

        private static int Int(JToken t, string key, int fallback)
        {
            JToken v = t[key];
            if (v == null || v.Type == JTokenType.Null) return fallback;
            try { return v.Value<int>(); } catch { return fallback; }
        }

        private static long Long(JToken t, string key, long fallback)
        {
            JToken v = t[key];
            if (v == null || v.Type == JTokenType.Null) return fallback;
            try { return v.Value<long>(); } catch { return fallback; }
        }

        private static float Float(JToken t, string key, float fallback)
        {
            JToken v = t[key];
            if (v == null || v.Type == JTokenType.Null) return fallback;
            try { return v.Value<float>(); } catch { return fallback; }
        }

        private static bool Bool(JToken t, string key)
        {
            JToken v = t[key];
            if (v == null || v.Type == JTokenType.Null) return false;
            try { return v.Value<bool>(); } catch { return false; }
        }

        private static string ComputeSha256Hex16(string raw)
        {
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
    }
}
