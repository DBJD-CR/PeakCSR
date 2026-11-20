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

        // 待补传战报 (上报重试窗口内没结算成功的局)
        // 关键点: 上报失败绝不能只写个 Pending 状态就完事，
        // 这里先落本地队列, 之后由 Start / 结算后 / 打开面板三条路径驱动重投。
        private sealed class PendingReport
        {
            public string InviteCode;
            public string OpponentSteamId;
            public string OpponentName;
            public int OpponentElo;
            public string WinnerSteamId;
            public int Seed;
            public int LevelIndex;
            public int Ascent;
            public McsrRuleFormat Format;
            public int Route;
            public long DurationMs;
            public float MyProgress;
            public float OppProgress;
            public bool IsVoid;
            public string VoidReason;
            public int Attempts;
            public float NextAttemptAt;
        }

        // 单局补传上限: 超过后放弃, 避免无限重投打爆 Worker 配额
        private const int MaxPendingAttempts = 12;
        private const float PendingRetryIntervalSeconds = 45f;
        private readonly List<PendingReport> _pendingReports = new List<PendingReport>();
        private bool _pendingDrainRunning;

        private CloudLadderClient _client;
        private PlayerProfileBridge _profile;

        private readonly List<CloudRankEntry> _entries = new List<CloudRankEntry>();
        private bool _isLoading;
        private CloudStatusView _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.None };
        private long _lastRefreshedAtMs;

        private readonly List<CsrHistoryEntry> _history = new List<CsrHistoryEntry>();
        private bool _historyLoading;
        // 加载中又来了强制刷新: 记下这次请求, 等当前请求结束再补一次
        private bool _historyForcePending;
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

        // 云端权威结算完成后在聊天框播报一次, 让玩家明确知道分数已入账
        private static void AnnounceCloudSettled(int elo, string verifyMethod)
        {
            try
            {
                MatchCoordinator coordinator = MatchCoordinator.Instance;
                if (coordinator == null) return;
                string detail = string.IsNullOrEmpty(verifyMethod) ? "cloud" : verifyMethod;
                coordinator.Gateway.PushSystemChat(CsrLoc.T("chat.match.settled", elo, detail));
            }
            catch
            {
            }
        }

        private void Start()
        {
            RefreshLeaderboard();
            SyncMyCloudProfile();
            // 启动即尝试补传上一局没收尾的战报
            DrainPendingReports();
        }

        private void Update()
        {
            // 待补传队列的定时驱动 (比协程更好控: 玩家在任意界面都在补传)
            if (_pendingReports.Count == 0 || _pendingDrainRunning) return;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < _pendingReports.Count; i++)
            {
                if (now >= _pendingReports[i].NextAttemptAt) { DrainPendingReports(); break; }
            }
        }

        // 战报在重试窗口内未结算时由 CloudLadderClient 回调登记
        public void EnqueuePendingReport(
            string inviteCode, string opponentSteamId, string opponentName, int opponentElo,
            string winnerSteamId, int seed, int levelIndex, int ascent, McsrRuleFormat format, int route,
            long durationMs, float myProgress01, float oppProgress01, bool isVoid, string voidReason)
        {
            if (string.IsNullOrEmpty(inviteCode) || string.IsNullOrEmpty(opponentSteamId)) return;
            // 同一场 (房间+种子) 只留一条, 重复登记直接刷新时间戳
            for (int i = 0; i < _pendingReports.Count; i++)
            {
                PendingReport e = _pendingReports[i];
                if (e.InviteCode == inviteCode && e.Seed == seed)
                {
                    e.NextAttemptAt = Time.realtimeSinceStartup + PendingRetryIntervalSeconds;
                    return;
                }
            }
            _pendingReports.Add(new PendingReport
            {
                InviteCode = inviteCode,
                OpponentSteamId = opponentSteamId,
                OpponentName = opponentName,
                OpponentElo = opponentElo,
                WinnerSteamId = winnerSteamId,
                Seed = seed,
                LevelIndex = levelIndex,
                Ascent = ascent,
                Format = format,
                Route = route,
                DurationMs = durationMs,
                MyProgress = myProgress01,
                OppProgress = oppProgress01,
                IsVoid = isVoid,
                VoidReason = voidReason ?? "",
                Attempts = 0,
                NextAttemptAt = Time.realtimeSinceStartup + PendingRetryIntervalSeconds
            });
            _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Pending };
            PcsrLog.LogInfo("[PeakCSR] 战报已登记待补传队列 (共 " + _pendingReports.Count + " 条)。");
        }

        public int PendingReportCount { get { return _pendingReports.Count; } }

        // 把队列里到期的战报逐条重投 (只跑一个协程, 避免并发互踩)
        public void DrainPendingReports()
        {
            if (!IsCloudConfigured()) return;
            if (_pendingDrainRunning) return;
            if (_pendingReports.Count == 0) return;
            StartCoroutine(DrainPendingReportsRoutine());
        }

        private IEnumerator DrainPendingReportsRoutine()
        {
            _pendingDrainRunning = true;
            try
            {
                float now = Time.realtimeSinceStartup;
                // 拷贝一份快照, 回调里可能改动队列
                List<PendingReport> batch = new List<PendingReport>();
                for (int i = 0; i < _pendingReports.Count; i++)
                {
                    if (_pendingReports[i].NextAttemptAt <= now) batch.Add(_pendingReports[i]);
                }

                for (int i = 0; i < batch.Count; i++)
                {
                    PendingReport e = batch[i];
                    e.Attempts++;

                    CloudLadderClient.ReportResult delivered = null;
                    // useRetryLadder: false —— 队列只管单次请求, 重投节奏由 Attempts/NextAttemptAt 控制
                    yield return _client.ReportMatch(
                        e.InviteCode, e.OpponentSteamId, e.OpponentName, e.OpponentElo, e.WinnerSteamId,
                        e.Seed, e.LevelIndex, e.Ascent, e.Format, e.Route, e.DurationMs,
                        e.MyProgress, e.OppProgress, e.IsVoid, e.VoidReason,
                        r => { delivered = r; }, false);

                    if (delivered != null && delivered.Settled)
                    {
                        _pendingReports.Remove(e);
                        _profile.ApplyCloudAuthoritativeElo(delivered.AuthElo);
                        _cloudStatus = new CloudStatusView
                        {
                            Kind = delivered.IsVoid ? CloudStatusKind.Voided : CloudStatusKind.Settled,
                            Elo = delivered.AuthElo,
                            Detail = delivered.VerifyMethod
                        };
                        PcsrLog.LogInfo("[PeakCSR] 待补传战报补发成功 (" + delivered.VerifyMethod + ") -> ELO " + delivered.AuthElo);
                        AnnounceCloudSettled(delivered.AuthElo, delivered.VerifyMethod);
                        RefreshLeaderboard();
                        RefreshHistory(true);
                        continue;
                    }

                    if (e.Attempts >= MaxPendingAttempts)
                    {
                        _pendingReports.Remove(e);
                        PcsrLog.LogWarning("[PeakCSR] 待补传战报重试 " + e.Attempts + " 次仍失败，已放弃 (房间 " + e.InviteCode + ")。");
                        continue;
                    }
                    e.NextAttemptAt = Time.realtimeSinceStartup + PendingRetryIntervalSeconds;
                }
            }
            finally
            {
                _pendingDrainRunning = false;
            }
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
            if (_historyLoading)
            {
                // 结算刚落地时正在跑的请求可能读到的是结算前的历史, 必须补一次而不是丢掉
                if (force) _historyForcePending = true;
                return;
            }
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

        // 上传玩家自选的偏好档案 (保留云端昵称不变)
        public void PushFavoriteProfile(int favoriteMap, int favoriteAscent, int favoriteFormat)
        {
            PushProfileInternal(favoriteMap, favoriteAscent, favoriteFormat, "", true, null);
        }

        // 修改 PCSR 昵称, 空串 = 沿用 Steam 昵称
        public void PushNickname(string nickname, int favoriteMap, int favoriteAscent, int favoriteFormat, Action<bool, string> onDone)
        {
            PushProfileInternal(favoriteMap, favoriteAscent, favoriteFormat, nickname, false, onDone);
        }

        // pushNickname = false 表示本次完全不碰昵称
        public void PushProfile(int favoriteMap, int favoriteAscent, int favoriteFormat, string nickname, Action<bool, string> onDone)
        {
            PushProfileInternal(favoriteMap, favoriteAscent, favoriteFormat, nickname, false, onDone);
        }

        // keepNickname: 明确告诉云端"这次不要动昵称"。
        private void PushProfileInternal(int favoriteMap, int favoriteAscent, int favoriteFormat,
            string nickname, bool keepNickname, Action<bool, string> onDone)
        {
            if (!IsCloudConfigured())
            {
                if (onDone != null) onDone(false, "cloud_unconfigured");
                return;
            }
            string effectiveNick = keepNickname ? "" : (nickname ?? "");
            StartCoroutine(PushProfileRoutine(favoriteMap, favoriteAscent, favoriteFormat, effectiveNick, keepNickname, onDone));
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

            // 请求发出前记下评分修订号
            int revisionAtStart = _profile.RatingRevision;

            bool done = false;
            yield return _client.FetchProfile(steamId, delegate(CloudProfile p, int rank)
            {
                done = true;
                if (p != null)
                {
                    if (_profile.RatingRevision != revisionAtStart)
                    {
                        PcsrLog.LogInfo("[PeakCSR] 自身档案快照已过期 (期间发生过结算)，丢弃本次回灌。");
                        return;
                    }

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
                        p.Elo, p.PeakElo, p.Wins, p.Losses, p.Draws, p.Streak,
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

            bool ok = false;
            yield return _client.FetchHistory(steamId, delegate(List<CsrHistoryEntry> list)
            {
                // list == null 表示请求失败或响应无法解析: 保留上一次成功的结果, 不要把面板清空
                ok = list != null;
                if (ok)
                {
                    _history.Clear();
                    _history.AddRange(list);
                }
            });
            _historyLoading = false;
            if (!ok) PcsrLog.LogInfo("[PeakCSR] 历史战绩拉取失败，保留上一次的结果。");

            // 加载期间被压下的强制刷新, 在这里补跑一次
            if (_historyForcePending)
            {
                _historyForcePending = false;
                if (ok) StartCoroutine(FetchHistoryRoutine());
            }
        }

        private IEnumerator PushProfileRoutine(
            int favoriteMap, int favoriteAscent, int favoriteFormat,
            string nickname, bool keepNickname, Action<bool, string> onDone)
        {
            bool ok = false;
            string errCode = "";
            yield return _client.PushProfile(favoriteMap, favoriteAscent, favoriteFormat, nickname, keepNickname, delegate(bool success, string error)
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
                    // 这里只更新状态文案, 不做权威分数覆盖 (云端还没算)
                    if (r == null)
                    {
                        _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Pending };
                        PcsrLog.LogInfo("[PeakCSR] 战报暂未结算，已转入待补传队列，稍后自动补发。");
                        return;
                    }
                    if (r.Failed)
                    {
                        // 两次上报全部失败: 这不是"等对手", 要让玩家看到真实原因 (同时已在待补传队列里)
                        _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Error, Detail = "report_failed" };
                        PcsrLog.LogWarning("[PeakCSR] 战报上报失败，已转入待补传队列，稍后自动补发。");
                        return;
                    }
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
                    AnnounceCloudSettled(r.AuthElo, r.VerifyMethod);
                    RefreshLeaderboard();
                    RefreshHistory(true);
                });

            if (delivered == null)
            {
                // 队列兜底: 这里只在异常情况 (协程被提前打断) 兜一手, 保证不会静默丢失。
                _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Pending };
            }
            else
            {
                // 结算成功后立刻再尝试把队列里其它待补传的局推掉
                DrainPendingReports();
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
