using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 赛后战报独立窗口 + ELO 滚动与双进度条补间动画
    // 弹出时机: 一方登顶、胜负裁定完成时立刻弹出, 双方对称, 不做任何延迟。
    public sealed class PostMatchReportView : MonoBehaviour
    {
        private const float BoxPad = 24f;
        private const float EloAnimSeconds = 1.35f;

        public static PostMatchReportView Instance { get; private set; }
        // 上一次对局结束后生成的专属赛后战报快照
        public static CsrPostMatchData LastPostMatch { get; set; }

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

        public bool IsOpen { get { return _postOpen; } }

        private void Awake()
        {
            Instance = this;
        }

        private void Update()
        {
            if (_postOpen) TickPostMatchAnimation();
        }

        // 结算完成即调用: 立即展示, 双方对称, 无延迟
        public static void ShowPostMatchWindow()
        {
            if (LastPostMatch == null) return;
            if (Instance == null)
            {
                GameObject go = new GameObject("PeakCSR_PostMatchReport");
                DontDestroyOnLoad(go);
                go.AddComponent<PostMatchReportView>();
            }
            if (Instance == null) return;
            Instance.ShowPostMatch(LastPostMatch);
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
                    data.InviteCode, MatchNaming.GetAscentShortName(data.Ascent),
                    data.LevelIndex, data.BiomeLabel, data.Seed);
            }

            // ELO 数字滚动
            _eloAnimFrom = data.OldElo;
            _eloAnimTo = data.NewElo;
            _eloAnimStart = Time.unscaledTime;

            if (_postTierLabel != null)
            {
                _postTierLabel.text = PcsrColors.Wrap(TierCatalog.GetTierHexColorOnLight(data.NewElo), data.NewTier);
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
            float oldRatio = TierCatalog.GetTierProgress01(data.OldElo, out oldFloor, out oldCeil);
            float newRatio = TierCatalog.GetTierProgress01(data.NewElo, out newFloor, out newCeil);

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

            SetBarWidth(_barOldFillRt, oldW);
            SetBarWidth(_barNewFillRt, newW);
        }

        private static void SetBarWidth(RectTransform rt, float width)
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
                    _postEloBig.text = PcsrColors.Wrap("2A3441", shown.ToString())
                        + "  " + PcsrColors.Wrap(PcsrColors.VoidTone, "±0");
                }
                else
                {
                    string sign = _postData.TotalDelta >= 0 ? "+" : "";
                    string deltaHex = _postData.TotalDelta >= 0 ? PcsrColors.Positive : PcsrColors.Danger;
                    _postEloBig.text = PcsrColors.Wrap("2A3441", shown.ToString())
                        + "  " + PcsrColors.Wrap(deltaHex, sign + _postData.TotalDelta);
                }
            }

            // 双进度条补间 (比 ELO 数字略长, 视觉上后收尾)
            float barT = Mathf.Clamp01(elapsed / (EloAnimSeconds * 1.15f));
            float barEased = 1f - Mathf.Pow(1f - barT, 3f);
            ApplyBarWidths(barEased);

            if (barT >= 1f && _postTierLabel != null)
            {
                string cross = _barCrossedTier ? CsrLoc.T("post.tier.changed") : "";
                _postTierLabel.text = PcsrColors.Wrap(TierCatalog.GetTierHexColorOnLight(_postData.NewElo), _postData.NewTier) + cross;
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

            _postSubtitle = UiKit.CreateText(body, "PostSub", "", 17f, PeakWindowStyle.TextMuted, 26f, 10f, inner, 24f);
            _postSubtitle.alignment = TextAlignmentOptions.Left;

            _postTitle = UiKit.CreateText(body, "PostTitle", "", 30f, PeakWindowStyle.Positive, 26f, 38f, inner, 40f);

            // ---- ELO 结算区 ----
            RectTransform eloBox = PeakWindowStyle.CreateBox(body, "PostEloBox", CsrLoc.T("post.box.elo"), 26f, 92f, inner, 210f);
            LocalizedTextBinder.BindBoxHeading(eloBox, "post.box.elo");
            float eloInner = inner - BoxPad * 2f;

            _postEloBig = UiKit.CreateText(eloBox, "PostEloBig", "", 34f, PeakWindowStyle.TextPrimary, BoxPad, 50f, 380f, 46f);
            _postTierLabel = UiKit.CreateText(eloBox, "PostTier", "", 24f, PeakWindowStyle.Heading, BoxPad + 396f, 54f, eloInner - 396f, 40f);

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
            UiKit.CreateText(eloBox, "EloFloor", "", 13f, PeakWindowStyle.TextMuted, BoxPad, 124f, 120f, 18f);

            _postEloDetail = UiKit.CreateText(eloBox, "PostEloDetail", "", 17f, PeakWindowStyle.TextPrimary, BoxPad, 140f, eloInner, 26f);

            // ---- 分段对比区 ----
            const float splitsH = 328f;
            RectTransform splitBox = PeakWindowStyle.CreateBox(body, "PostSplitsBox", CsrLoc.T("post.box.splits"), 26f, 310f, inner, splitsH);
            LocalizedTextBinder.BindBoxHeading(splitBox, "post.box.splits");
            float spInner = inner - BoxPad * 2f;

            TextMeshProUGUI splitHeader = UiKit.CreateText(splitBox, "SplitHeader", CsrLoc.T("post.splits.header"), 16f, PeakWindowStyle.Heading, BoxPad, 50f, spInner, 24f);
            LocalizedTextBinder.Bind(splitHeader, "post.splits.header");
            PeakWindowStyle.CreateSeparator(splitBox, "SplitSep", BoxPad, 76f, spInner);

            for (int i = 0; i < _postSplitRows.Length; i++)
            {
                _postSplitRows[i] = UiKit.CreateText(splitBox, "SplitRow" + i, "", 17f, PeakWindowStyle.TextPrimary,
                    BoxPad, 84f + i * 29f, spInner, 26f);
            }

            _postSplitsEmpty = UiKit.CreateText(splitBox, "SplitEmpty", CsrLoc.T("post.splits.empty"), 17f, PeakWindowStyle.TextMuted,
                BoxPad, 84f, spInner, 26f);
            // 空状态文案显示与否由数据动态控制, 语言切换时先恢复默认文本避免残留
            LocalizedTextBinder.Bind(_postSplitsEmpty, "post.splits.empty");

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
            LocalizedTextBinder.BindPill(closeBtn, "post.btn.close");
            LocalizedTextBinder.BindPill(menuBtn, "post.btn.menu");
            LocalizedTextBinder.BindPill(rematchBtn, "post.btn.rematch");

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
                // 未到达 与 到达但无记录 要区分: 前者是对手真的没跑到, 后者才是数据缺失
                string myText = row.MyMs > 0
                    ? ((row.MyEstimated ? "~" : "") + CsrFormat.FormatClockMs(row.MyMs))
                    : (row.MyReached ? CsrLoc.T("post.splits.missing") : CsrLoc.T("post.splits.unreached"));
                string oppText = row.OppMs > 0
                    ? ((row.OppEstimated ? "~" : "") + CsrFormat.FormatClockMs(row.OppMs))
                    : (row.OppReached ? CsrLoc.T("post.splits.missing") : CsrLoc.T("post.splits.unreached"));

                string deltaText = "-";
                string deltaHex = PcsrColors.VoidTone;
                if (row.MyMs > 0 && row.OppMs > 0)
                {
                    long delta = row.MyMs - row.OppMs;
                    if (delta < 0)
                    {
                        deltaText = "-" + CsrFormat.FormatClockMs(System.Math.Abs(delta));
                        deltaHex = PcsrColors.Positive;   // 我方更快 = 绿
                    }
                    else if (delta > 0)
                    {
                        deltaText = "+" + CsrFormat.FormatClockMs(delta);
                        deltaHex = PcsrColors.Danger;   // 我方更慢 = 红
                    }
                    else
                    {
                        deltaText = "±0.000";
                        deltaHex = PcsrColors.VoidTone;
                    }
                }

                _postSplitRows[i].gameObject.SetActive(true);
                _postSplitRows[i].text = "<b>" + (i + 1) + "</b>   " + row.Label
                    + "   <pos=55%><b>" + myText + "</b></pos>"
                    + "   <pos=73%>" + oppText + "</pos>"
                    + "   <pos=90%>" + PcsrColors.WrapBold(deltaHex, deltaText) + "</pos>";
            }
        }

        // 再来一局: 回主菜单并在到达后自动开始匹配 (沿用上一把的赛制/难度/计分设置)
        private void OnPostRematchClicked()
        {
            ClosePostMatch();
            MatchTeardownService.EnsureInitialized();
            MatchTeardownService.ReturnToMainMenu(true);
        }

        // 返回主菜单: 与"再来一局"共用同一条收尾路径, 只是不自动排队
        private void OnPostMenuClicked()
        {
            ClosePostMatch();
            MatchTeardownService.EnsureInitialized();
            MatchTeardownService.ReturnToMainMenu(false);
        }

        public void ClosePostMatch()
        {
            if (_postRoot != null) _postRoot.gameObject.SetActive(false);
            _postOpen = false;
        }
    }
}
