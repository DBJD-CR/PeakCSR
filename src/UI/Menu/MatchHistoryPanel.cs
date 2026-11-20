using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 右中分区: 云端历史战绩列表 (原 BuildHistoryBox + RefreshHistory 收口)
    public sealed class MatchHistoryPanel
    {
        private const float BoxPad = 24f;
        private const float RefreshBtnY = 12f;
        private const float RefreshBtnH = 36f;

        private readonly PcsrMenuWindow _host;
        public readonly RectTransform Box;

        private TextMeshProUGUI _lblHistoryMeta;
        private readonly TextMeshProUGUI[] _historyRows = new TextMeshProUGUI[6];

        public MatchHistoryPanel(PcsrMenuWindow host, RectTransform body, float x, float y, float w, float h)
        {
            _host = host;
            float inner = w - BoxPad * 2f;
            Box = PeakWindowStyle.CreateBox(body, "BoxHistory", CsrLoc.T("win.box.history"), x, y, w, h);

            LocalizedTextBinder.BindBoxHeading(Box, "win.box.history");
            _lblHistoryMeta = UiKit.CreateText(Box, "HistoryMeta", "", 16f, PeakWindowStyle.TextMuted, BoxPad, 52f, inner - 200f, 24f);
            Button btnHistoryRefresh = PeakWindowStyle.CreatePill(
                Box, "BtnHistoryRefresh", CsrLoc.T("hud.btn.refresh_history"),
                w - BoxPad - 176f, RefreshBtnY, 176f, RefreshBtnH, 16f);
            btnHistoryRefresh.onClick.AddListener(new UnityAction(OnRefreshHistoryClicked));
            LocalizedTextBinder.BindPill(btnHistoryRefresh, "hud.btn.refresh_history");
            UiKit.ShortenBoxHeading(Box, w, 176f + 12f);

            PeakWindowStyle.CreateSeparator(Box, "HistorySep", BoxPad, 80f, inner);

            for (int i = 0; i < _historyRows.Length; i++)
            {
                _historyRows[i] = UiKit.CreateText(Box, "HistoryRow" + i, "", 16f, PeakWindowStyle.TextPrimary,
                    BoxPad, 88f + i * 25f, inner, 22f);
            }
        }

        private void OnRefreshHistoryClicked()
        {
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null)
            {
                svc.RefreshHistory(true);
            }
            _host.RefreshDynamic();
        }

        public void Refresh(MatchCoordinator mc)
        {
            UiKit.Safe(delegate
            {
                LadderSyncService svc = LadderSyncService.Instance;
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
                            ? PcsrColors.Wrap(PcsrColors.Muted, CsrLoc.T("win.history.empty"))
                            : string.Empty;
                        continue;
                    }

                    CsrHistoryEntry h = svc.History[i];
                    bool zh = CsrLoc.Current == CsrLanguage.ZhCn;
                    string verdictHex = h.IsVoid ? PcsrColors.VoidTone : (h.Won ? PcsrColors.Positive : PcsrColors.Danger);
                    string verdict = h.IsVoid ? CsrLoc.T("win.history.void") : (h.Won ? CsrLoc.T("win.history.win") : CsrLoc.T("win.history.loss"));
                    string deltaSign = h.Delta >= 0 ? "+" : "";
                    string deltaText = h.IsVoid ? "±0" : (deltaSign + h.Delta);
                    // 难度用全名 (菜鸟新人/顶峰/天阶 N), 不再带 (A几) 后缀
                    string mode = MatchNaming.GetAscentDisplayName(h.Ascent);
                    // 单群系 (route > 0) 显示路线对应的具体群系名; 全山显示地图的完整群系组合
                    string biome;
                    if (h.Route > 0)
                    {
                        int biomeIdx = h.Route - 1;
                        biome = PcsrBiomes.Label(biomeIdx, zh);
                        if (string.IsNullOrEmpty(biome)) biome = "#" + h.LevelIndex;
                    }
                    else
                    {
                        biome = string.IsNullOrEmpty(h.BiomeLabel) ? ("#" + h.LevelIndex) : h.BiomeLabel;
                    }
                    string clock = CloudLadderClient.FormatHistoryClock(h.SettledAtMs);
                    string eloPart = h.IsVoid
                        ? h.EloBefore + "→" + h.EloAfter
                        : h.EloBefore + "→" + h.EloAfter + " (" + deltaText + ")";

                    _historyRows[i].text =
                        PcsrColors.WrapBold(verdictHex, verdict)
                        + "  " + mode + " · " + biome
                        + "  " + PcsrColors.WrapBold(verdictHex, deltaText)
                        + "  " + PcsrColors.Wrap(PcsrColors.Muted, eloPart + " · " + clock);
                }
            });
        }
    }
}
