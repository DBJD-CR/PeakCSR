using System.Collections.Generic;
using RankedPeak.Services;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 下方分区: 全服天梯真表格 (Top 8 + 斑马纹 + 点击查档案, 原 BuildBoardBox + RefreshBoard 收口)
    public sealed class LeaderboardPanel
    {
        private const float BoxPad = 24f;
        private const float RefreshBtnY = 12f;
        private const float RefreshBtnH = 36f;
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

        private readonly PcsrMenuWindow _host;
        public readonly RectTransform Box;

        private TextMeshProUGUI _lblBoardMeta;
        private readonly TextMeshProUGUI[] _colRank = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colName = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colTier = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colElo = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colRecord = new TextMeshProUGUI[8];
        private readonly TextMeshProUGUI[] _colModes = new TextMeshProUGUI[8];
        private readonly Button[] _rowButtons = new Button[8];
        private readonly Image[] _rowStripes = new Image[8];
        private TextMeshProUGUI _lblBoardEmpty;
        // 表头整组 (底色条 + 各列表头), 数据为空时整体隐藏
        private readonly List<GameObject> _boardHeaderParts = new List<GameObject>();

        public LeaderboardPanel(PcsrMenuWindow host, RectTransform body, float x, float y, float w, float h)
        {
            _host = host;
            float inner = w - BoxPad * 2f;
            Box = PeakWindowStyle.CreateBox(body, "BoxBoard", CsrLoc.T("win.box.board"), x, y, w, h);

            LocalizedTextBinder.BindBoxHeading(Box, "win.box.board");
            _lblBoardMeta = UiKit.CreateText(Box, "LblMeta", "", 16f, PeakWindowStyle.TextMuted, BoxPad, 52f, inner - 220f, 24f);
            Button btnRefreshBoard = PeakWindowStyle.CreatePill(
                Box, "BtnRefreshBoard", CsrLoc.T("hud.btn.refresh_board"),
                w - BoxPad - 180f, RefreshBtnY, 180f, RefreshBtnH, 17f);
            btnRefreshBoard.onClick.AddListener(new UnityAction(OnRefreshBoardClicked));
            LocalizedTextBinder.BindPill(btnRefreshBoard, "hud.btn.refresh_board");
            UiKit.ShortenBoxHeading(Box, w, 180f + 12f);

            PeakWindowStyle.CreateSeparator(Box, "BoardSep", BoxPad, 78f, inner);

            // 表头: 逐列独立定位, 与数据行共用同一套列坐标
            _boardHeaderParts.Clear();

            Color headerBgColor = new Color(0.82f, 0.86f, 0.87f, 1f);
            Image headerBg = PeakWindowStyle.CreateSurface(Box, "BoardHeaderBg", headerBgColor, 6);
            NativeUiFactory.Place((RectTransform)headerBg.transform, BoxPad, BoardHeaderY, inner, BoardHeaderH);
            headerBg.raycastTarget = false;
            _boardHeaderParts.Add(headerBg.gameObject);

            TextMeshProUGUI headRank = PeakWindowStyle.CreateText(Box, "BoardHeaderRank", "#", 15f, PeakWindowStyle.Heading, TextAlignmentOptions.Left);
            NativeUiFactory.Place((RectTransform)headRank.transform, BoxPad + ColRankX, BoardHeaderY + 5f, ColRankW, 20f);
            headRank.textWrappingMode = TextWrappingModes.NoWrap;
            headRank.overflowMode = TextOverflowModes.Truncate;
            headRank.margin = Vector4.zero;
            _boardHeaderParts.Add(headRank.gameObject);

            AddHeaderCell(Box, "BoardHeaderName", "win.board.col.player", ColNameX, ColNameW, TextAlignmentOptions.Left);
            AddHeaderCell(Box, "BoardHeaderTier", "win.board.col.tier", ColTierX, ColTierW, TextAlignmentOptions.Left);
            AddHeaderCell(Box, "BoardHeaderElo", "win.board.col.elo", ColEloX, ColEloW, TextAlignmentOptions.Right);
            AddHeaderCell(Box, "BoardHeaderRecord", "win.board.col.record", ColRecordX, ColRecordW, TextAlignmentOptions.Left);
            AddHeaderCell(Box, "BoardHeaderModes", "win.board.col.modes", ColModesX, ColModesW, TextAlignmentOptions.Left);
            // '#' 列头无需翻译, 不挂绑定

            Color stripeColor = new Color(0.86f, 0.89f, 0.88f, 1f);
            for (int i = 0; i < 8; i++)
            {
                float rowY = BoardRowStartY + i * BoardRowH;

                // 斑马纹: 隔行铺一层极浅底色, 长行更容易横向追踪
                if ((i & 1) == 1)
                {
                    Image stripe = PeakWindowStyle.CreateSurface(Box, "RowStripe" + i, stripeColor, 5);
                    NativeUiFactory.Place((RectTransform)stripe.transform, BoxPad, rowY - 1f, inner, BoardRowH);
                    stripe.raycastTarget = false;
                    _rowStripes[i] = stripe;
                }

                _colRank[i] = UiKit.CreateText(Box, "Rank" + i, "", 16f, PeakWindowStyle.TextPrimary,
                    BoxPad + ColRankX, rowY, ColRankW, 22f);
                _colName[i] = UiKit.CreateText(Box, "Name" + i, "", 16f, PeakWindowStyle.TextPrimary,
                    BoxPad + ColNameX, rowY, ColNameW, 22f);
                _colTier[i] = UiKit.CreateText(Box, "Tier" + i, "", 16f, PeakWindowStyle.TextPrimary,
                    BoxPad + ColTierX, rowY, ColTierW, 22f);
                _colElo[i] = UiKit.CreateTextRight(Box, "Elo" + i, "", 17f, PeakWindowStyle.TextPrimary,
                    BoxPad + ColEloX, rowY, ColEloW, 22f);
                _colRecord[i] = UiKit.CreateText(Box, "Rec" + i, "", 15f, PeakWindowStyle.TextMuted,
                    BoxPad + ColRecordX, rowY, ColRecordW, 22f);
                _colModes[i] = UiKit.CreateText(Box, "Modes" + i, "", 15f, PeakWindowStyle.TextMuted,
                    BoxPad + ColModesX, rowY, ColModesW, 22f);

                // 整行透明按钮: 点击打开玩家档案
                Image hit = PeakWindowStyle.CreateSurface(Box, "RowHit" + i, new Color(1f, 1f, 1f, 0f), 6);
                RectTransform hitRt = (RectTransform)hit.transform;
                NativeUiFactory.Place(hitRt, BoxPad, rowY - 1f, inner, BoardRowH);
                hit.raycastTarget = true;
                Button hitBtn = hit.gameObject.AddComponent<Button>();
                hitBtn.transition = Selectable.Transition.None;
                int captured = i;
                hitBtn.onClick.AddListener(new UnityAction(() => OnBoardRowClicked(captured)));
                _rowButtons[i] = hitBtn;
            }

            _lblBoardEmpty = UiKit.CreateText(Box, "BoardEmpty", CsrLoc.T("win.board.empty"), 16f, PeakWindowStyle.TextMuted,
                BoxPad, BoardRowStartY + 6f, inner, 24f);
            LocalizedTextBinder.Bind(_lblBoardEmpty, "win.board.empty");
        }

        private void AddHeaderCell(
            RectTransform box, string name, string locKey, float x, float w, TextAlignmentOptions align)
        {
            TextMeshProUGUI cell = PeakWindowStyle.CreateText(box, name, CsrLoc.T(locKey), 15f, PeakWindowStyle.Heading, align);
            NativeUiFactory.Place((RectTransform)cell.transform, BoxPad + x, BoardHeaderY + 5f, w, 20f);
            cell.textWrappingMode = TextWrappingModes.NoWrap;
            cell.overflowMode = TextOverflowModes.Truncate;
            cell.margin = Vector4.zero;
            LocalizedTextBinder.Bind(cell, locKey);
            _boardHeaderParts.Add(cell.gameObject);
        }

        private void OnRefreshBoardClicked()
        {
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null)
            {
                svc.RefreshLeaderboard();
            }
            _host.RefreshDynamic();
        }

        private void OnBoardRowClicked(int index)
        {
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc == null || svc.Entries == null || index >= svc.Entries.Count) return;
            CloudRankEntry e = svc.Entries[index];
            if (string.IsNullOrEmpty(e.SteamId)) return;
            _host.OpenProfileCard(e);
        }

        public void Refresh(MatchCoordinator mc)
        {
            UiKit.Safe(delegate
            {
                LadderSyncService svc = LadderSyncService.Instance;
                int count = svc != null && svc.Entries != null ? svc.Entries.Count : 0;

                if (_lblBoardMeta != null)
                {
                    string status = svc != null && svc.Entries != null && count >= 0 && svc.Status.Kind != LadderSyncService.CloudStatusKind.None
                        ? svc.CloudStatus
                        : CsrLoc.T("cloud.updated", count);
                    string clock = svc != null
                        ? CloudLadderClient.FormatRefreshClock(svc.LastRefreshedAtMs)
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

                string mySteamId = "";
                try { mySteamId = SteamIdentity.GetSteamId64(); } catch { }

                for (int i = 0; i < 8; i++)
                {
                    bool has = svc != null && svc.Entries != null && i < svc.Entries.Count;
                    UiKit.SetActive(_colRank[i], has);
                    UiKit.SetActive(_colName[i], has);
                    UiKit.SetActive(_colTier[i], has);
                    UiKit.SetActive(_colElo[i], has);
                    UiKit.SetActive(_colRecord[i], has);
                    UiKit.SetActive(_colModes[i], has);
                    UiKit.SetActive(_rowButtons[i], has);
                    UiKit.SetActive(_rowStripes[i], has);
                    if (!has) continue;

                    CloudRankEntry e = svc.Entries[i];
                    string tierHex = TierCatalog.GetTierHexColorOnLight(e.Elo);
                    string name = e.DisplayName;
                    if (string.IsNullOrEmpty(name)) name = CsrLoc.T("common.anonymous");

                    bool isMe = !string.IsNullOrEmpty(mySteamId) && e.SteamId == mySteamId;
                    string meTag = isMe ? CsrLoc.T("win.board.you_tag") : "";

                    // 昵称长度不可控, 先按可用像素宽度截断再拼富文本, 否则会直接糊到下一列
                    string shownName = UiKit.FitText(_colName[i], name, isMe ? meTag.Length * 2.4f : 0f);
                    _colRank[i].text = "<b>#" + e.Rank + "</b>";
                    _colName[i].text = PcsrColors.WrapBold(tierHex, shownName) + meTag;
                    _colTier[i].text = PcsrColors.Wrap(tierHex, TierCatalog.GetTierName(e.Elo));
                    _colElo[i].text = PcsrColors.WrapBold(isMe ? PcsrColors.AccentBlue : "2A3441", e.Elo.ToString());
                    _colRecord[i].text = CsrLoc.T("win.board.record", e.Wins, e.Losses);
                    _colModes[i].text = CsrLoc.T("win.board.modes",
                        e.WinsAllPeak, e.LossesAllPeak,
                        e.WinsHardcore, e.LossesHardcore,
                        e.WinsSprint, e.LossesSprint);
                }
            });
        }
    }
}
