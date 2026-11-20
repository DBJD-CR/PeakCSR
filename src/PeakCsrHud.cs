using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    public class PeakCsrHud : MonoBehaviour
    {
        private static PeakCsrHud _instance;
        // 默认隐藏: 主菜单已提供原生木牌入口, IMGUI 面板只在需要时按 F10 或点木牌呼出
        private bool _visible;
        private bool _showLeaderboard;
        private bool _confirmForfeit;
        private string _inviteCodeInput = "";
        private Rect _windowRect = new Rect(24f, 140f, 480f, 520f);

        private GUIStyle _headerStyle;
        private GUIStyle _subStyle;

        public static void EnsureVisible()
        {
            if (_instance != null) _instance._visible = true;
        }

        private void Awake()
        {
            _instance = this;
        }

        private void Update()
        {
            if (Plugin.Instance == null) return;
            if (!Input.GetKeyDown(Plugin.Instance.ToggleMenuKey.Value)) return;

            // F10 打开与 RANKED 同风格的原生排位中心窗口
            PcsrMenuWindow.ShowWindow();
        }

        private void LateUpdate()
        {
            if (!_visible) return;
            MatchController mc = MatchController.Instance;
            if (mc == null) return;
            if (mc.State == CsMatchState.LoadingLevel || mc.State == CsMatchState.Countdown || mc.State == CsMatchState.Running)
            {
                _visible = false;
            }
        }

        private void InitStyles()
        {
            if (_headerStyle != null) return;
            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                richText = true
            };
            _subStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 12,
                richText = true
            };
        }

        private void OnGUI()
        {
            if (Plugin.Instance == null) return;
            if (!_visible) return;
            if (PcsrMenuWindow.Instance != null && PcsrMenuWindow.Instance.IsOpen) return;
            InitStyles();
            string title = CsrLoc.T("hud.window.title", Plugin.Instance.ToggleMenuKey.Value);
            _windowRect = GUILayout.Window(90210, _windowRect, DrawWindow, title);
        }

        private void DrawWindow(int id)
        {
            MatchController mc = MatchController.Instance;
            if (mc == null) return;

            int elo = EloCalculator.CurrentElo;
            string tierHex = EloCalculator.GetTierHexColor(elo);
            string tierName = EloCalculator.GetTierName(elo);
            int winRate = EloCalculator.WinRatePercent;

            // 1. 顶部选手段位卡片
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label("<color=#" + tierHex + ">★ " + tierName + "</color>   |   <b>" + elo + " ELO</b>", _headerStyle);
            if (GUILayout.Button(CsrLoc.T("hud.btn.board_toggle"), GUILayout.Width(74f), GUILayout.Height(22f)))
            {
                _showLeaderboard = !_showLeaderboard;
                if (_showLeaderboard && CloudLeaderboardService.Instance != null)
                {
                    CloudLeaderboardService.Instance.RefreshLeaderboard();
                }
            }
            if (GUILayout.Button(CsrLoc.T("hud.btn.native"), GUILayout.Width(84f), GUILayout.Height(22f)))
            {
                _visible = false;
                PcsrMenuWindow.ShowWindow();
            }
            if (GUILayout.Button(CsrLoc.T("hud.btn.lang"), GUILayout.Width(72f), GUILayout.Height(22f)))
            {
                CsrLoc.ToggleLanguage();
            }
            GUILayout.EndHorizontal();
            GUILayout.Label(CsrLoc.T("hud.record", EloCalculator.Wins, EloCalculator.Losses, winRate, EloCalculator.Streak)
                + "   |   " + CsrLoc.T("hud.peak", EloCalculator.PeakElo), _subStyle);
            GUILayout.EndVertical();

            if (_showLeaderboard)
            {
                DrawCloudLeaderboardBox();
                GUI.DragWindow();
                return;
            }

            GUILayout.Space(4f);

            // 2. 赛制与难度选择区
            double mult = EloCalculator.GetModeMultiplier(mc.SelectedFormat, mc.SelectedAscent);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(CsrLoc.T("hud.ruleset.label", mc.GetCurrentFormatDisplayName(), mult.ToString("F2")), _subStyle);
            if (mc.State == CsMatchState.Idle)
            {
                GUILayout.BeginHorizontal();
                DrawFormatButton(mc, McsrRuleFormat.StandardAllPeak, CsrLoc.T("hud.format.standard"));
                DrawFormatButton(mc, McsrRuleFormat.HardcoreAscent4, CsrLoc.T("hud.format.hardcore"));
                DrawFormatButton(mc, McsrRuleFormat.BiomeSprint, CsrLoc.T("hud.format.sprint"));
                GUILayout.EndHorizontal();

                // 难度始终可调: 菜鸟新人 A-1 / 标准顶峰 A0 / 天阶 A1~A8
                GUILayout.Space(2f);
                GUILayout.BeginHorizontal();
                GUILayout.Label(CsrLoc.T("hud.ascent.select",
                    MatchController.GetAscentDisplayName(mc.SelectedAscent), mult.ToString("F2")), _subStyle);
                if (GUILayout.Button("-", GUILayout.Width(28f), GUILayout.Height(20f)))
                {
                    mc.SelectedAscent = mc.SelectedAscent - 1;
                }
                if (GUILayout.Button("+", GUILayout.Width(28f), GUILayout.Height(20f)))
                {
                    mc.SelectedAscent = mc.SelectedAscent + 1;
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();

            GUILayout.Space(4f);

            // 3. 好友邀请码 / 创建私密房
            if (mc.State == CsMatchState.Idle)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.BeginHorizontal();
                GUILayout.Label(CsrLoc.T("hud.invite.label"), _subStyle, GUILayout.Width(72f));
                _inviteCodeInput = GUILayout.TextField(_inviteCodeInput ?? "", 12, GUILayout.Width(95f), GUILayout.Height(22f));
                if (GUILayout.Button(CsrLoc.T("hud.invite.join"), GUILayout.Height(22f)))
                {
                    mc.JoinByInviteCode(_inviteCodeInput);
                }
                if (GUILayout.Button(CsrLoc.T("hud.invite.host"), GUILayout.Height(22f)))
                {
                    mc.CreatePrivate1v1Room();
                }
                string ratedLabel = mc.IsRatedMatch ? ("● " + CsrLoc.T("hud.invite.rated")) : ("○ " + CsrLoc.T("hud.invite.unrated"));
                if (GUILayout.Button(ratedLabel, GUILayout.Width(100f), GUILayout.Height(22f)))
                {
                    mc.IsRatedMatch = !mc.IsRatedMatch;
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(4f);
            }

            // 4. 对局状态与实时队列情报
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.Label(CsrLoc.T("hud.status", mc.StatusMessage), _subStyle);

            CsrQueueStats q = mc.QueueStats;
            GUILayout.Label(CsrLoc.T("win.queue.live_info",
                q.OnlinePlayers, q.WaitingRooms, q.ActiveRooms,
                q.StdWaiting, q.AscWaiting, q.SprWaiting), _subStyle);

            if (!string.IsNullOrEmpty(mc.InviteCode))
            {
                string biomeLabel = MapCatalog.GetBiomeLabel(mc.LevelIndex);
                string seedDisplay = (mc.State == CsMatchState.Running || mc.State == CsMatchState.Finished) ? mc.Seed.ToString() : CsrLoc.T("hud.seed.hidden");
                GUILayout.Label(CsrLoc.T("hud.room_info", mc.InviteCode, mc.LevelIndex, biomeLabel,
                    MatchController.GetAscentShortName(mc.Ascent), seedDisplay), _subStyle);

                string verifyTag = mc.OpponentVerified ? CsrLoc.T("hud.verified.ok") : CsrLoc.T("hud.verified.wait");
                GUILayout.Label(CsrLoc.T("hud.opponent", mc.OpponentName, mc.OpponentElo, verifyTag,
                    EloCalculator.GetTierName(mc.OpponentElo)), _subStyle);

                float myPct = mc.SelfProgress * 100f;
                float oppPct = mc.OpponentProgress * 100f;
                float diffPct = myPct - oppPct;
                string diffColor = diffPct >= 0f ? "55E673" : "FF6666";
                string diffSign = diffPct >= 0f ? "+" : "";

                GUILayout.Label(
                    CsrLoc.T("hud.progress", myPct.ToString("F1"), oppPct.ToString("F1"), diffColor, diffSign,
                        diffPct.ToString("F1"), winRate),
                    _subStyle
                );

                if (mc.State == CsMatchState.Running)
                {
                    string oppStateTag = mc.OpponentDown ? CsrLoc.T("hud.state.down") : CsrLoc.T("hud.state.climbing");
                    GUILayout.Label(
                        CsrLoc.T("hud.vitals", Mathf.RoundToInt(mc.OpponentHealth * 100f), Mathf.RoundToInt(mc.OpponentStamina * 100f), oppStateTag),
                        _subStyle
                    );
                }
            }
            GUILayout.EndVertical();

            GUILayout.Space(6f);

            // 5. 换图投票提示框
            if (mc.OppRerollVoteActive)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(CsrLoc.T("hud.reroll.prompt", mc.RerollVoteRemainingSeconds), _subStyle);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(CsrLoc.T("hud.btn.reroll_accept"), GUILayout.Height(26f)))
                {
                    mc.RespondRerollVote(true);
                }
                if (GUILayout.Button(CsrLoc.T("hud.btn.reroll_decline"), GUILayout.Height(26f)))
                {
                    mc.RespondRerollVote(false);
                }
                GUILayout.EndHorizontal();
                GUILayout.EndVertical();
                GUILayout.Space(4f);
            }

            // 6. 底部操作按钮
            if (mc.State == CsMatchState.Idle)
            {
                _confirmForfeit = false;
                if (GUILayout.Button(CsrLoc.T("hud.btn.queue"), GUILayout.Height(34f)))
                {
                    mc.Queue1v1();
                }
            }
            else if (mc.State == CsMatchState.SearchingLobby || mc.State == CsMatchState.WaitingOpponent)
            {
                _confirmForfeit = false;
                if (GUILayout.Button(CsrLoc.T("hud.btn.cancel_queue"), GUILayout.Height(30f)))
                {
                    mc.LeaveMatch();
                }
            }
            else if (mc.State == CsMatchState.LoadingLevel || mc.State == CsMatchState.Countdown || mc.State == CsMatchState.Running)
            {
                GUILayout.BeginHorizontal();
                if (mc.MyRerollVoteActive)
                {
                    GUI.enabled = false;
                    GUILayout.Button(CsrLoc.T("hud.btn.reroll_waiting", mc.RerollVoteRemainingSeconds), GUILayout.Height(30f));
                    GUI.enabled = true;
                }
                else if (!mc.OppRerollVoteActive)
                {
                    if (GUILayout.Button(CsrLoc.T("hud.btn.reroll"), GUILayout.Height(30f)))
                    {
                        mc.InitiateRerollVote();
                    }
                }

                if (!_confirmForfeit)
                {
                    if (GUILayout.Button(CsrLoc.T("hud.btn.forfeit"), GUILayout.Height(30f)))
                    {
                        _confirmForfeit = true;
                    }
                }
                else
                {
                    if (GUILayout.Button(CsrLoc.T("hud.btn.forfeit_confirm"), GUILayout.Height(30f)))
                    {
                        _confirmForfeit = false;
                        mc.ForfeitMatch();
                    }
                    if (GUILayout.Button(CsrLoc.T("hud.btn.forfeit_cancel"), GUILayout.Height(30f)))
                    {
                        _confirmForfeit = false;
                    }
                }
                GUILayout.EndHorizontal();
            }
            else if (mc.State == CsMatchState.Finished)
            {
                _confirmForfeit = false;
                // 作废局先说清楚发生了什么, 否则玩家会以为对局凭空消失
                if (mc.MatchVoided)
                {
                    GUILayout.Label("<color=#FFCC66>" + CsrLoc.T("hud.void.notice") + "</color>", _subStyle);
                }
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(CsrLoc.T(mc.MatchVoided ? "hud.btn.void_requeue" : "hud.btn.queue_next"), GUILayout.Height(32f)))
                {
                    mc.LeaveMatch();
                    mc.Queue1v1();
                }
                if (GUILayout.Button(CsrLoc.T("hud.btn.post_report"), GUILayout.Height(32f)))
                {
                    PcsrMenuWindow.ShowPostMatchWindow();
                }
                if (GUILayout.Button(CsrLoc.T("hud.btn.return_idle"), GUILayout.Height(32f)))
                {
                    mc.LeaveMatch();
                }
                GUILayout.EndHorizontal();
            }

            GUI.DragWindow();
        }

        private void DrawCloudLeaderboardBox()
        {
            GUILayout.Space(4f);
            GUILayout.BeginVertical(GUI.skin.box);
            GUILayout.BeginHorizontal();
            GUILayout.Label(CsrLoc.T("hud.board.title"), _subStyle);
            if (GUILayout.Button(CsrLoc.T("hud.btn.refresh_board"), GUILayout.Width(76f), GUILayout.Height(20f)))
            {
                if (CloudLeaderboardService.Instance != null)
                {
                    CloudLeaderboardService.Instance.RefreshLeaderboard();
                }
            }
            GUILayout.EndHorizontal();

            CloudLeaderboardService svc = CloudLeaderboardService.Instance;
            if (svc != null && !string.IsNullOrEmpty(svc.CloudStatus))
            {
                GUILayout.Label("<color=#AAAAAA>" + svc.CloudStatus + "</color>", _subStyle);
            }

            GUILayout.Space(4f);
            if (svc != null && svc.Entries != null && svc.Entries.Count > 0)
            {
                string mySteamId = SteamIdentity.GetSteamId64();
                for (int i = 0; i < svc.Entries.Count && i < 10; i++)
                {
                    CloudRankEntry e = svc.Entries[i];
                    string c = EloCalculator.GetTierHexColor(e.Elo);
                    string tierName = EloCalculator.GetTierName(e.Elo);
                    bool isMe = !string.IsNullOrEmpty(mySteamId) && e.SteamId == mySteamId;
                    string meTag = isMe ? " <color=#55E673>[我]</color>" : "";
                    string shownName = e.DisplayName;
                    if (string.IsNullOrEmpty(shownName)) shownName = CsrLoc.T("common.anonymous");
                    GUILayout.Label(
                        "<b>#" + e.Rank + "</b>  <color=#" + c + ">" + shownName + "</color>" + meTag
                        + "  <color=#" + c + ">[" + tierName + "]</color>   —   <b>" + e.Elo + " ELO</b>   (" + e.Wins + "W / " + e.Losses + "L)",
                        _subStyle
                    );
                }
            }
            else
            {
                GUILayout.Label("1. <b>You</b> — " + EloCalculator.CurrentElo + " ELO (" + EloCalculator.Wins + "W / " + EloCalculator.Losses + "L)", _subStyle);
            }
            GUILayout.EndVertical();
        }

        private static void DrawFormatButton(MatchController mc, McsrRuleFormat format, string label)
        {
            bool active = mc.SelectedFormat == format;
            string text = active ? ("● " + label) : label;
            if (GUILayout.Button(text, GUILayout.Height(24f)))
            {
                mc.SelectedFormat = format;
            }
        }
    }
}