using System;
using RankedPeak.Services;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 玩家资料卡独立弹窗 + 昵称/最爱群系编辑 (原 PcsrMenuWindow 档案卡面板拆分)
    public sealed class ProfileCardView : MonoBehaviour
    {
        private static ProfileCardView _instance;
        public static ProfileCardView Instance { get { return _instance; } }

        private RectTransform _profileRoot;
        private PeakWindowStyle.WindowParts _profileWindow;
        private TextMeshProUGUI _profileTitle;
        private TextMeshProUGUI _profileBody;
        private TextMeshProUGUI _profileModes;
        private TextMeshProUGUI _profileFav;
        private TextMeshProUGUI _profileMeta;
        private TextMeshProUGUI _profileNickLabel;
        private TextMeshProUGUI _profileNickHint;
        private TMP_InputField _profileNickInput;
        private Button _profileNickSave;
        private TextMeshProUGUI _profileBiomeLabel;
        private PcsrDropdown _profileBiomeDropdown;
        private bool _profileOpen;
        private string _profileSteamId = "";
        // 当前档案卡是不是自己 (决定昵称编辑区是否出现)
        private bool _profileIsSelf;
        private bool _profileNickBusy;

        private void Awake()
        {
            _instance = this;
            CsrLoc.OnLanguageChanged += OnLanguageChanged;
        }

        private void OnDestroy()
        {
            CsrLoc.OnLanguageChanged -= OnLanguageChanged;
        }

        public bool IsOpen { get { return _profileOpen; } }

        // 语言热切换: 原地刷新群系下拉选项文案, 不销毁 UI 树
        private void OnLanguageChanged()
        {
            if (_profileBiomeDropdown != null)
            {
                _profileBiomeDropdown.SetOptions(PcsrBiomes.Labels(CsrLoc.Current == CsrLanguage.ZhCn));
            }
            if (_profileOpen && _profileRoot != null && _profileSteamId != "")
            {
                LadderSyncService svc = LadderSyncService.Instance;
                CloudProfile cached = svc != null ? svc.CachedProfile(_profileSteamId) : null;
                if (cached != null) RenderProfile(_profileSteamId, cached);
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

            RenderProfile(entry.SteamId, BuildProfileFromEntry(entry));
            LadderSyncService svc = LadderSyncService.Instance;
            if (svc != null)
            {
                svc.RequestProfile(entry.SteamId, p =>
                {
                    if (p != null && _profileOpen && _profileSteamId == p.SteamId) RenderProfile(p.SteamId, p);
                });
            }
        }

        private static CloudProfile BuildProfileFromEntry(CloudRankEntry e)
        {
            return CloudProfile.FromEntry(e);
        }

        private void RenderProfile(string steamId, CloudProfile p)
        {
            if (p == null) return;
            _profileSteamId = steamId;

            string tierHex = TierCatalog.GetTierHexColorOnLight(p.Elo);
            string tierName = TierCatalog.GetTierName(p.Elo);
            string name = p.DisplayName;
            if (string.IsNullOrEmpty(name)) name = CsrLoc.T("common.anonymous");

            // 是否是自己的档案决定昵称编辑区显隐
            string mySteamId = "";
            try { mySteamId = SteamIdentity.GetSteamId64(); } catch { }
            bool isSelf = !string.IsNullOrEmpty(mySteamId) && steamId == mySteamId;
            _profileIsSelf = isSelf;
            UiKit.SetActive(_profileNickLabel, isSelf);
            UiKit.SetActive(_profileNickInput, isSelf);
            UiKit.SetActive(_profileNickSave, isSelf);
            UiKit.SetActive(_profileNickHint, isSelf);
            UiKit.SetActive(_profileBiomeLabel, isSelf);
            UiKit.SetActive(_profileBiomeDropdown, isSelf);
            if (isSelf && _profileNickInput != null && !_profileNickInput.isFocused)
            {
                _profileNickInput.text = p.Nickname ?? "";
            }
            if (_profileBiomeDropdown != null && !_profileBiomeDropdown.IsOpen)
            {
                // p.FavoriteMap 现在是群系索引, 越界或未设置都按"未选择"处理
                int biomeIdx = p.FavoriteMap >= 0 && p.FavoriteMap < PcsrBiomes.Count ? p.FavoriteMap : -1;
                _profileBiomeDropdown.SetValue(biomeIdx, false);
            }

            if (_profileTitle != null)
            {
                _profileTitle.text = CsrLoc.T("profile.title", name);
            }
            if (_profileMeta != null)
            {
                string rank = p.GlobalRank > 0 ? ("#" + p.GlobalRank) : "-";
                string peakHex = TierCatalog.GetTierHexColorOnLight(p.PeakElo);
                _profileMeta.text = CsrLoc.T("profile.meta",
                    rank,
                    tierHex, tierName,
                    p.Elo,
                    peakHex, p.PeakElo);
            }
            if (_profileBody != null)
            {
                string streakHex = p.Streak > 0 ? PcsrColors.Positive : (p.Streak < 0 ? PcsrColors.Danger : PcsrColors.StreakNeutral);
                _profileBody.text = CsrLoc.T("profile.body",
                    p.Wins, p.Losses, p.WinRatePercent, streakHex, p.Streak, p.TotalGames);
            }
            if (_profileModes != null)
            {
                _profileModes.text = CsrLoc.T("profile.modes",
                    p.WinsAllPeak + p.LossesAllPeak, p.WinsAllPeak, p.LossesAllPeak, p.ModeWinRatePercent(0),
                    p.WinsHardcore + p.LossesHardcore, p.WinsHardcore, p.LossesHardcore, p.ModeWinRatePercent(1),
                    p.WinsSprint + p.LossesSprint, p.WinsSprint, p.LossesSprint, p.ModeWinRatePercent(2));
            }
            if (_profileFav != null)
            {
                _profileFav.text = BuildFavoriteLine(p);
            }
        }

        // 最爱行: 群系玩家自选; 难度与模式由真实对局场次推导, 不做手选
        private static string BuildFavoriteLine(CloudProfile p)
        {
            bool zh = CsrLoc.Current == CsrLanguage.ZhCn;
            string favMap = p.FavoriteMap >= 0 && p.FavoriteMap < PcsrBiomes.Count
                ? PcsrBiomes.Label(p.FavoriteMap, zh)
                : CsrLoc.T("profile.none");

            int favAscent = p.FavoriteAscent;
            string favAsc = favAscent > -900
                ? MatchNaming.GetAscentDisplayName(favAscent)
                : CsrLoc.T("profile.none");

            string favFmt = p.FavoriteFormat >= 0
                ? MatchNaming.GetFormatDisplayName((McsrRuleFormat)p.FavoriteFormat, 0)
                : CsrLoc.T("profile.none");

            return CsrLoc.T("profile.favorite",
                PcsrColors.WrapBold(PcsrColors.AccentBlue, favMap),
                PcsrColors.WrapBold(PcsrColors.StreakNeutral, favAsc),
                PcsrColors.WrapBold(PcsrColors.StreakNeutral, favFmt));
        }

        // 群系下拉变化: 写入本地并同步云端 (难度/模式沿用当前推导值)
        private void OnBiomeChanged(int biomeIndex)
        {
            if (!_profileIsSelf) return;

            MatchCoordinator mc = MatchCoordinator.Instance;
            if (mc != null)
            {
                mc.Profile.SetFavoriteMap(biomeIndex);
                LadderSyncService svc = LadderSyncService.Instance;
                if (svc != null)
                {
                    svc.PushFavoriteProfile(biomeIndex, mc.Profile.FavoriteAscent, mc.Profile.FavoriteFormat);
                }
            }
            if (_profileFav != null)
            {
                int favAscent = mc != null ? mc.Profile.FavoriteAscent : -999;
                int favFormat = mc != null ? mc.Profile.FavoriteFormat : -1;
                CloudProfile preview = new CloudProfile
                {
                    FavoriteMap = biomeIndex,
                    FavoriteAscent = favAscent,
                    FavoriteFormat = favFormat
                };
                _profileFav.text = BuildFavoriteLine(preview);
            }
        }

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

            // 高度要给"群系下拉 + 底部关闭按钮"留出足够间距, 否则展开时会压到按钮
            const float w = 780f;
            const float h = 700f;
            const float titleH = 74f;
            _profileWindow = PeakWindowStyle.CreateWindow(_profileRoot, "PcsrProfile", w, h, titleH,
                CsrLoc.T("profile.window.title"), "");

            TextMeshProUGUI title = _profileWindow.Title;
            if (title != null)
            {
                RectTransform rt = title.transform as RectTransform;
                if (rt != null) NativeUiFactory.Place(rt, 30f, 0f, w - 60f - 110f, titleH);
                title.textWrappingMode = TextWrappingModes.NoWrap;
                title.overflowMode = TextOverflowModes.Overflow;
                title.alignment = TextAlignmentOptions.Left;
                LocalizedTextBinder.Bind(title, "profile.window.title");
            }

            RectTransform body = _profileWindow.Body;
            float inner = w - 52f;

            _profileTitle = UiKit.CreateText(body, "ProfName", "", 28f, PeakWindowStyle.Heading, 26f, 10f, inner, 38f);

            // 顶部信息行: 当前分数 ELO | 历史最高分 ELO (整串富文本上色)
            _profileMeta = UiKit.CreateText(body, "ProfMeta", "", 18f, PeakWindowStyle.TextPrimary, 26f, 50f, inner, 26f);

            PeakWindowStyle.CreateSeparator(body, "ProfSep1", 26f, 82f, inner);

            _profileBody = UiKit.CreateText(body, "ProfBody", "", 18f, PeakWindowStyle.TextPrimary, 26f, 94f, inner, 28f);

            PeakWindowStyle.CreateSeparator(body, "ProfSep2", 26f, 130f, inner);

            _profileModes = UiKit.CreateText(body, "ProfModes", "", 17f, PeakWindowStyle.TextPrimary, 26f, 142f, inner, 110f, wrap: true);

            PeakWindowStyle.CreateSeparator(body, "ProfSep3", 26f, 258f, inner);

            _profileFav = UiKit.CreateText(body, "ProfFav", "", 17f, PeakWindowStyle.TextMuted, 26f, 268f, inner, 44f, wrap: true);

            PeakWindowStyle.CreateSeparator(body, "ProfSep4", 26f, 318f, inner);

            // ---- 昵称编辑区 (仅自己的档案可见) ----
            _profileNickLabel = UiKit.CreateText(body, "ProfNickLabel", CsrLoc.T("profile.nick.label"),
                18f, PeakWindowStyle.Heading, 26f, 328f, inner, 26f);
            LocalizedTextBinder.Bind(_profileNickLabel, "profile.nick.label");
            _profileNickInput = PeakWindowStyle.CreateInputField(body, "ProfNickInput", "", 26f, 356f, 330f, 40f, 18f);
            // 昵称允许中英文/数字/空格等, 不能用 CreateInputField 默认的纯数字校验
            UiKit.ConfigurePlainInput(_profileNickInput, 16, false);
            _profileNickSave = PeakWindowStyle.CreatePill(body, "ProfNickSave", CsrLoc.T("profile.nick.save"),
                366f, 356f, 150f, 40f, 17f);
            _profileNickSave.onClick.AddListener(new UnityAction(OnSaveNicknameClicked));
            LocalizedTextBinder.BindPill(_profileNickSave, "profile.nick.save");

            _profileNickHint = UiKit.CreateText(body, "ProfNickHint", CsrLoc.T("profile.nick.hint"),
                14f, PeakWindowStyle.TextMuted, 26f, 402f, inner, 34f, wrap: true);
            LocalizedTextBinder.Bind(_profileNickHint, "profile.nick.hint");

            // ---- 最爱群系: 玩家唯一可自选的项目 (难度/模式按真实对局自动统计) ----
            _profileBiomeLabel = UiKit.CreateText(body, "ProfBiomeLabel", CsrLoc.T("profile.biome.label"),
                18f, PeakWindowStyle.Heading, 26f, 444f, inner, 26f);
            LocalizedTextBinder.Bind(_profileBiomeLabel, "profile.biome.label");
            _profileBiomeDropdown = PcsrDropdown.Create(
                body, "ProfBiomeDropdown", 26f, 472f, 300f, 40f,
                PcsrBiomes.Labels(CsrLoc.Current == CsrLanguage.ZhCn), -1, 17f);
            _profileBiomeDropdown.OnValueChanged = OnBiomeChanged;
            _profileBiomeDropdown.SetValue(-1, false);

            // 关闭按钮放到右下角, 与群系下拉保持距离; 展开的列表最多向下延伸 11 行, 也不会压到它
            Button close = PeakWindowStyle.CreatePill(body, "ProfClose", CsrLoc.T("win.btn.ok"),
                w - 52f - 200f, h - titleH - 14f - 62f, 200f, 46f, 18f);
            close.onClick.AddListener(new UnityAction(CloseProfileCard));
            LocalizedTextBinder.BindPill(close, "win.btn.ok");

            _profileRoot.gameObject.SetActive(false);
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

            // 先用本地已知数据铺底, 云端回来后再覆盖
            CloudProfile cached = svc.CachedProfile(mySteamId);
            if (cached != null)
            {
                RenderProfile(mySteamId, cached);
                ShowSelfEditors(true);
            }
            else
            {
                // 缓存缺失: 界面里可能还是上一个玩家的内容, 必须先清空并上锁
                ShowSelfEditors(false);
                RenderSelfLoading();
            }

            svc.RequestMyProfile(p =>
            {
                if (p != null && _profileOpen && _profileSteamId == p.SteamId)
                {
                    RenderProfile(p.SteamId, p);
                    ShowSelfEditors(true);
                }
            });
        }

        private void ShowSelfEditors(bool enabled)
        {
            UiKit.SetActive(_profileNickLabel, enabled);
            UiKit.SetActive(_profileNickInput, enabled);
            UiKit.SetActive(_profileNickSave, enabled);
            UiKit.SetActive(_profileNickHint, enabled);
            UiKit.SetActive(_profileBiomeLabel, enabled);
            UiKit.SetActive(_profileBiomeDropdown, enabled);
            if (enabled && _profileNickSave != null) _profileNickSave.interactable = !_profileNickBusy;
        }

        // 等待云端档案时的占位内容: 清掉上一位玩家的标题与统计
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
                // 全是非法字符/长度不足, 直接拒绝
                PeakCsrPrompt.Info(CsrLoc.T("profile.nick.label"), CsrLoc.T("profile.nick.failed", "invalid"));
                return;
            }

            _profileNickBusy = true;
            if (_profileNickSave != null) _profileNickSave.interactable = false;

            MatchCoordinator mc = MatchCoordinator.Instance;
            int favMap = mc != null ? mc.Profile.FavoriteMap : -1;
            int favAsc = mc != null ? mc.Profile.FavoriteAscent : -999;
            int favFmt = mc != null ? mc.Profile.FavoriteFormat : -1;
            svc.PushNickname(nick, favMap, favAsc, favFmt, (ok, err) =>
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
            if (_profileRoot != null) _profileRoot.gameObject.SetActive(false);
            _profileOpen = false;
            _profileSteamId = "";
        }
    }
}
