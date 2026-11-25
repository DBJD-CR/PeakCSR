using RankedPeak.Services;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 下方分区: 天梯速览
    //
    // Top3 用领奖台呈现 (台阶高度 1 > 2 > 3, 台阶色调跟随段位色),
    // 右侧是"我的排名"卡, 下方是完整榜入口。整块只占左列宽度,
    // 把实时匹配下方的空白区吃掉。
    public sealed class LeaderboardPanel
    {
        private const float BoxPad = MenuLayout.BoxPad;

        // 领奖台区域 (相对 Box 左上角), 起点让开标题与状态行
        private const float PodiumY = 72f;
        private const float PodiumW = 330f;
        private const float PodiumH = 142f;

        // 台阶视觉顺序固定为 #2 #1 #3
        private const float StepW = 104f;
        private static readonly float[] StepX = { 113f, 0f, 226f };
        private static readonly float[] StepH = { 88f, 68f, 52f };

        // 名字 / 分数相对台阶顶的上浮距离
        private const float NameDrop = 40f;
        private const float EloDrop = 19f;

        // 台阶固定金/银/铜: 段位色用作文字色
        private static readonly Color[] StepColors =
        {
            new Color(0.706f, 0.518f, 0.078f, 1f),  // #1 金
            new Color(0.478f, 0.522f, 0.573f, 1f),  // #2 银
            new Color(0.639f, 0.400f, 0.204f, 1f)   // #3 铜
        };

        private readonly PcsrMenuWindow _host;
        public readonly RectTransform Box;

        private TextMeshProUGUI _lblMeta;
        // 三座领奖台各自一个容器, 显隐整块切换
        private readonly GameObject[] _podiumRoots = new GameObject[3];
        private readonly Image[] _podiumCaps = new Image[3];
        private readonly TextMeshProUGUI[] _podiumRanks = new TextMeshProUGUI[3];
        private readonly TextMeshProUGUI[] _podiumNames = new TextMeshProUGUI[3];
        private readonly TextMeshProUGUI[] _podiumElos = new TextMeshProUGUI[3];
        private readonly Button[] _podiumButtons = new Button[3];
        // 绘制时快照, 点击按快照取, 不用实时列表
        private readonly CloudRankEntry[] _podiumEntries = new CloudRankEntry[3];
        private TextMeshProUGUI _lblEmpty;

        private TextMeshProUGUI _myRankLine;
        private TextMeshProUGUI _myTierLine;
        private TextMeshProUGUI _myEloLine;
        private TextMeshProUGUI _myRecordLine;

        public LeaderboardPanel(PcsrMenuWindow host, RectTransform body, float x, float y, float w, float h)
        {
            _host = host;
            float inner = w - BoxPad * 2f;
            Box = PeakWindowStyle.CreateBox(body, "BoxBoard", CsrLoc.T("win.box.board"), x, y, w, h);
            LocalizedTextBinder.BindBoxHeading(Box, "win.box.board");

            // 顶部状态行 (云端状态 + 刷新时间)
            _lblMeta = UiKit.CreateText(Box, "LblMeta", "", 15f, PeakWindowStyle.TextMuted, BoxPad, 46f, inner, 22f);
            UiKit.ShortenBoxHeading(Box, w, 0f);

            BuildPodium();
            BuildMyCard(inner);
            BuildOpenButton(inner, h);

            _lblEmpty = UiKit.CreateText(Box, "BoardEmpty", CsrLoc.T("win.board.empty"), 15f,
                PeakWindowStyle.TextMuted, BoxPad, PodiumY + 52f, PodiumW, 24f);
            LocalizedTextBinder.Bind(_lblEmpty, "win.board.empty");
        }

        // Top3 领奖台: 每座一个容器, 内部坐标从 0 起算
        private void BuildPodium()
        {
            for (int i = 0; i < 3; i++)
            {
                GameObject root = new GameObject("Podium" + i, typeof(RectTransform));
                root.transform.SetParent(Box, false);
                RectTransform rootRt = (RectTransform)root.transform;
                NativeUiFactory.Place(rootRt, BoxPad + StepX[i], PodiumY, StepW, PodiumH);
                _podiumRoots[i] = root;

                float stepH = StepH[i];
                float stepTop = PodiumH - stepH;

                // 台阶底座: 金/银/铜固定色, 与名次一一对应
                Image step = PeakWindowStyle.CreateSurface(rootRt, "Step", StepColors[i], 8);
                NativeUiFactory.Place((RectTransform)step.transform, 0f, stepTop, StepW, stepH);

                // 台阶上的名次
                _podiumRanks[i] = PeakWindowStyle.CreateText((RectTransform)step.transform, "Rank", "",
                    26f, PeakWindowStyle.TextOnBar, TextAlignmentOptions.Center);
                RectTransform rankRt = (RectTransform)_podiumRanks[i].transform;
                rankRt.anchorMin = Vector2.zero;
                rankRt.anchorMax = Vector2.one;
                rankRt.offsetMin = new Vector2(0f, 8f);
                rankRt.offsetMax = Vector2.zero;
                _podiumRanks[i].margin = Vector4.zero;

                // 自己的台阶顶端加一条白边, 一眼看出哪座是我
                Image cap = PeakWindowStyle.CreateSurface(rootRt, "Cap", PeakWindowStyle.TextOnBar, 6);
                NativeUiFactory.Place((RectTransform)cap.transform, 0f, stepTop, StepW, 4f);
                cap.gameObject.SetActive(false);
                _podiumCaps[i] = cap;

                _podiumNames[i] = UiKit.CreateText(rootRt, "Name", "", 15f, PeakWindowStyle.TextPrimary,
                    0f, stepTop - NameDrop, StepW, 22f);
                _podiumNames[i].alignment = TextAlignmentOptions.Center;
                _podiumNames[i].enableAutoSizing = true;
                _podiumNames[i].fontSizeMin = 10f;
                _podiumNames[i].fontSizeMax = 15f;

                _podiumElos[i] = UiKit.CreateText(rootRt, "Elo", "", 15f, PeakWindowStyle.TextMuted,
                    0f, stepTop - EloDrop, StepW, 20f);
                _podiumElos[i].alignment = TextAlignmentOptions.Center;

                // 整列可点, 三列互不重叠; 透明底只为接射线
                Image hit = PeakWindowStyle.CreateSurface(rootRt, "Hit", new Color(1f, 1f, 1f, 0f), 5);
                NativeUiFactory.Place((RectTransform)hit.transform, 0f, 0f, StepW, PodiumH);
                hit.raycastTarget = true;
                Button btn = hit.gameObject.AddComponent<Button>();
                btn.transition = Selectable.Transition.None;
                int captured = i;
                btn.onClick.AddListener(new UnityAction(() => OnPodiumClicked(captured)));
                _podiumButtons[i] = btn;
            }
        }

        private void BuildMyCard(float inner)
        {
            float cardX = BoxPad + PodiumW + 12f;
            float cardW = Mathf.Max(150f, inner - PodiumW - 12f);
            Image card = PeakWindowStyle.CreateSurface(Box, "MyCard", PeakWindowStyle.WindowFill, 10);
            NativeUiFactory.Place((RectTransform)card.transform, cardX, PodiumY, cardW, PodiumH);
            RectTransform cardRt = (RectTransform)card.transform;

            const float pad = 14f;
            float cw = cardW - pad * 2f;
            _myRankLine = UiKit.CreateText(cardRt, "MyRank", "", 16f, PeakWindowStyle.TextMuted, pad, 12f, cw, 22f);
            _myTierLine = UiKit.CreateText(cardRt, "MyTier", "", 18f, PeakWindowStyle.TextPrimary, pad, 40f, cw, 26f);
            _myEloLine = UiKit.CreateText(cardRt, "MyElo", "", 26f, PeakWindowStyle.TextPrimary, pad, 72f, cw, 34f);
            _myRecordLine = UiKit.CreateText(cardRt, "MyRecord", "", 14f, PeakWindowStyle.TextMuted, pad, 112f, cw, 20f);
        }

        private void BuildOpenButton(float inner, float h)
        {
            float btnY = PodiumY + PodiumH + 10f;
            float btnH = Mathf.Max(40f, h - btnY - 18f);
            Button openBtn = PeakWindowStyle.CreatePill(Box, "BtnFullBoard", CsrLoc.T("win.board.open_full"),
                BoxPad, btnY, inner, btnH, 20f);
            openBtn.onClick.AddListener(new UnityAction(OnOpenFullClicked));
            LocalizedTextBinder.BindPill(openBtn, "win.board.open_full");
        }

        private void OnPodiumClicked(int index)
        {
            if (index < 0 || index >= _podiumEntries.Length) return;
            CloudRankEntry e = _podiumEntries[index];
            if (e == null || string.IsNullOrEmpty(e.SteamId)) return;
            _host.OpenProfileCard(e);
        }

        private void OnOpenFullClicked()
        {
            _host.OpenFullBoard();
        }

        public void Refresh(MatchCoordinator mc)
        {
            UiKit.Safe(delegate
            {
                LadderSyncService svc = LadderSyncService.Instance;
                int count = svc != null && svc.Entries != null ? svc.Entries.Count : 0;

                if (_lblMeta != null)
                {
                    string clock = svc != null
                        ? CloudLadderClient.FormatRefreshClock(svc.LastRefreshedAtMs)
                        : "--:--:--";
                    int pending = svc != null ? svc.PendingReportCount : 0;
                    string status = svc != null && svc.Status.Kind != LadderSyncService.CloudStatusKind.None
                        ? svc.CloudStatus
                        : CsrLoc.T("cloud.updated", count);
                    _lblMeta.text = status + "   " + CsrLoc.T("win.queue.refreshed", clock)
                        + (pending > 0 ? "   " + CsrLoc.T("cloud.pending_queue", pending) : "");
                }

                if (_lblEmpty != null) _lblEmpty.gameObject.SetActive(count == 0);

                string mySteamId = "";
                try { mySteamId = SteamIdentity.GetSteamId64(); } catch { }

                for (int i = 0; i < 3; i++)
                {
                    bool has = svc != null && svc.Entries != null && i < svc.Entries.Count;
                    if (_podiumRoots[i] != null && _podiumRoots[i].activeSelf != has)
                    {
                        _podiumRoots[i].SetActive(has);
                    }
                    if (!has)
                    {
                        _podiumEntries[i] = null;
                        continue;
                    }

                    CloudRankEntry e = svc.Entries[i];
                    _podiumEntries[i] = e;
                    string tierHex = TierCatalog.GetTierHexColorOnLight(e.Elo);
                    string name = e.DisplayName;
                    if (string.IsNullOrEmpty(name)) name = CsrLoc.T("common.anonymous");
                    bool isMe = !string.IsNullOrEmpty(mySteamId) && e.SteamId == mySteamId;
                    string meTag = isMe ? CsrLoc.T("win.board.you_tag") : "";
                    string shownName = UiKit.FitText(_podiumNames[i], name, meTag.Length * 2.4f);

                    if (_podiumCaps[i] != null && _podiumCaps[i].gameObject.activeSelf != isMe)
                    {
                        _podiumCaps[i].gameObject.SetActive(isMe);
                    }

                    _podiumRanks[i].text = "#" + (i + 1);
                    _podiumNames[i].text = PcsrColors.Wrap(tierHex, shownName) + meTag;
                    _podiumElos[i].text = PcsrColors.WrapNum(tierHex, e.Elo.ToString());
                }

                RefreshMyRank(svc);
            });
        }

        // 我的排名: 优先用云端档案里的 GlobalRank (即使不在 Top50 也有值)
        private void RefreshMyRank(LadderSyncService svc)
        {
            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc == null) return;

            int elo = mc.Profile.CurrentElo;
            string tierHex = TierCatalog.GetTierHexColorOnLight(elo);
            string tierName = TierCatalog.GetTierName(elo);

            int rank = 0;
            CloudProfile my = svc != null ? svc.MyProfile : null;
            if (my != null && my.GlobalRank > 0) rank = my.GlobalRank;

            if (_myRankLine != null)
            {
                string rankText = rank > 0 ? PcsrColors.Wrap(PcsrColors.AccentBlue, "#" + rank) : "-";
                _myRankLine.text = CsrLoc.T("win.board.my_rank", rankText);
            }
            if (_myTierLine != null)
            {
                _myTierLine.text = PcsrColors.WrapBold(tierHex, tierName);
            }
            if (_myEloLine != null)
            {
                _myEloLine.text = PcsrColors.WrapNum(tierHex, elo.ToString())
                    + " <size=60%>" + CsrLoc.T("win.board.elo_unit") + "</size>";
            }
            if (_myRecordLine != null)
            {
                _myRecordLine.text = CsrLoc.T("win.board.record", mc.Profile.Wins, mc.Profile.Losses)
                    + "  ·  " + CsrLoc.T("win.board.my_winrate", mc.Profile.WinRatePercent);
            }
        }

    }
}
