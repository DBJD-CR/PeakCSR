using PeakCSR.Practice;
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
        private const float RefreshInterval = 0.2f;

        // 布局常量全部收敛到 MenuLayout, 这里只做别名
        private const float WindowWidth = MenuLayout.WindowWidth;
        private const float WindowHeight = MenuLayout.WindowHeight;
        private const float TitleBarHeight = MenuLayout.TitleBarHeight;
        private const float ColGap = MenuLayout.ColGap;
        private const float LeftX = MenuLayout.LeftX;
        private const float LeftW = MenuLayout.LeftW;
        private const float RightX = MenuLayout.RightX;
        private const float RightW = MenuLayout.RightW;
        private const float Row1Y = MenuLayout.Row1Y;
        private const float Row1H = MenuLayout.Row1H;
        private const float Row2Y = MenuLayout.Row2Y;
        private const float Row2H = MenuLayout.Row2H;
        private const float Row3Y = MenuLayout.Row3Y;
        private const float Row3H = MenuLayout.Row3H;
        private const float Row3W = MenuLayout.Row3W;
        private const float HistoryH = MenuLayout.HistoryH;

        private static PcsrMenuWindow _instance;
        public static PcsrMenuWindow Instance { get { return _instance; } }

        private RectTransform _root;
        private PeakWindowStyle.WindowParts _window;
        private RectTransform _body;
        private bool _built;
        private bool _open;
        private float _nextRefresh;

        // 5 大子面板 (练习入口内嵌在 RulesetPanel 的按钮行, 状态复用 MatchStatusPanel)
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

        // 进入跑图阶段时自动收起, 但每局只收一次
        private bool _autoClosedForThisMatch;

        private void OnMatchStateChanged()
        {
            if (!_open) return;
            MatchCoordinator mc = MatchCoordinator.Instance;
            PracticeCoordinator pc = PracticeCoordinator.Instance;
            bool practicing = pc != null && pc.IsActive && pc.Phase == PracticePhase.Running;
            bool inGame = practicing || (mc != null && mc.IsInGamePhase);

            // 阶段恢复为非对局: 复位标记, 让下一局仍能享受一次自动收起
            if (!inGame)
            {
                _autoClosedForThisMatch = false;
                return;
            }
            if (_autoClosedForThisMatch) return;
            _autoClosedForThisMatch = true;
            Close();
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
                CsrLoc.T("app.brand"), CsrLoc.T("win.subtitle"));
            _body = _window.Body;

            BuildTitleBar();
            _rulesPanel = new RulesetPanel(this, _body, LeftX, Row1Y, LeftW, Row1H);
            _statusPanel = new MatchStatusPanel(this, _body, RightX, Row1Y, RightW, Row1H);
            _queuePanel = new LiveQueuePanel(this, _body, LeftX, Row2Y, LeftW, Row2H);
            // 历史战绩纵向贯穿 Row2 + Row3, 与左列天梯底边对齐
            _historyPanel = new MatchHistoryPanel(this, _body, RightX, Row2Y, RightW, HistoryH);
            // 天梯速览收成单列宽, 吃掉实时匹配下方的空白区
            _boardPanel = new LeaderboardPanel(this, _body, LeftX, Row3Y, Row3W, Row3H);

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

            // 语言切换 Pill: 紧挨左侧品牌标题
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
            // 练习开关的显隐随"计分/练习"模式与练习态变化, 走高频刷新即时反映
            if (_rulesPanel != null) _rulesPanel.RefreshPracticeButton();
            if (_statusPanel != null) _statusPanel.RefreshStatus(mc);
            if (_queuePanel != null) _queuePanel.Refresh(mc);
            if (_statusPanel != null) _statusPanel.RefreshMatchInfo(mc);
            if (_statusPanel != null) _statusPanel.RefreshActionButtons(mc);
            if (_historyPanel != null) _historyPanel.Refresh(mc);
            if (_boardPanel != null) _boardPanel.Refresh(mc);
        }

        // 供 LeaderboardPanel 打开完整天梯独立窗口
        internal void OpenFullBoard()
        {
            LadderBoardView.ShowWindow();
        }

        // 供 MatchHistoryPanel 打开战报回看
        internal void OpenHistoryReport(CsrHistoryEntry entry)
        {
            HistoryReportView.Show(entry);
        }
    }
}
