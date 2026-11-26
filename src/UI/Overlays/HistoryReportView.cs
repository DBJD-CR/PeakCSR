using System;
using RankedPeak.Services;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 历史战报回看窗口
    //
    // 视觉直接沿用 PostMatchReportView 的骨架 (标题/ELO 结论/分段表), 但数据来源不同:
    //   1) 本地归档命中 → 完整双向分段 (零请求)
    //   2) 云端 match_splits 命中 → 双向分段
    //   3) 都没命中 → 只显示胜负/ELO/进度/用时, 分段区标注"该局分段未存档"
    public sealed class HistoryReportView : MonoBehaviour
    {
        private const float BoxPad = MenuLayout.BoxPad;
        private const float RowH = 30f;
        private const int SplitRows = 10;

        private static HistoryReportView _instance;

        private RectTransform _root;
        private PeakWindowStyle.WindowParts _window;
        private TextMeshProUGUI _lblTitle;
        private TextMeshProUGUI _lblSubtitle;
        private TextMeshProUGUI _lblElo;
        private TextMeshProUGUI _lblDetail;
        private TextMeshProUGUI _lblSource;

        // 段位进度条 (回看不做补间, 直接落到最终宽度)
        private RectTransform _barBackRt;
        private RectTransform _barOldRt;
        private RectTransform _barNewRt;
        private Image _barNewImage;
        private float _barTotalWidth = 1f;
        private TextMeshProUGUI _lblSplitHeader;
        private readonly TextMeshProUGUI[] _splitRows = new TextMeshProUGUI[SplitRows];
        private TextMeshProUGUI _lblSplitsEmpty;
        private bool _open;

        // 列锚点与 PostMatchReportView 保持一致
        private const string ColSeg = "<pos=0%>";
        private const string ColMySeg = "<pos=16%>";
        private const string ColOppSeg = "<pos=30%>";
        private const string ColSegDelta = "<pos=44%>";
        private const string ColMyCum = "<pos=57%>";
        private const string ColOppCum = "<pos=71%>";
        private const string ColCumDelta = "<pos=86%>";

        public static void Show(CsrHistoryEntry entry)
        {
            if (entry == null) return;
            if (_instance == null)
            {
                GameObject go = new GameObject("PeakCSR_HistoryReport");
                DontDestroyOnLoad(go);
                go.AddComponent<HistoryReportView>();
            }
            if (_instance != null) _instance.OpenInternal(entry);
        }

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        private void OpenInternal(CsrHistoryEntry entry)
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

            // 先用历史行渲染基础信息, 再异步补分段
            CsrPostMatchData basic = BuildBasic(entry);
            Render(basic, "history");

            // 请求令牌: 关窗后立刻打开另一局时, 上一局的迟到回调不能画到新窗口上
            int token = ++_requestToken;

            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null)
            {
                svc.RequestReportDetail(entry, delegate(CsrPostMatchData data, string source)
                {
                    if (!_open || data == null) return;
                    if (token != _requestToken) return;
                    // 再校一次局身份, 双保险
                    if (!string.Equals(data.InviteCode, entry.InviteCode, StringComparison.Ordinal)
                        || data.Seed != entry.Seed) return;
                    Render(data, source);
                });
            }
        }

        // 每次打开自增: 迟到的回调凭令牌失配被丢弃
        private int _requestToken;

        private void Close()
        {
            if (_root != null) _root.gameObject.SetActive(false);
            _open = false;
            _requestToken++;
        }

        private void Update()
        {
            if (_open && Input.GetKeyDown(KeyCode.Escape)) Close();
        }

        private static CsrPostMatchData BuildBasic(CsrHistoryEntry h)
        {
            return new CsrPostMatchData
            {
                IWon = h.Won,
                IsVoid = h.IsVoid,
                OpponentName = h.OpponentName,
                InviteCode = h.InviteCode,
                LevelIndex = h.LevelIndex,
                BiomeLabel = h.BiomeLabel,
                Seed = h.Seed,
                Ascent = h.Ascent,
                Format = (PcsrRuleFormat)h.Format,
                OldElo = h.EloBefore,
                NewElo = h.EloAfter,
                TotalDelta = h.Delta,
                // 与 BuildReportFromHistory 同口径
                MyTotalMs = h.Won || h.IsVoid ? h.DurationMs : 0L,
                MyProgress = h.MyProgress,
                OppProgress = h.OppProgress
            };
        }

        private void BuildUi()
        {
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return;

            GameObject rootGo = new GameObject("PcsrHistoryReportRoot", typeof(RectTransform));
            rootGo.transform.SetParent(canvas.transform, false);
            _root = (RectTransform)rootGo.transform;
            _root.anchorMin = Vector2.zero;
            _root.anchorMax = Vector2.one;
            _root.offsetMin = Vector2.zero;
            _root.offsetMax = Vector2.zero;

            PeakWindowStyle.CreateBackdrop(_root, "ReportBackdrop");

            const float w = 1180f;
            const float h = 860f;
            const float titleH = 78f;
            _window = PeakWindowStyle.CreateWindow(_root, "PcsrReport", w, h, titleH,
                CsrLoc.T("report.window.title"), "");

            TextMeshProUGUI winTitle = _window.Title;
            if (winTitle != null)
            {
                RectTransform rt = winTitle.transform as RectTransform;
                if (rt != null) NativeUiFactory.Place(rt, 30f, 0f, w - 60f - 110f, titleH);
                winTitle.textWrappingMode = TextWrappingModes.NoWrap;
                winTitle.overflowMode = TextOverflowModes.Overflow;
                winTitle.alignment = TextAlignmentOptions.Left;
                LocalizedTextBinder.Bind(winTitle, "report.window.title");
            }

            Button close = PeakWindowStyle.CreatePill(_window.TitleBar, "ReportClose", "X",
                w - 14f - 72f, 20f, 48f, 42f, 24f);
            UiKit.TintOnBar(close);
            close.onClick.AddListener(new UnityAction(Close));

            RectTransform body = _window.Body;
            float inner = w - 52f;

            _lblSubtitle = UiKit.CreateText(body, "RepSub", "", 17f, PeakWindowStyle.TextMuted, 26f, 8f, inner, 24f);
            _lblTitle = UiKit.CreateText(body, "RepTitle", "", 28f, PeakWindowStyle.TextPrimary, 26f, 32f, inner, 38f);
            _lblElo = UiKit.CreateText(body, "RepElo", "", 19f, PeakWindowStyle.TextPrimary, 26f, 74f, inner, 26f);
            // 明细行: 基础分/进度补偿/保底修正, 与标准战报同口径
            _lblDetail = UiKit.CreateText(body, "RepDetail", "", 16f, PeakWindowStyle.TextMuted, 26f, 100f, inner, 24f);
            _lblDetail.textWrappingMode = TextWrappingModes.NoWrap;
            _lblDetail.overflowMode = TextOverflowModes.Ellipsis;

            // 段位进度条: 与标准战报同构, 灰=赛前 / 绿或红=赛后 (回看不做补间)
            Image back = PeakWindowStyle.CreateSurface(body, "RepEloBarBack", new Color(0.82f, 0.85f, 0.83f, 1f), 8);
            _barBackRt = (RectTransform)back.transform;
            NativeUiFactory.Place(_barBackRt, 26f, 128f, inner, 14f);
            _barTotalWidth = Mathf.Max(1f, inner);

            Image oldFill = PeakWindowStyle.CreateSurface(body, "RepEloBarOld", new Color(0.62f, 0.66f, 0.65f, 1f), 8);
            _barOldRt = (RectTransform)oldFill.transform;
            NativeUiFactory.Place(_barOldRt, 26f, 129f, 0f, 12f);

            Image newFill = PeakWindowStyle.CreateSurface(body, "RepEloBarNew", new Color(0.11f, 0.51f, 0.29f, 0.85f), 8);
            _barNewImage = newFill;
            _barNewRt = (RectTransform)newFill.transform;
            NativeUiFactory.Place(_barNewRt, 26f, 129f, 0f, 12f);

            _lblSource = UiKit.CreateText(body, "RepSource", "", 14f, PeakWindowStyle.TextMuted, 26f, 146f, inner, 22f);

            PeakWindowStyle.CreateSeparator(body, "RepSep", 26f, 174f, inner);

            // 分段区
            RectTransform splitBox = PeakWindowStyle.CreateBox(body, "RepSplitsBox",
                CsrLoc.T("report.box.splits"), 26f, 184f, inner, h - titleH - 184f - 60f);
            LocalizedTextBinder.BindBoxHeading(splitBox, "report.box.splits");
            float spInner = inner - BoxPad * 2f;

            _lblSplitHeader = UiKit.CreateText(splitBox, "RepSplitHeader", BuildSplitHeader(),
                17f, PeakWindowStyle.Heading, BoxPad, 50f, spInner, 26f);
            PeakWindowStyle.CreateSeparator(splitBox, "RepSplitSep", BoxPad, 78f, spInner);

            for (int i = 0; i < SplitRows; i++)
            {
                _splitRows[i] = UiKit.CreateText(splitBox, "RepSplitRow" + i, "", 18f,
                    PeakWindowStyle.TextPrimary, BoxPad, 86f + i * RowH, spInner, RowH);
                _splitRows[i].textWrappingMode = TextWrappingModes.NoWrap;
            }

            _lblSplitsEmpty = UiKit.CreateText(splitBox, "RepSplitsEmpty",
                CsrLoc.T("report.splits.missing"), 18f, PeakWindowStyle.TextMuted,
                BoxPad, 86f, spInner, 50f, wrap: true);
            LocalizedTextBinder.Bind(_lblSplitsEmpty, "report.splits.missing");

            _root.gameObject.SetActive(false);
        }

        private static string BuildSplitHeader()
        {
            return ColSeg + CsrLoc.T("post.splits.col_seg")
                + ColMySeg + CsrLoc.T("post.splits.col_my_seg")
                + ColOppSeg + CsrLoc.T("post.splits.col_opp_seg")
                + ColSegDelta + CsrLoc.T("post.splits.col_seg_delta")
                + ColMyCum + CsrLoc.T("post.splits.col_my_cum")
                + ColOppCum + CsrLoc.T("post.splits.col_opp_cum")
                + ColCumDelta + CsrLoc.T("post.splits.col_cum_delta");
        }

        private void Render(CsrPostMatchData d, string source)
        {
            if (d == null) return;

            if (_lblTitle != null)
            {
                if (d.IsVoid)
                {
                    _lblTitle.text = CsrLoc.T("post.title.void");
                    _lblTitle.color = PeakWindowStyle.TextMuted;
                }
                else
                {
                    _lblTitle.text = d.IWon ? CsrLoc.T("post.title.win") : CsrLoc.T("post.title.loss");
                    _lblTitle.color = d.IWon ? PeakWindowStyle.Positive : PeakWindowStyle.Danger;
                }
            }
            if (_lblSubtitle != null)
            {
                _lblSubtitle.text = CsrLoc.T("post.subtitle", d.InviteCode,
                    MatchNaming.GetAscentShortName(d.Ascent), d.LevelIndex, d.BiomeLabel, d.Seed);
            }
            if (_lblElo != null)
            {
                string deltaText = d.IsVoid ? "±0" : ((d.TotalDelta >= 0 ? "+" : "") + d.TotalDelta);
                string hex = d.IsVoid ? PcsrColors.VoidTone : (d.TotalDelta >= 0 ? PcsrColors.Positive : PcsrColors.Danger);
                _lblElo.text = d.OldElo + " → " + d.NewElo + "  " + PcsrColors.WrapNum(hex, deltaText)
                    + "   " + CsrLoc.T("report.used_time", CsrFormat.FormatClockMs(d.MyTotalMs))
                    + "   " + CsrLoc.T("report.progress", Mathf.RoundToInt(d.MyProgress * 100f), Mathf.RoundToInt(d.OppProgress * 100f));
            }
            if (_lblDetail != null)
            {
                _lblDetail.text = BuildDetailLine(d);
            }
            if (_lblSource != null)
            {
                _lblSource.text = CsrLoc.T("report.source." + (source ?? "history"));
            }

            ApplyEloBars(d);
            FillSplits(d);
        }

        // 明细行: 作废局只报结论; 有本地明细就完整列出三项修正
        private static string BuildDetailLine(CsrPostMatchData d)
        {
            if (d.IsVoid) return "";
            string baseSign = d.BaseDelta >= 0 ? "+" : "";
            string bonusSign = d.ProgressBonus >= 0 ? "+" : "";
            string floorText = d.IWon
                ? ((d.FloorAdjust >= 0 ? "+" : "") + d.FloorAdjust)
                : "—";
            string line = CsrLoc.T("post.elo.detail2",
                d.OldElo, d.NewElo,
                baseSign + d.BaseDelta,
                bonusSign + d.ProgressBonus,
                floorText,
                Mathf.RoundToInt(d.MyProgress * 100f),
                Mathf.RoundToInt(d.OppProgress * 100f));
            // 该局是云端结算的: 总分已被权威分覆盖, 明细是当时的本地预估, 必须标注
            if (d.CloudSettled && d.AuthNewElo > 0)
            {
                line += "   " + CsrLoc.T("report.detail.estimated");
            }
            return line;
        }

        private void ApplyEloBars(CsrPostMatchData d)
        {
            if (_barOldRt == null || _barNewRt == null) return;
            int oldFloor, oldCeil, newFloor, newCeil;
            float oldRatio = TierCatalog.GetTierProgress01(d.OldElo, out oldFloor, out oldCeil);
            float newRatio = TierCatalog.GetTierProgress01(d.NewElo, out newFloor, out newCeil);
            float w = _barTotalWidth;

            SetBarWidth(_barOldRt, oldRatio * w);
            SetBarWidth(_barNewRt, (d.IsVoid ? oldRatio : newRatio) * w);

            if (_barNewImage != null)
            {
                if (d.IsVoid) _barNewImage.color = new Color(0.35f, 0.38f, 0.44f, 1f);
                else if (d.TotalDelta >= 0) _barNewImage.color = new Color(0.11f, 0.51f, 0.29f, 1f);
                else _barNewImage.color = new Color(0.78f, 0.20f, 0.18f, 1f);
            }
        }

        private static void SetBarWidth(RectTransform rt, float width)
        {
            Vector2 size = rt.sizeDelta;
            size.x = Mathf.Max(0f, width);
            rt.sizeDelta = size;
        }

        private void FillSplits(CsrPostMatchData d)
        {
            bool hasAny = d.Splits != null && d.Splits.Count > 0;
            if (_lblSplitsEmpty != null) _lblSplitsEmpty.gameObject.SetActive(!hasAny);

            for (int i = 0; i < SplitRows; i++)
            {
                if (_splitRows[i] == null) continue;
                if (!hasAny || i >= d.Splits.Count)
                {
                    _splitRows[i].gameObject.SetActive(false);
                    continue;
                }
                _splitRows[i].gameObject.SetActive(true);

                CsrSplitRow row = d.Splits[i];
                string mySeg = row.MyReached ? FormatMs(row.MySegMs, row.MyEstimated) : CsrLoc.T("post.splits.missing");
                string oppSeg = row.OppReached ? FormatMs(row.OppSegMs, row.OppEstimated) : CsrLoc.T("post.splits.missing");
                string myCum = row.MyReached ? FormatMs(row.MyMs, false) : "-";
                string oppCum = row.OppReached ? FormatMs(row.OppMs, false) : "-";

                BuildDelta(row.MySegMs, row.OppSegMs, out string delta, out string deltaHex);
                BuildDelta(row.MyMs, row.OppMs, out string cumDelta, out string cumDeltaHex);

                _splitRows[i].text = ColSeg + row.Label
                    + ColMySeg + mySeg
                    + ColOppSeg + oppSeg
                    + ColSegDelta + PcsrColors.WrapNum(deltaHex, delta)
                    + ColMyCum + myCum
                    + ColOppCum + oppCum
                    + ColCumDelta + PcsrColors.WrapNum(cumDeltaHex, cumDelta);
            }
        }

        private static string FormatMs(long ms, bool estimated)
        {
            if (ms <= 0) return "-";
            return (estimated ? "~" : "") + CsrFormat.FormatClockMs(ms);
        }

        // 与 PostMatchReportView.BuildDelta 同口径
        private static void BuildDelta(long mine, long theirs, out string text, out string hex)
        {
            text = "-";
            hex = PcsrColors.VoidTone;
            if (mine <= 0 || theirs <= 0) return;
            long delta = mine - theirs;
            if (delta < 0)
            {
                text = "-" + CsrFormat.FormatClockMs(System.Math.Abs(delta));
                hex = PcsrColors.Positive;
            }
            else if (delta > 0)
            {
                text = "+" + CsrFormat.FormatClockMs(delta);
                hex = PcsrColors.Danger;
            }
            else
            {
                text = "±0.000";
                hex = PcsrColors.VoidTone;
            }
        }
    }
}
