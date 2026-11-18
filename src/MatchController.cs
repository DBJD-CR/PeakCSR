using System;
using System.Collections.Generic;
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

    // 基于 RankedPeak.Services.RaceService 构建 PCSR 1v1 实时同种子排位控制器
    public class MatchController : MonoBehaviour
    {
        // 专属房间 AutoEndMs 标识魔数（45 分钟 + 赛制/天阶编码），用于精准区分原版娱乐房与 PeakCSR 排位房
        public const int BaseCsrAutoEndMagicMs = 2700100;
        public const string HandshakePrefix = "__PCSR1V1|";
        public const float RerollVoteTimeoutSeconds = 30f;

        private static MatchController _instance;
        public static MatchController Instance { get { return _instance; } }

        private CsMatchState _state = CsMatchState.Idle;
        private McsrRuleFormat _selectedFormat = McsrRuleFormat.StandardAllPeak;
        private int _selectedAscent = 4;
        private bool _isRatedMatch = true;

        private string _statusMessage = "";
        private string _inviteCode = "";
        private int _seed;
        private int _levelIndex;
        private int _ascent;
        private int _route;

        private string _opponentSteamId = "";
        // 空字符串表示占位, 显示时再按当前语言取文案
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

        // 赛中 30s 投票换图状态
        private bool _myRerollVoteActive;
        private bool _oppRerollVoteActive;
        private float _rerollVoteDeadline;
        private int _proposedSeed;
        private int _proposedLevelIndex;
        private int _proposedRoute;
        private bool _isSwitchingRerollRoom;

        public CsMatchState State { get { return _state; } }
        public McsrRuleFormat SelectedFormat
        {
            get { return _selectedFormat; }
            set { if (_state == CsMatchState.Idle) _selectedFormat = value; }
        }
        public int SelectedAscent
        {
            get { return _selectedFormat == McsrRuleFormat.StandardAllPeak ? 0 : _selectedAscent; }
            set { if (_state == CsMatchState.Idle) _selectedAscent = Mathf.Clamp(value, 1, 8); }
        }
        public bool IsRatedMatch
        {
            get { return _isRatedMatch; }
            set { if (_state == CsMatchState.Idle) _isRatedMatch = value; }
        }
        // 待机态不缓存文案, 否则插件 Awake 时语言尚未就绪会把英文固化下来
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

        public static event Action OnStateChanged;

        public static int GetMagicAutoEndMs(McsrRuleFormat format, int ascent)
        {
            int clampedAscent = format == McsrRuleFormat.StandardAllPeak ? 0 : Mathf.Clamp(ascent, 0, 8);
            return BaseCsrAutoEndMagicMs + ((int)format * 10) + clampedAscent;
        }

        public static bool IsPeakCsrMagicMs(int autoEndMs)
        {
            return autoEndMs >= BaseCsrAutoEndMagicMs && autoEndMs <= BaseCsrAutoEndMagicMs + 40;
        }

        public string GetCurrentFormatDisplayName()
        {
            return GetFormatDisplayName(_selectedFormat, SelectedAscent);
        }

        public static string GetFormatDisplayName(McsrRuleFormat format, int ascent)
        {
            switch (format)
            {
                case McsrRuleFormat.HardcoreAscent4:
                    return CsrLoc.T("format.hardcore.full", Mathf.Clamp(ascent, 1, 8));
                case McsrRuleFormat.BiomeSprint:
                    return CsrLoc.T("format.sprint.full", Mathf.Clamp(ascent, 0, 8));
                default:
                    return CsrLoc.T("format.standard.full");
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
            if (_state == CsMatchState.Idle || _state == CsMatchState.Finished) return;

            // 检查 30s 投票换图超时
            if ((_myRerollVoteActive || _oppRerollVoteActive) && Time.realtimeSinceStartup >= _rerollVoteDeadline)
            {
                _myRerollVoteActive = false;
                _oppRerollVoteActive = false;
                _statusMessage = CsrLoc.T("status.reroll_timeout");
                Plugin.Log.LogInfo("[PeakCSR] 换图投票超时（30秒），继续当前比赛。");
                NotifyStateChanged();
            }

            // 实时维护握手包发送与未验证路人超时踢出
            if (!string.IsNullOrEmpty(_opponentSteamId) && (_state == CsMatchState.WaitingOpponent || _state == CsMatchState.LoadingLevel || _state == CsMatchState.Countdown))
            {
                if ((!_opponentVerified || !_handshakeAcked) && Time.realtimeSinceStartup >= _nextHandshakeSendTime)
                {
                    _nextHandshakeSendTime = Time.realtimeSinceStartup + 1.5f;
                    SendHandshakePacket(_opponentVerified ? "ACK" : "SYN");
                }

                // 房主在等待阶段若对方 10 秒内未响应 PeakCSR 握手（说明是原版大厅误入的路人），自动踢出并继续排位
                if (_state == CsMatchState.WaitingOpponent && !_opponentVerified && RaceService.Instance != null && RaceService.Instance.IsHost)
                {
                    if (_opponentJoinedTime > 0f && Time.realtimeSinceStartup - _opponentJoinedTime > 10f)
                    {
                        Plugin.Log.LogInfo("[PeakCSR] 踢出未安装 PeakCSR 的误入玩家: " + _opponentName);
                        _statusMessage = CsrLoc.T("status.kick_vanilla", _opponentName);
                        string kickId = _opponentSteamId;
                        string kickName = _opponentName;
                        ResetOpponentInfo();
                        RaceService.Instance.RemoveParticipant(kickId, kickName, false, null);
                    }
                }
            }

            // 当双方均已完成 PeakCSR 握手且本机为房主时，自动触发倒计时开赛
            if (_state == CsMatchState.WaitingOpponent && _opponentVerified && !_hostStartRequested)
            {
                if (RaceService.Instance != null && RaceService.Instance.IsHost && RaceService.Instance.CurrentRace != null && RaceService.Instance.CurrentRace.RunnerCount >= 2)
                {
                    _hostStartRequested = true;
                    _statusMessage = CsrLoc.T("status.verified_starting", _opponentElo);
                    Plugin.Log.LogInfo("[PeakCSR] 双方握手完成，房主自动发起同步开赛！");
                    RaceService.Instance.StartRace(OnLobbyError);
                }
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

            // 保留玩家在面板里选择的计分/练习开关, 不再强制覆盖为计分
            ResetMatchRuntime();
            _state = CsMatchState.SearchingLobby;
            string fmtName = GetCurrentFormatDisplayName();
            _statusMessage = CsrLoc.T("status.searching", fmtName);
            Plugin.Log.LogInfo("[PeakCSR] 开始搜索 PCSR 1v1 排位队列，当前赛制: " + fmtName);
            NotifyStateChanged();

            RaceService.Instance.FetchLobbies(OnLobbiesFetched, OnLobbyError);
        }

        // 2. 创建私密好友 1v1 房间（生成房间邀请码供好友定向加入）
        public void CreatePrivate1v1Room()
        {
            if (RaceService.Instance == null)
            {
                _statusMessage = CsrLoc.T("status.not_ready");
                return;
            }

            ResetMatchRuntime();
            _state = CsMatchState.SearchingLobby;
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

            string cleanCode = (code ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(cleanCode))
            {
                _statusMessage = CsrLoc.T("status.code_empty");
                return;
            }

            ResetMatchRuntime();
            _state = CsMatchState.SearchingLobby;
            _statusMessage = CsrLoc.T("status.joining_code", cleanCode);
            Plugin.Log.LogInfo("[PeakCSR] 正在通过邀请码加入 1v1 房间: " + cleanCode);
            NotifyStateChanged();

            RaceService.Instance.JoinRace(cleanCode, OnRaceJoined, OnLobbyError);
        }

        private void OnLobbiesFetched(List<RaceLobbyEntry> entries, int total)
        {
            int expectedMagic = GetMagicAutoEndMs(_selectedFormat, SelectedAscent);
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    RaceLobbyEntry item = entries[i];
                    if (item != null &&
                        item.IsLobby &&
                        !item.IsFull &&
                        item.Compatible &&
                        item.MaxPlayers == 2 &&
                        item.Players == 1 &&
                        !item.ReviveDead &&
                        !item.GhostsVisible &&
                        item.SecretSeed &&
                        item.AutoEndMs == expectedMagic &&
                        !string.IsNullOrEmpty(item.InviteCode))
                    {
                        _statusMessage = CsrLoc.T("status.found_joining", item.InviteCode);
                        Plugin.Log.LogInfo("[PeakCSR] 找到匹配的 PCSR 1v1 房间 [" + item.InviteCode + "]，正在加入...");
                        NotifyStateChanged();
                        RaceService.Instance.JoinRace(item.InviteCode, OnRaceJoined, OnLobbyError);
                        return;
                    }
                }
            }

            CreateNew1v1Lobby(isPublicLobby: true);
        }

        private void CreateNew1v1Lobby(bool isPublicLobby)
        {
            System.Random rng = new System.Random(Guid.NewGuid().GetHashCode());
            _seed = rng.Next(100000000, 999999999);
            _levelIndex = PickAuthenticRandomLevelIndex(rng, -1);
            _ascent = SelectedAscent;
            _route = _selectedFormat == McsrRuleFormat.BiomeSprint ? rng.Next(1, Mathf.Max(2, RunConfigService.MiniRunBiomeCount + 1)) : 0;

            string biomeLabel = MapCatalog.GetBiomeLabel(_levelIndex);
            _statusMessage = CsrLoc.T(isPublicLobby ? "status.creating" : "status.creating_private", biomeLabel);
            Plugin.Log.LogInfo("[PeakCSR] 正在创建 PCSR 1v1 房间 (公开=" + isPublicLobby + ", 地图=#" + _levelIndex + " " + biomeLabel + ", 天阶=A" + _ascent + ")...");
            NotifyStateChanged();

            RaceRules rules = new RaceRules();
            rules.ReviveDead = false;

            int magicAutoEndMs = GetMagicAutoEndMs(_selectedFormat, _ascent);

            // ghostsVisible 强制设为 false（关闭对手幽灵防止跟爬），secretSeed 强制设为 true（盲打种子）
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
            _proposedRoute = _selectedFormat == McsrRuleFormat.BiomeSprint ? rng.Next(1, Mathf.Max(2, RunConfigService.MiniRunBiomeCount + 1)) : 0;

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

            if (RaceService.Instance == null || RaceService.Instance.CurrentRace == null) return;

            // 若还在大厅等待阶段，房主直接调用 UpdateRaceConfig 更新配置即可
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

            // 若已经在海岛跑图中，只有房主重建无缝接力房间并通过协议通知客机自动跟随切图（不扣分）
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
                        OnRaceJoined(newRace);
                    },
                    OnLobbyError
                );
            }
            else
            {
                _isSwitchingRerollRoom = true;
            }
            NotifyStateChanged();
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

        private void OnRaceJoined(RaceInfo race)
        {
            if (race == null) return;
            _inviteCode = race.InviteCode ?? "";
            _seed = race.Seed;
            _levelIndex = race.LevelIndex;
            _ascent = race.Ascent;
            _route = race.Route;

            if (IsPeakCsrMagicMs(race.AutoEndMs))
            {
                int offset = race.AutoEndMs - BaseCsrAutoEndMagicMs;
                int fmtIdx = Mathf.Clamp(offset / 10, 0, 2);
                _selectedFormat = (McsrRuleFormat)fmtIdx;
                if (_selectedFormat != McsrRuleFormat.StandardAllPeak)
                {
                    _selectedAscent = Mathf.Clamp(offset % 10, 1, 8);
                }
            }

            _state = CsMatchState.WaitingOpponent;
            _hostStartRequested = false;
            _statusMessage = CsrLoc.T("status.waiting_opp", _inviteCode);
            Plugin.Log.LogInfo("[PeakCSR] 已进入排位房间 [" + _inviteCode + "]，等待对手加入...");
            SyncFromRaceInfo(race);
            NotifyStateChanged();
        }

        private void OnLobbyError(string err)
        {
            _isSwitchingRerollRoom = false;
            _state = CsMatchState.Idle;
            _statusMessage = CsrLoc.T("status.error", err);
            Plugin.Log.LogWarning("[PeakCSR] 匹配服务异常: " + err);
            NotifyStateChanged();
        }

        private void OnLiveChatReceived(ChatEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Body)) return;
            if (!entry.Body.StartsWith(HandshakePrefix, StringComparison.Ordinal)) return;
            if (entry.Mine) return;

            string[] parts = entry.Body.Split('|');
            if (parts.Length < 2) return;

            string cmd = parts[1];

            // 处理赛中换图投票协议
            if (cmd == "VOTE_REQ" && parts.Length >= 5)
            {
                int.TryParse(parts[2], out _proposedSeed);
                int.TryParse(parts[3], out _proposedLevelIndex);
                int.TryParse(parts[4], out _proposedRoute);
                _oppRerollVoteActive = true;
                _rerollVoteDeadline = Time.realtimeSinceStartup + RerollVoteTimeoutSeconds;
                Plugin.Log.LogInfo("[PeakCSR] 收到对手的换图投票请求 (目标地图 #" + _proposedLevelIndex + ")");
                NotifyStateChanged();
                return;
            }
            if (cmd == "VOTE_NO")
            {
                _myRerollVoteActive = false;
                _oppRerollVoteActive = false;
                _statusMessage = CsrLoc.T("status.reroll_declined");
                Plugin.Log.LogInfo("[PeakCSR] 对手拒绝了换图投票。");
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

            // 处理握手与 ELO 同步协议: __PCSR1V1|SYN/ACK|<elo>|<version>|<format>|<rated>
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
                // 若在加载/倒计时/进行中本地主动退回主菜单关闭了 Race，判本机弃权负
                if (_state == CsMatchState.LoadingLevel || _state == CsMatchState.Countdown || _state == CsMatchState.Running)
                {
                    ConcludeMatch(false, CsrLoc.T("status.aborted_early"));
                }
                else if (_state != CsMatchState.Idle && _state != CsMatchState.Finished)
                {
                    _state = CsMatchState.Idle;
                    _statusMessage = CsrLoc.T("status.closed");
                    NotifyStateChanged();
                }
                return;
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
                _selfProgress = self.Progress;
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
                _opponentProgress = opp.Progress;
                _opponentStamina = opp.Stamina ?? 1f;
                _opponentHealth = opp.Health ?? 1f;
                _opponentDown = opp.Down;
            }
            else if (_state == CsMatchState.WaitingOpponent)
            {
                ResetOpponentInfo();
            }

            // 同步原版 Race.Status 状态机
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
                if (!_myRerollVoteActive && !_oppRerollVoteActive)
                {
                    _statusMessage = CsrLoc.T("status.running", _opponentName, _opponentElo);
                }
            }

            // 胜负判定与零和 ELO 结算
            if (!_isSwitchingRerollRoom && (_state == CsMatchState.Running || _state == CsMatchState.Countdown || _state == CsMatchState.LoadingLevel || race.Status == "finished") && _state != CsMatchState.Finished)
            {
                EvaluateWinCondition(race, self, opp);
            }

            NotifyStateChanged();
        }

        private void EvaluateWinCondition(RaceInfo race, RaceParticipant self, RaceParticipant opp)
        {
            if (self == null) return;

            // 1. 有人率先登顶完赛
            if (self.State == "finished" && self.Rank.HasValue && self.Rank.Value == 1)
            {
                double sec = (self.TotalMs ?? 0) / 1000.0;
                ConcludeMatch(true, CsrLoc.T("status.win.peak", sec.ToString("F2")));
                return;
            }
            if (opp != null && opp.State == "finished" && opp.Rank.HasValue && opp.Rank.Value == 1)
            {
                double sec = (opp.TotalMs ?? 0) / 1000.0;
                ConcludeMatch(false, CsrLoc.T("status.loss.peak", _opponentName, sec.ToString("F2")));
                return;
            }

            // 2. 死亡 / 放弃即 DNF 判负
            if (self.State == "dnf" || (self.Deaths > 0 && !race.ReviveDead))
            {
                ConcludeMatch(false, CsrLoc.T("status.loss.dnf"));
                return;
            }
            if (opp != null && (opp.State == "dnf" || (opp.Deaths > 0 && !race.ReviveDead)))
            {
                ConcludeMatch(true, CsrLoc.T("status.win.opp_dnf", _opponentName));
                return;
            }

            // 3. 比赛已进入加载/倒计时/进行中，但对手拔线离开房间
            if (opp == null && !string.IsNullOrEmpty(_opponentSteamId) && (_state == CsMatchState.Running || _state == CsMatchState.Countdown || _state == CsMatchState.LoadingLevel))
            {
                ConcludeMatch(true, CsrLoc.T("status.win.opp_dc", _opponentName));
            }
        }

        public void ForfeitMatch()
        {
            if (_state == CsMatchState.Idle || _state == CsMatchState.SearchingLobby || _state == CsMatchState.Finished) return;

            if (_state == CsMatchState.WaitingOpponent)
            {
                LeaveMatch();
                return;
            }

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

            ConcludeMatch(false, CsrLoc.T("status.loss.forfeit"));
        }

        private void ConcludeMatch(bool iWon, string reason)
        {
            if (_state == CsMatchState.Finished) return;
            _state = CsMatchState.Finished;
            _myRerollVoteActive = false;
            _oppRerollVoteActive = false;

            string antiBoostNote;
            _lastEloDelta = EloCalculator.RecordMatchResult(_opponentSteamId, _opponentElo, iWon ? 1.0 : 0.0, _selectedFormat, _ascent, _isRatedMatch, out antiBoostNote);
            string sign = _lastEloDelta >= 0 ? "+" : "";
            _lastMatchSummary = reason + " | ELO: " + EloCalculator.CurrentElo + " (" + sign + _lastEloDelta + ")";
            if (!string.IsNullOrEmpty(antiBoostNote))
            {
                _lastMatchSummary += " [" + antiBoostNote + "]";
            }
            _statusMessage = _lastMatchSummary;
            Plugin.Log.LogInfo("[PeakCSR] 对局结算完成: " + _statusMessage);

            if (_isRatedMatch && CloudLeaderboardService.Instance != null && !string.IsNullOrEmpty(_inviteCode) && !string.IsNullOrEmpty(_opponentSteamId))
            {
                string mySteamId = SteamIdentity.GetSteamId64();
                string winnerSteamId = iWon ? mySteamId : _opponentSteamId;
                long durationMs = RaceTimer.Instance != null ? RaceTimer.Instance.CurrentElapsedMs : 0;
                CloudLeaderboardService.Instance.SubmitSignedMatchReport(
                    _inviteCode,
                    _opponentSteamId,
                    _opponentName,
                    winnerSteamId,
                    _seed,
                    _levelIndex,
                    _ascent,
                    _selectedFormat,
                    durationMs
                );
            }

            NotifyStateChanged();
        }

        public void LeaveMatch()
        {
            if (RaceService.Instance != null && (RaceService.Instance.InRace || RaceService.Instance.RaceIsOver))
            {
                RaceService.Instance.LeaveRace();
            }
            ResetMatchRuntime();
            _state = CsMatchState.Idle;
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