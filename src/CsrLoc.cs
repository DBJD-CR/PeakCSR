using System;
using System.Collections.Generic;
using RankedPeak.Localization;

namespace PeakCSR
{
    public enum CsrLanguage
    {
        ZhCn = 0,
        EnUs = 1
    }

    // PeakCSR 多语言国际化 (i18n) 接口模块，默认优先中文并支持随原版语言动态切换
    public static class CsrLoc
    {
        private static CsrLanguage _current = CsrLanguage.ZhCn;
        // 是否跟随游戏原版语言切换事件; 启动时不能跟随,
        // 因为游戏语言设置加载晚于 BepInEx 插件 Awake, 那一刻恒为默认 English
        private static bool _followVanilla = false;

        private static readonly Dictionary<string, string> ZhStrings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "hud.window.title", "PeakCSR - PCSR 1v1 实时排位 ({0})" },
            { "hud.peak", "历史最高: {0}" },
            { "hud.record", "战绩: <color=#55E673>{0}胜</color> - <color=#FF6666>{1}负</color> ({2}%)   |   连胜: {3}" },
            { "hud.ruleset.label", "<b>PCSR 赛制:</b> {0}  <color=#AAAAAA>[无幽灵 · 盲打种子 · 死亡判负 · {1}x ELO]</color>" },
            { "hud.format.standard", "全山标准 (A0)" },
            { "hud.format.hardcore", "高难天阶 (A1~A8)" },
            { "hud.format.sprint", "单群系冲刺 (0.22x)" },
            { "hud.ascent.select", "<b>天阶难度:</b> 天阶 {0} (ELO 倍率: {1}x)" },
            { "hud.invite.label", "<b>好友房码:</b>" },
            { "hud.invite.join", "加入房间" },
            { "hud.invite.host", "创建私密房" },
            { "hud.invite.rated", "计分赛" },
            { "hud.invite.unrated", "练习赛(不计分)" },
            { "hud.status", "<b>当前状态:</b> {0}" },
            { "hud.room_info", "房间: <b>{0}</b>   |   地图: <b>#{1} ({2})</b>   |   天阶: <b>A{3}</b>   |   种子: {4}" },
            { "hud.seed.hidden", "已隐藏 (盲打)" },
            { "hud.opponent", "对手: <b>{0}</b> ({1} ELO) {2}" },
            { "hud.verified.ok", "<color=#55E673>[PCSR 已校验]</color>" },
            { "hud.verified.wait", "<color=#FFCC33>[握手中...]</color>" },
            { "hud.progress", "我方: <b>{0}%</b>   vs   对手: <b>{1}%</b>   (<color=#{2}>{3}{4}%</color>)" },
            { "hud.vitals", "对手状态: 体力 {0}%  |  耐力 {1}%  {2}" },
            { "hud.state.down", "<color=#FF4444>[已晕倒/濒死]</color>" },
            { "hud.state.climbing", "[攀爬中]" },
            { "hud.btn.queue", "⚔ 开始匹配 PCSR 1v1 排位" },
            { "hud.btn.cancel_queue", "取消匹配 / 离开房间" },
            { "hud.btn.forfeit", "🏳 投降认输 (-ELO)" },
            { "hud.btn.forfeit_confirm", "确认投降" },
            { "hud.btn.forfeit_cancel", "取消" },
            { "hud.btn.reroll", "🎲 发起投票换图 (30s)" },
            { "hud.btn.reroll_waiting", "⏳ 等待对手同意换图 ({0}s)..." },
            { "hud.reroll.prompt", "<color=#FFD933><b>[投票换图]</b> 对手请求重新随机开一张新地图 ({0}s)，是否同意？</color>" },
            { "hud.btn.reroll_accept", "✓ 同意换图" },
            { "hud.btn.reroll_decline", "✕ 拒绝 (继续跑)" },
            { "hud.btn.queue_next", "⚔ 继续匹配下一局" },
            { "hud.btn.return_idle", "返回待机" },
            { "hud.btn.lang", "语言: 中文" },

            { "sign.queuing", "<color=#FF6B2B>PCSR 1v1</color>  <size=80%>• 匹配中...</size>" },
            { "sign.idle", "<color=#{0}>PCSR 1v1</color>  <size=82%>• {1} ELO</size>" },
            { "hud.btn.native", "原生窗口" },
            { "win.opponent.placeholder", "等待中..." },
            { "common.anonymous", "匿名玩家" },

            { "tier.sherpa", "夏尔巴 (下界合金)" },
            { "tier.alpinist", "登山家 (钻石)" },
            { "tier.climber", "攀岩者 (绿宝石)" },
            { "tier.hiker", "徒步者 (黄金)" },
            { "tier.scout", "侦察兵 (白铁)" },
            { "tier.tenderfoot", "初学者 (黑煤)" },

            { "format.standard.full", "全山标准局 (顶峰)" },
            { "format.hardcore.full", "高难天阶局 (天阶 {0})" },
            { "format.sprint.full", "单群系冲刺局 (天阶 {0})" },

            { "status.ready", "准备就绪（选择赛制并开始匹配，或使用房间码约战）" },
            { "status.not_ready", "RaceService 尚未就绪，请先进入主菜单。" },
            { "status.searching", "正在搜索 [{0}] 1v1 排位房间..." },
            { "status.found_joining", "已找到 PCSR 1v1 房间 [{0}]，正在加入..." },
            { "status.joining_code", "正在通过邀请码加入房间 [{0}]..." },
            { "status.code_empty", "请先输入有效的房间邀请码！" },
            { "status.creating", "正在创建 PCSR 1v1 房间 ({0})..." },
            { "status.creating_private", "正在创建好友私密 1v1 房间 ({0})..." },
            { "status.waiting_opp", "房间 [{0}] 已就绪，正在等待对手加入 (1/2)..." },
            { "status.error", "匹配出错: {0}" },
            { "status.kick_vanilla", "已移出未安装 PeakCSR 的误入玩家 [{0}]，继续等待排位对手..." },
            { "status.verified_starting", "对手已通过校验 ({0} ELO)！正在发起同步开赛..." },
            { "status.matched_starting", "已匹配对手 {0} ({1} ELO)！即将开始..." },
            { "status.aborted_early", "对局中断 / 提前退出 (判负 DNF)。" },
            { "status.ruleset_mismatch", "赛制不一致，已退出房间 (房主: {0} / 本地: {1})。" },
            { "status.closed", "房间已关闭。" },
            { "status.loading_island", "正在加载海岛地图，等待双方选手苏醒就绪..." },
            { "status.countdown", "同步倒计时开赛: {0} 秒" },
            { "status.running", "PCSR 1v1 激战中 vs {0} ({1} ELO)" },
            { "status.reroll_sent", "已向对手发起换图投票，等待对方回应 (30秒超时)..." },
            { "status.reroll_declined", "对手拒绝了换图投票，比赛继续进行！" },
            { "status.reroll_timeout", "换图投票已超时 (30秒)，比赛继续进行！" },
            { "status.reroll_passed", "双方同意换图！正在切换至新地图 #{0} ({1})..." },
            { "status.win.peak", "胜利！你率先登顶完赛，用时 {0} 秒！" },
            { "status.loss.peak", "落败！对手 [{0}] 率先登顶完赛（用时 {1} 秒）。" },
            { "status.loss.dnf", "落败！你在攀登途中阵亡 / DNF。" },
            { "status.win.opp_dnf", "胜利！对手 [{0}] 阵亡或投降 (DNF)！" },
            { "status.win.opp_dc", "胜利！对手 [{0}] 断开连接逃跑！" },
            { "status.loss.forfeit", "落败！你主动投降认输。" },

            { "antiboost.unrated", "好友练习赛 (不计 ELO)" },
            { "antiboost.self", "检测到自建小号对局 (本次不计 ELO)" },
            { "antiboost.gap", "双方分差过大 (>650)，ELO 结算权重降低" },
            { "antiboost.limit", "防刷分触发：30分钟内同对手超过3局 (0 ELO)" },
            { "antiboost.half", "短时间重复匹配同对手：本局 ELO 权重减半" },
            { "win.subtitle", "PCSR 1v1 实时排位中心" },
            { "win.box.ruleset", "赛制与房间" },
            { "win.box.match", "对局状态" },
            { "win.box.board", "全服实时天梯" },
            { "win.lbl.format", "赛制选择" },
            { "win.lbl.ascent", "天阶难度" },
            { "win.lbl.ascent_value", "天阶 {0}" },
            { "win.lbl.mult", "ELO 倍率 {0}x" },
            { "win.lbl.invite", "好友房间码" },
            { "win.room.none", "当前未在房间中" },
            { "win.room.info", "房间: <b>{0}</b>   |   地图: <b>#{1} ({2})</b>   |   天阶: <b>A{3}</b>   |   种子: {4}" },
            { "win.board.empty", "暂无云端天梯数据（点击刷新重试）" },
            { "win.btn.help", "玩法说明" },
            { "win.btn.ok", "知道了" },
            { "win.btn.confirm", "确认" },
            { "win.btn.cancel", "取消" },
            { "win.prompt.forfeit", "确认投降？本局将判负并扣除 ELO。" },
            { "win.help.title", "PCSR 1v1 排位规则" },
            { "win.help.body", "· 双方使用相同地图与种子，不可见对手幽灵。\n· 死亡即判负，无复活。\n· 先登顶者获胜，中途离开判 DNF。\n· 天阶越高 ELO 倍率越高，单群系冲刺为 0.22x。\n· 同一对手 30 分钟内超过 3 局不结算 ELO。" },
            { "hud.btn.board_toggle", "🏆 全服榜" },
            { "hud.board.title", "<b>🏆 PCSR 1v1 全服实时天梯榜 (Top 10)</b>" },
            { "hud.btn.refresh_board", "刷新榜单" },
            { "cloud.unconfigured", "未配置云端天梯 URL（仅本地签名存档生效）" },
            { "cloud.loading", "正在同步全服天梯数据..." },
            { "cloud.updated", "已同步全服天梯 ({0} 名选手)" },
            { "cloud.pending", "战报已签名上报，等待对手战报或原版后端交叉核验..." },
            { "cloud.settled", "云端权威校验通过 ({1})！最新 ELO: {0}" },
            { "cloud.error", "云端连接提示: {0}" }
        };

        private static readonly Dictionary<string, string> EnStrings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "hud.window.title", "PeakCSR - PCSR 1v1 Live Ranked ({0})" },
            { "hud.peak", "Peak: {0}" },
            { "hud.record", "Record: <color=#55E673>{0}W</color> - <color=#FF6666>{1}L</color> ({2}%)   |   Streak: {3}" },
            { "hud.ruleset.label", "<b>PCSR Ruleset:</b> {0}  <color=#AAAAAA>[No Ghost · Blind Seed · Sudden Death · {1}x ELO]</color>" },
            { "hud.format.standard", "Full Peak (A0)" },
            { "hud.format.hardcore", "Ascent (A1~A8)" },
            { "hud.format.sprint", "Biome Sprint (0.22x)" },
            { "hud.ascent.select", "<b>Ascent Level:</b> Ascent {0} (ELO Mult: {1}x)" },
            { "hud.invite.label", "<b>Invite Code:</b>" },
            { "hud.invite.join", "Join Code" },
            { "hud.invite.host", "Create Private" },
            { "hud.invite.rated", "Rated" },
            { "hud.invite.unrated", "Unrated" },
            { "hud.status", "<b>Status:</b> {0}" },
            { "hud.room_info", "Room: <b>{0}</b>   |   Map: <b>#{1} ({2})</b>   |   Ascent: <b>A{3}</b>   |   Seed: {4}" },
            { "hud.seed.hidden", "HIDDEN (Blind)" },
            { "hud.opponent", "Opponent: <b>{0}</b> ({1} ELO) {2}" },
            { "hud.verified.ok", "<color=#55E673>[PCSR Verified]</color>" },
            { "hud.verified.wait", "<color=#FFCC33>[Handshaking...]</color>" },
            { "hud.progress", "You: <b>{0}%</b>   vs   Rival: <b>{1}%</b>   (<color=#{2}>{3}{4}%</color>)" },
            { "hud.vitals", "Rival Vitals: HP {0}%  |  Stamina {1}%  {2}" },
            { "hud.state.down", "<color=#FF4444>[PASSED OUT]</color>" },
            { "hud.state.climbing", "[Climbing]" },
            { "hud.btn.queue", "⚔ Queue PCSR 1v1 Live Ranked" },
            { "hud.btn.cancel_queue", "Cancel Queue / Leave Room" },
            { "hud.btn.forfeit", "🏳 Forfeit Match (-ELO)" },
            { "hud.btn.forfeit_confirm", "Confirm Forfeit" },
            { "hud.btn.forfeit_cancel", "Cancel" },
            { "hud.btn.reroll", "🎲 Vote Reroll Map (30s)" },
            { "hud.btn.reroll_waiting", "⏳ Waiting Opponent Vote ({0}s)..." },
            { "hud.reroll.prompt", "<color=#FFD933><b>[Map Vote]</b> Opponent wants to reroll map ({0}s). Agree?</color>" },
            { "hud.btn.reroll_accept", "✓ Agree Reroll" },
            { "hud.btn.reroll_decline", "✕ Decline (Keep Playing)" },
            { "hud.btn.queue_next", "⚔ Queue Next 1v1" },
            { "hud.btn.return_idle", "Return to Idle" },
            { "hud.btn.lang", "Lang: EN" },

            { "sign.queuing", "<color=#FF6B2B>PCSR 1v1</color>  <size=80%>• QUEUING...</size>" },
            { "sign.idle", "<color=#{0}>PCSR 1v1</color>  <size=82%>• {1} ELO</size>" },
            { "hud.btn.native", "Native" },
            { "win.opponent.placeholder", "Waiting..." },
            { "common.anonymous", "Anonymous" },

            { "tier.sherpa", "SHERPA (Netherite)" },
            { "tier.alpinist", "ALPINIST (Diamond)" },
            { "tier.climber", "CLIMBER (Emerald)" },
            { "tier.hiker", "HIKER (Gold)" },
            { "tier.scout", "SCOUT (Iron)" },
            { "tier.tenderfoot", "TENDERFOOT (Coal)" },

            { "format.standard.full", "Standard Full Peak (Peak)" },
            { "format.hardcore.full", "Hardcore Peak (Ascent {0})" },
            { "format.sprint.full", "Biome Sprint (Ascent {0})" },

            { "status.ready", "Ready - pick a ruleset and queue up" },
            { "status.not_ready", "RaceService not ready, enter Main Menu first." },
            { "status.searching", "Searching [{0}] 1v1 lobbies..." },
            { "status.found_joining", "Found PCSR 1v1 lobby [{0}], joining..." },
            { "status.joining_code", "Joining room [{0}] via invite code..." },
            { "status.code_empty", "Please enter a valid room invite code!" },
            { "status.creating", "Creating PCSR 1v1 room ({0})..." },
            { "status.creating_private", "Creating private 1v1 room ({0})..." },
            { "status.waiting_opp", "Room [{0}] waiting for opponent (1/2)..." },
            { "status.error", "Matchmaking error: {0}" },
            { "status.kick_vanilla", "Removed non-PeakCSR player [{0}]. Waiting for ranked opponent..." },
            { "status.verified_starting", "Opponent verified ({0} ELO)! Launching synchronized 1v1..." },
            { "status.matched_starting", "Matched vs {0} ({1} ELO)! Starting..." },
            { "status.aborted_early", "Match aborted / left early (DNF)." },
            { "status.ruleset_mismatch", "Ruleset mismatch, left the room (host: {0} / local: {1})." },
            { "status.closed", "Match closed." },
            { "status.loading_island", "Loading island & waiting for both runners to wake up..." },
            { "status.countdown", "Synchronized countdown: {0}s" },
            { "status.running", "LIVE 1v1 RUNNING vs {0} ({1} ELO)" },
            { "status.reroll_sent", "Map reroll vote sent! Waiting for opponent (30s)..." },
            { "status.reroll_declined", "Opponent declined map reroll. Keep climbing!" },
            { "status.reroll_timeout", "Map reroll vote timed out (30s). Keep climbing!" },
            { "status.reroll_passed", "Vote passed! Switching to new map #{0} ({1})..." },
            { "status.win.peak", "VICTORY! You reached the peak first in {0}s!" },
            { "status.loss.peak", "DEFEAT! {0} reached the peak first ({1}s)." },
            { "status.loss.dnf", "DEFEAT! You died / DNF." },
            { "status.win.opp_dnf", "VICTORY! Opponent [{0}] died / forfeited (DNF)!" },
            { "status.win.opp_dc", "VICTORY! Opponent [{0}] disconnected!" },
            { "status.loss.forfeit", "DEFEAT! You forfeited the match." },

            { "antiboost.unrated", "Unrated Private Match (0 ELO)" },
            { "antiboost.self", "Self-match detected (0 ELO change)" },
            { "antiboost.gap", "Extreme ELO gap (>650), rating weight reduced" },
            { "antiboost.limit", "Anti-boost triggered: >3 matches vs same opponent in 30m (0 ELO)" },
            { "antiboost.half", "Repeated opponent (3rd match in 30m): 50% ELO weight" },
            { "win.subtitle", "PCSR 1v1 Live Ranked Hub" },
            { "win.box.ruleset", "Ruleset & Room" },
            { "win.box.match", "Match State" },
            { "win.box.board", "Global Live Ladder" },
            { "win.lbl.format", "Ruleset" },
            { "win.lbl.ascent", "Ascent Level" },
            { "win.lbl.ascent_value", "Ascent {0}" },
            { "win.lbl.mult", "ELO Mult {0}x" },
            { "win.lbl.invite", "Friend Invite Code" },
            { "win.room.none", "Not in a room" },
            { "win.room.info", "Room: <b>{0}</b>   |   Map: <b>#{1} ({2})</b>   |   Ascent: <b>A{3}</b>   |   Seed: {4}" },
            { "win.board.empty", "No cloud ladder data yet (press refresh)" },
            { "win.btn.help", "How to play" },
            { "win.btn.ok", "Got it" },
            { "win.btn.confirm", "Confirm" },
            { "win.btn.cancel", "Cancel" },
            { "win.prompt.forfeit", "Forfeit this match? You will lose ELO." },
            { "win.help.title", "PCSR 1v1 Ranked Rules" },
            { "win.help.body", "· Same map and seed for both players, opponent ghosts hidden.\n· Sudden Death: dying loses the match, no revive.\n· First to the peak wins; leaving early counts as DNF.\n· Higher ascents give higher ELO multipliers; biome sprint is 0.22x.\n· More than 3 matches vs the same opponent in 30 min won't affect ELO." },
            { "hud.btn.board_toggle", "🏆 Top 10" },
            { "hud.board.title", "<b>🏆 PCSR 1v1 Global Leaderboard (Top 10)</b>" },
            { "hud.btn.refresh_board", "Refresh" },
            { "cloud.unconfigured", "Cloud API URL not set (Using local signed ELO)" },
            { "cloud.loading", "Syncing global leaderboard..." },
            { "cloud.updated", "Global leaderboard synced ({0} players)" },
            { "cloud.pending", "Signed report sent, awaiting consensus / upstream verify..." },
            { "cloud.settled", "Cloud verified ({1})! New ELO: {0}" },
            { "cloud.error", "Cloud notice: {0}" }
        };

        public static CsrLanguage Current
        {
            get { return _current; }
            set
            {
                if (_current != value)
                {
                    _current = value;
                    if (OnLanguageChanged != null) OnLanguageChanged();
                }
            }
        }

        public static event Action OnLanguageChanged;

        public static void Initialize(bool preferChinese = true)
        {
            _current = preferChinese ? CsrLanguage.ZhCn : CsrLanguage.EnUs;
            try
            {
                Loc.LanguageChanged += OnVanillaLanguageChanged;
            }
            catch
            {
            }
        }

        public static void ToggleLanguage()
        {
            _followVanilla = false;
            Current = (_current == CsrLanguage.ZhCn) ? CsrLanguage.EnUs : CsrLanguage.ZhCn;
        }

        // 手动切换后若再次跟随原版语言
        public static void FollowVanillaLanguage()
        {
            _followVanilla = true;
            OnVanillaLanguageChanged();
        }

        private static void OnVanillaLanguageChanged()
        {
            if (!_followVanilla) return;
            try
            {
                // RANKED 的 Loc.Current 只区分 English / German / Korean / ChineseSimplified / ChineseTraditional
                ModLanguage lang = Loc.Current;
                if (lang == ModLanguage.ChineseSimplified || lang == ModLanguage.ChineseTraditional)
                {
                    Current = CsrLanguage.ZhCn;
                }
                else
                {
                    Current = CsrLanguage.EnUs;
                }
            }
            catch
            {
            }
        }

        public static string T(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;
            Dictionary<string, string> primary = (_current == CsrLanguage.ZhCn) ? ZhStrings : EnStrings;
            string val;
            if (primary.TryGetValue(key, out val)) return val;
            if (ZhStrings.TryGetValue(key, out val)) return val;
            if (EnStrings.TryGetValue(key, out val)) return val;
            return key;
        }

        public static string T(string key, params object[] args)
        {
            string raw = T(key);
            if (args == null || args.Length == 0) return raw;
            try
            {
                return string.Format(raw, args);
            }
            catch
            {
                return raw;
            }
        }
    }
}