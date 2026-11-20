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
        // 进入新阶段后的静默期: 这段时间内不采信连接信号
        // 跑图阶段 socket 刚建连, 加载阶段场景切换本来就会短暂抖动, 都不能当掉线
        public const float ConnectivitySettleRunningSeconds = 4f;
        public const float ConnectivitySettleLoadingSeconds = 25f;
        // 对手实时心跳超过这个时长没更新, 就认定对手已掉线 (RANKED 每秒一帧)
        public const float OpponentHeartbeatStaleSeconds = 6f;
        // 作废后留给 VOID 协议送达的时间, 之后才真正退出房间
        public const float VoidBroadcastGraceSeconds = 3f;
        // 作废局房间收尾失败后的重试间隔
        public const float VoidRetryIntervalSeconds = 3f;
        // 赛中 30s 投票换图超时
        public const float RerollVoteTimeoutSeconds = 30f;

        // ===== 房间参数 =====
        public McsrRuleFormat Format = McsrRuleFormat.StandardAllPeak;
        public int Ascent;
        public int Route;
        public int Seed;
        public int LevelIndex;
        public string InviteCode = "";
        public bool IsRatedMatch = true;

        // 排队二次校验: 排队期间以 _pending* 为准 (房间值可能已被上一局覆写)
        public bool StrictRulesetJoin;
        public McsrRuleFormat PendingFormat = McsrRuleFormat.StandardAllPeak;
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

        // ===== 作废 / 断线 =====
        public bool MatchVoided;
        public string VoidReason = "";
        public bool VoidSubmitted;
        public bool DisconnectConfirmed;
        public bool ObservingDisconnect;
        // 一次性广播标记: 同一条提示每局只推一次, 避免轮询重复刷屏
        public bool StartAnnounced;
        public bool SelfPeakAnnounced;
        public bool OppPeakAnnounced;
        public bool SelfDnfAnnounced;
        public bool OppDnfAnnounced;
        public bool OppDcAnnounced;
        public bool RoomClosingAnnounced;
        public float DisconnectObserveUntil;
        public string DisconnectReason = "";
        public bool OpponentDepartedObserved;
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
        // RANKED 原始分段兜底 (仅当自建采集器完全没数据时才使用)
        public readonly List<long> LiveSplitsFallback = new List<long>();

        public string StatusMessage = "";

        private readonly IRankedPeakGateway _gw;
        private readonly Action _notify;
        private MatchSettlementService _settlement;

        public MatchSession(IRankedPeakGateway gw, McsrRuleFormat format, int ascent, bool rated, float queueStartTime, Action notify)
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
            SelfPeakAnnounced = false;
            OppPeakAnnounced = false;
            SelfDnfAnnounced = false;
            OppDnfAnnounced = false;
            OppDcAnnounced = false;
            RoomClosingAnnounced = false;
            MatchStartTime = -1f;
            StartCountdownUntil = -1f;
            HostStartRequested = false;
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

        // 求本局路线在"绝对进度点索引"里的区间
        // 必须来自游戏本体真实 progressPoints, 不能用 Splits.RouteLength 倒推
        public bool TryResolveRouteSpan(out int routeStartIndex, out int routeLength)
        {
            routeStartIndex = -1;
            routeLength = -1;

            // 全山: 不需要裁剪, 直接按已记录的最大索引导出
            if (Route <= 0) return false;

            // Route 是 1-based 的群系出现序号, 先换算成真实进度点索引
            int start;
            int count;
            if (!_gw.TryGetBiomePointSpan(Route - 1, out start, out count)) return false;
            if (count <= 0) return false;

            routeStartIndex = start;
            routeLength = count;
            return true;
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
                }
                OpponentName = opp.PlayerName ?? "";
                // 对手回来了 (重连/换房): 清掉"已消失"标记, 避免下次它再掉线时不作废
                OpponentDepartedObserved = false;
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

                AnnounceOpponentDnf(opp);
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

        // 我方登顶: 由结算路径调用 (那里才知道确切的完赛用时)
        public void AnnounceSelfPeak(long elapsedMs)
        {
            if (SelfPeakAnnounced) return;
            SelfPeakAnnounced = true;
            string clock = CsrFormat.FormatClock(elapsedMs / 1000.0);
            _gw.PushSystemChat(CsrLoc.T("chat.match.self_peak", clock));
            // 等对手期间房间会在 AutoEndMs 后关闭, 顺手把倒计时也广播掉
            AnnounceRoomClosing();
        }

        // 我方阵亡
        public void AnnounceSelfDnf(bool isTimeout)
        {
            if (SelfDnfAnnounced) return;
            SelfDnfAnnounced = true;
            _gw.PushSystemChat(CsrLoc.T(isTimeout ? "chat.match.self_timeout" : "chat.match.self_dnf"));
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
        // 主数据源为自建 SplitTracker (只记录真实到达, 不填充假数据)
        private void CaptureLiveSplits(RaceParticipant self, RaceParticipant opp, RaceInfo race)
        {
            try
            {
                // 保留一份 RANKED 原始数据作为兜底，只有本次确实读到分段时才覆盖
                if (self != null && !string.IsNullOrEmpty(self.SteamId))
                {
                    List<long> mine = _gw.GetLiveSplits(self.SteamId);
                    if (mine != null && mine.Count > 0)
                    {
                        LiveSplitsFallback.Clear();
                        for (int i = 0; i < mine.Count; i++) LiveSplitsFallback.Add(mine[i]);
                    }
                }

                // 对手: 用 Flags >> 4 的绝对点灯数推断, 但只记录"确实前进"的那一格, 不再伪造填充
                if (opp != null && !string.IsNullOrEmpty(opp.SteamId))
                {
                    int segmentsLit = opp.Flags >> 4;
                    long elapsed = opp.LiveTimeMs.HasValue ? opp.LiveTimeMs.Value : 0L;
                    if (segmentsLit > 0 && elapsed > 0)
                    {
                        Splits.ObserveSegmentsLit(opp.SteamId, segmentsLit, elapsed);
                    }
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 采集赛段数据失败: " + ex.Message);
            }
        }

        // 分段名: 单群系只显示该群系内的段, 全山显示从第一段开始
        public string BuildSplitLabel(int index)
        {
            int absoluteIndex = index;
            if (Route > 0)
            {
                int routeStart;
                int routeLen;
                if (TryResolveRouteSpan(out routeStart, out routeLen) && routeStart >= 0)
                {
                    absoluteIndex = routeStart + index;
                }
            }
            string biome = _gw.GetSegmentBiomeName(LevelIndex, absoluteIndex) ?? "";
            return "SEG " + (index + 1) + (string.IsNullOrEmpty(biome) ? "" : " · " + biome);
        }

        // 把 SplitTracker 的原始记录裁剪成本局路线的相对分段
        public void RebuildSplits(List<long> mySplits, List<long> oppSplits)
        {
            mySplits.Clear();
            oppSplits.Clear();

            string mySteamId = _gw.MySteamId;

            int routeStart = Splits.RouteStartIndex;
            int routeLen = Splits.RouteLength;

            // 单群系: routeStart = 真实进度点起点, routeLen = 该群系包含的段数
            // 全山: routeStart = -1, routeLen = -1 (取已记录的最大索引)
            if (routeStart < 0 && Route > 0)
            {
                int resolvedStart;
                int resolvedLen;
                if (TryResolveRouteSpan(out resolvedStart, out resolvedLen))
                {
                    routeStart = resolvedStart;
                    routeLen = resolvedLen;
                }
                else
                {
                    // 拿不到真实区间就保持"开放长度", 不要用 Route-1 猜一个起点
                    routeStart = -1;
                    routeLen = -1;
                }
            }

            List<long> mine = Splits.Export(mySteamId, routeStart, routeLen);
            List<long> theirs = Splits.Export(OpponentSteamId, routeStart, routeLen);

            // 自建采集器完全没数据时才回退到 RANKED 原始分段。
            // 单群系原本被排除, 但 RANKED 的 LiveSplitTrack 里存的是"绝对点灯段"的时间,
            // 单群系只会让序列前置若干空位, 不会伪造数据, 所以展示层裁剪后依然可用,
            // 比"整张表全空"要好得多。
            if (CountNonZero(mine) == 0 && CountNonZero(theirs) == 0
                && LiveSplitsFallback.Count > 0)
            {
                PcsrLog.LogInfo("[PeakCSR] 自建分段为空，回退使用 RANKED 原始分段 (Route=" + Route + ")。");
                for (int i = 0; i < LiveSplitsFallback.Count; i++) mySplits.Add(LiveSplitsFallback[i]);
                return;
            }

            for (int i = 0; i < mine.Count; i++) mySplits.Add(mine[i]);
            for (int i = 0; i < theirs.Count; i++) oppSplits.Add(theirs[i]);
        }

        private static int CountNonZero(List<long> list)
        {
            int c = 0;
            for (int i = 0; i < list.Count; i++) if (list[i] > 0) c++;
            return c;
        }
    }
}
