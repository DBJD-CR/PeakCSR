using UnityEngine;

namespace PeakCSR
{
    // 30s 换图投票与三步换房握手 (VOTE_REQ/VOTE_YES/VOTE_NO/REROLL_ROOM)
    // 原 MatchController 对应逻辑收口; 协议编解码统一走 PcsrProtocol
    public sealed class RerollVoteManager
    {
        private readonly IRankedPeakGateway _gw;
        private readonly MatchCoordinator _owner;

        public RerollVoteManager(IRankedPeakGateway gw, MatchCoordinator owner)
        {
            _gw = gw;
            _owner = owner;
        }

        // 投票超时检测
        public void Tick(MatchSession session)
        {
            if ((session.MyRerollVoteActive || session.OppRerollVoteActive)
                && Time.realtimeSinceStartup >= session.RerollVoteDeadline)
            {
                session.MyRerollVoteActive = false;
                session.OppRerollVoteActive = false;
                session.StatusMessage = CsrLoc.T("status.reroll_timeout");
                PcsrLog.LogInfo("[PeakCSR] 换图投票超时（30秒），继续当前比赛。");
                session.Notify();
            }

            // 换房加入的 20 秒 deadline 在这里也兜一层
            if (session.SwitchingRerollRoom
                && !_gw.IsHost
                && session.PendingRerollJoinDeadline > 0f
                && Time.realtimeSinceStartup > session.PendingRerollJoinDeadline)
            {
                PcsrLog.LogWarning("[PeakCSR] 换图等待超时，未收到或未能加入新房，已回到搜索队列。");
                session.SwitchingRerollRoom = false;
                session.PendingRerollJoinCode = "";
                session.PendingRerollJoinDeadline = -1f;
                session.RerollJoinAttempts = 0;
                session.State = CsMatchState.SearchingLobby;
                session.NextLobbyPollTime = 0f;
                session.StatusMessage = CsrLoc.T("status.reroll_failed_requeue");
                session.Notify();
            }
        }

        // 赛中发起 30 秒双人投票换图
        public void InitiateVote(MatchSession session)
        {
            if (session.State != CsMatchState.Running && session.State != CsMatchState.Countdown
                && session.State != CsMatchState.LoadingLevel && session.State != CsMatchState.WaitingOpponent) return;
            if (session.MyRerollVoteActive || session.OppRerollVoteActive) return;

            System.Random rng = new System.Random(System.Guid.NewGuid().GetHashCode());
            session.ProposedSeed = rng.Next(100000000, 999999999);
            session.ProposedLevelIndex = _gw.PickRandomAuthenticLevel(session.LevelIndex);
            session.ProposedRoute = session.Format == PcsrRuleFormat.BiomeSprint ? rng.Next(1, Mathf.Max(2, _gw.SprintBiomeCount())) : 0;

            session.MyRerollVoteActive = true;
            session.RerollVoteDeadline = Time.realtimeSinceStartup + MatchSession.RerollVoteTimeoutSeconds;
            session.StatusMessage = CsrLoc.T("status.reroll_sent");

            _gw.SendChat(PcsrProtocol.EncodeVoteReq(session.ProposedSeed, session.ProposedLevelIndex, session.ProposedRoute));
            PcsrLog.LogInfo("[PeakCSR] 已发起 30s 换图投票，目标新地图: #" + session.ProposedLevelIndex);

            // 同步在左下角聊天框提示自己
            _gw.PushSystemChat(CsrLoc.T("chat.reroll.req_sent",
                session.ProposedLevelIndex, _gw.GetBiomeLabel(session.ProposedLevelIndex)));
            session.Notify();
        }

        public void RespondVote(MatchSession session, bool accept)
        {
            if (!session.OppRerollVoteActive) return;
            // 截止后同帧内的点击仍然会走到这里 (flag 只在 Tick 里清), 自己再判一次超时
            if (Time.realtimeSinceStartup >= session.RerollVoteDeadline)
            {
                session.OppRerollVoteActive = false;
                session.MyRerollVoteActive = false;
                session.StatusMessage = CsrLoc.T("status.reroll_timeout");
                session.Notify();
                return;
            }
            session.OppRerollVoteActive = false;

            if (!accept)
            {
                _gw.SendChat(PcsrProtocol.EncodeVoteNo());
                session.StatusMessage = CsrLoc.T("status.reroll_declined");
                _gw.PushSystemChat(CsrLoc.T("chat.reroll.declined"));
                session.Notify();
                return;
            }

            _gw.SendChat(PcsrProtocol.EncodeVoteYes(session.ProposedSeed, session.ProposedLevelIndex, session.ProposedRoute));
            ExecuteAgreedReroll(session, session.ProposedSeed, session.ProposedLevelIndex, session.ProposedRoute);
        }

        public void ExecuteAgreedReroll(MatchSession session, int newSeed, int newLevelIndex, int newRoute)
        {
            // 先看能不能换: 拿不到房间时直接放弃, 不清掉进度/分段/峰值
            RankedPeak.Services.RaceInfo current = _gw.CurrentRace;
            if (_gw.ServiceAvailable == false || current == null)
            {
                PcsrLog.LogWarning("[PeakCSR] 换图不可用 (服务或房间缺失)，保持当前对局不变。");
                session.MyRerollVoteActive = false;
                session.OppRerollVoteActive = false;
                session.StatusMessage = CsrLoc.T("status.reroll_failed_requeue");
                session.Notify();
                return;
            }

            session.SwitchingRerollRoom = true;

            // 新一局从零开始统计进度与分段 (换图后路线区间也要重新解析)
            session.ResetForNewRace();
            session.Splits.Clear();

            session.Seed = newSeed;
            session.LevelIndex = newLevelIndex;
            session.Route = newRoute;

            string biomeLabel = _gw.GetBiomeLabel(session.LevelIndex);
            session.StatusMessage = CsrLoc.T("status.reroll_passed", session.LevelIndex, biomeLabel);
            PcsrLog.LogInfo("[PeakCSR] 双方同意换图！新地图: #" + session.LevelIndex + " (" + biomeLabel + "), 新种子: " + session.Seed);
            _gw.PushSystemChat(CsrLoc.T("chat.reroll.accepted", session.LevelIndex, biomeLabel));

            if (current.Status == "lobby")
            {
                // 还在 lobby: 直接改房间配置, 房间没换, 立刻解除拦截
                session.SwitchingRerollRoom = false;
                if (_gw.IsHost)
                {
                    _gw.UpdateRaceConfig(session.Seed, session.LevelIndex, session.Ascent, session.Route,
                        current.IsPublic, err => OnLobbyError(session, err));
                }
                return;
            }

            if (_gw.IsHost)
            {
                session.SwitchingRerollRoom = true;
                if (_gw.TimerState == RankedPeak.Timer.TimerState.Running)
                {
                    try { RankedPeak.Timer.RaceTimer.Instance.AbortRun(); } catch { }
                }

                _gw.CreateRace(session.Seed, session.LevelIndex, session.Ascent, session.Route, false,
                    newRace =>
                    {
                        if (!IsCurrent(session)) return;
                        session.SwitchingRerollRoom = false;
                        if (newRace == null)
                        {
                            OnLobbyError(session, "换图新建房间失败");
                            return;
                        }
                        // 三步握手第 2 步: 把新房号下发给加入方, 否则对方永远不知道新码
                        BroadcastRerollRoom(session, newRace.InviteCode);
                        _owner.OnRaceJoined(session, newRace);
                    },
                    err =>
                    {
                        if (!IsCurrent(session)) return;
                        OnLobbyError(session, err);
                    });
            }
            else
            {
                // 加入方: 只置 flag 并等房主的 REROLL_ROOM 新房号, 不再只能干等
                session.SwitchingRerollRoom = true;
                session.PendingRerollJoinDeadline = Time.realtimeSinceStartup + 20f;
                session.StatusMessage = CsrLoc.T("status.reroll_waiting_room");
            }
            session.Notify();
        }

        // 换房是异步的, 失败必须能自己爬起来
        private const int MaxRerollJoinAttempts = 3;

        private void RetryRerollJoin(MatchSession session)
        {
            if (session.RerollJoinAttempts >= MaxRerollJoinAttempts)
            {
                PcsrLog.LogWarning("[PeakCSR] 换图新房间连续 " + session.RerollJoinAttempts + " 次加入失败，放弃并回到搜索队列。");
                session.SwitchingRerollRoom = false;
                session.PendingRerollJoinCode = "";
                session.RerollJoinAttempts = 0;
                session.State = CsMatchState.SearchingLobby;
                session.NextLobbyPollTime = 0f;
                session.StatusMessage = CsrLoc.T("status.reroll_failed_requeue");
                session.Notify();
                return;
            }
            if (Time.realtimeSinceStartup > session.PendingRerollJoinDeadline)
            {
                PcsrLog.LogWarning("[PeakCSR] 换图新房间加入超过 20 秒仍未成功，放弃并回到搜索队列。");
                session.SwitchingRerollRoom = false;
                session.PendingRerollJoinCode = "";
                session.RerollJoinAttempts = 0;
                session.State = CsMatchState.SearchingLobby;
                session.NextLobbyPollTime = 0f;
                session.StatusMessage = CsrLoc.T("status.reroll_failed_requeue");
                session.Notify();
                return;
            }
            PcsrLog.LogInfo("[PeakCSR] 重试加入换图新房间 [" + session.PendingRerollJoinCode + "] (第 "
                + (session.RerollJoinAttempts + 1) + " 次)。");
            JoinRerollRoom(session, session.PendingRerollJoinCode,
                session.PendingRerollSeed, session.PendingRerollLevel, session.PendingRerollRoute);
        }

        // 房主把新房号与参数广播给对手 (重复发几次提高到达率, 服务端会去重)
        public void BroadcastRerollRoom(MatchSession session, string newCode)
        {
            if (string.IsNullOrEmpty(newCode)) return;
            _gw.SendChat(PcsrProtocol.EncodeRerollRoom(newCode, session.Seed, session.LevelIndex, session.Route));
            PcsrLog.LogInfo("[PeakCSR] 已向对手广播换图新房间号: " + newCode);
        }

        // 加入方收到房主的新房号后, 主动加入
        public void JoinRerollRoom(MatchSession session, string newCode, int seed, int levelIndex, int route)
        {
            if (!IsCurrent(session)) return;
            session.PendingRerollJoinCode = newCode;
            session.PendingRerollSeed = seed;
            session.PendingRerollLevel = levelIndex;
            session.PendingRerollRoute = route;
            if (session.PendingRerollJoinDeadline <= 0f)
            {
                session.PendingRerollJoinDeadline = Time.realtimeSinceStartup + 20f;
            }

            // 先把切换标记立起来: 服务不可用时不直接 return
            session.SwitchingRerollRoom = true;
            if (_gw.ServiceAvailable == false)
            {
                PcsrLog.LogWarning("[PeakCSR] 换房请求时服务不可用，等待自动重试。");
                session.StatusMessage = CsrLoc.T("status.reroll_waiting_room");
                session.Notify();
                return;
            }

            if (!string.Equals(session.InviteCode, newCode, System.StringComparison.OrdinalIgnoreCase))
            {
                // 先退出旧房, 再进新房, 避免两个房间互相覆盖状态
                _gw.LeaveRace();
            }

            session.StatusMessage = CsrLoc.T("status.reroll_joining_room", newCode);
            PcsrLog.LogInfo("[PeakCSR] 收到换图新房号 [" + newCode + "]，正在加入...");
            _gw.JoinRace(newCode, newRace =>
            {
                if (!IsCurrent(session)) return;
                session.PendingRerollJoinCode = "";
                session.PendingRerollJoinDeadline = -1f;
                session.RerollJoinAttempts = 0;
                session.StrictRulesetJoin = false;
                if (newRace != null)
                {
                    session.Splits.Clear();
                    _owner.OnRaceJoined(session, newRace);
                    // 只有真正进了新房才解除换房期拦截 (在里面再清, 避免提前允许结算旧房数据)
                    session.SwitchingRerollRoom = false;
                }
                session.Notify();
            }, err =>
            {
                if (!IsCurrent(session)) return;
                session.RerollJoinAttempts++;
                PcsrLog.LogWarning("[PeakCSR] 加入换图新房间失败 (第 " + session.RerollJoinAttempts + " 次): " + err);
                session.StatusMessage = CsrLoc.T("status.reroll_retrying", session.RerollJoinAttempts);
                session.Notify();
                RetryRerollJoin(session);
            });
            session.Notify();
        }

        // 会话身份校验: 换房回调属于异步链, 玩家可能已经开了新会话
        private static bool IsCurrent(MatchSession session)
        {
            MatchCoordinator owner = MatchCoordinator.Instance;
            if (owner == null) return false;
            return ReferenceEquals(owner.CurrentSession, session);
        }

        private void OnLobbyError(MatchSession session, string err)
        {
            _owner.Matchmaker.OnLobbyError(session, err);
        }
    }
}
