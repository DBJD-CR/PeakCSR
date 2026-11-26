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
        // internal 而非 private: PendingReportStore 需要按字段做持久化映射
        internal sealed class PendingReport
        {
            public string InviteCode;
            public string OpponentSteamId;
            public string OpponentName;
            public int OpponentElo;
            public string WinnerSteamId;
            public int Seed;
            public int LevelIndex;
            public int Ascent;
            public PcsrRuleFormat Format;
            public int Route;
            public long DurationMs;
            public float MyProgress;
            public float OppProgress;
            public bool IsVoid;
            public string VoidReason;
            public int Attempts;
            public float NextAttemptAt;
            // 云端已返回 pending (受理但未结算): 说明请求是通的, 只是还差对手/上游
            public bool CloudAccepted;
            // 待核验轮询已走过的步数
            public int VerifyPolls;
        }

        // 单局补传上限: 超过后放弃, 避免无限重投打爆 Worker 配额
        private const int MaxPendingAttempts = 200;
        // 补传基础间隔与退避上限 (秒)
        private const float PendingRetryBaseSeconds = 45f;
        private const float PendingRetryMaxSeconds = 900f;
        // 连续投递失败这么多次就明确告知玩家"云端不可达", 不再只在 UI 上显示"待结算"
        private const int CloudUnreachableWarnThreshold = 3;
        // 首投窗口结束后, 若云端已受理 (pending) 则进入待核验轮询的阶段间隔 (秒)
        private static readonly float[] VerifyPollSeconds = { 20f, 40f, 80f, 160f, 300f };
        // 历史/榜单缓存过期阈值: 超过这个时间即使没结算事件也会重新拉一次
        private const float HistoryStaleSeconds = 60f;
        private const float LeaderboardStaleSeconds = 60f;
        private float _lastHistoryFetchAt = -999f;
        private float _lastLeaderboardFetchAt = -999f;
        private readonly List<PendingReport> _pendingReports = new List<PendingReport>();
        private bool _pendingDrainRunning;
        private int _droppedReportCount;
        // 连续投递失败计数与"云端不可达"提示的一次性闸门
        private int _consecutiveReportFailures;
        private bool _cloudUnreachableAnnounced;

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

        // 退出前再落一次盘: 条目可能在 Update 里改过 Attempts
        private void OnApplicationQuit()
        {
            SavePendingReports();
            // PB 待补传队列也必须落盘, 否则退出后这批 PB 永远上不了云
            PersonalBestService pb = PersonalBestService.Instance;
            if (pb != null) pb.FlushOnQuit();
        }

        private void Start()
        {
            // 先把上次退出时没送出去的战报捡回来, 再谈拉取
            LoadPendingReports();

            // 启动阶段缓存必然为空, 用非强制刷新
            RefreshLeaderboard(false);
            SyncMyCloudProfile();
            // 启动即尝试补传上一局没收尾的战报
            DrainPendingReports();
            // 上次退出时没推成功的 PB 也顺手补一次
            SyncPendingPbs();
        }

        // ===== 待补传队列持久化 =====
        // 队列内容在每次增删后立刻落盘: 条目极少 (个位数), 写入开销可忽略
        private void LoadPendingReports()
        {
            List<PendingReportStore.Record> records = PendingReportStore.Load();
            if (records.Count == 0) return;

            int restored = 0;
            for (int i = 0; i < records.Count; i++)
            {
                PendingReportStore.Record r = records[i];
                // 同一场 (房间 + 种子) 只保留一条
                bool duplicated = false;
                for (int j = 0; j < _pendingReports.Count; j++)
                {
                    if (_pendingReports[j].InviteCode == r.InviteCode && _pendingReports[j].Seed == r.Seed)
                    {
                        duplicated = true;
                        break;
                    }
                }
                if (duplicated) continue;

                // realtimeSinceStartup 跨进程无意义: 用 -1 而不是 0
                _pendingReports.Add(new PendingReport
                {
                    InviteCode = r.InviteCode,
                    OpponentSteamId = r.OpponentSteamId,
                    OpponentName = r.OpponentName,
                    OpponentElo = r.OpponentElo,
                    WinnerSteamId = r.WinnerSteamId,
                    Seed = r.Seed,
                    LevelIndex = r.LevelIndex,
                    Ascent = r.Ascent,
                    Format = (PcsrRuleFormat)r.Format,
                    Route = r.Route,
                    DurationMs = r.DurationMs,
                    MyProgress = r.MyProgress,
                    OppProgress = r.OppProgress,
                    IsVoid = r.IsVoid,
                    VoidReason = r.VoidReason ?? "",
                    // 跨进程恢复的条目重置尝试计数
                    Attempts = 0,
                    NextAttemptAt = -1f
                });
                restored++;
            }
            if (restored > 0)
            {
                PcsrLog.LogInfo("[PeakCSR] 已从磁盘恢复 " + restored + " 条待补传战报。");
                _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Pending };
            }
        }

        private void SavePendingReports()
        {
            List<PendingReportStore.Record> records = new List<PendingReportStore.Record>();
            for (int i = 0; i < _pendingReports.Count; i++)
            {
                PendingReport e = _pendingReports[i];
                records.Add(new PendingReportStore.Record
                {
                    InviteCode = e.InviteCode,
                    OpponentSteamId = e.OpponentSteamId,
                    OpponentName = e.OpponentName,
                    OpponentElo = e.OpponentElo,
                    WinnerSteamId = e.WinnerSteamId,
                    Seed = e.Seed,
                    LevelIndex = e.LevelIndex,
                    Ascent = e.Ascent,
                    Format = (int)e.Format,
                    Route = e.Route,
                    DurationMs = e.DurationMs,
                    MyProgress = e.MyProgress,
                    OppProgress = e.OppProgress,
                    IsVoid = e.IsVoid,
                    VoidReason = e.VoidReason,
                    Attempts = e.Attempts
                });
            }
            PendingReportStore.Save(records);
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
            string winnerSteamId, int seed, int levelIndex, int ascent, PcsrRuleFormat format, int route,
            long durationMs, float myProgress01, float oppProgress01, bool isVoid, string voidReason)
        {
            if (string.IsNullOrEmpty(inviteCode) || string.IsNullOrEmpty(opponentSteamId)) return;
            // 同一场 (房间+种子) 只留一条, 重复登记直接刷新时间戳
            for (int i = 0; i < _pendingReports.Count; i++)
            {
                PendingReport e = _pendingReports[i];
                if (e.InviteCode == inviteCode && e.Seed == seed)
                {
                    e.NextAttemptAt = Time.realtimeSinceStartup + PendingRetryDelay(e.Attempts);
                    SavePendingReports();
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
                NextAttemptAt = Time.realtimeSinceStartup + PendingRetryDelay(0)
            });
            _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Pending };
            PcsrLog.LogInfo("[PeakCSR] 战报已登记待补传队列 (共 " + _pendingReports.Count + " 条)。");
            SavePendingReports();
        }

        public int PendingReportCount { get { return _pendingReports.Count; } }

        public int DroppedReportCount { get { return _droppedReportCount; } }

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
            // 本轮是否有过结算: 只在有成功时才刷榜单/历史
            bool anySettledThisRound = false;
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

                    if (delivered != null && delivered.HasAuthoritativeElo)
                    {
                        _pendingReports.Remove(e);
                        SavePendingReports();
                        ClearReportFailureStreak();
                        if (!_profile.ApplyCloudAuthoritativeElo(delivered.AuthElo))
                        {
                            // 分数不可信: 队列条目已移除 (云端确实结算了), 但不污染本地档案
                            PcsrLog.LogWarning("[PeakCSR] 待补传战报补发结算但权威分不可信，保留本地档案。");
                            continue;
                        }
                        _cloudStatus = new CloudStatusView
                        {
                            Kind = delivered.IsVoid ? CloudStatusKind.Voided : CloudStatusKind.Settled,
                            Elo = delivered.AuthElo,
                            Detail = delivered.VerifyMethod
                        };
                        PcsrLog.LogInfo("[PeakCSR] 待补传战报补发成功 (" + delivered.VerifyMethod + ") -> ELO " + delivered.AuthElo);
                        AnnounceCloudSettled(delivered.AuthElo, delivered.VerifyMethod);
                        // 刷新推迟到整轮结束
                        anySettledThisRound = true;
                        continue;
                    }

                    if (delivered == null || delivered.Failed)
                    {
                        NoteReportFailure();
                        // 投递彻底失败: 退回长间隔重试, 不能按"已受理"节奏高频轮询
                        e.CloudAccepted = false;
                    }
                    else
                    {
                        // 云端给了可解析响应 (哪怕是 pending): 链路是通的, 失败连击到此为止
                        ClearReportFailureStreak();
                        e.CloudAccepted = true;
                    }

                    if (!e.CloudAccepted)
                    {
                        // 网络层失败: 沿用指数退避, 且不计入"待核验轮询"额度
                        if (e.Attempts >= MaxPendingAttempts)
                        {
                            _pendingReports.Remove(e);
                            SavePendingReports();
                            _droppedReportCount++;
                            PcsrLog.LogWarning("[PeakCSR] 待补传战报重试 " + e.Attempts + " 次仍失败，已放弃 (房间 " + e.InviteCode
                                + ")。本局本地分已生效, 但云端未认账, 总放弃数: " + _droppedReportCount + "。");
                            continue;
                        }
                        e.NextAttemptAt = Time.realtimeSinceStartup + PendingRetryDelay(e.Attempts);
                        SavePendingReports();
                        continue;
                    }

                    // 云端已受理但还没结算: 用较密的节奏去撮合对手的上报
                    if (e.VerifyPolls >= VerifyPollSeconds.Length)
                    {
                        // 轮询额度用完: 降级到长间隔继续挂着, 直到玩家退出或达到总上限
                        if (e.Attempts >= MaxPendingAttempts)
                        {
                            _pendingReports.Remove(e);
                            SavePendingReports();
                            _droppedReportCount++;
                            PcsrLog.LogWarning("[PeakCSR] 待补传战报重试 " + e.Attempts + " 次仍失败，已放弃 (房间 " + e.InviteCode
                                + ")。本局本地分已生效, 但云端未认账, 总放弃数: " + _droppedReportCount + "。");
                            continue;
                        }
                        e.NextAttemptAt = Time.realtimeSinceStartup + PendingRetryDelay(e.Attempts);
                    }
                    else
                    {
                        float wait = VerifyPollSeconds[e.VerifyPolls];
                        e.VerifyPolls++;
                        e.NextAttemptAt = Time.realtimeSinceStartup + wait;
                        PcsrLog.LogInfo("[PeakCSR] 待核验轮询 " + e.VerifyPolls + "/" + VerifyPollSeconds.Length
                            + " (房间 " + e.InviteCode + ")，" + Mathf.CeilToInt(wait) + "s 后再试探撮合。");
                    }
                    SavePendingReports();
                }

                // 本轮有成功就顺手把整轮缓存刷一次, 失败时完全不刷新:
                // 榜单/历史在离线期间刷新只会白白消耗请求且注定失败
                if (anySettledThisRound)
                {
                    RefreshLeaderboard();
                    RefreshHistory(true);
                }
            }
            finally
            {
                _pendingDrainRunning = false;
            }
        }

        // 指数退避
        private static float PendingRetryDelay(int attempt)
        {
            if (attempt <= 1) return PendingRetryBaseSeconds;
            float delay = PendingRetryBaseSeconds * (float)System.Math.Pow(2, attempt - 1);
            return delay > PendingRetryMaxSeconds ? PendingRetryMaxSeconds : delay;
        }

        public bool IsCloudConfigured()
        {
            return _client != null && _client.IsCloudConfigured();
        }

        // ===== 云端可达性熔断与提示 =====
        private void NoteReportFailure()
        {
            _consecutiveReportFailures++;
            if (_consecutiveReportFailures < CloudUnreachableWarnThreshold) return;
            if (_cloudUnreachableAnnounced) return;

            _cloudUnreachableAnnounced = true;
            _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Error, Detail = "cloud_unreachable" };
            PcsrLog.LogWarning("[PeakCSR] 连续 " + _consecutiveReportFailures
                + " 次无法连接云端天梯，本局分数暂时只在本机生效，稍后会自动补传。");
            TryAnnounceCloudUnreachable();
        }

        private void ClearReportFailureStreak()
        {
            _consecutiveReportFailures = 0;
            _cloudUnreachableAnnounced = false;
        }

        private static void TryAnnounceCloudUnreachable()
        {
            try
            {
                MatchCoordinator coordinator = MatchCoordinator.Instance;
                if (coordinator == null) return;
                coordinator.Gateway.PushSystemChat(CsrLoc.T("cloud.unreachable"));
            }
            catch
            {
            }
        }

        public static string FormatRefreshClockStatic(long utcMs)
        {
            return CloudLadderClient.FormatRefreshClock(utcMs);
        }

        // force = false: 缓存还新鲜就直接复用, 避免每次回主菜单都打一次请求
        public void RefreshLeaderboard(bool force = true)
        {
            if (!IsCloudConfigured())
            {
                _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Unconfigured };
                return;
            }
            // 防止连点刷新导致多个协程并发覆盖 _entries
            if (_isLoading) return;
            if (!force && Time.realtimeSinceStartup - _lastLeaderboardFetchAt < LeaderboardStaleSeconds) return;
            StartCoroutine(FetchLeaderboardRoutine());
        }

        public void SyncMyCloudProfile()
        {
            if (!IsCloudConfigured()) return;
            StartCoroutine(FetchMyProfileRoutine());
        }

        // 把本地新刷新的 PB 推到云端 (打完后由面板刷新路径驱动)
        public void SyncPendingPbs()
        {
            if (!IsCloudConfigured()) return;
            PersonalBestService pb = PersonalBestService.Instance;
            if (pb == null) return;
            List<PersonalBest> pending = pb.DrainPendingUpload();
            if (pending == null || pending.Count == 0) return;
            StartCoroutine(PushPbsRoutine(pending));
        }

        // 拉取 PB: 自己的进本地台账, 别人的只进内存缓存 (供档案卡展示)
        public void PullPbs(string steamId)
        {
            if (string.IsNullOrEmpty(steamId) || !IsCloudConfigured()) return;
            StartCoroutine(FetchPbsRoutine(steamId));
        }

        // 战报回看: 本地归档优先, 未命中再问云端
        // 回调参数: 战报数据 (永不为 null), 来源 (local / cloud / none)
        public void RequestReportDetail(CsrHistoryEntry entry, Action<CsrPostMatchData, string> onDone)
        {
            if (entry == null)
            {
                if (onDone != null) onDone(null, "none");
                return;
            }

            // 1) 本地归档: 完整分段, 零请求
            LocalReportArchive archive = LocalReportArchive.Instance;
            if (archive != null)
            {
                CsrReportLookup hit = archive.TryFind(entry.InviteCode, entry.Seed);
                if (hit.Found && hit.Data != null)
                {
                    if (onDone != null) onDone(hit.Data, "local");
                    return;
                }
            }

            // 2) 云端分段: 只有历史行能提供基础信息时才有意义
            if (!IsCloudConfigured() || string.IsNullOrEmpty(entry.MatchKey))
            {
                if (onDone != null) onDone(BuildReportFromHistory(entry), "history");
                return;
            }
            StartCoroutine(FetchReportDetailRoutine(entry, onDone));
        }

        private IEnumerator FetchReportDetailRoutine(CsrHistoryEntry entry, Action<CsrPostMatchData, string> onDone)
        {
            List<CloudSplitDto> mine = null;
            List<CloudSplitDto> theirs = null;
            yield return _client.FetchReportDetail(entry.MatchKey, delegate(List<CloudSplitDto> m, List<CloudSplitDto> o)
            {
                mine = m;
                theirs = o;
            });

            CsrPostMatchData data = BuildReportFromHistory(entry);
            if (mine != null && mine.Count > 0)
            {
                ApplyCloudSplits(data, mine, theirs);
                PcsrLog.LogInfo("[PeakCSR] 战报分段已从云端取回 (" + mine.Count + " 段)。");
                if (onDone != null) onDone(data, "cloud");
                yield break;
            }
            if (onDone != null) onDone(data, "history");
        }

        private IEnumerator PushPbsRoutine(List<PersonalBest> pending)
        {
            bool ok = false;
            string err = "";
            yield return _client.PushPbs(pending, delegate(bool success, string error)
            {
                ok = success;
                err = error;
            });
            if (ok)
            {
                PcsrLog.LogInfo("[PeakCSR] PB 已同步到云端 (" + pending.Count + " 条)。");
                yield break;
            }
            // 失败要放回队列, 否则这几条 PB 永远上不了云
            PersonalBestService pb = PersonalBestService.Instance;
            if (pb != null) pb.RequeuePendingUpload(pending);
            PcsrLog.LogWarning("[PeakCSR] PB 云端同步失败 (已放回队列, 下次结算重试): " + err);
        }

        private IEnumerator FetchPbsRoutine(string steamId)
        {
            List<PersonalBest> remote = null;
            yield return _client.FetchPbs(steamId, delegate(List<PersonalBest> list) { remote = list; });
            if (remote == null) yield break;

            PersonalBestService pb = PersonalBestService.Instance;
            if (pb != null) pb.MergeFromCloud(steamId, remote);
            // 拉取是异步的: 档案卡若正停在这一页, 必须让它重绘, 否则一直是空的
            EventBus.PublishPbsUpdated(steamId);
        }

        // 用历史行拼出基础战报 (无分段): 这是"云端也没存分段"时的最终降级
        private static CsrPostMatchData BuildReportFromHistory(CsrHistoryEntry h)
        {
            return new CsrPostMatchData
            {
                IWon = h.Won,
                IsVoid = h.IsVoid,
                OpponentName = h.OpponentName,
                InviteCode = h.InviteCode,
                LevelIndex = h.LevelIndex,
                BiomeLabel = h.BiomeLabel,
                Seed = h.Seed,
                Ascent = h.Ascent,
                Format = (PcsrRuleFormat)h.Format,
                OldElo = h.EloBefore,
                NewElo = h.EloAfter,
                TotalDelta = h.Delta,
                // 云端只存了总变动与胜者完赛用时, 明细项由本地归档补;
                // 这里不带进度就无法声称登顶, 因此不当成有效成绩
                SelfSummited = h.Won && h.DurationMs > 0 && h.MyProgress >= 0.999f,
                MyTotalMs = h.Won || h.IsVoid ? h.DurationMs : 0L,
                MyProgress = h.MyProgress,
                OppProgress = h.OppProgress
            };
        }

        // 把云端分段套到战报上; 段名真值就是群系英文名, 无需额外映射
        private static void ApplyCloudSplits(CsrPostMatchData data, List<CloudSplitDto> mine, List<CloudSplitDto> theirs)
        {
            if (data == null || mine == null) return;
            data.Splits.Clear();

            // 对手单段耗时先按"对手自己的顺序"算好
            long oppPrev = 0L;
            Dictionary<string, long> oppSegByKey = new Dictionary<string, long>(StringComparer.Ordinal);
            if (theirs != null)
            {
                for (int k = 0; k < theirs.Count; k++)
                {
                    CloudSplitDto o = theirs[k];
                    if (o == null || string.IsNullOrEmpty(o.Key)) continue;
                    long oseg = o.CumMs - oppPrev;
                    // 同段重复出现时保留先到的那个 (云端已按顺序存)
                    if (!oppSegByKey.ContainsKey(o.Key)) oppSegByKey[o.Key] = oseg > 0 ? oseg : 0L;
                    oppPrev = o.CumMs;
                }
            }

            long myPrev = 0L;
            for (int i = 0; i < mine.Count; i++)
            {
                CloudSplitDto m = mine[i];
                if (m == null) continue;
                CsrSplitRow row = new CsrSplitRow
                {
                    Key = m.Key ?? "",
                    Label = BiomeSegmenter.LocalizeKey(m.Key),
                    MyMs = m.CumMs,
                    MyEstimated = m.Estimated,
                    MyReached = m.Reached,
                    IsFinal = i == mine.Count - 1
                };
                long seg = m.CumMs - myPrev;
                row.MySegMs = seg > 0 ? seg : 0L;
                myPrev = m.CumMs;

                // 对手分段按段名对齐
                if (theirs != null)
                {
                    for (int k = 0; k < theirs.Count; k++)
                    {
                        CloudSplitDto o = theirs[k];
                        if (o == null || !string.Equals(o.Key, m.Key, StringComparison.Ordinal)) continue;
                        row.OppMs = o.CumMs;
                        row.OppEstimated = o.Estimated;
                        row.OppReached = o.Reached;
                        long oseg;
                        if (oppSegByKey.TryGetValue(o.Key, out oseg)) row.OppSegMs = oseg;
                        break;
                    }
                }
                data.Splits.Add(row);
            }
        }

        // 拉取自己的云端历史战绩，按时间戳判过期, 只有"刚拉过且没过期"才跳过
        public void RefreshHistory(bool force = false)
        {
            if (!IsCloudConfigured()) return;
            if (_historyLoading)
            {
                // 结算刚落地时正在跑的请求可能读到的是结算前的历史, 必须补一次而不是丢掉
                if (force) _historyForcePending = true;
                return;
            }
            if (!force && Time.realtimeSinceStartup - _lastHistoryFetchAt < HistoryStaleSeconds) return;
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

        // 结算后把"最常玩难度/模式"的推导结果同步到云端。
        // 云端 players 表没有分难度场次统计列, 自己算不出来, 只能接收本地结论
        public void PushDerivedPreferences()
        {
            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc == null) return;
            PlayerProfileBridge profile = mc.Profile;
            if (profile == null) return;

            int ascent = profile.FavoriteAscent;   // 已是 Effective
            int format = profile.FavoriteFormat;   // 已是 Effective
            if (ascent <= -900 && format < 0) return;

            // 与本地现值一致时才推, 避免每局都发一条无意义的请求
            CloudProfile mine = MyProfile;
            if (mine != null && mine.FavoriteAscent == ascent && mine.FavoriteFormat == format) return;

            // 云端已选过群系而本地没有时沿用云端值
            int favoriteMap = profile.FavoriteMap;
            if (favoriteMap < 0 && mine != null) favoriteMap = mine.FavoriteMap;

            PushFavoriteProfile(favoriteMap, ascent, format);
        }

        // 上传玩家自选的偏好档案 (保留云端昵称不变)
        public void PushFavoriteProfile(int favoriteMap, int favoriteAscent, int favoriteFormat)
        {
            int disliked = LocalDislikedMap();
            PushProfileInternal(favoriteMap, disliked, favoriteAscent, favoriteFormat, "", true, LocalCountry(), null);
        }

        // 修改 PCSR 昵称, 空串 = 沿用 Steam 昵称
        public void PushNickname(string nickname, int favoriteMap, int favoriteAscent, int favoriteFormat, Action<bool, string> onDone)
        {
            PushProfileInternal(favoriteMap, LocalDislikedMap(), favoriteAscent, favoriteFormat, nickname, false, LocalCountry(), onDone);
        }

        // pushNickname = false 表示本次完全不碰昵称
        public void PushProfile(int favoriteMap, int favoriteAscent, int favoriteFormat, string nickname, Action<bool, string> onDone)
        {
            PushProfileInternal(favoriteMap, LocalDislikedMap(), favoriteAscent, favoriteFormat, nickname, false, LocalCountry(), onDone);
        }

        // 带 dislikedMap 的完整版本 (档案卡保存偏好时用)
        public void PushProfile(int favoriteMap, int dislikedMap, int favoriteAscent, int favoriteFormat,
            string nickname, bool keepNickname, Action<bool, string> onDone)
        {
            PushProfileInternal(favoriteMap, dislikedMap, favoriteAscent, favoriteFormat, nickname, keepNickname,
                LocalCountry(), onDone);
        }

        // 带国籍的完整版本 (偏好页改国籍时用)
        public void PushProfile(int favoriteMap, int dislikedMap, int favoriteAscent, int favoriteFormat,
            string country, string nickname, bool keepNickname, Action<bool, string> onDone)
        {
            PushProfileInternal(favoriteMap, dislikedMap, favoriteAscent, favoriteFormat, nickname, keepNickname,
                country, onDone);
        }

        // 从本地档案读"最讨厌群系", 失败按未设置
        private static int LocalDislikedMap()
        {
            try
            {
                MatchCoordinator c = MatchCoordinator.Instance;
                return c != null ? c.Profile.DislikedMap : -1;
            }
            catch
            {
                return -1;
            }
        }

        // 从本地档案读国籍, 失败按未设置
        private static string LocalCountry()
        {
            try
            {
                MatchCoordinator c = MatchCoordinator.Instance;
                return c != null ? c.Profile.Country : PcsrCountries.Unset;
            }
            catch
            {
                return PcsrCountries.Unset;
            }
        }

        // keepNickname: 明确告诉云端"这次不要动昵称"。
        private void PushProfileInternal(int favoriteMap, int dislikedMap, int favoriteAscent, int favoriteFormat,
            string nickname, bool keepNickname, string country, Action<bool, string> onDone)
        {
            if (!IsCloudConfigured())
            {
                if (onDone != null) onDone(false, "cloud_unconfigured");
                return;
            }
            string effectiveNick = keepNickname ? "" : (nickname ?? "");
            StartCoroutine(PushProfileRoutine(favoriteMap, dislikedMap, favoriteAscent, favoriteFormat,
                effectiveNick, keepNickname, country, onDone));
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
            PcsrRuleFormat format,
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
            PcsrRuleFormat format,
            int route,
            long durationMs,
            float myProgress01,
            float oppProgress01,
            bool isVoid,
            string voidReason)
        {
            SubmitSignedMatchReport(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01,
                isVoid, voidReason, null);
        }

        // mySplits: 我方分段快照 (可选). 随战报一起上送, 存入 match_splits 供回看
        public void SubmitSignedMatchReport(
            string inviteCode,
            string opponentSteamId,
            string opponentName,
            int opponentElo,
            string winnerSteamId,
            int seed,
            int levelIndex,
            int ascent,
            PcsrRuleFormat format,
            int route,
            long durationMs,
            float myProgress01,
            float oppProgress01,
            bool isVoid,
            string voidReason,
            List<CloudSplitDto> mySplits)
        {
            if (!IsCloudConfigured() || string.IsNullOrEmpty(inviteCode) || string.IsNullOrEmpty(opponentSteamId)) return;
            StartCoroutine(ReportMatchRoutine(inviteCode, opponentSteamId, opponentName, opponentElo, winnerSteamId,
                seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01,
                isVoid, voidReason, mySplits));
        }

        // ===== 协程实现 =====

        private IEnumerator FetchLeaderboardRoutine()
        {
            _isLoading = true;
            _lastLeaderboardFetchAt = Time.realtimeSinceStartup;
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
                    if (_pendingReports.Count > 0)
                    {
                        PcsrLog.LogInfo("[PeakCSR] 有 " + _pendingReports.Count
                            + " 局待云端核验，本轮不采纳云端权威档案 (保留本地结算结果 " + _profile.Record.Elo + " ELO)。");
                        return;
                    }

                    // 云端没存自选群系而本地有值: 单向补传一次自愈,
                    // 否则档案卡打开时会被云端空值覆盖显示成"未选择"
                    PlayerRecord rec = _profile.Record;
                    if (p.FavoriteMap < 0 && rec != null && rec.FavoriteMap >= 0)
                    {
                        PcsrLog.LogInfo("[PeakCSR] 云端档案缺少自选群系, 已用本地值补传: " + rec.FavoriteMap);
                        PushFavoriteProfile(rec.FavoriteMap, _profile.FavoriteAscent, _profile.FavoriteFormat);
                    }

                    if (_profile.ApplyCloudAuthoritativeState(
                        p.Elo, p.PeakElo, p.Wins, p.Losses, p.Draws, p.Streak, p.BestStreak,
                        p.WinsAllPeak, p.LossesAllPeak,
                        p.WinsHardcore, p.LossesHardcore,
                        p.WinsSprint, p.LossesSprint,
                        p.FavoriteMap, p.DislikedMap, p.FavoriteAscent, p.FavoriteFormat,
                        p.Country))
                    {
                        PcsrLog.LogInfo("[PeakCSR] 已从云端天梯同步权威档案: " + p.Elo + " ELO");
                    }
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
            _lastHistoryFetchAt = Time.realtimeSinceStartup;

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

            // 加载期间被压下的强制刷新, 无条件补跑一次
            if (_historyForcePending)
            {
                _historyForcePending = false;
                StartCoroutine(FetchHistoryRoutine());
            }
        }

        private IEnumerator PushProfileRoutine(
            int favoriteMap, int dislikedMap, int favoriteAscent, int favoriteFormat,
            string nickname, bool keepNickname, string country, Action<bool, string> onDone)
        {
            bool ok = false;
            string errCode = "";
            yield return _client.PushProfile(favoriteMap, dislikedMap, favoriteAscent, favoriteFormat, nickname, keepNickname,
                country, delegate(bool success, string error)
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
            string winnerSteamId, int seed, int levelIndex, int ascent, PcsrRuleFormat format, int route,
            long durationMs, float myProgress01, float oppProgress01, bool isVoid, string voidReason,
            List<CloudSplitDto> mySplits)
        {
            CloudLadderClient.ReportResult delivered = null;
            // 回调是否真的给过结果: 用它决定协程末尾要不要兜底成 Pending
            bool callbackReturned = false;
            yield return _client.ReportMatch(inviteCode, opponentSteamId, opponentName, opponentElo,
                winnerSteamId, seed, levelIndex, ascent, format, route, durationMs, myProgress01, oppProgress01,
                isVoid, voidReason,
                delegate(CloudLadderClient.ReportResult r)
                {
                    callbackReturned = true;
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
                        // 连续失败达到阈值时升级为"云端不可达"提示
                        NoteReportFailure();
                        return;
                    }
                    // 云端给了可解析响应就说明链路是通的, 失败连击到此为止
                    ClearReportFailureStreak();
                    // 云端已受理但在等对手/待上游核验 (pending_opponent_or_upstream):
                    // 这种响应没有权威分, 绝不能拿去覆盖本地档案
                    if (!r.HasAuthoritativeElo)
                    {
                        _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Pending };
                        // 把上游未通过的具体原因一起打出来, 否则线上只能看到无限 pending
                        string why = string.IsNullOrEmpty(r.UpstreamReason) ? "" : ("，上游: " + r.UpstreamReason);
                        PcsrLog.LogInfo("[PeakCSR] 云端已受理本局 (等待对手上报或上游核验)，未取得权威分 "
                            + "(AuthElo=" + r.AuthElo + ")" + why + "，不覆盖本地档案。");
                        return;
                    }
                    delivered = r;
                    if (!_profile.ApplyCloudAuthoritativeElo(r.AuthElo))
                    {
                        // 云端回了结算但分数不合法: 记错误状态, 绝不落地
                        _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Error, Detail = "implausible_elo" };
                        PcsrLog.LogWarning("[PeakCSR] 云端结算权威分不可信 (" + r.AuthElo + ")，保留本地档案。");
                        return;
                    }
                    if (r.IsVoid)
                    {
                        _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Voided, Elo = r.AuthElo };
                    }
                    else
                    {
                        _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Settled, Elo = r.AuthElo, Detail = r.VerifyMethod };
                    }
                    PcsrLog.LogInfo("[PeakCSR] 云端天梯权威结算完成 (" + r.VerifyMethod + ") -> 当前 ELO: " + r.AuthElo);
                    AnnounceCloudSettled(r.AuthElo, r.VerifyMethod);
                    RefreshLeaderboard();
                    RefreshHistory(true);
                    // 结算落地后把本轮刷新的 PB 推上去 (只推"更快了"的那几条)
                    SyncPendingPbs();
                }, true, mySplits);

            if (delivered == null)
            {
                // 队列兜底: 只在回调压根没到达 (协程被打断) 时兜一手。
                // 已经拿到 Failed 结果时保留 Error 状态, 让玩家能区分"投递失败"与"等对手"
                if (!callbackReturned)
                {
                    _cloudStatus = new CloudStatusView { Kind = CloudStatusKind.Pending };
                }
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
