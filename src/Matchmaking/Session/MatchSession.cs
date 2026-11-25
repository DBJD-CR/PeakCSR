using System;
using System.Collections.Generic;
using RankedPeak.Live;
using RankedPeak.Models;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // 单局生命周期状态容器: 开局 new, 结束丢弃, 用对象生命周期天然隔离每一局,
    // 彻底取代旧 MatchController 的 48 行手写 ResetMatchRuntime 清零。
    // 与"面板偏好/全局后台任务"相关的字段留在 MatchCoordinator, 这里只放单局绑定状态。
    public sealed class MatchSession
    {
        // ===== 阶段/节奏常量 (原 MatchController 私有常量原值迁移) =====
        public const float LobbyPollIntervalSeconds = 2.5f;
        public const float StartCountdownSeconds = 5f;
        public const float ConvergePollIntervalSeconds = 3f;
        public const float ConvergeMinRoomAgeSeconds = 4f;
        // 握手宽限: 对手进房后给它 30 秒完成 PCSR 握手 (socket 重连窗口较慢)
        public const float HandshakeGraceSeconds = 30f;
        // LoadingLevel 卡死超时: 超时后主动提示并允许重试
        public const float LoadingWatchdogSeconds = 45f;
        // 断线观察窗: race 消失/连接中断后先观察这么久, 期间不判负
        public const float DisconnectObserveSeconds = 45f;
        // 本机连接异常持续这么久才确认掉线, 避开 socket 1~2 秒的重连抖动
        public const float ConnectivityDownGraceSeconds = 8f;
        // 本机连接状态轮询间隔
        public const float ConnectivityCheckIntervalSeconds = 1f;
        // 存活探测 (PING) 发送间隔: 开赛前用来确认对手真的还挂在实时通道上
        public const float PingIntervalSeconds = 2f;
        // 对手连续这么久没发任何包, 判定为已离开 (PING 间隔 2s 的 3 倍容差)
        public const float OpponentPingStaleSeconds = 6f;
        // 大厅请求/房间操作的在途超时: 回调丢失时必须能自己爬起来
        public const float LobbyFetchTimeoutSeconds = 12f;
        public const float RoomOpTimeoutSeconds = 15f;
        // 进入新阶段后的静默期: 这段时间内不采信连接信号
        // 跑图阶段 socket 刚建连, 加载阶段场景切换本来就会短暂抖动, 都不能当掉线
        public const float ConnectivitySettleRunningSeconds = 4f;
        public const float ConnectivitySettleLoadingSeconds = 25f;
        // 对手实时心跳超过这个时长没更新, 才进入"疑似掉线"计时 (RANKED 每秒一帧)
        public const float OpponentHeartbeatStaleSeconds = 15f;
        // 判定对手掉线需要的连续陈旧采样次数
        public const int OpponentHeartbeatStaleProbes = 3;
        // 陈旧采样计数的节流间隔
        public const float OpponentHeartbeatStaleProbeIntervalSeconds = 2f;
        // 作废后留给 VOID 协议送达的时间, 之后才真正退出房间
        public const float VoidBroadcastGraceSeconds = 3f;
        // 作废局房间收尾失败后的重试间隔
        public const float VoidRetryIntervalSeconds = 3f;
        // VOID 协议重发窗口: 作废那一刻 socket 往往正在抖动, 单次发送很容易丢
        public const float VoidResendSeconds = 15f;
        public const float VoidResendIntervalSeconds = 0.5f;
        // 赛段用时下限: 再快也不可能 1 秒内从山脚爬到山顶,
        // 小于这个值的一律视为"开局首帧/跳段瞬间"的伪数据
        public const long MinPlausibleSplitMs = 1000L;
        // 赛中 30s 投票换图超时
        public const float RerollVoteTimeoutSeconds = 30f;
        // 开赛请求发出后等"真的进图"的宽限: 超过这段时间仍停在 WaitingOpponent 就认为这次开赛没生效
        public const float StartRetryGraceSeconds = 12f;
        // 开赛重试上限: 连续这么多次仍未进图就放弃本房间, 不让双方在房里互相干等
        public const int MaxStartAttempts = 3;
        // 瞬态网络错误 (SSL/超时/连不上) 的重试上限, 之后才当作真失败拆会话
        public const int MaxTransientLobbyRetries = 5;

        // ===== 房间参数 =====
        public PcsrRuleFormat Format = PcsrRuleFormat.StandardAllPeak;
        public int Ascent;
        public int Route;
        public int Seed;
        public int LevelIndex;
        public string InviteCode = "";
        public bool IsRatedMatch = true;

        // 排队二次校验: 排队期间以 _pending* 为准 (房间值可能已被上一局覆写)
        public bool StrictRulesetJoin;
        public PcsrRuleFormat PendingFormat = PcsrRuleFormat.StandardAllPeak;
        public int PendingAscent;

        // 本机最近一次真正参与过的房间号, 用于区分"我自己的局"与"别人遗留的幽灵房"
        public string LastOwnedRaceCode = "";

        // ===== 对手 =====
        public string OpponentSteamId = "";
        public string OpponentName = "";
        public int OpponentElo = 1000;
        public bool OpponentVerified;
        public bool HandshakeAcked;
        public float OpponentJoinedTime;
        public float NextHandshakeSendTime;
        public float OpponentProgress;
        public float OpponentStamina = 1f;
        public float OpponentHealth = 1f;
        public bool OpponentDown;

        // 本局是否曾经收到过对端任何 PCSR 包
        public bool EverReceivedPcsrPacket;

        // 本机是否已成功发起过一次真正意义上的移除 (用于移除失败时回滚)
        public string PendingKickSteamId = "";
        public float PendingKickAt = -1f;

        // ===== 握手重发诊断 =====
        // 发出的握手包总数与真正交给实时连接的数量: socket 抖动时两者会背离,
        // 这是区分"我没发出去"和"对端确实没收到"的唯一线索
        public int HandshakeSendCount;
        public int HandshakeSendOkCount;

        // ===== 被移出对手冷却 =====
        // 服务端移除玩家后短期内仍返回残留快照, 同一个人会被重新登记成"新对手",
        // 宽限期随之重新起算, 于是出现 踢 -> 重进 -> 再踢 的死循环。
        // 第一次移出冷却 60s; 同一对手第二次之后本次会话内不再移出。
        public const float KickedOpponentCooldownSeconds = 60f;
        public string RecentlyKickedSteamId = "";
        public float RecentlyKickedUntil = -1f;
        public int RecentlyKickedTimes;

        // ===== 进度 =====
        public float SelfProgress;
        public float MyBestProgress;
        public float OppBestProgress;

        // ===== 计时 =====
        public CsMatchState State = CsMatchState.SearchingLobby;
        public float QueueStartTime = -1f;
        public float MatchStartTime = -1f;
        public float RoomEnteredAt;

        // ===== 轮询/收敛 =====
        public bool LobbyFetchInFlight;
        // join/create 未回调前不允许再发起房间操作
        public bool RoomOpInFlight;
        public float NextLobbyPollTime;
        public bool ConvergeFetchInFlight;
        public float NextConvergePollTime;

        // ===== 开赛倒计时 =====
        public bool HostStartRequested;
        public float StartCountdownUntil = -1f;
        // 本次开赛请求的发出时刻 (倒计时归零那一刻), 开赛看门狗从这里起算
        public float StartAttemptAt = -1f;
        // 连续开赛未生效的次数
        public int StartAttempts;
        // 瞬态网络错误累计次数与退避截止时刻
        public int LobbyErrorRetries;
        public float NextLobbyRetryAt;

        // ===== 登顶宣告与胜负仲裁 =====
        // 我方真实完赛用时 (统计为准, >0 表示已登顶)
        public long SelfPeakMs;
        // 我方是否已广播出自己的 PEAK 包
        public bool SelfPeakBroadcasted;
        // 收到的对手登顶用时 (>0 表示对手已宣告登顶)
        public long OppPeakMs;
        // 对手 PEAK 包到达的本地时刻, 用于先后仲裁
        public float OppPeakReceivedAt = -1f;
        // 我方 PEAK 广播的本地时刻: 等对端回执的窗口从这里起算
        public float SelfPeakBroadcastAt = -1f;
        // 胜负仲裁已出结果 (防止同一局被裁决两次)
        public bool OutcomeDecided;

        // ===== 阵亡宣告 =====
        // 我方累计死亡次数 (随 DIED 包发给对端)
        public int SelfDeaths;
        // 我方是否已成功广播 DIED 包
        public bool SelfDiedBroadcasted;
        // 本机已按"阵亡即判负"完成本地结算 (防止多路重复裁决)
        public bool SelfDiedConcluded;
        // 是否收到对手阵亡宣告 (唯一能在对手退出房间后仍能判胜的证据)
        public bool OppDiedReceived;
        // 对手宣告的死亡次数
        public int OppDeaths;

        // ===== 存活探测 (PING) =====
        // 下一次发包时刻
        public float NextPingSendAt;
        // 最近一次收到对手任何协议包的时刻, -1 表示从未收到
        public float OpponentLastPacketAt = -1f;
        // 对手握手包声明支持 PING 时才启用严格存活判定
        public bool OpponentPingCapable;

        // ===== 在途操作看门狗 =====
        // 大厅请求发出时刻, 超时后强制解锁 LobbyFetchInFlight
        public float LobbyFetchStartedAt = -1f;
        // 房间操作 (join/create/leave) 发出时刻, 超时后强制解锁 RoomOpInFlight
        public float RoomOpStartedAt = -1f;
        // 收敛迁移: leave 已确认完成, 才允许发起 join
        public bool ConvergeLeaveAcked;

        // ===== 场景进入 (区分"从未进图"与"进过图又离开") =====
        // 服务端把 race.Status 改成 loading 时, 本地场景往往还停在 Title。
        // 那个瞬间绝不能把"不在 Level_* 场景"当成"已离开地图", 否则开局第一帧就误作废。
        public bool EnteredMatchSceneOnce;
        public float EnteredMatchSceneAt = -1f;

        // ===== 作废 / 断线 =====
        public bool MatchVoided;
        public string VoidReason = "";
        public bool VoidSubmitted;
        // VOID 协议是否已确认交给实时连接 (未送达时由 Guardian 持续补发)
        public bool VoidBroadcasted;
        public float NextVoidResendAt;
        public float VoidResendUntil;
        // 房主侧对手移除是否已尝试过 (只做一次, 避免每帧调用服务端)
        public bool OpponentRemovalAttempted;
        public bool DisconnectConfirmed;
        public bool ObservingDisconnect;
        // 本局已撤销观察窗的次数: 对端真的不在时会出现"进窗-恢复"反复, 超过上限直接作废
        public int DisconnectRecoverCount;
        // 同一局允许的最大撤销次数, 超过即认定对端确实没了
        public const int MaxDisconnectRecovers = 3;
        // 一次性广播标记: 同一条提示每局只推一次, 避免轮询重复刷屏
        public bool StartAnnounced;
        public float LastCountdownShown = -1f;
        public bool SelfPeakAnnounced;
        public bool OppPeakAnnounced;
        public bool SelfDnfAnnounced;
        public bool OppDnfAnnounced;
        public bool OppDcAnnounced;
        public bool RoomClosingAnnounced;
        public float DisconnectObserveUntil;
        public string DisconnectReason = "";
        public bool ObservationNeedsLiveEvidence;
        public bool OpponentDepartedObserved;
        // 本帧快照里对手刚消失 (仅用于就绪/倒计时阶段埋点)
        public bool RaceJustLostOpponent;
        // 对手 Live 心跳开始停滞的时刻, -1 表示心跳新鲜。
        // 服务端快照在对手掉线后会一直停在 running, 这条实时信号是发现对手已经没了的唯一通道
        public float OpponentHeartbeatStaleSince = -1f;
        // 这段停滞窗口内已经连续确认陈旧的采样次数
        public int OpponentHeartbeatStaleProbeCount;
        // 下一次允许推进陈旧采样计数的时刻
        public float NextHeartbeatStaleCheck;
        // 最近一次在实时 socket 上看到对手槽位的时刻 (-1 表示本轮从未见到)
        public float OpponentSlotSeenAt = -1f;
        // 本局是否成功连上过实时通道: 从没连上就不拿它的断线当信号
        public bool LiveEverConnected;
        public float ConnectivityDownSince = -1f;
        public float NextConnectivityCheck;
        // 当前观察阶段与其进入时间, 用于阶段切换后的静默期
        public CsMatchState WatchedPhase = CsMatchState.Idle;
        public float PhaseEnteredAt;

        // ===== LoadingLevel 看门狗 =====
        public float LoadingSince = -1f;

        // 玩家在过渡动作 (加载恢复/换房) 期间点了离开: 过渡结束后再执行, 不能直接丢掉
        public bool PendingLeaveRequest;
        // 实时 socket 可用时刻: 握手宽限期从这一刻起算
        public float LiveConnectedSince = -1f;

        // ===== 串行化过渡 =====
        public bool TransitionInFlight;
        public float TransitionDeadline;
        // 换房迁移中: 抑制旧房关闭时的空 race 回调
        public bool SwitchingRerollRoom;

        // ===== 幽灵开赛守卫 =====
        // 守卫自己的计时起点
        public float GhostGuardSince = -1f;

        // ===== 房间操作代际 =====
        public int RoomOpGeneration;
        // 最近一次房间操作的类型 (join / create / converge), 仅用于如实报错文案
        public string LastLobbyOpKind = "";
        // 主动清理迟到房间时置位
        public bool SuppressRaceClosedTeardown;

        // ===== 赛中 30s 投票换图 =====
        public bool MyRerollVoteActive;
        public bool OppRerollVoteActive;
        public float RerollVoteDeadline;
        public int ProposedSeed;
        public int ProposedLevelIndex;
        public int ProposedRoute;
        // 换图三步握手: 房主建好新房后把新房号下发给加入方
        public string PendingRerollJoinCode = "";
        public int PendingRerollSeed;
        public int PendingRerollLevel;
        public int PendingRerollRoute;
        public float PendingRerollJoinDeadline;
        // 换图新房加入失败的重试次数 (超过上限就回搜索队列)
        public int RerollJoinAttempts;

        // ===== 作废延迟退房 =====
        public float RetireLobbyAt = -1f;

        // ===== 本局分段与数据采集 =====
        public readonly SplitTracker Splits = new SplitTracker();
        // 用 Seed + LevelIndex 共同标识地图身份
        private int _routeSpanSeed = -1;
        private int _routeSpanLevel = -999;

        public string StatusMessage = "";

        private readonly IRankedPeakGateway _gw;
        private readonly Action _notify;
        private MatchSettlementService _settlement;

        public MatchSession(IRankedPeakGateway gw, PcsrRuleFormat format, int ascent, bool rated, float queueStartTime, Action notify)
        {
            _gw = gw;
            Format = format;
            Ascent = RoomMagicCodec.ClampAscent(ascent);
            IsRatedMatch = rated;
            QueueStartTime = queueStartTime;
            _notify = notify;
        }

        public void AttachSettlement(MatchSettlementService settlement)
        {
            _settlement = settlement;
        }

        public void Notify()
        {
            if (_notify != null) _notify();
        }

        // 单局只结算一次的幂等闸门 (护栏 4): 三条作废路径共用
        public bool CanConclude()
        {
            return State != CsMatchState.Finished && !VoidSubmitted;
        }

        // 排队已耗时 (秒), 未在排队时返回 0
        public float QueueElapsedSeconds
        {
            get
            {
                if (QueueStartTime < 0f) return 0f;
                if (State != CsMatchState.SearchingLobby && State != CsMatchState.WaitingOpponent) return 0f;
                return Math.Max(0f, Now() - QueueStartTime);
            }
        }

        public int RerollVoteRemainingSeconds
        {
            get
            {
                if (!MyRerollVoteActive && !OppRerollVoteActive) return 0;
                return Math.Max(0, Mathf.CeilToInt(RerollVoteDeadline - Now()));
            }
        }

        private static float Now()
        {
            return Time.realtimeSinceStartup;
        }

        public void ResetOpponentInfo()
        {
            OpponentSteamId = "";
            OpponentName = "";
            OpponentElo = 1000;
            OpponentVerified = false;
            HandshakeAcked = false;
            OpponentProgress = 0f;
            OpponentBestCleanup();
            OpponentStamina = 1f;
            OpponentHealth = 1f;
            OpponentDown = false;
            OpponentJoinedTime = 0f;
            NextHandshakeSendTime = 0f;
            // 对手不在了, 之前算好的开赛倒计时作废
            StartCountdownUntil = -1f;
            HostStartRequested = false;
            // 换人后开赛重试计数也要清零, 否则新对手会被上一任的失败次数拖累
            StartAttemptAt = -1f;
            StartAttempts = 0;
            OpponentHeartbeatStaleSince = -1f;
            OpponentHeartbeatStaleProbeCount = 0;
            NextHeartbeatStaleCheck = 0f;
            OpponentSlotSeenAt = -1f;
            // 换人后上一任对手的存活证据与握手诊断全部清零
            OpponentLastPacketAt = -1f;
            OpponentPingCapable = false;
            NextPingSendAt = 0f;
            HandshakeSendCount = 0;
            HandshakeSendOkCount = 0;
        }

        // 这名对手是否刚刚被我们移出过 (冷却期内不再重复移出)
        public bool IsRecentlyKicked(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return false;
            if (string.IsNullOrEmpty(RecentlyKickedSteamId)) return false;
            if (!string.Equals(steamId, RecentlyKickedSteamId, StringComparison.Ordinal)) return false;
            return Now() < RecentlyKickedUntil;
        }

        // ===== 移除结果追踪 =====
        // 发起移除: 先临时挂起冷却, 并记录待确认的移除
        public void BeginOpponentKick(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            PendingKickSteamId = steamId;
            PendingKickAt = Now();
            if (string.Equals(steamId, RecentlyKickedSteamId, StringComparison.Ordinal))
            {
                RecentlyKickedTimes++;
            }
            else
            {
                RecentlyKickedSteamId = steamId;
                RecentlyKickedTimes = 1;
            }
            // 同一对手第二次之后本次会话内不再移出: 它多半是装了 PCSR 但链路持续丢包的真人
            RecentlyKickedUntil = RecentlyKickedTimes >= 2
                ? float.PositiveInfinity
                : Now() + KickedOpponentCooldownSeconds;
        }

        // 移除被确认生效 (快照里该对手已消失): 冷却保留, 清掉待确认标记
        public void ConfirmOpponentKick()
        {
            PendingKickSteamId = "";
            PendingKickAt = -1f;
        }

        // 移除迟迟没有生效: 撤销本次冷却, 让握手侧下一轮可以重新尝试
        public void RollbackOpponentKick()
        {
            if (string.IsNullOrEmpty(PendingKickSteamId)) return;
            PendingKickSteamId = "";
            PendingKickAt = -1f;
            if (RecentlyKickedTimes > 0) RecentlyKickedTimes--;
            if (RecentlyKickedTimes <= 0)
            {
                RecentlyKickedSteamId = "";
                RecentlyKickedUntil = -1f;
                RecentlyKickedTimes = 0;
            }
        }

        // 换人/离开时清掉对手峰值
        private void OpponentBestCleanup()
        {
            OppBestProgress = 0f;
        }

        // 换图/重开一局时的运行态重置 (单局对象复用但地图换了, 进度基准必须重新起算)
        public void ResetForNewRace()
        {
            MyBestProgress = 0f;
            OppBestProgress = 0f;
            SelfProgress = 0f;
            OpponentProgress = 0f;
            MyRerollVoteActive = false;
            OppRerollVoteActive = false;
            StartAnnounced = false;
            LastCountdownShown = -1f;
            // 换图重开: 上一局的登顶宣告与仲裁状态必须清零, 否则新局会被旧结果锁死
            SelfPeakMs = 0L;
            OppPeakMs = 0L;
            SelfPeakBroadcasted = false;
            SelfPeakBroadcastAt = -1f;
            OppPeakReceivedAt = -1f;
            OutcomeDecided = false;
            SelfPeakAnnounced = false;
            OppPeakAnnounced = false;
            SelfDnfAnnounced = false;
            OppDnfAnnounced = false;
            OppDcAnnounced = false;
            // 阵亡宣告按局重置, 否则第二局会被上一局的 DIED 直接判负/判胜
            SelfDeaths = 0;
            SelfDiedBroadcasted = false;
            SelfDiedConcluded = false;
            OppDiedReceived = false;
            OppDeaths = 0;
            OpponentLastPacketAt = -1f;
            RoomClosingAnnounced = false;
            OpponentHeartbeatStaleSince = -1f;
            OpponentHeartbeatStaleProbeCount = 0;
            NextHeartbeatStaleCheck = 0f;
            OpponentSlotSeenAt = -1f;
            MatchStartTime = -1f;
            StartCountdownUntil = -1f;
            HostStartRequested = false;
            StartAttemptAt = -1f;
            StartAttempts = 0;
            SweepProgressState();
        }

        // 进度基准只在这里 (换局) 与开新会话时重置, 不做静态直调, 避免跨会话串味
        private static void SweepProgressState()
        {
            ProgressMonotonicState.Reset();
        }

        // 当前这次 join 是不是恢复到"我自己刚才那局" (换图/重试/观察窗恢复都会走到这里)
        public bool IsJoiningOwnActiveRace(RaceInfo race)
        {
            if (race == null || string.IsNullOrEmpty(race.InviteCode)) return false;
            if (!string.Equals(race.InviteCode, LastOwnedRaceCode, StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(race.InviteCode, InviteCode, StringComparison.OrdinalIgnoreCase)
                || SwitchingRerollRoom
                || TransitionInFlight;
        }


        // =====================================================================
        // 服务端房间快照同步 (原 MatchController.SyncFromRaceInfo 逐行迁移)
        // =====================================================================
        public void SyncFromRaceInfo(RaceInfo race)
        {
            if (race == null) return;

            // 换房/重进期的旧房快照整体丢弃
            if (SwitchingRerollRoom
                && !string.IsNullOrEmpty(InviteCode)
                && !string.IsNullOrEmpty(race.InviteCode)
                && !string.Equals(race.InviteCode, InviteCode, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (race.InviteCode != null) InviteCode = race.InviteCode;
            Seed = race.Seed;
            LevelIndex = race.LevelIndex;
            Ascent = race.Ascent;
            Route = race.Route;

            string mySteamId = _gw.MySteamId;
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
                SelfProgress = self.Progress;
                // 未完赛却报 100% 只可能是旧的"终局强制 1.0"污染, 不能记进战报
                if (!selfDone && self.Progress >= 0.999f && MyBestProgress < 0.999f)
                {
                    // 忽略这一帧伪值
                }
                else if (!selfDone && self.Progress < 0.95f && MyBestProgress >= 0.999f)
                {
                    // 自愈: 之前被污染成 1.0, 用真实值覆盖回去
                    MyBestProgress = self.Progress;
                }
                else if (self.Progress > MyBestProgress)
                {
                    MyBestProgress = self.Progress;
                }
            }

            if (opp != null)
            {
                if (OpponentSteamId != opp.SteamId)
                {
                    // 换人: 上一位对手的峰值与倒计时都不能留给新对手
                    OppBestProgress = 0f;
                    StartCountdownUntil = -1f;
                    HostStartRequested = false;
                    OpponentSteamId = opp.SteamId;
                    OpponentJoinedTime = Now();
                    OpponentVerified = false;
                    HandshakeAcked = false;
                    NextHandshakeSendTime = 0f;
                    // 握手诊断按对手隔离
                    HandshakeSendCount = 0;
                    HandshakeSendOkCount = 0;
                }
                OpponentName = opp.PlayerName ?? "";
                // 对手回来了 (重连/换房): 清掉"已消失"标记, 避免下次它再掉线时不作废
                OpponentDepartedObserved = false;
                RaceJustLostOpponent = false;
                bool oppDone = opp.State == "finished";
                OpponentProgress = opp.Progress;
                OpponentStamina = opp.Stamina ?? 1f;
                OpponentHealth = opp.Health ?? 1f;
                OpponentDown = opp.Down;
                if (!oppDone && opp.Progress >= 0.999f && OppBestProgress < 0.999f)
                {
                    // 同上: 对手掉线/未登顶时被伪造成 100%, 不采纳
                }
                else if (!oppDone && opp.Progress < 0.95f && OppBestProgress >= 0.999f)
                {
                    OppBestProgress = opp.Progress;
                }
                else if (opp.Progress > OppBestProgress)
                {
                    OppBestProgress = opp.Progress;
                }
            }
            else if (State == CsMatchState.WaitingOpponent)
            {
                ResetOpponentInfo();
            }
            else if (!string.IsNullOrEmpty(OpponentSteamId)
                && (State == CsMatchState.Countdown || State == CsMatchState.LoadingLevel))
            {
                // 就绪/倒计时阶段对手退出
                if (!RaceJustLostOpponent)
                {
                    RaceJustLostOpponent = true;
                    PcsrLog.LogWarning("[PeakCSR] 对手在 " + State
                        + " 阶段离开了房间（比赛尚未开始），本局取消。");
                }
            }

            // 真的离开"等人"阶段就说明开赛链路已经通了, 清零重试计数
            if (race.Status == "loading" || race.Status == "countdown" || race.Status == "running")
            {
                LobbyErrorRetries = 0;
                NextLobbyRetryAt = 0f;
                StartAttempts = 0;
                StartAttemptAt = -1f;
            }

            // 实时采集双方分段打卡时间 (营火分段)
            CaptureLiveSplits(self, opp, race);

            if (race.Status == "loading" && State != CsMatchState.Finished)
            {
                SwitchingRerollRoom = false;
                // 每次进入加载阶段都重新计时
                if (State != CsMatchState.LoadingLevel) LoadingSince = -1f;
                State = CsMatchState.LoadingLevel;
                StatusMessage = CsrLoc.T("status.loading_island");
            }
            else if (race.Status == "countdown" && State != CsMatchState.Finished)
            {
                SwitchingRerollRoom = false;
                State = CsMatchState.Countdown;
                long ms = _gw.MsUntilStart.HasValue ? _gw.MsUntilStart.Value : 0;
                if (ms > 0)
                {
                    StatusMessage = CsrLoc.T("status.countdown", (ms / 1000f).ToString("F1"));
                }
                else
                {
                    // 倒计时已归零: 直接进入 Running
                    State = CsMatchState.Running;
                    if (MatchStartTime < 0f) MatchStartTime = Now();
                    StatusMessage = CsrLoc.T("status.running", OpponentName, OpponentElo);
                    if (!StartAnnounced)
                    {
                        StartAnnounced = true;
                        _gw.PushSystemChat(CsrLoc.T("chat.match.start"));
                    }
                }
            }
            else if (race.Status == "running" && State != CsMatchState.Finished)
            {
                SwitchingRerollRoom = false;
                State = CsMatchState.Running;
                if (MatchStartTime < 0f) MatchStartTime = Now();

                // 真正开赛只广播一次
                if (!StartAnnounced)
                {
                    StartAnnounced = true;
                    _gw.PushSystemChat(CsrLoc.T("chat.match.start"));
                }

                if (!MyRerollVoteActive && !OppRerollVoteActive)
                {
                    // 对手已完赛: 说明服务端 rank 快照可能没到位, 提示约 30s 后房间自动收尾
                    if (opp != null && opp.State == "finished")
                    {
                        StatusMessage = CsrLoc.T("status.opp_finished_wait");
                        if (!OppPeakAnnounced)
                        {
                            OppPeakAnnounced = true;
                            OppDcAnnounced = true;
                            double oppSec = (opp.TotalMs ?? 0) / 1000.0;
                            _gw.PushSystemChat(CsrLoc.T("chat.match.opp_peak", OpponentName, CsrFormat.FormatClock(oppSec)));
                        }
                        // 对手先登顶后, 房间由服务端 AutoEndMs 收尾, 把倒计时也广播出去
                        AnnounceRoomClosing();
                    }
                    else
                    {
                        StatusMessage = CsrLoc.T("status.running", OpponentName, OpponentElo);
                    }
                }
            }

            if (!SwitchingRerollRoom
                && (State == CsMatchState.Running || State == CsMatchState.Countdown || State == CsMatchState.LoadingLevel || race.Status == "finished")
                && State != CsMatchState.Finished
                && _settlement != null)
            {
                _settlement.EvaluateWinCondition(this, race, self, opp);
            }

            Notify();
        }

        // ===== 对局状态广播 (每局每条只推一次) =====

        // 我方登顶: 记录用时并广播 PEAK 包给对手
        public void AnnounceSelfPeak(long elapsedMs)
        {
            // 非正用时不是真实登顶时间, 直接拒绝记录与广播 (与对手侧同一规则)
            if (elapsedMs <= 0)
            {
                PcsrLog.LogWarning("[PeakCSR] 忽略非正的我方完赛用时 (" + elapsedMs + "ms)，不广播 PEAK。");
                return;
            }
            if (SelfPeakMs <= 0) SelfPeakMs = elapsedMs;
            if (!SelfPeakAnnounced)
            {
                SelfPeakAnnounced = true;
                string clock = CsrFormat.FormatClock(SelfPeakMs / 1000.0);
                _gw.PushSystemChat(CsrLoc.T("chat.match.self_peak", clock));
            }
            // 广播登顶宣告 (重发是安全的, 对手侧只认第一条)
            // 只有网关确认包已交给实时连接才算"已广播": socket 未就绪时必须保持 false,
            // 否则 Tick 不再重发, 仲裁可能在对端没收到 PEAK 的情况下判我方胜
            if (!SelfPeakBroadcasted)
            {
                if (_gw.SendChat(PcsrProtocol.EncodePeak(SelfPeakMs)))
                {
                    SelfPeakBroadcasted = true;
                    SelfPeakBroadcastAt = Now();
                }
            }
            // 等对手期间房间会在 AutoEndMs 后关闭, 顺手把倒计时也广播掉
            AnnounceRoomClosing();
        }

        // 记录收到的对手登顶宣告 (重复包只认第一条)
        public void NoteOpponentPeak(long elapsedMs)
        {
            if (OppPeakMs > 0) return;
            if (elapsedMs <= 0)
            {
                PcsrLog.LogWarning("[PeakCSR] 忽略对手的非正完赛用时 (" + elapsedMs + "ms)，继续等待有效 PEAK。");
                return;
            }
            OppPeakMs = elapsedMs;
            OppPeakReceivedAt = Now();
            if (!OppPeakAnnounced)
            {
                OppPeakAnnounced = true;
                double sec = OppPeakMs / 1000.0;
                _gw.PushSystemChat(CsrLoc.T("chat.match.opp_peak", OpponentName, CsrFormat.FormatClock(sec)));
            }

            // 对手侧分段同理: 进度点索引投影只能证明"爬到了", 完赛时刻要由 PEAK 包写
            try
            {
                if (!string.IsNullOrEmpty(OpponentSteamId))
                {
                    EnsureRouteSpanRegistered();
                    Splits.ObserveCompletion(OpponentSteamId, Splits.FinalSegmentKey,
                        Splits.FinalBoundaryIndex, elapsedMs);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 对手终段记录失败: " + ex.Message);
            }
        }

        // 我方是否已进入"登顶待仲裁"状态
        public bool SelfPeaked { get { return SelfPeakMs > 0; } }

        // 我方阵亡
        public void AnnounceSelfDnf(bool isTimeout)
        {
            if (SelfDnfAnnounced) return;
            SelfDnfAnnounced = true;
            _gw.PushSystemChat(CsrLoc.T(isTimeout ? "chat.match.self_timeout" : "chat.match.self_dnf"));
            // 阵亡主动宣告
            if (!isTimeout)
            {
                SendDied();
            }
        }

        // 阵亡宣告重发 (对手可能因连接抖动漏收, 与 PEAK 同样策略)
        public void SendDied()
        {
            if (SelfDiedBroadcasted) return;
            try
            {
                if (_gw.SendChat(PcsrProtocol.EncodeDied(SelfDeaths)))
                {
                    SelfDiedBroadcasted = true;
                }
            }
            catch
            {
            }
        }

        // 记录收到的对手阵亡宣告 (重复包只认第一条)
        public void NoteOpponentDied(int deaths)
        {
            if (OppDiedReceived) return;
            OppDiedReceived = true;
            OppDeaths = deaths;
            PcsrLog.LogWarning("[PeakCSR] 收到对手阵亡宣告 (累计死亡 " + deaths + " 次)。");
            // 自己的连接抖动不该让这条确定性证据失效, 由结算侧决定如何用
        }

        // opp 传 null 表示"调用方已确认对手是真实阵亡", 只做播报不做状态复核
        public void AnnounceOpponentDnf(RaceParticipant opp = null)
        {
            if (OppDnfAnnounced) return;
            if (opp != null && opp.State != "dnf") return;
            // 心跳停止的 dnf 是掉线, 交给掉线分支处理, 不能当成"对手阵亡"
            if (OppDcAnnounced) return;
            OppDnfAnnounced = true;
            _gw.PushSystemChat(CsrLoc.T("chat.match.opp_dnf", OpponentName));
        }

        // 对手掉线/消失
        public void AnnounceOpponentDisconnect()
        {
            if (OppDcAnnounced) return;
            OppDcAnnounced = true;
            _gw.PushSystemChat(CsrLoc.T("chat.match.opp_dc", OpponentName));
        }

        // 房间收尾倒计时 (服务端 AutoEndMs 机制, 不是 PCSR 自己关房)
        public void AnnounceRoomClosing()
        {
            if (RoomClosingAnnounced) return;
            RoomClosingAnnounced = true;
            int seconds = _gw.AutoEndSecondsApprox;
            _gw.PushSystemChat(CsrLoc.T("chat.match.room_closing", seconds));
        }

        // 采集双方分段耗时, 用于赛后战报
        // 本地走篝火事件 (CampfireSplitPatch), 对手走进度点索引投影
        private void CaptureLiveSplits(RaceParticipant self, RaceParticipant opp, RaceInfo race)
        {
            try
            {
                // 路线区间登记: 段收集与导出都要按本局路线裁剪
                EnsureRouteSpanRegistered();

                // 对手: Flags >> 4 = maxProgressPointReached (钳到 0~15), 是 0-based 索引。
                // 用进度值解 "索引 0 + 进度 0" 的歧义; 跳帧的段用帧时间回填并标 Estimated。
                if (opp != null && !string.IsNullOrEmpty(opp.SteamId))
                {
                    int pointIndex = opp.Flags >> 4;
                    long elapsed = opp.LiveTimeMs.HasValue ? opp.LiveTimeMs.Value : 0L;
                    // 对手已完赛: 权威用时就是 TotalMs。
                    // 迷你跑跳段后 Flags>>4 开局就是 2, 用 LiveTimeMs 会把整段记成"开局 109ms"
                    if (opp.State == "finished" && opp.TotalMs.HasValue && opp.TotalMs.Value > 0)
                    {
                        elapsed = opp.TotalMs.Value;
                    }
                    // 小于 1 秒的用时只可能是"刚开赛/刚跳段"的伪值, 不写进分段
                    if (elapsed < MinPlausibleSplitMs)
                    {
                        elapsed = 0;
                    }
                    if (elapsed > 0)
                    {
                        IRankedPeakGateway gw = _gw;
                        ProgressPointInfo[] pts = gw.GetProgressPoints();
                        int spanStart = Splits.RouteStartIndex;
                        int spanEnd = Splits.RouteEndIndex;
                        Func<int, string> keyAt = delegate(int idx)
                        {
                            if (pts == null || idx < 0 || idx >= pts.Length) return "";
                            return BiomeSegmenter.NormalizeTitle(pts[idx].Title);
                        };
                        Func<int, bool> inRoute = delegate(int idx)
                        {
                            if (spanStart >= 0 && idx < spanStart) return false;
                            if (spanEnd >= 0 && idx > spanEnd) return false;
                            return true;
                        };
                        Splits.ObservePeerFrame(opp.SteamId, pointIndex, opp.Progress, elapsed, keyAt, inRoute);
                    }
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 采集赛段数据失败: " + ex.Message);
            }
        }

        // 把本局路线的进度点区间登记进 SplitTracker (每张地图只需一次)
        public void EnsureRouteSpanRegistered()
        {
            bool sameMap = Splits.RouteStartIndex >= 0
                && _routeSpanSeed == Seed && _routeSpanLevel == LevelIndex;
            if (sameMap) return;
            IRankedPeakGateway gw = _gw;
            if (gw == null) return;
            int start;
            int end;
            if (!gw.TryResolveRouteSpanFor(Route, out start, out end)) return;
            Splits.SetRoute(start, end);
            _routeSpanSeed = Seed;
            _routeSpanLevel = LevelIndex;
            PcsrLog.LogInfo("[PeakCSR] 本局赛段区间: Route=" + Route + ", 进度点 " + start + ".." + end);

            // 终段边界必须在区间确定后立刻登记
            RegisterFinalBoundary();
            LogRouteSpanDiagnostics(gw, start, end);
        }

        // 从段模型反推终段边界与前一段边界, 交给 SplitTracker
        private void RegisterFinalBoundary()
        {
            try
            {
                List<BiomeSegment> segs = BuildRouteSegments();
                if (segs == null || segs.Count == 0) return;
                BiomeSegment last = segs[segs.Count - 1];
                int preBoundary = segs.Count >= 2 ? segs[segs.Count - 2].LastPointIdx : -1;
                Splits.SetFinalBoundary(last.LastPointIdx, preBoundary, last.Key);
                PcsrLog.LogInfo("[PeakCSR] 终段边界: idx=" + last.LastPointIdx
                    + " key=" + last.Key + " 前段边界=" + preBoundary);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 终段边界登记失败: " + ex.Message);
            }
        }

        // 分段域自检: 把"进度点数 / 标题序列 / 段序号 / 段数"一次性打全
        // 用途: 确认段序号是否等于进度点索引
        // 只读诊断, 不参与任何判定
        private void LogRouteSpanDiagnostics(IRankedPeakGateway gw, int spanStart, int spanEnd)
        {
            try
            {
                ProgressPointInfo[] pts = gw.GetProgressPoints();
                if (pts == null || pts.Length == 0)
                {
                    PcsrLog.LogWarning("[PeakCSR] 分段自检: 进度点为空 (地图可能尚未加载)");
                    return;
                }

                System.Text.StringBuilder titles = new System.Text.StringBuilder();
                for (int i = 0; i < pts.Length; i++)
                {
                    if (i > 0) titles.Append(',');
                    titles.Append(i).Append('=').Append(BiomeSegmenter.NormalizeTitle(pts[i].Title));
                }

                string segNum = "?";
                try { segNum = ((int)MapHandler.CurrentSegmentNumber).ToString(); }
                catch { }

                int segCount = -1;
                try { segCount = BuildRouteSegments().Count; }
                catch { }

                PcsrLog.LogInfo("[PeakCSR] 分段自检: 点数=" + pts.Length
                    + " 段号=" + segNum
                    + " 段数=" + segCount
                    + " Route=" + Route
                    + " span=[" + spanStart + ".." + spanEnd + "]"
                    + " 标题=[" + titles + "]");
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 分段自检失败: " + ex.Message);
            }
        }

        // 新地图的进度点表已加载: 之前缓存的区间属于上一张图, 作废
        public void InvalidateRouteSpan()
        {
            _routeSpanSeed = -1;
            _routeSpanLevel = -999;
            Splits.SetRoute(-1, -1);
        }

        // 构建本局路线的赛段模型 (段名与端点全部取自真实进度点/篝火)
        public List<BiomeSegment> BuildRouteSegments()
        {
            EnsureRouteSpanRegistered();
            IRankedPeakGateway gw = _gw;
            if (gw == null) return new List<BiomeSegment>();

            ProgressPointInfo[] pts = gw.GetProgressPoints();
            if (pts == null || pts.Length < 2) return new List<BiomeSegment>();

            BiomeSegmenter.PointInput[] input = new BiomeSegmenter.PointInput[pts.Length];
            for (int i = 0; i < pts.Length; i++)
            {
                input[i].Title = pts[i].Title;
                input[i].HasPosition = pts[i].HasPosition;
                input[i].Z = pts[i].Z;
                input[i].IsNadir = pts[i].IsNadir;
            }

            // 直接取本局锚点链, 段终点 = 路线终点锚点。
            ProgressAnchor[] anchors;
            if (gw.TryGetRouteAnchors(Splits.RouteEndIndex >= 0 ? Route : 0, out anchors)
                && anchors != null && anchors.Length >= 2)
            {
                ProgressAnchor end = anchors[anchors.Length - 1];
                return BiomeSegmenter.Build(
                    input,
                    Splits.RouteStartIndex,
                    Splits.RouteEndIndex,
                    delegate(int boundaryIdx)
                    {
                        // 只有最后一个边界才映射到真实终点, 其余段保持"下一进度点"的回退语义
                        if (boundaryIdx > Splits.RouteEndIndex) return (float?)end.Z;
                        return null;
                    });
            }

            return BiomeSegmenter.Build(input, Splits.RouteStartIndex, Splits.RouteEndIndex, null);
        }

        // 段名: 一律取 progressPoints[i].title
        public string BuildSplitLabel(int index)
        {
            List<BiomeSegment> segs = BuildRouteSegments();
            if (index >= 0 && index < segs.Count) return segs[index].Key;
            return "SEG " + (index + 1);
        }

        // 导出双方赛段记录 (已按本局路线裁剪)
        public void RebuildSplits(List<SplitEntry> mySplits, List<SplitEntry> oppSplits)
        {
            mySplits.Clear();
            oppSplits.Clear();
            EnsureRouteSpanRegistered();

            string mySteamId = _gw.MySteamId;
            List<SplitEntry> mine = Splits.Export(mySteamId);
            List<SplitEntry> theirs = Splits.Export(OpponentSteamId);

            for (int i = 0; i < mine.Count; i++) mySplits.Add(mine[i]);
            for (int i = 0; i < theirs.Count; i++) oppSplits.Add(theirs[i]);
        }
    }
}
