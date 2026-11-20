using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 左上分区: 赛制、天阶难度、计分开关、房间码加入/建房 (原 PcsrMenuWindow.BuildRulesBox + 交互收口)
    public sealed class RulesetPanel
    {
        private const float BoxPad = 24f;

        private readonly PcsrMenuWindow _host;
        public readonly RectTransform Box;

        private Button _btnStandard;
        private Button _btnHardcore;
        private Button _btnSprint;
        private Button _btnAscentMinus;
        private Button _btnAscentPlus;
        private TextMeshProUGUI _lblAscent;
        private TextMeshProUGUI _lblMultiplier;
        private Button _btnRated;
        private TMP_InputField _inputInvite;

        public RulesetPanel(PcsrMenuWindow host, RectTransform body, float x, float y, float w, float h)
        {
            _host = host;
            float inner = w - BoxPad * 2f;
            Box = PeakWindowStyle.CreateBox(body, "BoxRules", CsrLoc.T("win.box.ruleset"), x, y, w, h);
            LocalizedTextBinder.BindBoxHeading(Box, "win.box.ruleset");

            UiKit.CreateText(Box, "LblFormat", CsrLoc.T("win.lbl.format"), 20f, PeakWindowStyle.Heading, BoxPad, 56f, inner, 26f);
            LocalizedTextBinder.Bind(Box.transform.Find("LblFormat").GetComponent<TMP_Text>(), "win.lbl.format");

            float gap = 10f;
            float pillW = (inner - gap * 2f) / 3f;
            float fmtY = 86f;
            _btnStandard = PeakWindowStyle.CreatePill(Box, "BtnStandard", CsrLoc.T("hud.format.standard"), BoxPad, fmtY, pillW, 40f, 17f);
            _btnStandard.onClick.AddListener(new UnityAction(OnStandardClicked));
            _btnHardcore = PeakWindowStyle.CreatePill(Box, "BtnHardcore", CsrLoc.T("hud.format.hardcore"), BoxPad + pillW + gap, fmtY, pillW, 40f, 17f);
            _btnHardcore.onClick.AddListener(new UnityAction(OnHardcoreClicked));
            _btnSprint = PeakWindowStyle.CreatePill(Box, "BtnSprint", CsrLoc.T("hud.format.sprint"), BoxPad + (pillW + gap) * 2f, fmtY, pillW, 40f, 17f);
            _btnSprint.onClick.AddListener(new UnityAction(OnSprintClicked));
            LocalizedTextBinder.BindPill(_btnStandard, "hud.format.standard");
            LocalizedTextBinder.BindPill(_btnHardcore, "hud.format.hardcore");
            LocalizedTextBinder.BindPill(_btnSprint, "hud.format.sprint");

            UiKit.CreateText(Box, "LblAscentCaption", CsrLoc.T("win.lbl.ascent"), 20f, PeakWindowStyle.Heading, BoxPad, 142f, inner, 26f);
            LocalizedTextBinder.Bind(Box.transform.Find("LblAscentCaption").GetComponent<TMP_Text>(), "win.lbl.ascent");

            // + 号右缘与下方"计分赛"按钮右缘(BoxPad + 240)对齐, 视觉上成一条竖线
            _btnAscentMinus = PeakWindowStyle.CreatePill(Box, "BtnAscentMinus", "-", BoxPad, 172f, 44f, 40f, 22f);
            _btnAscentMinus.onClick.AddListener(new UnityAction(OnAscentMinus));
            _lblAscent = UiKit.CreateText(Box, "LblAscent", "", 19f, PeakWindowStyle.TextPrimary, BoxPad + 54f, 172f, 130f, 40f);
            _lblAscent.enableAutoSizing = true;
            _lblAscent.fontSizeMin = 14f;
            _lblAscent.fontSizeMax = 19f;
            _btnAscentPlus = PeakWindowStyle.CreatePill(Box, "BtnAscentPlus", "+", BoxPad + 196f, 172f, 44f, 40f, 22f);
            _btnAscentPlus.onClick.AddListener(new UnityAction(OnAscentPlus));
            _lblMultiplier = UiKit.CreateText(Box, "LblMult", "", 18f, PeakWindowStyle.TextMuted, BoxPad + 252f, 172f, inner - 252f, 40f);

            _btnRated = PeakWindowStyle.CreatePill(Box, "BtnRated", CsrLoc.T("hud.invite.rated"), BoxPad, 228f, 240f, 40f, 18f);
            _btnRated.onClick.AddListener(new UnityAction(OnRatedToggled));
            LocalizedTextBinder.BindPill(_btnRated, "hud.invite.rated");

            UiKit.CreateText(Box, "LblInvite", CsrLoc.T("win.lbl.invite"), 20f, PeakWindowStyle.Heading, BoxPad, 280f, inner, 26f);
            LocalizedTextBinder.Bind(Box.transform.Find("LblInvite").GetComponent<TMP_Text>(), "win.lbl.invite");
            _inputInvite = PeakWindowStyle.CreateInputField(Box, "InputInvite", "", BoxPad, 310f, 200f, 40f, 19f);
            UiKit.ConfigurePlainInput(_inputInvite, 12, true);

            float actY = 310f;
            float actGap = 10f;
            float inputW = 200f;
            float btnEach = (inner - inputW - actGap * 2f) / 2f;
            float actX = BoxPad + inputW + actGap;
            Button join = PeakWindowStyle.CreatePill(Box, "BtnJoin", CsrLoc.T("hud.invite.join"), actX, actY, btnEach, 40f, 17f);
            join.onClick.AddListener(new UnityAction(OnJoinClicked));
            Button hostBtn = PeakWindowStyle.CreatePill(Box, "BtnHost", CsrLoc.T("hud.invite.host"), actX + btnEach + actGap, actY, btnEach, 40f, 17f);
            hostBtn.onClick.AddListener(new UnityAction(OnHostClicked));
            LocalizedTextBinder.BindPill(join, "hud.invite.join");
            LocalizedTextBinder.BindPill(hostBtn, "hud.invite.host");
        }

        private static MatchCoordinator Mc { get { return MatchCoordinator.Instance; } }

        private void OnStandardClicked()
        {
            if (Mc != null) Mc.SelectedFormat = McsrRuleFormat.StandardAllPeak;
            _host.RefreshAll();
        }

        private void OnHardcoreClicked()
        {
            if (Mc != null) Mc.SelectedFormat = McsrRuleFormat.HardcoreAscent4;
            _host.RefreshAll();
        }

        private void OnSprintClicked()
        {
            if (Mc != null) Mc.SelectedFormat = McsrRuleFormat.BiomeSprint;
            _host.RefreshAll();
        }

        private void OnAscentMinus()
        {
            if (Mc != null) Mc.SelectedAscent = Mc.SelectedAscent - 1;
            _host.RefreshAll();
        }

        private void OnAscentPlus()
        {
            if (Mc != null) Mc.SelectedAscent = Mc.SelectedAscent + 1;
            _host.RefreshAll();
        }

        private void OnRatedToggled()
        {
            if (Mc != null) Mc.IsRatedMatch = !Mc.IsRatedMatch;
            _host.RefreshAll();
        }

        private void OnJoinClicked()
        {
            if (Mc != null && _inputInvite != null)
            {
                Mc.JoinByInviteCode(_inputInvite.text);
            }
            _host.RefreshDynamic();
        }

        private void OnHostClicked()
        {
            if (Mc != null) Mc.CreatePrivate1v1Room();
            _host.RefreshDynamic();
        }

        public void RefreshSelection()
        {
            if (Mc == null) return;
            PeakWindowStyle.SetPillSelected(_btnStandard, Mc.SelectedFormat == McsrRuleFormat.StandardAllPeak);
            PeakWindowStyle.SetPillSelected(_btnHardcore, Mc.SelectedFormat == McsrRuleFormat.HardcoreAscent4);
            PeakWindowStyle.SetPillSelected(_btnSprint, Mc.SelectedFormat == McsrRuleFormat.BiomeSprint);
            PeakWindowStyle.SetPillSelected(_btnRated, Mc.IsRatedMatch);

            bool idle = Mc.State == CsMatchState.Idle;
            UiKit.SetActive(_btnAscentMinus, idle);
            UiKit.SetActive(_btnAscentPlus, idle);
            UiKit.SetActive(_lblAscent, true);
            UiKit.SetActive(_lblMultiplier, true);

            if (_lblAscent != null)
            {
                _lblAscent.text = MatchNaming.GetAscentDisplayName(Mc.SelectedAscent);
            }
            if (_lblMultiplier != null)
            {
                double mult = EloEngine.GetModeMultiplier(Mc.SelectedFormat, Mc.SelectedAscent);
                _lblMultiplier.text = CsrLoc.T("win.lbl.mult2", mult.ToString("F2"));
            }

            PeakWindowStyle.SetPillText(_btnRated,
                Mc.IsRatedMatch ? ("● " + CsrLoc.T("hud.invite.rated")) : ("○ " + CsrLoc.T("hud.invite.unrated")));
        }
    }
}
