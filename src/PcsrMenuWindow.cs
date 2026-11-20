using System;
using System.Collections.Generic;
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
    // 布局: 上排=赛制 / 对局状态, 中排=实时队列 / 历史战绩, 下排=全服天梯表格
    public class PcsrMenuWindow : MonoBehaviour
    {
        private const float WindowWidth = 1300f;
        private const float WindowHeight = 1090f;
        private const float TitleBarHeight = 82f;
        private const float BoxPad = 24f;
        private const float RefreshInterval = 0.2f;
        private const float ColGap = 18f;

        // 行高与左右分栏
        private const float LeftX = 26f;
        private const float LeftW = 600f;
        private const float RightX = 26f + 600f + ColGap;
        private const float RightW = 630f;
        private const float Row1Y = 8f;
        private const float Row1H = 380f;
        private const float Row2Y = Row1Y + Row1H + ColGap;
        private const float Row2H = 250f;
        private const float Row3Y = Row2Y + Row2H + ColGap;
        // 表头 30 + 8 行 x 22 + 上下留白 = 约 300
        private const float Row3H = 306f;
        private const float FullW = 1248f;
        private const float BoardRowH = 22f;
        // 天梯表格列坐标 (相对 box 内容区左边缘), 表头与数据行共用
        private const float ColRankX = 0f;
        private const float ColRankW = 52f;
        private const float ColNameX = 52f;
        private const float ColNameW = 268f;
        private const float ColTierX = 320f;
        private const float ColTierW = 176f;
        private const float ColEloX = 496f;
        private const float ColEloW = 110f;
        private const float ColRecordX = 616f;
        private const float ColRecordW = 128f;
        private const float ColModesX = 744f;
        private const float ColModesW = 456f;
        private const float BoardHeaderY = 84f;
        private const float BoardHeaderH = 30f;
        private const float BoardRowStartY = 118f;
        // 队列行三段式: 赛制名 | 人数文案 | 比例条, 给文案留足空间不再压条
        private const float QueueLabelW = 96f;
        private const float QueueCellX = QueueLabelW + 10f;
        private const float QueueCellW = 200f;
        private const float QueueBarX = QueueCellX + QueueCellW + 12f;
        private const float QueueBarW = LeftW - BoxPad * 2f - QueueBarX;
        // 刷新类按钮统一贴在 box 右上角, 与标题同一行
        private const float RefreshBtnY = 12f;
        private const float RefreshBtnH = 36f;

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

        // 实时队列独立面板
        private TextMeshProUGUI _lblQueueHead;
        private TextMeshProUGUI _lblQueueTotal;
        private TextMeshProUGUI _lblQueueStd;
        private TextMeshProUGUI _lblQueueAsc;
        private TextMeshProUGUI _lblQueueSpr;
        private TextMeshProUGUI _lblQueueMine;
        private Image _barQueueStd;
        private Image _barQueueAsc;
        private Image _barQueueSpr;

        // 历史战绩
        private TextMeshProUGUI _lblHistoryMeta;
        private Button _btnHistoryRefresh;
        private readonly TextMeshProUGUI[] _historyRows = new TextMeshProUGUI[6];

        // 全服天梯表格
        private TextMeshProUGUI _lblBoardMeta;
        private Button _btnRefreshBoard;
        private readonly TextMeshProUGUI[] _colRank = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colName = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colTier = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colElo = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colRecord = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colModes = new TextMeshProUGUI[8];
        private readonly Button[] _rowButtons = new Button[8];
        private readonly Image[] _rowStripes = new Image[8];
        private TextMeshProUGUI _lblBoardEmpty;
        private TextMeshProUGUI _lblBoardHeader;
        // 表头整组 (底色条 + 各列表头), 数据为空时整体隐藏
        private readonly List<GameObject> _boardHeaderParts = new List<GameObject>();

        // ===== 赛后战报面板 =====
        private RectTransform _postRoot;
        private PeakWindowStyle.WindowParts _postWindow;
        private TextMeshProUGUI _postTitle;
        private TextMeshProUGUI _postSubtitle;
        private TextMeshProUGUI _postEloBig;
        private TextMeshProUGUI _postEloDetail;
        private TextMeshProUGUI _postSplitsEmpty;
        private readonly TextMeshProUGUI[] _postSplitRows = new TextMeshProUGUI[8];
        private TextMeshProUGUI _postTierLabel;
        private bool _postOpen;
        private CsrPostMatchData _postData;

        // ELO 数字滚动
        private float _eloAnimStart;
        private float _eloAnimFrom;
        private float _eloAnimTo;
        private const float EloAnimSeconds = 1.35f;

        // 双进度条: 灰=赛前, 绿/红=赛后, 都带补间
        private Image _barBackImage;
        private RectTransform _barOldFillRt;
        private RectTransform _barNewFillRt;
        private Image _barNewFillImage;
        private float _barTotalWidth = 1f;
        private float _barAnimStart;
        private float _barFromOld;
        private float _barFromNew;
        private float _barToOld;
        private float _barToNew;
        private bool _barCrossedTier;
        private int _barOldTierFloor;
        private int _barOldTierCeil;
        private int _barNewTierFloor;
        private int _barNewTierCeil;

        // 战报延迟弹出: 对局结束后先挂标记, 等结算界面倒计时结束/回到主菜单才自动弹出
        private static bool _postPending;
        private bool _postAutoShown;
        private bool _sawEndScreen;

        // 玩家档案卡
        private RectTransform _profileRoot;
        private PeakWindowStyle.WindowParts _profileWindow;
        private TextMeshProUGUI _profileTitle;
        private TextMeshProUGUI _profileBody;
        private TextMeshProUGUI _profileModes;
        private TextMeshProUGUI _profileFav;
        private TextMeshProUGUI _profileMeta;
        private TextMeshProUGUI _profileNickLabel;
        private TextMeshProUGUI _profileNickHint;
        private TMP_InputField _profileNickInput;
        private Button _profileNickSave;
        private TextMeshProUGUI _profileBiomeLabel;
        private PcsrDropdown _profileBiomeDropdown;
        private bool _profileOpen;
        private string _profileSteamId = "";
        // 当前档案卡是不是自己 (决定昵称编辑区是否出现)
        private bool _profileIsSelf;
        private bool _profileNickBusy;

        public bool IsOpen { get { return _open; } }
        public bool IsPostOpen { get { return _postOpen; } }

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

        private void OnLanguageChanged()
        {
            if (!_built) return;
            bool wasOpen = _open;
            bool wasPostOpen = _postOpen;
            if (_root != null)
            {
                _root.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(_root.gameObject);
            }
            if (_postRoot != null)
            {
                _postRoot.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(_postRoot.gameObject);
            }
            if (_profileRoot != null)
            {
                _profileRoot.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(_profileRoot.gameObject);
            }
            _root = null;
            _postRoot = null;
            _profileRoot = null;
            _built = false;
            _open = false;
            _postOpen = false;
            _profileOpen = false;
            if (wasOpen) Open();
            if (wasPostOpen && _postData != null) ShowPostMatch(_postData);
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
            // ELO 数字与进度条补间
            if (_postOpen) TickPostMatchAnimation();
            TickPendingPostMatch();

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

        // =========================================================
        // 骨架
        // =========================================================
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
            BuildQueueBox();
            BuildHistoryBox();
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

            // 我的档案: 天梯表格只显示 Top N, 自己不在榜上时也要能进档案页改昵称
            Button profile = PeakWindowStyle.CreatePill(
                _window.TitleBar, "BtnProfile", CsrLoc.T("win.btn.profile"),
                WindowWidth - 14f - 72f - 12f - 178f - 12f - 178f, 20f, 178f, 42f, 19f);
            TintOnBar(profile);
            profile.onClick.AddListener(new UnityAction(OnMyProfileClicked));
        }

        private static void TintOnBar(Button button)
        {
            Image img = button.GetComponent<Image>();
            if (img != null) img.color = new Color(1f, 1f, 1f, 0.22f);
        }

        // =========================================================
        // 左: 赛制与设置
        // =========================================================
        private void BuildRulesBox()
        {
            const float boxW = LeftW;
            const float boxH = Row1H;
            RectTransform box = PeakWindowStyle.CreateBox(
                _body, "BoxRules", CsrLoc.T("win.box.ruleset"), LeftX, Row1Y, boxW, boxH);
            float inner = boxW - BoxPad * 2f;

            CreateText(box, "LblFormat", CsrLoc.T("win.lbl.format"), 20f, PeakWindowStyle.Heading, BoxPad, 56f, inner, 26f);

            float gap = 10f;
            float pillW = (inner - gap * 2f) / 3f;
            float fmtY = 86f;
            _btnStandard = PeakWindowStyle.CreatePill(box, "BtnStandard", CsrLoc.T("hud.format.standard"), BoxPad, fmtY, pillW, 40f, 17f);
            _btnStandard.onClick.AddListener(new UnityAction(OnStandardClicked));
            _btnHardcore = PeakWindowStyle.CreatePill(box, "BtnHardcore", CsrLoc.T("hud.format.hardcore"), BoxPad + pillW + gap, fmtY, pillW, 40f, 17f);
            _btnHardcore.onClick.AddListener(new UnityAction(OnHardcoreClicked));
            _btnSprint = PeakWindowStyle.CreatePill(box, "BtnSprint", CsrLoc.T("hud.format.sprint"), BoxPad + (pillW + gap) * 2f, fmtY, pillW, 40f, 17f);
            _btnSprint.onClick.AddListener(new UnityAction(OnSprintClicked));

            CreateText(box, "LblAscentCaption", CsrLoc.T("win.lbl.ascent"), 20f, PeakWindowStyle.Heading, BoxPad, 142f, inner, 26f);

            // + 号右缘与下方"计分赛"按钮右缘(BoxPad + 240)对齐, 视觉上成一条竖线
            _btnAscentMinus = PeakWindowStyle.CreatePill(box, "BtnAscentMinus", "-", BoxPad, 172f, 44f, 40f, 22f);
            _btnAscentMinus.onClick.AddListener(new UnityAction(OnAscentMinus));
            _lblAscent = CreateText(box, "LblAscent", "", 19f, PeakWindowStyle.TextPrimary, BoxPad + 54f, 172f, 130f, 40f);
            _lblAscent.enableAutoSizing = true;
            _lblAscent.fontSizeMin = 14f;
            _lblAscent.fontSizeMax = 19f;
            _btnAscentPlus = PeakWindowStyle.CreatePill(box, "BtnAscentPlus", "+", BoxPad + 196f, 172f, 44f, 40f, 22f);
            _btnAscentPlus.onClick.AddListener(new UnityAction(OnAscentPlus));
            _lblMultiplier = CreateText(box, "LblMult", "", 18f, PeakWindowStyle.TextMuted, BoxPad + 252f, 172f, inner - 252f, 40f);

            _btnRated = PeakWindowStyle.CreatePill(box, "BtnRated", CsrLoc.T("hud.invite.rated"), BoxPad, 228f, 240f, 40f, 18f);
            _btnRated.onClick.AddListener(new UnityAction(OnRatedToggled));

            CreateText(box, "LblInvite", CsrLoc.T("win.lbl.invite"), 20f, PeakWindowStyle.Heading, BoxPad, 280f, inner, 26f);
            _inputInvite = PeakWindowStyle.CreateInputField(box, "InputInvite", "", BoxPad, 310f, 200f, 40f, 19f);
            ConfigurePlainInput(_inputInvite, 12, true);

            float actY = 310f;
            float actGap = 10f;
            float inputW = 200f;
            float btnEach = (inner - inputW - actGap * 2f) / 2f;
            float actX = BoxPad + inputW + actGap;
            Button join = PeakWindowStyle.CreatePill(box, "BtnJoin", CsrLoc.T("hud.invite.join"), actX, actY, btnEach, 40f, 17f);
            join.onClick.AddListener(new UnityAction(OnJoinClicked));
            Button host = PeakWindowStyle.CreatePill(box, "BtnHost", CsrLoc.T("hud.invite.host"), actX + btnEach + actGap, actY, btnEach, 40f, 17f);
            host.onClick.AddListener(new UnityAction(OnHostClicked));
        }

        // =========================================================
        // 右上: 对局状态
        // =========================================================
        private void BuildMatchBox()
        {
            RectTransform box = PeakWindowStyle.CreateBox(
                _body, "BoxMatch", CsrLoc.T("win.box.match"), RightX, Row1Y, RightW, Row1H);
            float inner = RightW - BoxPad * 2f;

            _lblStatus = CreateText(box, "LblStatus", "", 19f, PeakWindowStyle.TextPrimary, BoxPad, 54f, inner, 52f, wrap: true);
            _lblEnv = CreateText(box, "LblEnv", "", 15f, PeakWindowStyle.TextMuted, BoxPad, 108f, inner, 22f);
            _lblRoom = CreateText(box, "LblRoom", "", 17f, PeakWindowStyle.TextMuted, BoxPad, 132f, inner, 24f);
            _lblOpponent = CreateText(box, "LblOpponent", "", 17f, PeakWindowStyle.TextMuted, BoxPad, 158f, inner, 24f);
            _lblProgress = CreateText(box, "LblProgress", "", 17f, PeakWindowStyle.TextPrimary, BoxPad, 184f, inner, 24f);
            _lblVitals = CreateText(box, "LblVitals", "", 17f, PeakWindowStyle.TextMuted, BoxPad, 210f, inner, 24f);

            _btnAction = PeakWindowStyle.CreatePill(box, "BtnAction", CsrLoc.T("hud.btn.queue"), BoxPad, 240f, 300f, 42f, 18f);
            _btnAction.onClick.AddListener(new UnityAction(OnActionClicked));

            _btnReroll = PeakWindowStyle.CreatePill(box, "BtnReroll", CsrLoc.T("hud.btn.reroll"), BoxPad, 296f, 250f, 40f, 17f);
            _btnReroll.onClick.AddListener(new UnityAction(OnRerollClicked));
            _btnRerollAccept = PeakWindowStyle.CreatePill(box, "BtnRerollAccept", CsrLoc.T("hud.btn.reroll_accept"), BoxPad, 296f, 250f, 40f, 17f);
            _btnRerollAccept.onClick.AddListener(new UnityAction(OnRerollAcceptClicked));
            _btnRerollDecline = PeakWindowStyle.CreatePill(box, "BtnRerollDecline", CsrLoc.T("hud.btn.reroll_decline"), BoxPad + 262f, 296f, 180f, 40f, 17f);
            _btnRerollDecline.onClick.AddListener(new UnityAction(OnRerollDeclineClicked));
            _btnForfeit = PeakWindowStyle.CreatePill(box, "BtnForfeit", CsrLoc.T("hud.btn.forfeit"), BoxPad + 454f, 296f, 128f, 40f, 16f);
            _btnForfeit.onClick.AddListener(new UnityAction(OnForfeitClicked));
        }

        // =========================================================
        // 左下: 实时队列独立面板
        // =========================================================
        private void BuildQueueBox()
        {
            RectTransform box = PeakWindowStyle.CreateBox(
                _body, "BoxQueue", CsrLoc.T("win.box.queue"), LeftX, Row2Y, LeftW, Row2H);
            float inner = LeftW - BoxPad * 2f;

            _lblQueueHead = CreateText(box, "QueueHead", "", 17f, PeakWindowStyle.TextMuted, BoxPad, 54f, inner, 24f);
            _lblQueueTotal = CreateText(box, "QueueTotal", "", 19f, PeakWindowStyle.TextPrimary, BoxPad, 80f, inner, 26f);
            _lblQueueMine = CreateText(box, "QueueMine", "", 16f, PeakWindowStyle.TextMuted, BoxPad, 108f, inner, 24f);

            float rowY = 142f;
            float rowH = 32f;
            _lblQueueStd = BuildQueueRow(box, "QueueStd", CsrLoc.T("win.queue.mode.std"), BoxPad, rowY, out _barQueueStd);
            _lblQueueAsc = BuildQueueRow(box, "QueueAsc", CsrLoc.T("win.queue.mode.asc"), BoxPad, rowY + rowH, out _barQueueAsc);
            _lblQueueSpr = BuildQueueRow(box, "QueueSpr", CsrLoc.T("win.queue.mode.spr"), BoxPad, rowY + rowH * 2f, out _barQueueSpr);
        }

        // 三段式: 赛制名(左) | 人数文案(中, 自动缩字号) | 比例条(右, 吃掉剩余空白)
        private TextMeshProUGUI BuildQueueRow(
            RectTransform box, string name, string label, float x, float y, out Image bar)
        {
            TextMeshProUGUI lbl = CreateText(box, name + "_Label", label, 16f, PeakWindowStyle.TextPrimary, x, y, QueueLabelW, 24f);
            lbl.enableAutoSizing = true;
            lbl.fontSizeMin = 12f;
            lbl.fontSizeMax = 16f;

            TextMeshProUGUI val = CreateText(box, name + "_Val", "", 15f, PeakWindowStyle.TextMuted,
                x + QueueCellX, y, QueueCellW, 24f);
            // 文案超宽时自动缩号, 不再压到右边比例条
            val.enableAutoSizing = true;
            val.fontSizeMin = 10f;
            val.fontSizeMax = 15f;

            Image back = PeakWindowStyle.CreateSurface(box, name + "_BarBack", new Color(0.82f, 0.85f, 0.83f, 1f), 7);
            NativeUiFactory.Place((RectTransform)back.transform, x + QueueBarX, y + 7f, QueueBarW, 12f);
            bar = PeakWindowStyle.CreateSurface(box, name + "_BarFill", new Color(0.16f, 0.42f, 0.9f, 1f), 7);
            NativeUiFactory.Place((RectTransform)bar.transform, x + QueueBarX, y + 7f, 0f, 12f);
            bar.name = name + "_BarFill";
            return val;
        }

        // =========================================================
        // 右下: 历史战绩
        // =========================================================
        private void BuildHistoryBox()
        {
            RectTransform box = PeakWindowStyle.CreateBox(
                _body, "BoxHistory", CsrLoc.T("win.box.history"), RightX, Row2Y, RightW, Row2H);
            float inner = RightW - BoxPad * 2f;

            _lblHistoryMeta = CreateText(box, "HistoryMeta", "", 16f, PeakWindowStyle.TextMuted, BoxPad, 52f, inner - 200f, 24f);
            _btnHistoryRefresh = PeakWindowStyle.CreatePill(
                box, "BtnHistoryRefresh", CsrLoc.T("hud.btn.refresh_history"),
                RightW - BoxPad - 176f, RefreshBtnY, 176f, RefreshBtnH, 16f);
            _btnHistoryRefresh.onClick.AddListener(new UnityAction(OnRefreshHistoryClicked));
            ShortenBoxHeading(box, RightW, 176f + 12f);

            PeakWindowStyle.CreateSeparator(box, "HistorySep", BoxPad, 80f, inner);

            for (int i = 0; i < _historyRows.Length; i++)
            {
                _historyRows[i] = CreateText(box, "HistoryRow" + i, "", 16f, PeakWindowStyle.TextPrimary,
                    BoxPad, 88f + i * 25f, inner, 22f);
            }
        }

        // =========================================================
        // 下: 全服天梯真表格
        // =========================================================
        private void BuildBoardBox()
        {
            RectTransform box = PeakWindowStyle.CreateBox(
                _body, "BoxBoard", CsrLoc.T("win.box.board"), LeftX, Row3Y, FullW, Row3H);
            float inner = FullW - BoxPad * 2f;

            _lblBoardMeta = CreateText(box, "LblMeta", "", 16f, PeakWindowStyle.TextMuted, BoxPad, 52f, inner - 220f, 24f);
            _btnRefreshBoard = PeakWindowStyle.CreatePill(
                box, "BtnRefreshBoard", CsrLoc.T("hud.btn.refresh_board"),
                FullW - BoxPad - 180f, RefreshBtnY, 180f, RefreshBtnH, 17f);
            _btnRefreshBoard.onClick.AddListener(new UnityAction(OnRefreshBoardClicked));
            ShortenBoxHeading(box, FullW, 180f + 12f);

            PeakWindowStyle.CreateSeparator(box, "BoardSep", BoxPad, 78f, inner);

            // 表头: 逐列独立定位, 与数据行共用同一套列坐标
            _boardHeaderParts.Clear();

            Image headerBg = PeakWindowStyle.CreateSurface(box, "BoardHeaderBg", new Color(0.82f, 0.86f, 0.87f, 1f), 6);
            NativeUiFactory.Place((RectTransform)headerBg.transform, BoxPad, BoardHeaderY, inner, BoardHeaderH);
            headerBg.raycastTarget = false;
            _boardHeaderParts.Add(headerBg.gameObject);

            _lblBoardHeader = CreateText(box, "BoardHeaderRank", "#", 15f, PeakWindowStyle.Heading,
                BoxPad + ColRankX, BoardHeaderY + 5f, ColRankW, 20f);
            _boardHeaderParts.Add(_lblBoardHeader.gameObject);
            AddHeaderCell(box, "BoardHeaderName", "win.board.col.player", ColNameX, ColNameW, TextAlignmentOptions.Left);
            AddHeaderCell(box, "BoardHeaderTier", "win.board.col.tier", ColTierX, ColTierW, TextAlignmentOptions.Left);
            AddHeaderCell(box, "BoardHeaderElo", "win.board.col.elo", ColEloX, ColEloW, TextAlignmentOptions.Right);
            AddHeaderCell(box, "BoardHeaderRecord", "win.board.col.record", ColRecordX, ColRecordW, TextAlignmentOptions.Left);
            AddHeaderCell(box, "BoardHeaderModes", "win.board.col.modes", ColModesX, ColModesW, TextAlignmentOptions.Left);

            for (int i = 0; i < 8; i++)
            {
                float y = BoardRowStartY + i * BoardRowH;

                // 斑马纹: 隔行铺一层极浅底色, 长行更容易横向追踪
                if ((i & 1) == 1)
                {
                    Image stripe = PeakWindowStyle.CreateSurface(box, "RowStripe" + i, new Color(0.86f, 0.89f, 0.88f, 1f), 5);
                    NativeUiFactory.Place((RectTransform)stripe.transform, BoxPad, y - 1f, inner, BoardRowH);
                    stripe.raycastTarget = false;
                    _rowStripes[i] = stripe;
                }

                _colRank[i] = CreateText(box, "Rank" + i, "", 16f, PeakWindowStyle.TextPrimary,
                    BoxPad + ColRankX, y, ColRankW, 22f);
                _colName[i] = CreateText(box, "Name" + i, "", 16f, PeakWindowStyle.TextPrimary,
                    BoxPad + ColNameX, y, ColNameW, 22f);
                _colTier[i] = CreateText(box, "Tier" + i, "", 16f, PeakWindowStyle.TextPrimary,
                    BoxPad + ColTierX, y, ColTierW, 22f);
                _colElo[i] = CreateTextRight(box, "Elo" + i, "", 17f, PeakWindowStyle.TextPrimary,
                    BoxPad + ColEloX, y, ColEloW, 22f);
                _colRecord[i] = CreateText(box, "Rec" + i, "", 15f, PeakWindowStyle.TextMuted,
                    BoxPad + ColRecordX, y, ColRecordW, 22f);
                _colModes[i] = CreateText(box, "Modes" + i, "", 15f, PeakWindowStyle.TextMuted,
                    BoxPad + ColModesX, y, ColModesW, 22f);

                // 整行透明按钮: 点击打开玩家档案
                Image hit = PeakWindowStyle.CreateSurface(box, "RowHit" + i, new Color(1f, 1f, 1f, 0f), 6);
                RectTransform hitRt = (RectTransform)hit.transform;
                NativeUiFactory.Place(hitRt, BoxPad, y - 1f, inner, BoardRowH);
                hit.raycastTarget = true;
                Button hitBtn = hit.gameObject.AddComponent<Button>();
                hitBtn.transition = Selectable.Transition.None;
                int captured = i;
                hitBtn.onClick.AddListener(new UnityAction(() => OnBoardRowClicked(captured)));
                _rowButtons[i] = hitBtn;
            }

            _lblBoardEmpty = CreateText(box, "BoardEmpty", CsrLoc.T("win.board.empty"), 16f, PeakWindowStyle.TextMuted,
                BoxPad, BoardRowStartY + 6f, inner, 24f);
        }

        private void AddHeaderCell(
            RectTransform box, string name, string locKey, float x, float w, TextAlignmentOptions align)
        {
            TextMeshProUGUI cell = PeakWindowStyle.CreateText(box, name, CsrLoc.T(locKey), 15f, PeakWindowStyle.Heading, align);
            NativeUiFactory.Place((RectTransform)cell.transform, BoxPad + x, BoardHeaderY + 5f, w, 20f);
            cell.textWrappingMode = TextWrappingModes.NoWrap;
            cell.overflowMode = TextOverflowModes.Truncate;
            cell.margin = Vector4.zero;
            _boardHeaderParts.Add(cell.gameObject);
        }

        // PeakWindowStyle.CreateInputField 内部把 contentType 写死为 IntegerNumber(2),
        // 导致所有输入框都拒收非数字。这里统一按用途重设, 并保持富文本开启:
        // TMP_InputField 靠富文本的 <u> 标签绘制光标与选区, 把 richText 关掉就会把标签当普通文字显示出来。
        private static void ConfigurePlainInput(TMP_InputField input, int characterLimit, bool alphanumericOnly)
        {
            if (input == null) return;

            input.characterLimit = characterLimit;
            input.contentType = alphanumericOnly
                ? TMP_InputField.ContentType.Alphanumeric
                : TMP_InputField.ContentType.Standard;
            input.characterValidation = alphanumericOnly
                ? TMP_InputField.CharacterValidation.Alphanumeric
                : TMP_InputField.CharacterValidation.None;
            input.lineType = TMP_InputField.LineType.SingleLine;

            if (input.textComponent != null)
            {
                // 必须保留富文本, 否则光标/选区的 <u> 标签会露出来
                input.textComponent.richText = true;
                input.textComponent.textWrappingMode = TextWrappingModes.NoWrap;
                input.textComponent.overflowMode = TextOverflowModes.Truncate;
            }
        }

        // 标题行右侧要放刷新按钮时, 把标题可用宽度收窄到按钮左侧, 防止长标题被压住
        private static void ShortenBoxHeading(RectTransform box, float boxW, float rightReserve)
        {
            if (box == null) return;
            Transform heading = box.Find("Heading");
            if (heading == null) return;
            RectTransform rt = heading as RectTransform;
            if (rt == null) return;
            float w = boxW - BoxPad * 2f - rightReserve;
            if (w > 40f) rt.sizeDelta = new Vector2(w, rt.sizeDelta.y);
        }

        private static TextMeshProUGUI CreateText(
            Transform parent, string name, string text, float size, Color color,
            float x, float y, float w, float h, bool wrap = false)
        {
            TextMeshProUGUI label = PeakWindowStyle.CreateText(
                parent, name, text, size, color, TextAlignmentOptions.Left);
            float minHeight = size * 1.35f;
            NativeUiFactory.Place((RectTransform)label.transform, x, y, w, Mathf.Max(h, minHeight));
            label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.margin = Vector4.zero;
            return label;
        }

        private static TextMeshProUGUI CreateTextRight(
            Transform parent, string name, string text, float size, Color color,
            float x, float y, float w, float h)
        {
            TextMeshProUGUI label = PeakWindowStyle.CreateText(
                parent, name, text, size, color, TextAlignmentOptions.Right);
            float minHeight = size * 1.35f;
            NativeUiFactory.Place((RectTransform)label.transform, x, y, w, Mathf.Max(h, minHeight));
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.margin = Vector4.zero;
            return label;
        }

        // =========================================================
        // 交互
        // =========================================================

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
            Mc.InitiateRerollVote();
            RefreshAll();
        }

        private void OnRerollAcceptClicked()
        {
            if (Mc == null) return;
            Mc.RespondRerollVote(true);
            RefreshAll();
        }

        private void OnRerollDeclineClicked()
        {
            if (Mc == null) return;
            Mc.RespondRerollVote(false);
            RefreshAll();
        }

        private void OnForfeitClicked()
        {
            if (Mc == null) return;
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

        private void OnRefreshHistoryClicked()
        {
            if (CloudLeaderboardService.Instance != null)
            {
                CloudLeaderboardService.Instance.RefreshHistory(true);
            }
            RefreshDynamic();
        }

        private void OnHelpClicked()
        {
            PeakCsrPrompt.Info(CsrLoc.T("win.help.title"), CsrLoc.T("win.help.body"));
        }

        private void OnBoardRowClicked(int index)
        {
            CloudLeaderboardService svc = CloudLeaderboardService.Instance;
            if (svc == null || svc.Entries == null || index >= svc.Entries.Count) return;
            CloudRankEntry e = svc.Entries[index];
            if (string.IsNullOrEmpty(e.SteamId)) return;
            _profileSteamId = e.SteamId;
            ShowProfileCard(e);
        }

        // =========================================================
        // 刷新
        // =========================================================

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

            bool idle = Mc.State == CsMatchState.Idle;
            SetActive(_btnAscentMinus, idle);
            SetActive(_btnAscentPlus, idle);
            SetActive(_lblAscent, true);
            SetActive(_lblMultiplier, true);

            if (_lblAscent != null)
            {
                _lblAscent.text = MatchController.GetAscentDisplayName(Mc.SelectedAscent);
            }
            if (_lblMultiplier != null)
            {
                double mult = EloCalculator.GetModeMultiplier(Mc.SelectedFormat, Mc.SelectedAscent);
                _lblMultiplier.text = CsrLoc.T("win.lbl.mult2", mult.ToString("F2"));
            }

            PeakWindowStyle.SetPillText(_btnRated,
                Mc.IsRatedMatch ? ("● " + CsrLoc.T("hud.invite.rated")) : ("○ " + CsrLoc.T("hud.invite.unrated")));
        }

        private static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 面板刷新字段异常: " + ex.Message);
            }
        }

        private static string SafeBiomeLabel(int levelIndex)
        {
            try
            {
                return MapCatalog.GetBiomeLabel(levelIndex) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void RefreshDynamic()
        {
            if (Mc == null)
            {
                if (_lblStatus != null) _lblStatus.text = CsrLoc.T("status.not_ready");
                return;
            }

            Safe(() =>
            {
                if (_lblStatus != null) _lblStatus.text = CsrLoc.T("hud.status", Mc.StatusMessage);
            });

            Safe(() =>
            {
                if (_lblEnv == null) return;
                _lblEnv.text = CsrLoc.T("win.lbl.env",
                    MatchController.GetEnvironmentGameVersion(),
                    MatchController.GetEnvironmentMapSetHash());
            });

            RefreshQueuePanel();
            RefreshMatchInfo();
            RefreshActionButtons();
            RefreshHistory();
            RefreshBoard();
        }

        // ===== 对局信息 =====
        private void RefreshMatchInfo()
        {
            Safe(() =>
            {
                if (_lblRoom == null) return;
                int elo = EloCalculator.CurrentElo;
                if (string.IsNullOrEmpty(Mc.InviteCode))
                {
                    _lblRoom.text = CsrLoc.T("win.room.none",
                        EloCalculator.GetTierHexColorOnLight(elo),
                        EloCalculator.GetTierName(elo),
                        elo,
                        EloCalculator.WinRatePercent);
                }
                else
                {
                    _lblRoom.text = CsrLoc.T("win.room.info", Mc.InviteCode, Mc.LevelIndex, SafeBiomeLabel(Mc.LevelIndex),
                        MatchController.GetAscentShortName(Mc.Ascent),
                        IsSeedRevealed() ? Mc.Seed.ToString() : CsrLoc.T("hud.seed.hidden"));
                }
            });

            Safe(() =>
            {
                if (_lblOpponent == null) return;
                string verify = Mc.OpponentVerified ? CsrLoc.T("hud.verified.ok") : CsrLoc.T("hud.verified.wait");
                string oppTier = EloCalculator.GetTierName(Mc.OpponentElo);
                // 云端天梯里有这名玩家时优先显示其 PCSR 昵称, 否则用握手包里的 Steam 名
                string oppName = Mc.OpponentName;
                CloudLeaderboardService svc = CloudLeaderboardService.Instance;
                if (svc != null && !string.IsNullOrEmpty(Mc.OpponentSteamId))
                {
                    string nick = ResolveNickname(Mc.OpponentSteamId);
                    if (!string.IsNullOrEmpty(nick)) oppName = nick;
                }
                _lblOpponent.text = CsrLoc.T("hud.opponent", oppName, Mc.OpponentElo, verify, oppTier);
            });

            Safe(() =>
            {
                if (_lblProgress == null) return;
                float mine = Mc.SelfProgress * 100f;
                float theirs = Mc.OpponentProgress * 100f;
                float diff = mine - theirs;
                string color = diff >= 0f ? "1E7E45" : "C62828";
                string sign = diff >= 0f ? "+" : "";
                _lblProgress.text = CsrLoc.T("hud.progress",
                    mine.ToString("F1"), theirs.ToString("F1"), color, sign, diff.ToString("F1"),
                    EloCalculator.WinRatePercent);
            });

            Safe(() =>
            {
                if (_lblVitals == null) return;
                if (Mc.State == CsMatchState.Finished && Mc.MatchVoided)
                {
                    _lblVitals.text = CsrLoc.T("hud.void.notice");
                    return;
                }
                _lblVitals.text = Mc.State == CsMatchState.Running
                    ? CsrLoc.T("hud.vitals",
                        Mathf.RoundToInt(Mc.OpponentHealth * 100f),
                        Mathf.RoundToInt(Mc.OpponentStamina * 100f),
                        Mc.OpponentDown ? CsrLoc.T("hud.state.down") : CsrLoc.T("hud.state.climbing"))
                    : "";
            });
        }

        // ===== 实时队列独立面板 =====
        private void RefreshQueuePanel()
        {
            CsrQueueStats q = Mc.QueueStats;
            int maxPlayers = Mathf.Max(1, Mathf.Max(q.StdPlayers, Mathf.Max(q.AscPlayers, q.SprPlayers)));

            Safe(() =>
            {
                if (_lblQueueHead != null)
                {
                    _lblQueueHead.text = q.HasData
                        ? CsrLoc.T("win.queue.refreshed", q.RefreshedClock)
                        : CsrLoc.T("win.queue.no_data");
                }
            });

            Safe(() =>
            {
                if (_lblQueueTotal != null)
                {
                    _lblQueueTotal.text = CsrLoc.T("win.queue.total",
                        q.OnlinePlayers, q.WaitingRooms, q.ActiveRooms);
                }
            });

            Safe(() =>
            {
                if (_lblQueueMine != null)
                {
                    bool queuing = Mc.State == CsMatchState.SearchingLobby || Mc.State == CsMatchState.WaitingOpponent;
                    _lblQueueMine.text = queuing
                        ? CsrLoc.T("win.queue.mine_queuing", Mc.GetCurrentFormatDisplayName(),
                            MatchController.FormatElapsed(Mc.QueueElapsedSeconds))
                        : CsrLoc.T("win.queue.mine_idle", Mc.GetCurrentFormatDisplayName());
                }
            });

            Safe(() =>
            {
                if (_lblQueueStd != null)
                {
                    _lblQueueStd.text = CsrLoc.T("win.queue.cell", q.StdPlayers, q.StdWaiting, q.StdActive);
                }
                FillRatioBar(_barQueueStd, q.StdPlayers / (float)maxPlayers);
            });

            Safe(() =>
            {
                if (_lblQueueAsc != null)
                {
                    _lblQueueAsc.text = CsrLoc.T("win.queue.cell", q.AscPlayers, q.AscWaiting, q.AscActive);
                }
                FillRatioBar(_barQueueAsc, q.AscPlayers / (float)maxPlayers);
            });

            Safe(() =>
            {
                if (_lblQueueSpr != null)
                {
                    _lblQueueSpr.text = CsrLoc.T("win.queue.cell", q.SprPlayers, q.SprWaiting, q.SprActive);
                }
                FillRatioBar(_barQueueSpr, q.SprPlayers / (float)maxPlayers);
            });
        }

        // 按比例上色, 三个赛制用同一轨道宽度上限, 便于横向对比
        private static void FillRatioBar(Image bar, float ratio)
        {
            if (bar == null) return;
            float full = QueueBarW;
            RectTransform rt = (RectTransform)bar.transform;
            Vector2 size = rt.sizeDelta;
            size.x = Mathf.Clamp01(ratio) * full;
            rt.sizeDelta = size;
        }

        private static bool IsSeedRevealed()
        {
            return Mc != null && (Mc.State == CsMatchState.Running || Mc.State == CsMatchState.Finished);
        }

        // 从云端天梯/档案缓存里找该玩家的 PCSR 昵称, 找不到返回空串
        private static string ResolveNickname(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return "";
            CloudLeaderboardService svc = CloudLeaderboardService.Instance;
            if (svc == null) return "";

            CloudProfile cached = svc.CachedProfile(steamId);
            if (cached != null)
            {
                string nick = PlayerNameSanitizer.Clean(cached.Nickname);
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
                        string nick = PlayerNameSanitizer.Clean(e.Nickname);
                        if (!string.IsNullOrEmpty(nick)) return nick;
                        break;
                    }
                }
            }
            return "";
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
                    // 作废局换个说法, 避免"继续匹配下一局"让人误以为这局算过了
                    actionText = CsrLoc.T(Mc.MatchVoided ? "hud.btn.void_requeue" : "hud.btn.queue_next");
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
            bool showRerollZone = inMatch || Mc.State == CsMatchState.WaitingOpponent;

            // 收到对手换图请求时, 拆成 同意 / 拒绝 双按钮
            bool oppVoting = showRerollZone && Mc.OppRerollVoteActive;
            SetActive(_btnReroll, showRerollZone && !oppVoting);
            SetActive(_btnRerollAccept, oppVoting);
            SetActive(_btnRerollDecline, oppVoting);
            SetActive(_btnForfeit, inMatch);

            if (oppVoting)
            {
                if (_btnRerollAccept != null) PeakWindowStyle.SetPillText(_btnRerollAccept, CsrLoc.T("hud.btn.reroll_accept"));
                if (_btnRerollDecline != null) PeakWindowStyle.SetPillText(_btnRerollDecline, CsrLoc.T("hud.btn.reroll_decline"));
            }
            else if (_btnReroll != null)
            {
                if (Mc.MyRerollVoteActive)
                {
                    PeakWindowStyle.SetPillText(_btnReroll, CsrLoc.T("hud.btn.reroll_waiting", Mc.RerollVoteRemainingSeconds));
                    _btnReroll.interactable = false;
                }
                else
                {
                    PeakWindowStyle.SetPillText(_btnReroll, CsrLoc.T("hud.btn.reroll"));
                    _btnReroll.interactable = true;
                }
            }
        }

        // ===== 历史战绩 =====
        private void RefreshHistory()
        {
            Safe(() =>
            {
                CloudLeaderboardService svc = CloudLeaderboardService.Instance;
                int count = svc != null && svc.History != null ? svc.History.Count : 0;

                if (_lblHistoryMeta != null)
                {
                    _lblHistoryMeta.text = svc != null && svc.HistoryLoading
                        ? CsrLoc.T("win.history.loading")
                        : CsrLoc.T("win.history.count", count);
                }

                for (int i = 0; i < _historyRows.Length; i++)
                {
                    if (_historyRows[i] == null) continue;
                    if (svc == null || svc.History == null || i >= svc.History.Count)
                    {
                        _historyRows[i].text = (i == 0 && count == 0)
                            ? "<color=#8A93A5>" + CsrLoc.T("win.history.empty") + "</color>"
                            : string.Empty;
                        continue;
                    }

                    CsrHistoryEntry h = svc.History[i];
                    string verdictHex = h.IsVoid ? "5A6270" : (h.Won ? "1E7E45" : "C62828");
                    string verdict = h.IsVoid ? CsrLoc.T("win.history.void") : (h.Won ? CsrLoc.T("win.history.win") : CsrLoc.T("win.history.loss"));
                    string deltaSign = h.Delta >= 0 ? "+" : "";
                    string deltaText = h.IsVoid ? "±0" : (deltaSign + h.Delta);
                    string mode = MatchController.GetAscentShortName(h.Ascent);
                    string biome = string.IsNullOrEmpty(h.BiomeLabel) ? ("#" + h.LevelIndex) : h.BiomeLabel;
                    string clock = CloudLeaderboardService.FormatRefreshClock(h.SettledAtMs);

                    _historyRows[i].text =
                        "<color=#" + verdictHex + "><b>" + verdict + "</b></color>"
                        + "  " + mode + " · " + biome
                        + "  <color=#" + verdictHex + "><b>" + deltaText + "</b></color>"
                        + "  <color=#8A93A5>" + h.EloBefore + "→" + h.EloAfter + " · " + clock + "</color>";
                }
            });
        }

        // ===== 天梯表格 =====
        private void RefreshBoard()
        {
            Safe(() =>
            {
                CloudLeaderboardService svc = CloudLeaderboardService.Instance;
                int count = svc != null && svc.Entries != null ? svc.Entries.Count : 0;

                if (_lblBoardMeta != null)
                {
                    string status = svc != null && !string.IsNullOrEmpty(svc.CloudStatus)
                        ? svc.CloudStatus
                        : CsrLoc.T("cloud.updated", count);
                    string clock = svc != null
                        ? CloudLeaderboardService.FormatRefreshClock(svc.LastRefreshedAtMs)
                        : "--:--:--";
                    _lblBoardMeta.text = status + "   " + CsrLoc.T("win.queue.refreshed", clock);
                }

                if (_lblBoardEmpty != null)
                {
                    _lblBoardEmpty.gameObject.SetActive(count == 0);
                }
                // 表头是底色条 + 六个独立列, 必须整组一起显隐
                for (int h = 0; h < _boardHeaderParts.Count; h++)
                {
                    if (_boardHeaderParts[h] != null) _boardHeaderParts[h].SetActive(count > 0);
                }

                string mySteamId = SteamIdentity.GetSteamId64();

                for (int i = 0; i < 8; i++)
                {
                    bool has = svc != null && svc.Entries != null && i < svc.Entries.Count;
                    if (_colRank[i] != null) _colRank[i].gameObject.SetActive(has);
                    if (_colName[i] != null) _colName[i].gameObject.SetActive(has);
                    if (_colTier[i] != null) _colTier[i].gameObject.SetActive(has);
                    if (_colElo[i] != null) _colElo[i].gameObject.SetActive(has);
                    if (_colRecord[i] != null) _colRecord[i].gameObject.SetActive(has);
                    if (_colModes[i] != null) _colModes[i].gameObject.SetActive(has);
                    if (_rowButtons[i] != null) _rowButtons[i].gameObject.SetActive(has);
                    if (_rowStripes[i] != null) _rowStripes[i].gameObject.SetActive(has);
                    if (!has) continue;

                    CloudRankEntry e = svc.Entries[i];
                    string tierHex = EloCalculator.GetTierHexColorOnLight(e.Elo);
                    string name = e.DisplayName;
                    if (string.IsNullOrEmpty(name)) name = CsrLoc.T("common.anonymous");

                    bool isMe = !string.IsNullOrEmpty(mySteamId) && e.SteamId == mySteamId;
                    string meTag = isMe ? CsrLoc.T("win.board.you_tag") : "";

                    // 昵称长度不可控, 先按可用像素宽度截断再拼富文本, 否则会直接糊到下一列
                    string shownName = FitText(_colName[i], name, isMe ? meTag.Length * 2.4f : 0f);
                    _colRank[i].text = "<b>#" + e.Rank + "</b>";
                    _colName[i].text = "<color=#" + tierHex + "><b>" + shownName + "</b></color>" + meTag;
                    _colTier[i].text = "<color=#" + tierHex + ">" + EloCalculator.GetTierName(e.Elo) + "</color>";
                    _colElo[i].text = "<color=#" + (isMe ? "0B5FA5" : "2A3441") + "><b>" + e.Elo + "</b></color>";
                    _colRecord[i].text = CsrLoc.T("win.board.record", e.Wins, e.Losses);
                    _colModes[i].text = CsrLoc.T("win.board.modes",
                        e.WinsAllPeak, e.LossesAllPeak,
                        e.WinsHardcore, e.LossesHardcore,
                        e.WinsSprint, e.LossesSprint);
                }
            });
        }

        // 按目标控件的实际像素宽度截断文本并补省略号, 中文按两倍宽估算
        private static string FitText(TextMeshProUGUI target, string text, float reservePx)
        {
            if (target == null || string.IsNullOrEmpty(text)) return text ?? "";

            RectTransform rt = target.transform as RectTransform;
            float available = (rt != null ? rt.rect.width : 200f) - reservePx - 6f;
            if (available <= 0f) return text;

            float fontSize = target.fontSize > 0f ? target.fontSize : 16f;
            float asciiWidth = fontSize * 0.55f;

            float used = 0f;
            int cut = text.Length;
            for (int i = 0; i < text.Length; i++)
            {
                float w = text[i] > 0x2E80 ? asciiWidth * 2f : asciiWidth;
                if (used + w > available)
                {
                    cut = i;
                    break;
                }
                used += w;
            }
            if (cut >= text.Length) return text;
            return cut <= 1 ? "…" : text.Substring(0, cut - 1) + "…";
        }

        private static void SetActive(Component comp, bool active)
        {
            if (comp != null && comp.gameObject.activeSelf != active)
            {
                comp.gameObject.SetActive(active);
            }
        }

        // =========================================================
        // 战报延迟弹出
        // =========================================================

        // 对局结束后调用: 只挂标记, 不糊脸
        public static void MarkPostMatchPending()
        {
            _postPending = true;
            if (_instance != null) _instance._postAutoShown = false;
        }

        public static bool HasPendingPostMatch { get { return _postPending; } }

        public static void ClearPendingPostMatch()
        {
            _postPending = false;
        }

        private void TickPendingPostMatch()
        {
            if (!_postPending || _postAutoShown) return;
            if (MatchController.LastPostMatch == null) return;

            // 等原生结算界面彻底消失 (倒计时结束 / 玩家点了返回主菜单) 才自动弹出
            bool endScreenAlive = false;
            try
            {
                endScreenAlive = UnityEngine.Object.FindAnyObjectByType<EndScreen>() != null;
            }
            catch { }

            if (endScreenAlive)
            {
                _sawEndScreen = true;
                return;
            }
            // 从未见过结算界面时也不要立刻弹, 给玩家 2 秒缓冲
            if (!_sawEndScreen)
            {
                _sawEndScreen = true;
                return;
            }

            _postAutoShown = true;
            _postPending = false;
            ShowPostMatch(MatchController.LastPostMatch);
        }

        public static void ShowPostMatchWindow()
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("PeakCSR_MenuWindow");
                DontDestroyOnLoad(go);
                go.AddComponent<PcsrMenuWindow>();
            }
            if (MatchController.LastPostMatch == null) return;
            _postPending = false;
            _instance.ShowPostMatch(MatchController.LastPostMatch);
        }

        // 兼容旧调用点: 外部显式要求弹出时才弹
        public static void TryAutoShowPostMatch(CsrPostMatchData data)
        {
            if (data == null) return;
            if (_instance == null) return;
            _instance.ShowPostMatch(data);
        }

        public void ShowPostMatch(CsrPostMatchData data)
        {
            if (data == null) return;
            _postData = data;

            if (_postRoot == null) BuildPostMatchUi();
            if (_postRoot == null) return;

            _postRoot.gameObject.SetActive(true);
            _postRoot.SetAsLastSibling();
            _postOpen = true;
            _sawEndScreen = false;
            NativeUiFactory.EnsureCanvasAlive();
            NativeUiFactory.EnsureTopMost();

            if (_postTitle != null)
            {
                if (data.IsVoid)
                {
                    _postTitle.text = CsrLoc.T("post.title.void");
                    _postTitle.color = PeakWindowStyle.TextMuted;
                }
                else
                {
                    _postTitle.text = data.IWon ? CsrLoc.T("post.title.win") : CsrLoc.T("post.title.loss");
                    _postTitle.color = data.IWon ? PeakWindowStyle.Positive : PeakWindowStyle.Danger;
                }
            }
            if (_postSubtitle != null)
            {
                _postSubtitle.text = CsrLoc.T("post.subtitle",
                    data.InviteCode, MatchController.GetAscentShortName(data.Ascent),
                    data.LevelIndex, data.BiomeLabel, data.Seed);
            }

            // ELO 数字滚动
            _eloAnimFrom = data.OldElo;
            _eloAnimTo = data.NewElo;
            _eloAnimStart = Time.unscaledTime;

            if (_postTierLabel != null)
            {
                _postTierLabel.text = "<color=#" + EloCalculator.GetTierHexColorOnLight(data.NewElo) + ">"
                    + data.NewTier + "</color>";
            }

            if (_postEloDetail != null)
            {
                if (data.IsVoid)
                {
                    _postEloDetail.text = CsrLoc.T("post.elo.void_detail",
                        data.OldElo,
                        Mathf.RoundToInt(data.MyProgress * 100f),
                        Mathf.RoundToInt(data.OppProgress * 100f));
                }
                else
                {
                    string baseSign = data.BaseDelta >= 0 ? "+" : "";
                    string bonusSign = data.ProgressBonus >= 0 ? "+" : "";
                    string floorSign = data.FloorAdjust >= 0 ? "+" : "";
                    _postEloDetail.text = CsrLoc.T("post.elo.detail2",
                        data.OldElo, data.NewElo,
                        baseSign + data.BaseDelta,
                        bonusSign + data.ProgressBonus,
                        floorSign + data.FloorAdjust,
                        Mathf.RoundToInt(data.MyProgress * 100f),
                        Mathf.RoundToInt(data.OppProgress * 100f));
                }
            }

            SetupEloBars(data);
            FillSplitRows(data);
        }

        // 双进度条: 灰=赛前位置, 新=赛后位置, 两条都补间
        private void SetupEloBars(CsrPostMatchData data)
        {
            if (_barOldFillRt == null || _barNewFillRt == null) return;

            int oldFloor, oldCeil, newFloor, newCeil;
            float oldRatio = EloCalculator.GetTierProgress01(data.OldElo, out oldFloor, out oldCeil);
            float newRatio = EloCalculator.GetTierProgress01(data.NewElo, out newFloor, out newCeil);

            _barOldTierFloor = oldFloor;
            _barOldTierCeil = oldCeil;
            _barNewTierFloor = newFloor;
            _barNewTierCeil = newCeil;
            _barCrossedTier = (oldFloor != newFloor || oldCeil != newCeil);

            _barFromOld = 0f;
            _barFromNew = 0f;
            _barToOld = oldRatio;
            _barToNew = newRatio;

            // 扣分时新条用红色, 加分用绿色, 作废局用中性灰
            if (_barNewFillImage != null)
            {
                if (data.IsVoid) _barNewFillImage.color = new Color(0.35f, 0.38f, 0.44f, 1f);
                else if (data.TotalDelta >= 0) _barNewFillImage.color = new Color(0.11f, 0.51f, 0.29f, 1f);
                else _barNewFillImage.color = new Color(0.78f, 0.20f, 0.18f, 1f);
            }
            if (_barBackImage != null)
            {
                _barBackImage.color = new Color(0.82f, 0.85f, 0.83f, 1f);
            }

            _barAnimStart = Time.unscaledTime;
            ApplyBarWidths(0f);
        }

        private void ApplyBarWidths(float eased)
        {
            if (_barOldFillRt == null || _barNewFillRt == null) return;
            float w = _barTotalWidth > 1f ? _barTotalWidth : 984f;

            float oldW = Mathf.Lerp(_barFromOld, _barToOld, eased) * w;
            float newW = Mathf.Lerp(_barFromNew, _barToNew, eased) * w;

            RectTransformOldWidth(_barOldFillRt, oldW);
            RectTransformOldWidth(_barNewFillRt, newW);
        }

        private void RectTransformOldWidth(RectTransform rt, float width)
        {
            Vector2 size = rt.sizeDelta;
            size.x = Mathf.Max(0f, width);
            rt.sizeDelta = size;
        }

        private void TickPostMatchAnimation()
        {
            if (_postData == null) return;

            // ELO 数字滚动
            float elapsed = Time.unscaledTime - _eloAnimStart;
            float t = Mathf.Clamp01(elapsed / EloAnimSeconds);
            float eased = 1f - Mathf.Pow(1f - t, 3f);   // EaseOutCubic
            int shown = Mathf.RoundToInt(Mathf.Lerp(_eloAnimFrom, _eloAnimTo, eased));

            if (_postEloBig != null)
            {
                if (_postData.IsVoid)
                {
                    _postEloBig.text = "<color=#2A3441>" + shown + "</color>"
                        + "  <color=#5A6270>±0</color>";
                }
                else
                {
                    string sign = _postData.TotalDelta >= 0 ? "+" : "";
                    string deltaHex = _postData.TotalDelta >= 0 ? "1E7E45" : "C62828";
                    _postEloBig.text = "<color=#2A3441>" + shown + "</color>"
                        + "  <color=#" + deltaHex + ">" + sign + _postData.TotalDelta + "</color>";
                }
            }

            // 双进度条补间 (比 ELO 数字略长, 视觉上后收尾)
            float barT = Mathf.Clamp01(elapsed / (EloAnimSeconds * 1.15f));
            float barEased = 1f - Mathf.Pow(1f - barT, 3f);
            ApplyBarWidths(barEased);

            if (barT >= 1f && _postTierLabel != null)
            {
                string cross = _barCrossedTier ? CsrLoc.T("post.tier.changed") : "";
                _postTierLabel.text = "<color=#" + EloCalculator.GetTierHexColorOnLight(_postData.NewElo) + ">"
                    + _postData.NewTier + "</color>" + cross;
            }
        }

        private void BuildPostMatchUi()
        {
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return;

            GameObject rootGo = new GameObject("PcsrPostRoot", typeof(RectTransform));
            rootGo.transform.SetParent(canvas.transform, false);
            _postRoot = (RectTransform)rootGo.transform;
            _postRoot.anchorMin = Vector2.zero;
            _postRoot.anchorMax = Vector2.one;
            _postRoot.offsetMin = Vector2.zero;
            _postRoot.offsetMax = Vector2.zero;

            PeakWindowStyle.CreateBackdrop(_postRoot, "PostBackdrop");

            const float w = 1080f;
            const float h = 812f;
            const float titleH = 78f;
            _postWindow = PeakWindowStyle.CreateWindow(_postRoot, "PcsrPost", w, h, titleH, "PEAK CSR", "");

            TextMeshProUGUI winTitle = _postWindow.Title;
            if (winTitle != null)
            {
                RectTransform rt = winTitle.transform as RectTransform;
                if (rt != null) NativeUiFactory.Place(rt, 30f, 0f, w - 60f - 110f, titleH);
                winTitle.textWrappingMode = TextWrappingModes.NoWrap;
                winTitle.overflowMode = TextOverflowModes.Overflow;
                winTitle.alignment = TextAlignmentOptions.Left;
            }

            RectTransform body = _postWindow.Body;
            float inner = w - 52f;

            _postSubtitle = CreateText(body, "PostSub", "", 17f, PeakWindowStyle.TextMuted, 26f, 10f, inner, 24f);
            _postSubtitle.alignment = TextAlignmentOptions.Left;

            _postTitle = CreateText(body, "PostTitle", "", 30f, PeakWindowStyle.Positive, 26f, 38f, inner, 40f);

            // ---- ELO 结算区 ----
            RectTransform eloBox = PeakWindowStyle.CreateBox(body, "PostEloBox", CsrLoc.T("post.box.elo"), 26f, 92f, inner, 210f);
            float eloInner = inner - BoxPad * 2f;

            _postEloBig = CreateText(eloBox, "PostEloBig", "", 34f, PeakWindowStyle.TextPrimary, BoxPad, 50f, 380f, 46f);
            _postTierLabel = CreateText(eloBox, "PostTier", "", 24f, PeakWindowStyle.Heading, BoxPad + 396f, 54f, eloInner - 396f, 40f);

            // 双进度条轨道
            Image back = PeakWindowStyle.CreateSurface(eloBox, "EloBarBack", new Color(0.82f, 0.85f, 0.83f, 1f), 8);
            _barBackImage = back;
            RectTransform backRt = (RectTransform)back.transform;
            NativeUiFactory.Place(backRt, BoxPad, 104f, eloInner, 16f);
            _barTotalWidth = Mathf.Max(1f, eloInner);

            // 旧进度 (灰, 表示赛前位置)
            Image oldFill = PeakWindowStyle.CreateSurface(eloBox, "EloBarOld", new Color(0.62f, 0.66f, 0.65f, 1f), 8);
            _barOldFillRt = (RectTransform)oldFill.transform;
            NativeUiFactory.Place(_barOldFillRt, BoxPad, 105f, 0f, 14f);

            // 新进度 (绿/红, 表示赛后位置)
            Image newFill = PeakWindowStyle.CreateSurface(eloBox, "EloBarNew", new Color(0.11f, 0.51f, 0.29f, 0.85f), 8);
            _barNewFillImage = newFill;
            _barNewFillRt = (RectTransform)newFill.transform;
            NativeUiFactory.Place(_barNewFillRt, BoxPad, 105f, 0f, 14f);

            // 段位分界刻度
            TextMeshProUGUI floorLbl = CreateText(eloBox, "EloFloor", "", 13f, PeakWindowStyle.TextMuted, BoxPad, 124f, 120f, 18f);
            if (floorLbl != null) floorLbl.text = "";

            _postEloDetail = CreateText(eloBox, "PostEloDetail", "", 17f, PeakWindowStyle.TextPrimary, BoxPad, 140f, eloInner, 26f);

            // ---- 分段对比区 ----
            const float splitsH = 328f;
            RectTransform splitBox = PeakWindowStyle.CreateBox(body, "PostSplitsBox", CsrLoc.T("post.box.splits"), 26f, 310f, inner, splitsH);
            float spInner = inner - BoxPad * 2f;

            CreateText(splitBox, "SplitHeader", CsrLoc.T("post.splits.header"), 16f, PeakWindowStyle.Heading, BoxPad, 50f, spInner, 24f);
            PeakWindowStyle.CreateSeparator(splitBox, "SplitSep", BoxPad, 76f, spInner);

            for (int i = 0; i < _postSplitRows.Length; i++)
            {
                _postSplitRows[i] = CreateText(splitBox, "SplitRow" + i, "", 17f, PeakWindowStyle.TextPrimary,
                    BoxPad, 84f + i * 29f, spInner, 26f);
            }

            _postSplitsEmpty = CreateText(splitBox, "SplitEmpty", CsrLoc.T("post.splits.empty"), 17f, PeakWindowStyle.TextMuted,
                BoxPad, 84f, spInner, 26f);

            // ---- 底部按钮 ----
            float btnY = h - titleH - 14f - 62f;
            float btnW = 240f;
            float gap = 20f;
            float totalW = btnW * 3f + gap * 2f;
            float startX = (w - totalW) / 2f;

            Button closeBtn = PeakWindowStyle.CreatePill(body, "PostClose", CsrLoc.T("post.btn.close"), startX, btnY, btnW, 46f, 18f);
            closeBtn.onClick.AddListener(new UnityAction(ClosePostMatch));
            Button menuBtn = PeakWindowStyle.CreatePill(body, "PostMenu", CsrLoc.T("post.btn.menu"), startX + btnW + gap, btnY, btnW, 46f, 18f);
            menuBtn.onClick.AddListener(new UnityAction(OnPostMenuClicked));
            Button rematchBtn = PeakWindowStyle.CreatePill(body, "PostRematch", CsrLoc.T("post.btn.rematch"), startX + (btnW + gap) * 2f, btnY, btnW, 46f, 18f);
            rematchBtn.onClick.AddListener(new UnityAction(OnPostRematchClicked));

            _postRoot.gameObject.SetActive(false);
        }

        private void FillSplitRows(CsrPostMatchData data)
        {
            if (data.Splits == null || data.Splits.Count == 0)
            {
                if (_postSplitsEmpty != null) _postSplitsEmpty.gameObject.SetActive(true);
                for (int i = 0; i < _postSplitRows.Length; i++)
                {
                    if (_postSplitRows[i] != null) _postSplitRows[i].gameObject.SetActive(false);
                }
                return;
            }

            if (_postSplitsEmpty != null) _postSplitsEmpty.gameObject.SetActive(false);

            for (int i = 0; i < _postSplitRows.Length; i++)
            {
                if (_postSplitRows[i] == null) continue;
                if (i >= data.Splits.Count)
                {
                    _postSplitRows[i].gameObject.SetActive(false);
                    continue;
                }

                CsrSplitRow row = data.Splits[i];
                string myText = row.MyMs > 0 ? MatchController.FormatClockMs(row.MyMs) : CsrLoc.T("post.splits.missing");
                string oppText = row.OppMs > 0 ? MatchController.FormatClockMs(row.OppMs) : CsrLoc.T("post.splits.missing");

                string deltaText = "-";
                string deltaHex = "5A6270";
                if (row.MyMs > 0 && row.OppMs > 0)
                {
                    long delta = row.MyMs - row.OppMs;
                    if (delta < 0)
                    {
                        deltaText = "-" + MatchController.FormatClockMs(Math.Abs(delta));
                        deltaHex = "1E7E45";   // 我方更快 = 绿
                    }
                    else if (delta > 0)
                    {
                        deltaText = "+" + MatchController.FormatClockMs(delta);
                        deltaHex = "C62828";   // 我方更慢 = 红
                    }
                    else
                    {
                        deltaText = "±0.000";
                        deltaHex = "5A6270";
                    }
                }

                _postSplitRows[i].gameObject.SetActive(true);
                _postSplitRows[i].text = "<b>" + (i + 1) + "</b>   " + row.Label
                    + "   <pos=55%><b>" + myText + "</b></pos>"
                    + "   <pos=73%>" + oppText + "</pos>"
                    + "   <pos=90%><color=#" + deltaHex + "><b>" + deltaText + "</b></color></pos>";
            }
        }

        private void OnPostRematchClicked()
        {
            ClosePostMatch();
            if (Mc != null)
            {
                Mc.LeaveMatch();
                Mc.Queue1v1();
            }
        }

        private void OnPostMenuClicked()
        {
            ClosePostMatch();
            if (Mc != null) Mc.LeaveMatch();
            try
            {
                // 优先走原版返回机场, 避免多人大厅等待卡死
                EndScreen screen = UnityEngine.Object.FindAnyObjectByType<EndScreen>();
                if (screen != null)
                {
                    screen.ReturnToAirport();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[PeakCSR] 返回机场失败: " + ex.Message);
            }
        }

        public void ClosePostMatch()
        {
            if (_postRoot != null) _postRoot.gameObject.SetActive(false);
            _postOpen = false;
            _postPending = false;
        }

        // =========================================================
        // 玩家档案卡
        // =========================================================
        private void ShowProfileCard(CloudRankEntry entry)
        {
            if (_profileRoot == null) BuildProfileUi();
            if (_profileRoot == null) return;

            _profileRoot.gameObject.SetActive(true);
            _profileRoot.SetAsLastSibling();
            _profileOpen = true;
            NativeUiFactory.EnsureCanvasAlive();
            NativeUiFactory.EnsureTopMost();

            RenderProfile(entry.SteamId, BuildProfileFromEntry(entry));
            CloudLeaderboardService svc = CloudLeaderboardService.Instance;
            if (svc != null)
            {
                svc.RequestProfile(entry.SteamId, p =>
                {
                    if (p != null && _profileOpen && _profileSteamId == p.SteamId) RenderProfile(p.SteamId, p);
                });
            }
        }

        private static CloudProfile BuildProfileFromEntry(CloudRankEntry e)
        {
            return new CloudProfile
            {
                SteamId = e.SteamId,
                PlayerName = e.PlayerName,
                Nickname = e.Nickname,
                Elo = e.Elo,
                PeakElo = e.Elo,
                Wins = e.Wins,
                Losses = e.Losses,
                Streak = e.Streak,
                WinsAllPeak = e.WinsAllPeak,
                LossesAllPeak = e.LossesAllPeak,
                WinsHardcore = e.WinsHardcore,
                LossesHardcore = e.LossesHardcore,
                WinsSprint = e.WinsSprint,
                LossesSprint = e.LossesSprint,
                GamesAllPeak = e.GamesAllPeak,
                GamesHardcore = e.GamesHardcore,
                GamesSprint = e.GamesSprint,
                FavoriteMap = e.FavoriteMap,
                FavoriteAscent = e.FavoriteAscent,
                FavoriteFormat = e.FavoriteFormat,
                GlobalRank = e.Rank,
                Loaded = true
            };
        }

        private void RenderProfile(string steamId, CloudProfile p)
        {
            if (p == null) return;
            _profileSteamId = steamId;

            string tierHex = EloCalculator.GetTierHexColorOnLight(p.Elo);
            string tierName = EloCalculator.GetTierName(p.Elo);
            string name = p.DisplayName;
            if (string.IsNullOrEmpty(name)) name = CsrLoc.T("common.anonymous");

            // 是否是自己的档案决定昵称编辑区显隐
            string mySteamId = SteamIdentity.GetSteamId64();
            bool isSelf = !string.IsNullOrEmpty(mySteamId) && steamId == mySteamId;
            _profileIsSelf = isSelf;
            SetActive(_profileNickLabel, isSelf);
            SetActive(_profileNickInput, isSelf);
            SetActive(_profileNickSave, isSelf);
            SetActive(_profileNickHint, isSelf);
            SetActive(_profileBiomeLabel, isSelf);
            SetActive(_profileBiomeDropdown, isSelf);
            if (isSelf && _profileNickInput != null && !_profileNickInput.isFocused)
            {
                _profileNickInput.text = p.Nickname ?? "";
            }
            if (_profileBiomeDropdown != null && !_profileBiomeDropdown.IsOpen)
            {
                // p.FavoriteMap 现在是群系索引, 越界或未设置都按"未选择"处理
                int biomeIdx = p.FavoriteMap >= 0 && p.FavoriteMap < PcsrBiomes.Count ? p.FavoriteMap : -1;
                _profileBiomeDropdown.SetValue(biomeIdx, false);
            }

            if (_profileTitle != null)
            {
                _profileTitle.text = CsrLoc.T("profile.title", name);
            }
            if (_profileMeta != null)
            {
                string rank = p.GlobalRank > 0 ? ("#" + p.GlobalRank) : "-";
                string peakHex = EloCalculator.GetTierHexColorOnLight(p.PeakElo);
                _profileMeta.text = CsrLoc.T("profile.meta",
                    rank,
                    tierHex, tierName,
                    p.Elo,
                    peakHex, p.PeakElo);
            }
            if (_profileBody != null)
            {
                string streakHex = p.Streak > 0 ? "1E7E45" : (p.Streak < 0 ? "C62828" : "5A6270");
                _profileBody.text = CsrLoc.T("profile.body",
                    p.Wins, p.Losses, p.WinRatePercent, streakHex, p.Streak, p.TotalGames);
            }
            if (_profileModes != null)
            {
                _profileModes.text = CsrLoc.T("profile.modes",
                    p.WinsAllPeak + p.LossesAllPeak, p.WinsAllPeak, p.LossesAllPeak, p.ModeWinRatePercent(0),
                    p.WinsHardcore + p.LossesHardcore, p.WinsHardcore, p.LossesHardcore, p.ModeWinRatePercent(1),
                    p.WinsSprint + p.LossesSprint, p.WinsSprint, p.LossesSprint, p.ModeWinRatePercent(2));
            }
            if (_profileFav != null)
            {
                _profileFav.text = BuildFavoriteLine(p);
            }
        }

        // 最爱行: 群系玩家自选; 难度与模式由真实对局场次推导, 不做手选
        private string BuildFavoriteLine(CloudProfile p)
        {
            bool zh = CsrLoc.Current == CsrLanguage.ZhCn;
            string favMap = p.FavoriteMap >= 0 && p.FavoriteMap < PcsrBiomes.Count
                ? PcsrBiomes.Label(p.FavoriteMap, zh)
                : CsrLoc.T("profile.none");

            int favAscent = p.FavoriteAscent;
            string favAsc = favAscent > -900
                ? MatchController.GetAscentDisplayName(favAscent)
                : CsrLoc.T("profile.none");

            string favFmt = p.FavoriteFormat >= 0
                ? MatchController.GetFormatDisplayName((McsrRuleFormat)p.FavoriteFormat, 0)
                : CsrLoc.T("profile.none");

            return CsrLoc.T("profile.favorite",
                "<color=#0B5FA5><b>" + favMap + "</b></color>",
                "<color=#5A6270><b>" + favAsc + "</b></color>",
                "<color=#5A6270><b>" + favFmt + "</b></color>");
        }

        // 群系下拉变化: 写入本地并同步云端 (难度/模式沿用当前推导值)
        private void OnBiomeChanged(int biomeIndex)
        {
            if (!_profileIsSelf) return;

            EloCalculator.SetFavoriteMap(biomeIndex);
            CloudLeaderboardService svc = CloudLeaderboardService.Instance;
            if (svc != null)
            {
                svc.PushFavoriteProfile(biomeIndex, EloCalculator.FavoriteAscent, EloCalculator.FavoriteFormat);
            }
            if (_profileFav != null)
            {
                CloudProfile preview = new CloudProfile
                {
                    FavoriteMap = biomeIndex,
                    FavoriteAscent = EloCalculator.FavoriteAscent,
                    FavoriteFormat = EloCalculator.FavoriteFormat
                };
                _profileFav.text = BuildFavoriteLine(preview);
            }
        }

        private void BuildProfileUi()
        {
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return;

            GameObject rootGo = new GameObject("PcsrProfileRoot", typeof(RectTransform));
            rootGo.transform.SetParent(canvas.transform, false);
            _profileRoot = (RectTransform)rootGo.transform;
            _profileRoot.anchorMin = Vector2.zero;
            _profileRoot.anchorMax = Vector2.one;
            _profileRoot.offsetMin = Vector2.zero;
            _profileRoot.offsetMax = Vector2.zero;

            PeakWindowStyle.CreateBackdrop(_profileRoot, "ProfileBackdrop");

            // 高度要给"群系下拉 + 底部关闭按钮"留出足够间距, 否则展开时会压到按钮
            const float w = 780f;
            const float h = 700f;
            const float titleH = 74f;
            _profileWindow = PeakWindowStyle.CreateWindow(_profileRoot, "PcsrProfile", w, h, titleH,
                CsrLoc.T("profile.window.title"), "");

            TextMeshProUGUI title = _profileWindow.Title;
            if (title != null)
            {
                RectTransform rt = title.transform as RectTransform;
                if (rt != null) NativeUiFactory.Place(rt, 30f, 0f, w - 60f - 110f, titleH);
                title.textWrappingMode = TextWrappingModes.NoWrap;
                title.overflowMode = TextOverflowModes.Overflow;
                title.alignment = TextAlignmentOptions.Left;
            }

            RectTransform body = _profileWindow.Body;
            float inner = w - 52f;

            _profileTitle = CreateText(body, "ProfName", "", 28f, PeakWindowStyle.Heading, 26f, 10f, inner, 38f);

            // 顶部信息行: 当前分数 ELO | 历史最高分 ELO (整串富文本上色)
            _profileMeta = CreateText(body, "ProfMeta", "", 18f, PeakWindowStyle.TextPrimary, 26f, 50f, inner, 26f);

            PeakWindowStyle.CreateSeparator(body, "ProfSep1", 26f, 82f, inner);

            _profileBody = CreateText(body, "ProfBody", "", 18f, PeakWindowStyle.TextPrimary, 26f, 94f, inner, 28f);

            PeakWindowStyle.CreateSeparator(body, "ProfSep2", 26f, 130f, inner);

            _profileModes = CreateText(body, "ProfModes", "", 17f, PeakWindowStyle.TextPrimary, 26f, 142f, inner, 110f, wrap: true);

            PeakWindowStyle.CreateSeparator(body, "ProfSep3", 26f, 258f, inner);

            _profileFav = CreateText(body, "ProfFav", "", 17f, PeakWindowStyle.TextMuted, 26f, 268f, inner, 44f, wrap: true);

            PeakWindowStyle.CreateSeparator(body, "ProfSep4", 26f, 318f, inner);

            // ---- 昵称编辑区 (仅自己的档案可见) ----
            _profileNickLabel = CreateText(body, "ProfNickLabel", CsrLoc.T("profile.nick.label"),
                18f, PeakWindowStyle.Heading, 26f, 328f, inner, 26f);
            _profileNickInput = PeakWindowStyle.CreateInputField(body, "ProfNickInput", "", 26f, 356f, 330f, 40f, 18f);
            // 昵称允许中英文/数字/空格等, 不能用 CreateInputField 默认的纯数字校验
            ConfigurePlainInput(_profileNickInput, 16, false);
            _profileNickSave = PeakWindowStyle.CreatePill(body, "ProfNickSave", CsrLoc.T("profile.nick.save"),
                366f, 356f, 150f, 40f, 17f);
            _profileNickSave.onClick.AddListener(new UnityAction(OnSaveNicknameClicked));

            _profileNickHint = CreateText(body, "ProfNickHint", CsrLoc.T("profile.nick.hint"),
                14f, PeakWindowStyle.TextMuted, 26f, 402f, inner, 34f, wrap: true);

            // ---- 最爱群系: 玩家唯一可自选的项目 (难度/模式按真实对局自动统计) ----
            _profileBiomeLabel = CreateText(body, "ProfBiomeLabel", CsrLoc.T("profile.biome.label"),
                18f, PeakWindowStyle.Heading, 26f, 444f, inner, 26f);
            _profileBiomeDropdown = PcsrDropdown.Create(
                body, "ProfBiomeDropdown", 26f, 472f, 300f, 40f,
                PcsrBiomes.Labels(CsrLoc.Current == CsrLanguage.ZhCn), -1, 17f);
            _profileBiomeDropdown.OnValueChanged = OnBiomeChanged;
            _profileBiomeDropdown.SetValue(-1, false);

            // 关闭按钮放到右下角, 与群系下拉保持距离; 展开的列表最多向下延伸 11 行, 也不会压到它
            Button close = PeakWindowStyle.CreatePill(body, "ProfClose", CsrLoc.T("win.btn.ok"),
                w - 52f - 200f, h - titleH - 14f - 62f, 200f, 46f, 18f);
            close.onClick.AddListener(new UnityAction(CloseProfileCard));

            _profileRoot.gameObject.SetActive(false);
        }

        // 昵称清洗: 与 worker.js 的 sanitizeNickname 对齐, 本地先过滤一遍减少无效请求
        internal static string SanitizeNickname(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = System.Text.RegularExpressions.Regex.Replace(raw,
                "</?(?:align|allcaps|alpha|b|br|color|cspace|font|gradient|i|indent|line-height|line-indent|link|lowercase|margin|mark|material|mspace|nobr|noparse|page|pos|quad|rotate|s|size|smallcaps|space|sprite|strikethrough|style|sub|sup|u|uppercase|voffset|width)(?:[=\\s][^<>]*)?>",
                "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            System.Text.StringBuilder sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (char.IsControl(c) || c == '|')
                {
                    sb.Append(' ');
                    continue;
                }
                if (char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.' || c == '·')
                {
                    sb.Append(c);
                }
            }
            s = System.Text.RegularExpressions.Regex.Replace(sb.ToString().Trim(), "\\s{2,}", " ");
            if (s.Length > 16) s = s.Substring(0, 16).Trim();
            if (s.Length > 0 && s.Length < 2) return "";
            return s;
        }

        private void OnMyProfileClicked()
        {
            string mySteamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(mySteamId))
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.window.title"), CsrLoc.T("profile.no_steam"));
                return;
            }

            CloudLeaderboardService svc = CloudLeaderboardService.Instance;
            if (svc == null)
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.window.title"), CsrLoc.T("profile.steam_only"));
                return;
            }

            if (_profileRoot == null) BuildProfileUi();
            if (_profileRoot == null) return;

            _profileRoot.gameObject.SetActive(true);
            _profileRoot.SetAsLastSibling();
            _profileOpen = true;
            _profileIsSelf = true;
            _profileSteamId = mySteamId;
            SetActive(_profileNickLabel, true);
            SetActive(_profileNickInput, true);
            SetActive(_profileNickSave, true);
            SetActive(_profileNickHint, true);
            SetActive(_profileBiomeLabel, true);
            SetActive(_profileBiomeDropdown, true);
            NativeUiFactory.EnsureCanvasAlive();
            NativeUiFactory.EnsureTopMost();

            // 先用本地已知数据铺底, 云端回来后再覆盖
            CloudProfile cached = svc.CachedProfile(mySteamId);
            if (cached != null) RenderProfile(mySteamId, cached);

            svc.RequestMyProfile(p =>
            {
                if (p != null && _profileOpen && _profileSteamId == p.SteamId) RenderProfile(p.SteamId, p);
            });
        }

        private void OnSaveNicknameClicked()
        {
            if (!_profileIsSelf || _profileNickBusy) return;

            string mySteamId = SteamIdentity.GetSteamId64();
            if (string.IsNullOrEmpty(mySteamId))
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.window.title"), CsrLoc.T("profile.no_steam"));
                return;
            }
            CloudLeaderboardService svc = CloudLeaderboardService.Instance;
            if (svc == null || !CloudLeaderboardService.IsCloudConfigured())
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.window.title"), CsrLoc.T("profile.nick.offline"));
                return;
            }

            string raw = _profileNickInput != null ? _profileNickInput.text : "";
            string nick = SanitizeNickname(raw);
            if (!string.IsNullOrEmpty(raw) && string.IsNullOrEmpty(nick))
            {
                // 全是非法字符/长度不足, 直接拒绝
                PeakCsrPrompt.Info(CsrLoc.T("profile.nick.label"), CsrLoc.T("profile.nick.failed", "invalid"));
                return;
            }

            _profileNickBusy = true;
            if (_profileNickSave != null) _profileNickSave.interactable = false;

            int favMap = EloCalculator.FavoriteMap;
            int favAsc = EloCalculator.FavoriteAscent;
            int favFmt = EloCalculator.FavoriteFormat;
            svc.PushNickname(nick, favMap, favAsc, favFmt, (ok, err) =>
            {
                _profileNickBusy = false;
                if (_profileNickSave != null) _profileNickSave.interactable = true;
                if (ok)
                {
                    PeakCsrPrompt.Info(CsrLoc.T("profile.nick.label"), CsrLoc.T("profile.nick.saved"));
                    if (Mc != null) RefreshAll();
                    return;
                }
                PeakCsrPrompt.Info(CsrLoc.T("profile.nick.label"),
                    CsrLoc.T("profile.nick.failed", DescribeProfileError(err)));
            });
        }

        private static string DescribeProfileError(string err)
        {
            if (string.IsNullOrEmpty(err)) return "unknown";
            if (err.StartsWith("Nickname cooldown:", StringComparison.Ordinal))
            {
                string h = err.Substring("Nickname cooldown:".Length);
                return CsrLoc.Current == CsrLanguage.ZhCn
                    ? ("昵称 " + h + " 小时后再试")
                    : ("cooldown, retry in " + h + "h");
            }
            if (err == "Nickname already taken")
            {
                return CsrLoc.Current == CsrLanguage.ZhCn ? "该昵称已被占用" : "nickname already taken";
            }
            return err;
        }

        public void CloseProfileCard()
        {
            if (_profileBiomeDropdown != null) _profileBiomeDropdown.Close();
            if (_profileRoot != null) _profileRoot.gameObject.SetActive(false);
            _profileOpen = false;
            _profileSteamId = "";
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

            const float w = 780f;
            const float h = 400f;
            PeakWindowStyle.WindowParts win = PeakWindowStyle.CreateWindow(
                rootRt, "PcsrPrompt", w, h, 74f, title, "");

            TextMeshProUGUI titleText = win.Title;
            if (titleText != null)
            {
                RectTransform titleRt = titleText.transform as RectTransform;
                if (titleRt != null)
                {
                    NativeUiFactory.Place(titleRt, 30f, 0f, w - 60f - 110f, 74f);
                }
                titleText.textWrappingMode = TextWrappingModes.NoWrap;
                titleText.overflowMode = TextOverflowModes.Overflow;
                titleText.alignment = TextAlignmentOptions.Left;
            }

            PeakWindowStyle.CreateScrollingText(
                win.Body, "Body", message, 21f, PeakWindowStyle.TextPrimary,
                26f, 16f, w - 52f, h - 74f - 100f, 2f, 6f);

            float btnW = 180f;
            float totalW = options.Length * btnW + (options.Length - 1) * 14f;
            float startX = (w - totalW) / 2f;
            for (int i = 0; i < options.Length; i++)
            {
                Option opt = options[i];
                Button btn = PeakWindowStyle.CreatePill(
                    win.Body, "PromptBtn" + i, opt.Label,
                    startX + i * (btnW + 14f), h - 74f - 88f, btnW, 46f, 19f);
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
