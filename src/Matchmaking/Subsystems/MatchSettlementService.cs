using System.Collections.Generic;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // 胜负/DNF/作废幂等判定、触发算分与战报生成、作废延迟退房
    // (原 MatchController.EvaluateWinCondition / ConcludeMatch / ConcludeVoid / RetireVoidLobby / BuildPostMatchData 收口)
    public sealed class MatchSettlementService
    {
        private readonly IRankedPeakGateway _gw;
        private readonly MatchCoordinator _owner;

        public MatchSettlementService(IRankedPeakGateway gw, MatchCoordinator owner)
        {
            _gw = gw;
            _owner = owner;
        }

        // ===== 胜负判定 =====
        public void EvaluateWinCondition(MatchSession session, RaceInfo race, RaceParticipant self, RaceParticipant opp)
        {
            if (self == null) return;

            // 断线已确认: 本局只可能作废, 任何"掉线 -> 判胜/判负"都不允许
            if (session.DisconnectConfirmed) return;
            // 本机连接此刻不健康: 手上的 self/opp 数据不可信, 一律不判
            if (!_owner.Guardian.IsLocalConnectivityHealthy(session)) return;

            // 只有真实完赛 (服务端给出 finished + 名次) 才算数, 不做任何本地推断
            if (self.State == "finished" && self.Rank.HasValue && self.Rank.Value == 1)
            {
                double sec = (self.TotalMs ?? 0) / 1000.0;
                ConcludeMatch(session, true, CsrLoc.T("status.win.peak", CsrFormat.FormatClock(sec)));
                return;
            }
            if (opp != null && opp.State == "finished" && opp.Rank.HasValue && opp.Rank.Value == 1)
            {
                double sec = (opp.TotalMs ?? 0) / 1000.0;
                ConcludeMatch(session, false, CsrLoc.T("status.loss.peak", session.OpponentName, CsrFormat.FormatClock(sec)));
                return;
            }

            // 对手 dnf 分支的前提: 本局必须真的通过实时通道拿到过对手心跳证据。
            // socket 从未连上时 IsOpponentHeartbeatFresh 会返回 true (无法判断不做反向误判),
            // 那个 true 不能当成"对手活着"的证据, 否则一开局就能凭服务端 dnf 白拿一胜
            bool heartbeatEvidenceAvailable = session.LiveEverConnected;

            // 死亡判负: 必须是我方自己死亡 (不是 revived/replaced), 且本局确实禁止复活
            if (self.State == "dnf" && !race.ReviveDead)
            {
                // deaths 为 0 的 dnf 是超时/放弃而非阵亡, 播报要区分开
                bool died = self.Deaths > 0;
                session.AnnounceSelfDnf(!died);
                ConcludeMatch(session, false, CsrLoc.T("status.loss.dnf"));
                return;
            }

            // 对手 dnf: 必须同时满足"对手实时心跳还新鲜" 才认定是真实死亡/投降,
            // 否则就是对手掉线 (服务端把掉线者也标成 dnf), 这种情况要作废而不是判我方胜。
            if (opp != null && opp.State == "dnf" && !race.ReviveDead)
            {
                // 没有心跳通道可依 (从未连上) 时不能判胜: 交给观察窗, 恢复不了就作废
                if (!heartbeatEvidenceAvailable)
                {
                    PcsrLog.LogWarning("[PeakCSR] 对手 DNF 但本局从未建立实时通道，无法确认是阵亡还是掉线，暂不判胜。");
                    _owner.Guardian.BeginDisconnectObservation(session, "opponent dnf without live evidence");
                    return;
                }

                if (_gw.IsOpponentHeartbeatFresh(session.OpponentSteamId))
                {
                    session.AnnounceOpponentDnf();
                    ConcludeMatch(session, true, CsrLoc.T("status.win.opp_dnf", session.OpponentName));
                }
                else
                {
                    PcsrLog.LogWarning("[PeakCSR] 对手 dnf 但心跳已停止，判定为对手掉线，本局作废。");
                    session.AnnounceOpponentDisconnect();
                    ConcludeVoid(session, CsrLoc.T("status.void_opp_dc", session.OpponentName));
                }
                return;
            }

            // 对手从参与者列表里消失: 同样只可能是掉线/被踢, 绝不判胜
            if (opp == null && !string.IsNullOrEmpty(session.OpponentSteamId) && !session.OpponentDepartedObserved)
            {
                session.OpponentDepartedObserved = true;
                PcsrLog.LogWarning("[PeakCSR] 对手已从房间消失，本局作废（不判胜负、不计分）。");
                session.AnnounceOpponentDisconnect();
                ConcludeVoid(session, CsrLoc.T("status.void_opp_dc", session.OpponentName));
            }
        }

        public void ConcludeMatch(MatchSession session, bool iWon, string reason)
        {
            ConcludeMatchInternal(session, iWon, reason, false);
        }

        // 无法确认结果 (掉线 / 被踢 / Photo 房间断线) 的作废局: 双方都不计分、不计胜负
        public void ConcludeVoid(MatchSession session, string reason)
        {
            if (session.State == CsMatchState.Finished) return;
            // 同一局可能被观察窗 / 连接检测 / 对手消失三条路径同时触发, 只认第一次 (护栏 4)
            if (session.VoidSubmitted) return;
            session.VoidSubmitted = true;
            session.DisconnectConfirmed = true;
            session.MatchVoided = true;
            session.VoidReason = reason ?? "";
            PcsrLog.LogWarning("[PeakCSR] 本局作废: " + reason);

            // 尽最大努力把作废决定同步给对手: 对手若还在线, 能立即收尾而不是干等观察窗。
            // 发送失败完全无所谓 (多半是本机已经断网), 所以整段包在 try/catch 里。
            try
            {
                _gw.SendChat(PcsrProtocol.EncodeVoid());
            }
            catch
            {
            }

            ConcludeMatchInternal(session, false, reason, true);

            // 作废后房间已经没有继续存在的意义, 尽早清掉公开房标记,
            // 避免残留的 "1/2 等待中" 房间被第三方插进来打一局没有规则的幽灵对局。
            RetireVoidLobby(session);

            // 通知云端作废, 让双方都不会被扣分
            LadderSyncService ladder = _owner.Ladder;
            if (ladder != null && !string.IsNullOrEmpty(session.InviteCode) && !string.IsNullOrEmpty(session.OpponentSteamId))
            {
                long durationMs = _gw.TimerElapsedMs;
                ladder.SubmitSignedMatchReport(
                    session.InviteCode, session.OpponentSteamId, session.OpponentName, session.OpponentElo,
                    "", session.Seed, session.LevelIndex, session.Ascent, session.Format, session.Route, durationMs,
                    session.MyBestProgress, session.OppBestProgress,
                    true, reason);
            }
        }

        private void ConcludeMatchInternal(MatchSession session, bool iWon, string reason, bool isVoid)
        {
            if (session.State == CsMatchState.Finished) return;
            session.State = CsMatchState.Finished;
            session.MyRerollVoteActive = false;
            session.OppRerollVoteActive = false;

            // 战报进度快照补正:
            // ReportedProgress 是逐帧单调记录的真值 (确认登顶后为 1.0), 用它补正。
            // PeakReported 只保存"观测到的峰值", 不包含完赛强制值, 不能直接拿来当战报进度。
            if (!isVoid)
            {
                float peak = ProgressMonotonicState.ReportedProgress;
                if (peak > session.MyBestProgress) session.MyBestProgress = peak;
                // 我自己登顶完赛: 无论快照停在哪一帧, 战报都应该显示 100%
                if (iWon) session.MyBestProgress = 1f;
            }
            if (session.MyBestProgress < 0f) session.MyBestProgress = 0f;
            if (session.MyBestProgress > 1f) session.MyBestProgress = 1f;
            if (session.OppBestProgress < 0f) session.OppBestProgress = 0f;
            if (session.OppBestProgress > 1f) session.OppBestProgress = 1f;

            PlayerProfileBridge profile = _owner.Profile;
            bool wasPlacement = profile.Record.TotalGames < EloEngine.PlacementMatchesCount;

            SettlementDetail detail = isVoid
                ? profile.RecordVoid(session.Format, session.Ascent, reason)
                : profile.RecordMatchResultDetailed(
                    session.OpponentSteamId,
                    session.OpponentElo,
                    iWon ? 1.0 : 0.0,
                    session.Format,
                    session.Ascent,
                    session.IsRatedMatch,
                    session.MyBestProgress,
                    session.OppBestProgress);

            // 自方登顶播报 (带上真实完赛用时; 掉线作废不作登顶播报)
            if (!isVoid && iWon)
            {
                long peakMs = _gw.TimerElapsedMs;
                if (peakMs > 0) session.AnnounceSelfPeak(peakMs);
            }

            string sign = detail.TotalDelta >= 0 ? "+" : "";
            session.StatusMessage = reason + (isVoid ? " | 本局作废，不计分" : (" | ELO: " + detail.NewElo + " (" + sign + detail.TotalDelta + ")"));
            if (!string.IsNullOrEmpty(detail.AntiBoostNote) && !isVoid)
            {
                session.StatusMessage += " [" + detail.AntiBoostNote + "]";
            }
            PcsrLog.LogInfo("[PeakCSR] 对局结算完成: " + session.StatusMessage);

            BuildPostMatchData(session, iWon, reason, detail, wasPlacement, isVoid);

            // 战报不再糊脸自动弹出: 挂一个"可查看"标记, 等玩家在结算界面/主菜单主动打开
            // 作废局也给标记, 否则玩家会以为对局凭空消失
            PostMatchReportView.MarkPostMatchPending();

            // 换房期间禁止上报
            if (session.SwitchingRerollRoom && !isVoid)
            {
                PcsrLog.LogWarning("[PeakCSR] 换图切换期间不提交战报 (房间号与地图数据不属于同一场)。");
            }
            else if (!isVoid && session.IsRatedMatch && _owner.Ladder != null
                && !string.IsNullOrEmpty(session.InviteCode) && !string.IsNullOrEmpty(session.OpponentSteamId))
            {
                string mySteamId = _gw.MySteamId;
                string winnerSteamId = iWon ? mySteamId : session.OpponentSteamId;
                long durationMs = _gw.TimerElapsedMs;
                _owner.Ladder.SubmitSignedMatchReport(
                    session.InviteCode,
                    session.OpponentSteamId,
                    session.OpponentName,
                    session.OpponentElo,
                    winnerSteamId,
                    session.Seed,
                    session.LevelIndex,
                    session.Ascent,
                    session.Format,
                    session.Route,
                    durationMs,
                    session.MyBestProgress,
                    session.OppBestProgress
                );
            }

            session.Notify();
        }

        // 战报快照构建 (原 BuildPostMatchData 原样迁移)
        private void BuildPostMatchData(MatchSession session, bool iWon, string reason, SettlementDetail detail, bool wasPlacement, bool isVoid)
        {
            CsrPostMatchData data = new CsrPostMatchData
            {
                IWon = iWon,
                IsVoid = isVoid,
                Reason = reason,
                OpponentName = session.OpponentName,
                OpponentElo = session.OpponentElo,
                InviteCode = session.InviteCode,
                LevelIndex = session.LevelIndex,
                Seed = session.Seed,
                Ascent = session.Ascent,
                Format = session.Format,
                OldElo = detail.OldElo,
                NewElo = detail.NewElo,
                TotalDelta = detail.TotalDelta,
                BaseDelta = detail.BaseDelta,
                ProgressBonus = detail.ProgressBonus,
                // 保底/上限钳制修正与保底下限: 战报明细行"保底/倍率修正"依赖这两项
                FloorAdjust = detail.FloorAdjust,
                MinWinDelta = detail.MinWinDelta,
                MyProgress = session.MyBestProgress,
                OppProgress = session.OppBestProgress,
                ModeMultiplier = detail.ModeMultiplier,
                IsPlacement = wasPlacement,
                OldTier = detail.OldTierFull,
                NewTier = detail.NewTierFull,
                AntiBoostNote = detail.AntiBoostNote,
                CreatedAt = Time.realtimeSinceStartup
            };
            data.BiomeLabel = _gw.GetBiomeLabel(session.LevelIndex) ?? "";

            // 分段数据优先用自建采集器 (只记录真实到达)
            List<long> mySplits = new List<long>();
            List<long> oppSplits = new List<long>();
            session.RebuildSplits(mySplits, oppSplits);

            // 汇总总用时
            if (mySplits.Count > 0) data.MyTotalMs = mySplits[mySplits.Count - 1];
            if (oppSplits.Count > 0) data.OppTotalMs = oppSplits[oppSplits.Count - 1];

            // 构建分段对比行: 按本局路线裁剪并重编号, 缺的段老实留空
            int count = Mathf.Max(mySplits.Count, oppSplits.Count);
            for (int i = 0; i < count; i++)
            {
                CsrSplitRow row = new CsrSplitRow();
                row.MyMs = i < mySplits.Count ? mySplits[i] : 0;
                row.OppMs = i < oppSplits.Count ? oppSplits[i] : 0;
                row.Label = session.BuildSplitLabel(i);
                data.Splits.Add(row);
            }

            PostMatchReportView.LastPostMatch = data;
        }

        // 作废后把房间收起来, 杜绝"公开房残留 1/2 等待中"被第三方插进来。
        // 关键点: 掉线时我们通常已经没法和 RANKED 服务端通话了, 这里的操作必须全部容错,
        // 任何一步失败都不能影响本地已经把本局判成作废这件事。
        private void RetireVoidLobby(MatchSession session)
        {
            try
            {
                // 服务不可用/拿不到房间时直接退出, 但不清 RetireLobbyAt:
                // 由 ConnectivityGuardian.TickRetireLobby 的可用性检查前移来保证下次再试
                if (_gw.ServiceAvailable == false)
                {
                    PcsrLog.LogWarning("[PeakCSR] 作废局收尾暂不可用 (服务未就绪)，稍后重试。");
                    session.RetireLobbyAt = Time.realtimeSinceStartup + MatchSession.VoidRetryIntervalSeconds;
                    return;
                }
                RankedPeak.Services.RaceInfo race = _gw.CurrentRace;
                if (race == null)
                {
                    PcsrLog.LogWarning("[PeakCSR] 作废局收尾时拿不到房间快照，稍后重试。");
                    session.RetireLobbyAt = Time.realtimeSinceStartup + MatchSession.VoidRetryIntervalSeconds;
                    return;
                }

                // 已经结束的房间 RANKED 自己会收走, 不必重复操作
                if (race.Status == "finished")
                {
                    session.RetireLobbyAt = -1f;
                    return;
                }

                // 第一步: 房主把公开房改成私密。
                // 即便退出后房间暂时只剩 1 人, 也不会出现在大厅列表里被第三方捡走。
                // 只有房主能改配置, 加入方这里会静默失败, 这正是期望行为。
                if (_gw.IsHost)
                {
                    try
                    {
                        _gw.SetPublic(false, err =>
                        {
                            PcsrLog.LogInfo("[PeakCSR] 作废局公开房已置为私密: " + err);
                        });
                    }
                    catch (System.Exception ex)
                    {
                        PcsrLog.LogWarning("[PeakCSR] 作废局置私密失败: " + ex.Message);
                    }
                }

                // 第二步: 预约延迟退出, 让服务端把本机标记 removed。
                // 人数归零后房间由服务端回收, 不会长期挂在列表上。
                // 不立刻退是为了先让 VOID 协议走完 socket, 否则对手收不到通知只能干等观察窗。
                session.RetireLobbyAt = Time.realtimeSinceStartup + MatchSession.VoidBroadcastGraceSeconds;
                PcsrLog.LogInfo("[PeakCSR] 作废局将在 " + MatchSession.VoidBroadcastGraceSeconds + "s 后退出房间。");
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 作废局房间收尾异常: " + ex.Message);
            }
        }
    }
}
