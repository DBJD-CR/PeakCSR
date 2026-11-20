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

        // ===== 排队主轮询 (SearchingLobby 阶段) =====
        public void TickQueuePoll(MatchSession session)
        {
            if (session.LobbyFetchInFlight) return;
            if (session.RoomOpInFlight) return;
            if (Time.realtimeSinceStartup < session.NextLobbyPollTime) return;
            if (_gw.ServiceAvailable == false) return;

            session.LobbyFetchInFlight = true;
            session.NextLobbyPollTime = Time.realtimeSinceStartup + MatchSession.LobbyPollIntervalSeconds;
            _gw.FetchLobbies((entries, total) => OnQueueLobbies(session, entries), err => OnQueueError(session, err));
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
                            && item.AutoEndMs == RoomMagicCodec.GetMagicAutoEndMs(session.Format, session.Ascent)
                            && item.Status == "lobby"
                            && !item.IsFull)
                        {
                            sawIncompatibleCsrLobby = true;
                        }
                        continue;
                    }

                    if (!LobbyFilter.MatchesQueue(item, session.PendingFormat, session.PendingAscent, session.LastOwnedRaceCode)) continue;

                    session.StatusMessage = CsrLoc.T("status.found_joining", item.InviteCode);
                    PcsrLog.LogInfo("[PeakCSR] 找到匹配的 PCSR 1v1 房间 [" + item.InviteCode + "]，正在加入...");
                    session.Notify();
                    session.RoomOpInFlight = true;
                    _gw.JoinRace(item.InviteCode, race =>
                    {
                        session.RoomOpInFlight = false;
                        _owner.OnRaceJoined(session, race);
                    }, err =>
                    {
                        session.RoomOpInFlight = false;
                        OnLobbyError(session, err);
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

            PcsrLog.LogInfo("[PeakCSR] 当前没有符合赛制的大厅，创建新的 PCSR 1v1 房间。");
            session.State = CsMatchState.SearchingLobby;
            CreateNew1v1Lobby(session, isPublicLobby: true);
        }

        private void OnQueueError(MatchSession session, string err)
        {
            if (!IsStillCurrent(session)) return;
            OnLobbyError(session, err);
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
            _gw.FetchLobbies((entries, total) => OnConvergeLobbies(session, entries), err =>
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

            RaceLobbyEntry rival = LobbyFilter.FindConvergeTarget(entries, session.InviteCode, session.PendingFormat, session.PendingAscent);
            if (rival == null) return;

            PcsrLog.LogInfo("[PeakCSR] 收敛: 放弃自建房 [" + session.InviteCode + "]，迁移到 [" + rival.InviteCode + "]。");
            _gw.PushSystemChat(CsrLoc.T("chat.converge", rival.InviteCode));
            session.StatusMessage = CsrLoc.T("status.converging", rival.InviteCode);

            session.SwitchingRerollRoom = true;   // 抑制旧房关闭时的空 race 回调
            session.RoomOpInFlight = true;
            string target = rival.InviteCode;
            _gw.LeaveRace();

            _gw.JoinRace(target, race =>
            {
                session.RoomOpInFlight = false;
                session.SwitchingRerollRoom = false;
                session.StrictRulesetJoin = false;
                if (race != null)
                {
                    session.Splits.Clear();
                    _owner.OnRaceJoined(session, race);
                }
            }, err =>
            {
                session.RoomOpInFlight = false;
                session.SwitchingRerollRoom = false;
                PcsrLog.LogWarning("[PeakCSR] 收敛迁移失败: " + err);
                // 直接回到搜索队列重新找房
                session.InviteCode = "";
                session.State = CsMatchState.SearchingLobby;
                session.NextLobbyPollTime = 0f;
                session.NextConvergePollTime = 0f;
                session.StatusMessage = CsrLoc.T("status.converge_failed_requeue", err);
                session.Notify();
            });
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
            session.Route = session.Format == McsrRuleFormat.BiomeSprint ? rng.Next(1, Mathf.Max(2, _gw.SprintBiomeCount())) : 0;

            string biomeLabel = _gw.GetBiomeLabel(session.LevelIndex);
            session.StatusMessage = CsrLoc.T(isPublicLobby ? "status.creating" : "status.creating_private", biomeLabel);
            PcsrLog.LogInfo("[PeakCSR] 正在创建 PCSR 1v1 房间 (公开=" + isPublicLobby + ", 地图=#" + session.LevelIndex + " " + biomeLabel + ", 难度=" + MatchNaming.GetAscentDisplayName(session.Ascent) + ")...");
            session.Notify();

            session.RoomOpInFlight = true;
            _gw.CreateRace(session.Seed, session.LevelIndex, session.Ascent, session.Route, isPublicLobby,
                race =>
                {
                    session.RoomOpInFlight = false;
                    _owner.OnRaceJoined(session, race);
                },
                err =>
                {
                    session.RoomOpInFlight = false;
                    OnLobbyError(session, err);
                });
        }

        // ===== 队列统计 (原 UpdateQueueStats 原样迁移) =====
        public void UpdateQueueStats(List<RaceLobbyEntry> entries)
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
                    if (!RoomMagicCodec.IsCsrMagicMs(e.AutoEndMs)) continue;

                    int roomPlayers = Mathf.Max(0, e.Players);
                    players += roomPlayers;

                    McsrRuleFormat fmt;
                    int ascVal;
                    RoomMagicCodec.ParseMagicAutoEndMs(e.AutoEndMs, out fmt, out ascVal);

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

            QueueStats.WaitingRooms = waiting;
            QueueStats.ActiveRooms = active;
            // 真实人数: 等待房与激战房各自已把自己房内玩家计入 Players, 直接求和即可, 不再臆造
            QueueStats.OnlinePlayers = players;
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
            // 单群系: 起点/长度都来自真实进度点表
            // 全山: -1 / -1 表示取已记录的最大索引
            int routeStart;
            int routeLen;
            if (session.TryResolveRouteSpan(out routeStart, out routeLen))
            {
                session.Splits.SetRoute(routeStart, routeLen);
            }
            else
            {
                session.Splits.SetRoute(-1, -1);
            }

            // 记录本机"当前这局"的房号, 供幽灵房判定区分"我自己的局"和"别人的残局"
            if (!string.IsNullOrEmpty(race.InviteCode))
            {
                session.LastOwnedRaceCode = race.InviteCode;
            }

            if (RoomMagicCodec.IsCsrMagicMs(race.AutoEndMs))
            {
                McsrRuleFormat fmt;
                int asc;
                RoomMagicCodec.ParseMagicAutoEndMs(race.AutoEndMs, out fmt, out asc);
                session.Format = fmt;
                session.Ascent = asc;
            }
            else
            {
                McsrRuleFormat fmt;
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
        public void OnLobbyError(MatchSession session, string err)
        {
            session.SwitchingRerollRoom = false;
            session.LobbyFetchInFlight = false;
            session.RoomOpInFlight = false;
            if (!IsStillCurrent(session)) return;

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
                PcsrLog.LogWarning("[PeakCSR] 大厅轮询失败, 稍后重试: " + err);
                session.NextLobbyPollTime = Time.realtimeSinceStartup + MatchSession.LobbyPollIntervalSeconds;
                session.Notify();
                return;
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
    }
}
