using System.Collections.Generic;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 右上分区: 当前状态、房间/对手信息、进度对比、投票/投降按钮 (原 BuildMatchBox + 刷新收口)
    public sealed class MatchStatusPanel
    {
        private const float BoxPad = 24f;

        private readonly PcsrMenuWindow _host;
        public readonly RectTransform Box;

        private TextMeshProUGUI _lblStatus;
        private TextMeshProUGUI _lblEnv;
        private TextMeshProUGUI _lblRoom;
        private TextMeshProUGUI _lblOpponent;
        private TextMeshProUGUI _lblProgress;
        private TextMeshProUGUI _lblVitals;
        private Button _btnAction;
        private Button _btnReroll;
        private Button _btnRerollAccept;
        private Button _btnRerollDecline;
        private Button _btnForfeit;

        public MatchStatusPanel(PcsrMenuWindow host, RectTransform body, float x, float y, float w, float h)
        {
            _host = host;
            float inner = w - BoxPad * 2f;
            Box = PeakWindowStyle.CreateBox(body, "BoxMatch", CsrLoc.T("win.box.match"), x, y, w, h);
            LocalizedTextBinder.BindBoxHeading(Box, "win.box.match");

            _lblStatus = UiKit.CreateText(Box, "LblStatus", "", 19f, PeakWindowStyle.TextPrimary, BoxPad, 54f, inner, 52f, wrap: true);
            _lblEnv = UiKit.CreateText(Box, "LblEnv", "", 15f, PeakWindowStyle.TextMuted, BoxPad, 108f, inner, 22f);
            _lblRoom = UiKit.CreateText(Box, "LblRoom", "", 17f, PeakWindowStyle.TextMuted, BoxPad, 132f, inner, 24f);
            _lblOpponent = UiKit.CreateText(Box, "LblOpponent", "", 17f, PeakWindowStyle.TextMuted, BoxPad, 158f, inner, 24f);
            _lblProgress = UiKit.CreateText(Box, "LblProgress", "", 17f, PeakWindowStyle.TextPrimary, BoxPad, 184f, inner, 24f);
            _lblVitals = UiKit.CreateText(Box, "LblVitals", "", 17f, PeakWindowStyle.TextMuted, BoxPad, 210f, inner, 24f);

            _btnAction = PeakWindowStyle.CreatePill(Box, "BtnAction", CsrLoc.T("hud.btn.queue"), BoxPad, 240f, 300f, 42f, 18f);
            _btnAction.onClick.AddListener(new UnityAction(OnActionClicked));

            _btnReroll = PeakWindowStyle.CreatePill(Box, "BtnReroll", CsrLoc.T("hud.btn.reroll"), BoxPad, 296f, 250f, 40f, 17f);
            _btnReroll.onClick.AddListener(new UnityAction(OnRerollClicked));
            _btnRerollAccept = PeakWindowStyle.CreatePill(Box, "BtnRerollAccept", CsrLoc.T("hud.btn.reroll_accept"), BoxPad, 296f, 250f, 40f, 17f);
            _btnRerollAccept.onClick.AddListener(new UnityAction(OnRerollAcceptClicked));
            _btnRerollDecline = PeakWindowStyle.CreatePill(Box, "BtnRerollDecline", CsrLoc.T("hud.btn.reroll_decline"), BoxPad + 262f, 296f, 180f, 40f, 17f);
            _btnRerollDecline.onClick.AddListener(new UnityAction(OnRerollDeclineClicked));
            _btnForfeit = PeakWindowStyle.CreatePill(Box, "BtnForfeit", CsrLoc.T("hud.btn.forfeit"), BoxPad + 454f, 296f, 128f, 40f, 16f);
            _btnForfeit.onClick.AddListener(new UnityAction(OnForfeitClicked));
            // 换图/投降按钮文字由 RefreshActionButtons 按状态动态改写, 这里只绑初始 key,
            // 语言切换时先恢复默认文案, 随后 0.2s 内动态刷新会按当前状态重写
            LocalizedTextBinder.BindPill(_btnReroll, "hud.btn.reroll");
            LocalizedTextBinder.BindPill(_btnRerollAccept, "hud.btn.reroll_accept");
            LocalizedTextBinder.BindPill(_btnRerollDecline, "hud.btn.reroll_decline");
            LocalizedTextBinder.BindPill(_btnForfeit, "hud.btn.forfeit");
        }

        private static MatchCoordinator Mc { get { return MatchCoordinator.Instance; } }

        private void OnActionClicked()
        {
            if (Mc == null) return;
            switch (Mc.State)
            {
                case CsMatchState.SearchingLobby:
                case CsMatchState.WaitingOpponent:
                    Mc.LeaveMatch();
                    break;
                case CsMatchState.Finished:
                    Mc.LeaveMatch();
                    Mc.Queue1v1();
                    break;
                default:
                    Mc.Queue1v1();
                    break;
            }
            _host.RefreshAll();
        }

        private void OnRerollClicked()
        {
            if (Mc == null) return;
            Mc.InitiateRerollVote();
            _host.RefreshAll();
        }

        private void OnRerollAcceptClicked()
        {
            if (Mc == null) return;
            Mc.RespondRerollVote(true);
            _host.RefreshAll();
        }

        private void OnRerollDeclineClicked()
        {
            if (Mc == null) return;
            Mc.RespondRerollVote(false);
            _host.RefreshAll();
        }

        private void OnForfeitClicked()
        {
            if (Mc == null) return;
            PeakCsrPrompt.Confirm(CsrLoc.T("win.prompt.forfeit"), () =>
            {
                if (Mc != null) Mc.ForfeitMatch();
                _host.RefreshAll();
            });
        }

        public void RefreshStatus(MatchCoordinator mc)
        {
            if (_lblStatus != null)
            {
                _lblStatus.text = mc != null
                    ? CsrLoc.T("hud.status", mc.StatusMessage)
                    : CsrLoc.T("status.not_ready");
            }
            if (_lblEnv != null && mc != null)
            {
                IRankedPeakGateway gw = mc.Gateway;
                _lblEnv.text = CsrLoc.T("win.lbl.env", gw.GameVersion, gw.MapSetHash);
            }
        }

        public void RefreshMatchInfo(MatchCoordinator mc)
        {
            UiKit.Safe(delegate
            {
                if (_lblRoom == null || mc == null) return;
                PlayerProfileBridge profile = mc.Profile;
                int elo = profile.CurrentElo;
                MatchSession s = mc.CurrentSession;
                if (s == null || string.IsNullOrEmpty(s.InviteCode))
                {
                    _lblRoom.text = CsrLoc.T("win.room.none",
                        TierCatalog.GetTierHexColorOnLight(elo),
                        TierCatalog.GetTierName(elo),
                        elo,
                        profile.WinRatePercent);
                }
                else
                {
                    bool seedRevealed = s.State == CsMatchState.Running || s.State == CsMatchState.Finished;
                    _lblRoom.text = CsrLoc.T("win.room.info", s.InviteCode, s.LevelIndex, UiKit.SafeBiomeLabel(s.LevelIndex),
                        MatchNaming.GetAscentShortName(s.Ascent),
                        seedRevealed ? s.Seed.ToString() : CsrLoc.T("hud.seed.hidden"));
                }
            });

            UiKit.Safe(delegate
            {
                if (_lblOpponent == null || mc == null) return;
                MatchSession s = mc.CurrentSession;
                string oppName = s != null ? s.OpponentName : "";
                int oppElo = s != null ? s.OpponentElo : 1000;
                bool verified = s != null && s.OpponentVerified;
                if (string.IsNullOrEmpty(oppName)) oppName = CsrLoc.T("win.opponent.placeholder");
                string verify = verified ? CsrLoc.T("hud.verified.ok") : CsrLoc.T("hud.verified.wait");
                string oppTier = TierCatalog.GetTierName(oppElo);
                // 云端天梯里有这名玩家时优先显示其 PCSR 昵称, 否则用握手包里的 Steam 名
                string oppSteamId = s != null ? s.OpponentSteamId : "";
                LadderSyncService svc = LadderSyncService.Instance;
                if (svc != null && !string.IsNullOrEmpty(oppSteamId))
                {
                    string nick = ResolveNickname(oppSteamId);
                    if (!string.IsNullOrEmpty(nick)) oppName = nick;
                }
                _lblOpponent.text = CsrLoc.T("hud.opponent", oppName, oppElo, verify, oppTier);
            });

            UiKit.Safe(delegate
            {
                if (_lblProgress == null || mc == null) return;
                MatchSession s = mc.CurrentSession;
                float mine = (s != null ? s.SelfProgress : 0f) * 100f;
                float theirs = (s != null ? s.OpponentProgress : 0f) * 100f;
                float diff = mine - theirs;
                string color = diff >= 0f ? PcsrColors.Positive : PcsrColors.Danger;
                string sign = diff >= 0f ? "+" : "";
                _lblProgress.text = CsrLoc.T("hud.progress",
                    mine.ToString("F1"), theirs.ToString("F1"), color, sign, diff.ToString("F1"),
                    mc.Profile.WinRatePercent);
            });

            UiKit.Safe(delegate
            {
                if (_lblVitals == null || mc == null) return;
                MatchSession s = mc.CurrentSession;
                if (s == null)
                {
                    _lblVitals.text = "";
                    return;
                }
                // 作废局的提示优先于生命体征 (结算后该行不再显示对手状态)
                if (s.State == CsMatchState.Finished && s.MatchVoided)
                {
                    _lblVitals.text = CsrLoc.T("hud.void.notice");
                    return;
                }
                if (s.State != CsMatchState.Running)
                {
                    _lblVitals.text = "";
                    return;
                }
                _lblVitals.text = CsrLoc.T("hud.vitals",
                    Mathf.RoundToInt(s.OpponentHealth * 100f),
                    Mathf.RoundToInt(s.OpponentStamina * 100f),
                    s.OpponentDown ? CsrLoc.T("hud.state.down") : CsrLoc.T("hud.state.climbing"));
            });
        }

        // 从云端天梯/档案缓存里找该玩家的 PCSR 昵称, 找不到返回空串
        private static string ResolveNickname(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return "";
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc == null) return "";

            CloudProfile cached = svc.CachedProfile(steamId);
            if (cached != null)
            {
                string nick = RankedPeak.Services.PlayerNameSanitizer.Clean(cached.Nickname);
                if (!string.IsNullOrEmpty(nick)) return nick;
            }

            IReadOnlyList<CloudRankEntry> entries = svc.Entries;
            if (entries != null)
            {
                for (int i = 0; i < entries.Count; i++)
                {
                    CloudRankEntry e = entries[i];
                    if (e != null && e.SteamId == steamId)
                    {
                        string nick = RankedPeak.Services.PlayerNameSanitizer.Clean(e.Nickname);
                        if (!string.IsNullOrEmpty(nick)) return nick;
                        break;
                    }
                }
            }
            return "";
        }

        public void RefreshActionButtons(MatchCoordinator mc)
        {
            string actionText;
            bool actionInteractable = true;
            CsMatchState state = mc != null ? mc.State : CsMatchState.Idle;
            bool matchVoided = mc != null && mc.CurrentSession != null && mc.CurrentSession.MatchVoided;
            switch (state)
            {
                case CsMatchState.Idle:
                    actionText = CsrLoc.T("hud.btn.queue");
                    break;
                case CsMatchState.SearchingLobby:
                case CsMatchState.WaitingOpponent:
                    actionText = CsrLoc.T("hud.btn.cancel_queue");
                    break;
                case CsMatchState.Finished:
                    // 作废局换个说法, 避免"继续匹配下一局"让人误以为这局算过了
                    actionText = CsrLoc.T(matchVoided ? "hud.btn.void_requeue" : "hud.btn.queue_next");
                    break;
                default:
                    actionText = CsrLoc.T("hud.btn.reroll");
                    actionInteractable = false;
                    break;
            }
            if (_btnAction != null)
            {
                PeakWindowStyle.SetPillText(_btnAction, actionText);
                _btnAction.interactable = actionInteractable;
            }

            bool inMatch = state == CsMatchState.LoadingLevel
                || state == CsMatchState.Countdown
                || state == CsMatchState.Running;
            bool showRerollZone = inMatch || state == CsMatchState.WaitingOpponent;

            bool myVoting = mc != null && mc.CurrentSession != null && mc.CurrentSession.MyRerollVoteActive;
            bool oppVoting = mc != null && mc.CurrentSession != null && mc.CurrentSession.OppRerollVoteActive;
            int rerollRemain = mc != null ? Mathf.CeilToInt(mc.RerollVoteRemainingSeconds) : 0;

            // 收到对手换图请求时, 拆成 同意 / 拒绝 双按钮
            bool voting = showRerollZone && oppVoting;
            UiKit.SetActive(_btnReroll, showRerollZone && !voting);
            UiKit.SetActive(_btnRerollAccept, voting);
            UiKit.SetActive(_btnRerollDecline, voting);
            UiKit.SetActive(_btnForfeit, inMatch);

            if (voting)
            {
                if (_btnRerollAccept != null) PeakWindowStyle.SetPillText(_btnRerollAccept, CsrLoc.T("hud.btn.reroll_accept"));
                if (_btnRerollDecline != null) PeakWindowStyle.SetPillText(_btnRerollDecline, CsrLoc.T("hud.btn.reroll_decline"));
            }
            else if (_btnReroll != null)
            {
                if (myVoting)
                {
                    PeakWindowStyle.SetPillText(_btnReroll, CsrLoc.T("hud.btn.reroll_waiting", rerollRemain));
                    _btnReroll.interactable = false;
                }
                else
                {
                    PeakWindowStyle.SetPillText(_btnReroll, CsrLoc.T("hud.btn.reroll"));
                    _btnReroll.interactable = true;
                }
            }
        }
    }
}
