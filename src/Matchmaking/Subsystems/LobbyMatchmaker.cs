using System;
using System.Collections.Generic;
using RankedPeak.Models;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // 搜房、建房、双房自动合并迁移、队列情报独立心跳 (原 MatchController 对应逻辑收口)
    public sealed class LobbyMatchmaker
    {
        private readonly IRankedPeakGateway _gw;
        private readonly MatchCoordinator _owner;
        public readonly CsrQueueStats QueueStats = new CsrQueueStats();

        // 队列情报独立心跳: 即使不在排队也要定期刷新, 否则面板人数会一直定格
        private const float QueueHeartbeatSeconds = 8f;
        private bool _queueHeartbeatInFlight;
        private float _nextQueueHeartbeatTime;

        public LobbyMatchmaker(IRankedPeakGateway gw, MatchCoordinator owner)
        {
            _gw = gw;
            _owner = owner;
        }

        // ===== 在途请求看门狗 =====
        public void TickInFlightWatchdog()
        {
            MatchSession s = _owner.CurrentSession;
            if (s == null) return;
            float now = Time.realtimeSinceStartup;

            if (s.LobbyFetchInFlight && s.LobbyFetchStartedAt > 0f
                && now - s.LobbyFetchStartedAt > MatchSession.LobbyFetchTimeoutSeconds)
            {
                s.LobbyFetchInFlight = false;
                s.LobbyFetchStartedAt = -1f;
                PcsrLog.LogWarning("[PeakCSR] 大厅请求超时未回调，强制解锁并重试。");
                s.NextLobbyPollTime = now;
            }

            if (s.RoomOpInFlight && s.RoomOpStartedAt > 0f
                && now - s.RoomOpStartedAt > MatchSession.RoomOpTimeoutSeconds)
            {
                s.RoomOpInFlight = false;
                s.RoomOpStartedAt = -1f;
                s.TransitionInFlight = false;
                // 递增代际: 在飞的请求稍后即使成功也视为迟到结果并丢弃
                s.RoomOpGeneration++;
                PcsrLog.LogWarning("[PeakCSR] 房间操作超时未回调，强制解锁并作废该请求 (状态 " + s.State + ")。");
                if (s.State == CsMatchState.SearchingLobby) s.NextLobbyPollTime = now;
            }
        }

        // 发起房间操作: 自增代际并记录操作类型 (失败文案要能说清是加入还是建房)
        private int BeginRoomOp(MatchSession session, string kind)
        {
            session.RoomOpGeneration++;
            session.LastLobbyOpKind = kind;
            session.RoomOpInFlight = true;
            session.RoomOpStartedAt = Time.realtimeSinceStartup;
            return session.RoomOpGeneration;
        }

        // 迟到结果: 主动清掉它在服务端留下的房间, 绝不让它污染 CurrentRace
        private void DiscardLateRoomOp(MatchSession session, RaceInfo race, string kind)
        {
            PcsrLog.LogWarning("[PeakCSR] 收到已作废的房间操作结果 (" + kind + ")，丢弃并清理房间 ["
                + (race != null ? race.InviteCode : "?") + "]。");
            if (ReferenceEquals(session, _owner.CurrentSession))
            {
                session.SuppressRaceClosedTeardown = true;
            }
            try
            {
                if (_gw.InRace || _gw.RaceIsOver) _gw.LeaveRace();
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 清理迟到房间失败 (可忽略): " + ex.Message);
            }
            finally
            {
                if (ReferenceEquals(session, _owner.CurrentSession))
                {
                    session.SuppressRaceClosedTeardown = false;
                    session.RoomOpInFlight = false;
                    session.RoomOpStartedAt = -1f;
                    if (session.State == CsMatchState.SearchingLobby)
                    {
                        session.NextLobbyPollTime = Time.realtimeSinceStartup;
                    }
                }
            }
        }

        // ===== 排队主轮询 (SearchingLobby 阶段) =====
        public void TickQueuePoll(MatchSession session)
        {
            if (session.LobbyFetchInFlight) return;
            if (session.RoomOpInFlight) return;
            if (Time.realtimeSinceStartup < session.NextLobbyPollTime) return;
            if (_gw.ServiceAvailable == false) return;

            session.LobbyFetchInFlight = true;
            session.LobbyFetchStartedAt = Time.realtimeSinceStartup;
            session.NextLobbyPollTime = Time.realtimeSinceStartup + MatchSession.LobbyPollIntervalSeconds;
            _gw.FetchLobbies((entries, total) =>
            {
                session.LobbyFetchInFlight = false;
                session.LobbyFetchStartedAt = -1f;
                OnQueueLobbies(session, entries);
            }, err =>
            {
                session.LobbyFetchInFlight = false;
                session.LobbyFetchStartedAt = -1f;
                OnQueueError(session, err);
            });
        }

        private void OnQueueLobbies(MatchSession session, List<RaceLobbyEntry> entries)
        {
            session.LobbyFetchInFlight = false;
            UpdateQueueStats(entries);

            // 已取消/已被替换的搜索: 这个回调既不能发起 join, 也不能拆会话
            if (!IsStillCurrent(session)) return;
            if (session.State != CsMatchState.SearchingLobby) return;

            bool sawIncompatibleCsrLobby = false;

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    RaceLobbyEntry item = entries[i];
                    if (item == null) continue;

                    PcsrLog.LogInfo("[PeakCSR] 大厅候选 [" + item.InviteCode + "] 状态=" + item.Status
                        + " 玩家=" + item.Players + "/" + item.MaxPlayers
                        + " 天阶=" + item.Ascent + " 路线=" + item.Route
                        + " autoEnd=" + item.AutoEndMs + " 版本=" + item.GameVersion
                        + " 复活=" + item.ReviveDead + " 幽灵=" + item.GhostsVisible
                        + " 盲种=" + item.SecretSeed + " 兼容=" + item.Compatible);

                    if (!item.Compatible)
                    {
                        // 只有"本来可以加入"的房间 (lobby 且有空位) 才算环境不匹配
                        if (RoomMagicCodec.IsCsrMagicMs(item.AutoEndMs)
                            && item.AutoEndMs == RoomMagicCodec.GetMagicAutoEndMs(
                                session.Format, session.Ascent, session.IsRatedMatch)
                            && item.Status == "lobby"
                            && !item.IsFull)
                        {
                            sawIncompatibleCsrLobby = true;
                        }
                        continue;
                    }

                    if (!LobbyFilter.MatchesQueue(item, session.PendingFormat, session.PendingAscent,
                            session.LastOwnedRaceCode, session.IsRatedMatch)) continue;

                    session.StatusMessage = CsrLoc.T("status.found_joining", item.InviteCode);
                    PcsrLog.LogInfo("[PeakCSR] 找到匹配的 PCSR 1v1 房间 [" + item.InviteCode + "]，正在加入...");
                    session.Notify();
                    int gen = BeginRoomOp(session, "join");
                    _gw.JoinRace(item.InviteCode, race =>
                    {
                        if (gen != session.RoomOpGeneration)
                        {
                            DiscardLateRoomOp(session, race, "join");
                            return;
                        }
                        session.RoomOpInFlight = false;
                        session.RoomOpStartedAt = -1f;
                        _owner.OnRaceJoined(session, race);
                    }, err =>
                    {
                        if (gen != session.RoomOpGeneration) return;
                        session.RoomOpInFlight = false;
                        session.RoomOpStartedAt = -1f;
                        OnLobbyError(session, err, "join");
                    });
                    return;
                }
            }

            if (sawIncompatibleCsrLobby)
            {
                session.StatusMessage = CsrLoc.T("status.env_mismatch",
                    _gw.GameVersion, _gw.MapSetHash);
                PcsrLog.LogWarning("[PeakCSR] 检测到同赛制 PCSR 房间但地图集不一致, 本机 mapSetHash="
                    + _gw.MapSetHash + ", gameVersion=" + _gw.GameVersion);
                session.Notify();
                session.NextLobbyPollTime = Time.realtimeSinceStartup + MatchSession.LobbyPollIntervalSeconds;
                return;
            }

            // 建房前最后一道闸: 本机若还挂在某个房间里, 先退掉再建
            if (_gw.InRace || _gw.RaceIsOver)
            {
                PcsrLog.LogWarning("[PeakCSR] 建房前检测到本机仍停留在房间 ["
                    + session.InviteCode + "]，先退出该房间再新建。");
                try
                {
                    _gw.LeaveRace();
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 建房前退房失败 (继续新建): " + ex.Message);
                }
            }

            PcsrLog.LogInfo("[PeakCSR] 当前没有符合赛制的大厅，创建新的 PCSR 1v1 房间。");
            session.State = CsMatchState.SearchingLobby;
            CreateNew1v1Lobby(session, isPublicLobby: true);
        }

        private void OnQueueError(MatchSession session, string err)
        {
            if (!IsStillCurrent(session)) return;
            OnLobbyError(session, err, "lobby_poll");
        }

        // 回调可能属于一个已经被取消/替换掉的搜索: 用会话身份与状态双重过滤
        private bool IsStillCurrent(MatchSession session)
        {
            return session != null && ReferenceEquals(session, _owner.CurrentSession);
        }

        // ===== 双方各建一房时的收敛 =====
        public void TickConvergePoll(MatchSession session)
        {
            if (session.ConvergeFetchInFlight) return;
            if (_gw.ServiceAvailable == false) return;

            // 已经握手成功/已有对手, 无需收敛
            if (session.OpponentVerified || !string.IsNullOrEmpty(session.OpponentSteamId)) return;
            // 房主若已进入倒计时就别再折腾
            if (session.StartCountdownUntil > 0f) return;

            // 房龄不足时不行动: 两人刚同时建房时互相迁移会反复抖动
            if (session.RoomEnteredAt > 0f && Time.realtimeSinceStartup - session.RoomEnteredAt < MatchSession.ConvergeMinRoomAgeSeconds) return;
            if (Time.realtimeSinceStartup < session.NextConvergePollTime) return;

            if (session.RoomOpInFlight) return;

            session.ConvergeFetchInFlight = true;
            session.NextConvergePollTime = Time.realtimeSinceStartup + MatchSession.ConvergePollIntervalSeconds;
            _gw.FetchLobbies((entries, total) =>
            {
                session.ConvergeFetchInFlight = false;
                try
                {
                    OnConvergeLobbies(session, entries);
                }
                catch (Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 收敛轮询处理异常: " + ex.Message);
                }
            }, err =>
            {
                session.ConvergeFetchInFlight = false;
                if (!IsStillCurrent(session)) return;
                session.SwitchingRerollRoom = false;
            });
        }

        private void OnConvergeLobbies(MatchSession session, List<RaceLobbyEntry> entries)
        {
            session.ConvergeFetchInFlight = false;
            UpdateQueueStats(entries);

            if (!IsStillCurrent(session)) return;
            if (session.State != CsMatchState.WaitingOpponent) return;
            if (session.OpponentVerified || !string.IsNullOrEmpty(session.OpponentSteamId)) return;
            if (session.StartCountdownUntil > 0f) return;

            RaceLobbyEntry rival = LobbyFilter.FindConvergeTarget(entries, session.InviteCode,
                session.PendingFormat, session.PendingAscent, session.IsRatedMatch);
            if (rival == null) return;

            PcsrLog.LogInfo("[PeakCSR] 收敛: 放弃自建房 [" + session.InviteCode + "]，迁移到 [" + rival.InviteCode + "]。");
            _gw.PushSystemChat(CsrLoc.T("chat.converge", rival.InviteCode));
            session.StatusMessage = CsrLoc.T("status.converging", rival.InviteCode);

            session.SwitchingRerollRoom = true;   // 抑制旧房关闭时的空 race 回调
            BeginRoomOp(session, "converge");
            string target = rival.InviteCode;

            // 关键: 必须等退房真的落地后再 join
            JoinConvergeTarget(session, target, 0);
            session.Notify();
        }

        // 收敛迁移: 先确认离开旧房, 再进入目标房
        private void JoinConvergeTarget(MatchSession session, string target, int attempt)
        {
            try
            {
                _gw.LeaveRace();
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 收敛退房异常 (继续尝试加入): " + ex.Message);
            }

            int gen = session.RoomOpGeneration;
            _gw.JoinRace(target, race =>
            {
                if (gen != session.RoomOpGeneration)
                {
                    DiscardLateRoomOp(session, race, "converge");
                    return;
                }
                session.RoomOpInFlight = false;
                session.RoomOpStartedAt = -1f;
                session.SwitchingRerollRoom = false;
                session.StrictRulesetJoin = false;
                if (race != null)
                {
                    session.Splits.Clear();
                    _owner.OnRaceJoined(session, race);
                    return;
                }
                FallBackToSearch(session, target, "收敛目标为空", attempt);
            }, err =>
            {
                if (gen != session.RoomOpGeneration) return;
                session.RoomOpInFlight = false;
                session.RoomOpStartedAt = -1f;
                session.SwitchingRerollRoom = false;
                FallBackToSearch(session, target, err, attempt);
            });
        }

        // 收敛失败: 旧房号留作排除项, 避免再把自己送回刚放弃的残局
        private void FallBackToSearch(MatchSession session, string target, string err, int attempt)
        {
            if (!IsStillCurrent(session)) return;
            PcsrLog.LogWarning("[PeakCSR] 收敛迁移失败 (第 " + (attempt + 1) + " 次): " + err);

            if (attempt < 1 && !string.IsNullOrEmpty(target))
            {
                // 一次重试: 多半是退房还没在服务端生效
                session.RoomOpInFlight = true;
                session.RoomOpStartedAt = Time.realtimeSinceStartup;
                session.SwitchingRerollRoom = true;
                JoinConvergeTarget(session, target, attempt + 1);
                return;
            }

            // 记住刚放弃的旧房号, 直接退回搜索队列也不会再捡回自己
            if (!string.IsNullOrEmpty(session.InviteCode)) session.LastOwnedRaceCode = session.InviteCode;
            session.InviteCode = "";
            session.State = CsMatchState.SearchingLobby;
            session.NextLobbyPollTime = 0f;
            session.NextConvergePollTime = Time.realtimeSinceStartup + 2f;
            session.StatusMessage = CsrLoc.T("status.converge_failed_requeue", err);
            session.Notify();
        }

        // ===== 队列情报独立心跳 =====
        public void TickQueueHeartbeat()
        {
            if (_queueHeartbeatInFlight) return;
            if (Time.realtimeSinceStartup < _nextQueueHeartbeatTime) return;
            if (_gw.ServiceAvailable == false) return;

            // 正在排队时复用主轮询, 避免双请求
            MatchSession session = _owner.CurrentSession;
            if (session != null && session.LobbyFetchInFlight) return;

            _nextQueueHeartbeatTime = Time.realtimeSinceStartup + QueueHeartbeatSeconds;
            _queueHeartbeatInFlight = true;
            _gw.FetchLobbies((entries, total) =>
            {
                _queueHeartbeatInFlight = false;
                UpdateQueueStats(entries);
                session2Notify();
            }, err =>
            {
                _queueHeartbeatInFlight = false;
                // 静默失败: 心跳不应打断主流程, 只推迟下次尝试
                _nextQueueHeartbeatTime = Time.realtimeSinceStartup + QueueHeartbeatSeconds;
            });
        }

        private void session2Notify()
        {
            _owner.NotifyStateChanged();
        }

        // ===== 建房 =====
        public void CreateNew1v1Lobby(MatchSession session, bool isPublicLobby)
        {
            System.Random rng = new System.Random(Guid.NewGuid().GetHashCode());
            session.Seed = rng.Next(100000000, 999999999);
            session.LevelIndex = _gw.PickRandomAuthenticLevel(-1);
            session.Ascent = session.PendingAscent >= 0 || session.PendingAscent == RoomMagicCodec.MinAscent
                ? session.PendingAscent
                : session.Ascent;
            session.Route = session.Format == PcsrRuleFormat.BiomeSprint ? rng.Next(1, Mathf.Max(2, _gw.SprintBiomeCount())) : 0;

            string biomeLabel = _gw.GetBiomeLabel(session.LevelIndex);
            session.StatusMessage = CsrLoc.T(isPublicLobby ? "status.creating" : "status.creating_private", biomeLabel);
            PcsrLog.LogInfo("[PeakCSR] 正在创建 PCSR 1v1 房间 (公开=" + isPublicLobby + ", 地图=#" + session.LevelIndex + " " + biomeLabel + ", 难度=" + MatchNaming.GetAscentDisplayName(session.Ascent) + ")...");
            session.Notify();

            int gen = BeginRoomOp(session, "create");
            _gw.CreateRace(session.Seed, session.LevelIndex, session.Ascent, session.Route, isPublicLobby,
                race =>
                {
                    if (gen != session.RoomOpGeneration)
                    {
                        DiscardLateRoomOp(session, race, "create");
                        return;
                    }
                    session.RoomOpInFlight = false;
                    session.RoomOpStartedAt = -1f;
                    _owner.OnRaceJoined(session, race);
                },
                err =>
                {
                    if (gen != session.RoomOpGeneration) return;
                    session.RoomOpInFlight = false;
                    session.RoomOpStartedAt = -1f;
                    // 建房失败在 SearchingLobby 阶段, 不能再留下一个空会话干等
                    session.StatusMessage = CsrLoc.T("status.error", err);
                    PcsrLog.LogWarning("[PeakCSR] 创建房间失败: " + err);
                    _owner.TearDownSession("create_failed");
                    session.Notify();
                });
        }

        // 停掉当前公开房 (取消匹配/回主菜单前调用)
        public void RetireCurrentLobby(MatchSession session)
        {
            if (session == null) return;
            if (string.IsNullOrEmpty(session.InviteCode)) return;
            session.LastOwnedRaceCode = session.InviteCode;
        }

        // ===== 队列统计 =====
        public void UpdateQueueStats(List<RaceLobbyEntry> entries)
        {
            int waitingPlayers = 0, activePlayers = 0;
            // std/asc/spr = 该赛制匹配中人数, stdActive/... = 该赛制激战中人数
            int std = 0, asc = 0, spr = 0;
            int stdPlayers = 0, ascPlayers = 0, sprPlayers = 0;
            int stdActive = 0, ascActive = 0, sprActive = 0;

            // 同一房号只统计一次 (按房号去重; 空房号是服务端异常数据, 直接丢弃)
            HashSet<string> seenRooms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            MatchSession selfSession = _owner.CurrentSession;
            string selfRoom = selfSession != null ? selfSession.InviteCode : "";
            // 已经放弃 / 已作废的自己旧房: 服务端可能还挂着, 不再算进在线人数
            string retiredRoom = selfSession != null ? selfSession.LastOwnedRaceCode : "";
            // 本机真实所在地: 在大厅里被服务端计数, 但不算"别人"
            bool selfPresent = !string.IsNullOrEmpty(selfRoom);

            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    RaceLobbyEntry e = entries[i];
                    if (e == null) continue;
                    if (!RoomMagicCodec.IsCsrMagicMs(e.AutoEndMs)) continue;
                    if (string.IsNullOrEmpty(e.InviteCode)) continue;
                    if (!seenRooms.Add(e.InviteCode)) continue;

                    bool isSelfRoom = !string.IsNullOrEmpty(selfRoom)
                        && string.Equals(e.InviteCode, selfRoom, StringComparison.OrdinalIgnoreCase);
                    bool isRetiredGhost = !isSelfRoom
                        && !string.IsNullOrEmpty(retiredRoom)
                        && string.Equals(e.InviteCode, retiredRoom, StringComparison.OrdinalIgnoreCase);

                    // 已放弃的旧房是幽灵, 整条不计
                    if (isRetiredGhost) continue;

                    PcsrRuleFormat fmt;
                    int ascVal;
                    RoomMagicCodec.ParseMagicAutoEndMs(e.AutoEndMs, out fmt, out ascVal);

                    bool isWaiting = e.Status == "lobby" && !e.IsFull;
                    bool isActive = e.Status == "running" || e.Status == "countdown" || e.Status == "loading" || e.IsFull;

                    // 人数口径: 2 人房的进行中对局固定算 2 人
                    // 服务端在房主用 kick/1 席 RoomPrivacy 收房时会把 players 掉到 1,
                    // 但"开打中"这个事实本身意味着双方都在, 所以按 2 补正。
                    int reported = Mathf.Max(0, e.Players);
                    int cap = e.MaxPlayers > 0 ? e.MaxPlayers : reported;
                    int roomPlayers = reported;
                    if (isActive && cap == 2 && reported == 1)
                    {
                        roomPlayers = 2;
                    }
                    if (isWaiting && roomPlayers < 1)
                    {
                        // 空位房至少有一个房主在等, 不能让"匹配中"显示 0 人
                        roomPlayers = 1;
                    }

                    if (isWaiting)
                    {
                        waitingPlayers += roomPlayers;
                        if (fmt == PcsrRuleFormat.StandardAllPeak) { std += roomPlayers; stdPlayers += roomPlayers; }
                        else if (fmt == PcsrRuleFormat.BiomeSprint) { spr += roomPlayers; sprPlayers += roomPlayers; }
                        else { asc += roomPlayers; ascPlayers += roomPlayers; }
                    }
                    else if (isActive)
                    {
                        activePlayers += roomPlayers;
                        if (fmt == PcsrRuleFormat.StandardAllPeak) { stdPlayers += roomPlayers; stdActive += roomPlayers; }
                        else if (fmt == PcsrRuleFormat.BiomeSprint) { sprPlayers += roomPlayers; sprActive += roomPlayers; }
                        else { ascPlayers += roomPlayers; ascActive += roomPlayers; }
                    }
                }
            }

            // 人数优先: 匹配中 + 激战中 = 正在玩的 PCSR 人数
            QueueStats.WaitingPlayers = waitingPlayers;
            QueueStats.ActivePlayers = activePlayers;
            QueueStats.OnlinePlayers = waitingPlayers + activePlayers;
            QueueStats.StdWaiting = std;
            QueueStats.AscWaiting = asc;
            QueueStats.SprWaiting = spr;
            QueueStats.StdPlayers = stdPlayers;
            QueueStats.AscPlayers = ascPlayers;
            QueueStats.SprPlayers = sprPlayers;
            QueueStats.StdActive = stdActive;
            QueueStats.AscActive = ascActive;
            QueueStats.SprActive = sprActive;
            QueueStats.UpdatedAt = Time.realtimeSinceStartup;
            QueueStats.RefreshedAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            QueueStats.HasData = true;
        }

        // ===== 加入流程 (原 OnRaceJoined 中除幽灵房/赛制校验外的登记逻辑) =====
        public void RegisterRoomEntry(MatchSession session, RaceInfo race)
        {
            session.InviteCode = race.InviteCode ?? "";
            session.Seed = race.Seed;
            session.LevelIndex = race.LevelIndex;
            session.Ascent = race.Ascent;
            session.Route = race.Route;

            // 登记本局路线区间, 供分段采集裁剪使用
            session.Splits.Clear();
            session.EnsureRouteSpanRegistered();

            // 记录本机"当前这局"的房号, 供幽灵房判定区分"我自己的局"和"别人的残局"
            if (!string.IsNullOrEmpty(race.InviteCode))
            {
                session.LastOwnedRaceCode = race.InviteCode;
            }

            if (RoomMagicCodec.IsCsrMagicMs(race.AutoEndMs))
            {
                PcsrRuleFormat fmt;
                int asc;
                RoomMagicCodec.ParseMagicAutoEndMs(race.AutoEndMs, out fmt, out asc);
                session.Format = fmt;
                session.Ascent = asc;
            }
            else
            {
                PcsrRuleFormat fmt;
                int asc;
                if (RoomMagicCodec.TryDecodeRuleset(race.Ascent, race.Route, out fmt, out asc))
                {
                    session.Format = fmt;
                    session.Ascent = asc;
                }
            }

            session.State = CsMatchState.WaitingOpponent;
            session.HostStartRequested = false;
            session.StatusMessage = CsrLoc.T("status.waiting_opp", session.InviteCode, CsrFormat.FormatElapsed(session.QueueElapsedSeconds));
            PcsrLog.LogInfo("[PeakCSR] 已进入排位房间 [" + session.InviteCode + "]，等待对手加入...");
            session.RoomEnteredAt = Time.realtimeSinceStartup;
            session.SyncFromRaceInfo(race);
        }

        // ===== 大厅错误处理 (原 OnLobbyError 原样迁移) =====
        // kind: 失败来自哪一步 (join / create / converge / lobby_poll)
        public void OnLobbyError(MatchSession session, string err, string kind = "")
        {
            session.SwitchingRerollRoom = false;
            session.LobbyFetchInFlight = false;
            session.LobbyFetchStartedAt = -1f;
            session.RoomOpInFlight = false;
            session.RoomOpStartedAt = -1f;
            if (!IsStillCurrent(session)) return;

            string op = string.IsNullOrEmpty(kind) ? session.LastLobbyOpKind : kind;
            bool isPoll = op == "lobby_poll" || string.IsNullOrEmpty(op);

            if (IsEnvironmentMismatchError(err))
            {
                // 先写文案再拆会话: TearDownSession 会把最后一条文案保留给 Idle 状态展示
                session.StatusMessage = CsrLoc.T("status.env_mismatch", _gw.GameVersion, _gw.MapSetHash);
                PcsrLog.LogWarning("[PeakCSR] 房间环境不一致 (地图集/版本), 本机 mapSetHash="
                    + _gw.MapSetHash + ", gameVersion=" + _gw.GameVersion + " | " + err);
                _owner.TearDownSession("env_mismatch");
                session.Notify();
                return;
            }

            if (session.State == CsMatchState.SearchingLobby)
            {
                session.StatusMessage = CsrLoc.T("status.error", err);
                if (isPoll)
                {
                    PcsrLog.LogWarning("[PeakCSR] 大厅轮询失败, 稍后重试: " + err);
                }
                else
                {
                    // 加入/建房失败: 直接撤掉会话, 让玩家能干净地重新点匹配
                    PcsrLog.LogWarning("[PeakCSR] 房间操作失败 (" + op + "): " + err);
                    _owner.TearDownSession("room_op_failed");
                }
                session.NextLobbyPollTime = Time.realtimeSinceStartup + MatchSession.LobbyPollIntervalSeconds;
                session.Notify();
                return;
            }

            // 瞬态网络错误不该毁掉整局
            if (IsTransientNetworkError(err))
            {
                session.LobbyErrorRetries++;
                if (session.LobbyErrorRetries <= MatchSession.MaxTransientLobbyRetries)
                {
                    float backoff = Mathf.Min(2f * session.LobbyErrorRetries, 10f);
                    session.NextLobbyRetryAt = Time.realtimeSinceStartup + backoff;
                    // 复位开赛闸门: 退避到期后由 TickActiveSession 重新起倒计时并再次 StartRace
                    session.HostStartRequested = false;
                    session.StartCountdownUntil = -1f;
                    session.StartAttemptAt = -1f;
                    session.LastCountdownShown = -1f;
                    // 顺手补一次握手, 确认对手此刻真的还在房里
                    session.NextHandshakeSendTime = 0f;
                    session.StatusMessage = CsrLoc.T("status.network_retrying", Mathf.CeilToInt(backoff));
                    PcsrLog.LogWarning("[PeakCSR] 开赛请求遇到瞬态网络错误，"
                        + Mathf.CeilToInt(backoff) + "s 后重试 (" + session.LobbyErrorRetries
                        + "/" + MatchSession.MaxTransientLobbyRetries + "): " + err);
                    session.Notify();
                    return;
                }
                PcsrLog.LogWarning("[PeakCSR] 瞬态网络错误连续 " + session.LobbyErrorRetries
                    + " 次仍未成功，放弃当前房间: " + err);
            }

            // 先写文案再拆会话: TearDownSession 会把最后一条文案保留给 Idle 状态展示,
            // 顺序反了会导致"匹配出错"被旧的"正在加入房间..."覆盖
            session.StatusMessage = CsrLoc.T("status.error", err);
            PcsrLog.LogWarning("[PeakCSR] 匹配服务异常: " + err);
            _owner.TearDownSession("lobby_error");
            session.Notify();
        }

        private static bool IsEnvironmentMismatchError(string err)
        {
            if (string.IsNullOrEmpty(err)) return false;
            return err.IndexOf("version mismatch", StringComparison.OrdinalIgnoreCase) >= 0
                || err.IndexOf("mapSetHash", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        // 瞬态网络错误判定: 与 CloudLadderClient.IsTransientTlsError 保持同一套关键词
        private static bool IsTransientNetworkError(string err)
        {
            if (string.IsNullOrEmpty(err)) return false;
            string e = err.ToLowerInvariant();
            return e.Contains("ssl") || e.Contains("tls") || e.Contains("certificate")
                || e.Contains("cannot connect") || e.Contains("connection reset")
                || e.Contains("timeout") || e.Contains("timed out")
                || e.Contains("no connection") || e.Contains("failed to connect")
                || e.Contains("unreachable") || e.Contains("network");
        }
    }
}
