using System;
using System.Collections;
using System.Collections.Generic;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // 云端天梯同步编排服务 (原 CloudLeaderboardService 的 MonoBehaviour 外观保留)
    // 协调本地档案与云端权威覆盖、战报双次重试上报、档案缓存。
    // 文案状态: 直接产出已本地化的 UI 状态文案改为枚举 + 参数 (防腐), UI 端按 LocKey 自行渲染
    public sealed class LadderSyncService : MonoBehaviour
    {
        public enum CloudStatusKind
        {
            None,
            Unconfigured,
            Loading,
            Updated,
            Pending,
            Settled,
            Voided,
            Error
        }

        public struct CloudStatusView
        {
            public CloudStatusKind Kind;
            public int Count;
            public int Elo;
            public string Detail;
        }

        public static LadderSyncService Instance { get; private set; }

        private CloudLadderClient _client;
        private PlayerProfileBridge _profile;

        private readonly List<CloudRankEntry> _entries = new List<CloudRankEntry>();
        private bool _isLoading;
        private CloudStatusView _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.None };
        private long _lastRefreshedAtMs;

        private readonly List<CsrHistoryEntry> _history = new List<CsrHistoryEntry>();
        private bool _historyLoading;
        private CloudProfile _myProfile = new CloudProfile();
        private readonly Dictionary<string, CloudProfile> _profileCache = new Dictionary<string, CloudProfile>();

        public IReadOnlyList<CloudRankEntry> Entries { get { return _entries; } }
        public bool IsLoading { get { return _isLoading; } }
        public CloudStatusView Status { get { return _cloudStatus; } }
        public long LastRefreshedAtMs { get { return _lastRefreshedAtMs; } }
        public IReadOnlyList<CsrHistoryEntry> History { get { return _history; } }
        public bool HistoryLoading { get { return _historyLoading; } }
        public CloudProfile MyProfile { get { return _myProfile; } }

        // 兼容外观: 旧 UI 读取的本地化文案 (由 StatusView 现渲染, 不再持久化存文案)
        public string CloudStatus
        {
            get { return RenderStatus(_cloudStatus); }
        }

        public static string RenderStatus(CloudStatusView s)
        {
            switch (s.Kind)
            {
                case CloudStatusKind.Unconfigured: return CsrLoc.T("cloud.unconfigured");
                case CloudStatusKind.Loading: return CsrLoc.T("cloud.loading");
                case CloudStatusKind.Updated: return CsrLoc.T("cloud.updated", s.Count);
                case CloudStatusKind.Pending: return CsrLoc.T("cloud.pending");
                case CloudStatusKind.Settled: return CsrLoc.T("cloud.settled", s.Elo, s.Detail);
                case CloudStatusKind.Voided: return CsrLoc.T("cloud.voided", s.Elo);
                case CloudStatusKind.Error: return CsrLoc.T("cloud.error", s.Detail);
                default: return "";
            }
        }

        internal void Initialize(CloudLadderClient client, PlayerProfileBridge profile)
        {
            _client = client;
            _profile = profile;
        }

        private void Awake()
        {
            Instance = this;
        }

        private void Start()
        {
            RefreshLeaderboard();
            SyncMyCloudProfile();
        }

        public bool IsCloudConfigured()
        {
            return _client != null && _client.IsCloudConfigured();
        }

        public static string FormatRefreshClockStatic(long utcMs)
        {
            return CloudLadderClient.FormatRefreshClock(utcMs);
        }

        public void RefreshLeaderboard()
        {
            if (!IsCloudConfigured())
            {
                _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Unconfigured };
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
            string steamId = SafeSteamId();
            if (string.IsNullOrEmpty(steamId))
            {
                if (onDone != null) onDone(null);
                return;
            }
            if (!IsCloudConfigured())
            {
                if (onDone != null) onDone(BuildLocalProfile(steamId, false));
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
                CloudProfile local = BuildLocalProfile(steamId, false);
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
            int route,
            long durationMs,
            float myProgress01,
            float oppProgress01)
        {
            SubmitSignedMatchReport(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01, false, "");
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
            int route,
            long durationMs,
            float myProgress01,
            float oppProgress01,
            bool isVoid,
            string voidReason)
        {
            if (!IsCloudConfigured() || string.IsNullOrEmpty(inviteCode) || string.IsNullOrEmpty(opponentSteamId)) return;
            StartCoroutine(ReportMatchRoutine(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01, isVoid, voidReason));
        }

        // ===== 协程实现 =====

        private IEnumerator FetchLeaderboardRoutine()
        {
            _isLoading = true;
            _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Loading };
            bool done = false;
            yield return _client.FetchLeaderboard(delegate(List<CloudRankEntry> entries, long refreshedAt, string error)
            {
                done = true;
                _isLoading = false;
                if (error != null)
                {
                    _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Error, Detail = error };
                    return;
                }
                _entries.Clear();
                if (entries != null) _entries.AddRange(entries);
                _lastRefreshedAtMs = refreshedAt;
                _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Updated, Count = _entries.Count };
            });
            // 回调同步路径兜底 (协程的 yield return IEnumerator 一定会走完)
            if (!done) { _isLoading = false; }
        }

        private IEnumerator FetchMyProfileRoutine()
        {
            string steamId = SafeSteamId();
            if (string.IsNullOrEmpty(steamId)) yield break;

            bool done = false;
            yield return _client.FetchProfile(steamId, delegate(CloudProfile p, int rank)
            {
                done = true;
                if (p != null)
                {
                    _myProfile = p;
                    _profileCache[steamId] = p;

                    // 云端没存自选群系而本地有值: 单向补传一次自愈,
                    // 否则档案卡打开时会被云端空值覆盖显示成"未选择"
                    PlayerRecord rec = _profile.Record;
                    if (p.FavoriteMap < 0 && rec != null && rec.FavoriteMap >= 0)
                    {
                        PcsrLog.LogInfo("[PeakCSR] 云端档案缺少自选群系, 已用本地值补传: " + rec.FavoriteMap);
                        PushFavoriteProfile(rec.FavoriteMap, _profile.FavoriteAscent, _profile.FavoriteFormat);
                    }

                    _profile.ApplyCloudAuthoritativeState(
                        p.Elo, p.PeakElo, p.Wins, p.Losses, p.Streak,
                        p.WinsAllPeak, p.LossesAllPeak,
                        p.WinsHardcore, p.LossesHardcore,
                        p.WinsSprint, p.LossesSprint,
                        p.FavoriteMap, p.FavoriteAscent, p.FavoriteFormat);
                    PcsrLog.LogInfo("[PeakCSR] 已从云端天梯同步权威档案: " + p.Elo + " ELO");
                }
            });
            if (!done) { }
        }

        private IEnumerator FetchProfileRoutine(string steamId, Action<CloudProfile> onDone)
        {
            CloudProfile result = null;
            yield return _client.FetchProfile(steamId, delegate(CloudProfile p, int rank)
            {
                if (p != null)
                {
                    result = p;
                    _profileCache[steamId] = p;
                }
            });
            if (onDone != null) onDone(result);
        }

        private IEnumerator FetchHistoryRoutine()
        {
            string steamId = SafeSteamId();
            if (string.IsNullOrEmpty(steamId)) yield break;
            _historyLoading = true;
            yield return _client.FetchHistory(steamId, delegate(List<CsrHistoryEntry> list)
            {
                _historyLoading = false;
                _history.Clear();
                if (list != null) _history.AddRange(list);
            });
        }

        private IEnumerator PushProfileRoutine(
            int favoriteMap, int favoriteAscent, int favoriteFormat,
            string nickname, Action<bool, string> onDone)
        {
            bool ok = false;
            string errCode = "";
            yield return _client.PushProfile(favoriteMap, favoriteAscent, favoriteFormat, nickname, delegate(bool success, string error)
            {
                ok = success;
                errCode = error;
            });

            if (ok)
            {
                _profileCache.Remove(SafeSteamId());
                RefreshLeaderboard();
                SyncMyCloudProfile();
            }
            if (onDone != null) onDone(ok, errCode);
        }

        private IEnumerator ReportMatchRoutine(
            string inviteCode, string opponentSteamId, string opponentName, int opponentElo,
            string winnerSteamId, int seed, int levelIndex, int ascent, McsrRuleFormat format, int route,
            long durationMs, float myProgress01, float oppProgress01, bool isVoid, string voidReason)
        {
            CloudLadderClient.ReportResult delivered = null;
            yield return _client.ReportMatch(inviteCode, opponentSteamId, opponentName, opponentElo,
                winnerSteamId, seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01,
                isVoid, voidReason,
                delegate(CloudLadderClient.ReportResult r)
                {
                    delivered = r;
                    if (r.IsVoid)
                    {
                        // 作废局: 云端分数不变, 直接用云端权威值校正本地
                        _profile.ApplyCloudAuthoritativeElo(r.AuthElo);
                        _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Voided, Elo = r.AuthElo };
                    }
                    else
                    {
                        _profile.ApplyCloudAuthoritativeElo(r.AuthElo);
                        _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Settled, Elo = r.AuthElo, Detail = r.VerifyMethod };
                    }
                    PcsrLog.LogInfo("[PeakCSR] 云端天梯权威结算完成 (" + r.VerifyMethod + ") -> 当前 ELO: " + r.AuthElo);
                    RefreshLeaderboard();
                    RefreshHistory(true);
                });

            if (delivered == null)
            {
                _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Pending };
            }
        }

        // ===== 辅助 =====

        private static string SafeSteamId()
        {
            try { return SteamIdentity.GetSteamId64() ?? ""; } catch { return ""; }
        }

        private CloudProfile BuildLocalProfile(string steamId, bool loaded)
        {
            MatchCoordinator c = MatchCoordinator.Instance;
            PlayerRecord rec = c != null ? c.Profile.Record : new PlayerRecord();
            return new CloudProfile
            {
                SteamId = steamId,
                PlayerName = PlayerNameSanitizer.Current("") ?? "",
                Elo = rec.Elo,
                PeakElo = rec.PeakElo,
                Wins = rec.Wins,
                Losses = rec.Losses,
                Streak = rec.Streak,
                FavoriteMap = rec.FavoriteMap,
                FavoriteAscent = rec.FavoriteAscentEffective,
                FavoriteFormat = rec.FavoriteFormatEffective,
                Loaded = loaded
            };
        }
    }
}
