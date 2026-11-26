using System;
using PeakCSR.Practice;
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
        // 落盘近期交手台账
        private readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<long>> _recentOpponents
            = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<long>>(StringComparer.Ordinal);
        private bool _encounterDirty;

        public PlayerProfileBridge(PlayerRecord record, LocalProfileStore store)
        {
            _record = record;
            _store = store;
            LoadEncounters();
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
        public int BestStreak { get { return _record.BestStreak; } }
        public int TotalGames { get { return _record.TotalGames; } }
        public int WinRatePercent { get { return _record.WinRatePercent; } }
        public int FavoriteMap { get { return _record.FavoriteMap; } }
        public int DislikedMap { get { return _record.DislikedMap; } }
        public string Country
        {
            get { return _record.Country; }
            set
            {
                _record.Country = value;
                _store.Save(_record);
            }
        }
        public int FavoriteAscent { get { return _record.FavoriteAscentEffective; } }
        public int FavoriteFormat { get { return _record.FavoriteFormatEffective; } }

        public int ModeWins(PcsrRuleFormat format) { return _record.ModeWins(format); }
        public int ModeLosses(PcsrRuleFormat format) { return _record.ModeLosses(format); }
        public int ModeGames(PcsrRuleFormat format) { return _record.ModeGames(format); }
        public int ModeWinRatePercent(PcsrRuleFormat format) { return _record.ModeWinRatePercent(format); }

        // 保存玩家自选的群系索引 (见 PcsrBiomes.All), -1 表示未设置
        public void SetFavoriteMap(int biomeIndex)
        {
            _record.SetFavoriteMap(biomeIndex, PcsrBiomes.Count);
            _store.Save(_record);
        }

        // 保存"最讨厌群系", 返回 false 表示与最爱冲突被拒
        public bool SetDislikedMap(int biomeIndex)
        {
            if (!_record.SetDislikedMap(biomeIndex, PcsrBiomes.Count)) return false;
            _store.Save(_record);
            return true;
        }

        // ===== Anti-Boost 近期交手台账 =====
        int IEncounterLedger.PruneAndCount(string opponentSteamId, long nowMinutesUtc)
        {
            System.Collections.Generic.List<long> history;
            if (!_recentOpponents.TryGetValue(opponentSteamId, out history))
            {
                return 0;
            }
            int removed = history.RemoveAll(t => nowMinutesUtc - t > RatingRules.Instance.AntiBoost.WindowMinutes);
            if (removed > 0) _encounterDirty = true;
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
            _encounterDirty = true;
            // 每次交手都立刻落盘: 玩家打完一局很可能直接退出游戏
            SaveEncounters();
        }

        // 台账路径与本地档案同目录 (config), 独立成文件, 不与签名档案耦合
        private static string EncounterPath
        {
            get
            {
                try
                {
                    return PcsrDataPaths.Resolve("encounters.json");
                }
                catch
                {
                    return "";
                }
            }
        }

        private void LoadEncounters()
        {
            try
            {
                string path = EncounterPath;
                if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;
                string json = System.IO.File.ReadAllText(path);
                var loaded = Newtonsoft.Json.JsonConvert
                    .DeserializeObject<System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<long>>>(json);
                if (loaded == null) return;
                long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
                int window = RatingRules.Instance.AntiBoost.WindowMinutes;
                foreach (var kv in loaded)
                {
                    if (string.IsNullOrEmpty(kv.Key) || kv.Value == null) continue;
                    kv.Value.RemoveAll(t => now - t > window);
                    if (kv.Value.Count > 0) _recentOpponents[kv.Key] = kv.Value;
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取交手台账失败 (按空台账处理): " + ex.Message);
            }
        }

        private void SaveEncounters()
        {
            if (!_encounterDirty) return;
            try
            {
                string path = EncounterPath;
                if (string.IsNullOrEmpty(path)) return;
                string dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                string json = Newtonsoft.Json.JsonConvert.SerializeObject(_recentOpponents);
                System.IO.File.WriteAllText(path, json);
                _encounterDirty = false;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 保存交手台账失败 (不影响本局结算): " + ex.Message);
            }
        }

        // ===== 结算入口 =====
        public SettlementDetail RecordMatchResultDetailed(
            string opponentSteamId,
            int opponentElo,
            double actualScore,
            PcsrRuleFormat format,
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

            int baseDelta;
            int delta;
            if (isRated)
            {
                delta = EloEngine.ComputeDelta(rec.Elo, opponentElo, actualScore, baseK, totalWeight, antiBoostWeight,
                    minWinDelta, progressBonus, out baseDelta);
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
            // 本地结算同样改动了权威记录，推进修订号
            _ratingRevision++;
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
        public SettlementDetail RecordVoid(PcsrRuleFormat format, int ascent, string reason)
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
        // 返回 false 表示云端分数不可信, 本地档案保持原样
        public bool ApplyCloudAuthoritativeElo(int cloudElo)
        {
            if (!_record.ApplyCloudAuthoritativeElo(cloudElo)) return false;
            _store.Save(_record);
            // 每次权威分落地都推进一次修订号
            _ratingRevision++;
            EventBus.PublishRatingChanged();
            // 战报可能已用本地预估渲染过: 广播权威分, 让它在屏幕上直接改写成最终值
            EventBus.PublishAuthoritativeEloApplied(cloudElo);
            return true;
        }

        // 评分修订号: 递增即代表本地权威分发生了变化
        public int RatingRevision { get { return _ratingRevision; } }
        private int _ratingRevision;
// 云端权威档案覆盖 (含分赛制战绩)
// draws: 云端平局数, 传 -1 表示本次没有拿到权威档案
// 返回 false 表示云端分数不可信, 整份档案未采纳
public bool ApplyCloudAuthoritativeState(
    int elo, int peak, int wins, int losses, int draws, int streak, int bestStreak,
    int winsAllPeak, int lossesAllPeak,
    int winsHardcore, int lossesHardcore,
    int winsSprint, int lossesSprint,
    int favoriteMap, int dislikedMap, int favoriteAscent, int favoriteFormat,
    string country)
{
    if (!_record.ApplyCloudAuthoritativeState(elo, peak, wins, losses, draws, streak,
        winsAllPeak, lossesAllPeak,
        winsHardcore, lossesHardcore,
        winsSprint, lossesSprint,
        favoriteMap, dislikedMap, favoriteAscent, favoriteFormat, PcsrBiomes.Count, country))
    {
        return false;
    }
    // 云端最长连胜是异地/重装后唯一能恢复口径的来源, 只取更大值
    if (bestStreak > _record.BestStreak) _record.BestStreak = bestStreak;
    _store.Save(_record);
    _ratingRevision++;
    EventBus.PublishRatingChanged();
    return true;
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
        private PcsrRuleFormat _preferredFormat = PcsrRuleFormat.StandardAllPeak;
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

            // 计分属性同样编入魔数
            gateway.CoordinatorRated = delegate
            {
                MatchSession s = _currentSession;
                return s != null ? s.IsRatedMatch : _preferredIsRated;
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
        //
        // 语义拆分说明:
        //   IsCurrentPeakCsrMatch  = "这是一个有对手、会产生 ELO 的排位对局"
        //                            -> 胜负仲裁 / 登顶后回主菜单 / 战报 用它
        //   IsCurrentCsrRun        = "这是一个 PCSR 赛制下的爬图过程" (含练习)
        //                            -> 分段采集用它, 练习局也必须采到分段
        public static bool IsCurrentPeakCsrMatch()
        {
            MatchCoordinator mc = _instance;
            if (mc == null) return false;
            // 练习局永远不会被当作排位对局
            if (PracticeRunFlag.IsActive) return false;
            MatchSession s = mc._currentSession;
            if (s == null) return false;
            // 不在这里排除 Finished
            if (s.State == CsMatchState.SearchingLobby || s.State == CsMatchState.WaitingOpponent) return false;
            try
            {
                RaceInfo race = mc.Gateway.CurrentRace;
                if (race == null && s.State != CsMatchState.Finished)
                {
                    // LastFinishedRace 只在"仍在对局阶段"作为兜底;
                    // 已结算后回主菜单时不拿它冒充在局
                    race = mc.Gateway.LastFinishedRace;
                }
                if (race != null && RoomMagicCodec.IsCsrMagicMs(race.AutoEndMs)) return true;
            }
            catch
            {
            }
            // 兜底: 状态机已在 PCSR 对局中且持有 PCSR 房间码
            return !string.IsNullOrEmpty(s.InviteCode);
        }

        // 分段采集专用闸门: 练习局也要采集 (需求 1: 练习时一切照常计算)
        public static bool IsCurrentCsrRun()
        {
            if (PracticeRunFlag.IsActive) return true;
            return IsCurrentPeakCsrMatch();
        }

        // ===== 面板偏好 =====
        public PcsrRuleFormat PreferredFormat
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
        public PcsrRuleFormat SelectedFormat
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

        // ===== 练习模式交棒入口 =====
        //
        // 练习期间后台探针命中后, 由 PracticeOrchestrator 回调到这里:
        // 复用排位那条已经打磨过的 join -> 握手 -> 5 秒倒计时 -> 载图链路
        // 调用前 Photon 必须已恢复在线, 且玩家已回到主菜单。
        //
        // onResult: 必须且只会被调用一次。
        //   true  = join 真的成功, 排位链路已接管
        //   false = 当场拒绝或 join 失败, 携带可展示原因, 由练习侧回退
        public bool BeginMatchFromPractice(string inviteCode, System.Action<bool, string> onResult)
        {
            System.Action<bool, string> report = OnceResult(onResult);

            string cleanCode = PcsrProtocol.SanitizeRoomCode(inviteCode);
            if (string.IsNullOrEmpty(cleanCode))
            {
                PublishIdleStatus(CsrLoc.T("status.code_empty"));
                report(false, CsrLoc.T("status.code_empty"));
                return false;
            }

            if (Gateway.ServiceAvailable == false)
            {
                PublishIdleStatus(CsrLoc.T("status.not_ready"));
                report(false, CsrLoc.T("status.not_ready"));
                return false;
            }

            // 残留会话先彻底收干净 (练习期间理论上不会有, 但换图/异常退出的补做路径要稳)
            if (_currentSession != null)
            {
                TearDownSession("practice_handoff");
            }

            PcsrLog.LogInfo("[PeakCSR] 练习模式交棒: 接续加入房间 [" + cleanCode + "]。");
            // 交棒接续的是真排位对局, 一律按计分处理
            StartNewSession(_preferredFormat, _preferredAscent, true, strictRuleset: false);
            MatchSession s = _currentSession;
            s.PendingFormat = _preferredFormat;
            s.PendingAscent = _preferredAscent;
            s.StatusMessage = CsrLoc.T("status.joining_code", cleanCode);
            s.RoomOpInFlight = true;
            s.RoomOpStartedAt = Time.realtimeSinceStartup;
            NotifyStateChanged();

            Gateway.JoinRace(cleanCode,
                race =>
                {
                    s.RoomOpInFlight = false;
                    s.RoomOpStartedAt = -1f;
                    // 交棒成功: 解冻 RANKED 的退房判定, 让正常的对局收尾逻辑接管
                    PracticeHandoffGuard.EndSwitch();
                    OnRaceJoined(s, race);
                    // 幽灵房/赛制不一致会在 OnRaceJoined 里当场拆掉会话,
                    // 这里必须复查: 会话没了就等于没接管成功
                    if (ReferenceEquals(_currentSession, s) && s.State != CsMatchState.Idle)
                    {
                        report(true, "");
                    }
                    else
                    {
                        PcsrLog.LogWarning("[PeakCSR] 练习交棒后被幽灵房/赛制校验拒绝。");
                        report(false, string.IsNullOrEmpty(_idleStatusMessage)
                            ? CsrLoc.T("status.ghost_room_skipped")
                            : _idleStatusMessage);
                    }
                },
                err =>
                {
                    s.RoomOpInFlight = false;
                    s.RoomOpStartedAt = -1f;
                    PracticeHandoffGuard.EndSwitch();
                    PcsrLog.LogWarning("[PeakCSR] 练习交棒加入失败: " + err);
                    TearDownSession("practice_join_failed");
                    PublishIdleStatus(CsrLoc.T("practice.status.handoff_join_failed", err ?? ""));
                    report(false, err ?? "");
                });
            return true;
        }

        // 结果回调只允许生效一次: 成功/失败回调都可能被重入
        private static System.Action<bool, string> OnceResult(System.Action<bool, string> onResult)
        {
            bool fired = false;
            return delegate (bool ok, string err)
            {
                if (fired) return;
                fired = true;
                if (onResult == null) return;
                try { onResult(ok, err); }
                catch (Exception ex) { PcsrLog.LogWarning("[PeakCSR] 交棒结果回调异常: " + ex.Message); }
            };
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

            // 取消等待中的公开房时先置私密
            if (s != null && s.State == CsMatchState.WaitingOpponent && Gateway.IsHost)
            {
                try
                {
                    Gateway.SetPublic(false, err =>
                    {
                        PcsrLog.LogInfo("[PeakCSR] 取消匹配: 公开房已置为私密 " + err);
                    });
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 取消匹配时置私密失败: " + ex.Message);
                }
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

        private void StartNewSession(PcsrRuleFormat format, int ascent, bool rated, bool strictRuleset)
        {
            // 旧会话还挂着"过渡中被延后的离开请求"时, 先把旧房真的退掉
            FlushPendingLeaveBeforeNewSession();

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

        // 开新会话前补做被延后的离开: 只在过渡已经结束时才动网关,
        // 仍在过渡中则保持原样 (让 Tick 里的延迟执行逻辑接手)
        private void FlushPendingLeaveBeforeNewSession()
        {
            MatchSession s = _currentSession;
            if (s == null) return;
            if (!s.PendingLeaveRequest) return;
            if (s.TransitionInFlight && Time.realtimeSinceStartup < s.TransitionDeadline) return;

            s.PendingLeaveRequest = false;
            s.TransitionInFlight = false;
            try
            {
                if (Gateway.InRace || Gateway.RaceIsOver) Gateway.LeaveRace();
            }
            catch
            {
            }
            PcsrLog.LogInfo("[PeakCSR] 开新匹配前补做被延后的离开请求。");
        }

        // 离开房间时直接置 null: 单局状态随对象一起蒸发
        // reason 时的最后一条文案保留给 UI: 置 null 前先写入 _idleStatusMessage,
        // 否则"赛制不一致/环境不一致"等错误提示会跟着会话对象一起蒸发。
        public void TearDownSession(string reason)
        {
            MatchSession s = _currentSession;
            if (s != null)
            {
                // 兜底: 会话一旦销毁, 观察窗与所有结算路径都会跟着失效
                // 因此只要这一局真的开打过却还没结论, 先当场给出结论再拆
                try
                {
                    if (s.EnteredMatchSceneOnce
                        && s.State != CsMatchState.Finished
                        && !s.VoidSubmitted
                        && !s.SwitchingRerollRoom)
                    {
                        PcsrLog.LogWarning("[PeakCSR] 销毁会话前检测到未结算对局 (" + reason + ")，先补做结算。");
                        // 只有确定阵亡才判负, 其余一律作废 (不计分)
                        if (s.SelfDiedConcluded)
                        {
                            Settlement.ConcludeLocalLoss(s, CsrLoc.T("status.loss.dnf"));
                        }
                        else if (IsLocalPlayerDeadInGame())
                        {
                            Settlement.ConcludeSelfDeath(s);
                        }
                        else
                        {
                            Settlement.ConcludeVoid(s, CsrLoc.T("status.void_left_map"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 拆会话前补结算失败: " + ex.Message);
                }

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
                PcsrRuleFormat hostFormat;
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

        // 对局中被切出地图场景 (掉线 / 被踢 / 读图失败) 的统一收尾入口
        public void OnLeftMatchScene(string sceneName)
        {
            MatchSession s = _currentSession;
            if (s == null) return;
            // 已经结算过 (含正常完赛) 就不再插手
            if (s.State == CsMatchState.Finished) return;
            if (s.SwitchingRerollRoom) return;
            if (s.VoidSubmitted) return;

            // 观察窗正在走: 不要抢判
            if (s.ObservingDisconnect)
            {
                PcsrLog.LogWarning("[PeakCSR] 离开地图场景 (" + sceneName
                    + ") 时观察窗仍在进行，等待观察窗到期再统一结算。");
                return;
            }
            // 只处理真正进入过对局阶段的会话; 建房/等人阶段的场景切换不算
            if (s.State != CsMatchState.LoadingLevel
                && s.State != CsMatchState.Countdown
                && s.State != CsMatchState.Running) return;
            // 必须真的进过地图: 跳过机场直连时 race.Status 先变成 loading,
            // 本地场景此刻还是 Title, 切场景回调会立刻打到 OnLeftMatchScene。
            // 若在此处判死, 会在载图请求发出前就把整局作废掉。
            if (!s.EnteredMatchSceneOnce) return;

            // 本机已阵亡后离场是正常流程 (死亡即退图), 按判负收场而不是作废
            if (s.SelfDiedConcluded)
            {
                if (Settlement.ConcludeLocalLoss(s, CsrLoc.T("status.loss.dnf")))
                {
                    PcsrLog.LogInfo("[PeakCSR] 本机阵亡后离开地图场景 (" + sceneName + ")，按判负结算。");
                }
                return;
            }
            if (IsLocalPlayerDeadInGame())
            {
                if (Settlement.ConcludeSelfDeath(s))
                {
                    PcsrLog.LogInfo("[PeakCSR] 本机阵亡后离开地图场景 (" + sceneName + ")，按判负结算。");
                }
                return;
            }

            PcsrLog.LogWarning("[PeakCSR] 对局中离开地图场景 (" + sceneName + ")，本局无法续接，立即作废。");
            Settlement.ConcludeVoid(s, CsrLoc.T("status.void_left_map"));
        }

        // 是否处于异步房间过渡中 (join/create/leave 在飞)
        public bool IsTransitionBusy(MatchSession s)
        {
            if (s == null) return false;
            if (s.TransitionInFlight && Time.realtimeSinceStartup < s.TransitionDeadline) return true;
            if (s.RoomOpInFlight && s.RoomOpStartedAt > 0f
                && Time.realtimeSinceStartup - s.RoomOpStartedAt < MatchSession.RoomOpTimeoutSeconds) return true;
            return false;
        }

        // 房间已结束: 自动把玩家送回主菜单, 不让任何人卡在结算场景里
        private void TickAutoReturnToMainMenu(MatchSession s)
        {
            if (s == null) return;
            if (s.State != CsMatchState.Finished) return;
            if (s.SwitchingRerollRoom) return;
            if (MatchTeardownService.AutoReturnRequested) return;

            // 还没离开房间时不抢跑: LeaveRace 走完 (InRace/RaceIsOver 都为 false) 才切场景
            if (Gateway.InRace || Gateway.RaceIsOver) return;

            PcsrLog.LogInfo("[PeakCSR] 房间已收尾且玩家仍停留在结算场景，自动返回主菜单。");
            MatchTeardownService.ReturnToMainMenu(false);
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
                // 主动清理迟到房间产生的空回调
                if (s.SuppressRaceClosedTeardown)
                {
                    PcsrLog.LogInfo("[PeakCSR] 清理迟到房间产生的空回调，忽略。");
                    return;
                }
                // 过渡动作在飞时, 旧的 null 回调属于"上一次房间关闭", 不能当成当前房间已关
                if (IsTransitionBusy(s))
                {
                    PcsrLog.LogInfo("[PeakCSR] 过渡期收到旧房关闭回调，忽略。");
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

            // 房间回来了: 与 guardian tick 用同一套恢复证据 (实时连接 + 未完赛房间)
            if (Guardian.TryRecoverFromObservation(s, race))
            {
                PcsrLog.LogInfo("[PeakCSR] 观察窗内房间已恢复，继续对局。");
            }
            else if (s.ObservingDisconnect)
            {
                // 恢复被拒: 观察窗仍有效, 这份快照不参与结算
                return;
            }

            TrackOpponentKickOutcome(s, race);
            s.SyncFromRaceInfo(race);
        }

        // 服务端快照是移除是否生效的唯一可靠证据:
        //   - 待确认的对手已从成员表消失 -> 移除确实落地, 冷却正式保留
        //   - 超过移除超时仍挂在房里    -> 移除失败/回调丢失, 撤销冷却以便重试
        private static void TrackOpponentKickOutcome(MatchSession s, RaceInfo race)
        {
            if (s == null) return;
            if (string.IsNullOrEmpty(s.PendingKickSteamId)) return;

            bool stillPresent = false;
            if (race != null && race.Participants != null)
            {
                for (int i = 0; i < race.Participants.Count; i++)
                {
                    RaceParticipant p = race.Participants[i];
                    if (p == null || p.Removed || p.IsSpectator) continue;
                    if (!string.Equals(p.SteamId, s.PendingKickSteamId, StringComparison.Ordinal)) continue;
                    stillPresent = true;
                    break;
                }
            }

            if (!stillPresent)
            {
                s.ConfirmOpponentKick();
                return;
            }

            if (s.PendingKickAt > 0f
                && Time.realtimeSinceStartup - s.PendingKickAt > MatchSession.RoomOpTimeoutSeconds)
            {
                PcsrLog.LogWarning("[PeakCSR] 移出对手后 " + MatchSession.RoomOpTimeoutSeconds
                    + "s 仍未在服务端快照中消失，撤销本次冷却以便重新尝试。");
                s.RollbackOpponentKick();
            }
        }

        // ===== 协议消息派发 (原 OnLiveChatReceived 的 Split('|') 分支链 => 强类型路由) =====
        private void OnLiveChatReceived(ChatEntry entry)
        {
            MatchSession s = _currentSession;
            if (s == null) return;
            if (entry == null || string.IsNullOrEmpty(entry.Body)) return;
            if (!entry.Body.StartsWith(PcsrProtocol.PacketPrefix, StringComparison.Ordinal)) return;
            if (entry.Mine) return;

            // 包能到就证明对端装了 PCSR 且实时通道是通的
            // 这条存活证据与"本地能否解析"解耦
            s.OpponentLastPacketAt = Time.realtimeSinceStartup;
            s.EverReceivedPcsrPacket = true;

            PcsrPacket packet;
            if (!PcsrProtocol.TryDecode(entry.Body, out packet))
            {
                // 只影响语义解析, 不影响存活判定; 打出来便于发现版本/字段漂移
                PcsrLog.LogWarning("[PeakCSR] 收到无法解析的 PCSR 包 (仍按存活证据处理): " + entry.Body);
                return;
            }

            // 存活探测: 只刷新上方已记录的时间戳, 不回包
            if (packet.Kind == PcsrPacketKind.Ping)
            {
                return;
            }

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

            // 对手宣告登顶: 记录用时, 胜负交给 ArbitratePeak 统一裁定
            // 只有本地比赛真的跑起来 (Running) 才允许它参与裁定
            // 赛前状态收到 PEAK 只做记录, 由 TickActiveSession 在进入 Running 后统一裁决
            if (packet.Kind == PcsrPacketKind.Peak)
            {
                if (s.State != CsMatchState.Finished && !s.OutcomeDecided && packet.HasPeakMs)
                {
                    PcsrLog.LogInfo("[PeakCSR] 收到对手登顶宣告: " + s.OpponentName + " 用时 " + packet.PeakMs + "ms (当前状态 " + s.State + ")。");
                    s.NoteOpponentPeak(packet.PeakMs);
                    if (s.State == CsMatchState.Running)
                    {
                        Settlement.ArbitratePeak(s);
                    }
                    else
                    {
                        PcsrLog.LogInfo("[PeakCSR] 本地比赛尚未进入 Running，PEAK 仅记录不裁定，稍后统一仲裁。");
                    }
                }
                return;
            }

            // 对手宣告本局作废 (掉线等无法确认结果的情况)
            // 立刻跟进作废, 不必干等 45 秒观察窗到期
            if (packet.Kind == PcsrPacketKind.Void)
            {
                if (s.State != CsMatchState.Idle && !s.MatchVoided)
                {
                    PcsrLog.LogWarning("[PeakCSR] 收到对手的作废协议，本局同步作废。");
                    Settlement.ConcludeVoid(s, CsrLoc.T("status.void_opp_dc", s.OpponentName));
                }
                return;
            }

            // 对手阵亡宣告: 本局禁止复活时, 阵亡等同判负, 我方立即判胜
            if (packet.Kind == PcsrPacketKind.Died)
            {
                s.NoteOpponentDied(packet.Deaths);
                if (s.State != CsMatchState.Finished && s.State != CsMatchState.Idle && !s.MatchVoided)
                {
                    PcsrLog.LogInfo("[PeakCSR] 对手阵亡，本局判胜。");
                    Settlement.ConcludeMatch(s, true, CsrLoc.T("status.win.opp_died", s.OpponentName));
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

            // 握手同步: 只按包种类判定
            bool isHandshakePacket = packet.Kind == PcsrPacketKind.Syn
                || packet.Kind == PcsrPacketKind.Ack
                || packet.Kind == PcsrPacketKind.Unknown;
            if (isHandshakePacket)
            {
                if (packet.HasElo && packet.OpponentElo > 0)
                {
                    int clamped = packet.OpponentElo;
                    if (clamped < 100) clamped = 100;
                    if (clamped > 5000) clamped = 5000;
                    s.OpponentElo = clamped;
                }
                // 双向校验, 不区分房主或加入方
                if (packet.HasRatedFlag && s.IsRatedMatch != packet.RatedFlag)
                {
                    PcsrLog.LogWarning("[PeakCSR] 计分设置不一致 (本机=" + s.IsRatedMatch
                        + ", 对手=" + packet.RatedFlag + "), 本局按不计分处理。");
                    s.IsRatedMatch = false;
                    Gateway.PushSystemChat(CsrLoc.T("chat.rated.mismatch"));
                    NotifyStateChanged();
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

                // 对端支持存活探测时才启用严格判定
                if (packet.HasPingCap) s.OpponentPingCapable = true;

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

        // ===== 存活探测 (PING) =====
        // 服务端在对手离开后会短暂保留成员快照, 只靠 RunnerCount 无法区分
        // "对手真在房里" 与 "只剩残留成员", 幽灵开赛正是这么来的。
        // 实时包的到达时间是唯一能戳穿残留的证据。
        public void TickPing(MatchSession s, bool force = false)
        {
            if (s == null) return;
            if (string.IsNullOrEmpty(s.OpponentSteamId)) return;
            if (s.State == CsMatchState.Finished) return;
            if (s.State != CsMatchState.WaitingOpponent
                && s.State != CsMatchState.Countdown
                && s.State != CsMatchState.LoadingLevel
                && s.State != CsMatchState.Running) return;
            if (s.MatchVoided) return;

            float now = Time.realtimeSinceStartup;
            if (!force && now < s.NextPingSendAt) return;
            s.NextPingSendAt = now + MatchSession.PingIntervalSeconds;
            try
            {
                Gateway.SendChat(PcsrProtocol.EncodePing());
            }
            catch
            {
            }
        }

        // ===== 开赛 5 秒倒计时 (纯提示) =====
        private void BeginStartCountdown(MatchSession s)
        {
            if (s.StartCountdownUntil > 0f) return;
            // 瞬态网络错误后的退避窗口内不重新发起
            if (s.NextLobbyRetryAt > 0f && Time.realtimeSinceStartup < s.NextLobbyRetryAt) return;
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
                s.LastCountdownShown = -1f;
                return;
            }

            float remain = s.StartCountdownUntil - Time.realtimeSinceStartup;
            if (remain > 0f)
            {
                int seconds = Mathf.CeilToInt(remain);
                s.StatusMessage = CsrLoc.T("status.start_countdown", seconds, s.OpponentName);
                // 秒数变化才重绘面板, 否则每帧 NotifyStateChanged 会拖垮 UI
                if (Mathf.Abs(s.LastCountdownShown - seconds) > 0.01f)
                {
                    s.LastCountdownShown = seconds;
                    NotifyStateChanged();
                }
                return;
            }

            // 倒计时归零: 房主发起开赛
            s.StartCountdownUntil = -1f;
            // 记录这次开赛请求的时刻, 由 TickStartWatchdog 判断它到底有没有生效
            s.StartAttemptAt = Time.realtimeSinceStartup;
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

        // ===== 开赛看门狗 =====
        //
        // 倒计时归零后如果迟迟没进图, 说明这次开赛请求没生效
        // 返回 true 表示已经放弃本房间并离开 (调用方不要再继续处理这个会话)
        public bool TickStartWatchdog(MatchSession s)
        {
            if (s == null) return false;
            if (s.State != CsMatchState.WaitingOpponent) return false;
            if (!s.HostStartRequested) return false;
            if (s.StartCountdownUntil > 0f) return false;
            if (s.StartAttemptAt < 0f) return false;
            if (Time.realtimeSinceStartup - s.StartAttemptAt < MatchSession.StartRetryGraceSeconds) return false;

            // 对手已经不在了: 由 ResetOpponentInfo / 掉线流程接管, 这里不抢判
            if (!s.OpponentVerified || string.IsNullOrEmpty(s.OpponentSteamId)) return false;

            s.StartAttempts++;
            s.StartAttemptAt = -1f;

            if (s.StartAttempts >= MatchSession.MaxStartAttempts)
            {
                PcsrLog.LogWarning("[PeakCSR] 开赛连续 " + s.StartAttempts
                    + " 次未生效，放弃本房间并返回匹配队列。");
                s.StatusMessage = CsrLoc.T("status.start_failed");
                NotifyStateChanged();
                LeaveMatch();
                return true;
            }

            PcsrLog.LogWarning("[PeakCSR] 开赛请求未生效 (" + s.StartAttempts + "/"
                + MatchSession.MaxStartAttempts + ")，重新握手并重试开赛。");
            s.StatusMessage = CsrLoc.T("status.start_retrying", s.StartAttempts, MatchSession.MaxStartAttempts);
            // 复位闸门让 TickActiveSession 重新起倒计时; 房主会再发一次 StartRace, 加入方重新等 running 快照
            s.HostStartRequested = false;
            s.StartCountdownUntil = -1f;
            s.LastCountdownShown = -1f;
            // 立刻补发握手, 顺便确认对手心跳还在
            s.NextHandshakeSendTime = 0f;
            // 重试同样要清掉可能残留的退避截止时刻, 否则 BeginStartCountdown 会被挡住
            if (s.NextLobbyRetryAt > 0f && s.NextLobbyRetryAt > Time.realtimeSinceStartup)
            {
                s.NextLobbyRetryAt = 0f;
            }
            s.Notify();
            return false;
        }

        // ===== LoadingLevel 看门狗 =====
        public void TickLoadingWatchdog(MatchSession s)
        {
            if (s.State != CsMatchState.LoadingLevel) return;

            if (s.LoadingSince < 0f) s.LoadingSince = Time.realtimeSinceStartup;

            // 已经进入地图场景就不算加载超时。
            if (s.EnteredMatchSceneOnce)
            {
                s.LoadingSince = -1f;
                return;
            }

            if (Time.realtimeSinceStartup - s.LoadingSince > MatchSession.LoadingWatchdogSeconds)
            {
                s.LoadingSince = -1f;
                s.StatusMessage = CsrLoc.T("status.loading_stuck", Mathf.RoundToInt(MatchSession.LoadingWatchdogSeconds));
                PcsrLog.LogWarning("[PeakCSR] 加载超时 " + MatchSession.LoadingWatchdogSeconds
                    + "s 仍未进入对局场景，本局无法续接。");
                NotifyStateChanged();
                Settlement.ConcludeVoid(s, CsrLoc.T("status.void_left_map"));
            }
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

        // 本机角色是否已阵亡
        private static bool IsLocalPlayerDeadInGame()
        {
            try
            {
                Character c = Character.localCharacter;
                if (c == null || c.data == null) return false;
                return c.data.dead;
            }
            catch
            {
                return false;
            }
        }

        // 供对局收尾链路判断"这次离图是不是阵亡导致的"
        public bool IsLocalDead()
        {
            return IsLocalPlayerDeadInGame();
        }

        public void TickGlobalServices()
        {
            MatchSession s = _currentSession;

            // 聊天框晚就绪时补推积压的系统消息
            Gateway.TickPendingSystemChat();

            // 队列情报心跳: 主菜单干看着、等对手、刚打完都要继续刷新, 但对局进行中停掉。
            bool inMatch = s != null
                && (s.State == CsMatchState.Running
                    || s.State == CsMatchState.Countdown
                    || s.State == CsMatchState.LoadingLevel);
            if (!inMatch)
            {
                Matchmaker.TickQueueHeartbeat();
            }

            // 在途请求看门狗: 回调丢失时必须能自己解锁, 否则匹配会永久静默
            Matchmaker.TickInFlightWatchdog();
            if (s != null)
            {
                // 延迟退房必须在状态判断之前驱动:
                // 作废后 _state 立刻变成 Finished, 如果放在后面就永远不会被执行。
                Guardian.TickRetireLobby(s);
                // VOID 未送达时持续补发, 且房主侧主动把掉线对手移出房间
                Guardian.TickVoidResend(s);
                Guardian.TickHostOpponentRemoval(s);
                // 收尾回主菜单也走全局管道
                TickAutoReturnToMainMenu(s);
            }
        }

        // 离开当前场景/回主菜单前的统一结算闸门
        public bool TrySettlePendingBeforeExit()
        {
            MatchSession s = _currentSession;
            if (s == null) return false;
            if (s.State == CsMatchState.Finished) return false;
            if (s.VoidSubmitted) return false;
            if (!s.EnteredMatchSceneOnce) return false;

            // 本机已阵亡: 这是确定性负局, 必须走判负而不是作废
            if (s.SelfDiedConcluded)
            {
                return Settlement.ConcludeLocalLoss(s, CsrLoc.T("status.loss.dnf"));
            }
            if (IsLocalPlayerDeadInGame())
            {
                return Settlement.ConcludeSelfDeath(s);
            }

            // 其余情况无法确认结果, 走作废 (双方不计分)
            PcsrLog.LogWarning("[PeakCSR] 离开前本局仍未结算，按作废收场。");
            Settlement.ConcludeVoid(s, CsrLoc.T("status.void_left_map"));
            return true;
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

            // 本机登顶信号: 只作为"我方已完赛"的事实, 广播 PEAK 给对手
            if (s.State == CsMatchState.Running
                && !s.MatchVoided && !s.DisconnectConfirmed && !s.VoidSubmitted
                && !s.SelfPeaked
                && IsLocalPlayerWonInGame())
            {
                long elapsedMs = Gateway.TimerElapsedMs;
                if (elapsedMs <= 0 && s.MatchStartTime > 0f)
                {
                    elapsedMs = (long)((Time.realtimeSinceStartup - s.MatchStartTime) * 1000f);
                }
                PcsrLog.LogInfo("[PeakCSR] 检测到游戏本体登顶信号 (stats.won), 用时 " + elapsedMs + "ms, 广播 PEAK 等待仲裁。");

                // 终段落笔: stats.won 是直升机倒计时结束后的真实完赛时刻
                try
                {
                    s.EnsureRouteSpanRegistered();
                    s.Splits.ObserveCompletion(Gateway.MySteamId, s.Splits.FinalSegmentKey,
                        s.Splits.FinalBoundaryIndex, elapsedMs);
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 我方终段记录失败: " + ex.Message);
                }

                s.AnnounceSelfPeak(elapsedMs);
            }

            // PEAK 重发: socket 可能在我方登顶那一刻还没就绪, 未确认送达前持续补发
            if (s.SelfPeaked && !s.SelfPeakBroadcasted && !s.OutcomeDecided)
            {
                s.SelfPeakBroadcastAt = -1f;
                s.AnnounceSelfPeak(s.SelfPeakMs);
            }

            // 本机阵亡信号: 本局禁止复活时阵亡即判负
            if (s.State == CsMatchState.Running
                && !s.MatchVoided && !s.VoidSubmitted
                && IsLocalPlayerDeadInGame())
            {
                if (!s.SelfDiedBroadcasted)
                {
                    int deaths = 1;
                    try
                    {
                        RaceParticipant me = Gateway.Self;
                        if (me != null && me.Deaths > 0) deaths = me.Deaths;
                    }
                    catch
                    {
                    }
                    s.SelfDeaths = deaths;
                    PcsrLog.LogInfo("[PeakCSR] 检测到本机阵亡 (deaths=" + deaths + ")，广播 DIED 宣告。");
                    s.SendDied();
                }
                // 阵亡已是确定性事实, 不必等服务端 dnf 快照或观察窗
                Settlement.ConcludeSelfDeath(s);
            }

            // 登顶先后仲裁 (含双方都到 / 只有我方到 / 只有对手到三种情形)
            if (Settlement.ArbitratePeak(s)) return;

            // 开赛倒计时: 双方各自本地起算, 不区分房主/加入方,
            // 否则加入方既看不到聊天框提示也看不到面板秒数
            if (s.State == CsMatchState.WaitingOpponent && s.OpponentVerified && !s.HostStartRequested)
            {
                RaceInfo race = Gateway.CurrentRace;
                if (race != null && race.RunnerCount >= 2)
                {
                    s.HostStartRequested = true;
                    BeginStartCountdown(s);
                }
            }
            TickStartCountdown(s);
            if (TickStartWatchdog(s)) return;

            // 排队状态文案实时刷新已匹配时长 (倒计时中不覆盖)
            if (s.StartCountdownUntil < 0f
                && (s.State == CsMatchState.SearchingLobby || s.State == CsMatchState.WaitingOpponent))
            {
                RefreshQueueStatusText(s);
            }

            Guardian.TickConnectivityWatch(s);
            // 幽灵开赛: 已进倒计时/载图却始终没有对手握手, 说明对端从未连进来
            Guardian.TickGhostStartGuard(s);
            Guardian.TickOpponentHeartbeat(s);
            Guardian.TickDisconnectObserver(s);
            Reroll.Tick(s);
            Handshake.Tick(s);
            TickPing(s);
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
