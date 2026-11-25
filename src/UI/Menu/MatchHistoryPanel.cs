using System.Collections.Generic;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using RankedPeak.Services;

namespace PeakCSR
{
    // 右中分区: 云端历史战绩 (表头 + 定宽列 + 底部 ELO 走势图)
    public sealed class MatchHistoryPanel
    {
        private const float BoxPad = MenuLayout.BoxPad;
        private const float RefreshBtnY = 12f;
        private const float RefreshBtnH = 36f;
        private const float RowH = 26f;
        private const float VisibleRows = 6f;

        // ===== 表格列 (相对表格左缘) =====
        private const float ColVerdictX = 0f;
        private const float ColVerdictW = 56f;
        private const float ColModeX = 58f;
        private const float ColModeW = 128f;
        private const float ColOppX = 188f;
        private const float ColOppW = 100f;
        private const float ColDeltaX = 290f;
        private const float ColDeltaW = 50f;
        private const float ColEloX = 342f;
        private const float ColEloW = 54f;
        private const float ColTimeX = 398f;
        private const float ColTimeW = 184f;

        // 版式纵向断点 (相对 Box 左上角)
        private const float HeaderY = 88f;
        private const float ListY = 118f;
        private const float ChartSepY = 292f;
        private const float ChartTitleY = 298f;
        private const float ChartY = 328f;
        private const float ChartH = 202f;

        // 走势图最多画多少个点, 再多就只保留最近的
        private const int ChartMaxPoints = 30;

        private readonly PcsrMenuWindow _host;
        public readonly RectTransform Box;

        private TextMeshProUGUI _lblMeta;
        private PcsrScrollList _list;

        // 行池由 PcsrScrollList 按视口自动扩容
        private sealed class HistoryRow
        {
            public TextMeshProUGUI Verdict;
            public TextMeshProUGUI Mode;
            public TextMeshProUGUI Opponent;
            public TextMeshProUGUI Delta;
            public TextMeshProUGUI Elo;
            public TextMeshProUGUI Time;
        }

        private readonly List<HistoryRow> _rows = new List<HistoryRow>();

        // 走势图
        private RectTransform _chartRoot;
        private RectTransform _chartLayer;
        private TextMeshProUGUI _lblChartRange;
        private int _chartPoints = -1;
        private long _chartNewestAt = -1;

        public MatchHistoryPanel(PcsrMenuWindow host, RectTransform body, float x, float y, float w, float h)
        {
            _host = host;
            float inner = w - BoxPad * 2f;
            Box = PeakWindowStyle.CreateBox(body, "BoxHistory", CsrLoc.T("win.box.history"), x, y, w, h);
            LocalizedTextBinder.BindBoxHeading(Box, "win.box.history");

            _lblMeta = UiKit.CreateText(Box, "HistoryMeta", "", 16f, PeakWindowStyle.TextMuted, BoxPad, 52f, inner - 200f, 24f);
            Button btnRefresh = PeakWindowStyle.CreatePill(
                Box, "BtnHistoryRefresh", CsrLoc.T("hud.btn.refresh_history"),
                w - BoxPad - 176f, RefreshBtnY, 176f, RefreshBtnH, 16f);
            btnRefresh.onClick.AddListener(new UnityAction(OnRefreshClicked));
            LocalizedTextBinder.BindPill(btnRefresh, "hud.btn.refresh_history");
            UiKit.ShortenBoxHeading(Box, w, 176f + 12f);

            PeakWindowStyle.CreateSeparator(Box, "HistorySep", BoxPad, 82f, inner);

            BuildHeader(inner);

            // 滚动列表: 6 行整行可见
            const float listH = VisibleRows * RowH;
            _list = PcsrScrollList.Create(Box, "HistoryList", BoxPad, ListY, inner, listH,
                RowH, Mathf.RoundToInt(VisibleRows));
            _list.SetRowBinder(BindRow);
            _list.SetClickHandler(OnRowClicked);
            // 滚轮改变可视起点后必须重绘, 否则池行保留旧战绩
            _list.SetScrollChangedHandler(RefillFromCurrentScroll);

            PeakWindowStyle.CreateSeparator(Box, "ChartSep", BoxPad, ChartSepY, inner);
            BuildChart(inner, h);
        }

        // 表头与数据行同构: 同样挂在一个整行容器下, 用同样的左右拉伸锚点和行高
        private void BuildHeader(float inner)
        {
            GameObject headGo = new GameObject("HistHeadRow", typeof(RectTransform));
            headGo.transform.SetParent(Box, false);
            RectTransform headRt = (RectTransform)headGo.transform;
            NativeUiFactory.Place(headRt, BoxPad, HeaderY, inner, RowH);

            for (int i = 0; i < HeaderKeys.Length; i++)
            {
                _headerCells[i] = MakeHeaderCell(headRt, "HistHead" + i,
                    HeaderX[i], HeaderW[i], HeaderRight[i], CsrLoc.T(HeaderKeys[i]));
            }
        }

        private static TextMeshProUGUI MakeHeaderCell(RectTransform row, string name,
            float x, float w, bool right, string text)
        {
            TextMeshProUGUI t = PeakWindowStyle.CreateText(row, name, text, 16f, PeakWindowStyle.Heading,
                right ? TextAlignmentOptions.Right : TextAlignmentOptions.Left);
            RectTransform rt = (RectTransform)t.transform;
            rt.anchorMin = new Vector2(0f, 0f);
            rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 0.5f);
            rt.offsetMin = new Vector2(x, 0f);
            rt.offsetMax = new Vector2(x + w, 0f);
            t.textWrappingMode = TextWrappingModes.NoWrap;
            t.overflowMode = TextOverflowModes.Overflow;
            t.margin = Vector4.zero;
            return t;
        }

        // 语言切换或刷新时重设表头文本, 不依赖全局 binder
        private void ApplyHeaderText()
        {
            if (_headerCells == null) return;
            for (int i = 0; i < _headerCells.Length && i < HeaderKeys.Length; i++)
            {
                if (_headerCells[i] != null) _headerCells[i].text = CsrLoc.T(HeaderKeys[i]);
            }
        }

        private readonly TextMeshProUGUI[] _headerCells = new TextMeshProUGUI[6];
        private static readonly string[] HeaderKeys =
        {
            "win.history.col.verdict", "win.history.col.mode", "win.history.col.opponent",
            "win.history.col.delta", "win.history.col.elo", "win.history.col.time"
        };
        private static readonly float[] HeaderX = { ColVerdictX, ColModeX, ColOppX, ColDeltaX, ColEloX, ColTimeX };
        private static readonly float[] HeaderW = { ColVerdictW, ColModeW, ColOppW, ColDeltaW, ColEloW, ColTimeW };
        private static readonly bool[] HeaderRight = { false, false, false, true, true, true };

        private void BindRow(int idx, RectTransform rowRt)
        {
            while (_rows.Count <= idx) _rows.Add(null);
            if (_rows[idx] != null) return;

            HistoryRow rw = new HistoryRow();
            rw.Verdict = RowCell(rowRt, "Verdict", ColVerdictX, ColVerdictW, false);
            rw.Mode = RowCell(rowRt, "Mode", ColModeX, ColModeW, false);
            rw.Opponent = RowCell(rowRt, "Opp", ColOppX, ColOppW, false);
            rw.Delta = RowCell(rowRt, "Delta", ColDeltaX, ColDeltaW, true);
            rw.Elo = RowCell(rowRt, "Elo", ColEloX, ColEloW, true);
            rw.Time = RowCell(rowRt, "Time", ColTimeX, ColTimeW, true);
            _rows[idx] = rw;
        }

        private static TextMeshProUGUI RowCell(RectTransform row, string name, float x, float w, bool right)
        {
            TextMeshProUGUI t = PeakWindowStyle.CreateText(row, name, "", 16f, PeakWindowStyle.TextPrimary,
                right ? TextAlignmentOptions.Right : TextAlignmentOptions.Left);
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
            if (svc != null) svc.RefreshHistory(true);
            _host.RefreshDynamic();
        }

        // 点行: 打开该场战报 (本地归档 → 云端分段 → 基础降级)
        private void OnRowClicked(int dataIndex)
        {
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc == null || svc.History == null) return;
            if (dataIndex < 0 || dataIndex >= svc.History.Count) return;
            _host.OpenHistoryReport(svc.History[dataIndex]);
        }

        public void Refresh(MatchCoordinator mc)
        {
            UiKit.Safe(delegate
            {
                LadderSyncService svc = LadderSyncService.Instance;
                int count = svc != null && svc.History != null ? svc.History.Count : 0;
                bool loading = svc != null && svc.HistoryLoading;
                CsrLanguage lang = CsrLoc.Current;

                // 面板被 0.2s 高频拉起: 指纹没变就不重排富文本
                if (count == _boundCount && loading == _boundLoading && lang == _boundLang) return;
                _boundCount = count;
                _boundLoading = loading;
                _boundLang = lang;
                ApplyHeaderText();

                if (_lblMeta != null)
                {
                    _lblMeta.text = loading
                        ? CsrLoc.T("win.history.loading")
                        : CsrLoc.T("win.history.count_scroll", count);
                }
                if (_list == null) return;

                _list.SetItemCount(count);
                FillRows(svc, count);
                RefreshChart(svc, count);
            });
        }

        // 上次绘制时的数据指纹: 条数 + 加载态 + 语言
        private int _boundCount = -1;
        private bool _boundLoading;
        private CsrLanguage _boundLang = CsrLanguage.ZhCn;

        // 滚动后按当前可视起点重绘 (数据源用最新缓存)
        private void RefillFromCurrentScroll()
        {
            LadderSyncService svc = LadderSyncService.Instance;
            int count = svc != null && svc.History != null ? svc.History.Count : 0;
            FillRows(svc, count);
        }

        // 滚动绕开 Refresh 的指纹判断, 直接重绘: 条数没变但可视起点变了
        private void FillRows(LadderSyncService svc, int count)
        {
            bool zh = CsrLoc.Current == CsrLanguage.ZhCn;
            int first = _list != null ? _list.VisibleStartIndex : 0;

            for (int i = 0; i < _rows.Count; i++)
            {
                HistoryRow rw = _rows[i];
                if (rw == null) continue;
                int dataIndex = first + i;

                if (svc == null || svc.History == null || dataIndex >= count)
                {
                    ClearRow(rw);
                    // 空状态只在"完全没有数据"时显示一次
                    if (i == 0 && count == 0)
                    {
                        rw.Verdict.text = PcsrColors.Wrap(PcsrColors.Muted, CsrLoc.T("win.history.empty"));
                    }
                    continue;
                }

                FillRow(rw, svc.History[dataIndex], zh);
            }
        }

        private static void ClearRow(HistoryRow rw)
        {
            rw.Verdict.text = string.Empty;
            rw.Mode.text = string.Empty;
            rw.Opponent.text = string.Empty;
            rw.Delta.text = string.Empty;
            rw.Elo.text = string.Empty;
            rw.Time.text = string.Empty;
        }

        private static void FillRow(HistoryRow rw, CsrHistoryEntry h, bool zh)
        {
            string verdictHex = h.IsVoid ? PcsrColors.VoidTone : (h.Won ? PcsrColors.Positive : PcsrColors.Danger);
            string verdict = h.IsVoid ? CsrLoc.T("win.history.void") : (h.Won ? CsrLoc.T("win.history.win") : CsrLoc.T("win.history.loss"));
            string deltaText = h.IsVoid ? "±0" : ((h.Delta >= 0 ? "+" : "") + h.Delta);
            string eloText = h.EloAfter > 0 ? h.EloAfter.ToString() : "--";

            string mode = MatchNaming.GetAscentShortName(h.Ascent);
            string biome;
            if (h.Route > 0)
            {
                biome = ResolveRouteBiomeName(h, zh);
                if (string.IsNullOrEmpty(biome)) biome = "#" + h.LevelIndex;
            }
            else
            {
                biome = string.IsNullOrEmpty(h.BiomeLabel) ? ("#" + h.LevelIndex) : h.BiomeLabel;
            }

            string opp = string.IsNullOrEmpty(h.OpponentName) ? CsrLoc.T("common.anonymous") : h.OpponentName;

            rw.Verdict.text = PcsrColors.Wrap(verdictHex, verdict);
            rw.Mode.text = mode + "·" + biome;
            rw.Opponent.text = "vs " + UiKit.FitText(rw.Opponent, opp, 22f);
            rw.Delta.text = PcsrColors.WrapNum(verdictHex, deltaText);
            rw.Elo.text = PcsrColors.Wrap(PcsrColors.Muted, eloText);
            // 历史可能跨天, 只用 HH:mm:ss 会看不出来是哪天
            rw.Time.text = PcsrColors.Wrap(PcsrColors.Muted, CloudLadderClient.FormatHistoryClock(h.SettledAtMs));
        }

        // 把"本局路线在本地图里的第几个群系"翻译成群系名
        private static string ResolveRouteBiomeName(CsrHistoryEntry h, bool zh)
        {
            try
            {
                // 用原始签名
                string sig = MapCatalogHelper.RawBiomeSignature(h.LevelIndex);
                int idx = h.Route - 1;
                if (!string.IsNullOrEmpty(sig) && idx >= 0 && idx < sig.Length)
                {
                    int global = PcsrBiomes.IndexOfSig(sig[idx]);
                    if (global >= 0)
                    {
                        string name = PcsrBiomes.Label(global, zh);
                        if (!string.IsNullOrEmpty(name)) return name;
                    }
                }
            }
            catch
            {
            }
            // 回退用地图编号: Route 是"本地图内序号", 不能当全局群系下标用
            return "";
        }

        // ===== ELO 走势图 =====

        private void BuildChart(float inner, float h)
        {
            // h 是面板总高, 图高吃掉底部剩余空间; 留底边
            float chartH = Mathf.Min(ChartH, h - ChartY - 18f);
            if (chartH < 80f) chartH = 80f;

            _chartRoot = new GameObject("EloChart", typeof(RectTransform)).GetComponent<RectTransform>();
            _chartRoot.SetParent(Box, false);
            NativeUiFactory.Place(_chartRoot, BoxPad, ChartY, inner, chartH);

            // 标题行: 左侧标题, 右侧区间摘要
            UiKit.CreateText(Box, "ChartTitle", CsrLoc.T("win.history.elo_chart"), 17f,
                PeakWindowStyle.Heading, BoxPad, ChartTitleY, 200f, 24f);
            _lblChartRange = UiKit.CreateTextRight(Box, "ChartRange", "", 14f,
                PeakWindowStyle.TextMuted, BoxPad, ChartTitleY, inner, 24f);

            // 图形层: 每次数据变化整体重建
            GameObject layerGo = new GameObject("Layer", typeof(RectTransform));
            layerGo.transform.SetParent(_chartRoot, false);
            _chartLayer = (RectTransform)layerGo.transform;
            _chartLayer.anchorMin = Vector2.zero;
            _chartLayer.anchorMax = Vector2.one;
            _chartLayer.offsetMin = Vector2.zero;
            _chartLayer.offsetMax = Vector2.zero;
        }

        private void RefreshChart(LadderSyncService svc, int count)
        {
            if (_chartLayer == null) return;

            // 条数或最新一条时间变了才重建, 避免 0.2s 反复销毁重建
            long newest = 0;
            if (svc != null && svc.History != null && count > 0)
            {
                newest = svc.History[0].SettledAtMs;
            }
            if (count == _chartPoints && newest == _chartNewestAt) return;
            _chartPoints = count;
            _chartNewestAt = newest;

            for (int i = _chartLayer.childCount - 1; i >= 0; i--)
            {
                Object.Destroy(_chartLayer.GetChild(i).gameObject);
            }

            // 历史是倒序 (最新在前), 折线要按时间正序画
            List<int> values = new List<int>();
            if (svc != null && svc.History != null)
            {
                int start = Mathf.Max(0, count - ChartMaxPoints);
                for (int i = count - 1; i >= start; i--)
                {
                    CsrHistoryEntry h = svc.History[i];
                    if (h == null) continue;
                    if (h.IsVoid) continue;
                    if (h.EloAfter <= 0) continue;
                    values.Add(h.EloAfter);
                }
            }

            if (_lblChartRange != null)
            {
                if (values.Count >= 2)
                {
                    // 净变化前面不加正负号, 改用红绿着色表达语义
                    int net = values[values.Count - 1] - values[0];
                    string netHex = net >= 0 ? PcsrColors.Positive : PcsrColors.Danger;
                    string netArrow = net >= 0 ? "▲" : "▼";
                    _lblChartRange.text = CsrLoc.T("win.history.chart_range",
                        Max(values), Min(values), netArrow, PcsrColors.WrapNum(netHex, Mathf.Abs(net).ToString()));
                }
                else
                {
                    _lblChartRange.text = "";
                }
            }

            if (values.Count < 2)
            {
                TextMeshProUGUI empty = UiKit.CreateText(_chartLayer, "ChartEmpty",
                    CsrLoc.T("win.history.chart_empty"), 15f, PeakWindowStyle.TextMuted,
                    0f, 40f, _chartRoot.sizeDelta.x, 24f);
                empty.alignment = TextAlignmentOptions.Center;
                return;
            }

            float plotX0 = 46f;
            float plotX1 = Mathf.Max(plotX0 + 40f, _chartRoot.sizeDelta.x - 10f);
            float plotY0 = 10f;
            float plotY1 = _chartRoot.sizeDelta.y - 26f;

            int min = Min(values);
            int max = Max(values);
            if (max == min)
            {
                max += 10;
                min -= 10;
            }
            float span = max - min;

            // 三条横向网格 + 左侧刻度
            for (int g = 0; g <= 2; g++)
            {
                float t = g / 2f;
                float y = Mathf.Lerp(plotY0, plotY1, t);
                PeakWindowStyle.CreateSeparator(_chartLayer, "Grid" + g, plotX0, y, plotX1 - plotX0);
                int value = Mathf.RoundToInt(max - t * span);
                TextMeshProUGUI tick = UiKit.CreateTextRight(_chartLayer, "Tick" + g, value.ToString(), 13f,
                    PeakWindowStyle.TextMuted, 0f, y - 10f, 40f, 20f);
                tick.margin = Vector4.zero;
            }

            // 折线: 相邻两点之间放一根旋转细条
            Vector2 prev = Vector2.zero;
            for (int i = 0; i < values.Count; i++)
            {
                float tx = (float)i / (values.Count - 1);
                float ty = (values[i] - min) / span;
                Vector2 pt = new Vector2(
                    Mathf.Lerp(plotX0, plotX1, tx),
                    Mathf.Lerp(plotY1, plotY0, ty));

                if (i > 0)
                {
                    Image seg = PeakWindowStyle.CreateSurface(_chartLayer, "Seg" + i,
                        HexColor(values[i] >= values[i - 1] ? PcsrColors.Positive : PcsrColors.Danger, PeakWindowStyle.Pill), 2);
                    RectTransform rt = (RectTransform)seg.transform;
                    rt.anchorMin = new Vector2(0f, 1f);
                    rt.anchorMax = new Vector2(0f, 1f);
                    rt.pivot = new Vector2(0.5f, 0.5f);
                    float dx = pt.x - prev.x;
                    float dy = pt.y - prev.y;
                    rt.anchoredPosition = new Vector2((prev.x + pt.x) * 0.5f, -(prev.y + pt.y) * 0.5f);
                    rt.sizeDelta = new Vector2(Mathf.Sqrt(dx * dx + dy * dy), 2.5f);
                    // 屏幕 y 向下, 旋转角取反才与视觉方向一致
                    rt.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(-dy, dx) * Mathf.Rad2Deg);
                }

                // 节点: 末点加粗放大, 便于一眼看到当前分
                bool last = i == values.Count - 1;
                float dot = last ? 10f : 6f;
                Image node = PeakWindowStyle.CreateSurface(_chartLayer, "Dot" + i,
                    HexColor(PcsrColors.AccentBlue, PeakWindowStyle.Pill), (int)(dot * 0.5f));
                NativeUiFactory.Place((RectTransform)node.transform,
                    pt.x - dot * 0.5f, pt.y - dot * 0.5f, dot, dot);

                prev = pt;
            }

            // 末点数值
            TextMeshProUGUI lastVal = UiKit.CreateText(_chartLayer, "LastVal", values[values.Count - 1].ToString(),
                14f, PeakWindowStyle.TextPrimary, prev.x - 60f, prev.y - 26f, 60f, 20f);
            lastVal.alignment = TextAlignmentOptions.Right;
            lastVal.margin = Vector4.zero;
        }

        private static int Min(List<int> values)
        {
            int m = values[0];
            for (int i = 1; i < values.Count; i++) if (values[i] < m) m = values[i];
            return m;
        }

        private static int Max(List<int> values)
        {
            int m = values[0];
            for (int i = 1; i < values.Count; i++) if (values[i] > m) m = values[i];
            return m;
        }

        // 段位/语义色都是 #RRGGBB 字符串, 画图要真实 Color
        private static Color HexColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length < 6) return fallback;
            int r, g, b;
            if (int.TryParse(hex.Substring(0, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out r)
                && int.TryParse(hex.Substring(2, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out g)
                && int.TryParse(hex.Substring(4, 2), System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out b))
            {
                return new Color(r / 255f, g / 255f, b / 255f, 1f);
            }
            return fallback;
        }
    }
}
