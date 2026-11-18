using System;
using RankedPeak.Services;
using RankedPeak.Ui.Menu;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 复刻 RANKED 原生窗口观感 (蓝白标题栏 + 分区 Box + pill 按钮 + 点线分隔)
    public class PcsrMenuWindow : MonoBehaviour
    {
        private const float WindowWidth = 1280f;
        private const float WindowHeight = 820f;
        private const float TitleBarHeight = 82f;
        private const float BoxPad = 24f;
        private const float RefreshInterval = 0.2f;

        private static PcsrMenuWindow _instance;
        public static PcsrMenuWindow Instance { get { return _instance; } }

        private RectTransform _root;
        private PeakWindowStyle.WindowParts _window;
        private RectTransform _body;
        private bool _built;
        private bool _open;
        private float _nextRefresh;

        // 赛制与设置
        private Button _btnStandard;
        private Button _btnHardcore;
        private Button _btnSprint;
        private Button _btnAscentMinus;
        private Button _btnAscentPlus;
        private TextMeshProUGUI _lblAscent;
        private TextMeshProUGUI _lblMultiplier;
        private Button _btnRated;
        private TMP_InputField _inputInvite;

        // 对局状态
        private TextMeshProUGUI _lblStatus;
        private TextMeshProUGUI _lblRoom;
        private TextMeshProUGUI _lblOpponent;
        private TextMeshProUGUI _lblProgress;
        private TextMeshProUGUI _lblVitals;
        private Button _btnAction;
        private Button _btnReroll;
        private Button _btnForfeit;

        // 全服天梯
        private TextMeshProUGUI _lblBoardMeta;
        private Button _btnRefreshBoard;
        private readonly TextMeshProUGUI[] _rows = new TextMeshProUGUI[8];

        public bool IsOpen { get { return _open; } }

        private void Awake()
        {
            _instance = this;
            MatchController.OnStateChanged += OnMatchStateChanged;
            CsrLoc.OnLanguageChanged += OnLanguageChanged;
        }

        private void OnDestroy()
        {
            MatchController.OnStateChanged -= OnMatchStateChanged;
            CsrLoc.OnLanguageChanged -= OnLanguageChanged;
        }

        // 一旦真正进入跑图阶段就收起窗口, 让原生 HUD 完整可见
        private void OnMatchStateChanged()
        {
            if (!_open) return;
            MatchController mc = MatchController.Instance;
            if (mc == null) return;
            if (mc.State == CsMatchState.LoadingLevel
                || mc.State == CsMatchState.Countdown
                || mc.State == CsMatchState.Running)
            {
                Close();
            }
        }

        // 语言切换后重建界面文案 (控件引用需要重新抓取)
        private void OnLanguageChanged()
        {
            if (!_built) return;
            bool wasOpen = _open;
            if (_root != null)
            {
                _root.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(_root.gameObject);
            }
            _root = null;
            _built = false;
            _open = false;
            if (wasOpen) Open();
        }

        public static void ShowWindow()
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("PeakCSR_MenuWindow");
                DontDestroyOnLoad(go);
                go.AddComponent<PcsrMenuWindow>();
            }
            _instance.Open();
        }

        public void Open()
        {
            EnsureEventSystem();
            NativeUiFactory.GetCanvas();

            // Canvas 重建后 _root 会变成已销毁引用, 需要重新构建
            if (_built && _root == null)
            {
                _built = false;
            }
            if (!_built) BuildUi();
            if (_root == null) return;

            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            _open = true;
            NativeUiFactory.EnsureCanvasAlive();
            NativeUiFactory.EnsureTopMost();
            RefreshAll();
        }

        public void Close()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            _open = false;
        }

        public void Toggle()
        {
            if (_open) Close();
            else Open();
        }

        private void Update()
        {
            if (!_open) return;
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                Close();
                return;
            }
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + RefreshInterval;
            RefreshDynamic();
        }

        // PEAK 启用了新 Input System, 直接挂 StandaloneInputModule 会抛异常;
        // 优先复用游戏自带 EventSystem, 实在没有则按可用模块类型反射创建
        private static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;

            GameObject go = new GameObject("PeakCSR_EventSystem");
            DontDestroyOnLoad(go);
            go.AddComponent<UnityEngine.EventSystems.EventSystem>();

            Type moduleType = Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (moduleType != null)
            {
                try
                {
                    go.AddComponent(moduleType);
                    return;
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning("[PeakCSR] 挂载 InputSystemUIInputModule 失败: " + ex.Message);
                }
            }

            try
            {
                go.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 无可用输入模块, 窗口可能无法点击: " + ex.Message);
            }
        }

        // 1. 窗口骨架
        private void BuildUi()
        {
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return;

            GameObject rootGo = new GameObject("PcsrMenuRoot", typeof(RectTransform));
            rootGo.transform.SetParent(canvas.transform, false);
            _root = (RectTransform)rootGo.transform;
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;

            PeakWindowStyle.CreateBackdrop(_root, "Backdrop");

            _window = PeakWindowStyle.CreateWindow(
                _root, "PcsrWindow", WindowWidth, WindowHeight, TitleBarHeight,
                "PEAK CSR", CsrLoc.T("win.subtitle"));
            _body = _window.Body;

            BuildTitleBar();
            BuildRulesBox();
            BuildMatchBox();
            BuildBoardBox();

            _built = true;
            _root.gameObject.SetActive(false);
        }

        private void BuildTitleBar()
        {
            Button close = PeakWindowStyle.CreatePill(
                _window.TitleBar, "BtnClose", "X",
                WindowWidth - 14f - 72f, 20f, 48f, 42f, 24f);
            TintOnBar(close);
            close.onClick.AddListener(new UnityAction(Close));

            Button help = PeakWindowStyle.CreatePill(
                _window.TitleBar, "BtnHelp", CsrLoc.T("win.btn.help"),
                WindowWidth - 14f - 72f - 12f - 178f, 20f, 178f, 42f, 19f);
            TintOnBar(help);
            help.onClick.AddListener(new UnityAction(OnHelpClicked));
        }

        private static void TintOnBar(Button button)
        {
            Image img = button.GetComponent<Image>();
            if (img != null) img.color = new Color(1f, 1f, 1f, 0.22f);
        }

        // 2. 左: 赛制与设置
        private void BuildRulesBox()
        {
            RectTransform box = PeakWindowStyle.CreateBox(
                _body, "BoxRules", CsrLoc.T("win.box.ruleset"), 26f, 8f, 560f, 352f);
            float inner = 560f - BoxPad * 2f;

            CreateText(box, "LblFormat", CsrLoc.T("win.lbl.format"), 20f, PeakWindowStyle.Heading, 24f, 58f, inner, 26f);

            _btnStandard = PeakWindowStyle.CreatePill(box, "BtnStandard", CsrLoc.T("hud.format.standard"), 24f, 88f, 160f, 38f, 18f);
            _btnStandard.onClick.AddListener(new UnityAction(OnStandardClicked));
            _btnHardcore = PeakWindowStyle.CreatePill(box, "BtnHardcore", CsrLoc.T("hud.format.hardcore"), 192f, 88f, 176f, 38f, 18f);
            _btnHardcore.onClick.AddListener(new UnityAction(OnHardcoreClicked));
            _btnSprint = PeakWindowStyle.CreatePill(box, "BtnSprint", CsrLoc.T("hud.format.sprint"), 376f, 88f, 160f, 38f, 18f);
            _btnSprint.onClick.AddListener(new UnityAction(OnSprintClicked));

            CreateText(box, "LblAscentCaption", CsrLoc.T("win.lbl.ascent"), 20f, PeakWindowStyle.Heading, 24f, 138f, inner, 26f);

            _btnAscentMinus = PeakWindowStyle.CreatePill(box, "BtnAscentMinus", "-", 24f, 168f, 40f, 38f, 22f);
            _btnAscentMinus.onClick.AddListener(new UnityAction(OnAscentMinus));
            _lblAscent = CreateText(box, "LblAscent", "A4", 20f, PeakWindowStyle.TextPrimary, 68f, 168f, 180f, 38f);
            _btnAscentPlus = PeakWindowStyle.CreatePill(box, "BtnAscentPlus", "+", 252f, 168f, 40f, 38f, 22f);
            _btnAscentPlus.onClick.AddListener(new UnityAction(OnAscentPlus));
            _lblMultiplier = CreateText(box, "LblMult", "", 19f, PeakWindowStyle.TextMuted, 300f, 168f, 236f, 38f);

            _btnRated = PeakWindowStyle.CreatePill(box, "BtnRated", CsrLoc.T("hud.invite.rated"), 24f, 218f, 210f, 38f, 18f);
            _btnRated.onClick.AddListener(new UnityAction(OnRatedToggled));

            CreateText(box, "LblInvite", CsrLoc.T("win.lbl.invite"), 20f, PeakWindowStyle.Heading, 24f, 268f, inner, 26f);
            _inputInvite = PeakWindowStyle.CreateInputField(box, "InputInvite", "", 24f, 296f, 196f, 38f, 19f);
            if (_inputInvite != null) _inputInvite.characterLimit = 12;

            Button join = PeakWindowStyle.CreatePill(box, "BtnJoin", CsrLoc.T("hud.invite.join"), 228f, 296f, 130f, 38f, 18f);
            join.onClick.AddListener(new UnityAction(OnJoinClicked));
            Button host = PeakWindowStyle.CreatePill(box, "BtnHost", CsrLoc.T("hud.invite.host"), 366f, 296f, 170f, 38f, 18f);
            host.onClick.AddListener(new UnityAction(OnHostClicked));
        }

        // 3. 右: 对局状态
        private void BuildMatchBox()
        {
            RectTransform box = PeakWindowStyle.CreateBox(
                _body, "BoxMatch", CsrLoc.T("win.box.match"), 604f, 8f, 650f, 352f);
            float inner = 650f - BoxPad * 2f;

            _lblStatus = CreateText(box, "LblStatus", "", 21f, PeakWindowStyle.TextPrimary, 24f, 56f, inner, 54f);
            _lblRoom = CreateText(box, "LblRoom", "", 19f, PeakWindowStyle.TextMuted, 24f, 116f, inner, 26f);
            _lblOpponent = CreateText(box, "LblOpponent", "", 19f, PeakWindowStyle.TextMuted, 24f, 146f, inner, 26f);
            _lblProgress = CreateText(box, "LblProgress", "", 19f, PeakWindowStyle.TextPrimary, 24f, 178f, inner, 26f);
            _lblVitals = CreateText(box, "LblVitals", "", 19f, PeakWindowStyle.TextMuted, 24f, 208f, inner, 26f);

            _btnAction = PeakWindowStyle.CreatePill(box, "BtnAction", CsrLoc.T("hud.btn.queue"), 24f, 252f, 210f, 40f, 19f);
            _btnAction.onClick.AddListener(new UnityAction(OnActionClicked));
            _btnReroll = PeakWindowStyle.CreatePill(box, "BtnReroll", CsrLoc.T("hud.btn.reroll"), 242f, 252f, 196f, 40f, 18f);
            _btnReroll.onClick.AddListener(new UnityAction(OnRerollClicked));
            _btnForfeit = PeakWindowStyle.CreatePill(box, "BtnForfeit", CsrLoc.T("hud.btn.forfeit"), 446f, 252f, 180f, 40f, 18f);
            _btnForfeit.onClick.AddListener(new UnityAction(OnForfeitClicked));
        }

        // 4. 下: 全服天梯
        private void BuildBoardBox()
        {
            RectTransform box = PeakWindowStyle.CreateBox(
                _body, "BoxBoard", CsrLoc.T("win.box.board"), 26f, 378f, 1228f, 320f);
            float inner = 1228f - BoxPad * 2f;

            _lblBoardMeta = CreateText(box, "LblMeta", "", 19f, PeakWindowStyle.TextMuted, 24f, 56f, inner - 200f, 24f);
            _btnRefreshBoard = PeakWindowStyle.CreatePill(box, "BtnRefreshBoard", CsrLoc.T("hud.btn.refresh_board"), 1228f - BoxPad - 196f, 50f, 172f, 34f, 17f);
            _btnRefreshBoard.onClick.AddListener(new UnityAction(OnRefreshBoardClicked));

            PeakWindowStyle.CreateSeparator(box, "BoardSep", 24f, 84f, inner);

            for (int i = 0; i < _rows.Length; i++)
            {
                _rows[i] = CreateText(box, "Row" + i, "", 20f, PeakWindowStyle.TextPrimary, 24f, 92f + i * 25f, inner, 24f);
            }
        }

        private static TextMeshProUGUI CreateText(
            Transform parent, string name, string text, float size, Color color,
            float x, float y, float w, float h)
        {
            TextMeshProUGUI label = PeakWindowStyle.CreateText(
                parent, name, text, size, color, TextAlignmentOptions.Left);
            NativeUiFactory.Place((RectTransform)label.transform, x, y, w, h);
            return label;
        }

        // ===== 交互 =====

        private static MatchController Mc { get { return MatchController.Instance; } }

        private void OnStandardClicked()
        {
            if (Mc != null) Mc.SelectedFormat = McsrRuleFormat.StandardAllPeak;
            RefreshAll();
        }

        private void OnHardcoreClicked()
        {
            if (Mc != null) Mc.SelectedFormat = McsrRuleFormat.HardcoreAscent4;
            RefreshAll();
        }

        private void OnSprintClicked()
        {
            if (Mc != null) Mc.SelectedFormat = McsrRuleFormat.BiomeSprint;
            RefreshAll();
        }

        private void OnAscentMinus()
        {
            if (Mc != null) Mc.SelectedAscent = Mc.SelectedAscent - 1;
            RefreshAll();
        }

        private void OnAscentPlus()
        {
            if (Mc != null) Mc.SelectedAscent = Mc.SelectedAscent + 1;
            RefreshAll();
        }

        private void OnRatedToggled()
        {
            if (Mc != null) Mc.IsRatedMatch = !Mc.IsRatedMatch;
            RefreshAll();
        }

        private void OnJoinClicked()
        {
            if (Mc != null && _inputInvite != null)
            {
                Mc.JoinByInviteCode(_inputInvite.text);
            }
            RefreshDynamic();
        }

        private void OnHostClicked()
        {
            if (Mc != null) Mc.CreatePrivate1v1Room();
            RefreshDynamic();
        }

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
            RefreshAll();
        }

        private void OnRerollClicked()
        {
            if (Mc == null) return;
            if (Mc.OppRerollVoteActive)
            {
                Mc.RespondRerollVote(true);
            }
            else
            {
                Mc.InitiateRerollVote();
            }
            RefreshAll();
        }

        private void OnForfeitClicked()
        {
            if (Mc == null) return;
            // 二次确认交给统一弹窗, 避免误触直接判负
            PeakCsrPrompt.Confirm(CsrLoc.T("win.prompt.forfeit"), () =>
            {
                if (Mc != null) Mc.ForfeitMatch();
                RefreshAll();
            });
        }

        private void OnRefreshBoardClicked()
        {
            if (CloudLeaderboardService.Instance != null)
            {
                CloudLeaderboardService.Instance.RefreshLeaderboard();
            }
            RefreshDynamic();
        }

        private void OnHelpClicked()
        {
            PeakCsrPrompt.Info(CsrLoc.T("win.help.title"), CsrLoc.T("win.help.body"));
        }

        // ===== 刷新 =====

        private void RefreshAll()
        {
            RefreshSelection();
            RefreshDynamic();
        }

        private void RefreshSelection()
        {
            if (Mc == null) return;

            PeakWindowStyle.SetPillSelected(_btnStandard, Mc.SelectedFormat == McsrRuleFormat.StandardAllPeak);
            PeakWindowStyle.SetPillSelected(_btnHardcore, Mc.SelectedFormat == McsrRuleFormat.HardcoreAscent4);
            PeakWindowStyle.SetPillSelected(_btnSprint, Mc.SelectedFormat == McsrRuleFormat.BiomeSprint);
            PeakWindowStyle.SetPillSelected(_btnRated, Mc.IsRatedMatch);

            bool showAscent = Mc.SelectedFormat != McsrRuleFormat.StandardAllPeak;
            SetActive(_btnAscentMinus, showAscent);
            SetActive(_btnAscentPlus, showAscent);
            SetActive(_lblAscent, showAscent);
            SetActive(_lblMultiplier, true);

            if (_lblAscent != null)
            {
                _lblAscent.text = CsrLoc.T("win.lbl.ascent_value", Mc.SelectedAscent);
            }
            if (_lblMultiplier != null)
            {
                double mult = EloCalculator.GetModeMultiplier(Mc.SelectedFormat, Mc.SelectedAscent);
                _lblMultiplier.text = CsrLoc.T("win.lbl.mult", mult.ToString("F2"));
            }

            // 赛制在天阶选择区里保留可读性
            PeakWindowStyle.SetPillText(_btnRated,
                Mc.IsRatedMatch ? ("● " + CsrLoc.T("hud.invite.rated")) : ("○ " + CsrLoc.T("hud.invite.unrated")));
        }

        private void RefreshDynamic()
        {
            if (Mc == null)
            {
                if (_lblStatus != null) _lblStatus.text = CsrLoc.T("status.not_ready");
                return;
            }

            if (_lblStatus != null) _lblStatus.text = CsrLoc.T("hud.status", Mc.StatusMessage);

            string biome = MapCatalog.GetBiomeLabel(Mc.LevelIndex);
            bool revealSeed = Mc.State == CsMatchState.Running || Mc.State == CsMatchState.Finished;
            if (_lblRoom != null)
            {
                _lblRoom.text = string.IsNullOrEmpty(Mc.InviteCode)
                    ? CsrLoc.T("win.room.none")
                    : CsrLoc.T("win.room.info", Mc.InviteCode, Mc.LevelIndex, biome, Mc.Ascent,
                        revealSeed ? Mc.Seed.ToString() : CsrLoc.T("hud.seed.hidden"));
            }

            if (_lblOpponent != null)
            {
                string verify = Mc.OpponentVerified ? CsrLoc.T("hud.verified.ok") : CsrLoc.T("hud.verified.wait");
                _lblOpponent.text = CsrLoc.T("hud.opponent", Mc.OpponentName, Mc.OpponentElo, verify);
            }

            if (_lblProgress != null)
            {
                float mine = Mc.SelfProgress * 100f;
                float theirs = Mc.OpponentProgress * 100f;
                float diff = mine - theirs;
                string color = diff >= 0f ? "55E673" : "FF6666";
                string sign = diff >= 0f ? "+" : "";
                _lblProgress.text = CsrLoc.T("hud.progress",
                    mine.ToString("F1"), theirs.ToString("F1"), color, sign, diff.ToString("F1"));
            }

            if (_lblVitals != null)
            {
                _lblVitals.text = Mc.State == CsMatchState.Running
                    ? CsrLoc.T("hud.vitals",
                        Mathf.RoundToInt(Mc.OpponentHealth * 100f),
                        Mathf.RoundToInt(Mc.OpponentStamina * 100f),
                        Mc.OpponentDown ? CsrLoc.T("hud.state.down") : CsrLoc.T("hud.state.climbing"))
                    : "";
            }

            RefreshActionButtons();
            RefreshBoard();
        }

        private void RefreshActionButtons()
        {
            string actionText;
            bool actionInteractable = true;
            switch (Mc.State)
            {
                case CsMatchState.Idle:
                    actionText = CsrLoc.T("hud.btn.queue");
                    break;
                case CsMatchState.SearchingLobby:
                case CsMatchState.WaitingOpponent:
                    actionText = CsrLoc.T("hud.btn.cancel_queue");
                    break;
                case CsMatchState.Finished:
                    actionText = CsrLoc.T("hud.btn.queue_next");
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

            bool inMatch = Mc.State == CsMatchState.LoadingLevel
                || Mc.State == CsMatchState.Countdown
                || Mc.State == CsMatchState.Running;

            if (_btnReroll != null)
            {
                _btnReroll.gameObject.SetActive(inMatch || Mc.State == CsMatchState.WaitingOpponent);
                bool canReroll = !Mc.MyRerollVoteActive;
                _btnReroll.interactable = canReroll;
                if (Mc.OppRerollVoteActive)
                {
                    PeakWindowStyle.SetPillText(_btnReroll, CsrLoc.T("hud.btn.reroll_accept"));
                }
                else if (Mc.MyRerollVoteActive)
                {
                    PeakWindowStyle.SetPillText(_btnReroll, CsrLoc.T("hud.btn.reroll_waiting", Mc.RerollVoteRemainingSeconds));
                }
                else
                {
                    PeakWindowStyle.SetPillText(_btnReroll, CsrLoc.T("hud.btn.reroll"));
                }
            }

            if (_btnForfeit != null)
            {
                _btnForfeit.gameObject.SetActive(inMatch);
            }
        }

        private void RefreshBoard()
        {
            CloudLeaderboardService svc = CloudLeaderboardService.Instance;
            if (_lblBoardMeta != null)
            {
                int count = svc != null && svc.Entries != null ? svc.Entries.Count : 0;
                _lblBoardMeta.text = svc != null && !string.IsNullOrEmpty(svc.CloudStatus)
                    ? svc.CloudStatus
                    : CsrLoc.T("cloud.updated", count);
            }

            for (int i = 0; i < _rows.Length; i++)
            {
                if (_rows[i] == null) continue;
                if (svc == null || svc.Entries == null || i >= svc.Entries.Count)
                {
                    _rows[i].text = i == 0 && (svc == null || svc.Entries == null || svc.Entries.Count == 0)
                        ? "<color=#8A93A5>" + CsrLoc.T("win.board.empty") + "</color>"
                        : string.Empty;
                    continue;
                }

                CloudRankEntry e = svc.Entries[i];
                string tierHex = ColorUtility.ToHtmlStringRGB(RankedMenuIcons.GetTierColorOnLight(EloCalculator.GetVanillaTierKey(e.Elo)));
                _rows[i].text = "<b>#" + e.Rank + "</b>   <color=#" + tierHex + ">" + e.PlayerName + "</color>   —   <b>"
                    + e.Elo + " ELO</b>   (" + e.Wins + "W / " + e.Losses + "L)";
            }
        }

        private static void SetActive(Component comp, bool active)
        {
            if (comp != null && comp.gameObject.activeSelf != active)
            {
                comp.gameObject.SetActive(active);
            }
        }
    }

    // 极简原生风格提示框, 供 PCSR 窗口内部复用
    public static class PeakCsrPrompt
    {
        private static GameObject _root;

        public static void Confirm(string message, Action onConfirm)
        {
            Show("PEAK CSR", message, new[]
            {
                new Option(CsrLoc.T("win.btn.confirm"), onConfirm),
                new Option(CsrLoc.T("win.btn.cancel"), null)
            });
        }

        public static void Info(string title, string message)
        {
            Show(title, message, new[] { new Option(CsrLoc.T("win.btn.ok"), null) });
        }

        private struct Option
        {
            public string Label;
            public Action Callback;
            public Option(string label, Action callback)
            {
                Label = label;
                Callback = callback;
            }
        }

        private static void Show(string title, string message, Option[] options)
        {
            Destroy();
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return;

            _root = new GameObject("PcsrPromptRoot", typeof(RectTransform));
            _root.transform.SetParent(canvas.transform, false);
            RectTransform rootRt = (RectTransform)_root.transform;
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            PeakWindowStyle.CreateBackdrop(rootRt, "Backdrop");

            const float w = 720f;
            const float h = 300f;
            PeakWindowStyle.WindowParts win = PeakWindowStyle.CreateWindow(
                rootRt, "PcsrPrompt", w, h, 74f, title, "");
            PeakWindowStyle.CreateScrollingText(
                win.Body, "Body", message, 21f, PeakWindowStyle.TextPrimary,
                26f, 16f, w - 52f, h - 74f - 90f, 2f, 5f);

            float btnW = 170f;
            float totalW = options.Length * btnW + (options.Length - 1) * 14f;
            float startX = (w - totalW) / 2f;
            for (int i = 0; i < options.Length; i++)
            {
                Option opt = options[i];
                Button btn = PeakWindowStyle.CreatePill(
                    win.Body, "PromptBtn" + i, opt.Label,
                    startX + i * (btnW + 14f), h - 74f - 74f, btnW, 44f, 19f);
                if (opt.Callback != null)
                {
                    Action cb = opt.Callback;
                    btn.onClick.AddListener(new UnityAction(() =>
                    {
                        Destroy();
                        cb();
                    }));
                }
                else
                {
                    btn.onClick.AddListener(new UnityAction(Destroy));
                }
            }
        }

        private static void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
        }
    }
}
