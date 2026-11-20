using System;
using RankedPeak.Live;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // 本地玩家档案门面 (原 EloCalculator 静态门面的保留外观)
    // UI / 结算服务经由它访问 PlayerRecord, 内部状态全部收进 Ladder 模块的记录实体
    public sealed class PlayerProfileBridge : IEncounterLedger
    {
        private readonly PlayerRecord _record;
        private readonly LocalProfileStore _store;
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<long>> _recentOpponents
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<long>>(StringComparer.Ordinal);

        public PlayerProfileBridge(PlayerRecord record, LocalProfileStore store)
        {
            _record = record;
            _store = store;
        }

        public PlayerRecord Record { get { return _record; } }

        public int CurrentElo { get { return _record.Elo; } }
        public int PeakElo { get { return _record.PeakElo; } }
        public int SeedElo { get { return _record.SeedElo; } }
        public bool SeededFromVanilla { get { return _record.SeededFromVanilla; } }
        public int Wins { get { return _record.Wins; } }
        public int Losses { get { return _record.Losses; } }
        public int Draws { get { return _record.Draws; } }
        public int Streak { get { return _record.Streak; } }
        public int TotalGames { get { return _record.TotalGames; } }
        public int WinRatePercent { get { return _record.WinRatePercent; } }
        public int FavoriteMap { get { return _record.FavoriteMap; } }
        public int FavoriteAscent { get { return _record.FavoriteAscentEffective; } }
        public int FavoriteFormat { get { return _record.FavoriteFormatEffective; } }

        public int ModeWins(McsrRuleFormat format) { return _record.ModeWins(format); }
        public int ModeLosses(McsrRuleFormat format) { return _record.ModeLosses(format); }
        public int ModeGames(McsrRuleFormat format) { return _record.ModeGames(format); }
        public int ModeWinRatePercent(McsrRuleFormat format) { return _record.ModeWinRatePercent(format); }

        // 保存玩家自选的群系索引 (见 PcsrBiomes.All), -1 表示未设置
        public void SetFavoriteMap(int biomeIndex)
        {
            _record.SetFavoriteMap(biomeIndex, PcsrBiomes.Count);
            _store.Save(_record);
        }

        // ===== Anti-Boost 近期交手台账 =====
        int IEncounterLedger.PruneAndCount(string opponentSteamId, long nowMinutesUtc)
        {
            System.Collections.Generic.List<long> history;
            if (!_recentOpponents.TryGetValue(opponentSteamId, out history))
            {
                return 0;
            }
            history.RemoveAll(t => nowMinutesUtc - t > RatingRules.Instance.AntiBoost.WindowMinutes);
            return history.Count;
        }

        void IEncounterLedger.Record(string opponentSteamId, long nowMinutesUtc)
        {
            System.Collections.Generic.List<long> history;
            if (!_recentOpponents.TryGetValue(opponentSteamId, out history))
            {
                history = new System.Collections.Generic.List<long>();
                _recentOpponents[opponentSteamId] = history;
            }
            history.Add(nowMinutesUtc);
        }

        // ===== 结算入口 =====
        public SettlementDetail RecordMatchResultDetailed(
            string opponentSteamId,
            int opponentElo,
            double actualScore,
            McsrRuleFormat format,
            int ascent,
            bool isRated,
            float selfProgress01,
            float oppProgress01)
        {
            PlayerRecord rec = _record;
            int oldElo = rec.Elo;
            string oldTier = TierCatalog.GetTierName(oldElo);

            string antiBoostKey;
            double antiBoostWeight = AntiBoostPolicy.Evaluate(
                isRated, _store.SelfSteamId, opponentSteamId, opponentElo, rec.Elo, this, out antiBoostKey);
            string antiBoostNote = string.IsNullOrEmpty(antiBoostKey) ? "" : CsrLoc.T(antiBoostKey);

            double modeMult = EloEngine.GetModeMultiplier(format, ascent);
            double totalWeight = antiBoostWeight * modeMult;

            int totalGames = rec.TotalGames;
            bool isPlacement = totalGames < EloEngine.PlacementMatchesCount;
            int baseK = EloEngine.GetBaseKFactor(totalGames, rec.Elo);

            int progressBonus = isRated
                ? EloEngine.ComputeProgressModifier(actualScore, selfProgress01, oppProgress01, format, ascent, isPlacement, antiBoostWeight)
                : 0;

            int minWinDelta = EloEngine.GetMinWinDelta(format, ascent, rec.Elo);
            int maxLossDelta = EloEngine.GetMaxLossDelta(format);

            int baseDelta;
            int delta;
            if (isRated)
            {
                delta = EloEngine.ComputeDelta(rec.Elo, opponentElo, actualScore, baseK, totalWeight, antiBoostWeight,
                    minWinDelta, maxLossDelta, progressBonus, out baseDelta);
            }
            else
            {
                baseDelta = 0;
                delta = 0;
            }

            if (isRated)
            {
                rec.ApplyRatedResult(format, ascent, actualScore, delta);
            }
            else
            {
                rec.ApplyUnratedResult(delta);
            }

            // 落地后的真实变化: 战报与云端历史都必须记录"实际发生了多少"
            int appliedDelta = rec.Elo - oldElo;
            int floorAdjust = appliedDelta - baseDelta - progressBonus;

            _store.Save(rec);
            EventBus.PublishRatingChanged();

            return new SettlementDetail
            {
                OldElo = oldElo,
                NewElo = rec.Elo,
                TotalDelta = appliedDelta,
                BaseDelta = baseDelta,
                ProgressBonus = progressBonus,
                FloorAdjust = floorAdjust,
                MinWinDelta = minWinDelta,
                ModeMultiplier = modeMult,
                AntiBoostWeight = antiBoostWeight,
                IsPlacement = isPlacement,
                OldTierFull = oldTier,
                NewTierFull = TierCatalog.GetTierName(rec.Elo),
                AntiBoostNote = antiBoostNote,
                Void = false
            };
        }

        // 断线 / 无法确认结果的作废局: 不改动 ELO, 也不计入胜负场次
        public SettlementDetail RecordVoid(McsrRuleFormat format, int ascent, string reason)
        {
            PlayerRecord rec = _record;
            return new SettlementDetail
            {
                OldElo = rec.Elo,
                NewElo = rec.Elo,
                TotalDelta = 0,
                BaseDelta = 0,
                ProgressBonus = 0,
                FloorAdjust = 0,
                MinWinDelta = EloEngine.GetMinWinDelta(format, ascent, rec.Elo),
                ModeMultiplier = EloEngine.GetModeMultiplier(format, ascent),
                AntiBoostWeight = 1.0,
                IsPlacement = rec.TotalGames < EloEngine.PlacementMatchesCount,
                OldTierFull = TierCatalog.GetTierName(rec.Elo),
                NewTierFull = TierCatalog.GetTierName(rec.Elo),
                AntiBoostNote = reason,
                Void = true
            };
        }

        // 云端权威分数覆盖
        public void ApplyCloudAuthoritativeElo(int cloudElo)
        {
            _record.ApplyCloudAuthoritativeElo(cloudElo);
            _store.Save(_record);
            // 每次权威分落地都推进一次修订号
            _ratingRevision++;
            EventBus.PublishRatingChanged();
        }

        // 评分修订号: 递增即代表本地权威分发生了变化
        public int RatingRevision { get { return _ratingRevision; } }
        private int _ratingRevision;

        // 云端权威档案覆盖 (含分赛制战绩)
        // draws: 云端平局数, 传 -1 表示本次没有拿到权威档案
        public void ApplyCloudAuthoritativeState(
            int elo, int peak, int wins, int losses, int draws, int streak,
            int winsAllPeak, int lossesAllPeak,
            int winsHardcore, int lossesHardcore,
            int winsSprint, int lossesSprint,
            int favoriteMap, int favoriteAscent, int favoriteFormat)
        {
            _record.ApplyCloudAuthoritativeState(elo, peak, wins, losses, draws, streak,
                winsAllPeak, lossesAllPeak, winsHardcore, lossesHardcore, winsSprint, lossesSprint,
                favoriteMap, favoriteAscent, favoriteFormat, PcsrBiomes.Count);
            _store.Save(_record);
            _ratingRevision++;
            EventBus.PublishRatingChanged();
        }

        public void Save()
        {
            _store.Save(_record);
        }
    }

    // 顶层对局协调器 (< 250 行核心逻辑):
    //  - 面板偏好 (PreferredFormat/PreferredAscent/PreferredIsRated) 留在本层
    //  - 全局后台任务 (队列心跳/延迟退房) 留在本层 TickGlobalServices
    //  - 单局 30+ 状态全部封装进 MatchSession, 每局 new, 结束置 null, 零残留风险
    public sealed class MatchCoordinator : IDisposable
    {
        // 顶层单例 (供 Patches 静态闸门与 UI 访问)
        private static MatchCoordinator _instance;
        public static MatchCoordinator Instance { get { return _instance; } }

        public readonly IRankedPeakGateway Gateway;
        public readonly LobbyMatchmaker Matchmaker;
        public readonly HandshakeNegotiator Handshake;
        public readonly RerollVoteManager Reroll;
        public readonly ConnectivityGuardian Guardian;
        public readonly MatchSettlementService Settlement;
        public readonly PlayerProfileBridge Profile;

        // 天梯同步服务引用 (结算上报用; 由 Boot 装配时注入)
        public LadderSyncService Ladder { get; set; }

        // 面板选择意图值: 与"当前房间赛制"分离, 避免对局结束后 Selection 被静默丢弃
        private McsrRuleFormat _preferredFormat = McsrRuleFormat.StandardAllPeak;
        private int _preferredAscent = 0;
        private bool _preferredIsRated = true;

        private MatchSession _currentSession;
        private readonly SessionPhaseMachine _phaseMachine;
        private bool _raceEventsHooked;

        public MatchCoordinator(IRankedPeakGateway gateway, PlayerProfileBridge profile)
        {
            _instance = this;
            Gateway = gateway;
            Profile = profile;
            Matchmaker = new LobbyMatchmaker(gateway, this);
            Handshake = new HandshakeNegotiator(gateway, this);
            Reroll = new RerollVoteManager(gateway, this);
            Guardian = new ConnectivityGuardian(gateway, this);
            Settlement = new MatchSettlementService(gateway, this);
            _phaseMachine = new SessionPhaseMachine(new IdlePhase());
            _phaseMachine.Register(CsMatchState.SearchingLobby, new SearchingPhase(this));
            _phaseMachine.Register(CsMatchState.WaitingOpponent, new WaitingPhase(this));
            _phaseMachine.Register(CsMatchState.LoadingLevel, new LoadingPhase(this));

            HookRaceEvents();

            // Gateway 需要知道当前会话赛制来编码房间魔数
            gateway.CoordinatorFormat = delegate
            {
                MatchSession s = _currentSession;
                return s != null ? s.Format : _preferredFormat;
            };
        }

        private void HookRaceEvents()
        {
            if (_raceEventsHooked) return;
            _raceEventsHooked = true;
            Gateway.RaceUpdated += OnRaceUpdated;
            Gateway.ChatEntryReceived += OnLiveChatReceived;
        }

        public void Dispose()
        {
            if (_raceEventsHooked)
            {
                _raceEventsHooked = false;
                Gateway.RaceUpdated -= OnRaceUpdated;
                Gateway.ChatEntryReceived -= OnLiveChatReceived;
            }
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        // ===== 静态闸门 (护栏 2): 补丁判定当前是否 PCSR 对局的唯一入口 =====
        public static bool IsCurrentPeakCsrMatch()
        {
            MatchCoordinator mc = _instance;
            if (mc == null) return false;
            MatchSession s = mc._currentSession;
            if (s == null) return false;
            if (s.State == CsMatchState.SearchingLobby || s.State == CsMatchState.WaitingOpponent) return false;
            try
            {
                RaceInfo race = mc.Gateway.CurrentRace;
                if (race == null) race = mc.Gateway.LastFinishedRace;
                if (race != null && RoomMagicCodec.IsCsrMagicMs(race.AutoEndMs)) return true;
            }
            catch
            {
            }
            // 兜底: 状态机已在 PCSR 对局中且持有 PCSR 房间码
            return !string.IsNullOrEmpty(s.InviteCode);
        }

        // ===== 面板偏好 =====
        public McsrRuleFormat PreferredFormat
        {
            get { return _preferredFormat; }
            set { _preferredFormat = value; }
        }

        public int PreferredAscent
        {
            get { return _preferredAscent; }
            set { _preferredAscent = RoomMagicCodec.ClampAscent(value); }
        }

        public bool PreferredIsRated
        {
            get { return _preferredIsRated; }
            set { _preferredIsRated = value; }
        }

        // UI 兼容外观: Selected* 永远读写偏好值 (旧行为: Idle 才同步到房间值, 新架构里房间值在 Session 内)
        public McsrRuleFormat SelectedFormat
        {
            get { return _preferredFormat; }
            set { _preferredFormat = value; }
        }

        public int SelectedAscent
        {
            get { return _preferredAscent; }
            set { _preferredAscent = RoomMagicCodec.ClampAscent(value); }
        }

        public bool IsRatedMatch
        {
            get { return _preferredIsRated; }
            set { _preferredIsRated = value; }
        }

        // ===== UI 只读视图 =====
        public MatchSession CurrentSession { get { return _currentSession; } }

        public CsMatchState State
        {
            get
            {
                MatchSession s = _currentSession;
                return s != null ? s.State : CsMatchState.Idle;
            }
        }

        public string StatusMessage
        {
            get
            {
                MatchSession s = _currentSession;
                if (s == null)
                {
                    // Idle: 优先展示最后一条错误/提示, 无则回退"准备就绪"
                    return string.IsNullOrEmpty(_idleStatusMessage)
                        ? CsrLoc.T("status.ready")
                        : _idleStatusMessage;
                }
                if (string.IsNullOrEmpty(s.StatusMessage))
                {
                    return CsrLoc.T("status.ready");
                }
                return s.StatusMessage;
            }
        }

        public CsrQueueStats QueueStats { get { return Matchmaker.QueueStats; } }

        public string GetCurrentFormatDisplayName()
        {
            return MatchNaming.GetFormatDisplayName(PreferredFormat, PreferredAscent);
        }

        public float QueueElapsedSeconds
        {
            get
            {
                MatchSession s = _currentSession;
                return s != null ? s.QueueElapsedSeconds : 0f;
            }
        }

        public float RerollVoteRemainingSeconds
        {
            get
            {
                MatchSession s = _currentSession;
                return s != null ? s.RerollVoteRemainingSeconds : 0;
            }
        }

        // 对局过程中收起窗口用 (原 OnMatchStateChanged 读取的派生状态)
        public bool IsInGamePhase
        {
            get
            {
                MatchSession s = _currentSession;
                return s != null
                    && (s.State == CsMatchState.LoadingLevel || s.State == CsMatchState.Countdown || s.State == CsMatchState.Running);
            }
        }

        // ===== 公共 API (UI 命令入口) =====

        // 1. 一键搜索同赛制公开 1v1 房间，若无则创建带专属魔数与规则的排位房
        public void Queue1v1()
        {
            if (Gateway.ServiceAvailable == false)
            {
                PublishIdleStatus(CsrLoc.T("status.not_ready"));
                return;
            }

            StartNewSession(_preferredFormat, _preferredAscent, _preferredIsRated, strictRuleset: true);
            MatchSession s = _currentSession;
            s.State = CsMatchState.SearchingLobby;
            s.PendingFormat = _preferredFormat;
            s.PendingAscent = _preferredAscent;
            s.StatusMessage = CsrLoc.T("status.searching", GetCurrentFormatDisplayName(), CsrFormat.FormatElapsed(0f));
            PcsrLog.LogInfo("[PeakCSR] 开始搜索 PCSR 1v1 排位队列，当前赛制: " + GetCurrentFormatDisplayName());
            NotifyStateChanged();

            Matchmaker.TickQueuePoll(s);
        }

        // 2. 创建私密好友 1v1 房间
        public void CreatePrivate1v1Room()
        {
            if (Gateway.ServiceAvailable == false)
            {
                PublishIdleStatus(CsrLoc.T("status.not_ready"));
                return;
            }

            StartNewSession(_preferredFormat, _preferredAscent, _preferredIsRated, strictRuleset: false);
            Matchmaker.CreateNew1v1Lobby(_currentSession, isPublicLobby: false);
        }

        // 3. 通过好友分享的房间邀请码直接加入 1v1 房间
        public void JoinByInviteCode(string code)
        {
            if (Gateway.ServiceAvailable == false)
            {
                PublishIdleStatus(CsrLoc.T("status.not_ready"));
                return;
            }

            string cleanCode = PcsrProtocol.SanitizeRoomCode(code);
            if (string.IsNullOrEmpty(cleanCode))
            {
                PublishIdleStatus(CsrLoc.T("status.code_empty"));
                return;
            }

            StartNewSession(_preferredFormat, _preferredAscent, _preferredIsRated, strictRuleset: false);
            MatchSession s = _currentSession;
            s.StatusMessage = CsrLoc.T("status.joining_code", cleanCode);
            PcsrLog.LogInfo("[PeakCSR] 正在通过邀请码加入 1v1 房间: " + cleanCode);
            NotifyStateChanged();

            Gateway.JoinRace(cleanCode, race => OnRaceJoined(s, race), err => Matchmaker.OnLobbyError(s, err));
        }

        public void InitiateRerollVote()
        {
            MatchSession s = _currentSession;
            if (s == null) return;
            Reroll.InitiateVote(s);
        }

        public void RespondRerollVote(bool accept)
        {
            MatchSession s = _currentSession;
            if (s == null) return;
            Reroll.RespondVote(s, accept);
        }

        public void ForfeitMatch()
        {
            MatchSession s = _currentSession;
            if (s == null) return;
            if (s.State == CsMatchState.Idle || s.State == CsMatchState.SearchingLobby || s.State == CsMatchState.Finished) return;

            if (s.State == CsMatchState.WaitingOpponent)
            {
                LeaveMatch();
                return;
            }

            // 主动通知对手我方投降, 免得对方只能等轮询 DNF
            try
            {
                Gateway.SendChat(PcsrProtocol.EncodeForfeit());
            }
            catch
            {
            }

            if (Gateway.InRace)
            {
                RaceInfo race = Gateway.CurrentRace;
                if (race != null && race.Status == "running")
                {
                    Gateway.Forfeit();
                }
                else
                {
                    if (Gateway.TimerState == RankedPeak.Timer.TimerState.Running)
                    {
                        try { RankedPeak.Timer.RaceTimer.Instance.AbortRun(); } catch { }
                    }
                    Gateway.LeaveRace();
                }
            }

            Gateway.PushSystemChat(CsrLoc.T("chat.forfeit.self"));
            Settlement.ConcludeMatch(s, false, CsrLoc.T("status.loss.forfeit"));
        }

        public void LeaveMatch()
        {
            MatchSession s = _currentSession;
            // 串行化: 若已有过渡动作在飞 (加入/退房/换房), 先等它结束, 避免 join 与 leave 响应乱序互相覆盖
            if (s != null && s.TransitionInFlight && Time.realtimeSinceStartup < s.TransitionDeadline)
            {
                // 记下离开意图
                s.PendingLeaveRequest = true;
                s.StatusMessage = CsrLoc.T("status.leaving");
                s.Notify();
                return;
            }

            if (Gateway.InRace || Gateway.RaceIsOver)
            {
                Gateway.LeaveRace();
            }
            TearDownSession("leave");
            PublishIdleStatus(CsrLoc.T("status.ready"));
            PcsrLog.LogInfo("[PeakCSR] 已离开排位房间并返回待机状态。");
            NotifyStateChanged();
        }

        // ===== 顶层协调逻辑 =====

        private void StartNewSession(McsrRuleFormat format, int ascent, bool rated, bool strictRuleset)
        {
            // 每次开启新匹配直接 new 会话, 旧会话对象整个丢弃 (零残留风险)
            ClearIdleStatus();
            _currentSession = new MatchSession(Gateway, format, RoomMagicCodec.ClampAscent(ascent), rated,
                Time.realtimeSinceStartup, NotifyStateChanged);
            _currentSession.StrictRulesetJoin = strictRuleset;
            _currentSession.PendingFormat = format;
            _currentSession.PendingAscent = RoomMagicCodec.ClampAscent(ascent);
            // 开新局前重置进度修正状态、分段采集游标与原版分段兜底
            ProgressMonotonicState.Reset();
            SplitCapturePatch.ResetTracking();
            Gateway.ClearLiveSplits();
        }

        // 离开房间时直接置 null: 单局状态随对象一起蒸发
        // reason 时的最后一条文案保留给 UI: 置 null 前先写入 _idleStatusMessage,
        // 否则"赛制不一致/环境不一致"等错误提示会跟着会话对象一起蒸发。
        public void TearDownSession(string reason)
        {
            MatchSession s = _currentSession;
            if (s != null)
            {
                _idleStatusMessage = s.StatusMessage;
                s.Splits.Clear();
            }
            _currentSession = null;
        }

        // Idle 状态下的最后一次可见文案 (错误提示/幽灵房跳过提示等)
        private string _idleStatusMessage = "";

        private void PublishIdleStatus(string msg)
        {
            // Idle 状态没有会话容器: 文案存本层, StatusMessage 属性读取时兜底展示
            _idleStatusMessage = msg;
            NotifyStateChanged();
        }

        // 开新会话时清掉上一局的遗留文案, 避免旧错误串台
        private void ClearIdleStatus()
        {
            _idleStatusMessage = "";
        }

        public void NotifyStateChanged()
        {
            EventBus.PublishMatchStateChanged();
        }

        // ===== join 结果统一入口 (原 OnRaceJoined 幽灵房/赛制校验逻辑) =====
        public void OnRaceJoined(MatchSession session, RaceInfo race)
        {
            if (race == null) return;
            // 迟到的 join 结果: 玩家已经开了新会话, 旧结果不拿来处理 (幽灵房分支会顺手拆掉新会话)
            if (session != null && !ReferenceEquals(session, _currentSession)) return;

            // 幽灵房防护: 通过大厅/邀请码进来时, 只接受还在 lobby 状态的房间。
            // 掉线遗留的公开房在服务端可能仍是 "1/2 等待中", 只有真正 join 进来才看得到 status,
            // 这一步是最后一道闸: 一旦发现已经开赛就立刻退出, 绝不掺和别人的残局。
            if (!session.IsJoiningOwnActiveRace(race) && race.Status != null && race.Status != "lobby")
            {
                PcsrLog.LogWarning("[PeakCSR] 拒绝加入已开赛的房间 [" + race.InviteCode + "] status=" + race.Status
                    + "，这是掉线残留的幽灵房，立即退出。");
                try { Gateway.LeaveRace(); } catch { }
                TearDownSession("ghost_room");
                PublishIdleStatus(CsrLoc.T("status.ghost_room_skipped"));
                NotifyStateChanged();
                return;
            }

            if (session.StrictRulesetJoin)
            {
                McsrRuleFormat hostFormat;
                int hostAscent;
                if (RoomMagicCodec.TryDecodeRuleset(race.Ascent, race.Route, out hostFormat, out hostAscent)
                    && (hostFormat != session.PendingFormat || hostAscent != session.PendingAscent))
                {
                    session.StrictRulesetJoin = false;
                    PcsrLog.LogWarning("[PeakCSR] 赛制不一致，退出房间 [" + race.InviteCode + "]: 房主="
                        + MatchNaming.GetFormatDisplayName(hostFormat, hostAscent) + ", 本地="
                        + MatchNaming.GetFormatDisplayName(session.PendingFormat, session.PendingAscent));
                    Gateway.LeaveRace();
                    TearDownSession("ruleset_mismatch");
                    PublishIdleStatus(CsrLoc.T("status.ruleset_mismatch",
                        MatchNaming.GetFormatDisplayName(hostFormat, hostAscent),
                        MatchNaming.GetFormatDisplayName(session.PendingFormat, session.PendingAscent)));
                    NotifyStateChanged();
                    return;
                }
            }
            session.StrictRulesetJoin = false;

            Matchmaker.RegisterRoomEntry(session, race);
            NotifyStateChanged();
        }

        // ===== RaceUpdated 事件 (原 OnRaceUpdated) =====
        private void OnRaceUpdated(RaceInfo race)
        {
            MatchSession s = _currentSession;
            if (s == null) return;

            if (race == null)
            {
                if (s.SwitchingRerollRoom)
                {
                    return;
                }
                // 关键修正: race 消失绝不等于判负。
                // 掉线 / 被踢 / Photo 房间断线 / socket 看门狗重连 / 换图过程 都会让 CurrentRace 变 null。
                if (s.State == CsMatchState.LoadingLevel || s.State == CsMatchState.Countdown || s.State == CsMatchState.Running)
                {
                    Guardian.BeginDisconnectObservation(s, s.DisconnectReason.Length > 0 ? s.DisconnectReason : "race disappeared");
                }
                else if (s.State != CsMatchState.Idle && s.State != CsMatchState.Finished && !s.ObservingDisconnect)
                {
                    TearDownSession("race_closed");
                    PublishIdleStatus(CsrLoc.T("status.closed"));
                    NotifyStateChanged();
                }
                return;
            }

            // 房间回来了: 必须连接真的恢复才能撤销观察窗, 否则只是拿到了本地缓存的旧快照
            if (s.ObservingDisconnect && Guardian.IsLocalConnectivityHealthy(s))
            {
                s.ObservingDisconnect = false;
                s.DisconnectReason = "";
                Guardian.ClearDisconnectFreeze(s);
                PcsrLog.LogInfo("[PeakCSR] 观察窗内房间已恢复，继续对局。");
            }

            s.SyncFromRaceInfo(race);
        }

        // ===== 协议消息派发 (原 OnLiveChatReceived 的 Split('|') 分支链 => 强类型路由) =====
        private void OnLiveChatReceived(ChatEntry entry)
        {
            MatchSession s = _currentSession;
            if (s == null) return;
            if (entry == null || string.IsNullOrEmpty(entry.Body)) return;
            if (!entry.Body.StartsWith(PcsrProtocol.PacketPrefix, StringComparison.Ordinal)) return;
            if (entry.Mine) return;

            PcsrPacket packet;
            if (!PcsrProtocol.TryDecode(entry.Body, out packet)) return;

            // 赛前或赛中换图投票
            if (packet.Kind == PcsrPacketKind.VoteReq)
            {
                s.ProposedSeed = packet.Seed;
                s.ProposedLevelIndex = packet.LevelIndex;
                s.ProposedRoute = packet.Route;
                s.OppRerollVoteActive = true;
                s.RerollVoteDeadline = Time.realtimeSinceStartup + MatchSession.RerollVoteTimeoutSeconds;
                PcsrLog.LogInfo("[PeakCSR] 收到对手的换图投票请求 (目标地图 #" + s.ProposedLevelIndex + ")");
                Gateway.PushSystemChat(CsrLoc.T("chat.reroll.req_recv",
                    s.OpponentName, s.ProposedLevelIndex, Gateway.GetBiomeLabel(s.ProposedLevelIndex)));
                NotifyStateChanged();
                return;
            }
            if (packet.Kind == PcsrPacketKind.VoteNo)
            {
                s.MyRerollVoteActive = false;
                s.OppRerollVoteActive = false;
                s.StatusMessage = CsrLoc.T("status.reroll_declined");
                PcsrLog.LogInfo("[PeakCSR] 对手拒绝了换图投票。");
                Gateway.PushSystemChat(CsrLoc.T("chat.reroll.declined"));
                NotifyStateChanged();
                return;
            }
            if (packet.Kind == PcsrPacketKind.VoteYes)
            {
                s.ProposedSeed = packet.Seed;
                s.ProposedLevelIndex = packet.LevelIndex;
                s.ProposedRoute = packet.Route;
                Reroll.ExecuteAgreedReroll(s, s.ProposedSeed, s.ProposedLevelIndex, s.ProposedRoute);
                return;
            }

            // 换图三步握手第 3 步: 房主下发新房号, 加入方主动进新房
            if (packet.Kind == PcsrPacketKind.RerollRoom)
            {
                if (!string.IsNullOrEmpty(packet.RoomCode))
                {
                    PcsrLog.LogInfo("[PeakCSR] 收到房主换图新房号 [" + packet.RoomCode + "]，准备加入。");
                    Reroll.JoinRerollRoom(s, packet.RoomCode, packet.Seed, packet.LevelIndex, packet.Route);
                }
                return;
            }

            // 对手宣告本局作废 (掉线等无法确认结果的情况)
            // 双方都掉线时会各自上报作废, 但只有一方还能连上 socket 时, 这条通知能让另一方
            // 立刻跟进作废, 不必干等 45 秒观察窗到期。
            if (packet.Kind == PcsrPacketKind.Void)
            {
                if (s.State != CsMatchState.Idle && !s.MatchVoided)
                {
                    PcsrLog.LogWarning("[PeakCSR] 收到对手的作废协议，本局同步作废。");
                    Settlement.ConcludeVoid(s, CsrLoc.T("status.void_opp_dc", s.OpponentName));
                }
                return;
            }

            // 对手主动投降: 直接判我方胜利
            if (packet.Kind == PcsrPacketKind.Forfeit)
            {
                if (s.State != CsMatchState.Finished && s.State != CsMatchState.Idle)
                {
                    PcsrLog.LogInfo("[PeakCSR] 收到对手投降协议，本局判胜。");
                    Gateway.PushSystemChat(CsrLoc.T("chat.forfeit.opp", s.OpponentName));
                    Settlement.ConcludeMatch(s, true, CsrLoc.T("status.win.opp_dnf", s.OpponentName));
                }
                return;
            }

            // SYN/ACK/未知命令的握手同步 (旧协议向后兼容)
            if (packet.HasElo)
            {
                if (packet.OpponentElo > 0)
                {
                    int clamped = packet.OpponentElo;
                    if (clamped < 100) clamped = 100;
                    if (clamped > 5000) clamped = 5000;
                    s.OpponentElo = clamped;
                }
                if (packet.HasRatedFlag && Gateway.IsHost == false)
                {
                    s.IsRatedMatch = packet.RatedFlag;
                }

                if (!string.IsNullOrEmpty(entry.SteamId))
                {
                    s.OpponentSteamId = entry.SteamId;
                }
                if (!string.IsNullOrEmpty(entry.Name))
                {
                    s.OpponentName = entry.Name;
                }

                bool wasVerified = s.OpponentVerified;
                s.OpponentVerified = true;

                if (packet.Kind == PcsrPacketKind.Syn)
                {
                    Handshake.SendHandshakePacket(s, "ACK");
                    s.HandshakeAcked = true;
                }
                else if (packet.Kind == PcsrPacketKind.Ack)
                {
                    s.HandshakeAcked = true;
                }

                if (!wasVerified)
                {
                    PcsrLog.LogInfo("[PeakCSR] 对手握手校验成功: " + s.OpponentName + " (ELO: " + s.OpponentElo + ")");
                    if (s.State == CsMatchState.WaitingOpponent)
                    {
                        s.StatusMessage = CsrLoc.T("status.matched_starting", s.OpponentName, s.OpponentElo);
                    }
                    NotifyStateChanged();
                }
            }
        }

        // ===== 开赛 5 秒倒计时 (纯提示) =====
        private void BeginStartCountdown(MatchSession s)
        {
            if (s.StartCountdownUntil > 0f) return;
            s.StartCountdownUntil = Time.realtimeSinceStartup + MatchSession.StartCountdownSeconds;
            PcsrLog.LogInfo("[PeakCSR] 双方就绪，开始 " + MatchSession.StartCountdownSeconds + " 秒开赛倒计时。");
            Gateway.PushSystemChat(CsrLoc.T("chat.start_countdown", Mathf.RoundToInt(MatchSession.StartCountdownSeconds)));
            NotifyStateChanged();
        }

        private void TickStartCountdown(MatchSession s)
        {
            if (s.StartCountdownUntil < 0f) return;

            // 对手掉了/换了人: 倒计时作废, 等新的握手完成再重来
            if (!s.OpponentVerified || string.IsNullOrEmpty(s.OpponentSteamId))
            {
                s.StartCountdownUntil = -1f;
                s.HostStartRequested = false;
                return;
            }

            float remain = s.StartCountdownUntil - Time.realtimeSinceStartup;
            if (remain > 0f)
            {
                int seconds = Mathf.CeilToInt(remain);
                s.StatusMessage = CsrLoc.T("status.start_countdown", seconds, s.OpponentName);
                return;
            }

            // 倒计时归零: 房主发起开赛
            s.StartCountdownUntil = -1f;
            if (Gateway.IsHost && Gateway.CurrentRace != null && Gateway.CurrentRace.RunnerCount >= 2)
            {
                s.StatusMessage = CsrLoc.T("status.verified_starting", s.OpponentElo);
                PcsrLog.LogInfo("[PeakCSR] 倒计时结束，房主发起同步开赛。");
                Gateway.StartRace(err => Matchmaker.OnLobbyError(s, err));
            }
            else if (Gateway.ServiceAvailable && Gateway.IsHost == false)
            {
                s.StatusMessage = CsrLoc.T("status.waiting_host_start");
                PcsrLog.LogInfo("[PeakCSR] 倒计时结束，等待房主开赛。");
            }
            NotifyStateChanged();
        }

        // ===== LoadingLevel 看门狗 =====
        public void TickLoadingWatchdog(MatchSession s)
        {
            if (s.State == CsMatchState.LoadingLevel)
            {
                if (s.LoadingSince < 0f) s.LoadingSince = Time.realtimeSinceStartup;
                if (Time.realtimeSinceStartup - s.LoadingSince > MatchSession.LoadingWatchdogSeconds)
                {
                    s.LoadingSince = -1f;
                    s.StatusMessage = CsrLoc.T("status.loading_stuck", Mathf.RoundToInt(MatchSession.LoadingWatchdogSeconds));
                    PcsrLog.LogWarning("[PeakCSR] 加载超时 " + MatchSession.LoadingWatchdogSeconds + "s 仍未进入对局，尝试重新加入房间自动重试。");
                    NotifyStateChanged();
                    TryRecoverLoading(s);
                }
            }
        }

        // 加载卡死时重发一次 join, 让 RaceService 重新走 LoadRaceLevel
        private void TryRecoverLoading(MatchSession s)
        {
            if (s.TransitionInFlight) return;
            if (string.IsNullOrEmpty(s.InviteCode)) return;
            if (Gateway.ServiceAvailable == false) return;

            s.TransitionInFlight = true;
            s.TransitionDeadline = Time.realtimeSinceStartup + 15f;
            PcsrLog.LogInfo("[PeakCSR] 尝试重新加入房间 [" + s.InviteCode + "] 以恢复加载。");
            Gateway.JoinRace(s.InviteCode, race =>
            {
                s.TransitionInFlight = false;
                if (race != null) OnRaceJoined(s, race);
            }, err =>
            {
                s.TransitionInFlight = false;
                PcsrLog.LogWarning("[PeakCSR] 加载恢复失败: " + err);
                s.StatusMessage = CsrLoc.T("status.error", err);
                NotifyStateChanged();
            });
        }

        // 排队状态文案实时刷新已匹配时长 (倒计时中不覆盖)
        private void RefreshQueueStatusText(MatchSession s)
        {
            if (s.State == CsMatchState.SearchingLobby)
            {
                s.StatusMessage = CsrLoc.T("status.searching",
                    MatchNaming.GetFormatDisplayName(s.PendingFormat, s.PendingAscent), CsrFormat.FormatElapsed(s.QueueElapsedSeconds));
            }
            else if (s.State == CsMatchState.WaitingOpponent)
            {
                s.StatusMessage = CsrLoc.T("status.waiting_opp", s.InviteCode, CsrFormat.FormatElapsed(s.QueueElapsedSeconds));
            }
        }

        // =====================================================
        // 双轨 Tick 管道 (由 Boot 层的 MonoBehaviour 驱动)
        // =====================================================

        // 全局服务: 无论当前处于什么状态 (Idle/Finished) 都要驱动
        // 游戏本体登顶信号读取 (与 EndScreen banner 使用的 stats.won 同源)
        private static bool IsLocalPlayerWonInGame()
        {
            try
            {
                Character c = Character.localCharacter;
                if (c == null || c.refs == null || c.refs.stats == null) return false;
                return c.refs.stats.won;
            }
            catch
            {
                return false;
            }
        }

        public void TickGlobalServices()
        {
            // 队列情报心跳独立于对局状态: 主菜单干看着、等对手、刚打完 都要继续刷新
            Matchmaker.TickQueueHeartbeat();

            MatchSession s = _currentSession;
            if (s != null)
            {
                // 延迟退房必须在状态判断之前驱动:
                // 作废后 _state 立刻变成 Finished, 如果放在后面就永远不会被执行。
                Guardian.TickRetireLobby(s);
            }
        }

        // 活动会话: 仅当 CurrentSession 存在时驱动
        public void TickActiveSession()
        {
            MatchSession s = _currentSession;
            if (s == null) return;

            // 记录实时 socket 首次可用的时刻, 握手的宽限期从这里起算
            if (s.LiveConnectedSince < 0f && Gateway.LiveConnected) s.LiveConnectedSince = Time.realtimeSinceStartup;

            // 过渡动作期间记下的离开意图: 过渡结束 (或超时作废) 后立刻执行
            if (s.PendingLeaveRequest
                && (!s.TransitionInFlight || Time.realtimeSinceStartup >= s.TransitionDeadline))
            {
                s.PendingLeaveRequest = false;
                s.TransitionInFlight = false;
                PcsrLog.LogInfo("[PeakCSR] 执行过渡期间记下的离开请求。");
                LeaveMatch();
                return;
            }

            _phaseMachine.Tick(s);

            // 本机登顶判胜兜底: 游戏本体 stats.won 与结算画面 banner 同源,
            // 服务端 race 快照缺失/消失时 (实测开赛瞬间可能 race == null),
            // 完赛信号只存在于游戏本体, 不兜底就会等 45s 观察窗误判作废
            if (s.State == CsMatchState.Running
                && !s.MatchVoided && !s.DisconnectConfirmed && !s.VoidSubmitted
                && IsLocalPlayerWonInGame())
            {
                PcsrLog.LogInfo("[PeakCSR] 检测到游戏本体登顶信号 (stats.won), 服务端快照缺失, 兜底判胜。");
                // 用真实完赛用时
                long elapsedMs = Gateway.TimerElapsedMs;
                if (elapsedMs <= 0) elapsedMs = (long)((Time.realtimeSinceStartup - s.MatchStartTime) * 1000f);
                double sec = elapsedMs / 1000.0;
                Settlement.ConcludeMatch(s, true, CsrLoc.T("status.win.peak", CsrFormat.FormatClock(sec)));
                return;
            }

            // 开赛倒计时 (房间就绪 + 对手已校验 + 房主 + 人数齐)
            if (s.State == CsMatchState.WaitingOpponent && s.OpponentVerified && !s.HostStartRequested)
            {
                if (Gateway.IsHost && Gateway.CurrentRace != null && Gateway.CurrentRace.RunnerCount >= 2)
                {
                    // 不再一握手就瞬间开赛: 先播 5 秒倒计时提示
                    s.HostStartRequested = true;
                    BeginStartCountdown(s);
                }
            }
            TickStartCountdown(s);

            // 排队状态文案实时刷新已匹配时长 (倒计时中不覆盖)
            if (s.StartCountdownUntil < 0f
                && (s.State == CsMatchState.SearchingLobby || s.State == CsMatchState.WaitingOpponent))
            {
                RefreshQueueStatusText(s);
            }

            Guardian.TickConnectivityWatch(s);
            Guardian.TickDisconnectObserver(s);
            Reroll.Tick(s);
            Handshake.Tick(s);
        }

        // 兼容旧 UI: MatchController.Instance.StatusMessage 的替代读取
        public static string CurrentStatusText
        {
            get
            {
                MatchCoordinator c = _instance;
                return c != null ? c.StatusMessage : CsrLoc.T("status.not_ready");
            }
        }
    }
}
