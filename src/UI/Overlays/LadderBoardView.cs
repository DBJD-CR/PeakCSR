using System.Collections.Generic;
using RankedPeak.Services;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 完整天梯榜独立窗口
    //
    // 与 ProfileCardView / PostMatchReportView 同一套范式: 单例宿主 + 懒构建 + 顶层置顶。
    // 表体用 PcsrScrollList 做虚拟滚动, 行池只建可见行数, 数据可以到 50+。
    public sealed class LadderBoardView : MonoBehaviour
    {
        private const float BoxPad = MenuLayout.BoxPad;
        private const float RowH = 40f;
        private const float VisibleRows = 15f;

        // 列坐标 (相对内容区左边缘, 按 1248 宽重新分配)
        // ELO 水平居中
        private const float ColRankX = 0f;
        private const float ColRankW = 76f;
        private const float ColNameX = 76f;
        private const float ColNameW = 330f;
        private const float ColTierX = 406f;
        private const float ColTierW = 190f;
        private const float ColEloX = 604f;
        private const float ColEloW = 110f;
        private const float ColRecordX = 740f;
        private const float ColRecordW = 178f;
        private const float ColModesX = 934f;
        private const float ColModesW = 314f;

        // 昵称前的国旗占位: 名称列内容左缩
        private const float FlagGap = 6f;
        private const float NameInset = FlagSprites.IconWidth + FlagGap;

        private static LadderBoardView _instance;

        private RectTransform _root;
        private PeakWindowStyle.WindowParts _window;
        private PcsrScrollList _list;
        private TextMeshProUGUI _lblMeta;
        private TextMeshProUGUI _lblEmpty;
        private TextMeshProUGUI[] _headerCells;

        // 行内控件引用 (按行池索引)
        private readonly List<RowWidgets> _rows = new List<RowWidgets>();
        private readonly List<CloudRankEntry> _entries = new List<CloudRankEntry>();
        private bool _open;

        private sealed class RowWidgets
        {
            public TextMeshProUGUI Rank;
            // 国旗图标: 无国籍时整体隐藏, 名称列同时左移回去
            public Image Flag;
            public RectTransform FlagRt;
            public TextMeshProUGUI Name;
            public RectTransform NameRt;
            public TextMeshProUGUI Tier;
            public TextMeshProUGUI Elo;
            public TextMeshProUGUI Record;
            public TextMeshProUGUI Modes;
            public Image Stripe;
        }

        public static void ShowWindow()
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("PeakCSR_LadderBoard");
                DontDestroyOnLoad(go);
                go.AddComponent<LadderBoardView>();
            }
            if (_instance != null) _instance.Open();
        }

        private void Awake()
        {
            _instance = this;
            CsrLoc.OnLanguageChanged += OnLanguageChanged;
        }

        private void OnDestroy()
        {
            CsrLoc.OnLanguageChanged -= OnLanguageChanged;
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        private void OnLanguageChanged()
        {
            if (!_open) return;
            LocalizedTextBinder.RefreshAll();
            RefreshHeaderText();
            // 语言变了必须重绘, 指纹里已含语言, 这里清一次保险
            _dirtySeen = false;
            Refresh();
        }

        private void Open()
        {
            UiKit.EnsureEventSystem();
            NativeUiFactory.GetCanvas();
            if (_root == null) BuildUi();
            if (_root == null) return;

            _root.gameObject.SetActive(true);
            _root.SetAsLastSibling();
            _open = true;
            NativeUiFactory.EnsureCanvasAlive();
            NativeUiFactory.EnsureTopMost();

            // 打开即拉一次, 之后交给手动刷新按钮
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null) svc.RefreshLeaderboard();
            Refresh();
        }

        private void Close()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            _open = false;
        }

        private void Update()
        {
            if (!_open) return;
            if (Input.GetKeyDown(KeyCode.Escape)) Close();
            // 数据回来后刷新一次即可; 这里用低频轮询兜住异步拉取
            if (Time.unscaledTime < _nextRefresh) return;
            _nextRefresh = Time.unscaledTime + 0.5f;
            Refresh();
        }

        private float _nextRefresh;
        // 重绘脏标记: 条数/刷新时间/语言任一变化才重绘
        private int _boundCount = -1;
        private long _boundClock = -1;
        private CsrLanguage _boundLang = CsrLanguage.ZhCn;
        private bool _dirtySeen;
        private bool _emptyVisibleDirty = true;

        private void BuildUi()
        {
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return;

            GameObject rootGo = new GameObject("PcsrBoardRoot", typeof(RectTransform));
            rootGo.transform.SetParent(canvas.transform, false);
            _root = (RectTransform)rootGo.transform;
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;

            PeakWindowStyle.CreateBackdrop(_root, "BoardBackdrop");

            const float w = MenuLayout.BoardWindowWidth;
            const float h = MenuLayout.BoardWindowHeight;
            const float titleH = MenuLayout.BoardTitleBarHeight;
            _window = PeakWindowStyle.CreateWindow(_root, "PcsrBoard", w, h, titleH,
                CsrLoc.T("win.board.window.title"), "");

            TextMeshProUGUI title = _window.Title;
            if (title != null)
            {
                RectTransform rt = title.transform as RectTransform;
                if (rt != null) NativeUiFactory.Place(rt, 30f, 0f, w - 60f - 130f, titleH);
                title.textWrappingMode = TextWrappingModes.NoWrap;
                title.overflowMode = TextOverflowModes.Overflow;
                title.alignment = TextAlignmentOptions.Left;
                LocalizedTextBinder.Bind(title, "win.board.window.title");
            }

            // 右上关闭
            Button close = PeakWindowStyle.CreatePill(_window.TitleBar, "BoardClose", "X",
                w - 14f - 72f, 20f, 48f, 42f, 24f);
            UiKit.TintOnBar(close);
            close.onClick.AddListener(new UnityAction(Close));

            RectTransform body = _window.Body;
            float inner = w - 52f;

            // 工具栏: 状态 + 刷新
            _lblMeta = UiKit.CreateText(body, "BoardLoadedMeta", "", 17f, PeakWindowStyle.TextMuted,
                26f, 8f, inner - 220f, 26f);
            Button refresh = PeakWindowStyle.CreatePill(body, "BoardRefresh", CsrLoc.T("hud.btn.refresh_board"),
                w - 52f - 200f, 4f, 200f, 36f, 17f);
            refresh.onClick.AddListener(new UnityAction(OnRefreshClicked));
            LocalizedTextBinder.BindPill(refresh, "hud.btn.refresh_board");

            // 表头: 与数据行同一套列常量 + 同样的对齐方式
            BuildTableHeader(body);
            PeakWindowStyle.CreateSeparator(body, "BoardHeaderSep", 26f, 72f, inner);

            // 表体: 虚拟滚动
            _list = PcsrScrollList.Create(body, "BoardList", 26f, 82f, inner,
                h - titleH - 82f - 30f, RowH, Mathf.RoundToInt(VisibleRows));

            _list.SetRowBinder(delegate(int idx, RectTransform rowRt)
            {
                EnsureRowWidgets(idx, rowRt);
            });
            _list.SetClickHandler(OnRowClicked);
            // 滚动后重绘可见行, 否则会看到旧排名
            _list.SetScrollChangedHandler(FillRows);

            _lblEmpty = UiKit.CreateText(body, "BoardEmptyBig", CsrLoc.T("win.board.empty"),
                20f, PeakWindowStyle.TextMuted, 26f, 96f, inner, 30f);
            LocalizedTextBinder.Bind(_lblEmpty, "win.board.empty");

            _root.gameObject.SetActive(false);
        }

        private void EnsureRowWidgets(int idx, RectTransform rowRt)
        {
            while (_rows.Count <= idx)
            {
                RowWidgets rw = new RowWidgets();
                int i = _rows.Count;

                // 斑马纹底色: 每个池行都建, 显隐由数据索引决定
                Image stripe = PeakWindowStyle.CreateSurface(rowRt, "Stripe",
                    new Color(0.86f, 0.89f, 0.88f, 1f), 5);
                RectTransform strt = (RectTransform)stripe.transform;
                strt.anchorMin = Vector2.zero;
                strt.anchorMax = Vector2.one;
                strt.offsetMin = Vector2.zero;
                strt.offsetMax = Vector2.zero;
                stripe.raycastTarget = false;
                rw.Stripe = stripe;

                // 国旗先建, 保证在名称文字下层; 位置固定在名称列左缘
                GameObject flagGo = new GameObject("Flag", typeof(RectTransform), typeof(Image));
                flagGo.transform.SetParent(rowRt, false);
                Image flag = flagGo.GetComponent<Image>();
                flag.type = Image.Type.Simple;
                flag.color = Color.white;
                RectTransform flagRt = (RectTransform)flag.transform;
                flagRt.anchorMin = new Vector2(0f, 0.5f);
                flagRt.anchorMax = new Vector2(0f, 0.5f);
                flagRt.pivot = new Vector2(0f, 0.5f);
                flagRt.sizeDelta = new Vector2(FlagSprites.IconWidth, FlagSprites.IconHeight);
                flagRt.anchoredPosition = new Vector2(ColNameX, 0f);
                flag.raycastTarget = false;
                flag.preserveAspect = true;
                flag.enabled = false;
                rw.Flag = flag;
                rw.FlagRt = flagRt;

                rw.Rank = MakeCell(rowRt, "Rank", ColRankX, ColRankW, false, 20f);
                rw.Name = MakeCell(rowRt, "Name", ColNameX, ColNameW, false, 20f);
                rw.NameRt = (RectTransform)rw.Name.transform;
                rw.Tier = MakeCell(rowRt, "Tier", ColTierX, ColTierW, false, 19f);
                // ELO 改为居中, 表头同样走 Center
                rw.Elo = MakeCell(rowRt, "Elo", ColEloX, ColEloW, TextAlignmentOptions.Center, 21f);
                rw.Record = MakeCell(rowRt, "Record", ColRecordX, ColRecordW, false, 18f);
                rw.Modes = MakeCell(rowRt, "Modes", ColModesX, ColModesW, false, 17f);
                _rows.Add(rw);
            }
        }

        // 重载: 右对齐/左对齐二态
        private static TextMeshProUGUI MakeCell(RectTransform row, string name, float x, float w, bool right, float size)
        {
            return MakeCell(row, name, x, w,
                right ? TextAlignmentOptions.Right : TextAlignmentOptions.Left, size);
        }

        private static TextMeshProUGUI MakeCell(RectTransform row, string name, float x, float w,
            TextAlignmentOptions alignment, float size)
        {
            TextMeshProUGUI t = PeakWindowStyle.CreateText(row, name, "", size, PeakWindowStyle.TextPrimary,
                alignment);
            RectTransform rt = (RectTransform)t.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(x, 0f);
            rt.offsetMax = new Vector2(x + w, 0f);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Ellipsis;
            t.margin = Vector4.zero;
            return t;
        }

        private void OnRefreshClicked()
        {
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null) svc.RefreshLeaderboard();
            Refresh();
        }

        private void OnRowClicked(int dataIndex)
        {
            if (dataIndex < 0 || dataIndex >= _entries.Count) return;
            CloudRankEntry e = _entries[dataIndex];
            if (e == null || string.IsNullOrEmpty(e.SteamId)) return;
            ProfileCardView.ShowProfileByEntry(e);
        }

        private void Refresh()
        {
            UiKit.Safe(delegate
            {
                LadderSyncService svc = LadderSyncService.Instance;
                int incoming = svc != null && svc.Entries != null ? svc.Entries.Count : 0;
                // 0.5s 轮询兜异步拉取
                long clock = svc != null ? svc.LastRefreshedAtMs : 0;
                if (_dirtySeen && incoming == _boundCount && clock == _boundClock && CsrLoc.Current == _boundLang
                    && !_emptyVisibleDirty)
                {
                    return;
                }
                _dirtySeen = true;
                _boundCount = incoming;
                _boundClock = clock;
                _boundLang = CsrLoc.Current;
                _emptyVisibleDirty = false;

                _entries.Clear();
                if (svc != null && svc.Entries != null) _entries.AddRange(svc.Entries);

                if (_lblMeta != null)
                {
                    string clockText = svc != null
                        ? CloudLadderClient.FormatRefreshClock(svc.LastRefreshedAtMs)
                        : "--:--:--";
                    _lblMeta.text = CsrLoc.T("cloud.updated", _entries.Count) + "   "
                        + CsrLoc.T("win.queue.refreshed", clockText);
                }
                if (_lblEmpty != null) _lblEmpty.gameObject.SetActive(_entries.Count == 0);
                if (_list == null) return;

                _list.SetItemCount(_entries.Count);
                FillRows();
            });
        }

        private void FillRows()
        {
            string mySteamId = "";
            try { mySteamId = SteamIdentity.GetSteamId64(); } catch { }

            // 与 PcsrScrollList 的虚拟起点保持一致
            int first = _list != null ? _list.VisibleStartIndex : 0;

            for (int i = 0; i < _rows.Count; i++)
            {
                RowWidgets rw = _rows[i];
                int dataIndex = first + i;
                bool has = dataIndex < _entries.Count;
                if (rw.Name == null) continue;

                // 条纹按"数据行的奇偶"决定, 不能按池行索引:
                // 池行只代表视口内第几格, 滚动时同一格会换数据, 条纹会跟着跳
                if (rw.Stripe != null)
                {
                    bool striped = has && (dataIndex & 1) == 1;
                    if (rw.Stripe.enabled != striped) rw.Stripe.enabled = striped;
                }

                if (!has)
                {
                    if (rw.Rank != null) rw.Rank.text = "";
                    rw.Name.text = "";
                    if (rw.Tier != null) rw.Tier.text = "";
                    if (rw.Elo != null) rw.Elo.text = "";
                    if (rw.Record != null) rw.Record.text = "";
                    if (rw.Modes != null) rw.Modes.text = "";
                    if (rw.Flag != null) rw.Flag.enabled = false;
                    SetNameInset(rw, false);
                    continue;
                }

                CloudRankEntry e = _entries[dataIndex];
                string tierHex = TierCatalog.GetTierHexColorOnLight(e.Elo);
                string name = e.DisplayName;
                if (string.IsNullOrEmpty(name)) name = CsrLoc.T("common.anonymous");
                bool isMe = !string.IsNullOrEmpty(mySteamId) && e.SteamId == mySteamId;
                string meTag = isMe ? CsrLoc.T("win.board.you_tag") : "";

                // 国旗: 有国籍才显示, 同时把名称列右移给它让位
                Sprite flag = FlagSprites.Get(e.Country);
                bool showFlag = flag != null;
                if (rw.Flag != null)
                {
                    rw.Flag.enabled = showFlag;
                    if (showFlag) rw.Flag.sprite = flag;
                }
                SetNameInset(rw, showFlag);

                // 名称可用宽度随国旗显隐变化, FitText 必须读调整后的 rect
                string shown = UiKit.FitText(rw.Name, name, meTag.Length * 2.6f);

                if (rw.Rank != null) rw.Rank.text = "#" + e.Rank;
                rw.Name.text = PcsrColors.Wrap(tierHex, shown) + meTag;
                if (rw.Tier != null) rw.Tier.text = PcsrColors.Wrap(tierHex, TierCatalog.GetTierName(e.Elo));
                // ELO 与同行的段位/昵称统一用段位色, 自己的条目额外加下划线区分
                if (rw.Elo != null)
                {
                    rw.Elo.text = isMe
                        ? ("<u>" + PcsrColors.WrapNum(tierHex, e.Elo.ToString()) + "</u>")
                        : PcsrColors.WrapNum(tierHex, e.Elo.ToString());
                }
                if (rw.Record != null) rw.Record.text = CsrLoc.T("win.board.record", e.Wins, e.Losses);
                if (rw.Modes != null) rw.Modes.text = CsrLoc.T("win.board.modes",
                    e.WinsAllPeak, e.LossesAllPeak, e.WinsHardcore, e.LossesHardcore, e.WinsSprint, e.LossesSprint);
            }
        }

        // 名称列左缘随国旗显隐移动: 无国籍时文字顶到列首, 不留空档
        private static void SetNameInset(RowWidgets rw, bool withFlag)
        {
            if (rw.NameRt == null) return;
            float x = withFlag ? ColNameX + NameInset : ColNameX;
            float w = withFlag ? ColNameW - NameInset : ColNameW;
            rw.NameRt.offsetMin = new Vector2(x, 0f);
            rw.NameRt.offsetMax = new Vector2(x + w, 0f);
        }

        // 表头逐个控件建出来, 复用 MakeCell 的列坐标与对齐参数
        private void BuildTableHeader(RectTransform body)
        {
            _headerCells = new TextMeshProUGUI[6];
            _headerCells[0] = MakeHeaderCell(body, "HeadRank", ColRankX, ColRankW, false, "win.board.col.rank");
            _headerCells[1] = MakeHeaderCell(body, "HeadPlayer", ColNameX, ColNameW, false, "win.board.col.player");
            _headerCells[2] = MakeHeaderCell(body, "HeadTier", ColTierX, ColTierW, false, "win.board.col.tier");
            _headerCells[3] = MakeHeaderCell(body, "HeadElo", ColEloX, ColEloW, TextAlignmentOptions.Center, "win.board.col.elo");
            _headerCells[4] = MakeHeaderCell(body, "HeadRecord", ColRecordX, ColRecordW, false, "win.board.col.record");
            _headerCells[5] = MakeHeaderCell(body, "HeadModes", ColModesX, ColModesW, false, "win.board.col.modes");
        }

        private static TextMeshProUGUI MakeHeaderCell(RectTransform body, string name, float x, float w,
            bool right, string locKey)
        {
            return MakeHeaderCell(body, name, x, w,
                right ? TextAlignmentOptions.Right : TextAlignmentOptions.Left, locKey);
        }

        private static TextMeshProUGUI MakeHeaderCell(RectTransform body, string name, float x, float w,
            TextAlignmentOptions alignment, string locKey)
        {
            TextMeshProUGUI t = PeakWindowStyle.CreateText(body, name, CsrLoc.T(locKey), 17f, PeakWindowStyle.Heading,
                alignment);
            NativeUiFactory.Place((RectTransform)t.transform, 26f + x, 42f, w, 28f);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.margin = Vector4.zero;
            LocalizedTextBinder.Bind(t, locKey);
            return t;
        }

        // 语言切换时表头要跟着换, 但控件本身已建好, 只改文本
        private void RefreshHeaderText()
        {
            if (_headerCells == null) return;
            string[] keys =
            {
                "win.board.col.rank", "win.board.col.player", "win.board.col.tier",
                "win.board.col.elo", "win.board.col.record", "win.board.col.modes"
            };
            for (int i = 0; i < _headerCells.Length && i < keys.Length; i++)
            {
                if (_headerCells[i] != null) _headerCells[i].text = CsrLoc.T(keys[i]);
            }
        }
    }
}
