using System;
using System.Collections.Generic;
using Photon.Pun;
using Photon.Realtime;
using RankedPeak.Live;
using RankedPeak.Models;
using RankedPeak.Services;
using RankedPeak.Timer;
using UnityEngine;

namespace PeakCSR
{
    public enum CsMatchState
    {
        Idle,
        SearchingLobby,
        WaitingOpponent,
        LoadingLevel,
        Countdown,
        Running,
        Finished
    }

    public enum McsrRuleFormat
    {
        StandardAllPeak = 0,
        HardcoreAscent4 = 1,
        BiomeSprint = 2
    }

    // 赛后战报中的单个赛段对比行
    public class CsrSplitRow
    {
        public string Label;
        public long MyMs;
        public long OppMs;
    }

    // PCSR 专属赛后战报数据快照
    public class CsrPostMatchData
    {
        public bool IWon;
        // 作废局: 不分胜负、不加减分
        public bool IsVoid;
        public string Reason = "";
        public string OpponentName = "";
        public int OpponentElo = 1000;
        public string InviteCode = "";
        public int LevelIndex;
        public string BiomeLabel = "";
        public int Seed;
        public int Ascent;
        public McsrRuleFormat Format;
        public int OldElo;
        public int NewElo;
        public int TotalDelta;
        public int BaseDelta;
        public int ProgressBonus;
        // 保底 / 上限钳制带来的附加修正 (总变动 - 基础 - 进度补偿)
        public int FloorAdjust;
        public int MinWinDelta;
        public float MyProgress;
        public float OppProgress;
        public double ModeMultiplier = 1.0;
        public bool IsPlacement;
        public string OldTier = "";
        public string NewTier = "";
        public string AntiBoostNote = "";
        public long MyTotalMs;
        public long OppTotalMs;
        public readonly List<CsrSplitRow> Splits = new List<CsrSplitRow>();
        public float CreatedAt;
    }

    // 实时队列情报快照
    public class CsrQueueStats
    {
        public int WaitingRooms;
        public int ActiveRooms;
        public int OnlinePlayers;
        public int StdWaiting;
        public int AscWaiting;
        public int SprWaiting;

        // 分赛制人数 / 激战房数, 供排位中心细分面板展示
        public int StdPlayers;
        public int AscPlayers;
        public int SprPlayers;
        public int StdActive;
        public int AscActive;
        public int SprActive;

        public float UpdatedAt;
        // 24 小时制刷新时间戳 (UTC 毫秒), 供 UI 显示"最近刷新 HH:mm:ss"
        public long RefreshedAtUtcMs;
        public bool HasData;

        public string RefreshedClock
        {
            get { return CloudLeaderboardService.FormatRefreshClock(RefreshedAtUtcMs); }
        }
    }

    // 基于 RankedPeak.Services.RaceService 构建 PCSR 1v1 实时同种子排位控制器
    public class MatchController : MonoBehaviour
    {
        // 专属房间 AutoEndMs 标识魔数: 30000ms(第一名完赛后 30 秒快节奏收尾) + 赛制/难度编码位
        public const int BaseCsrAutoEndMagicMs = 30000;
        public const int CsrMagicSpan = 40;
        public const string HandshakePrefix = "__PCSR1V1|";
        public const float RerollVoteTimeoutSeconds = 30f;
        private const float LobbyPollIntervalSeconds = 2.5f;
        // 队列情报独立心跳: 即使不在排队也要定期刷新, 否则面板人数会一直定格
        private const float QueueHeartbeatSeconds = 8f;
        // 双方就绪后的开赛倒计时 (只做提示, 无二次确认)
        private const float StartCountdownSeconds = 5f;
        // 建房后已在 WaitingOpponent 时的收敛轮询间隔
        private const float ConvergePollIntervalSeconds = 3f;
        // 自己房间存在超过这个时长才允许迁移, 避免双方同时刚建房就互相搬家
        private const float ConvergeMinRoomAgeSeconds = 4f;
        // 握手宽限: 对手进房后给它 30 秒完成 PCSR 握手 (socket 重连窗口较慢)
        private const float HandshakeGraceSeconds = 30f;
        // LoadingLevel 卡死超时: 超时后主动提示并允许重试
        private const float LoadingWatchdogSeconds = 45f;
        // 断线观察窗: race 消失/连接中断后先观察这么久, 期间不判负
        private const float DisconnectObserveSeconds = 45f;
        // 本机连接异常持续这么久才确认掉线, 避开 socket 1~2 秒的重连抖动
        private const float ConnectivityDownGraceSeconds = 8f;
        // 本机连接状态轮询间隔
        private const float ConnectivityCheckIntervalSeconds = 1f;
        // 进入新阶段后的静默期: 这段时间内不采信连接信号
        // 跑图阶段 socket 刚建连, 加载阶段场景切换本来就会短暂抖动, 都不能当掉线
        private const float ConnectivitySettleRunningSeconds = 4f;
        private const float ConnectivitySettleLoadingSeconds = 25f;
        // 对手实时心跳超过这个时长没更新, 就认定对手已掉线 (RANKED 每秒一帧)
        private const float OpponentHeartbeatStaleSeconds = 6f;
        // 作废后留给 VOID 协议送达的时间, 之后才真正退出房间
        private const float VoidBroadcastGraceSeconds = 3f;
        public const int MinAscent = -1;
        public const int MaxAscent = 8;

        private static MatchController _instance;
        public static MatchController Instance { get { return _instance; } }

        private CsMatchState _state = CsMatchState.Idle;
        private McsrRuleFormat _selectedFormat = McsrRuleFormat.StandardAllPeak;
        private int _selectedAscent = 0;
        private bool _isRatedMatch = true;

        private string _statusMessage = "";
        private string _inviteCode = "";
        private int _seed;
        private int _levelIndex;
        private int _ascent;
        private int _route;

        private string _opponentSteamId = "";
        private string _opponentName = "";
        private int _opponentElo = 1000;
        private bool _opponentVerified;
        private float _opponentProgress;
        private float _selfProgress;
        private float _opponentStamina = 1f;
        private float _opponentHealth = 1f;
        private bool _opponentDown;
        private int _lastEloDelta;
        private string _lastMatchSummary = "";

        private bool _hostStartRequested;
        private float _opponentJoinedTime;
        private float _nextHandshakeSendTime;
        private bool _handshakeAcked;

        // 本局是否已按作废结算 (供 UI 显示"本局已作废 / 可重新匹配")
        private bool _matchVoided;
        // 作废原因, 供 UI 直接展示
        private string _voidReason = "";
        // 本机最近一次真正参与过的房间号, 用于区分"我自己的局"与"别人遗留的幽灵房"
        private string _lastOwnedRaceCode = "";
        // 作废后延迟退出房间的时刻: 留几秒把 VOID 协议发出去再断 socket
        private float _retireLobbyAt = -1f;

        // 赛中 30s 投票换图状态
        private bool _myRerollVoteActive;
        private bool _oppRerollVoteActive;
        private float _rerollVoteDeadline;
        private int _proposedSeed;
        private int _proposedLevelIndex;
        private int _proposedRoute;
        private bool _isSwitchingRerollRoom;

        // 排队轮询与二次校验状态
        private bool _lobbyFetchInFlight;
        private float _nextLobbyPollTime;
        private bool _strictRulesetJoin;
        private McsrRuleFormat _pendingFormat;
        private int _pendingAscent;

        // 队列情报独立心跳
        private bool _queueHeartbeatInFlight;
        private float _nextQueueHeartbeatTime;

        // 面板选择意图值: 与"当前房间赛制"分离, 避免对局结束后 Selection 被静默丢弃
        private McsrRuleFormat _preferredFormat = McsrRuleFormat.StandardAllPeak;
        private int _preferredAscent = 0;
        private bool _preferredIsRated = true;

        // 换图三步握手: 房主建好新房后把新房号下发给加入方
        private string _pendingRerollJoinCode = "";
        private int _pendingRerollSeed;
        private int _pendingRerollLevel;
        private int _pendingRerollRoute;
        private float _pendingRerollJoinDeadline;

        // 断线观察窗
        private bool _observingDisconnect;
        private float _disconnectObserveUntil;
        private string _disconnectReason = "";

        // 本机连接健康检测 (Photon / RANKED 实时 socket)
        private float _connectivityDownSince = -1f;
        private float _nextConnectivityCheck;
        // 当前观察阶段与其进入时间, 用于阶段切换后的静默期
        private CsMatchState _watchedPhase = CsMatchState.Idle;
        private float _phaseEnteredAt;
        // 本局是否成功连上过实时通道: 从没连上就不拿它的断线当信号
        private bool _liveEverConnected;
        // 已确认本局连接不可信: 期间一律禁止判定胜负, 只允许作废
        private bool _disconnectConfirmed;
        // 本局已按作废处理, 防止重复提交
        private bool _voidSubmitted;
        // 对手消失只作废一次
        private bool _opponentDepartedObserved;

        // LoadingLevel 看门狗
        private float _loadingSince = -1f;

        // 串行化: 离开/加入/换房 同一时刻只允许一个过渡动作在飞
        private bool _transitionInFlight;
        private float _transitionDeadline;

        // 开赛倒计时
        private float _startCountdownUntil = -1f;

        // 双方都建了房时的收敛轮询
        private bool _convergeFetchInFlight;
        private float _nextConvergePollTime;
        private float _roomEnteredAt;

        // 排队计时与实时队列统计
        private float _queueStartTime = -1f;
        private float _matchStartTime = -1f;
        private readonly CsrQueueStats _queueStats = new CsrQueueStats();

        // 本局分段与最终数据采集
        private readonly List<long> _mySplits = new List<long>();
        private readonly List<long> _oppSplits = new List<long>();
        // RANKED 原始分段兜底 (仅当自建采集器完全没数据时才使用)
        private readonly List<long> _liveSplitsFallback = new List<long>();
        private float _myBestProgressThisMatch;
        private float _oppBestProgressThisMatch;

        public CsMatchState State { get { return _state; } }
        // 面板上的赛制选择永远写入 _preferred*, 只在真正建房/加入时才应用到房间参数
        // 之前用 if (_state == Idle) 限制, 导致对局结束后(Finished)点击赛制按钮静默失效
        public McsrRuleFormat SelectedFormat
        {
            get { return _preferredFormat; }
            set
            {
                _preferredFormat = value;
                if (_state == CsMatchState.Idle) _selectedFormat = value;
            }
        }
        public int SelectedAscent
        {
            get { return _preferredAscent; }
            set
            {
                _preferredAscent = Mathf.Clamp(value, MinAscent, MaxAscent);
                if (_state == CsMatchState.Idle) _selectedAscent = _preferredAscent;
            }
        }
        public bool IsRatedMatch
        {
            get { return _preferredIsRated; }
            set
            {
                _preferredIsRated = value;
                if (_state == CsMatchState.Idle) _isRatedMatch = value;
            }
        }
        public string StatusMessage
        {
            get
            {
                if (_state == CsMatchState.Idle || string.IsNullOrEmpty(_statusMessage))
                {
                    return CsrLoc.T("status.ready");
                }
                return _statusMessage;
            }
        }
        public string InviteCode { get { return _inviteCode; } }
        public int Seed { get { return _seed; } }
        public int LevelIndex { get { return _levelIndex; } }
        public int Ascent { get { return _ascent; } }
        public int Route { get { return _route; } }
        public string OpponentSteamId { get { return _opponentSteamId; } }
        public string OpponentName
        {
            get { return string.IsNullOrEmpty(_opponentName) ? CsrLoc.T("win.opponent.placeholder") : _opponentName; }
        }
        public int OpponentElo { get { return _opponentElo; } }
        public bool OpponentVerified { get { return _opponentVerified; } }
        public float OpponentProgress { get { return _opponentProgress; } }
        public float SelfProgress { get { return _selfProgress; } }
        public float OpponentStamina { get { return _opponentStamina; } }
        public float OpponentHealth { get { return _opponentHealth; } }
        public bool OpponentDown { get { return _opponentDown; } }
        public int LastEloDelta { get { return _lastEloDelta; } }
        public string LastMatchSummary { get { return _lastMatchSummary; } }

        // 本局已作废 (掉线等无法确认结果的情况), UI 据此展示"本局已作废"并给出重新匹配入口
        public bool MatchVoided { get { return _matchVoided; } }
        public string VoidReason { get { return _voidReason; } }

        public bool MyRerollVoteActive { get { return _myRerollVoteActive; } }
        public bool OppRerollVoteActive { get { return _oppRerollVoteActive; } }
        public int RerollVoteRemainingSeconds
        {
            get
            {
                if (!_myRerollVoteActive && !_oppRerollVoteActive) return 0;
                return Mathf.Max(0, Mathf.CeilToInt(_rerollVoteDeadline - Time.realtimeSinceStartup));
            }
        }

        // 排队已耗时 (秒), 未在排队时返回 0
        public float QueueElapsedSeconds
        {
            get
            {
                if (_queueStartTime < 0f) return 0f;
                if (_state != CsMatchState.SearchingLobby && _state != CsMatchState.WaitingOpponent) return 0f;
                return Mathf.Max(0f, Time.realtimeSinceStartup - _queueStartTime);
            }
        }

        public CsrQueueStats QueueStats { get { return _queueStats; } }

        // 上一次对局结束后生成的专属赛后战报快照
        public static CsrPostMatchData LastPostMatch { get; private set; }

        public static event Action OnStateChanged;

        // 难度档位 -1..8 编码为 0..9 塞进魔数个位
        public static int EncodeAscent(int ascent)
        {
            return Mathf.Clamp(ascent, MinAscent, MaxAscent) - MinAscent;
        }

        public static int DecodeAscent(int code)
        {
            return Mathf.Clamp(code, 0, 9) + MinAscent;
        }

        public static int GetMagicAutoEndMs(McsrRuleFormat format, int ascent)
        {
            int fmtIdx = Mathf.Clamp((int)format, 0, 2);
            return BaseCsrAutoEndMagicMs + fmtIdx * 10 + EncodeAscent(ascent);
        }

        public static bool IsPeakCsrMagicMs(int autoEndMs)
        {
            return autoEndMs >= BaseCsrAutoEndMagicMs && autoEndMs <= BaseCsrAutoEndMagicMs + CsrMagicSpan;
        }

        // 当前是否处于 PCSR 对局中 (供 EndScreen 防卡死等补丁做安全判定)
        public static bool IsCurrentPeakCsrMatch()
        {
            MatchController mc = _instance;
            if (mc == null) return false;
            if (mc._state == CsMatchState.Idle
                || mc._state == CsMatchState.SearchingLobby
                || mc._state == CsMatchState.WaitingOpponent)
            {
                return false;
            }
            try
            {
                RaceService svc = RaceService.Instance;
                RaceInfo race = svc != null ? svc.CurrentRace : null;
                if (race == null) race = svc != null ? svc.LastFinishedRace : null;
                if (race != null && IsPeakCsrMagicMs(race.AutoEndMs)) return true;
            }
            catch { }
            // 兜底: 状态机已在 PCSR 对局中且持有 PCSR 房间码
            return !string.IsNullOrEmpty(mc._inviteCode);
        }

        // 从魔数还原赛制与难度
        public static void ParseMagicAutoEndMs(int autoEndMs, out McsrRuleFormat format, out int ascent)
        {
            int offset = Mathf.Clamp(autoEndMs - BaseCsrAutoEndMagicMs, 0, CsrMagicSpan);
            format = (McsrRuleFormat)Mathf.Clamp(offset / 10, 0, 2);
            ascent = DecodeAscent(offset % 10);
        }

        public static string GetEnvironmentGameVersion()
        {
            try { return MapCatalog.GameVersion ?? ""; }
            catch { return ""; }
        }

        public static string GetEnvironmentMapSetHash()
        {
            try { return MapCatalog.MapSetHash ?? ""; }
            catch { return ""; }
        }

        // 难度档位显示名 (走本地化, 支持菜鸟新人/标准顶峰/天阶 1~8)
        public static string GetAscentDisplayName(int ascent)
        {
            if (ascent <= -1) return CsrLoc.T("ascent.name.tenderfoot");
            if (ascent == 0) return CsrLoc.T("ascent.name.peak");
            return CsrLoc.T("ascent.name.numbered", ascent);
        }

        public static string GetAscentShortName(int ascent)
        {
            if (ascent <= -1) return CsrLoc.T("ascent.short.tenderfoot");
            if (ascent == 0) return CsrLoc.T("ascent.short.peak");
            return CsrLoc.T("ascent.short.numbered", ascent);
        }

        // 统一时间格式: 不足 1 小时用 mm:ss.fff, 超过 1 小时用 h:mm:ss.fff
        public static string FormatClock(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            TimeSpan ts = TimeSpan.FromSeconds(seconds);
            if (ts.TotalHours >= 1)
            {
                return ((int)ts.TotalHours) + ":" + ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00") + "." + ts.Milliseconds.ToString("000");
            }
            return ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00") + "." + ts.Milliseconds.ToString("000");
        }

        public static string FormatClockMs(long ms)
        {
            return FormatClock(ms / 1000.0);
        }

        // 排队/计时用的 mm:ss 短格式
        public static string FormatElapsed(float seconds)
        {
            if (float.IsNaN(seconds) || seconds < 0f) seconds = 0f;
            int total = Mathf.FloorToInt(seconds);
            return (total / 60).ToString("00") + ":" + (total % 60).ToString("00");
        }

        public string GetCurrentFormatDisplayName()
        {
            return GetFormatDisplayName(_selectedFormat, _selectedAscent);
        }

        public static string GetFormatDisplayName(McsrRuleFormat format, int ascent)
        {
            string diff = GetAscentDisplayName(ascent);
            switch (format)
            {
                case McsrRuleFormat.HardcoreAscent4:
                    return CsrLoc.T("format.hardcore.full", diff);
                case McsrRuleFormat.BiomeSprint:
                    return CsrLoc.T("format.sprint.full", diff);
                default:
                    return CsrLoc.T("format.standard.full", diff);
            }
        }

        private void Awake()
        {
            _instance = this;
            _statusMessage = CsrLoc.T("status.ready");
            RaceService.RaceUpdated += OnRaceUpdated;
            RaceLiveConnection.ChatEntryReceived += OnLiveChatReceived;
        }

        private void OnDestroy()
        {
            RaceService.RaceUpdated -= OnRaceUpdated;
            RaceLiveConnection.ChatEntryReceived -= OnLiveChatReceived;
        }

        private void Update()
        {
            // 队列情报心跳独立于对局状态: 主菜单干看着、等对手、刚打完 都要继续刷新
            TickQueueHeartbeat();

            // 延迟退房必须在状态判断之前驱动:
            // 作废后 _state 立刻变成 Finished, 如果放在后面就永远不会被执行。
            TickRetireLobby();

            if (_state == CsMatchState.Idle || _state == CsMatchState.Finished)
            {
                TickDisconnectObserver();
                return;
            }

            if (_state == CsMatchState.SearchingLobby && !_lobbyFetchInFlight && Time.realtimeSinceStartup >= _nextLobbyPollTime)
            {
                PollLobbyQueue();
            }

            // 双方都建了房: 在 WaitingOpponent 阶段继续扫大厅做收敛, 只留一个房间
            if (_state == CsMatchState.WaitingOpponent)
            {
                TickConvergePoll();
            }

            // 开赛倒计时
            TickStartCountdown();

            // 排队状态文案实时刷新已匹配时长 (倒计时中不覆盖)
            if (_startCountdownUntil < 0f
                && (_state == CsMatchState.SearchingLobby || _state == CsMatchState.WaitingOpponent))
            {
                RefreshQueueStatusText();
            }

            TickConnectivityWatch();
            TickDisconnectObserver();
            TickLoadingWatchdog();

            if ((_myRerollVoteActive || _oppRerollVoteActive) && Time.realtimeSinceStartup >= _rerollVoteDeadline)
            {
                _myRerollVoteActive = false;
                _oppRerollVoteActive = false;
                _statusMessage = CsrLoc.T("status.reroll_timeout");
                Plugin.Log.LogInfo("[PeakCSR] 换图投票超时（30秒），继续当前比赛。");
                NotifyStateChanged();
            }

            if (!string.IsNullOrEmpty(_opponentSteamId) && (_state == CsMatchState.WaitingOpponent || _state == CsMatchState.LoadingLevel || _state == CsMatchState.Countdown))
            {
                if ((!_opponentVerified || !_handshakeAcked) && Time.realtimeSinceStartup >= _nextHandshakeSendTime)
                {
                    _nextHandshakeSendTime = Time.realtimeSinceStartup + 1.5f;
                    SendHandshakePacket(_opponentVerified ? "ACK" : "SYN");
                }

                // 只有"连接确实已就绪 + 已进入房间并稳定 3 秒 + 对手仍在 joined 状态"三闸齐全才允许踢人
                // 之前是进房就起 10 秒倒计时且不看 socket 状态, socket 重连窗口期内 SYN 全丢就会误踢真人
                if (_state == CsMatchState.WaitingOpponent && !_opponentVerified && RaceService.Instance != null && RaceService.Instance.IsHost)
                {
                    bool connReady = IsLiveConnectionReady();
                    bool stableInRoom = _opponentJoinedTime > 0f && Time.realtimeSinceStartup - _opponentJoinedTime > 3f;
                    bool oppIdle = IsOpponentStillJoined();
                    bool graceExpired = _opponentJoinedTime > 0f && Time.realtimeSinceStartup - _opponentJoinedTime > HandshakeGraceSeconds;
                    if (connReady && stableInRoom && oppIdle && graceExpired)
                    {
                        Plugin.Log.LogInfo("[PeakCSR] 握手超时 " + HandshakeGraceSeconds + "s，移出未安装 PeakCSR 的误入玩家: " + _opponentName);
                        _statusMessage = CsrLoc.T("status.kick_vanilla", _opponentName);
                        string kickId = _opponentSteamId;
                        string kickName = _opponentName;
                        ResetOpponentInfo();
                        RaceService.Instance.RemoveParticipant(kickId, kickName, false, null);
                    }
                }
            }

            if (_state == CsMatchState.WaitingOpponent && _opponentVerified && !_hostStartRequested)
            {
                if (RaceService.Instance != null && RaceService.Instance.IsHost && RaceService.Instance.CurrentRace != null && RaceService.Instance.CurrentRace.RunnerCount >= 2)
                {
                    // 不再一握手就瞬间开赛: 先播 5 秒倒计时提示
                    _hostStartRequested = true;
                    BeginStartCountdown();
                }
            }
        }

        // ===== 开赛 5 秒倒计时 (纯提示) =====
        private void BeginStartCountdown()
        {
            if (_startCountdownUntil > 0f) return;
            _startCountdownUntil = Time.realtimeSinceStartup + StartCountdownSeconds;
            Plugin.Log.LogInfo("[PeakCSR] 双方就绪，开始 " + StartCountdownSeconds + " 秒开赛倒计时。");
            SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.start_countdown", Mathf.RoundToInt(StartCountdownSeconds)));
            NotifyStateChanged();
        }

        private void TickStartCountdown()
        {
            if (_startCountdownUntil < 0f) return;

            float remain = _startCountdownUntil - Time.realtimeSinceStartup;
            if (remain > 0f)
            {
                int seconds = Mathf.CeilToInt(remain);
                _statusMessage = CsrLoc.T("status.start_countdown", seconds, _opponentName);
                return;
            }

            // 倒计时归零: 房主发起开赛
            _startCountdownUntil = -1f;
            if (RaceService.Instance != null && RaceService.Instance.IsHost
                && RaceService.Instance.CurrentRace != null
                && RaceService.Instance.CurrentRace.RunnerCount >= 2)
            {
                _statusMessage = CsrLoc.T("status.verified_starting", _opponentElo);
                Plugin.Log.LogInfo("[PeakCSR] 倒计时结束，房主发起同步开赛。");
                RaceService.Instance.StartRace(OnLobbyError);
            }
            else if (RaceService.Instance != null && !RaceService.Instance.IsHost)
            {
                _statusMessage = CsrLoc.T("status.waiting_host_start");
                Plugin.Log.LogInfo("[PeakCSR] 倒计时结束，等待房主开赛。");
            }
            NotifyStateChanged();
        }

        // ===== 双方各建一房时的收敛 =====
        // 现象: 两人同时点匹配, 各自 FetchLobbies 都看到空列表 → 各自 CreateRace
        // 结果互相看不到对方的房 (各自的 CurrentRace 已存在, 主轮询已停), 于是永远各关一个房
        private void TickConvergePoll()
        {
            if (_convergeFetchInFlight) return;
            if (RaceService.Instance == null) return;

            // 已经握手成功/已有对手, 无需收敛
            if (_opponentVerified || !string.IsNullOrEmpty(_opponentSteamId)) return;
            // 房主若已进入倒计时就别再折腾
            if (_startCountdownUntil > 0f) return;

            // 房龄不足时不行动: 两人刚同时建房时互相迁移会反复抖动
            if (_roomEnteredAt > 0f && Time.realtimeSinceStartup - _roomEnteredAt < ConvergeMinRoomAgeSeconds) return;
            if (Time.realtimeSinceStartup < _nextConvergePollTime) return;

            _convergeFetchInFlight = true;
            _nextConvergePollTime = Time.realtimeSinceStartup + ConvergePollIntervalSeconds;
            RaceService.Instance.FetchLobbies(OnConvergeLobbiesFetched, err => { _convergeFetchInFlight = false; });
        }

        private void OnConvergeLobbiesFetched(List<RaceLobbyEntry> entries, int total)
        {
            _convergeFetchInFlight = false;
            UpdateQueueStats(entries);

            if (_state != CsMatchState.WaitingOpponent) return;
            if (_opponentVerified || !string.IsNullOrEmpty(_opponentSteamId)) return;
            if (_startCountdownUntil > 0f) return;

            RaceLobbyEntry myRoom = FindLobbyByCode(entries, _inviteCode);
            RaceLobbyEntry rival = FindConvergeTarget(entries, myRoom);

            if (rival == null) return;

            Plugin.Log.LogInfo("[PeakCSR] 收敛: 放弃自建房 [" + _inviteCode + "]，迁移到 [" + rival.InviteCode + "]。");
            SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.converge", rival.InviteCode));
            _statusMessage = CsrLoc.T("status.converging", rival.InviteCode);

            _isSwitchingRerollRoom = true;   // 抑制旧房关闭时的空 race 回调
            string target = rival.InviteCode;
            try { RaceService.Instance.LeaveRace(); } catch { }

            RaceService.Instance.JoinRace(target, race =>
            {
                _isSwitchingRerollRoom = false;
                _strictRulesetJoin = false;
                if (race != null)
                {
                    PcsrSplitTrack.Clear();
                    OnRaceJoined(race);
                }
            }, err =>
            {
                _isSwitchingRerollRoom = false;
                Plugin.Log.LogWarning("[PeakCSR] 收敛迁移失败: " + err);
                _statusMessage = CsrLoc.T("status.error", err);
                NotifyStateChanged();
            });
            NotifyStateChanged();
        }

        private static RaceLobbyEntry FindLobbyByCode(List<RaceLobbyEntry> entries, string code)
        {
            if (entries == null || string.IsNullOrEmpty(code)) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                RaceLobbyEntry e = entries[i];
                if (e == null) continue;
                if (string.Equals(e.InviteCode, code, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        // 选房规则 (保证任意两台机器计算结果一致, 不依赖本地 SteamId 之外的时序):
        //   1) 优先选"非我、赛制完全一致、1/2 人可以加入"的房间
        //   2) 若双方都是房主, 用房号字典序决定谁搬家: 房号更大的主动迁移到更小的
        //      (房间号由服务端生成, 随机性足够, 双方看到同一对房号时结论必然一致)
        private RaceLobbyEntry FindConvergeTarget(List<RaceLobbyEntry> entries, RaceLobbyEntry myRoom)
        {
            if (entries == null) return null;

            RaceLobbyEntry best = null;
            for (int i = 0; i < entries.Count; i++)
            {
                RaceLobbyEntry e = entries[i];
                if (e == null) continue;
                if (string.Equals(e.InviteCode, _inviteCode, StringComparison.OrdinalIgnoreCase)) continue;
                if (!IsPeakCsrMagicMs(e.AutoEndMs)) continue;
                if (e.AutoEndMs != GetMagicAutoEndMs(_pendingFormat, _pendingAscent)) continue;
                if (!e.Compatible) continue;
                if (e.Status != "lobby" || e.IsFull) continue;
                if (e.MaxPlayers != 2 || e.Players != 1) continue;
                if (e.ReviveDead || e.GhostsVisible || !e.SecretSeed) continue;

                // 谁搬家: 房号字典序更大的一方迁移, 双方结论一致
                bool myRoomKnown = myRoom != null;
                if (myRoomKnown && string.Compare(
                        _inviteCode, e.InviteCode, StringComparison.OrdinalIgnoreCase) <= 0)
                {
                    continue;   // 我的房号更小, 我不动
                }

                if (best == null
                    || string.Compare(e.InviteCode, best.InviteCode, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    best = e;
                }
            }
            return best;
        }

        // ===== 队列情报独立心跳 =====
        private void TickQueueHeartbeat()
        {
            if (_queueHeartbeatInFlight) return;
            if (Time.realtimeSinceStartup < _nextQueueHeartbeatTime) return;
            if (RaceService.Instance == null) return;
            if (_lobbyFetchInFlight) return;   // 正在排队时复用主轮询, 避免双请求

            _nextQueueHeartbeatTime = Time.realtimeSinceStartup + QueueHeartbeatSeconds;
            _queueHeartbeatInFlight = true;
            RaceService.Instance.FetchLobbies(OnHeartbeatLobbiesFetched, OnHeartbeatError);
        }

        private void OnHeartbeatLobbiesFetched(List<RaceLobbyEntry> entries, int total)
        {
            _queueHeartbeatInFlight = false;
            UpdateQueueStats(entries);
            NotifyStateChanged();
        }

        private void OnHeartbeatError(string err)
        {
            _queueHeartbeatInFlight = false;
            // 静默失败: 心跳不应打断主流程, 只推迟下次尝试
            _nextQueueHeartbeatTime = Time.realtimeSinceStartup + QueueHeartbeatSeconds;
        }

        // ===== 本机连接健康检测 =====
        // 关键: 掉线时 RANKED 未必立刻把 CurrentRace 置空 (socket 掉线但 HTTP 轮询仍能拿到旧快照),
        // 于是观察窗永远不会启动, EvaluateWinCondition 还在拿"自己掉线前最后一帧"的数据判胜负,
        // 对手那边看到的则是"我不动了" -> 双方各自结算、各自 +- 分。这里必须主动检测本机连接。
        private void TickConnectivityWatch()
        {
            if (!IsConnectivityRelevant())
            {
                _connectivityDownSince = -1f;
                _watchedPhase = CsMatchState.Idle;
                return;
            }

            // 阶段切换 (加载 -> 倒计时 -> 跑图) 时重新起算, 避免把建连/切场景的抖动算成掉线
            if (_state != _watchedPhase)
            {
                _watchedPhase = _state;
                _phaseEnteredAt = Time.realtimeSinceStartup;
                _connectivityDownSince = -1f;
                return;
            }

            float settle = _state == CsMatchState.LoadingLevel
                ? ConnectivitySettleLoadingSeconds
                : ConnectivitySettleRunningSeconds;
            if (Time.realtimeSinceStartup - _phaseEnteredAt < settle) return;

            if (Time.realtimeSinceStartup < _nextConnectivityCheck) return;
            _nextConnectivityCheck = Time.realtimeSinceStartup + ConnectivityCheckIntervalSeconds;

            if (IsLocalConnectivityHealthy())
            {
                if (_connectivityDownSince >= 0f)
                {
                    Plugin.Log.LogInfo("[PeakCSR] 本机连接已恢复，撤销连接异常计时。");
                }
                _connectivityDownSince = -1f;
                return;
            }

            if (_connectivityDownSince < 0f)
            {
                _connectivityDownSince = Time.realtimeSinceStartup;
                Plugin.Log.LogWarning("[PeakCSR] 检测到本机对局连接异常，先观察 " + ConnectivityDownGraceSeconds + " 秒。");
                return;
            }

            if (Time.realtimeSinceStartup - _connectivityDownSince < ConnectivityDownGraceSeconds) return;
            if (_disconnectConfirmed) return;

            // 断线确认: 先冻结胜负判定并进入观察窗。
            // 不在这一刻直接作废, 是因为网络瞬断十几秒后恢复的情况很常见,
            // 一断就作废会把本来能打完的好局白白扔掉; 观察窗到期仍未恢复才真正作废。
            _disconnectConfirmed = true;
            Plugin.Log.LogWarning("[PeakCSR] 本机连接中断超过 " + ConnectivityDownGraceSeconds + "s，冻结胜负判定并进入观察窗。");
            SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.void.announced"));
            BeginDisconnectObservation("local connection lost");
        }

        // 只有真正进入对局阶段才需要看连接; 房间列表/握手阶段由各自动作处理
        private bool IsConnectivityRelevant()
        {
            if (_state != CsMatchState.LoadingLevel
                && _state != CsMatchState.Countdown
                && _state != CsMatchState.Running) return false;
            if (string.IsNullOrEmpty(_inviteCode)) return false;
            return true;
        }

        private bool IsLocalConnectivityHealthy()
        {
            // Photon 是强信号: 游戏本体已经掉线, 对局不可能继续
            bool photonOk;
            try
            {
                ClientState state = PhotonNetwork.NetworkClientState;
                bool disconnected = state == ClientState.Disconnected
                    || state == ClientState.PeerCreated
                    || state == ClientState.ConnectingToNameServer;
                photonOk = !disconnected && PhotonNetwork.IsConnected;
            }
            catch { photonOk = true; }   // Photon 不可用时不参与判断

            // RANKED 实时 socket 是弱信号: 只有"本局确实连上过"之后掉线才算数,
            // 否则开局建连慢会被误判成掉线, 把好局作废掉。
            bool liveOk = true;
            try
            {
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn != null)
                {
                    if (conn.IsConnected) _liveEverConnected = true;
                    else if (conn.IsActive && _liveEverConnected) liveOk = false;
                }
            }
            catch { liveOk = true; }

            return photonOk && liveOk;
        }

        // ===== 断线观察窗 =====
        // race 消失不等于判负: 掉线/被踢/换房/socket 看门狗都会让 CurrentRace 变 null
        private void BeginDisconnectObservation(string reason)
        {
            if (_observingDisconnect) return;
            _observingDisconnect = true;
            _disconnectObserveUntil = Time.realtimeSinceStartup + DisconnectObserveSeconds;
            _disconnectReason = reason ?? "";
            _statusMessage = CsrLoc.T("status.reconnecting", Mathf.CeilToInt(DisconnectObserveSeconds));
            Plugin.Log.LogWarning("[PeakCSR] 对局连接疑似中断 (" + _disconnectReason + ")，进入 " + DisconnectObserveSeconds + " 秒观察窗，暂不结算。");
            NotifyStateChanged();
        }

        private void TickDisconnectObserver()
        {
            if (!_observingDisconnect) return;

            // 恢复: 必须连接真的恢复了才算, 只看到"还拿着旧 RaceInfo"不能撤销观察窗,
            // 否则掉线后会因为本地缓存快照而误判"已恢复", 进而继续按旧数据结算。
            try
            {
                RaceService svc = RaceService.Instance;
                if (svc != null && svc.CurrentRace != null && svc.CurrentRace.Status != "finished"
                    && IsLocalConnectivityHealthy())
                {
                    _observingDisconnect = false;
                    _disconnectReason = "";
                    ClearDisconnectFreeze();
                    Plugin.Log.LogInfo("[PeakCSR] 观察窗内连接已恢复，继续对局，不做任何结算。");
                    if (_state != CsMatchState.Finished) SyncFromRaceInfo(svc.CurrentRace);
                    return;
                }
            }
            catch { }

            if (Time.realtimeSinceStartup < _disconnectObserveUntil)
            {
                _statusMessage = CsrLoc.T("status.reconnecting", Mathf.CeilToInt(_disconnectObserveUntil - Time.realtimeSinceStartup));
                return;
            }

            _observingDisconnect = false;
            Plugin.Log.LogWarning("[PeakCSR] 观察窗到期仍未恢复，本局作废（双方不计分、不计胜负）。");
            ConcludeVoid(CsrLoc.T("status.void_disconnect", _disconnectReason));
        }

        // 观察窗内连接恢复时解除冻结, 让对局可以正常继续
        private void ClearDisconnectFreeze()
        {
            _disconnectConfirmed = false;
            _connectivityDownSince = -1f;
            _nextConnectivityCheck = 0f;
        }

        // ===== LoadingLevel 看门狗 =====
        private void TickLoadingWatchdog()
        {
            if (_state == CsMatchState.LoadingLevel)
            {
                if (_loadingSince < 0f) _loadingSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - _loadingSince > LoadingWatchdogSeconds)
                {
                    _loadingSince = -1f;
                    _statusMessage = CsrLoc.T("status.loading_stuck", Mathf.RoundToInt(LoadingWatchdogSeconds));
                    Plugin.Log.LogWarning("[PeakCSR] 加载超时 " + LoadingWatchdogSeconds + "s 仍未进入对局，尝试重新加入房间自动重试。");
                    NotifyStateChanged();
                    TryRecoverLoading();
                }
            }
            else
            {
                _loadingSince = -1f;
            }
        }

        // 加载卡死时重发一次 join, 让 RaceService 重新走 LoadRaceLevel
        private void TryRecoverLoading()
        {
            if (_transitionInFlight) return;
            if (string.IsNullOrEmpty(_inviteCode)) return;
            if (RaceService.Instance == null) return;

            _transitionInFlight = true;
            _transitionDeadline = Time.realtimeSinceStartup + 15f;
            Plugin.Log.LogInfo("[PeakCSR] 尝试重新加入房间 [" + _inviteCode + "] 以恢复加载。");
            RaceService.Instance.JoinRace(_inviteCode, race =>
            {
                _transitionInFlight = false;
                if (race != null) OnRaceJoined(race);
            }, err =>
            {
                _transitionInFlight = false;
                Plugin.Log.LogWarning("[PeakCSR] 加载恢复失败: " + err);
                _statusMessage = CsrLoc.T("status.error", err);
                NotifyStateChanged();
            });
        }

        private static bool IsLiveConnectionReady()
        {
            try
            {
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                return conn != null && conn.IsConnected;
            }
            catch { return false; }
        }

        // 对手是否仍然在房间里且尚未开赛 (而不是已经跑起来或已离开)
        private static bool IsOpponentStillJoined()
        {
            try
            {
                RaceService svc = RaceService.Instance;
                RaceInfo race = svc != null ? svc.CurrentRace : null;
                if (race == null || race.Participants == null) return false;
                string mySteamId = SteamIdentity.GetSteamId64();
                for (int i = 0; i < race.Participants.Count; i++)
                {
                    RaceParticipant p = race.Participants[i];
                    if (p == null || p.Removed || p.IsSpectator) continue;
                    if (p.SteamId == mySteamId) continue;
                    return p.State == "joined" || p.State == "race_ready";
                }
            }
            catch { }
            return false;
        }

        private void RefreshQueueStatusText()
        {
            if (_state == CsMatchState.SearchingLobby)
            {
                _statusMessage = CsrLoc.T("status.searching", GetFormatDisplayName(_pendingFormat, _pendingAscent), FormatElapsed(QueueElapsedSeconds));
            }
            else if (_state == CsMatchState.WaitingOpponent)
            {
                _statusMessage = CsrLoc.T("status.waiting_opp", _inviteCode, FormatElapsed(QueueElapsedSeconds));
            }
        }

        // 1. 一键搜索同赛制公开 1v1 房间，若无则创建带专属魔数与规则的排位房
        public void Queue1v1()
        {
            if (RaceService.Instance == null)
            {
                _statusMessage = CsrLoc.T("status.not_ready");
                return;
            }

            ResetMatchRuntime();
            _state = CsMatchState.SearchingLobby;
            _strictRulesetJoin = true;
            // 用面板意图值, 不再用可能被上一局覆写过的 _selected*
            _pendingFormat = _preferredFormat;
            _pendingAscent = _preferredAscent;
            _isRatedMatch = _preferredIsRated;
            _selectedFormat = _preferredFormat;
            _selectedAscent = _preferredAscent;
            _queueStartTime = Time.realtimeSinceStartup;

            string fmtName = GetCurrentFormatDisplayName();
            _statusMessage = CsrLoc.T("status.searching", fmtName, FormatElapsed(0f));
            Plugin.Log.LogInfo("[PeakCSR] 开始搜索 PCSR 1v1 排位队列，当前赛制: " + fmtName);
            NotifyStateChanged();

            PollLobbyQueue();
        }

        private void PollLobbyQueue()
        {
            if (RaceService.Instance == null) return;
            _lobbyFetchInFlight = true;
            _nextLobbyPollTime = Time.realtimeSinceStartup + LobbyPollIntervalSeconds;
            RaceService.Instance.FetchLobbies(OnLobbiesFetched, OnLobbyError);
        }

        // 2. 创建私密好友 1v1 房间
        public void CreatePrivate1v1Room()
        {
            if (RaceService.Instance == null)
            {
                _statusMessage = CsrLoc.T("status.not_ready");
                return;
            }

            ResetMatchRuntime();
            _strictRulesetJoin = false;
            _state = CsMatchState.SearchingLobby;
            _queueStartTime = Time.realtimeSinceStartup;
            CreateNew1v1Lobby(isPublicLobby: false);
        }

        // 3. 通过好友分享的房间邀请码直接加入 1v1 房间
        public void JoinByInviteCode(string code)
        {
            if (RaceService.Instance == null)
            {
                _statusMessage = CsrLoc.T("status.not_ready");
                return;
            }

            string cleanCode = SanitizeInviteCode(code);
            if (string.IsNullOrEmpty(cleanCode))
            {
                _statusMessage = CsrLoc.T("status.code_empty");
                return;
            }

            ResetMatchRuntime();
            _strictRulesetJoin = false;
            _state = CsMatchState.SearchingLobby;
            _queueStartTime = Time.realtimeSinceStartup;
            _statusMessage = CsrLoc.T("status.joining_code", cleanCode);
            Plugin.Log.LogInfo("[PeakCSR] 正在通过邀请码加入 1v1 房间: " + cleanCode);
            NotifyStateChanged();

            RaceService.Instance.JoinRace(cleanCode, OnRaceJoined, OnLobbyError);
        }

        private void OnLobbiesFetched(List<RaceLobbyEntry> entries, int total)
        {
            _lobbyFetchInFlight = false;

            UpdateQueueStats(entries);

            if (_state != CsMatchState.SearchingLobby) return;

            bool sawIncompatibleCsrLobby = false;

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    RaceLobbyEntry item = entries[i];
                    if (item == null) continue;

                    Plugin.Log.LogInfo("[PeakCSR] 大厅候选 [" + item.InviteCode + "] 状态=" + item.Status
                        + " 玩家=" + item.Players + "/" + item.MaxPlayers
                        + " 天阶=" + item.Ascent + " 路线=" + item.Route
                        + " autoEnd=" + item.AutoEndMs + " 版本=" + item.GameVersion
                        + " 复活=" + item.ReviveDead + " 幽灵=" + item.GhostsVisible
                        + " 盲种=" + item.SecretSeed + " 兼容=" + item.Compatible);

                    if (!item.Compatible)
                    {
                        if (IsPeakCsrMagicMs(item.AutoEndMs)
                            && item.AutoEndMs == GetMagicAutoEndMs(_selectedFormat, _selectedAscent))
                        {
                            sawIncompatibleCsrLobby = true;
                        }
                        continue;
                    }

                    if (!IsMatchingLobby(item)) continue;

                    _statusMessage = CsrLoc.T("status.found_joining", item.InviteCode);
                    Plugin.Log.LogInfo("[PeakCSR] 找到匹配的 PCSR 1v1 房间 [" + item.InviteCode + "]，正在加入...");
                    NotifyStateChanged();
                    RaceService.Instance.JoinRace(item.InviteCode, OnRaceJoined, OnLobbyError);
                    return;
                }
            }

            if (sawIncompatibleCsrLobby)
            {
                _statusMessage = CsrLoc.T("status.env_mismatch",
                    GetEnvironmentGameVersion(), GetEnvironmentMapSetHash());
                Plugin.Log.LogWarning("[PeakCSR] 检测到同赛制 PCSR 房间但地图集不一致, 本机 mapSetHash="
                    + GetEnvironmentMapSetHash() + ", gameVersion=" + GetEnvironmentGameVersion());
                NotifyStateChanged();
                _nextLobbyPollTime = Time.realtimeSinceStartup + LobbyPollIntervalSeconds;
                return;
            }

            Plugin.Log.LogInfo("[PeakCSR] 当前没有符合赛制的大厅，创建新的 PCSR 1v1 房间。");
            _state = CsMatchState.SearchingLobby;
            CreateNew1v1Lobby(isPublicLobby: true);
        }

        // 遍历大厅列表, 统计 PCSR 专属房的实时排队/激战情况
        private void UpdateQueueStats(List<RaceLobbyEntry> entries)
        {
            int waiting = 0, active = 0, players = 0;
            int std = 0, asc = 0, spr = 0;
            int stdPlayers = 0, ascPlayers = 0, sprPlayers = 0;
            int stdActive = 0, ascActive = 0, sprActive = 0;

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    RaceLobbyEntry e = entries[i];
                    if (e == null) continue;
                    if (!IsPeakCsrMagicMs(e.AutoEndMs)) continue;

                    int roomPlayers = Mathf.Max(0, e.Players);
                    players += roomPlayers;

                    McsrRuleFormat fmt;
                    int ascVal;
                    ParseMagicAutoEndMs(e.AutoEndMs, out fmt, out ascVal);

                    bool isWaiting = e.Status == "lobby" && !e.IsFull;
                    bool isActive = e.Status == "running" || e.Status == "countdown" || e.Status == "loading" || e.IsFull;

                    if (isWaiting)
                    {
                        waiting++;
                        if (fmt == McsrRuleFormat.StandardAllPeak) { std++; stdPlayers += roomPlayers; }
                        else if (fmt == McsrRuleFormat.BiomeSprint) { spr++; sprPlayers += roomPlayers; }
                        else { asc++; ascPlayers += roomPlayers; }
                    }
                    else if (isActive)
                    {
                        active++;
                        if (fmt == McsrRuleFormat.StandardAllPeak) { stdPlayers += roomPlayers; stdActive++; }
                        else if (fmt == McsrRuleFormat.BiomeSprint) { sprPlayers += roomPlayers; sprActive++; }
                        else { ascPlayers += roomPlayers; ascActive++; }
                    }
                }
            }

            _queueStats.WaitingRooms = waiting;
            _queueStats.ActiveRooms = active;
            // 真实人数: 等待房与激战房各自已把自己房内玩家计入 Players, 直接求和即可, 不再臆造
            _queueStats.OnlinePlayers = players;
            _queueStats.StdWaiting = std;
            _queueStats.AscWaiting = asc;
            _queueStats.SprWaiting = spr;
            _queueStats.StdPlayers = stdPlayers;
            _queueStats.AscPlayers = ascPlayers;
            _queueStats.SprPlayers = sprPlayers;
            _queueStats.StdActive = stdActive;
            _queueStats.AscActive = ascActive;
            _queueStats.SprActive = sprActive;
            _queueStats.UpdatedAt = Time.realtimeSinceStartup;
            _queueStats.RefreshedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _queueStats.HasData = true;
        }

        private static string SanitizeInviteCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(code.Length);
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                {
                    sb.Append(char.ToUpperInvariant(c));
                }
            }
            return sb.ToString();
        }

        // 用 天阶 + 路线 反推房间赛制: 天阶 0/-1 且无路线 = 全山, 有路线 = 单群系, 其余高难天阶
        private static bool TryDecodeRuleset(int ascent, int route, out McsrRuleFormat format, out int decodedAscent)
        {
            decodedAscent = Mathf.Clamp(ascent, MinAscent, MaxAscent);
            if (route > 0)
            {
                format = McsrRuleFormat.BiomeSprint;
                return true;
            }
            if (ascent >= 1)
            {
                format = McsrRuleFormat.HardcoreAscent4;
                return true;
            }
            format = McsrRuleFormat.StandardAllPeak;
            return true;
        }

        private bool IsMatchingLobby(RaceLobbyEntry item)
        {
            if (!item.IsLobby || item.IsFull || !item.Compatible) return false;
            // 只接受仍在 lobby 的房间: 已开赛/加载中的房间是别人的进行时,
            // 掉线残留的公开房正是靠这一条挡在门外。
            if (item.Status != "lobby") return false;
            if (item.MaxPlayers != 2 || item.Players != 1) return false;
            if (item.ReviveDead || item.GhostsVisible || !item.SecretSeed) return false;
            if (string.IsNullOrEmpty(item.InviteCode)) return false;
            // 自己刚作废的房间在服务端可能还挂着, 绝不能再把自己送回去
            if (!string.IsNullOrEmpty(_lastOwnedRaceCode)
                && string.Equals(item.InviteCode, _lastOwnedRaceCode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // 排队期间以 _pending* 为准: _selected* 可能已被上一局的 OnRaceJoined 覆写
            McsrRuleFormat wantFormat = _pendingFormat;
            int wantAscent = _pendingAscent;

            if (item.AutoEndMs == GetMagicAutoEndMs(wantFormat, wantAscent)) return true;
            if (IsPeakCsrMagicMs(item.AutoEndMs)) return false;

            McsrRuleFormat fmt;
            int asc;
            if (!TryDecodeRuleset(item.Ascent, item.Route, out fmt, out asc)) return false;
            return fmt == wantFormat && asc == wantAscent;
        }

        private void CreateNew1v1Lobby(bool isPublicLobby)
        {
            System.Random rng = new System.Random(Guid.NewGuid().GetHashCode());
            _seed = rng.Next(100000000, 999999999);
            _levelIndex = PickAuthenticRandomLevelIndex(rng, -1);
            _ascent = _preferredAscent;
            _selectedAscent = _preferredAscent;
            _selectedFormat = _preferredFormat;
            _isRatedMatch = _preferredIsRated;
            _route = _preferredFormat == McsrRuleFormat.BiomeSprint ? rng.Next(1, Mathf.Max(2, RunConfigService.MiniRunBiomeCount + 1)) : 0;

            string biomeLabel = MapCatalog.GetBiomeLabel(_levelIndex);
            _statusMessage = CsrLoc.T(isPublicLobby ? "status.creating" : "status.creating_private", biomeLabel);
            Plugin.Log.LogInfo("[PeakCSR] 正在创建 PCSR 1v1 房间 (公开=" + isPublicLobby + ", 地图=#" + _levelIndex + " " + biomeLabel + ", 难度=" + GetAscentDisplayName(_ascent) + ")...");
            NotifyStateChanged();

            RaceRules rules = new RaceRules();
            rules.ReviveDead = false;

            int magicAutoEndMs = GetMagicAutoEndMs(_selectedFormat, _ascent);

            RaceService.Instance.CreateRace(
                _seed,
                _levelIndex,
                _ascent,
                _route,
                2,
                false,
                false,
                true,
                true,
                magicAutoEndMs,
                rules,
                false,
                isPublicLobby,
                OnRaceJoined,
                OnLobbyError
            );
        }

        // 赛中发起 30 秒双人投票换图
        public void InitiateRerollVote()
        {
            if (_state != CsMatchState.Running && _state != CsMatchState.Countdown && _state != CsMatchState.LoadingLevel && _state != CsMatchState.WaitingOpponent) return;
            if (_myRerollVoteActive || _oppRerollVoteActive) return;

            System.Random rng = new System.Random(Guid.NewGuid().GetHashCode());
            _proposedSeed = rng.Next(100000000, 999999999);
            _proposedLevelIndex = PickAuthenticRandomLevelIndex(rng, _levelIndex);
            _proposedRoute = _selectedFormat == McsrRuleFormat.BiomeSprint ? rng.Next(1, Mathf.Max(2, RunConfigMiniRunBiomeCount())) : 0;

            _myRerollVoteActive = true;
            _rerollVoteDeadline = Time.realtimeSinceStartup + RerollVoteTimeoutSeconds;
            _statusMessage = CsrLoc.T("status.reroll_sent");

            RaceLiveConnection conn = RaceLiveConnection.Instance;
            if (conn != null && conn.IsConnected)
            {
                string packet = HandshakePrefix + "VOTE_REQ|" + _proposedSeed + "|" + _proposedLevelIndex + "|" + _proposedRoute;
                conn.SendChat(packet);
            }
            Plugin.Log.LogInfo("[PeakCSR] 已发起 30s 换图投票，目标新地图: #" + _proposedLevelIndex);

            // 同步在左下角聊天框提示自己
            SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.reroll.req_sent",
                _proposedLevelIndex, MapCatalog.GetBiomeLabel(_proposedLevelIndex)));
            NotifyStateChanged();
        }

        public void RespondRerollVote(bool accept)
        {
            if (!_oppRerollVoteActive) return;
            _oppRerollVoteActive = false;

            RaceLiveConnection conn = RaceLiveConnection.Instance;
            if (!accept)
            {
                if (conn != null && conn.IsConnected)
                {
                    conn.SendChat(HandshakePrefix + "VOTE_NO");
                }
                _statusMessage = CsrLoc.T("status.reroll_declined");
                SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.reroll.declined"));
                NotifyStateChanged();
                return;
            }

            if (conn != null && conn.IsConnected)
            {
                conn.SendChat(HandshakePrefix + "VOTE_YES|" + _proposedSeed + "|" + _proposedLevelIndex + "|" + _proposedRoute);
            }
            ExecuteAgreedReroll(_proposedSeed, _proposedLevelIndex, _proposedRoute);
        }

        private void ExecuteAgreedReroll(int newSeed, int newLevelIndex, int newRoute)
        {
            _myRerollVoteActive = false;
            _oppRerollVoteActive = false;
            _seed = newSeed;
            _levelIndex = newLevelIndex;
            _route = newRoute;

            string biomeLabel = MapCatalog.GetBiomeLabel(_levelIndex);
            _statusMessage = CsrLoc.T("status.reroll_passed", _levelIndex, biomeLabel);
            Plugin.Log.LogInfo("[PeakCSR] 双方同意换图！新地图: #" + _levelIndex + " (" + biomeLabel + "), 新种子: " + _seed);
            SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.reroll.accepted", _levelIndex, biomeLabel));

            if (RaceService.Instance == null || RaceService.Instance.CurrentRace == null) return;

            if (RaceService.Instance.CurrentRace.Status == "lobby")
            {
                if (RaceService.Instance.IsHost)
                {
                    RaceRules rules = new RaceRules { ReviveDead = false };
                    RaceService.Instance.UpdateRaceConfig(
                        _seed,
                        _levelIndex,
                        _ascent,
                        _route,
                        2,
                        false,
                        false,
                        true,
                        true,
                        GetMagicAutoEndMs(_selectedFormat, _ascent),
                        rules,
                        false,
                        RaceService.Instance.CurrentRace.IsPublic,
                        OnLobbyError
                    );
                }
                return;
            }

            if (RaceService.Instance.IsHost)
            {
                _isSwitchingRerollRoom = true;
                if (RaceTimer.Instance != null && RaceTimer.Instance.State == TimerState.Running)
                {
                    RaceTimer.Instance.AbortRun();
                }

                RaceRules rules = new RaceRules { ReviveDead = false };
                int magicAutoEndMs = GetMagicAutoEndMs(_selectedFormat, _ascent);

                RaceService.Instance.CreateRace(
                    _seed,
                    _levelIndex,
                    _ascent,
                    _route,
                    2,
                    false,
                    false,
                    true,
                    true,
                    magicAutoEndMs,
                    rules,
                    false,
                    false,
                    (RaceInfo newRace) =>
                    {
                        _isSwitchingRerollRoom = false;
                        if (newRace == null)
                        {
                            OnLobbyError("换图新建房间失败");
                            return;
                        }
                        // 三步握手第 2 步: 把新房号下发给加入方, 否则对方永远不知道新码
                        BroadcastRerollRoom(newRace.InviteCode);
                        OnRaceJoined(newRace);
                    },
                    OnLobbyError
                );
            }
            else
            {
                // 加入方: 只置 flag 并等房主的 REROLL_ROOM 新房号, 不再只能干等
                _isSwitchingRerollRoom = true;
                _pendingRerollJoinDeadline = Time.realtimeSinceStartup + 20f;
                _statusMessage = CsrLoc.T("status.reroll_waiting_room");
            }
            NotifyStateChanged();
        }

        // 房主把新房号与参数广播给对手 (重复发几次提高到达率, 服务端会去重)
        private void BroadcastRerollRoom(string newCode)
        {
            if (string.IsNullOrEmpty(newCode)) return;
            RaceLiveConnection conn = RaceLiveConnection.Instance;
            if (conn == null || !conn.IsConnected) return;

            string packet = HandshakePrefix + "REROLL_ROOM|" + newCode + "|" + _seed + "|" + _levelIndex + "|" + _route;
            conn.SendChat(packet);
            Plugin.Log.LogInfo("[PeakCSR] 已向对手广播换图新房间号: " + newCode);
        }

        // 加入方收到房主的新房号后, 主动加入
        private void JoinRerollRoom(string newCode, int seed, int levelIndex, int route)
        {
            _pendingRerollJoinCode = newCode;
            _pendingRerollSeed = seed;
            _pendingRerollLevel = levelIndex;
            _pendingRerollRoute = route;

            if (RaceService.Instance == null) return;
            if (!string.Equals(_inviteCode, newCode, StringComparison.OrdinalIgnoreCase))
            {
                // 先退出旧房, 再进新房, 避免两个房间互相覆盖状态
                try { RaceService.Instance.LeaveRace(); } catch { }
            }

            _isSwitchingRerollRoom = true;
            _statusMessage = CsrLoc.T("status.reroll_joining_room", newCode);
            Plugin.Log.LogInfo("[PeakCSR] 收到换图新房号 [" + newCode + "]，正在加入...");
            RaceService.Instance.JoinRace(newCode, newRace =>
            {
                _isSwitchingRerollRoom = false;
                _pendingRerollJoinCode = "";
                _strictRulesetJoin = false;
                if (newRace != null)
                {
                    PcsrSplitTrack.Clear();
                    OnRaceJoined(newRace);
                }
            }, err =>
            {
                _isSwitchingRerollRoom = false;
                Plugin.Log.LogWarning("[PeakCSR] 加入换图新房间失败: " + err);
                _statusMessage = CsrLoc.T("status.error", err);
                NotifyStateChanged();
            });
            NotifyStateChanged();
        }

        // 该群系在地图里的段数 (依据 MapCatalog 的 biome 签名字符串)
        private static int CountSegmentsOfBiome(int levelIndex, int biomeIndex)
        {
            try
            {
                string label = MapCatalog.GetBiomeLabel(levelIndex);
                if (string.IsNullOrEmpty(label)) return 1;
                // 单群系下"本群系内部段数"按实际加载的进度点推算更准, 这里先按 1 段兜底
                // 真实段数由 ProgressFixPatch 侧观察到的进度点数量决定
                return Mathf.Max(1, PcsrSplitTrack.RouteLength);
            }
            catch { return 1; }
        }

        private static int RunConfigMiniRunBiomeCount()
        {
            try { return Mathf.Max(2, RunConfigService.MiniRunBiomeCount + 1); }
            catch { return 5; }
        }

        private static int PickAuthenticRandomLevelIndex(System.Random rng, int excludeIndex)
        {
            int slots = MapCatalog.SlotCount;
            if (slots <= 0) slots = 14;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                int candidate = rng.Next(0, slots);
                if (candidate != excludeIndex && MapCatalog.IsSlotAuthentic(candidate))
                {
                    return candidate;
                }
            }
            return MapCatalog.CurrentLevelIndex;
        }

        // 当前这次 join 是不是恢复到"我自己刚才那局" (换图/重试/观察窗恢复都会走到这里)
        private bool IsJoiningOwnActiveRace(RaceInfo race)
        {
            if (race == null || string.IsNullOrEmpty(race.InviteCode)) return false;
            if (!string.Equals(race.InviteCode, _lastOwnedRaceCode, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(race.InviteCode, _inviteCode, StringComparison.OrdinalIgnoreCase)
                || _isSwitchingRerollRoom
                || _transitionInFlight;
        }

        private void OnRaceJoined(RaceInfo race)
        {
            if (race == null) return;

            // 幽灵房防护: 通过大厅/邀请码进来时, 只接受还在 lobby 状态的房间。
            // 掉线遗留的公开房在服务端可能仍是 "1/2 等待中", 只有真正 join 进来才看得到 status,
            // 这一步是最后一道闸: 一旦发现已经开赛就立刻退出, 绝不掺和别人的残局。
            if (!IsJoiningOwnActiveRace(race) && race.Status != null && race.Status != "lobby")
            {
                Plugin.Log.LogWarning("[PeakCSR] 拒绝加入已开赛的房间 [" + race.InviteCode + "] status=" + race.Status
                    + "，这是掉线残留的幽灵房，立即退出。");
                try { RaceService.Instance.LeaveRace(); } catch { }
                _state = CsMatchState.Idle;
                _queueStartTime = -1f;
                _strictRulesetJoin = false;
                _statusMessage = CsrLoc.T("status.ghost_room_skipped");
                NotifyStateChanged();
                return;
            }

            if (_strictRulesetJoin)
            {
                McsrRuleFormat hostFormat;
                int hostAscent;
                if (TryDecodeRuleset(race.Ascent, race.Route, out hostFormat, out hostAscent)
                    && (hostFormat != _pendingFormat || hostAscent != _pendingAscent))
                {
                    _strictRulesetJoin = false;
                    Plugin.Log.LogWarning("[PeakCSR] 赛制不一致，退出房间 [" + race.InviteCode + "]: 房主="
                        + GetFormatDisplayName(hostFormat, hostAscent) + ", 本地="
                        + GetFormatDisplayName(_pendingFormat, _pendingAscent));
                    RaceService.Instance.LeaveRace();
                    _state = CsMatchState.Idle;
                    _queueStartTime = -1f;
                    _statusMessage = CsrLoc.T("status.ruleset_mismatch",
                        GetFormatDisplayName(hostFormat, hostAscent),
                        GetFormatDisplayName(_pendingFormat, _pendingAscent));
                    NotifyStateChanged();
                    return;
                }
            }
            _strictRulesetJoin = false;

            _inviteCode = race.InviteCode ?? "";
            _seed = race.Seed;
            _levelIndex = race.LevelIndex;
            _ascent = race.Ascent;
            _route = race.Route;

            // 登记本局路线起点与长度, 供分段采集裁剪使用
            // 单群系: 从 (route-1) 号群系开始, 长度 = 该群系包含的段数
            // 全山: -1 / -1 表示取已记录的最大索引
            if (_route > 0)
            {
                int segCount = CountSegmentsOfBiome(_levelIndex, _route - 1);
                PcsrSplitTrack.SetRoute(_route - 1, segCount);
            }
            else
            {
                PcsrSplitTrack.SetRoute(-1, -1);
            }

            // 记录本机"当前这局"的房号, 供幽灵房判定区分"我自己的局"和"别人的残局"
            if (!string.IsNullOrEmpty(race.InviteCode))
            {
                _lastOwnedRaceCode = race.InviteCode;
            }

            if (IsPeakCsrMagicMs(race.AutoEndMs))
            {
                McsrRuleFormat fmt;
                int asc;
                ParseMagicAutoEndMs(race.AutoEndMs, out fmt, out asc);
                _selectedFormat = fmt;
                _selectedAscent = asc;
            }
            else
            {
                McsrRuleFormat fmt;
                int asc;
                if (TryDecodeRuleset(race.Ascent, race.Route, out fmt, out asc))
                {
                    _selectedFormat = fmt;
                    _selectedAscent = asc;
                }
            }

            _state = CsMatchState.WaitingOpponent;
            _hostStartRequested = false;
            _statusMessage = CsrLoc.T("status.waiting_opp", _inviteCode, FormatElapsed(QueueElapsedSeconds));
            Plugin.Log.LogInfo("[PeakCSR] 已进入排位房间 [" + _inviteCode + "]，等待对手加入...");
            SyncFromRaceInfo(race);
            NotifyStateChanged();
        }

        private void OnLobbyError(string err)
        {
            _isSwitchingRerollRoom = false;
            _lobbyFetchInFlight = false;

            bool envMismatch = IsEnvironmentMismatchError(err);
            if (envMismatch)
            {
                _statusMessage = CsrLoc.T("status.env_mismatch",
                    GetEnvironmentGameVersion(), GetEnvironmentMapSetHash());
                Plugin.Log.LogWarning("[PeakCSR] 房间环境不一致 (地图集/版本), 本机 mapSetHash="
                    + GetEnvironmentMapSetHash() + ", gameVersion=" + GetEnvironmentGameVersion() + " | " + err);
                _state = CsMatchState.Idle;
                _queueStartTime = -1f;
                NotifyStateChanged();
                return;
            }

            if (_state == CsMatchState.SearchingLobby)
            {
                _statusMessage = CsrLoc.T("status.error", err);
                Plugin.Log.LogWarning("[PeakCSR] 大厅轮询失败, 稍后重试: " + err);
                _nextLobbyPollTime = Time.realtimeSinceStartup + LobbyPollIntervalSeconds;
                NotifyStateChanged();
                return;
            }

            _state = CsMatchState.Idle;
            _queueStartTime = -1f;
            _statusMessage = CsrLoc.T("status.error", err);
            Plugin.Log.LogWarning("[PeakCSR] 匹配服务异常: " + err);
            NotifyStateChanged();
        }

        private static bool IsEnvironmentMismatchError(string err)
        {
            if (string.IsNullOrEmpty(err)) return false;
            return err.IndexOf("version mismatch", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("mapSetHash", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnLiveChatReceived(ChatEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Body)) return;
            if (!entry.Body.StartsWith(HandshakePrefix, StringComparison.Ordinal)) return;
            if (entry.Mine) return;

            string[] parts = entry.Body.Split('|');
            if (parts.Length < 2) return;

            string cmd = parts[1];

            // 赛前或赛中换图投票
            if (cmd == "VOTE_REQ" && parts.Length >= 5)
            {
                int.TryParse(parts[2], out _proposedSeed);
                int.TryParse(parts[3], out _proposedLevelIndex);
                int.TryParse(parts[4], out _proposedRoute);
                _oppRerollVoteActive = true;
                _rerollVoteDeadline = Time.realtimeSinceStartup + RerollVoteTimeoutSeconds;
                Plugin.Log.LogInfo("[PeakCSR] 收到对手的换图投票请求 (目标地图 #" + _proposedLevelIndex + ")");
                SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.reroll.req_recv",
                    _opponentName, _proposedLevelIndex, MapCatalog.GetBiomeLabel(_proposedLevelIndex)));
                NotifyStateChanged();
                return;
            }
            if (cmd == "VOTE_NO")
            {
                _myRerollVoteActive = false;
                _oppRerollVoteActive = false;
                _statusMessage = CsrLoc.T("status.reroll_declined");
                Plugin.Log.LogInfo("[PeakCSR] 对手拒绝了换图投票。");
                SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.reroll.declined"));
                NotifyStateChanged();
                return;
            }
            if (cmd == "VOTE_YES" && parts.Length >= 5)
            {
                int.TryParse(parts[2], out _proposedSeed);
                int.TryParse(parts[3], out _proposedLevelIndex);
                int.TryParse(parts[4], out _proposedRoute);
                ExecuteAgreedReroll(_proposedSeed, _proposedLevelIndex, _proposedRoute);
                return;
            }

            // 换图三步握手第 3 步: 房主下发新房号, 加入方主动进新房
            if (cmd == "REROLL_ROOM" && parts.Length >= 6)
            {
                string newCode = SanitizeInviteCode(parts[2]);
                int newSeed, newLevel, newRoute;
                int.TryParse(parts[3], out newSeed);
                int.TryParse(parts[4], out newLevel);
                int.TryParse(parts[5], out newRoute);
                if (!string.IsNullOrEmpty(newCode))
                {
                    Plugin.Log.LogInfo("[PeakCSR] 收到房主换图新房号 [" + newCode + "]，准备加入。");
                    JoinRerollRoom(newCode, newSeed, newLevel, newRoute);
                }
                return;
            }

            // 对手宣告本局作废 (掉线等无法确认结果的情况)
            // 双方都掉线时会各自上报作废, 但只有一方还能连上 socket 时, 这条通知能让另一方
            // 立刻跟进作废, 不必干等 45 秒观察窗到期。
            if (cmd == "VOID")
            {
                if (_state != CsMatchState.Idle && !_matchVoided)
                {
                    Plugin.Log.LogWarning("[PeakCSR] 收到对手的作废协议，本局同步作废。");
                    ConcludeVoid(CsrLoc.T("status.void_opp_dc", _opponentName));
                }
                return;
            }

            // 对手主动投降: 直接判我方胜利
            if (cmd == "FORFEIT")
            {
                if (_state != CsMatchState.Finished && _state != CsMatchState.Idle)
                {
                    Plugin.Log.LogInfo("[PeakCSR] 收到对手投降协议，本局判胜。");
                    SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.forfeit.opp", _opponentName));
                    ConcludeMatch(true, CsrLoc.T("status.win.opp_dnf", _opponentName));
                }
                return;
            }

            if (parts.Length < 4) return;
            int parsedElo;
            if (int.TryParse(parts[2], out parsedElo))
            {
                _opponentElo = Mathf.Clamp(parsedElo, 100, 5000);
            }
            if (parts.Length >= 6 && RaceService.Instance != null && !RaceService.Instance.IsHost)
            {
                _isRatedMatch = parts[5] != "0";
            }

            if (!string.IsNullOrEmpty(entry.SteamId))
            {
                _opponentSteamId = entry.SteamId;
            }
            if (!string.IsNullOrEmpty(entry.Name))
            {
                _opponentName = entry.Name;
            }

            bool wasVerified = _opponentVerified;
            _opponentVerified = true;

            if (cmd == "SYN")
            {
                SendHandshakePacket("ACK");
                _handshakeAcked = true;
            }
            else if (cmd == "ACK")
            {
                _handshakeAcked = true;
            }

            if (!wasVerified)
            {
                Plugin.Log.LogInfo("[PeakCSR] 对手握手校验成功: " + _opponentName + " (ELO: " + _opponentElo + ")");
                if (_state == CsMatchState.WaitingOpponent)
                {
                    _statusMessage = CsrLoc.T("status.matched_starting", _opponentName, _opponentElo);
                }
                NotifyStateChanged();
            }
        }

        private void SendHandshakePacket(string kind)
        {
            RaceLiveConnection conn = RaceLiveConnection.Instance;
            if (conn == null || !conn.IsConnected) return;
            string ratedFlag = _isRatedMatch ? "1" : "0";
            string msg = HandshakePrefix + kind + "|" + EloCalculator.CurrentElo + "|" + Plugin.PluginVersion + "|" + (int)_selectedFormat + "|" + ratedFlag;
            conn.SendChat(msg);
        }

        private void OnRaceUpdated(RaceInfo race)
        {
            if (race == null)
            {
                if (_isSwitchingRerollRoom)
                {
                    return;
                }
                // 关键修正: race 消失绝不等于判负。
                // 掉线 / 被踢 / Photo 房间断线 / socket 看门狗重连 / 换图过程 都会让 CurrentRace 变 null。
                if (_state == CsMatchState.LoadingLevel || _state == CsMatchState.Countdown || _state == CsMatchState.Running)
                {
                    BeginDisconnectObservation(_disconnectReason.Length > 0 ? _disconnectReason : "race disappeared");
                }
                else if (_state != CsMatchState.Idle && _state != CsMatchState.Finished && !_observingDisconnect)
                {
                    _state = CsMatchState.Idle;
                    _queueStartTime = -1f;
                    _statusMessage = CsrLoc.T("status.closed");
                    NotifyStateChanged();
                }
                return;
            }

            // 房间回来了: 必须连接真的恢复才能撤销观察窗, 否则只是拿到了本地缓存的旧快照
            if (_observingDisconnect && IsLocalConnectivityHealthy())
            {
                _observingDisconnect = false;
                _disconnectReason = "";
                ClearDisconnectFreeze();
                Plugin.Log.LogInfo("[PeakCSR] 观察窗内房间已恢复，继续对局。");
            }

            SyncFromRaceInfo(race);
        }

        private void SyncFromRaceInfo(RaceInfo race)
        {
            if (race.InviteCode != null) _inviteCode = race.InviteCode;
            _seed = race.Seed;
            _levelIndex = race.LevelIndex;
            _ascent = race.Ascent;
            _route = race.Route;

            string mySteamId = SteamIdentity.GetSteamId64();
            RaceParticipant self = null;
            RaceParticipant opp = null;

            if (race.Participants != null)
            {
                for (int i = 0; i < race.Participants.Count; i++)
                {
                    RaceParticipant p = race.Participants[i];
                    if (p == null || p.IsSpectator || p.Removed) continue;
                    if (p.SteamId == mySteamId) self = p;
                    else opp = p;
                }
            }

            if (self != null)
            {
                bool selfDone = self.State == "finished";
                _selfProgress = self.Progress;
                // 未完赛却报 100% 只可能是旧的"终局强制 1.0"污染, 不能记进战报
                if (!selfDone && self.Progress >= 0.999f && _myBestProgressThisMatch < 0.999f)
                {
                    // 忽略这一帧伪值
                }
                else if (!selfDone && self.Progress < 0.95f && _myBestProgressThisMatch >= 0.999f)
                {
                    // 自愈: 之前被污染成 1.0, 用真实值覆盖回去
                    _myBestProgressThisMatch = self.Progress;
                }
                else if (self.Progress > _myBestProgressThisMatch)
                {
                    _myBestProgressThisMatch = self.Progress;
                }
            }

            if (opp != null)
            {
                if (_opponentSteamId != opp.SteamId)
                {
                    _opponentSteamId = opp.SteamId;
                    _opponentJoinedTime = Time.realtimeSinceStartup;
                    _opponentVerified = false;
                    _handshakeAcked = false;
                    _nextHandshakeSendTime = 0f;
                }
                _opponentName = opp.PlayerName ?? "";
                // 对手回来了 (重连/换房): 清掉"已消失"标记, 避免下次它再掉线时不作废
                _opponentDepartedObserved = false;
                bool oppDone = opp.State == "finished";
                _opponentProgress = opp.Progress;
                _opponentStamina = opp.Stamina ?? 1f;
                _opponentHealth = opp.Health ?? 1f;
                _opponentDown = opp.Down;
                if (!oppDone && opp.Progress >= 0.999f && _oppBestProgressThisMatch < 0.999f)
                {
                    // 同上: 对手掉线/未登顶时被伪造成 100%, 不采纳
                }
                else if (!oppDone && opp.Progress < 0.95f && _oppBestProgressThisMatch >= 0.999f)
                {
                    _oppBestProgressThisMatch = opp.Progress;
                }
                else if (opp.Progress > _oppBestProgressThisMatch)
                {
                    _oppBestProgressThisMatch = opp.Progress;
                }
            }
            else if (_state == CsMatchState.WaitingOpponent)
            {
                ResetOpponentInfo();
            }

            // 实时采集双方分段打卡时间 (营火分段)
            CaptureLiveSplits(self, opp, race);

            if (race.Status == "loading" && _state != CsMatchState.Finished)
            {
                _isSwitchingRerollRoom = false;
                _state = CsMatchState.LoadingLevel;
                _statusMessage = CsrLoc.T("status.loading_island");
            }
            else if (race.Status == "countdown" && _state != CsMatchState.Finished)
            {
                _isSwitchingRerollRoom = false;
                _state = CsMatchState.Countdown;
                long ms = RaceService.Instance != null && RaceService.Instance.MsUntilStart.HasValue ? RaceService.Instance.MsUntilStart.Value : 0;
                if (ms > 0)
                {
                    _statusMessage = CsrLoc.T("status.countdown", (ms / 1000f).ToString("F1"));
                }
                else
                {
                    _state = CsMatchState.Running;
                    _statusMessage = CsrLoc.T("status.running", _opponentName, _opponentElo);
                }
            }
            else if (race.Status == "running" && _state != CsMatchState.Finished)
            {
                _isSwitchingRerollRoom = false;
                _state = CsMatchState.Running;
                if (_matchStartTime < 0f) _matchStartTime = Time.realtimeSinceStartup;
                if (!_myRerollVoteActive && !_oppRerollVoteActive)
                {
                    _statusMessage = CsrLoc.T("status.running", _opponentName, _opponentElo);
                }
            }

            if (!_isSwitchingRerollRoom && (_state == CsMatchState.Running || _state == CsMatchState.Countdown || _state == CsMatchState.LoadingLevel || race.Status == "finished") && _state != CsMatchState.Finished)
            {
                EvaluateWinCondition(race, self, opp);
            }

            NotifyStateChanged();
        }

        // 采集双方分段耗时, 用于赛后战报
        // 主数据源改为自建 PcsrSplitTrack (只记录真实到达, 不填充假数据)
        private void CaptureLiveSplits(RaceParticipant self, RaceParticipant opp, RaceInfo race)
        {
            try
            {
                // 保留一份 RANKED 原始数据作为兜底
                if (self != null && !string.IsNullOrEmpty(self.SteamId))
                {
                    var mine = LiveSplitTrack.For(self.SteamId);
                    if (mine != null)
                    {
                        _liveSplitsFallback.Clear();
                        for (int i = 0; i < mine.Count; i++) _liveSplitsFallback.Add(mine[i]);
                    }
                }

                // 对手: 用 Flags >> 4 的绝对点灯数推断, 但只记录"确实前进"的那一格, 不再伪造填充
                if (opp != null && !string.IsNullOrEmpty(opp.SteamId))
                {
                    int segmentsLit = opp.Flags >> 4;
                    long elapsed = opp.LiveTimeMs.HasValue ? opp.LiveTimeMs.Value : 0L;
                    if (segmentsLit > 0 && elapsed > 0)
                    {
                        PcsrSplitTrack.ObserveSegmentsLit(opp.SteamId, segmentsLit, elapsed);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 采集赛段数据失败: " + ex.Message);
            }
        }

        private void EvaluateWinCondition(RaceInfo race, RaceParticipant self, RaceParticipant opp)
        {
            if (self == null) return;

            // 断线已确认: 本局只可能作废, 任何"掉线 -> 判胜/判负"都不允许
            if (_disconnectConfirmed) return;
            // 本机连接此刻不健康: 手上的 self/opp 数据不可信, 一律不判
            if (!IsLocalConnectivityHealthy()) return;

            // 只有真实完赛 (服务端给出 finished + 名次) 才算数, 不做任何本地推断
            if (self.State == "finished" && self.Rank.HasValue && self.Rank.Value == 1)
            {
                double sec = (self.TotalMs ?? 0) / 1000.0;
                ConcludeMatch(true, CsrLoc.T("status.win.peak", FormatClock(sec)));
                return;
            }
            if (opp != null && opp.State == "finished" && opp.Rank.HasValue && opp.Rank.Value == 1)
            {
                double sec = (opp.TotalMs ?? 0) / 1000.0;
                ConcludeMatch(false, CsrLoc.T("status.loss.peak", _opponentName, FormatClock(sec)));
                return;
            }

            // 死亡判负: 必须是我方自己死亡 (不是 revived/replaced), 且本局确实禁止复活
            if (self.State == "dnf" && !race.ReviveDead)
            {
                ConcludeMatch(false, CsrLoc.T("status.loss.dnf"));
                return;
            }

            // 对手 dnf: 必须同时满足"对手实时心跳还新鲜" 才认定是真实死亡/投降,
            // 否则就是对手掉线 (服务端把掉线者也标成 dnf), 这种情况要作废而不是判我方胜。
            if (opp != null && opp.State == "dnf" && !race.ReviveDead)
            {
                if (IsOpponentHeartbeatFresh())
                {
                    ConcludeMatch(true, CsrLoc.T("status.win.opp_dnf", _opponentName));
                }
                else
                {
                    Plugin.Log.LogWarning("[PeakCSR] 对手 dnf 但心跳已停止，判定为对手掉线，本局作废。");
                    ConcludeVoid(CsrLoc.T("status.void_opp_dc", _opponentName));
                }
                return;
            }

            // 对手从参与者列表里消失: 同样只可能是掉线/被踢, 绝不判胜
            if (opp == null && !string.IsNullOrEmpty(_opponentSteamId) && !_opponentDepartedObserved)
            {
                _opponentDepartedObserved = true;
                Plugin.Log.LogWarning("[PeakCSR] 对手已从房间消失，本局作废（不判胜负、不计分）。");
                ConcludeVoid(CsrLoc.T("status.void_opp_dc", _opponentName));
            }
        }

        // 对手实时心跳是否仍在更新 (RANKED 每秒采样一次, 4 秒内必有新帧)
        private bool IsOpponentHeartbeatFresh()
        {
            try
            {
                if (string.IsNullOrEmpty(_opponentSteamId)) return true;
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn == null || !conn.IsConnected) return true;   // 无法判断时不做反向误判
                foreach (KeyValuePair<byte, string> pair in conn.PeerSteamIds)
                {
                    if (pair.Value != _opponentSteamId) continue;
                    LiveState state;
                    if (conn.Peers.TryGetValue(pair.Key, out state) && state != null)
                    {
                        return Time.realtimeSinceStartup - state.ReceivedAt <= OpponentHeartbeatStaleSeconds;
                    }
                }
                return false;   // socket 上根本没有对手的信道 -> 早已掉线
            }
            catch { return true; }
        }

        public void ForfeitMatch()
        {
            if (_state == CsMatchState.Idle || _state == CsMatchState.SearchingLobby || _state == CsMatchState.Finished) return;

            if (_state == CsMatchState.WaitingOpponent)
            {
                LeaveMatch();
                return;
            }

            // 主动通知对手我方投降, 免得对方只能等轮询 DNF
            try
            {
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn != null && conn.IsConnected)
                {
                    conn.SendChat(HandshakePrefix + "FORFEIT");
                }
            }
            catch { }

            if (RaceService.Instance != null && RaceService.Instance.InRace)
            {
                if (RaceService.Instance.CurrentRace != null && RaceService.Instance.CurrentRace.Status == "running")
                {
                    RaceService.Instance.Forfeit();
                }
                else
                {
                    if (RaceTimer.Instance != null && RaceTimer.Instance.State == TimerState.Running)
                    {
                        RaceTimer.Instance.AbortRun();
                    }
                    RaceService.Instance.LeaveRace();
                }
            }

            SilentHandshakeChatPatch.PushSystemChatMessage(CsrLoc.T("chat.forfeit.self"));
            ConcludeMatch(false, CsrLoc.T("status.loss.forfeit"));
        }

        private void ConcludeMatch(bool iWon, string reason)
        {
            ConcludeMatchInternal(iWon, reason, false);
        }

        // 无法确认结果 (掉线 / 被踢 / Photo 房间断线) 的作废局: 双方都不计分、不计胜负
        private void ConcludeVoid(string reason)
        {
            if (_state == CsMatchState.Finished) return;
            // 同一局可能被观察窗 / 连接检测 / 对手消失三条路径同时触发, 只认第一次
            if (_voidSubmitted) return;
            _voidSubmitted = true;
            _disconnectConfirmed = true;
            _matchVoided = true;
            _voidReason = reason ?? "";
            Plugin.Log.LogWarning("[PeakCSR] 本局作废: " + reason);

            // 尽最大努力把作废决定同步给对手: 对手若还在线, 能立即收尾而不是干等观察窗。
            // 发送失败完全无所谓 (多半是本机已经断网), 所以整段包在 try/catch 里。
            try
            {
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn != null && conn.IsConnected)
                {
                    conn.SendChat(HandshakePrefix + "VOID");
                }
            }
            catch { }

            ConcludeMatchInternal(false, reason, true);

            // 作废后房间已经没有继续存在的意义, 尽早清掉公开房标记,
            // 避免残留的 "1/2 等待中" 房间被第三方插进来打一局没有规则的幽灵对局。
            RetireVoidLobby();

            // 通知云端作废, 让双方都不会被扣分
            if (CloudLeaderboardService.Instance != null && !string.IsNullOrEmpty(_inviteCode) && !string.IsNullOrEmpty(_opponentSteamId))
            {
                long durationMs = RaceTimer.Instance != null ? RaceTimer.Instance.CurrentElapsedMs : 0;
                CloudLeaderboardService.Instance.SubmitSignedMatchReport(
                    _inviteCode, _opponentSteamId, _opponentName, _opponentElo,
                    "", _seed, _levelIndex, _ascent, _selectedFormat, durationMs,
                    _myBestProgressThisMatch, _oppBestProgressThisMatch,
                    true, reason);
            }
        }

        private void ConcludeMatchInternal(bool iWon, string reason, bool isVoid)
        {
            if (_state == CsMatchState.Finished) return;
            _state = CsMatchState.Finished;
            _myRerollVoteActive = false;
            _oppRerollVoteActive = false;

            bool wasPlacement = EloCalculator.TotalGames < EloCalculator.PlacementMatchesCount;

            EloSettlementDetail detail = isVoid
                ? EloCalculator.RecordVoid(_selectedFormat, _ascent, reason)
                : EloCalculator.RecordMatchResultDetailed(
                    _opponentSteamId,
                    _opponentElo,
                    iWon ? 1.0 : 0.0,
                    _selectedFormat,
                    _ascent,
                    _isRatedMatch,
                    _myBestProgressThisMatch,
                    _oppBestProgressThisMatch);

            _lastEloDelta = detail.TotalDelta;
            string sign = _lastEloDelta >= 0 ? "+" : "";
            _lastMatchSummary = reason + (isVoid ? " | 本局作废，不计分" : (" | ELO: " + detail.NewElo + " (" + sign + _lastEloDelta + ")"));
            if (!string.IsNullOrEmpty(detail.AntiBoostNote) && !isVoid)
            {
                _lastMatchSummary += " [" + detail.AntiBoostNote + "]";
            }
            _statusMessage = _lastMatchSummary;
            Plugin.Log.LogInfo("[PeakCSR] 对局结算完成: " + _statusMessage);

            BuildPostMatchData(iWon, reason, detail, wasPlacement, isVoid);

            // 战报不再糊脸自动弹出: 挂一个"可查看"标记, 等玩家在结算界面/主菜单主动打开
            // 作废局也给标记, 否则玩家会以为对局凭空消失
            PcsrMenuWindow.MarkPostMatchPending();

            if (!isVoid && _isRatedMatch && CloudLeaderboardService.Instance != null && !string.IsNullOrEmpty(_inviteCode) && !string.IsNullOrEmpty(_opponentSteamId))
            {
                string mySteamId = SteamIdentity.GetSteamId64();
                string winnerSteamId = iWon ? mySteamId : _opponentSteamId;
                long durationMs = RaceTimer.Instance != null ? RaceTimer.Instance.CurrentElapsedMs : 0;
                CloudLeaderboardService.Instance.SubmitSignedMatchReport(
                    _inviteCode,
                    _opponentSteamId,
                    _opponentName,
                    _opponentElo,
                    winnerSteamId,
                    _seed,
                    _levelIndex,
                    _ascent,
                    _selectedFormat,
                    durationMs,
                    _myBestProgressThisMatch,
                    _oppBestProgressThisMatch
                );
            }

            NotifyStateChanged();
        }

        private void BuildPostMatchData(bool iWon, string reason, EloSettlementDetail detail, bool wasPlacement, bool isVoid)
        {
            CsrPostMatchData data = new CsrPostMatchData
            {
                IWon = iWon,
                IsVoid = isVoid,
                Reason = reason,
                OpponentName = _opponentName,
                OpponentElo = _opponentElo,
                InviteCode = _inviteCode,
                LevelIndex = _levelIndex,
                Seed = _seed,
                Ascent = _ascent,
                Format = _selectedFormat,
                OldElo = detail.OldElo,
                NewElo = detail.NewElo,
                TotalDelta = detail.TotalDelta,
                BaseDelta = detail.BaseDelta,
                ProgressBonus = detail.ProgressBonus,
                MyProgress = _myBestProgressThisMatch,
                OppProgress = _oppBestProgressThisMatch,
                ModeMultiplier = detail.ModeMultiplier,
                IsPlacement = wasPlacement,
                OldTier = detail.OldTierFull,
                NewTier = detail.NewTierFull,
                AntiBoostNote = detail.AntiBoostNote,
                CreatedAt = Time.realtimeSinceStartup
            };
            try { data.BiomeLabel = MapCatalog.GetBiomeLabel(_levelIndex) ?? ""; } catch { data.BiomeLabel = ""; }

            // 分段数据优先用自建采集器 (只记录真实到达)
            RebuildSplitsFromSplitTrack();

            // 汇总总用时
            if (_mySplits.Count > 0) data.MyTotalMs = _mySplits[_mySplits.Count - 1];
            if (_oppSplits.Count > 0) data.OppTotalMs = _oppSplits[_oppSplits.Count - 1];

            // 构建分段对比行: 按本局路线裁剪并重编号, 缺的段老实留空
            int count = Mathf.Max(_mySplits.Count, _oppSplits.Count);
            for (int i = 0; i < count; i++)
            {
                CsrSplitRow row = new CsrSplitRow();
                row.MyMs = i < _mySplits.Count ? _mySplits[i] : 0;
                row.OppMs = i < _oppSplits.Count ? _oppSplits[i] : 0;
                row.Label = BuildSplitLabel(i);
                data.Splits.Add(row);
            }

            LastPostMatch = data;
        }

        // 把 PcsrSplitTrack 的原始记录裁剪成本局路线的相对分段
        private void RebuildSplitsFromSplitTrack()
        {
            _mySplits.Clear();
            _oppSplits.Clear();

            string mySteamId = "";
            try { mySteamId = SteamIdentity.GetSteamId64(); } catch { }

            int routeStart = PcsrSplitTrack.RouteStartIndex;
            int routeLen = PcsrSplitTrack.RouteLength;

            // 单群系: routeStart = 群系索引, routeLen = 该群系包含的段数
            // 全山: routeStart = -1, routeLen = -1 (取已记录的最大索引)
            if (routeStart < 0 && _route > 0)
            {
                routeStart = Mathf.Max(0, _route - 1);
            }

            List<long> mine = PcsrSplitTrack.Export(mySteamId, routeStart, routeLen);
            List<long> theirs = PcsrSplitTrack.Export(_opponentSteamId, routeStart, routeLen);

            // 自建采集器完全没数据时, 才回退到 RANKED 原始分段 (仅全山, 单群系的原始数据不可信)
            if (CountNonZero(mine) == 0 && CountNonZero(theirs) == 0
                && _route <= 0 && _liveSplitsFallback.Count > 0)
            {
                Plugin.Log.LogInfo("[PeakCSR] 自建分段为空，回退使用 RANKED 原始分段 (全山)。");
                for (int i = 0; i < _liveSplitsFallback.Count; i++) _mySplits.Add(_liveSplitsFallback[i]);
                return;
            }

            for (int i = 0; i < mine.Count; i++) _mySplits.Add(mine[i]);
            for (int i = 0; i < theirs.Count; i++) _oppSplits.Add(theirs[i]);
        }

        private static int CountNonZero(List<long> list)
        {
            int c = 0;
            for (int i = 0; i < list.Count; i++) if (list[i] > 0) c++;
            return c;
        }

        // 作废后把房间收起来, 杜绝"公开房残留 1/2 等待中"被第三方插进来。
        // 关键点: 掉线时我们通常已经没法和 RANKED 服务端通话了, 这里的操作必须全部容错,
        // 任何一步失败都不能影响本地已经把本局判成作废这件事。
        private void RetireVoidLobby()
        {
            try
            {
                RaceService svc = RaceService.Instance;
                if (svc == null || svc.CurrentRace == null) return;

                // 已经结束的房间 RANKED 自己会收走, 不必重复操作
                if (svc.CurrentRace.Status == "finished") return;

                // 第一步: 房主把公开房改成私密。
                // 即便退出后房间暂时只剩 1 人, 也不会出现在大厅列表里被第三方捡走。
                // 只有房主能改配置, 加入方这里会静默失败, 这正是期望行为。
                if (svc.IsHost)
                {
                    try
                    {
                        svc.SetPublic(false, err =>
                        {
                            Plugin.Log.LogInfo("[PeakCSR] 作废局公开房已置为私密: " + err);
                        });
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning("[PeakCSR] 作废局置私密失败: " + ex.Message);
                    }
                }

                // 第二步: 预约延迟退出, 让服务端把本机标记 removed。
                // 人数归零后房间由服务端回收, 不会长期挂在列表上。
                // 不立刻退是为了先让 VOID 协议走完 socket, 否则对手收不到通知只能干等观察窗。
                _retireLobbyAt = Time.realtimeSinceStartup + VoidBroadcastGraceSeconds;
                Plugin.Log.LogInfo("[PeakCSR] 作废局将在 " + VoidBroadcastGraceSeconds + "s 后退出房间。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 作废局房间收尾异常: " + ex.Message);
            }
        }

        // 作废后的延迟退房: 先让 VOID 协议有足够时间送达, 再真正断开实时 socket
        private void TickRetireLobby()
        {
            if (_retireLobbyAt < 0f) return;
            if (Time.realtimeSinceStartup < _retireLobbyAt) return;
            _retireLobbyAt = -1f;

            try
            {
                RaceService svc = RaceService.Instance;
                if (svc == null) return;
                if (svc.CurrentRace == null) return;
                if (svc.CurrentRace.Status == "finished") return;
                svc.LeaveRace();
                Plugin.Log.LogInfo("[PeakCSR] 作废局已退出房间，等待服务端回收。");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 作废局退出房间失败（多半已断网，可忽略）: " + ex.Message);
            }
        }

        // 分段名: 单群系只显示该群系内的段, 全山显示从第一段开始
        private string BuildSplitLabel(int index)
        {
            string biome = "";
            int absoluteIndex = index;
            if (_route > 0)
            {
                // 单群系: 本局路线从 (route-1) 号群系开始, 段名取该群系内的相对段
                absoluteIndex = (_route - 1) + index;
            }
            try { biome = MapCatalog.SegmentBiomeName(_levelIndex, absoluteIndex) ?? ""; } catch { biome = ""; }
            return "SEG " + (index + 1) + (string.IsNullOrEmpty(biome) ? "" : " · " + biome);
        }

        public void LeaveMatch()
        {
            // 串行化: 若已有过渡动作在飞 (加入/退房/换房), 先等它结束, 避免 join 与 leave 响应乱序互相覆盖
            if (_transitionInFlight && Time.realtimeSinceStartup < _transitionDeadline)
            {
                _statusMessage = CsrLoc.T("status.leaving");
                NotifyStateChanged();
                return;
            }

            if (RaceService.Instance != null && (RaceService.Instance.InRace || RaceService.Instance.RaceIsOver))
            {
                _transitionInFlight = true;
                _transitionDeadline = Time.realtimeSinceStartup + 15f;
                try { RaceService.Instance.LeaveRace(); } catch { }
                _transitionInFlight = false;
            }
            ResetMatchRuntime();
            _state = CsMatchState.Idle;
            _queueStartTime = -1f;
            _statusMessage = CsrLoc.T("status.ready");
            Plugin.Log.LogInfo("[PeakCSR] 已离开排位房间并返回待机状态。");
            NotifyStateChanged();
        }

        private void ResetMatchRuntime()
        {
            _inviteCode = "";
            _hostStartRequested = false;
            _isSwitchingRerollRoom = false;
            _myRerollVoteActive = false;
            _oppRerollVoteActive = false;
            _selfProgress = 0f;
            _lobbyFetchInFlight = false;
            _nextLobbyPollTime = 0f;
            _strictRulesetJoin = false;
            _queueStartTime = -1f;
            _matchStartTime = -1f;
            _mySplits.Clear();
            _oppSplits.Clear();
            _liveSplitsFallback.Clear();
            _myBestProgressThisMatch = 0f;
            _oppBestProgressThisMatch = 0f;

            // 每局开始前清空观察窗/看门狗/换房握手状态与分段表
            _observingDisconnect = false;
            _disconnectReason = "";
            _disconnectObserveUntil = 0f;
            _connectivityDownSince = -1f;
            _nextConnectivityCheck = 0f;
            _watchedPhase = CsMatchState.Idle;
            _phaseEnteredAt = 0f;
            _liveEverConnected = false;
            _disconnectConfirmed = false;
            _voidSubmitted = false;
            _opponentDepartedObserved = false;
            _matchVoided = false;
            _voidReason = "";
            _retireLobbyAt = -1f;
            _loadingSince = -1f;
            _pendingRerollJoinCode = "";
            _pendingRerollJoinDeadline = 0f;
            _transitionInFlight = false;
            _transitionDeadline = 0f;
            _startCountdownUntil = -1f;
            _convergeFetchInFlight = false;
            _nextConvergePollTime = 0f;
            _roomEnteredAt = 0f;
            PcsrSplitTrack.Clear();
            ProgressFixPatch.Reset();
            try { LiveSplitTrack.Clear(); } catch { }

            ResetOpponentInfo();
        }

        private void ResetOpponentInfo()
        {
            _opponentSteamId = "";
            _opponentName = "";
            _opponentElo = 1000;
            _opponentVerified = false;
            _handshakeAcked = false;
            _opponentProgress = 0f;
            _opponentStamina = 1f;
            _opponentHealth = 1f;
            _opponentDown = false;
            _opponentJoinedTime = 0f;
            _nextHandshakeSendTime = 0f;
        }

        private static void NotifyStateChanged()
        {
            if (OnStateChanged != null) OnStateChanged();
        }
    }
}