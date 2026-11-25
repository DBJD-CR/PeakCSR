using System;
using RankedPeak.Services;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 玩家资料卡 (Tab 化: 概览 / 最佳时间 / 偏好)
    public sealed class ProfileCardView : MonoBehaviour
    {
        private enum Tab { Overview = 0, Bests = 1, Prefs = 2 }

        private static ProfileCardView _instance;
        public static ProfileCardView Instance { get { return _instance; } }

        private RectTransform _profileRoot;
        private PeakWindowStyle.WindowParts _profileWindow;
        private bool _profileOpen;
        private string _profileSteamId = "";
        private bool _profileIsSelf;
        private bool _profileNickBusy;
        private Tab _tab = Tab.Overview;

        // Tab 按钮
        private Button _tabOverview;
        private Button _tabBests;
        private Button _tabPrefs;
        private readonly GameObject[] _tabPages = new GameObject[3];

        // ---- 概览页 ----
        private TextMeshProUGUI _profileTitle;
        private TextMeshProUGUI _profileMeta;
        private TextMeshProUGUI _profileBody;
        private TextMeshProUGUI _profileModes;
        private TextMeshProUGUI _profileFav;

        // ---- 最佳时间页 ----
        private TextMeshProUGUI _lblAllPeakPb;
        private TextMeshProUGUI _lblAllPeakAt;
        private TextMeshProUGUI _lblBpt;
        private TextMeshProUGUI _lblBptRoute;
        private TextMeshProUGUI _lblAscent7;
        private TextMeshProUGUI _lblAscent7At;
        private TextMeshProUGUI _lblAscent8;
        private TextMeshProUGUI _lblAscent8At;
        private TextMeshProUGUI _lblPbTableHeader;
        private TextMeshProUGUI _lblPbSource;
        // 固定行渲染: 行数与 TrackableBiomes 一样多即可
        private readonly System.Collections.Generic.List<TextMeshProUGUI> _pbRows
            = new System.Collections.Generic.List<TextMeshProUGUI>();

        // ---- 偏好页 ----
        private TextMeshProUGUI _profileNickLabel;
        private TextMeshProUGUI _profileNickHint;
        private TMP_InputField _profileNickInput;
        private Button _profileNickSave;
        private TextMeshProUGUI _profileBiomeLabel;
        private PcsrDropdown _profileBiomeDropdown;
        private TextMeshProUGUI _profileDislikedLabel;
        private PcsrDropdown _profileDislikedDropdown;
        private TextMeshProUGUI _profileCountryLabel;
        private PcsrDropdown _profileCountryDropdown;
        private TextMeshProUGUI _profilePrefHint;

        private void Awake()
        {
            _instance = this;
            CsrLoc.OnLanguageChanged += OnLanguageChanged;
            EventBus.OnPbsUpdated += OnPbsUpdated;
        }

        private void OnDestroy()
        {
            CsrLoc.OnLanguageChanged -= OnLanguageChanged;
            EventBus.OnPbsUpdated -= OnPbsUpdated;
        }

        // 云端 PB 异步到位: 只有当前档案正好是这个人且正停在"最佳时间"页才重绘
        private void OnPbsUpdated(string steamId)
        {
            if (!_profileOpen || _profileRoot == null) return;
            if (!string.Equals(steamId, _profileSteamId, StringComparison.Ordinal)) return;
            if (_tab != Tab.Bests) return;
            RefreshBests();
        }

        public bool IsOpen { get { return _profileOpen; } }

        private void OnLanguageChanged()
        {
            if (_profileBiomeDropdown != null)
            {
                _profileBiomeDropdown.SetOptions(PcsrBiomes.Labels(CsrLoc.Current == CsrLanguage.ZhCn));
            }
            if (_profileDislikedDropdown != null)
            {
                _profileDislikedDropdown.SetOptions(PcsrBiomes.Labels(CsrLoc.Current == CsrLanguage.ZhCn));
            }
            // 国籍选项名含中英文, 切语言必须一起重建, 否则会停在上一种语言
            if (_profileCountryDropdown != null)
            {
                _profileCountryDropdown.SetOptions(
                    PcsrCountries.Labels(CsrLoc.Current == CsrLanguage.ZhCn, CsrLoc.T("profile.country.unset")));
            }
            if (_profileOpen && _profileRoot != null && _profileSteamId != "")
            {
                LadderSyncService svc = LadderSyncService.Instance;
                CloudProfile cached = svc != null ? svc.CachedProfile(_profileSteamId) : null;
                if (cached != null) RenderProfile(_profileSteamId, cached);
                RefreshBests();
            }
        }

        public static void ShowProfileByEntry(CloudRankEntry entry)
        {
            Ensure();
            _instance.ShowProfileCard(entry);
        }

        public static void ShowMyProfile()
        {
            Ensure();
            _instance.OpenMyProfile();
        }

        private static void Ensure()
        {
            if (_instance == null)
            {
                GameObject go = new GameObject("PeakCSR_ProfileCard");
                DontDestroyOnLoad(go);
                go.AddComponent<ProfileCardView>();
            }
        }

        private void ShowProfileCard(CloudRankEntry entry)
        {
            if (_profileRoot == null) BuildProfileUi();
            if (_profileRoot == null) return;

            _profileRoot.gameObject.SetActive(true);
            _profileRoot.SetAsLastSibling();
            _profileOpen = true;
            NativeUiFactory.EnsureCanvasAlive();
            NativeUiFactory.EnsureTopMost();

            RenderProfile(entry.SteamId, CloudProfile.FromEntry(entry));
            RefreshBests();
            SetTab(Tab.Overview);

            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null)
            {
                svc.RequestProfile(entry.SteamId, p =>
                {
                    if (p != null && _profileOpen && _profileSteamId == p.SteamId) RenderProfile(p.SteamId, p);
                });
                // 看别人的 PB 也从云端拉
                svc.PullPbs(entry.SteamId);
            }
        }

        private void RenderProfile(string steamId, CloudProfile p)
        {
            if (p == null) return;
            _profileSteamId = steamId;

            string tierHex = TierCatalog.GetTierHexColorOnLight(p.Elo);
            string tierName = TierCatalog.GetTierName(p.Elo);
            string name = p.DisplayName;
            if (string.IsNullOrEmpty(name)) name = CsrLoc.T("common.anonymous");

            string mySteamId = "";
            try { mySteamId = SteamIdentity.GetSteamId64(); } catch { }
            bool isSelf = !string.IsNullOrEmpty(mySteamId) && steamId == mySteamId;
            _profileIsSelf = isSelf;
            ShowSelfEditors(isSelf);
            if (isSelf && _profileNickInput != null && !_profileNickInput.isFocused)
            {
                _profileNickInput.text = p.Nickname ?? "";
            }
            if (_profileBiomeDropdown != null && !_profileBiomeDropdown.IsOpen)
            {
                int biomeIdx = p.FavoriteMap >= 0 && p.FavoriteMap < PcsrBiomes.Count ? p.FavoriteMap : -1;
                _profileBiomeDropdown.SetValue(biomeIdx, false);
            }
            if (_profileCountryDropdown != null)
            {
                _profileCountryDropdown.SetValue(PcsrCountries.ToDropdownIndex(p.Country), false);
            }
            if (_profileDislikedDropdown != null && !_profileDislikedDropdown.IsOpen)
            {
                int idx = p.DislikedMap >= 0 && p.DislikedMap < PcsrBiomes.Count ? p.DislikedMap : -1;
                _profileDislikedDropdown.SetValue(idx, false);
            }

            if (_profileTitle != null) _profileTitle.text = CsrLoc.T("profile.title", name);
            if (_profileMeta != null)
            {
                string rank = p.GlobalRank > 0 ? ("#" + p.GlobalRank) : "-";
                string peakHex = TierCatalog.GetTierHexColorOnLight(p.PeakElo);
                _profileMeta.text = CsrLoc.T("profile.meta", rank, tierHex, tierName, p.Elo, peakHex, p.PeakElo);
            }
            if (_profileBody != null)
            {
                string streakHex = p.Streak > 0 ? PcsrColors.Positive : (p.Streak < 0 ? PcsrColors.Danger : PcsrColors.StreakNeutral);
                // 自己的最长连胜本地记录最权威 (云端可能还没回灌), 取两者较大值
                int bestStreak = p.BestStreak;
                if (isSelf)
                {
                    MatchCoordinator mc = MatchCoordinator.Instance;
                    if (mc != null && mc.Profile.BestStreak > bestStreak) bestStreak = mc.Profile.BestStreak;
                }
                _profileBody.text = CsrLoc.T("profile.body",
                    p.Wins, p.Losses, p.WinRatePercent, streakHex, p.Streak, bestStreak, p.TotalGames);
            }
            if (_profileModes != null)
            {
                _profileModes.text = CsrLoc.T("profile.modes",
                    p.WinsAllPeak + p.LossesAllPeak, p.WinsAllPeak, p.LossesAllPeak, p.ModeWinRatePercent(0),
                    p.WinsHardcore + p.LossesHardcore, p.WinsHardcore, p.LossesHardcore, p.ModeWinRatePercent(1),
                    p.WinsSprint + p.LossesSprint, p.WinsSprint, p.LossesSprint, p.ModeWinRatePercent(2));
            }
            if (_profileFav != null) _profileFav.text = BuildFavoriteLine(p, isSelf);
        }

        // isSelf 时"最常玩难度/模式"取本地推导结果
        private static string BuildFavoriteLine(CloudProfile p, bool isSelf)
        {
            bool zh = CsrLoc.Current == CsrLanguage.ZhCn;
            string favMap = p.FavoriteMap >= 0 && p.FavoriteMap < PcsrBiomes.Count
                ? PcsrBiomes.Label(p.FavoriteMap, zh)
                : CsrLoc.T("profile.no_data");
            string dislikeMap = p.DislikedMap >= 0 && p.DislikedMap < PcsrBiomes.Count
                ? PcsrBiomes.Label(p.DislikedMap, zh)
                : CsrLoc.T("profile.no_data");

            int favAscent = p.FavoriteAscent;
            int favFormat = p.FavoriteFormat;
            if (isSelf)
            {
                MatchCoordinator mc = MatchCoordinator.Instance;
                if (mc != null)
                {
                    // Profile.FavoriteAscent/Format 已是 Effective (按场次统计推导)
                    if (mc.Profile.FavoriteAscent > -900) favAscent = mc.Profile.FavoriteAscent;
                    if (mc.Profile.FavoriteFormat >= 0) favFormat = mc.Profile.FavoriteFormat;
                }
            }
            string favAsc = favAscent > -900 ? MatchNaming.GetAscentDisplayName(favAscent) : CsrLoc.T("profile.no_data");
            string favFmt = favFormat >= 0
                ? MatchNaming.GetFormatDisplayName((PcsrRuleFormat)favFormat, 0)
                : CsrLoc.T("profile.no_data");

            return CsrLoc.T("profile.favorite",
                PcsrColors.WrapBold(PcsrColors.AccentBlue, favMap),
                PcsrColors.WrapBold(PcsrColors.Danger, dislikeMap),
                PcsrColors.WrapBold(PcsrColors.StreakNeutral, favAsc),
                PcsrColors.WrapBold(PcsrColors.StreakNeutral, favFmt));
        }

        // ===== 最佳时间页 =====

        private void RefreshBests()
        {
            PersonalBestService svc = PersonalBestService.Instance;
            if (svc == null) return;
            bool zh = CsrLoc.Current == CsrLanguage.ZhCn;

            // 关键: 按"当前档案是谁"取快照, 不能直接用单例台账
            PersonalBestSnapshot pb = svc.SnapshotFor(_profileSteamId);
            if (_lblPbSource != null)
            {
                _lblPbSource.text = CsrLoc.T("profile.pb.source");
            }

            long allPeak = pb.AllPeakMs;
            if (_lblAllPeakPb != null)
            {
                // 全山 PB 是档案主展示字段, 用金色与加粗强调;
                // 但数字本体走 WrapNum (只上色不加粗), 避免数字笔画粘连
                _lblAllPeakPb.text = allPeak > 0
                    ? PcsrColors.WrapNum(PcsrColors.GoldNum, CsrFormat.FormatClockMs(allPeak))
                    : CsrLoc.T("profile.pb.unset");
            }
            if (_lblAllPeakAt != null)
            {
                _lblAllPeakAt.text = FormatAchievedAt(allPeak, pb.AllPeakAtMs);
            }

            long bpt = pb.BestPossibleMs;
            if (_lblBpt != null)
            {
                _lblBpt.text = bpt > 0
                    ? PcsrColors.WrapNum(PcsrColors.Positive, CsrFormat.FormatClockMs(bpt))
                    : CsrLoc.T("profile.pb.unset");
            }
            if (_lblBptRoute != null)
            {
                // 算不出来时要说清缺哪一层, 否则玩家分不清"没跑过"与"只差熔炉"
                if (bpt > 0)
                {
                    string desc = pb.DescribeBptRoute(zh);
                    _lblBptRoute.text = string.IsNullOrEmpty(desc) ? "" : CsrLoc.T("profile.bpt.route", desc);
                }
                else
                {
                    string missing = pb.DescribeMissingBptLayer(zh);
                    _lblBptRoute.text = string.IsNullOrEmpty(missing)
                        ? "" : CsrLoc.T("profile.bpt.missing", missing);
                }
            }

            long ascent7 = pb.AscentMs(7);
            long ascent8 = pb.AscentMs(8);
            if (_lblAscent7 != null) _lblAscent7.text = FormatPb(ascent7);
            if (_lblAscent8 != null) _lblAscent8.text = FormatPb(ascent8);
            if (_lblAscent7At != null) _lblAscent7At.text = FormatAchievedAt(ascent7, pb.AscentAtMs(7));
            if (_lblAscent8At != null) _lblAscent8At.text = FormatAchievedAt(ascent8, pb.AscentAtMs(8));

            // 赛段表: 可采集群系逐个一行 (顶峰不单独成段)
            FillPbRows();
        }

        // 逐行填充全部可采集群系 (固定行, 无滚动/无虚拟起点)
        private void FillPbRows()
        {
            PersonalBestService svc = PersonalBestService.Instance;
            if (svc == null) return;

            PersonalBestSnapshot pb = svc.SnapshotFor(_profileSteamId);
            bool zh = CsrLoc.Current == CsrLanguage.ZhCn;
            int[] biomes = BptCalculator.TrackableBiomes();

            for (int i = 0; i < _pbRows.Count; i++)
            {
                TextMeshProUGUI label = _pbRows[i];
                if (label == null) continue;

                if (i >= biomes.Length)
                {
                    UiKit.SetActive(label, false);
                    label.text = string.Empty;
                    continue;
                }
                UiKit.SetActive(label, true);
                int dataIndex = i;

                int biome = biomes[dataIndex];
                string biomeName = PcsrBiomes.Label(biome, zh);

                long sprintMs = pb.SprintMs(biome);
                long segmentMs = pb.SegmentMs(biome);

                // 多列定宽排布: 群系 | 冲刺PB | 达成时间 | 赛段PB | 达成时间
                label.text = PcsrColors.Wrap(PcsrColors.AccentBlue, biomeName)
                    + PbColumn(PbColSprint, FormatPb(sprintMs))
                    + PbColumn(PbColSprintAt, FormatAtCell(sprintMs, pb.SprintAtMs(biome)))
                    + PbColumn(PbColSegment, FormatPb(segmentMs))
                    + PbColumn(PbColSegmentAt, FormatAtCell(segmentMs, pb.SegmentAtMs(biome)));
            }
        }

        private static string FormatPb(long ms)
        {
            return ms > 0 ? PcsrColors.WrapNum(PcsrColors.Positive, CsrFormat.FormatClockMs(ms))
                          : PcsrColors.Wrap(PcsrColors.Muted, "--:--.---");
        }

        // 达成时间单元格: 没有成绩就没有时间; 有成绩但云端老数据缺时间时用 -- 占位
        private static string FormatAtCell(long pbMs, long atMs)
        {
            if (pbMs <= 0) return PcsrColors.Wrap(PcsrColors.Muted, CsrLoc.T("profile.pb.at_unset"));
            string text = CsrFormat.FormatAchievedAt(atMs);
            return PcsrColors.Wrap(PcsrColors.Muted,
                string.IsNullOrEmpty(text) ? CsrLoc.T("profile.pb.at_unset") : text);
        }

        // 卡片内的达成时间 (全山/天阶): 只有真的达成了才显示
        private static string FormatAchievedAt(long pbMs, long atMs)
        {
            if (pbMs <= 0) return "";
            string text = CsrFormat.FormatAchievedAt(atMs);
            if (string.IsNullOrEmpty(text)) return "";
            return PcsrColors.WrapNum(PcsrColors.Muted, CsrLoc.T("profile.pb.at", text));
        }

        // PB 表格列 (相对行左缘), 表头与数据行共用同一组常量
        // 5 列: 群系 | 冲刺PB | 达成时间 | 赛段PB | 达成时间
        private const float PbColSprint = 120f;
        private const float PbColSprintAt = 240f;
        private const float PbColSegment = 400f;
        private const float PbColSegmentAt = 520f;
        // 列宽基准: 窗口 840 - body 左右 26*2 = 788
        private const float PbInner = 788f;

        private static string PbColumn(float x, string text)
        {
            int pct = Mathf.Clamp(Mathf.RoundToInt(x / PbInner * 100f), 0, 99);
            return "<pos=" + pct + "%>" + text;
        }

        // ===== 偏好 =====

        private void OnBiomeChanged(int biomeIndex)
        {
            if (!_profileIsSelf) return;
            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc == null) return;

            mc.Profile.SetFavoriteMap(biomeIndex);
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null)
            {
                svc.PushProfile(biomeIndex, mc.Profile.DislikedMap, mc.Profile.FavoriteAscent,
                    mc.Profile.FavoriteFormat, "", true, null);
            }
            // 最爱可能顶掉了原有的最讨厌, 下拉要跟着同步
            if (_profileDislikedDropdown != null)
            {
                _profileDislikedDropdown.SetValue(mc.Profile.DislikedMap, false);
            }
            RefreshPrefPreview();
        }

        private void OnCountryChanged(int dropdownIndex)
        {
            if (!_profileIsSelf) return;
            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc == null) return;

            // 下拉索引 0 = 未设置, 其余映射到国家码
            string country = PcsrCountries.FromDropdownIndex(dropdownIndex);
            mc.Profile.Country = country;

            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null)
            {
                // 只改国籍, 昵称保持云端现值
                svc.PushProfile(mc.Profile.FavoriteMap, mc.Profile.DislikedMap, mc.Profile.FavoriteAscent,
                    mc.Profile.FavoriteFormat, country, "", true, null);
            }
            if (_profilePrefHint != null) _profilePrefHint.text = CsrLoc.T("profile.country.saved");
        }

        private void OnDislikedChanged(int biomeIndex)
        {
            if (!_profileIsSelf) return;
            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc == null) return;

            if (!mc.Profile.SetDislikedMap(biomeIndex))
            {
                // 与最爱冲突: 回退到当前值并提示
                if (_profileDislikedDropdown != null)
                {
                    _profileDislikedDropdown.SetValue(mc.Profile.DislikedMap, false);
                }
                if (_profilePrefHint != null)
                {
                    _profilePrefHint.text = CsrLoc.T("profile.disliked.conflict");
                }
                return;
            }

            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null)
            {
                svc.PushProfile(mc.Profile.FavoriteMap, biomeIndex, mc.Profile.FavoriteAscent,
                    mc.Profile.FavoriteFormat, "", true, null);
            }
            RefreshPrefPreview();
        }

        private void RefreshPrefPreview()
        {
            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc == null || _profileFav == null) return;
            CloudProfile preview = new CloudProfile
            {
                FavoriteMap = mc.Profile.FavoriteMap,
                DislikedMap = mc.Profile.DislikedMap,
                FavoriteAscent = mc.Profile.FavoriteAscent,
                FavoriteFormat = mc.Profile.FavoriteFormat
            };
            _profileFav.text = BuildFavoriteLine(preview, true);
            if (_profilePrefHint != null) _profilePrefHint.text = CsrLoc.T("profile.pref.saved");
        }

        // ===== 构建 UI =====

        private void BuildProfileUi()
        {
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return;

            GameObject rootGo = new GameObject("PcsrProfileRoot", typeof(RectTransform));
            rootGo.transform.SetParent(canvas.transform, false);
            _profileRoot = (RectTransform)rootGo.transform;
            _profileRoot.anchorMin = Vector2.zero;
            _profileRoot.anchorMax = Vector2.one;
            _profileRoot.offsetMin = Vector2.zero;
            _profileRoot.offsetMax = Vector2.zero;

            PeakWindowStyle.CreateBackdrop(_profileRoot, "ProfileBackdrop");

            const float w = 840f;
            const float h = 760f;
            const float titleH = 74f;
            _profileWindow = PeakWindowStyle.CreateWindow(_profileRoot, "PcsrProfile", w, h, titleH,
                CsrLoc.T("profile.window.title"), "");

            TextMeshProUGUI title = _profileWindow.Title;
            if (title != null)
            {
                RectTransform rt = title.transform as RectTransform;
                // 标题只占到 Tab 起始位置之前
                if (rt != null) NativeUiFactory.Place(rt, 30f, 0f, 200f, titleH);
                title.textWrappingMode = TextWrappingModes.NoWrap;
                title.overflowMode = TextOverflowModes.Overflow;
                title.alignment = TextAlignmentOptions.Left;
                LocalizedTextBinder.Bind(title, "profile.window.title");
            }

            // Tab 直接坐在标题栏里: 页面内容从 body 顶部起排
            const float tabW = 150f;
            const float tabY = 17f;
            const float tabH = 40f;
            _tabOverview = PeakWindowStyle.CreatePill(_profileWindow.TitleBar, "TabOverview",
                CsrLoc.T("profile.tab.overview"), 240f, tabY, tabW, tabH, 18f);
            _tabOverview.onClick.AddListener(new UnityAction(() => SetTab(Tab.Overview)));
            _tabBests = PeakWindowStyle.CreatePill(_profileWindow.TitleBar, "TabBests",
                CsrLoc.T("profile.tab.bests"), 400f, tabY, tabW, tabH, 18f);
            _tabBests.onClick.AddListener(new UnityAction(() => SetTab(Tab.Bests)));
            _tabPrefs = PeakWindowStyle.CreatePill(_profileWindow.TitleBar, "TabPrefs",
                CsrLoc.T("profile.tab.prefs"), 560f, tabY, tabW, tabH, 18f);
            _tabPrefs.onClick.AddListener(new UnityAction(() => SetTab(Tab.Prefs)));
            LocalizedTextBinder.BindPill(_tabOverview, "profile.tab.overview");
            LocalizedTextBinder.BindPill(_tabBests, "profile.tab.bests");
            LocalizedTextBinder.BindPill(_tabPrefs, "profile.tab.prefs");
            UiKit.TintOnBar(_tabOverview);
            UiKit.TintOnBar(_tabBests);
            UiKit.TintOnBar(_tabPrefs);

            Button close = PeakWindowStyle.CreatePill(_profileWindow.TitleBar, "ProfCloseX", "X",
                w - 14f - 68f, 18f, 46f, 40f, 24f);
            UiKit.TintOnBar(close);
            close.onClick.AddListener(new UnityAction(CloseProfileCard));

            RectTransform body = _profileWindow.Body;
            float inner = w - 52f;

            // 三个页面容器
            _tabPages[0] = BuildOverviewPage(body, inner, h, titleH);
            _tabPages[1] = BuildBestsPage(body, inner, h, titleH);
            _tabPages[2] = BuildPrefsPage(body, inner, h, titleH);

            _profileRoot.gameObject.SetActive(false);
        }

        private GameObject BuildOverviewPage(RectTransform body, float inner, float w, float titleH)
        {
            GameObject page = NewPage(body, "PageOverview", titleH);
            RectTransform pt = (RectTransform)page.transform;

            // 标题内容是动态的 (玩家名), 不能挂 binder
            _profileTitle = UiKit.CreateText(pt, "ProfName", "", 34f, PeakWindowStyle.Heading, 26f, 8f, inner, 44f);

            _profileMeta = UiKit.CreateText(pt, "ProfMeta", "", 22f, PeakWindowStyle.TextPrimary, 26f, 58f, inner, 30f);
            PeakWindowStyle.CreateSeparator(pt, "ProfSep1", 26f, 92f, inner);

            _profileBody = UiKit.CreateText(pt, "ProfBody", "", 22f, PeakWindowStyle.TextPrimary, 26f, 104f, inner, 32f);
            PeakWindowStyle.CreateSeparator(pt, "ProfSep2", 26f, 142f, inner);

            _profileModes = UiKit.CreateText(pt, "ProfModes", "", 21f, PeakWindowStyle.TextPrimary, 26f, 154f, inner, 132f, wrap: true);
            PeakWindowStyle.CreateSeparator(pt, "ProfSep3", 26f, 292f, inner);

            // 最常玩难度/模式已独立成第二行, 这里留够两行高度
            _profileFav = UiKit.CreateText(pt, "ProfFav", "", 21f, PeakWindowStyle.TextMuted, 26f, 304f, inner, 84f, wrap: true);
            return page;
        }

        private GameObject BuildBestsPage(RectTransform body, float inner, float w, float titleH)
        {
            GameObject page = NewPage(body, "PageBests", titleH);
            RectTransform pt = (RectTransform)page.transform;

            // 主展示字段: 标准全山 PB — 标签与时间同一行, 超大字号的视觉重心
            RectTransform mainBox = PeakWindowStyle.CreateBox((RectTransform)pt, "AllPeakBox",
                "", 26f, 8f, inner, 114f);
            UiKit.CreateText(mainBox, "AllPeakLabel", CsrLoc.T("profile.pb.allpeak"), 24f,
                PeakWindowStyle.Heading, 24f, 22f, 300f, 56f);
            LocalizedTextBinder.Bind(mainBox.transform.Find("AllPeakLabel").GetComponent<TMP_Text>(), "profile.pb.allpeak");
            // 达成时间放在标签下方, 不与超大号时间抢同一行
            _lblAllPeakAt = UiKit.CreateTextRight(mainBox, "AllPeakAt", "", 13f,
                PeakWindowStyle.TextMuted, 24f, 90f, inner - 48f, 20f);
            _lblAllPeakAt.alignment = TextAlignmentOptions.Right;
            _lblAllPeakPb = UiKit.CreateTextRight(mainBox, "AllPeakVal", "", 54f,
                PeakWindowStyle.TextPrimary, 24f, 14f, inner - 48f, 72f);
            _lblAllPeakPb.alignment = TextAlignmentOptions.Right;

            // 次行: BPT + 天阶7 + 天阶8 (三卡等分, 吃掉最后一卡的右侧余量)
            float cardW = (inner - 24f) / 3f;
            TextMeshProUGUI asc7Hint;
            TextMeshProUGUI asc8Hint;
            _lblBpt = BuildStatCard(pt, "BptBox", "profile.pb.bpt", 26f, 134f, cardW, out _lblBptRoute);
            _lblAscent7 = BuildStatCard(pt, "Asc7Box", "profile.pb.ascent7", 26f + cardW + 12f, 134f, cardW, out asc7Hint);
            _lblAscent8 = BuildStatCard(pt, "Asc8Box", "profile.pb.ascent8", 26f + (cardW + 12f) * 2f, 134f, cardW, out asc8Hint);
            // 天阶卡不需要说明行, 直接隐掉, 把位置让给达成时间
            UiKit.SetActive(asc7Hint, false);
            UiKit.SetActive(asc8Hint, false);
            // 天阶卡同样补达成时间 (小字放在数值下方)
            _lblAscent7At = BuildStatCardAt(pt, "Asc7Box", cardW);
            _lblAscent8At = BuildStatCardAt(pt, "Asc8Box", cardW);

            // BPT 说明行是"最优路线: 海岸 + 森蕈 + 雪山 + 火山 → ..."这种长文本,
            // 单行且限宽必然溢出。这里放开换行并给出两行高度, 卡片也相应加高
            ConfigureBptRouteHint(_lblBptRoute, cardW);
            float tableTop = 134f + StatCardHeight + 8f;

            // 赛段表: 表头与数据行共用 PbColumn 的列坐标, 保证严格对齐
            string header = CsrLoc.T("profile.pb.table_header")
                + PbColumn(PbColSprint, CsrLoc.T("profile.pb.col_sprint"))
                + PbColumn(PbColSprintAt, CsrLoc.T("profile.pb.col_at"))
                + PbColumn(PbColSegment, CsrLoc.T("profile.pb.col_segment"))
                + PbColumn(PbColSegmentAt, CsrLoc.T("profile.pb.col_at"));
            // 表头字号与数据行一致
            _lblPbTableHeader = UiKit.CreateText(pt, "PbTableHeader", header,
                17f, PeakWindowStyle.Heading, 26f, tableTop, inner, 30f);
            _lblPbTableHeader.textWrappingMode = TextWrappingModes.NoWrap;
            // 来源提示固定在表格区右下角落
            _lblPbSource = UiKit.CreateTextRight(pt, "PbSource", CsrLoc.T("profile.pb.source"),
                13f, PeakWindowStyle.TextMuted, 26f + (inner - 240f), 640f, 240f, 20f);
            PeakWindowStyle.CreateSeparator(pt, "PbTableSep", 26f, tableTop + 36f, inner);

            // 赛段 PB 用固定行渲染, 不走虚拟滚动池
            _pbRows.Clear();
            float rowTop = tableTop + 44f;
            for (int i = 0; i < PbMaxRows; i++)
            {
                TextMeshProUGUI row = UiKit.CreateText(pt, "PbRow" + i, "", 17f,
                    PeakWindowStyle.TextPrimary, 26f, rowTop + i * PbRowHeight, inner, PbRowHeight);
                row.textWrappingMode = TextWrappingModes.NoWrap;
                row.overflowMode = TextOverflowModes.Overflow;
                row.margin = Vector4.zero;
                UiKit.SetActive(row, false);
                _pbRows.Add(row);
            }
            return page;
        }

        // 可采集群系最多 11 个, 留一格余量
        private const int PbMaxRows = 11;
        private const float PbRowHeight = 30f;

        // 统计小卡: 返回数值控件, 可选说明行
        private const float StatCardHeight = 116f;

        private static TextMeshProUGUI BuildStatCard(RectTransform parent, string name, string locKey,
            float x, float y, float w, out TextMeshProUGUI hint)
        {
            RectTransform box = PeakWindowStyle.CreateBox(parent, name, CsrLoc.T(locKey), x, y, w, StatCardHeight);
            LocalizedTextBinder.BindBoxHeading(box, locKey);
            TextMeshProUGUI val = UiKit.CreateText(box, name + "Val", "", 26f, PeakWindowStyle.TextPrimary, 20f, 44f, w - 40f, 34f);
            hint = UiKit.CreateText(box, name + "Hint", "", 13f, PeakWindowStyle.TextMuted, 20f, 74f, w - 40f, 34f, wrap: true);
            return val;
        }

        // 天阶卡的达成时间行
        private static TextMeshProUGUI BuildStatCardAt(RectTransform parent, string boxName, float cardW)
        {
            Transform found = parent.Find(boxName);
            RectTransform box = found as RectTransform;
            if (box == null) return null;
            return UiKit.CreateText(box, boxName + "At", "", 13f,
                PeakWindowStyle.TextMuted, 20f, 74f, cardW - 40f, 18f);
        }

        // BPT 路线说明: 允许换行并压到 12px, 两行内可容纳完整路线描述
        private static void ConfigureBptRouteHint(TextMeshProUGUI hint, float cardWidth)
        {
            if (hint == null) return;
            hint.textWrappingMode = TextWrappingModes.Normal;
            hint.overflowMode = TextOverflowModes.Ellipsis;
            hint.fontSize = 12f;
            RectTransform rt = hint.transform as RectTransform;
            if (rt != null) rt.sizeDelta = new Vector2(cardWidth - 40f, 34f);
        }

        private GameObject BuildPrefsPage(RectTransform body, float inner, float w, float titleH)
        {
            GameObject page = NewPage(body, "PagePrefs", titleH);
            RectTransform pt = (RectTransform)page.transform;

            _profileNickLabel = UiKit.CreateText(pt, "ProfNickLabel", CsrLoc.T("profile.nick.label"),
                20f, PeakWindowStyle.Heading, 26f, 8f, inner, 28f);
            LocalizedTextBinder.Bind(_profileNickLabel, "profile.nick.label");
            _profileNickInput = PeakWindowStyle.CreateInputField((RectTransform)pt, "ProfNickInput", "", 26f, 40f, 380f, 42f, 18f);
            UiKit.ConfigurePlainInput(_profileNickInput, 16, false);
            _profileNickSave = PeakWindowStyle.CreatePill((RectTransform)pt, "ProfNickSave", CsrLoc.T("profile.nick.save"),
                418f, 40f, 150f, 42f, 17f);
            _profileNickSave.onClick.AddListener(new UnityAction(OnSaveNicknameClicked));
            LocalizedTextBinder.BindPill(_profileNickSave, "profile.nick.save");

            _profileNickHint = UiKit.CreateText(pt, "ProfNickHint", CsrLoc.T("profile.nick.hint"),
                15f, PeakWindowStyle.TextMuted, 26f, 88f, inner, 36f, wrap: true);
            LocalizedTextBinder.Bind(_profileNickHint, "profile.nick.hint");

            PeakWindowStyle.CreateSeparator(pt, "PrefSep", 26f, 130f, inner);

            // 群系三列并排: 最爱 | 最讨厌 | 国籍, 高度一致不多占纵向空间
            const float prefColW = 240f;
            const float prefColGap = 14f;
            const float prefCol2X = 26f + prefColW + prefColGap;
            const float prefCol3X = prefCol2X + prefColW + prefColGap;

            _profileBiomeLabel = UiKit.CreateText(pt, "ProfBiomeLabel", CsrLoc.T("profile.biome.label"),
                20f, PeakWindowStyle.Heading, 26f, 142f, prefColW, 28f);
            LocalizedTextBinder.Bind(_profileBiomeLabel, "profile.biome.label");
            _profileBiomeDropdown = PcsrDropdown.Create((RectTransform)pt, "ProfBiomeDropdown",
                26f, 174f, prefColW, 42f, PcsrBiomes.Labels(CsrLoc.Current == CsrLanguage.ZhCn), -1, 17f);
            _profileBiomeDropdown.OnValueChanged = OnBiomeChanged;
            _profileBiomeDropdown.SetValue(-1, false);

            _profileDislikedLabel = UiKit.CreateText(pt, "ProfDislikedLabel", CsrLoc.T("profile.disliked.label"),
                20f, PeakWindowStyle.Heading, prefCol2X, 142f, prefColW, 28f);
            LocalizedTextBinder.Bind(_profileDislikedLabel, "profile.disliked.label");
            _profileDislikedDropdown = PcsrDropdown.Create((RectTransform)pt, "ProfDislikedDropdown",
                prefCol2X, 174f, prefColW, 42f, PcsrBiomes.Labels(CsrLoc.Current == CsrLanguage.ZhCn), -1, 17f);
            _profileDislikedDropdown.OnValueChanged = OnDislikedChanged;
            _profileDislikedDropdown.SetValue(-1, false);

            // 国籍: 索引 0 固定为"未设置", 与 PcsrCountries.ToDropdownIndex 对应
            _profileCountryLabel = UiKit.CreateText(pt, "ProfCountryLabel", CsrLoc.T("profile.country.label"),
                20f, PeakWindowStyle.Heading, prefCol3X, 142f, prefColW, 28f);
            LocalizedTextBinder.Bind(_profileCountryLabel, "profile.country.label");
            _profileCountryDropdown = PcsrDropdown.Create((RectTransform)pt, "ProfCountryDropdown",
                prefCol3X, 174f, prefColW, 42f,
                PcsrCountries.Labels(CsrLoc.Current == CsrLanguage.ZhCn, CsrLoc.T("profile.country.unset")), 0, 17f);
            _profileCountryDropdown.OnValueChanged = OnCountryChanged;
            _profileCountryDropdown.SetValue(0, false);

            _profilePrefHint = UiKit.CreateText(pt, "ProfPrefHint", "", 15f, PeakWindowStyle.TextMuted,
                26f, 232f, inner, 40f, wrap: true);
            return page;
        }

        private static GameObject NewPage(RectTransform body, string name, float titleH)
        {
            GameObject page = new GameObject(name, typeof(RectTransform));
            page.transform.SetParent(body, false);
            RectTransform rt = (RectTransform)page.transform;
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            return page;
        }

        private void SetTab(Tab tab)
        {
            _tab = tab;
            for (int i = 0; i < _tabPages.Length; i++)
            {
                if (_tabPages[i] != null) _tabPages[i].SetActive(i == (int)tab);
            }
            ApplyTabStyle(_tabOverview, tab == Tab.Overview);
            ApplyTabStyle(_tabBests, tab == Tab.Bests);
            ApplyTabStyle(_tabPrefs, tab == Tab.Prefs);

            // 切到"最佳时间"页时才刷新一次, 避免每帧计算 BPT
            if (tab == Tab.Bests) RefreshBests();
        }

        // Tab 选中态对比度调优:
        // 标题栏本身是饱和蓝, 选中态再用默认 Pill 蓝几乎看不出差别,
        // 因此选中态换紫色强调底 + 白字加粗, 未选中态压成半透明白, 靠明度差拉开层次
        private static void ApplyTabStyle(Button pill, bool selected)
        {
            if (pill == null) return;
            Image img = pill.GetComponent<Image>();
            if (img != null)
            {
                img.color = selected
                    ? new Color(0.38f, 0.20f, 0.72f, 1f)
                    : new Color(1f, 1f, 1f, 0.18f);
            }
            TextMeshProUGUI label = pill.GetComponentInChildren<TextMeshProUGUI>(true);
            if (label != null)
            {
                label.fontStyle = selected ? FontStyles.Bold : FontStyles.Normal;
                label.color = selected ? Color.white : new Color(1f, 1f, 1f, 0.82f);
            }
        }

        private void OpenMyProfile()
        {
            string mySteamId = "";
            try { mySteamId = SteamIdentity.GetSteamId64(); } catch { }
            if (string.IsNullOrEmpty(mySteamId))
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.window.title"), CsrLoc.T("profile.no_steam"));
                return;
            }

            LadderSyncService svc = LadderSyncService.Instance;
            if (svc == null)
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.window.title"), CsrLoc.T("profile.steam_only"));
                return;
            }

            if (_profileRoot == null) BuildProfileUi();
            if (_profileRoot == null) return;

            _profileRoot.gameObject.SetActive(true);
            _profileRoot.SetAsLastSibling();
            _profileOpen = true;
            _profileIsSelf = true;
            _profileSteamId = mySteamId;
            NativeUiFactory.EnsureCanvasAlive();
            NativeUiFactory.EnsureTopMost();
            SetTab(Tab.Overview);

            CloudProfile cached = svc.CachedProfile(mySteamId);
            if (cached != null)
            {
                RenderProfile(mySteamId, cached);
                ShowSelfEditors(true);
            }
            else
            {
                ShowSelfEditors(false);
                RenderSelfLoading();
            }
            RefreshBests();

            svc.RequestMyProfile(p =>
            {
                if (p != null && _profileOpen && _profileSteamId == p.SteamId)
                {
                    RenderProfile(p.SteamId, p);
                    ShowSelfEditors(true);
                }
            });
            // 先推本地新刷出的, 再从云端把已有的拉回来合并
            svc.SyncPendingPbs();
            svc.PullPbs(mySteamId);
        }

        private void ShowSelfEditors(bool enabled)
        {
            UiKit.SetActive(_profileNickLabel, enabled);
            UiKit.SetActive(_profileNickInput, enabled);
            UiKit.SetActive(_profileNickSave, enabled);
            UiKit.SetActive(_profileNickHint, enabled);
            UiKit.SetActive(_profileBiomeLabel, enabled);
            UiKit.SetActive(_profileBiomeDropdown, enabled);
            UiKit.SetActive(_profileDislikedLabel, enabled);
            UiKit.SetActive(_profileDislikedDropdown, enabled);
            UiKit.SetActive(_profileCountryLabel, enabled);
            UiKit.SetActive(_profileCountryDropdown, enabled);
            if (enabled && _profileNickSave != null) _profileNickSave.interactable = !_profileNickBusy;
        }

        private void RenderSelfLoading()
        {
            if (_profileTitle != null) _profileTitle.text = CsrLoc.T("profile.window.title");
            if (_profileMeta != null) _profileMeta.text = CsrLoc.T("cloud.loading");
            if (_profileBody != null) _profileBody.text = "";
            if (_profileModes != null) _profileModes.text = "";
            if (_profileFav != null) _profileFav.text = "";
            if (_profileNickInput != null && !_profileNickInput.isFocused) _profileNickInput.text = "";
        }

        private void OnSaveNicknameClicked()
        {
            if (!_profileIsSelf || _profileNickBusy) return;

            string mySteamId = "";
            try { mySteamId = SteamIdentity.GetSteamId64(); } catch { }
            if (string.IsNullOrEmpty(mySteamId))
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.window.title"), CsrLoc.T("profile.no_steam"));
                return;
            }
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc == null || !svc.IsCloudConfigured())
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.window.title"), CsrLoc.T("profile.nick.offline"));
                return;
            }

            string raw = _profileNickInput != null ? _profileNickInput.text : "";
            string nick = NicknamePolicy.Sanitize(raw);
            if (!string.IsNullOrEmpty(raw) && string.IsNullOrEmpty(nick))
            {
                PeakCsrPrompt.Info(CsrLoc.T("profile.nick.label"), CsrLoc.T("profile.nick.failed", "invalid"));
                return;
            }

            _profileNickBusy = true;
            if (_profileNickSave != null) _profileNickSave.interactable = false;

            MatchCoordinator mc = MatchCoordinator.Instance;
            int favMap = mc != null ? mc.Profile.FavoriteMap : -1;
            int dislikeMap = mc != null ? mc.Profile.DislikedMap : -1;
            int favAsc = mc != null ? mc.Profile.FavoriteAscent : -999;
            int favFmt = mc != null ? mc.Profile.FavoriteFormat : -1;
            string country = mc != null ? mc.Profile.Country : PcsrCountries.Unset;
            svc.PushProfile(favMap, dislikeMap, favAsc, favFmt, country, nick, false, (ok, err) =>
            {
                _profileNickBusy = false;
                if (_profileNickSave != null) _profileNickSave.interactable = true;
                if (ok)
                {
                    PeakCsrPrompt.Info(CsrLoc.T("profile.nick.label"), CsrLoc.T("profile.nick.saved"));
                    PcsrMenuWindow host = PcsrMenuWindow.Instance;
                    if (host != null) host.RefreshAll();
                    return;
                }
                PeakCsrPrompt.Info(CsrLoc.T("profile.nick.label"),
                    CsrLoc.T("profile.nick.failed", DescribeProfileError(err)));
            });
        }

        private static string DescribeProfileError(string err)
        {
            if (string.IsNullOrEmpty(err)) return "unknown";
            if (err.StartsWith("Nickname cooldown:", StringComparison.Ordinal))
            {
                string h = err.Substring("Nickname cooldown:".Length);
                return CsrLoc.Current == CsrLanguage.ZhCn
                    ? ("昵称 " + h + " 小时后再试")
                    : ("cooldown, retry in " + h + "h");
            }
            if (err == "Nickname already taken")
            {
                return CsrLoc.Current == CsrLanguage.ZhCn ? "该昵称已被占用" : "nickname already taken";
            }
            return err;
        }

        public void CloseProfileCard()
        {
            if (_profileBiomeDropdown != null) _profileBiomeDropdown.Close();
            if (_profileDislikedDropdown != null) _profileDislikedDropdown.Close();
            if (_profileCountryDropdown != null) _profileCountryDropdown.Close();
            if (_profileRoot != null) _profileRoot.gameObject.SetActive(false);
            // 别人的 PB 只是展示用, 关窗就释放, 避免内存长期累积
            PersonalBestService pb = PersonalBestService.Instance;
            if (pb != null && !_profileIsSelf) pb.ForgetRemote(_profileSteamId);
            _profileOpen = false;
            _profileSteamId = "";
        }
    }
}
