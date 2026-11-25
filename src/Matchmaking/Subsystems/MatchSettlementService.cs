using System;
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

        // ===== 登顶先后仲裁 =====
        // 唯一可信的胜负依据: 双方各自广播的 PEAK 用时

        // 等对端回执的窗口。这个值只影响"对手包还没到的等待时间", 不影响判定精度,
        // 取 1.5s 是为了覆盖 socket 半秒级抖动, 又远小于原 45s 观察窗
        public const float PeakAckWindowSeconds = 1.5f;

        // 同时登顶的判定阈值
        // 精度事实: RaceTimer.CurrentElapsedMs 是 (Time.time - start) * 1000f, 采样在渲染帧上,
        // 真实分辨力 = 1 帧 (60fps 约 16.7ms); 双方 ResyncStart 的本地起点还有几十毫秒
        // 的网络对齐偏差。取 100ms (约 3~6 帧) 既覆盖起点对齐误差, 又不会把真实胜负误判成同时
        public const long PeakTieThresholdMs = 100L;

        // 由 MatchCoordinator.TickActiveSession 每帧驱动。返回 true 表示本帧已做出裁决。
        public bool ArbitratePeak(MatchSession session)
        {
            if (session == null) return false;
            if (session.OutcomeDecided) return false;
            if (session.State == CsMatchState.Finished) return false;
            if (session.MatchVoided || session.DisconnectConfirmed || session.VoidSubmitted) return false;
            // 观察窗期间一律不仲裁
            if (session.ObservingDisconnect) return false;
            if (!session.SelfPeaked && session.OppPeakMs <= 0) return false;

            float now = Time.realtimeSinceStartup;

            // 情况 1: 双方都已宣告登顶 -> 比较用时
            if (session.SelfPeaked && session.OppPeakMs > 0)
            {
                long delta = session.SelfPeakMs - session.OppPeakMs;
                if (delta < 0) delta = -delta;

                if (delta <= PeakTieThresholdMs)
                {
                    session.OutcomeDecided = true;
                    PcsrLog.LogWarning("[PeakCSR] 双方几乎同时登顶 (差 " + delta + "ms)，本局作废不计分。");
                    ConcludeVoid(session, CsrLoc.T("status.void_peak_tie"));
                    return true;
                }

                session.OutcomeDecided = true;
                bool iWon = session.SelfPeakMs < session.OppPeakMs;
                if (iWon)
                {
                    ConcludeMatch(session, true, CsrLoc.T("status.win.peak",
                        CsrFormat.FormatClock(session.SelfPeakMs / 1000.0)));
                }
                else
                {
                    ConcludeMatch(session, false, CsrLoc.T("status.loss.peak",
                        session.OpponentName, CsrFormat.FormatClock(session.OppPeakMs / 1000.0)));
                }
                return true;
            }

            // 情况 2: 我方已登顶, 对手未宣告 -> 我方先到, 等回执窗口
            if (session.SelfPeaked && session.OppPeakMs <= 0)
            {
                if (session.SelfPeakBroadcastAt < 0f) return false;
                if (now - session.SelfPeakBroadcastAt < PeakAckWindowSeconds) return false;

                session.OutcomeDecided = true;
                ConcludeMatch(session, true, CsrLoc.T("status.win.peak",
                    CsrFormat.FormatClock(session.SelfPeakMs / 1000.0)));
                return true;
            }

            // 情况 3: 对手已宣告登顶, 我方未登顶 -> 可能是我方后到
            // 先等一个对称窗口: 双方用时差在 PeakTieThresholdMs 内应作废而不是判负
            if (!session.SelfPeaked && session.OppPeakMs > 0)
            {
                if (session.OppPeakReceivedAt < 0f) return false;
                if (now - session.OppPeakReceivedAt < PeakAckWindowSeconds)
                {
                    // 窗口内继续观察, 等本地完赛检测把 PEAK 补上
                    return false;
                }

                session.OutcomeDecided = true;
                ConcludeMatch(session, false, CsrLoc.T("status.loss.peak",
                    session.OpponentName, CsrFormat.FormatClock(session.OppPeakMs / 1000.0)));
                return true;
            }

            return false;
        }

        // ===== 胜负判定 =====
        public void EvaluateWinCondition(MatchSession session, RaceInfo race, RaceParticipant self, RaceParticipant opp)
        {
            if (self == null) return;

            // 断线已确认: 本局只可能作废, 任何"掉线 -> 判胜/判负"都不允许
            if (session.DisconnectConfirmed) return;

            // 对手阵亡已由 DIED 包确认: 这是不依赖本机网络状况的确定性证据
            if (session.OppDiedReceived)
            {
                if (!session.OppDnfAnnounced)
                {
                    AnnounceOpponentDnfFromPacket(session);
                }
                ConcludeMatch(session, true, CsrLoc.T("status.win.opp_died", session.OpponentName));
                return;
            }

            // 兜底
            if (opp == null
                && string.Equals(race.Status, "finished", StringComparison.OrdinalIgnoreCase)
                && session.OppPeakMs <= 0
                && !session.SelfPeaked)
            {
                PcsrLog.LogWarning("[PeakCSR] 对手已离开且本局已收尾(未登顶)，判定对手阵亡/退赛，判我方胜。");
                session.AnnounceOpponentDnf();
                ConcludeMatch(session, true, CsrLoc.T("status.win.opp_left", session.OpponentName));
                return;
            }

            // 本机连接此刻不健康: 手上的 self/opp 数据不可信, 一律不判
            if (!_owner.Guardian.IsLocalConnectivityHealthy(session)) return;

            // 服务端给出 finished + 名次时, 只用它来补全"我方/对手完赛用时",
            // 真正的胜负裁决交给 ArbitratePeak (以 PEAK 协议用时为准, 双方结论一致)
            if (self.State == "finished" && self.TotalMs.HasValue)
            {
                session.AnnounceSelfPeak(self.TotalMs.Value);
            }
            if (opp != null && opp.State == "finished" && opp.TotalMs.HasValue)
            {
                session.NoteOpponentPeak(opp.TotalMs.Value);
            }
            if (ArbitratePeak(session)) return;

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

            // 对手 dnf: 阵亡与掉线在服务端都会被标成 dnf, 必须用更强的证据区分
            //
            // 判据优先级:
            //   1) Deaths > 0      -> 真实阵亡 (掉线不会增加死亡计数)
            //   2) race.Status 已 finished -> 对手那一局已经结束, 是阵亡/退赛而非掉线
            //   3) 心跳仍新鲜      -> 兼容旧对端, 按阵亡处理
            //   4) 其余            -> 掉线, 走作废
            if (opp != null && opp.State == "dnf" && !race.ReviveDead)
            {
                bool diedEvidence = opp.Deaths > 0
                    || string.Equals(race.Status, "finished", StringComparison.OrdinalIgnoreCase);

                if (diedEvidence || (heartbeatEvidenceAvailable && _gw.IsOpponentHeartbeatFresh(session.OpponentSteamId)))
                {
                    PcsrLog.LogWarning("[PeakCSR] 对手 DNF 判定为阵亡 (deaths=" + opp.Deaths
                        + ", race=" + race.Status + ")，本局判胜。");
                    session.AnnounceOpponentDnf();
                    ConcludeMatch(session, true, CsrLoc.T("status.win.opp_dnf", session.OpponentName));
                    return;
                }

                if (!heartbeatEvidenceAvailable)
                {
                    PcsrLog.LogWarning("[PeakCSR] 对手 DNF 但无阵亡证据也无实时通道，无法确认结果，暂不判胜。");
                    _owner.Guardian.BeginDisconnectObservation(session, "opponent dnf without live evidence", true);
                    return;
                }

                PcsrLog.LogWarning("[PeakCSR] 对手 dnf 但无阵亡证据且心跳已停止，判定为对手掉线，本局作废。");
                session.AnnounceOpponentDisconnect();
                ConcludeVoid(session, CsrLoc.T("status.void_opp_dc", session.OpponentName));
                return;
            }

            // 对手从参与者列表里消失
            if (opp == null && !string.IsNullOrEmpty(session.OpponentSteamId) && !session.OpponentDepartedObserved)
            {
                session.OpponentDepartedObserved = true;

                // 只有 running / countdown 才算"本局仍在进行"; 其余状态 (finished 等) 都表示对手那局已收尾
                string st = race.Status ?? "";
                bool stillRunning = string.Equals(st, "running", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(st, "countdown", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(st, "loading", StringComparison.OrdinalIgnoreCase);
                bool ended = !string.IsNullOrEmpty(st) && !stillRunning;

                if (ended || session.OppDiedReceived)
                {
                    PcsrLog.LogWarning("[PeakCSR] 对手已离开但本局已结束 (race=" + race.Status
                        + ", died=" + session.OppDiedReceived + ")，判我方胜。");
                    session.AnnounceOpponentDnf();
                    ConcludeMatch(session, true, CsrLoc.T("status.win.opp_left", session.OpponentName));
                    return;
                }

                PcsrLog.LogWarning("[PeakCSR] 对手已从房间消失且本局仍在进行，判定为掉线，本局作废。");
                session.AnnounceOpponentDisconnect();
                ConcludeVoid(session, CsrLoc.T("status.void_opp_dc", session.OpponentName));
            }
        }

        // 由 DIED 包触发的阵亡播报
        private void AnnounceOpponentDnfFromPacket(MatchSession session)
        {
            if (session == null) return;
            session.AnnounceOpponentDnf();
        }

        public void ConcludeMatch(MatchSession session, bool iWon, string reason)
        {
            // RANKED 已收尾 (且记录确认属于本局) 说明本机确实跑完了全程
            // 这是比 SelfPeaked 更硬的证据: 后者依赖我们在 stats.won 那一帧成功落笔,
            // 一旦漏判就会把"完赛"误记成"未登顶", 导致 PB 与终段全部丢失
            //
            // 排除项(必须保留, 否则会把 DNF 误判成完赛):
            //   1) 我方已宣告阵亡/超时 -> 本局绝不可能算完赛
            //   2) 本局已作废 -> 任何完赛数据都不应写进战报
            bool selfOutOfRace = session.SelfDnfAnnounced || session.MatchVoided
                || session.DisconnectConfirmed || session.VoidSubmitted;
            long ranked = selfOutOfRace ? 0L : _gw.AuthoritativeRunMs(session.Seed, session.LevelIndex);
            if (ranked > 0 && session.SelfPeakMs <= 0)
            {
                session.AnnounceSelfPeak(ranked);
                PcsrLog.LogInfo("[PeakCSR] 以 RANKED 权威记录补登我方完赛用时: " + ranked + "ms");
            }

            // 只有"我方真的登顶完赛"才允许把战报进度抬到 100%
            bool selfSummited = iWon && session.SelfPeaked;
            ConcludeMatchInternal(session, iWon, reason, false, selfSummited);
        }

        // 本机阵亡即判负 (本局禁复活)
        //
        // 为什么必须本地判负而不能等对手的 PEAK:
        //   1) 死亡瞬间角色被传输出关卡, 下一帧 CurrentRace 往往直接变 null,
        //      SyncFromRaceInfo 不再执行, EvaluateWinCondition 里的 dnf 分支永远不命中
        //   2) 阵亡是确定性事实, 不依赖服务端快照, 也不需要等观察窗
        //   3) 只有本机也上报一条 winner=对手, 云端双端共识才能立即结算;
        //      缺了这条, 对手的获胜上报会永久停在 pending, 整局被当成缺数据作废
        public bool ConcludeSelfDeath(MatchSession session)
        {
            if (!CanConcludeLocalLoss(session)) return false;
            PcsrLog.LogWarning("[PeakCSR] 本机阵亡为确定性事实，本地立即判负并上报。");
            return ConcludeLocalLoss(session, CsrLoc.T("status.loss.dnf"));
        }

        // 本机直接判负 (阵亡已判定 / 主动放弃)
        // 禁复活规则下这才是确定性结论, 只有它能与对手的获胜上报构成双端共识
        public bool ConcludeLocalLoss(MatchSession session, string reason)
        {
            if (!CanConcludeLocalLoss(session)) return false;
            session.SelfDiedConcluded = true;
            session.OutcomeDecided = true;
            session.AnnounceSelfDnf(false);
            ConcludeMatch(session, false, reason);
            return true;
        }

        // 判负的前置条件: 本局真在跑、尚无结论
        private static bool CanConcludeLocalLoss(MatchSession session)
        {
            if (session == null) return false;
            if (session.State == CsMatchState.Finished) return false;
            if (session.VoidSubmitted) return false;
            if (session.SelfDiedConcluded) return false;
            // 已有确定性结论 (对手已死 / 我方已登顶) 时不能再改判
            if (session.OppDiedReceived) return false;
            if (session.SelfPeakMs > 0) return false;
            // 只有真正开打的局才谈得上判负; 建房/等人阶段不算
            if (session.State != CsMatchState.Running
                && session.State != CsMatchState.Countdown
                && session.State != CsMatchState.LoadingLevel) return false;
            return true;
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
            // 先立即尝试一次; 未送达时由 Guardian.TickVoidResend 在窗口内持续补发。
            // 作废那一刻往往正是本机 socket 抖动最严重的时刻, 只发一次很容易丢
            try
            {
                session.VoidBroadcasted = _gw.SendChat(PcsrProtocol.EncodeVoid());
            }
            catch
            {
                session.VoidBroadcasted = false;
            }
            session.VoidResendUntil = Time.realtimeSinceStartup + MatchSession.VoidResendSeconds;
            session.NextVoidResendAt = Time.realtimeSinceStartup + MatchSession.VoidResendIntervalSeconds;

            ConcludeMatchInternal(session, false, reason, true, false);

            // 作废后房间已经没有继续存在的意义, 尽早清掉公开房标记,
            // 避免残留的 "1/2 等待中" 房间被第三方插进来打一局没有规则的幽灵对局。
            RetireVoidLobby(session);

            // 换图窗口内不报云端作废
            if (session.SwitchingRerollRoom)
            {
                PcsrLog.LogWarning("[PeakCSR] 换图切换期间不提交云端作废 (房间号与地图数据不属于同一场)。");
                return;
            }

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

        private void ConcludeMatchInternal(MatchSession session, bool iWon, string reason, bool isVoid, bool selfSummited)
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
                // 只有我自己登顶完赛才强制 100% (对手 DNF/投降 时照实记录)
                if (selfSummited) session.MyBestProgress = 1f;
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

            // 自方登顶播报 (带上真实完赛用时): 只有真的登顶完赛才播,
            // 对手 DNF/投降 拿下的胜利不算登顶
            if (!isVoid && selfSummited)
            {
                // 已有完赛用时就不再重读计时器: 此时计时器可能已被下一局归零
                long peakMs = session.SelfPeakMs > 0 ? session.SelfPeakMs : _gw.TimerElapsedMs;
                if (peakMs > 0) session.AnnounceSelfPeak(peakMs);
            }

            string sign = detail.TotalDelta >= 0 ? "+" : "";
            session.StatusMessage = reason + (isVoid ? " | 本局作废，不计分" : (" | ELO: " + detail.NewElo + " (" + sign + detail.TotalDelta + ")"));
            if (!string.IsNullOrEmpty(detail.AntiBoostNote) && !isVoid)
            {
                session.StatusMessage += " [" + detail.AntiBoostNote + "]";
            }
            PcsrLog.LogInfo("[PeakCSR] 对局结算完成: " + session.StatusMessage);

            // 快照必须当次构建、当次使用: 上报要带这一局的精确分段,
            // 不能等到协程里再回头读 PostMatchReportView.LastPostMatch
            // (那时可能已被下一局覆盖)
            CsrPostMatchData postData = BuildPostMatchData(session, iWon, reason, detail, wasPlacement, isVoid, selfSummited);
            // 采集与归档是一次性的: 放在这里显式执行, 不在 Build 里偷偷做
            PersistPostMatch(postData);

            // 战报立即弹出: 一方登顶即给双方看结果, 不再等结算界面关闭事件
            // 展示层异常不中断后面的签名上报与作废退房
            try
            {
                if (!PostMatchReportView.ShowPostMatchWindow())
                {
                    // 之前这里是静默失败: Canvas 未就绪时既不弹窗也不留痕,
                    // 玩家只会看到"没结算", 线上完全无法定位
                    PcsrLog.LogWarning("[PeakCSR] 战报窗口未能弹出 (UI 画布/宿主不可用)，结算与上报已正常完成。");
                }
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 战报窗口展示失败 (不影响结算): " + ex.Message);
            }

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
                // 只有我方真的登顶才上报自己的完赛用时
                // 优先用已确定的完赛时刻, 避免二次读到时序已被重置的计时器
                long durationMs = 0L;
                if (selfSummited)
                {
                    durationMs = session.SelfPeakMs > 0 ? session.SelfPeakMs : _gw.TimerElapsedMs;
                }
                // 我方分段随战报一起上送, 让对方也能回看我的赛段
                List<CloudSplitDto> mySplits = BuildSplitPayload(postData);
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
                    session.OppBestProgress,
                    false,
                    "",
                    mySplits
                );
            }

            session.Notify();
        }

        // 战报快照 -> 云端分段落库格式
        // 只保留精确记录且已到达的段; 估算段精度不足, 回看时会误导
        private static List<CloudSplitDto> BuildSplitPayload(CsrPostMatchData data)
        {
            if (data == null || data.Splits == null || data.Splits.Count == 0) return null;
            List<CloudSplitDto> list = new List<CloudSplitDto>();
            for (int i = 0; i < data.Splits.Count; i++)
            {
                CsrSplitRow row = data.Splits[i];
                if (row == null || !row.MyReached) continue;
                if (row.MyMs <= 0) continue;
                list.Add(new CloudSplitDto
                {
                    Key = row.Key ?? "",
                    SegMs = row.MySegMs,
                    CumMs = row.MyMs,
                    Estimated = row.MyEstimated,
                    Reached = true
                });
            }
            return list.Count > 0 ? list : null;
        }

        // 战报快照构建 (原 BuildPostMatchData 原样迁移)
        // 返回构建出的快照, 让调用方能直接用于上报, 避免回头读全局单例
        private CsrPostMatchData BuildPostMatchData(MatchSession session, bool iWon, string reason, SettlementDetail detail, bool wasPlacement, bool isVoid, bool selfSummited)
        {
            CsrPostMatchData data = new CsrPostMatchData
            {
                IWon = iWon,
                IsVoid = isVoid,
                // 只有真的登顶完赛才算有效成绩: PB 采集与云端 duration_ms 都依赖这一位
                SelfSummited = selfSummited,
                IsRatedMatch = session.IsRatedMatch,
                Reason = reason,
                OpponentName = session.OpponentName,
                OpponentElo = session.OpponentElo,
                InviteCode = session.InviteCode,
                LevelIndex = session.LevelIndex,
                Seed = session.Seed,
                Ascent = session.Ascent,
                // 冲刺局的群系归属要靠路线号反查, 随快照一起带走
                Route = session.Route,
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
                CreatedAt = Time.realtimeSinceStartup,
                CreatedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            data.BiomeLabel = _gw.GetBiomeLabel(session.LevelIndex) ?? "";

            // 分段数据: 本地走篝火事件, 对手走索引投影, 统一成"段名 + 累计用时"
            List<SplitEntry> mySplits = new List<SplitEntry>();
            List<SplitEntry> oppSplits = new List<SplitEntry>();
            session.RebuildSplits(mySplits, oppSplits);

            // RANKED 权威数据的总开关: 只有"我方确认完赛且本局未作废"才采纳
            //
            // selfSummited 是最终判据 (已含 iWon 与 SelfPeaked), 而 DNF/作废局必须
            // 一律不采纳: 那两种情况下 RANKED 的 LastRunRecord 可能是上一局的残留,
            // 或者本局根本没跑完, 拿来覆盖会把半途分段伪造成完赛成绩
            bool useRanked = selfSummited && !isVoid;

            // 汇总总用时: RANKED 的 RunRecord.TotalMs 是唯一权威
            long rankedMs = useRanked ? _gw.AuthoritativeRunMs(session.Seed, session.LevelIndex) : 0L;
            if (rankedMs > 0)
            {
                data.MyTotalMs = rankedMs;
            }
            else if (session.SelfPeakMs > 0)
            {
                data.MyTotalMs = session.SelfPeakMs;
            }
            else if (mySplits.Count > 0)
            {
                data.MyTotalMs = mySplits[mySplits.Count - 1].ElapsedMs;
            }
            if (session.OppPeakMs > 0)
            {
                data.OppTotalMs = session.OppPeakMs;
            }
            else if (oppSplits.Count > 0)
            {
                data.OppTotalMs = oppSplits[oppSplits.Count - 1].ElapsedMs;
            }

            // 构建段对照表: 以段模型为准, 双方按"绝对进度点索引"对齐
            List<BiomeSegment> segments = new List<BiomeSegment>();
            try { segments = session.BuildRouteSegments(); }
            catch { }

            // RANKED 权威分段: 段名 -> 累计用时
            // 只作为覆盖源, 段名对不上就完全不影响原有逻辑; useRanked 为假时整块跳过
            Dictionary<string, long> rankedByKey = new Dictionary<string, long>();
            try
            {
                List<RankedSplitRaw> rankedSplits = useRanked
                    ? _gw.AuthoritativeRunSplits(session.Seed, session.LevelIndex)
                    : null;
                if (rankedSplits != null)
                {
                    for (int i = 0; i < rankedSplits.Count; i++)
                    {
                        string key = BiomeSegmenter.NormalizeTitle(rankedSplits[i].SegmentName);
                        if (string.IsNullOrEmpty(key)) continue;
                        long ms = rankedSplits[i].ElapsedMs;
                        if (ms <= 0) continue;
                        // 同名段只认最后一次出现 (完整链路以完成时刻为准)
                        rankedByKey[key] = ms;
                    }
                }
            }
            catch { }

            // 段终点索引 -> 各自 SplitEntry
            Dictionary<int, SplitEntry> myByIndex = new Dictionary<int, SplitEntry>();
            for (int i = 0; i < mySplits.Count; i++) myByIndex[mySplits[i].AbsoluteIndex] = mySplits[i];
            Dictionary<int, SplitEntry> oppByIndex = new Dictionary<int, SplitEntry>();
            for (int i = 0; i < oppSplits.Count; i++) oppByIndex[oppSplits[i].AbsoluteIndex] = oppSplits[i];

            // 段权重前缀和: 把 0..1 的全局进度反算回"该段内爬了多少"
            // 单群系只有 1 段, 反算结果就等于全局进度
            float totalSpan = 0f;
            float[] spanPrefix = new float[segments.Count + 1];
            for (int i = 0; i < segments.Count; i++)
            {
                totalSpan += BiomeSegmenter.Span(segments[i]);
                spanPrefix[i + 1] = totalSpan;
            }

            int count = segments.Count;
            if (count == 0) count = Mathf.Max(mySplits.Count, oppSplits.Count);

            // 单段用时
            long myPrev = 0L;
            long oppPrev = 0L;
            for (int i = 0; i < count; i++)
            {
                CsrSplitRow row = new CsrSplitRow();
                // 段模型里的 keyIndex 是该段最后一个进度点索引
                int keyIndex = i;
                if (i < segments.Count && !string.IsNullOrEmpty(segments[i].Key))
                {
                    // 显示用中文名, 匹配用英文 key (best time 字典仍按英文 key 存)
                    row.Label = BiomeSegmenter.LocalizeKey(segments[i].Key);
                    row.Key = segments[i].Key;
                    row.IsFinal = segments[i].IsFinal;
                    keyIndex = segments[i].LastPointIdx;
                }
                else if (i < mySplits.Count && !string.IsNullOrEmpty(mySplits[i].Key))
                {
                    row.Label = BiomeSegmenter.LocalizeKey(mySplits[i].Key);
                    row.Key = mySplits[i].Key;
                    keyIndex = mySplits[i].AbsoluteIndex;
                }
                else if (i < oppSplits.Count && !string.IsNullOrEmpty(oppSplits[i].Key))
                {
                    row.Label = BiomeSegmenter.LocalizeKey(oppSplits[i].Key);
                    row.Key = oppSplits[i].Key;
                    keyIndex = oppSplits[i].AbsoluteIndex;
                }
                else
                {
                    row.Label = "SEG " + (i + 1);
                }

                SplitEntry mine;
                if (myByIndex.TryGetValue(keyIndex, out mine))
                {
                    row.MyMs = mine.ElapsedMs;
                    row.MyEstimated = mine.Estimated;
                    row.MyReached = true;
                }
                SplitEntry theirs;
                if (oppByIndex.TryGetValue(keyIndex, out theirs))
                {
                    row.OppMs = theirs.ElapsedMs;
                    row.OppEstimated = theirs.Estimated;
                    row.OppReached = true;
                }

                // RANKED 权威分段覆盖: 段名与进度点 title 同源, 命中即用它的累计用时
                // 只覆盖"已到达"的段; 单段耗时仍走下面的通用相减逻辑, 不在这里算
                long rankedSegMs;
                if (rankedByKey.TryGetValue(row.Key ?? "", out rankedSegMs) && rankedSegMs > 0)
                {
                    row.MyMs = rankedSegMs;
                    row.MyReached = true;
                    row.MyEstimated = false;
                }

                // 终段权威覆盖: 完赛用时就是这一段终点的定义, 无条件覆盖
                // 我方优先用 RANKED 的 TotalMs (与排行榜同源), 退回 SelfPeakMs
                if (row.IsFinal)
                {
                    long myFinalMs = rankedMs > 0 ? rankedMs : session.SelfPeakMs;
                    if (myFinalMs > 0)
                    {
                        row.MyMs = myFinalMs;
                        row.MyReached = true;
                        row.MyEstimated = false;
                    }
                    if (session.OppPeakMs > 0)
                    {
                        row.OppMs = session.OppPeakMs;
                        row.OppReached = true;
                        row.OppEstimated = false;
                    }
                }

                // 单段用时: 本段累计减上一段累计。
                // 相减 <= 0 说明这一段没有独立打卡 (多段共用同一时刻), 只能标"未记录",
                // 绝不能退回成本段累计值 —— 那样会把总用时伪造成最后一程的单段耗时。
                if (row.MyReached)
                {
                    long seg = row.MyMs - myPrev;
                    row.MySegMs = seg > 0 ? seg : 0L;
                    myPrev = row.MyMs;
                }
                else
                {
                    row.MySegMs = 0L;
                }
                if (row.OppReached)
                {
                    long seg = row.OppMs - oppPrev;
                    row.OppSegMs = seg > 0 ? seg : 0L;
                    oppPrev = row.OppMs;
                }
                else
                {
                    row.OppSegMs = 0L;
                }

                // 未到达时补算段内爬升比例 (权重前缀和法, 单段时退化为全局进度)
                if (i < segments.Count && totalSpan > 0.01f)
                {
                    float spanHere = BiomeSegmenter.Span(segments[i]);
                    float accBefore = spanPrefix[i];
                    if (!row.MyReached)
                    {
                        row.MyFraction = Mathf.Clamp01((data.MyProgress * totalSpan - accBefore) / spanHere);
                    }
                    if (!row.OppReached)
                    {
                        row.OppFraction = Mathf.Clamp01((data.OppProgress * totalSpan - accBefore) / spanHere);
                    }
                }

                data.Splits.Add(row);
            }

            PostMatchReportView.LastPostMatch = data;
            return data;
        }

        // PB 采集与本地战报归档 (单独的副作用入口)
        private static void PersistPostMatch(CsrPostMatchData data)
        {
            if (data == null) return;

            try
            {
                PersonalBestService pbService = PersonalBestService.Instance;
                if (pbService != null) pbService.ObservePostMatch(data);
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] PB 采集失败 (不影响结算): " + ex.Message);
            }

            try
            {
                LocalReportArchive archive = LocalReportArchive.Instance;
                if (archive != null) archive.Append(data);
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 战报归档失败 (不影响结算): " + ex.Message);
            }
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
