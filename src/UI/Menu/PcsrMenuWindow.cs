using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 排位中心主窗口骨架: 窗口外框、标题栏与打开/关闭生命周期 (< 250 行)
    // 内部 5 大业务分区由 RulesetPanel / MatchStatusPanel / LiveQueuePanel /
    // MatchHistoryPanel / LeaderboardPanel 承担; 语言切换原地刷新不销毁 UI 树
    public class PcsrMenuWindow : MonoBehaviour
    {
        private const float WindowWidth = 1300f;
        private const float WindowHeight = 1090f;
        private const float TitleBarHeight = 82f;
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

        private static PcsrMenuWindow _instance;
        public static PcsrMenuWindow Instance { get { return _instance; } }

        private RectTransform _root;
        private PeakWindowStyle.WindowParts _window;
        private RectTransform _body;
        private bool _built;
        private bool _open;
        private float _nextRefresh;

        // 5 大子面板
        private RulesetPanel _rulesPanel;
        private MatchStatusPanel _statusPanel;
        private LiveQueuePanel _queuePanel;
        private MatchHistoryPanel _historyPanel;
        private LeaderboardPanel _boardPanel;

        // 标题栏上的语言切换按钮
        private TMPro.TextMeshProUGUI _langBtnLabel;
        private Button _langBtn;

        public bool IsOpen { get { return _open; } }

        private void Awake()
        {
            _instance = this;
            EventBus.OnMatchStateChanged += OnMatchStateChanged;
            CsrLoc.OnLanguageChanged += OnLanguageChanged;
        }

        private void OnDestroy()
        {
            EventBus.OnMatchStateChanged -= OnMatchStateChanged;
            CsrLoc.OnLanguageChanged -= OnLanguageChanged;
        }

        // 一旦真正进入跑图阶段就收起窗口, 让原生 HUD 完整可见
        private void OnMatchStateChanged()
        {
            if (!_open) return;
            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc == null) return;
            if (mc.IsInGamePhase)
            {
                Close();
            }
        }

        // 原地热切语言: 刷新全部 LocalizedTextBinder 绑定与动态数据, 不再 Destroy 重建整棵 UI 树
        private void OnLanguageChanged()
        {
            if (!_built) return;
            LocalizedTextBinder.RefreshAll();
            if (_window.Subtitle != null)
            {
                _window.Subtitle.text = CsrLoc.T("win.subtitle");
            }
            if (_langBtnLabel != null)
            {
                _langBtnLabel.text = CsrLoc.Current == CsrLanguage.ZhCn ? "中文 / EN" : "EN / 中文";
            }
            if (_open) RefreshAll();
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

        // F10 热键入口: 真正的开/关切换 (窗口开着就关)
        public static void ToggleWindow()
        {
            if (_instance == null)
            {
                ShowWindow();
                return;
            }
            if (_instance._open) _instance.Close();
            else _instance.Open();
        }

        public void Open()
        {
            UiKit.EnsureEventSystem();
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
            _rulesPanel = new RulesetPanel(this, _body, LeftX, Row1Y, LeftW, Row1H);
            _statusPanel = new MatchStatusPanel(this, _body, RightX, Row1Y, RightW, Row1H);
            _queuePanel = new LiveQueuePanel(this, _body, LeftX, Row2Y, LeftW, Row2H);
            _historyPanel = new MatchHistoryPanel(this, _body, RightX, Row2Y, RightW, Row2H);
            _boardPanel = new LeaderboardPanel(this, _body, LeftX, Row3Y, FullW, Row3H);

            _built = true;
            _root.gameObject.SetActive(false);
        }

        private void BuildTitleBar()
        {
            Button close = PeakWindowStyle.CreatePill(
                _window.TitleBar, "BtnClose", "X",
                WindowWidth - 14f - 72f, 20f, 48f, 42f, 24f);
            UiKit.TintOnBar(close);
            close.onClick.AddListener(new UnityAction(Close));

            Button help = PeakWindowStyle.CreatePill(
                _window.TitleBar, "BtnHelp", CsrLoc.T("win.btn.help"),
                WindowWidth - 14f - 72f - 12f - 178f, 20f, 178f, 42f, 19f);
            UiKit.TintOnBar(help);
            help.onClick.AddListener(new UnityAction(OnHelpClicked));
            LocalizedTextBinder.BindPill(help, "win.btn.help");

            // 我的档案: 天梯表格只显示 Top N, 自己不在榜上时也要能进档案页改昵称
            Button profile = PeakWindowStyle.CreatePill(
                _window.TitleBar, "BtnProfile", CsrLoc.T("win.btn.profile"),
                WindowWidth - 14f - 72f - 12f - 178f - 12f - 178f, 20f, 178f, 42f, 19f);
            UiKit.TintOnBar(profile);
            profile.onClick.AddListener(new UnityAction(OnMyProfileClicked));
            LocalizedTextBinder.BindPill(profile, "win.btn.profile");

            // 语言切换 Pill: 紧挨左侧 "PEAK CSR" 标题 (标题 40px 约 200px 宽),
            // "中文 / EN" 19px 约 90px 宽, 110 足够且右缘 360 不碰 416 起的副标题
            Button lang = PeakWindowStyle.CreatePill(
                _window.TitleBar, "BtnLang", "中文 / EN", 250f, 20f, 110f, 42f, 19f);
            UiKit.TintOnBar(lang);
            lang.onClick.AddListener(new UnityAction(OnLangClicked));
            _langBtn = lang;
            _langBtnLabel = lang.GetComponentInChildren<TMPro.TextMeshProUGUI>(true);
        }

        private void OnLangClicked()
        {
            CsrLoc.ToggleLanguage();
        }

        private void OnHelpClicked()
        {
            PeakCsrPrompt.Info(CsrLoc.T("win.help.title"), CsrLoc.T("win.help.body"));
        }

        private void OnMyProfileClicked()
        {
            ProfileCardView.ShowMyProfile();
        }

        // 供 LeaderboardPanel 打开他人档案
        internal void OpenProfileCard(CloudRankEntry entry)
        {
            ProfileCardView.ShowProfileByEntry(entry);
        }

        // =========================================================
        // 刷新
        // =========================================================

        public void RefreshAll()
        {
            RefreshSelection();
            RefreshDynamic();

            // 打开/刷新面板时顺手把待补传战报推一次
            LadderSyncService sync = LadderSyncService.Instance;
            if (sync != null) sync.DrainPendingReports();
        }

        private void RefreshSelection()
        {
            if (_rulesPanel != null) _rulesPanel.RefreshSelection();
        }

        public void RefreshDynamic()
        {
            MatchCoordinator mc = MatchCoordinator.Instance;
            if (_statusPanel != null) _statusPanel.RefreshStatus(mc);
            if (_queuePanel != null) _queuePanel.Refresh(mc);
            if (_statusPanel != null) _statusPanel.RefreshMatchInfo(mc);
            if (_statusPanel != null) _statusPanel.RefreshActionButtons(mc);
            if (_historyPanel != null) _historyPanel.Refresh(mc);
            if (_boardPanel != null) _boardPanel.Refresh(mc);
        }
    }
}
