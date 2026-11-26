using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using RankedPeak.Services;
using UnityEngine;
using UnityEngine.Networking;

namespace PeakCSR
{
    // 云端天梯条目 (原 CloudRankEntry; 分赛制战绩按 ModeRecord 语义平铺映射 DB 列)
    public sealed class CloudRankEntry
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
        public int BestStreak;
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
        public int DislikedMap = -1;
        public int FavoriteAscent = -999;
        public int FavoriteFormat = -1;
        // 国籍 (ISO 3166-1 alpha-2 大写), 空串 = 未设置
        public string Country = PcsrCountries.Unset;

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

    // 云端个人档案 (原 CloudProfile)
    public sealed class CloudProfile
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
        public int BestStreak;
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
        public int DislikedMap = -1;
        public int FavoriteAscent = -999;
        public int FavoriteFormat = -1;
        public string Country = PcsrCountries.Unset;
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

        // 工厂: 从天梯行构造档案预览 (取代 UI 层 20 行手工字段搬运)
        public static CloudProfile FromEntry(CloudRankEntry e)
        {
            return new CloudProfile
            {
                SteamId = e.SteamId,
                PlayerName = e.PlayerName,
                Nickname = e.Nickname,
                Elo = e.Elo,
                PeakElo = e.Elo,
                Wins = e.Wins,
                Losses = e.Losses,
                Streak = e.Streak,
                BestStreak = e.BestStreak,
                WinsAllPeak = e.WinsAllPeak,
                LossesAllPeak = e.LossesAllPeak,
                WinsHardcore = e.WinsHardcore,
                LossesHardcore = e.LossesHardcore,
                WinsSprint = e.WinsSprint,
                LossesSprint = e.LossesSprint,
                GamesAllPeak = e.GamesAllPeak,
                GamesHardcore = e.GamesHardcore,
                GamesSprint = e.GamesSprint,
                FavoriteMap = e.FavoriteMap,
                DislikedMap = e.DislikedMap,
                FavoriteAscent = e.FavoriteAscent,
                FavoriteFormat = e.FavoriteFormat,
                Country = e.Country,
                GlobalRank = e.Rank,
                Loaded = true
            };
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

    // 云端历史战绩行 (原 CsrHistoryEntry)
    // 云端战报分段行 (与 match_splits 的 JSON 结构一致)
    public sealed class CloudSplitDto
    {
        public string Key = "";
        public long SegMs;
        public long CumMs;
        public bool Estimated;
        public bool Reached;
    }

    public sealed class CsrHistoryEntry
    {
        public string MatchKey = "";
        public string InviteCode = "";
        public int Seed;
        public bool Won;
        public bool IsVoid;
        public string OpponentSteamId = "";
        // 云端 history 现在 LEFT JOIN players 带回对手昵称, 老库没留行时为空
        public string OpponentName = "";
        public int Delta;
        public int EloBefore;
        public int EloAfter;
        public int LevelIndex;
        public string BiomeLabel = "";
        public int Ascent;
        public int Format;
        public int Route;
        public long DurationMs;
        public float MyProgress;
        public float OppProgress;
        public long SettledAtMs;
    }

    // 纯 HTTP/JSON API 客户端与请求签名 (原 CloudLeaderboardService 通信部分收口)
    // 只负责"发请求 + 解 DTO", 不产出本地化文案; 文案由 LadderSyncService 转 ViewModel 状态
    public sealed class CloudLadderClient
    {
        private const string SharedCloudSalt = "PEAKCSR_CLOUD_V1_2026";
        // PB 上传的合理区间, 必须与 cloudflare/src/handlers/pbs.js 的 MIN/MAX_TIME_MS 一致
        private const long PbUploadMinMs = 30L * 1000L;
        private const long PbUploadMaxMs = 6L * 60L * 60L * 1000L;
        private const string DefaultWorkerUrl = "https://peakcsr-ladder.3405689090.workers.dev";
        // 完整天梯窗口的容量上限
        private const int LeaderboardFetchLimit = 50;

        private readonly Func<string> _baseUrlSource;

        public CloudLadderClient(Func<string> baseUrlSource)
        {
            _baseUrlSource = baseUrlSource;
        }

        public static string ComputeSha256Hex16(string raw)
        {
            using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
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

        private static string FallbackUrl()
        {
            string raw = "";
            try
            {
                if (Plugin.Instance != null && Plugin.Instance.CloudApiUrl != null)
                {
                    raw = (Plugin.Instance.CloudApiUrl.Value ?? "").Trim();
                }
            }
            catch
            {
            }
            if (string.IsNullOrEmpty(raw))
            {
                raw = DefaultWorkerUrl;
                try
                {
                    if (Plugin.Instance != null && Plugin.Instance.CloudApiUrl != null)
                    {
                        Plugin.Instance.CloudApiUrl.Value = DefaultWorkerUrl;
                    }
                }
                catch
                {
                }
            }
            int apiIdx = raw.IndexOf("/api/", StringComparison.OrdinalIgnoreCase);
            if (apiIdx > 0)
            {
                raw = raw.Substring(0, apiIdx);
            }
            return raw.TrimEnd('/');
        }

        public string BaseUrl()
        {
            if (_baseUrlSource != null)
            {
                string configured = _baseUrlSource();
                if (!string.IsNullOrEmpty(configured)) return configured;
            }
            return FallbackUrl();
        }

        public bool IsCloudConfigured()
        {
            string url = BaseUrl();
            return !string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
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

        // 历史战绩时间戳: 完整到年月日时分秒, 便于跨天回看
        public static string FormatHistoryClock(long utcMs)
        {
            if (utcMs <= 0) return "----";
            try
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(utcMs).ToOffset(RefreshClockOffset).ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                return "----";
            }
        }

        // UnityWebRequest.result 是枚举, 不要硬编码魔法数字
        private static bool HasRequestFailed(UnityWebRequest req)
        {
            return req.result != UnityWebRequest.Result.Success;
        }

        public IEnumerator FetchLeaderboard(Action<List<CloudRankEntry>, long, string> onDone)
        {
            string url = BaseUrl() + "/api/leaderboard?limit=" + LeaderboardFetchLimit;
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                if (HasRequestFailed(req))
                {
                    if (onDone != null) onDone(null, 0, req.error);
                    yield break;
                }
                try
                {
                    JObject root = JObject.Parse(req.downloadHandler.text);
                    long refreshedAt = root["refreshedAt"] != null && root["refreshedAt"].Type != JTokenType.Null
                        ? root["refreshedAt"].Value<long>()
                        : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                    List<CloudRankEntry> entries = new List<CloudRankEntry>();
                    JArray arr = root["entries"] as JArray;
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Count; i++)
                        {
                            JToken item = arr[i];
                            entries.Add(new CloudRankEntry
                            {
                                Rank = i + 1,
                                SteamId = Str(item, "steam_id"),
                                PlayerName = Str(item, "player_name", "Player"),
                                Nickname = Str(item, "nickname", ""),
                                Elo = Int(item, "elo", 1000),
                                Wins = Int(item, "wins", 0),
                                Losses = Int(item, "losses", 0),
                                Streak = Int(item, "streak", 0),
                                BestStreak = Int(item, "best_streak", 0),
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
                                DislikedMap = Int(item, "disliked_map", -1),
                                FavoriteAscent = Int(item, "favorite_ascent", -999),
                                FavoriteFormat = Int(item, "favorite_format", -1),
                                Country = PcsrCountries.Normalize(Str(item, "country", ""))
                            });
                        }
                    }
                    if (onDone != null) onDone(entries, refreshedAt, null);
                }
                catch (Exception ex)
                {
                    if (onDone != null) onDone(null, 0, ex.Message);
                }
            }
        }

        public IEnumerator FetchProfile(string steamId, Action<CloudProfile, int> onDone)
        {
            string url = BaseUrl() + "/api/profile?steamId=" + UnityWebRequest.EscapeURL(steamId);
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                if (HasRequestFailed(req))
                {
                    if (onDone != null) onDone(null, 0);
                    yield break;
                }
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
                        if (onDone != null) onDone(p, p.GlobalRank);
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 解析玩家档案失败: " + ex.Message);
                }
                if (onDone != null) onDone(null, 0);
            }
        }

        // ===== PB 同步 =====

        // 批量上传 PB: 服务端按"更快才覆盖"幂等处理, 重复提交无害
        public IEnumerator PushPbs(List<PersonalBest> pbs, Action<bool, string> onDone)
        {
            string steamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(steamId))
            {
                if (onDone != null) onDone(false, "no_steam_id");
                yield break;
            }
            if (pbs == null || pbs.Count == 0)
            {
                if (onDone != null) onDone(true, "");
                yield break;
            }

            JArray arr = new JArray();
            // 签名用的规范化串必须与服务端完全一致: 排序后拼 category:subKey:timeMs
            //
            // 剔除规则也要与服务端逐条对齐 (category/subKey 范围、timeMs 区间),
            // 否则服务端多剔除一条就会导致签名集合不同、永远 403。
            List<string> sigParts = new List<string>();
            for (int i = 0; i < pbs.Count; i++)
            {
                PersonalBest pb = pbs[i];
                if (pb == null || !pb.IsValid) continue;
                // 与服务端 clampCategory / clampSubKey 的取值域一致
                int cat = (int)pb.Key.Category;
                if (cat < 0 || cat > 3) continue;
                if (pb.Key.SubKey < 0 || pb.Key.SubKey > 15) continue;
                // 与服务端 MIN/MAX_TIME_MS 一致 (30s ~ 6h)
                if (pb.TimeMs < PbUploadMinMs || pb.TimeMs > PbUploadMaxMs) continue;

                arr.Add(new JObject
                {
                    ["category"] = cat,
                    ["subKey"] = pb.Key.SubKey,
                    ["timeMs"] = pb.TimeMs,
                    ["levelIndex"] = pb.LevelIndex,
                    ["seed"] = pb.Seed,
                    ["ascent"] = pb.Ascent,
                    ["format"] = (int)pb.Format,
                    // 达成时间随 PB 一起上行
                    ["achievedAt"] = pb.AchievedAtMs
                });
                sigParts.Add(cat + ":" + pb.Key.SubKey + ":" + pb.TimeMs);
            }
            if (arr.Count == 0)
            {
                // 全部被剔除: 返回失败让调用方 RequeuePendingUpload
                // 同时留一条日志便于定位区间漂移
                PcsrLog.LogWarning("[PeakCSR] PB 上传候选全部被本地区间校验剔除 ("
                    + pbs.Count + " 条), 回退到待补传队列。");
                if (onDone != null) onDone(false, "all_entries_filtered");
                yield break;
            }
            sigParts.Sort(StringComparer.Ordinal);
            string rawSigStr = steamId + "|" + string.Join(",", sigParts.ToArray()) + "|" + SharedCloudSalt;
            string sig = ComputeSha256Hex16(rawSigStr);

            JObject payload = new JObject
            {
                ["steamId"] = steamId,
                ["pbs"] = arr,
                ["signature"] = sig
            };

            bool ok = false;
            string err = "";
            yield return PostJson(BaseUrl() + "/api/pbs", payload, delegate(bool success, string body, string error)
            {
                ok = success;
                if (!success) err = ParseProfileError(body) ?? error;
            });
            if (onDone != null) onDone(ok, err);
        }

        // 拉取某个玩家的全部 PB
        public IEnumerator FetchPbs(string steamId, Action<List<PersonalBest>> onDone)
        {
            if (string.IsNullOrEmpty(steamId))
            {
                if (onDone != null) onDone(null);
                yield break;
            }
            string url = BaseUrl() + "/api/pbs?steamId=" + UnityWebRequest.EscapeURL(steamId);
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                if (HasRequestFailed(req))
                {
                    if (onDone != null) onDone(null);
                    yield break;
                }
                try
                {
                    JObject root = JObject.Parse(req.downloadHandler.text);
                    JArray arr = root["pbs"] as JArray;
                    if (arr == null)
                    {
                        if (onDone != null) onDone(null);
                        yield break;
                    }
                    List<PersonalBest> list = new List<PersonalBest>();
                    for (int i = 0; i < arr.Count; i++)
                    {
                        JToken it = arr[i];
                        list.Add(new PersonalBest
                        {
                            Key = new PbKey((PbCategory)Int(it, "category", -1), Int(it, "subKey", -1)),
                            TimeMs = Long(it, "timeMs", 0),
                            LevelIndex = Int(it, "levelIndex", 0),
                            Seed = Int(it, "seed", 0),
                            Ascent = Int(it, "ascent", 0),
                            Format = (PcsrRuleFormat)Int(it, "format", 0),
                            AchievedAtMs = Long(it, "achievedAt", 0)
                        });
                    }
                    if (onDone != null) onDone(list);
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 解析云端 PB 失败: " + ex.Message);
                    if (onDone != null) onDone(null);
                }
            }
        }

        // ===== 战报分段详情 =====

        // 单场战报分段: 本地归档未命中时才调用
        // onDone(我方分段, 对手分段); 两者都为 null 表示云端也没有
        public IEnumerator FetchReportDetail(string matchKey, Action<List<CloudSplitDto>, List<CloudSplitDto>> onDone)
        {
            string steamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(matchKey) || string.IsNullOrEmpty(steamId))
            {
                if (onDone != null) onDone(null, null);
                yield break;
            }
            string url = BaseUrl() + "/api/reportdetail?matchKey=" + UnityWebRequest.EscapeURL(matchKey)
                + "&steamId=" + UnityWebRequest.EscapeURL(steamId);
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                if (HasRequestFailed(req))
                {
                    if (onDone != null) onDone(null, null);
                    yield break;
                }
                try
                {
                    JObject root = JObject.Parse(req.downloadHandler.text);
                    bool found = root["found"] != null && root["found"].Type != JTokenType.Null
                        && root["found"].Value<bool>();
                    if (!found)
                    {
                        if (onDone != null) onDone(null, null);
                        yield break;
                    }
                    if (onDone != null) onDone(ParseSplits(root["mySplits"] as JArray), ParseSplits(root["oppSplits"] as JArray));
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 解析战报分段失败: " + ex.Message);
                    if (onDone != null) onDone(null, null);
                }
            }
        }

        private static List<CloudSplitDto> ParseSplits(JArray arr)
        {
            if (arr == null) return null;
            List<CloudSplitDto> list = new List<CloudSplitDto>();
            for (int i = 0; i < arr.Count; i++)
            {
                JToken it = arr[i];
                list.Add(new CloudSplitDto
                {
                    Key = Str(it, "key"),
                    SegMs = Long(it, "segMs", 0),
                    CumMs = Long(it, "cumMs", 0),
                    Estimated = Bool(it, "est"),
                    Reached = Bool(it, "reached")
                });
            }
            return list;
        }

        public IEnumerator FetchHistory(string steamId, Action<List<CsrHistoryEntry>> onDone)
        {
            string url = BaseUrl() + "/api/history?limit=20&steamId=" + UnityWebRequest.EscapeURL(steamId);
            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 8;
                yield return req.SendWebRequest();
                if (HasRequestFailed(req))
                {
                    if (onDone != null) onDone(null);
                    yield break;
                }
                List<CsrHistoryEntry> list = new List<CsrHistoryEntry>();
                bool parsed = false;
                try
                {
                    JObject root = JObject.Parse(req.downloadHandler.text);
                    JArray arr = root["history"] as JArray;
                    parsed = arr != null;
                    if (arr != null)
                    {
                        for (int i = 0; i < arr.Count; i++)
                        {
                            JToken item = arr[i];
                            // 非对象元素说明响应已经不可信, 不能再把"读到一半的列表"当成完整历史发布出去
                            if (item == null || item.Type != JTokenType.Object)
                            {
                                parsed = false;
                                break;
                            }
                            CsrHistoryEntry e = new CsrHistoryEntry
                            {
                                MatchKey = Str(item, "matchKey"),
                                InviteCode = Str(item, "inviteCode"),
                                Seed = Int(item, "seed", 0),
                                Won = Bool(item, "won"),
                                IsVoid = Bool(item, "isVoid"),
                                OpponentSteamId = Str(item, "opponentSteamId"),
                                OpponentName = Str(item, "opponentName"),
                                Delta = Int(item, "delta", 0),
                                EloBefore = Int(item, "eloBefore", 0),
                                EloAfter = Int(item, "eloAfter", 0),
                                LevelIndex = Int(item, "levelIndex", 0),
                                Ascent = Int(item, "ascent", 0),
                                Format = Int(item, "format", 0),
                                Route = Int(item, "route", 0),
                                DurationMs = Long(item, "durationMs", 0),
                                MyProgress = Float(item, "myProgress", 0f),
                                OppProgress = Float(item, "oppProgress", 0f),
                                SettledAtMs = Long(item, "settledAt", 0)
                            };
                            try { e.BiomeLabel = MapCatalogHelper.BiomeLabel(e.LevelIndex); } catch { }
                            list.Add(e);
                        }
                    }
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 解析历史战绩失败: " + ex.Message);
                    parsed = false;
                }
                // 解析失败返回 null (保留调用方缓存); 合法的空数组仍然返回空列表 (清空面板)
                if (onDone != null) onDone(parsed ? list : null);
            }
        }

        // 兼容旧调用: 不带国籍时按未设置上传
        public IEnumerator PushProfile(
            int favoriteMap, int dislikedMap, int favoriteAscent, int favoriteFormat, string nickname, bool keepNickname,
            Action<bool, string> onDone)
        {
            return PushProfile(favoriteMap, dislikedMap, favoriteAscent, favoriteFormat, nickname, keepNickname,
                PcsrCountries.Unset, onDone);
        }

        // keepNickname = true 时云端完全不碰昵称字段 (昵称编辑器的那条 "" 才是真正的清空)
        // country: 已归一的国籍码, 空串表示未设置
        public IEnumerator PushProfile(
            int favoriteMap, int dislikedMap, int favoriteAscent, int favoriteFormat, string nickname, bool keepNickname,
            string country, Action<bool, string> onDone)
        {
            string steamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(steamId))
            {
                if (onDone != null) onDone(false, "no_steam_id");
                yield break;
            }

            // 国籍在客户端先归一, 非主权代码根本不会离开本机
            string safeCountry = PcsrCountries.Normalize(country);

            // 签名顺序与 Worker 的 handleProfileUpdate 完全一致
            string rawSigStr = steamId + "|" + favoriteMap + "|" + dislikedMap + "|" + favoriteAscent + "|" + favoriteFormat
                + "|" + nickname + "|" + (keepNickname ? 1 : 0) + "|" + safeCountry + "|" + SharedCloudSalt;
            string sig = ComputeSha256Hex16(rawSigStr);

            JObject payload = new JObject
            {
                ["steamId"] = steamId,
                ["playerName"] = PlayerNameSanitizer.Current("") ?? "",
                // 首次建档时云端用它作为初始 ELO; 从原版 RANKED 软折算的种子分必须带上,
                // 否则新建档会被写死成 1000, 再回灌就把本地继承来的基础分覆盖掉了
                ["seedElo"] = Mathf.Clamp(PlayerProfileSeed(), EloEngine.MinSeedElo, EloEngine.MaxSeedElo),
                ["nickname"] = nickname ?? "",
                ["keepNickname"] = keepNickname,
                ["favoriteMap"] = favoriteMap,
                ["dislikedMap"] = dislikedMap,
                ["favoriteAscent"] = favoriteAscent,
                ["favoriteFormat"] = favoriteFormat,
                ["country"] = safeCountry,
                ["signature"] = sig
            };

            bool ok = false;
            string errCode = "";
            yield return PostJson(BaseUrl() + "/api/profile", payload, delegate(bool success, string body, string error)
            {
                ok = success;
                if (!success)
                {
                    errCode = ParseProfileError(body) ?? error;
                }
            });

            if (ok)
            {
                if (onDone != null) onDone(true, "");
                yield break;
            }
            if (onDone != null) onDone(false, errCode);
        }

        // 上报重试节奏 (秒)
        private static readonly float[] ReportRetryOffsets = { 0f, 3f, 10f, 25f };

        private static JArray BuildSplitsArray(List<CloudSplitDto> splits)
        {
            if (splits == null || splits.Count == 0) return null;
            JArray arr = new JArray();
            for (int i = 0; i < splits.Count && i < 16; i++)
            {
                CloudSplitDto s = splits[i];
                if (s == null || string.IsNullOrEmpty(s.Key) || s.CumMs <= 0) continue;
                arr.Add(new JObject
                {
                    ["key"] = s.Key,
                    ["segMs"] = s.SegMs,
                    ["cumMs"] = s.CumMs,
                    ["est"] = s.Estimated,
                    ["reached"] = s.Reached
                });
            }
            return arr.Count > 0 ? arr : null;
        }

        public IEnumerator ReportMatch(
            string inviteCode, string opponentSteamId, string opponentName, int opponentElo,
            string winnerSteamId, int seed, int levelIndex, int ascent, PcsrRuleFormat format, int route,
            long durationMs, float myProgress01, float oppProgress01, bool isVoid, string voidReason,
            Action<ReportResult> onSettled)
        {
            return ReportMatch(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01,
                isVoid, voidReason, onSettled, true);
        }

        // singleShot = true: 只请求一次, 由外部队列控制重投节奏
        public IEnumerator ReportMatch(
            string inviteCode, string opponentSteamId, string opponentName, int opponentElo,
            string winnerSteamId, int seed, int levelIndex, int ascent, PcsrRuleFormat format, int route,
            long durationMs, float myProgress01, float oppProgress01, bool isVoid, string voidReason,
            Action<ReportResult> onSettled, bool useRetryLadder)
        {
            return ReportMatch(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01,
                isVoid, voidReason, onSettled, useRetryLadder, null);
        }

        // mySplits: 我方分段 (可选, 不参与签名). 服务端按上报方身份写入 winner/loser 对应列
        public IEnumerator ReportMatch(
            string inviteCode, string opponentSteamId, string opponentName, int opponentElo,
            string winnerSteamId, int seed, int levelIndex, int ascent, PcsrRuleFormat format, int route,
            long durationMs, float myProgress01, float oppProgress01, bool isVoid, string voidReason,
            Action<ReportResult> onSettled, bool useRetryLadder, List<CloudSplitDto> mySplits)
        {
            string mySteamId = SteamIdentity.GetSteamId64();
            string myName = PlayerNameSanitizer.Current();
            string cleanCode = inviteCode.Trim().ToUpperInvariant();
            int mySeedElo = Mathf.Clamp(PlayerProfileSeed(), EloEngine.MinSeedElo, EloEngine.MaxSeedElo);
            int oppSeedElo = Mathf.Clamp(opponentElo, EloEngine.MinSeedElo, EloEngine.MaxSeedElo);

            // 种子分与作废标记必须纳入签名, 否则可被篡改用于抬高首次建档基数或伪造 void
            // 顺序必须与 worker.js 的 rawSigStr 完全一致
            string rawSigStr = cleanCode + "|" + mySteamId + "|" + opponentSteamId + "|" + (winnerSteamId ?? "")
                + "|" + seed + "|" + ascent + "|" + (int)format
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
                ["format"] = (int)format,
                ["route"] = route,
                ["durationMs"] = durationMs,
                // 进度补偿入参 (不参与签名, 云端会做 0~1 严格钳制)
                ["reporterProgress"] = Mathf.Clamp01(myProgress01),
                ["opponentProgress"] = Mathf.Clamp01(oppProgress01),
                ["isVoid"] = isVoid,
                ["voidReason"] = voidReason ?? "",
                ["signature"] = sig
            };

            // 分段不参与签名: 伪造分段没有收益 (不影响 ELO), 服务端会做长度与类型钳制
            JArray splitsArr = BuildSplitsArray(mySplits);
            if (splitsArr != null) payload["mySplits"] = splitsArr;

            int attempts = useRetryLadder ? ReportRetryOffsets.Length : 1;
            ReportResult lastResult = null;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (useRetryLadder && attempt > 0)
                {
                    float gap = ReportRetryOffsets[attempt] - ReportRetryOffsets[attempt - 1];
                    if (gap > 0f) yield return new WaitForSecondsRealtime(gap);
                }

                ReportResult result = null;
                string lastError = null;
                string reportUrl = BaseUrl() + "/api/report";
                yield return PostJson(reportUrl, payload, delegate(bool success, string body, string error)
                {
                    if (success)
                    {
                        result = ParseReportResponse(body);
                        if (result == null)
                        {
                            // 解析失败也要留痕, 否则线上只看到"没结算"却不知道为什么
                            string raw = body ?? "";
                            lastError = "unparsable response: " + raw.Substring(0, System.Math.Min(160, raw.Length));
                        }
                    }
                    else
                    {
                        lastError = error;
                    }
                });

                // SSL/TLS 握手失败就地抢修一次: 换全新 TLS 连接重发
                if (result == null && IsTransientTlsError(lastError))
                {
                    PcsrLog.LogInfo("[PeakCSR] 战报 SSL 握手失败，重建 TLS 连接重试: " + lastError);
                    lastError = null;
                    yield return PostJsonFreshTls(reportUrl, payload, delegate(bool success, string body, string error)
                    {
                        if (success)
                        {
                            result = ParseReportResponse(body);
                            if (result == null)
                            {
                                string raw = body ?? "";
                                lastError = "unparsable response: " + raw.Substring(0, System.Math.Min(160, raw.Length));
                            }
                        }
                        else
                        {
                            lastError = error;
                        }
                    });
                }

                if (result != null && result.Settled)
                {
                    PcsrLog.LogInfo("[PeakCSR] 战报上报成功 (第 " + (attempt + 1) + " 次, "
                        + result.VerifyMethod + "): ELO " + result.AuthElo);
                    if (onSettled != null) onSettled(result);
                    yield break;
                }

                // 服务器给过可解析响应 (待对手/待上游) 就记住, 之后用来区分"等对手"与"根本打不通"
                if (result != null) lastResult = result;

                PcsrLog.LogInfo("[PeakCSR] 战报第 " + (attempt + 1) + "/" + attempts
                    + " 次上报未结算 (等待对手上报或上游核验)"
                    + (lastError != null ? "，错误: " + lastError : "")
                    + "，将在后续重试。");
            }

            // 只有"首次完整阶梯"才入队; 队列自己的单次重投失败了由队列自己安排下次
            if (useRetryLadder)
            {
                PcsrLog.LogWarning("[PeakCSR] 战报重试窗口内始终未结算，已登记为待补传 (房间 " + inviteCode + ", 种子 " + seed + ")。");
                LadderSyncService sync = LadderSyncService.Instance;
                if (sync != null)
                {
                    sync.EnqueuePendingReport(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                        seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01, isVoid, voidReason);
                }
            }

            if (lastResult != null)
            {
                // 云端已受理, 只是还没结算
                if (onSettled != null) onSettled(lastResult);
                yield break;
            }
            // 一次可用响应都没拿到: 明确回一个"投递失败", 让界面显示真实状态而不是"待结算"
            ReportResult failed = new ReportResult();
            failed.Failed = true;
            failed.VerifyMethod = "report_failed";
            failed.AuthElo = -1;
            if (onSettled != null) onSettled(failed);
        }

        public sealed class ReportResult
        {
            public bool Settled;
            public bool IsVoid;
            // 云端给出 pending 时的具体原因 (上游未通过的环节), 用于线上定位
            public string UpstreamReason = "";
            // -1 表示响应里没有权威分 (云端仍在等对手/待上游核验)。
            public int AuthElo = -1;
            public string VerifyMethod = "cloud";
            // 服务器根本没有给出可用响应 (网络失败/无法解析): 与"已受理但等对手"必须区分开
            public bool Failed;

            // 只有"已结算 + 拿到合法权威分"才允许覆盖本地档案
            public bool HasAuthoritativeElo
            {
                get { return Settled && AuthElo > 0; }
            }
        }

        private static ReportResult ParseReportResponse(string body)
        {
            try
            {
                JObject resp = JObject.Parse(body);
                string status = resp["status"] != null ? resp["status"].ToString() : "";
                bool settled = status == "settled" || status == "already_settled" || status == "settled_void";
                ReportResult r = new ReportResult();
                r.Settled = settled;
                r.IsVoid = resp["isVoid"] != null && resp["isVoid"].Type != JTokenType.Null && resp["isVoid"].Value<bool>();
                // pending_opponent_or_upstream 响应里没有 elo 字段, 必须保持 -1 而不是 0
                JToken eloToken = resp["elo"];
                if (eloToken != null && eloToken.Type != JTokenType.Null)
                {
                    try { r.AuthElo = eloToken.Value<int>(); }
                    catch { r.AuthElo = -1; }
                }
                r.VerifyMethod = resp["verifyMethod"] != null ? resp["verifyMethod"].ToString() : "cloud";
                r.UpstreamReason = resp["upstreamReason"] != null ? resp["upstreamReason"].ToString() : "";
                return r;
            }
            catch
            {
                return null;
            }
        }

        private static int PlayerProfileSeed()
        {
            MatchCoordinator c = MatchCoordinator.Instance;
            return c != null ? c.Profile.SeedElo : 1000;
        }

        private static IEnumerator PostJson(string url, JObject payload, Action<bool, string, string> onDone)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(payload.ToString());
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.timeout = 10;
                yield return req.SendWebRequest();

                string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (HasRequestFailed(req))
                {
                    if (onDone != null) onDone(false, body, req.error);
                }
                else
                {
                    if (onDone != null) onDone(true, body, null);
                }
            }
        }

        // UnityWebRequest 的 SSL 握手失败是"整条连接建不起来", 重发同一条请求多半还是失败。
        // 这里改用一次全新的长连接 (KeepAlive 关掉, 强制重建 TLS 会话) 再试一次
        private static IEnumerator PostJsonFreshTls(string url, JObject payload, Action<bool, string, string> onDone)
        {
            yield return new WaitForSecondsRealtime(0.6f);
            byte[] bodyBytes = Encoding.UTF8.GetBytes(payload.ToString());
            using (UnityWebRequest req = new UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UploadHandlerRaw(bodyBytes);
                req.downloadHandler = new DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "application/json");
                req.SetRequestHeader("Connection", "close");
                req.disposeCertificateHandlerOnDispose = true;
                req.disposeDownloadHandlerOnDispose = true;
                req.timeout = 15;
                yield return req.SendWebRequest();

                string body = req.downloadHandler != null ? req.downloadHandler.text : "";
                if (HasRequestFailed(req))
                {
                    if (onDone != null) onDone(false, body, req.error);
                }
                else
                {
                    if (onDone != null) onDone(true, body, null);
                }
            }
        }

        // SSL 握手类错误: 值得换条新连接再试一次 (其他错误如 4xx/403 重试没有意义)
        private static bool IsTransientTlsError(string error)
        {
            if (string.IsNullOrEmpty(error)) return false;
            string e = error.ToLowerInvariant();
            return e.Contains("ssl") || e.Contains("tls") || e.Contains("certificate")
                || e.Contains("connection reset") || e.Contains("cannot connect");
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

        private static CloudProfile ParseProfile(JToken prof)
        {
            return new CloudProfile
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
                BestStreak = Int(prof, "best_streak", 0),
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
                DislikedMap = Int(prof, "disliked_map", -1),
                FavoriteAscent = Int(prof, "favorite_ascent", -999),
                FavoriteFormat = Int(prof, "favorite_format", -1),
                Country = PcsrCountries.Normalize(Str(prof, "country", ""))
            };
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
    }

    // MapCatalog 的安全包装 (Ladder 层不直接 try-catch 游戏类型)
    internal static class MapCatalogHelper
    {
        public static string BiomeLabel(int levelIndex)
        {
            try { return RankedPeak.Services.MapCatalog.GetBiomeLabel(levelIndex) ?? ""; }
            catch { return ""; }
        }

        // 原始群系签名 (未经 S->G 的显示替换)
        // GetBiomeLabel 会把下标 >=1 的 'S' 改成 'G', 按位反查会把非首位的海岸误判成雾沼
        public static string RawBiomeSignature(int levelIndex)
        {
            try { return RankedPeak.Services.MapCatalog.GetBiomeId(levelIndex) ?? ""; }
            catch { return ""; }
        }
    }
}
