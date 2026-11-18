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
        public int Elo;
        public int Wins;
        public int Losses;
        public int Streak;
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

        public IReadOnlyList<CloudRankEntry> Entries { get { return _entries; } }
        public bool IsLoading { get { return _isLoading; } }
        public string CloudStatus { get { return _cloudStatus; } }

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

        public void SubmitSignedMatchReport(
            string inviteCode,
            string opponentSteamId,
            string opponentName,
            string winnerSteamId,
            int seed,
            int levelIndex,
            int ascent,
            McsrRuleFormat format,
            long durationMs)
        {
            if (!IsCloudConfigured() || string.IsNullOrEmpty(inviteCode) || string.IsNullOrEmpty(opponentSteamId)) return;
            StartCoroutine(ReportMatchRoutine(inviteCode, opponentSteamId, opponentName, winnerSteamId, seed, levelIndex, ascent, (int)format, durationMs));
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
                        int cloudElo = prof["elo"] != null ? prof["elo"].Value<int>() : EloCalculator.CurrentElo;
                        int cloudPeak = prof["peak_elo"] != null ? prof["peak_elo"].Value<int>() : EloCalculator.PeakElo;
                        int cloudWins = prof["wins"] != null ? prof["wins"].Value<int>() : EloCalculator.Wins;
                        int cloudLosses = prof["losses"] != null ? prof["losses"].Value<int>() : EloCalculator.Losses;
                        int cloudStreak = prof["streak"] != null ? prof["streak"].Value<int>() : EloCalculator.Streak;
                        EloCalculator.ApplyCloudAuthoritativeState(cloudElo, cloudPeak, cloudWins, cloudLosses, cloudStreak);
                        Plugin.Log.LogInfo("[PeakCSR] 已从云端天梯同步权威 ELO 档案: " + cloudElo + " ELO");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[PeakCSR] 解析云端个人档案失败: " + ex.Message);
                }
            }
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
                                SteamId = item["steam_id"] != null ? item["steam_id"].ToString() : "",
                                PlayerName = item["player_name"] != null ? item["player_name"].ToString() : "Player",
                                Elo = item["elo"] != null ? item["elo"].Value<int>() : 1000,
                                Wins = item["wins"] != null ? item["wins"].Value<int>() : 0,
                                Losses = item["losses"] != null ? item["losses"].Value<int>() : 0,
                                Streak = item["streak"] != null ? item["streak"].Value<int>() : 0
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

        private IEnumerator ReportMatchRoutine(
            string inviteCode,
            string opponentSteamId,
            string opponentName,
            string winnerSteamId,
            int seed,
            int levelIndex,
            int ascent,
            int format,
            long durationMs)
        {
            string mySteamId = SteamIdentity.GetSteamId64();
            string myName = PlayerNameSanitizer.Current();
            string cleanCode = inviteCode.Trim().ToUpperInvariant();

            string rawSigStr = cleanCode + "|" + mySteamId + "|" + opponentSteamId + "|" + winnerSteamId + "|" + seed + "|" + ascent + "|" + format + "|" + SharedCloudSalt;
            string sig = ComputeSha256Hex16(rawSigStr);

            JObject payload = new JObject
            {
                ["inviteCode"] = cleanCode,
                ["reporterSteamId"] = mySteamId,
                ["opponentSteamId"] = opponentSteamId,
                ["reporterName"] = myName,
                ["opponentName"] = opponentName,
                ["winnerSteamId"] = winnerSteamId,
                ["seed"] = seed,
                ["levelIndex"] = levelIndex,
                ["ascent"] = ascent,
                ["format"] = format,
                ["durationMs"] = durationMs,
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
                            if (status == "settled" || status == "already_settled")
                            {
                                int authElo = resp["elo"] != null ? resp["elo"].Value<int>() : EloCalculator.CurrentElo;
                                string verifyMethod = resp["verifyMethod"] != null ? resp["verifyMethod"].ToString() : "cloud";
                                EloCalculator.ApplyCloudAuthoritativeElo(authElo);
                                _cloudStatus = CsrLoc.T("cloud.settled", authElo, verifyMethod);
                                Plugin.Log.LogInfo("[PeakCSR] 云端天梯权威结算完成 (" + verifyMethod + ") -> 当前 ELO: " + authElo);
                                RefreshLeaderboard();
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