using PeakCSR.Practice;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakCSR
{
    // 左中分区: 实时队列人数与三赛制比例条 (原 BuildQueueBox + RefreshQueuePanel 收口)
    public sealed class LiveQueuePanel
    {
        private const float BoxPad = MenuLayout.BoxPad;
        // 队列行三段式: 赛制名 | 人数文案 | 比例条, 给文案留足空间不再压条
        private const float QueueLabelW = 96f;
        private const float QueueCellX = QueueLabelW + 10f;
        private const float QueueCellW = 200f;
        private const float QueueBarX = QueueCellX + QueueCellW + 12f;

        private readonly PcsrMenuWindow _host;
        public readonly RectTransform Box;
        private readonly float _queueBarW;

        private TextMeshProUGUI _lblQueueHead;
        private TextMeshProUGUI _lblQueueTotal;
        private TextMeshProUGUI _lblQueueStd;
        private TextMeshProUGUI _lblQueueAsc;
        private TextMeshProUGUI _lblQueueSpr;
        private TextMeshProUGUI _lblQueueMine;
        private Image _barQueueStd;
        private Image _barQueueAsc;
        private Image _barQueueSpr;

        public LiveQueuePanel(PcsrMenuWindow host, RectTransform body, float x, float y, float w, float h)
        {
            _host = host;
            float inner = w - BoxPad * 2f;
            _queueBarW = w - BoxPad * 2f - QueueBarX;
            Box = PeakWindowStyle.CreateBox(body, "BoxQueue", CsrLoc.T("win.box.queue"), x, y, w, h);

            LocalizedTextBinder.BindBoxHeading(Box, "win.box.queue");
            _lblQueueHead = UiKit.CreateText(Box, "QueueHead", "", 17f, PeakWindowStyle.TextMuted, BoxPad, 54f, inner, 24f);
            _lblQueueTotal = UiKit.CreateText(Box, "QueueTotal", "", 19f, PeakWindowStyle.TextPrimary, BoxPad, 80f, inner, 26f);
            _lblQueueMine = UiKit.CreateText(Box, "QueueMine", "", 16f, PeakWindowStyle.TextMuted, BoxPad, 108f, inner, 24f);

            float rowY = 142f;
            float rowH = 32f;
            _lblQueueStd = BuildQueueRow(Box, "QueueStd", CsrLoc.T("win.queue.mode.std"), BoxPad, rowY, _queueBarW, out _barQueueStd);
            _lblQueueAsc = BuildQueueRow(Box, "QueueAsc", CsrLoc.T("win.queue.mode.asc"), BoxPad, rowY + rowH, _queueBarW, out _barQueueAsc);
            _lblQueueSpr = BuildQueueRow(Box, "QueueSpr", CsrLoc.T("win.queue.mode.spr"), BoxPad, rowY + rowH * 2f, _queueBarW, out _barQueueSpr);
            LocalizedTextBinder.Bind(Box.transform.Find("QueueStd_Label").GetComponent<TMP_Text>(), "win.queue.mode.std");
            LocalizedTextBinder.Bind(Box.transform.Find("QueueAsc_Label").GetComponent<TMP_Text>(), "win.queue.mode.asc");
            LocalizedTextBinder.Bind(Box.transform.Find("QueueSpr_Label").GetComponent<TMP_Text>(), "win.queue.mode.spr");
        }

        // 三段式: 赛制名(左) | 人数文案(中, 自动缩字号) | 比例条(右, 吃掉剩余空白)
        // barW 由实例计算后经构造链传入, 不再使用硬编码宽度
        private TextMeshProUGUI BuildQueueRow(
            RectTransform box, string name, string label, float x, float y, float barW, out Image bar)
        {
            TextMeshProUGUI lbl = UiKit.CreateText(box, name + "_Label", label, 16f, PeakWindowStyle.TextPrimary, x, y, QueueLabelW, 24f);
            lbl.enableAutoSizing = true;
            lbl.fontSizeMin = 12f;
            lbl.fontSizeMax = 16f;

            TextMeshProUGUI val = UiKit.CreateText(box, name + "_Val", "", 15f, PeakWindowStyle.TextMuted,
                x + QueueCellX, y, QueueCellW, 24f);
            // 文案超宽时自动缩号, 不再压到右边比例条
            val.enableAutoSizing = true;
            val.fontSizeMin = 10f;
            val.fontSizeMax = 15f;

            Color trackColor = new Color(0.82f, 0.85f, 0.83f, 1f);
            Color fillColor = new Color(0.16f, 0.42f, 0.9f, 1f);
            Image back = PeakWindowStyle.CreateSurface(box, name + "_BarBack", trackColor, 7);
            NativeUiFactory.Place((RectTransform)back.transform, x + QueueBarX, y + 7f, barW, 12f);
            bar = PeakWindowStyle.CreateSurface(box, name + "_BarFill", fillColor, 7);
            NativeUiFactory.Place((RectTransform)bar.transform, x + QueueBarX, y + 7f, 0f, 12f);
            bar.name = name + "_BarFill";
            return val;
        }

        public void Refresh(MatchCoordinator mc)
        {
            CsrQueueStats q = mc.QueueStats;
            int maxPlayers = Mathf.Max(1, Mathf.Max(q.StdPlayers, Mathf.Max(q.AscPlayers, q.SprPlayers)));

            MatchSession s = mc.CurrentSession;
            bool queuing = s != null && (s.State == CsMatchState.SearchingLobby || s.State == CsMatchState.WaitingOpponent);

            UiKit.Safe(delegate
            {
                if (_lblQueueHead != null)
                {
                    _lblQueueHead.text = q.HasData
                        ? CsrLoc.T("win.queue.refreshed", q.RefreshedClock)
                        : CsrLoc.T("win.queue.no_data");
                }
            });

            UiKit.Safe(delegate
            {
                if (_lblQueueTotal != null)
                {
                    // 三个数字全部是人数口径 (与面板里"共 N 人"保持一致)
                    _lblQueueTotal.text = CsrLoc.T("win.queue.total",
                        q.OnlinePlayers, q.WaitingPlayers, q.ActivePlayers);
                }
            });

            UiKit.Safe(delegate
            {
                if (_lblQueueMine == null) return;
                _lblQueueMine.text = queuing
                    ? CsrLoc.T("win.queue.mine_queuing", mc.GetCurrentFormatDisplayName(),
                        CsrFormat.FormatElapsed(mc.QueueElapsedSeconds))
                    : CsrLoc.T("win.queue.mine_idle", mc.GetCurrentFormatDisplayName());
            });

            // 三行赛制条的三个数字同为人数口径, 与汇总行严格一致
            UiKit.Safe(delegate
            {
                if (_lblQueueStd != null)
                {
                    _lblQueueStd.text = CsrLoc.T("win.queue.cell", q.StdPlayers, q.StdWaiting, q.StdActive);
                }
                FillRatioBar(_barQueueStd, q.StdPlayers / (float)maxPlayers);
            });

            UiKit.Safe(delegate
            {
                if (_lblQueueAsc != null)
                {
                    _lblQueueAsc.text = CsrLoc.T("win.queue.cell", q.AscPlayers, q.AscWaiting, q.AscActive);
                }
                FillRatioBar(_barQueueAsc, q.AscPlayers / (float)maxPlayers);
            });

            UiKit.Safe(delegate
            {
                if (_lblQueueSpr != null)
                {
                    _lblQueueSpr.text = CsrLoc.T("win.queue.cell", q.SprPlayers, q.SprWaiting, q.SprActive);
                }
                FillRatioBar(_barQueueSpr, q.SprPlayers / (float)maxPlayers);
            });
        }

        // 按比例上色, 三个赛制用同一轨道宽度上限, 便于横向对比
        private void FillRatioBar(Image bar, float ratio)
        {
            if (bar == null) return;
            float full = _queueBarW;
            RectTransform rt = (RectTransform)bar.transform;
            Vector2 size = rt.sizeDelta;
            size.x = Mathf.Clamp01(ratio) * full;
            rt.sizeDelta = size;
        }
    }
}
