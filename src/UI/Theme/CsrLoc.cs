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

    // PeakCSR 多语言国际化模块 (纯词典, 不再被 UI 重建逻辑耦合)
    // 富文本颜色统一由 PcsrColors 语义常量构造, 词条内容与重构前逐字一致
    public static class CsrLoc
    {
        private static CsrLanguage _current = CsrLanguage.ZhCn;
        private static bool _followVanilla = false;

        private static string C(string hex) { return PcsrColors.Wrap(hex, "{0}"); }
        private static string CB(string hex) { return PcsrColors.WrapBold(hex, "{0}"); }

        private static readonly Dictionary<string, string> ZhStrings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "hud.window.title", "PeakCSR - PCSR 1v1 实时排位 ({0})" },
            { "hud.peak", "历史最高: {0}" },
            { "hud.record", "战绩: " + PcsrColors.Wrap(PcsrColors.PositiveBright, "{0}胜") + " - " + PcsrColors.Wrap(PcsrColors.DangerBright, "{1}负") + " (胜率 {2}%)   |   连胜: {3}" },
            { "hud.ruleset.label", "<b>PCSR 赛制:</b> {0}  " + PcsrColors.Wrap(PcsrColors.NeutralGray, "[无幽灵 · 盲打种子 · 死亡判负 · {1}x ELO]") },
            { "hud.format.standard", "全山标准" },
            { "hud.format.hardcore", "高难天阶" },
            { "hud.format.sprint", "单群系冲刺 (0.22x)" },
            { "hud.ascent.select", "<b>天阶难度:</b> 天阶 {0} (ELO 倍率: {1}x)" },
            { "hud.invite.label", "<b>好友房码:</b>" },
            { "hud.invite.join", "加入房间" },
            { "hud.invite.host", "创建私密房" },
            { "hud.invite.rated", "计分赛" },
            { "hud.invite.unrated", "练习赛(不计分)" },
            { "hud.status", "<b>当前状态:</b> {0}" },
            { "hud.room_info", "房间: <b>{0}</b>   |   地图: <b>#{1} ({2})</b>   |   天阶: <b>{3}</b>   |   种子: {4}" },
            { "hud.seed.hidden", "已隐藏 (盲打)" },
            { "hud.opponent", "对手: <b>{0}</b> ({1} ELO · {3}) {2}" },
            { "hud.verified.ok", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR 已校验]") },
            { "hud.verified.wait", PcsrColors.Wrap(PcsrColors.Warning, "[握手中...]") },
            { "hud.progress", "攀爬进度: 我方 <b>{0}%</b>  vs  对手 <b>{1}%</b>  (" + PcsrColors.Wrap("{2}", "{3}{4}%") + ")   |   我方胜率: <b>{5}%</b>" },
            { "hud.vitals", "对手状态: 体力 {0}%  |  耐力 {1}%  {2}" },
            { "hud.state.down", PcsrColors.Wrap(PcsrColors.DownRed, "[已晕倒/濒死]") },
            { "hud.state.climbing", "[攀爬中]" },
            { "hud.btn.queue", "开始匹配 PCSR 1v1 排位" },
            { "hud.btn.cancel_queue", "取消匹配 / 离开房间" },
            { "hud.btn.forfeit", "投降认输 (-ELO)" },
            { "hud.btn.forfeit_confirm", "确认投降" },
            { "hud.btn.forfeit_cancel", "取消" },
            { "hud.btn.reroll", "发起投票换图 (30s)" },
            { "hud.btn.reroll_waiting", "等待对手同意换图 ({0}s)..." },
            { "hud.reroll.prompt", PcsrColors.Wrap(PcsrColors.Warning, "<b>[投票换图]</b> 对手请求重新随机开一张新地图 ({0}s)，是否同意？") },
            { "hud.btn.reroll_accept", "✓ 同意换图" },
            { "hud.btn.reroll_decline", "✕ 拒绝换图" },
            { "hud.btn.queue_next", "继续匹配下一局" },
            { "hud.btn.return_idle", "返回待机" },
            { "hud.btn.post_report", "查看赛后战报" },
            { "hud.btn.void_requeue", "重新匹配下一局" },
            { "hud.void.notice", "本局已作废：双方均不加减分、不计胜负。" },
            { "hud.btn.lang", "语言: 中文" },

            { "sign.queuing", PcsrColors.Wrap(PcsrColors.Orange, "PCSR 1v1") + "  <size=80%>• 匹配中 ({0})</size>" },
            { "sign.idle", PcsrColors.Wrap("{0}", "PCSR 1v1") + "  <size=82%>• {1} ELO</size>" },
            { "hud.btn.native", "原生窗口" },
            { "win.opponent.placeholder", "等待中..." },
            { "common.anonymous", "匿名玩家" },

            { "tier.sherpa", "夏尔巴" },
            { "tier.alpinist", "登山家" },
            { "tier.climber", "攀岩者" },
            { "tier.hiker", "徒步者" },
            { "tier.scout", "侦察兵" },
            { "tier.tenderfoot", "初学者" },
            { "tier.short.sherpa", "夏尔巴" },
            { "tier.short.alpinist", "登山家" },
            { "tier.short.climber", "攀岩者" },
            { "tier.short.hiker", "徒步者" },
            { "tier.short.scout", "侦察兵" },
            { "tier.short.tenderfoot", "初学者" },

            { "ascent.name.tenderfoot", "菜鸟新人" },
            { "ascent.name.peak", "标准顶峰" },
            { "ascent.name.numbered", "天阶 {0}" },
            { "ascent.short.tenderfoot", "菜鸟(A-1)" },
            { "ascent.short.peak", "顶峰(A0)" },
            { "ascent.short.numbered", "天阶{0}(A{0})" },

            { "format.standard.full", "全山标准局 ({0})" },
            { "format.hardcore.full", "高难天阶局 ({0})" },
            { "format.sprint.full", "单群系冲刺局 ({0})" },

            { "status.ready", "准备就绪（选择赛制并开始匹配，或使用房间码约战）" },
            { "status.not_ready", "RaceService 尚未就绪，请先进入主菜单。" },
            { "status.searching", "正在搜索 [{0}] 1v1 排位房间... (已匹配 {1})" },
            { "status.found_joining", "已找到 PCSR 1v1 房间 [{0}]，正在加入..." },
            { "status.joining_code", "正在通过邀请码加入房间 [{0}]..." },
            { "status.code_empty", "请先输入有效的房间邀请码！" },
            { "status.creating", "正在创建 PCSR 1v1 房间 ({0})..." },
            { "status.creating_private", "正在创建好友私密 1v1 房间 ({0})..." },
            { "status.waiting_opp", "房间 [{0}] 等待对手加入 (1/2) · 已匹配 {1}" },
            { "status.error", "匹配出错: {0}" },
            { "status.kick_vanilla", "已移出未安装 PeakCSR 的误入玩家 [{0}]，继续等待排位对手..." },
            { "status.verified_starting", "对手已通过校验 ({0} ELO)！正在发起同步开赛..." },
            { "status.matched_starting", "已匹配对手 {0} ({1} ELO)！即将开始..." },
            { "status.aborted_early", "对局中断 / 提前退出 (判负 DNF)。" },
            { "status.ruleset_mismatch", "赛制不一致，已退出房间 (房主: {0} / 本地: {1})。" },
            { "status.env_mismatch", "发现对手房间但游戏内容不一致，无法加入。本机 版本 {0} · 地图集 {1}，请与对方核对游戏版本及地图类模组。" },
            { "win.lbl.env", "环境: 版本 {0} · 地图集 {1}" },
            { "win.queue.live_info", "实时队列: 在线约战 <b>{0}</b> 人 (等待房 <b>{1}</b> · 激战中 <b>{2}</b>)   |   全山:{3}  天阶:{4}  冲刺:{5}" },
            { "status.closed", "房间已关闭。" },
            { "status.loading_island", "正在加载海岛地图，等待双方选手苏醒就绪..." },
            { "status.countdown", "同步倒计时开赛: {0} 秒" },
            { "status.running", "PCSR 1v1 激战中 vs {0} ({1} ELO)" },
            { "status.reroll_sent", "已向对手发起换图投票，等待对方回应 (30秒超时)..." },
            { "status.reroll_declined", "对手拒绝了换图投票，比赛继续进行！" },
            { "status.reroll_timeout", "换图投票已超时 (30秒)，比赛继续进行！" },
            { "status.reroll_passed", "双方同意换图！正在切换至新地图 #{0} ({1})..." },
            { "status.win.peak", "胜利！你率先登顶完赛，用时 {0}s！" },
            { "status.loss.peak", "落败！对手 [{0}] 率先登顶完赛（用时 {1}s）。" },
            { "status.loss.dnf", "落败！你在攀登途中阵亡 / 超时未完赛 (DNF)。" },
            { "status.opp_finished_wait", "对手已登顶完赛！房间将在 30 秒后自动关闭，本局即将结算..." },
            { "status.win.opp_dnf", "胜利！对手 [{0}] 阵亡或投降认输 (DNF)！" },
            { "status.win.opp_dc", "胜利！对手 [{0}] 断开连接逃跑！" },
            { "status.loss.forfeit", "落败！你主动投降认输。" },
            { "status.reconnecting", "连接中断，正在重连并核对对局状态... ({0}s) 本局暂不结算。" },
            { "status.void_disconnect", "无法确认对局结果（{0}），本局已作废：双方均不扣分、不计胜负。" },
            { "status.void_local_dc", "本机网络中断，本局已作废：双方均不计分、不计胜负。" },
            { "status.void_opp_dc", "对手 [{0}] 连接中断，无法确认结果，本局已作废：双方均不计分。" },
            { "status.ghost_room_skipped", "该房间已经开始对局（可能是掉线残留），已自动退出，请重新匹配。" },
            { "status.loading_stuck", "加载超过 {0} 秒仍未进入地图，正在自动重试加入房间..." },
            { "status.reroll_waiting_room", "已同意换图，正在等待房主创建新房间..." },
            { "status.reroll_joining_room", "收到新房号 [{0}]，正在进入新地图..." },
            { "status.leaving", "正在离开当前房间，请稍候..." },
            { "status.start_countdown", "双方已就绪！{0} 秒后进入地图...  vs {1}" },
            { "status.waiting_host_start", "双方已就绪，等待房主发起同步开赛..." },
            { "status.converging", "检测到空房重复创建，正在合并到房间 [{0}]..." },
            { "chat.start_countdown", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR] 双方已就绪，{0} 秒后开始载入地图！") },
            { "chat.converge", PcsrColors.Wrap(PcsrColors.Warning, "[PCSR] 检测到双方各自建房，正在自动合并到同一个房间 [{0}]...") },

            { "chat.reroll.req_recv", PcsrColors.Wrap(PcsrColors.Warning, "[PCSR] 对手 [{0}] 请求重新随机换图 (#{1} {2})，30秒内按 F10 打开面板可同意或拒绝！") },
            { "chat.reroll.req_sent", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR] 已发起换图投票 (#{0} {1})，等待对手在 30 秒内回应...") },
            { "chat.reroll.accepted", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR] 双方同意换图！正在切换至新地图 #{0} ({1})...") },
            { "chat.reroll.declined", PcsrColors.Wrap(PcsrColors.DangerBright, "[PCSR] 换图投票被拒绝，继续当前比赛！") },
            { "chat.forfeit.opp", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR] 对手 [{0}] 已主动投降认输，你获得了本局胜利！") },
            { "chat.forfeit.self", PcsrColors.Wrap(PcsrColors.DangerBright, "[PCSR] 你已主动投降认输。") },
            { "chat.void.announced", PcsrColors.Wrap(PcsrColors.VoidAmber, "[PCSR] 对局连接中断，本局作废，双方均不计分。") },

            { "antiboost.unrated", "好友练习赛 (不计 ELO)" },
            { "antiboost.self", "检测到自建小号对局 (本次不计 ELO)" },
            { "antiboost.gap", "双方分差过大 (>650)，ELO 结算权重降低" },
            { "antiboost.limit", "防刷分触发：30分钟内同对手超过3局 (0 ELO)" },
            { "antiboost.half", "短时间重复匹配同对手：本局 ELO 权重减半" },
            { "elo.note.progress", "高度差修正 {0}{1}" },

            { "win.subtitle", "PCSR 1v1 实时排位中心" },
            { "win.box.ruleset", "赛制与房间" },
            { "win.box.match", "对局状态与实时队列" },
            { "win.box.board", "全服实时天梯" },
            { "win.box.queue", "实时匹配情况" },
            { "win.box.history", "我的历史战绩" },
            { "win.board.col.player", "选手" },
            { "win.board.col.tier", "段位" },
            { "win.board.col.elo", "ELO" },
            { "win.board.col.record", "总战绩" },
            { "win.board.col.modes", "全山标准 / 高难天阶 / 单群系冲刺" },
            { "win.board.record", "{0}W / {1}L" },
            { "win.board.modes", "全山 {0}-{1} · 天阶 {2}-{3} · 冲刺 {4}-{5}" },
            { "win.queue.refreshed", "最近刷新 {0}" },
            { "win.queue.no_data", "尚无队列数据（等待首次刷新）" },
            { "win.queue.total", "在线约战 <b>{0}</b> 人   ·   等待房 <b>{1}</b>   ·   激战中 <b>{2}</b>" },
            { "win.queue.mine_queuing", "我正在排队: {0}   已匹配 {1}" },
            { "win.queue.mine_idle", "当前赛制: {0}   （未在排队）" },
            { "win.queue.mode.std", "全山标准" },
            { "win.queue.mode.asc", "高难天阶" },
            { "win.queue.mode.spr", "单群系冲刺" },
            { "win.queue.cell", "{0} 人 · 等待 {1} · 激战 {2}" },
            { "win.history.loading", "正在获取历史战绩..." },
            { "win.history.count", "最近 {0} 局（点击刷新可重新拉取）" },
            { "win.history.route", "单群系: {0}" },
            { "win.history.empty", "还没有云端历史战绩记录" },
            { "win.history.win", "胜" },
            { "win.history.loss", "负" },
            { "win.history.void", "作废" },
            { "hud.btn.refresh_history", "刷新战绩" },
            { "win.lbl.mult2", "ELO 倍率 {0}x" },
            { "profile.window.title", "玩家档案" },
            { "profile.title", "<b>{0}</b> 的资料卡" },
            { "profile.meta", "全服排名 {0}   |   " + PcsrColors.WrapBold("{1}", "{2}") + "   |   当前分数 " + PcsrColors.WrapBold(PcsrColors.AccentBlue, "{3}") + " ELO   |   历史最高分 " + PcsrColors.WrapBold("{4}", "{5}") + " ELO" },
            { "profile.body", "总战绩: " + PcsrColors.WrapBold(PcsrColors.Positive, "{0}胜") + " " + PcsrColors.WrapBold(PcsrColors.Danger, "{1}负") + "   胜率 <b>{2}%</b>   连胜 " + PcsrColors.WrapBold("{3}", "{4}") + "   累计 <b>{5}</b> 局" },
            { "profile.modes", PcsrColors.WrapBold(PcsrColors.ModeStd, "全山标准") + "  <b>{0}</b> 局  " + PcsrColors.Wrap(PcsrColors.Muted, "({1}W-{2}L · {3}%)") + "\n" + PcsrColors.WrapBold(PcsrColors.ModeAsc, "高难天阶") + "  <b>{4}</b> 局  " + PcsrColors.Wrap(PcsrColors.Muted, "({5}W-{6}L · {7}%)") + "\n" + PcsrColors.WrapBold(PcsrColors.ModeSpr, "单群系冲刺") + "  <b>{8}</b> 局  " + PcsrColors.Wrap(PcsrColors.Muted, "({9}W-{10}L · {11}%)") },
            { "profile.favorite", "最爱群系: {0}   最常玩难度: {1}   最常玩模式: {2}" },
            { "profile.biome.label", "最爱群系（可自选）" },
            { "profile.biome.saved", "已保存你的最爱群系。" },
            { "profile.none", "未设置" },
            { "profile.btn.set_fav", "把当前选择设为最爱" },
            { "profile.fav_self_only", "只能修改自己的档案偏好。" },
            { "profile.fav_saved", "已保存你的偏好并同步到云端天梯。" },
            { "profile.nick.label", "PCSR 昵称" },
            { "profile.nick.hint", "2~16 个字符，可用中英文/数字/空格/_ - .·，留空则显示 Steam 昵称；每 24 小时可改一次。" },
            { "profile.nick.save", "保存昵称" },
            { "profile.nick.saved", "昵称已更新并同步到云端天梯。" },
            { "profile.nick.saving", "正在保存昵称..." },
            { "profile.nick.failed", "昵称保存失败: {0}" },
            { "profile.nick.offline", "当前离线或未连接云端，无法修改昵称。" },
            { "profile.no_steam", "未获取到 SteamID，无法打开云端档案。" },
            { "profile.steam_only", "还没有云端档案，先打一局排位吧。" },
            { "win.btn.profile", "我的档案" },
            { "post.elo.detail2", "赛前 <b>{0}</b>  ->  当前 <b>{1}</b>   |   基础变动: {2}   |   进度补偿: {3}   |   保底/倍率修正: {4}   |   我方进度: {5}% vs 对手: {6}%" },
            { "post.tier.changed", "   <size=70%>" + PcsrColors.Wrap(PcsrColors.Positive, "(段位变动)") + "</size>" },
            { "post.splits.missing", "未记录" },
            { "win.lbl.format", "赛制选择" },
            { "win.lbl.ascent", "天阶难度" },
            { "win.lbl.ascent_value", "天阶 {0}" },
            { "win.lbl.mult", "ELO 倍率 {0}x" },
            { "win.lbl.invite", "好友房间码" },
            { "win.room.none", "当前未在房间中   |   我的段位: " + PcsrColors.WrapBold("{0}", "{1}") + " ({2} ELO · 胜率 {3}%)" },
            { "win.room.info", "房间: <b>{0}</b>   |   地图: <b>#{1} ({2})</b>   |   难度: <b>{3}</b>   |   种子: {4}" },
            { "win.board.empty", "暂无云端天梯数据（点击刷新重试）" },
            { "win.board.you_tag", PcsrColors.Wrap("1B8449", " [我]") },
            { "win.btn.help", "玩法说明" },
            { "win.btn.ok", "知道了" },
            { "win.btn.confirm", "确认" },
            { "win.btn.cancel", "取消" },
            { "win.prompt.forfeit", "确认投降？本局将判负并扣除 ELO。" },
            { "win.help.title", "PCSR 1v1 排位规则" },
            { "win.help.body", "· 双方使用相同地图与种子，不可见对手幽灵。\n· 死亡即判负，无复活。\n· 先登顶者获胜，中途离开判 DNF。\n· 第一名登顶后开启 30 秒超时倒计时。\n· 支持菜鸟新人(0.75x)、标准顶峰(1.00x)与天阶 1~8 难度，单群系冲刺为 0.22x。\n· 定级期与常规局均引入攀爬完成度(高度差)修正，逆风高完成度少扣分、大胜多加分。\n· 比赛期间随时可按 F10 呼出排位中心发起换图投票或投降认输。" },
            { "hud.btn.board_toggle", "全服榜" },
            { "hud.board.title", "<b>PCSR 1v1 全服实时天梯榜 (Top 10)</b>" },
            { "hud.btn.refresh_board", "刷新榜单" },
            { "cloud.unconfigured", "未配置云端天梯 URL（仅本地签名存档生效）" },
            { "cloud.loading", "正在同步全服天梯数据..." },
            { "cloud.updated", "已同步全服天梯 ({0} 名选手)" },
            { "cloud.pending", "战报已签名上报，等待对手战报或原版后端交叉核验..." },
            { "cloud.settled", "云端权威校验通过 ({1})！最新 ELO: {0}" },
            { "cloud.voided", "本局已作废，云端分数未变动: {0}" },
            { "cloud.error", "云端连接提示: {0}" },

            { "post.title.win", "PCSR 1v1 排位胜利！" },
            { "post.title.loss", "PCSR 1v1 排位落败" },
            { "post.title.void", "PCSR 1v1 对局作废" },
            { "post.subtitle", "房间 {0}  ·  {1}  ·  地图 #{2} ({3})  ·  种子 {4}" },
            { "post.elo.void_detail", "赛前 {0} -> 当前 {0} | 本局作废: 双方均不加减分, 不计胜负 | 我方进度: {1}% vs 对手: {2}%" },
            { "post.box.elo", "ELO 积分结算与段位进度" },
            { "post.box.splits", "赛段用时对比 (Splits Comparison)" },
            { "post.elo.detail", "赛前 <b>{0}</b>  ->  当前 <b>{1}</b>   |   基础变动: {2}   |   高度差补偿: {3}   |   我方进度: {4}% vs 对手: {5}%" },
            { "post.splits.header", "#   赛段名称                     我方累计         对手累计         赛段差值" },
            { "post.splits.empty", "本局无分段营火打卡记录（或提前结束）" },
            { "post.splits.finish", "顶峰完赛 (FINISH)" },
            { "post.btn.close", "关闭战报" },
            { "post.btn.rematch", "再来一局 (继续匹配)" },
            { "post.btn.menu", "返回主菜单 / 机场" }
        };

        private static readonly Dictionary<string, string> EnStrings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "hud.window.title", "PeakCSR - PCSR 1v1 Live Ranked ({0})" },
            { "hud.peak", "Peak: {0}" },
            { "hud.record", "Record: " + PcsrColors.Wrap(PcsrColors.PositiveBright, "{0}W") + " - " + PcsrColors.Wrap(PcsrColors.DangerBright, "{1}L") + " (WR {2}%)   |   Streak: {3}" },
            { "hud.ruleset.label", "<b>PCSR Ruleset:</b> {0}  " + PcsrColors.Wrap(PcsrColors.NeutralGray, "[No Ghost · Blind Seed · Sudden Death · {1}x ELO]") },
            { "hud.format.standard", "Full Peak" },
            { "hud.format.hardcore", "Hardcore Ascent" },
            { "hud.format.sprint", "Biome Sprint (0.22x)" },
            { "hud.ascent.select", "<b>Ascent Level:</b> Ascent {0} (ELO Mult: {1}x)" },
            { "hud.invite.label", "<b>Invite Code:</b>" },
            { "hud.invite.join", "Join Code" },
            { "hud.invite.host", "Create Private" },
            { "hud.invite.rated", "Rated" },
            { "hud.invite.unrated", "Unrated" },
            { "hud.status", "<b>Status:</b> {0}" },
            { "hud.room_info", "Room: <b>{0}</b>   |   Map: <b>#{1} ({2})</b>   |   Diff: <b>{3}</b>   |   Seed: {4}" },
            { "hud.seed.hidden", "HIDDEN (Blind)" },
            { "hud.opponent", "Opponent: <b>{0}</b> ({1} ELO · {3}) {2}" },
            { "hud.verified.ok", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR Verified]") },
            { "hud.verified.wait", PcsrColors.Wrap(PcsrColors.Warning, "[Handshaking...]") },
            { "hud.progress", "Climb Progress: You <b>{0}%</b>  vs  Rival <b>{1}%</b>  (" + PcsrColors.Wrap("{2}", "{3}{4}%") + ")   |   Win Rate: <b>{5}%</b>" },
            { "hud.vitals", "Rival Vitals: HP {0}%  |  Stamina {1}%  {2}" },
            { "hud.state.down", PcsrColors.Wrap(PcsrColors.DownRed, "[PASSED OUT]") },
            { "hud.state.climbing", "[Climbing]" },
            { "hud.btn.queue", "Queue PCSR 1v1 Live Ranked" },
            { "hud.btn.cancel_queue", "Cancel Queue / Leave Room" },
            { "hud.btn.forfeit", "Forfeit Match (-ELO)" },
            { "hud.btn.forfeit_confirm", "Confirm Forfeit" },
            { "hud.btn.forfeit_cancel", "Cancel" },
            { "hud.btn.reroll", "Vote Reroll Map (30s)" },
            { "hud.btn.reroll_waiting", "Waiting Opponent Vote ({0}s)..." },
            { "hud.reroll.prompt", PcsrColors.Wrap(PcsrColors.Warning, "<b>[Map Vote]</b> Opponent wants to reroll map ({0}s). Agree?") },
            { "hud.btn.reroll_accept", "✓ Agree Reroll" },
            { "hud.btn.reroll_decline", "✕ Decline" },
            { "hud.btn.queue_next", "Queue Next 1v1" },
            { "hud.btn.return_idle", "Return to Idle" },
            { "hud.btn.post_report", "Match Report" },
            { "hud.btn.void_requeue", "Queue Next 1v1" },
            { "hud.void.notice", "This match is VOID: no ELO change, no W/L for either side." },
            { "hud.btn.lang", "Lang: EN" },

            { "sign.queuing", PcsrColors.Wrap(PcsrColors.Orange, "PCSR 1v1") + "  <size=80%>• QUEUING ({0})</size>" },
            { "sign.idle", PcsrColors.Wrap("{0}", "PCSR 1v1") + "  <size=82%>• {1} ELO</size>" },
            { "hud.btn.native", "Native" },
            { "win.opponent.placeholder", "Waiting..." },
            { "common.anonymous", "Anonymous" },

            { "tier.sherpa", "SHERPA" },
            { "tier.alpinist", "ALPINIST" },
            { "tier.climber", "CLIMBER" },
            { "tier.hiker", "HIKER" },
            { "tier.scout", "SCOUT" },
            { "tier.tenderfoot", "TENDERFOOT" },
            { "tier.short.sherpa", "SHERPA" },
            { "tier.short.alpinist", "ALPINIST" },
            { "tier.short.climber", "CLIMBER" },
            { "tier.short.hiker", "HIKER" },
            { "tier.short.scout", "SCOUT" },
            { "tier.short.tenderfoot", "TENDERFOOT" },

            { "ascent.name.tenderfoot", "Tenderfoot" },
            { "ascent.name.peak", "Standard Peak" },
            { "ascent.name.numbered", "Ascent {0}" },
            { "ascent.short.tenderfoot", "Rookie(A-1)" },
            { "ascent.short.peak", "Peak(A0)" },
            { "ascent.short.numbered", "Ascent {0}" },

            { "format.standard.full", "Standard Full Peak (Peak)" },
            { "format.hardcore.full", "Hardcore Peak (Ascent {0})" },
            { "format.sprint.full", "Biome Sprint (Ascent {0})" },

            { "status.ready", "Ready - pick a ruleset and queue up" },
            { "status.not_ready", "RaceService not ready, enter Main Menu first." },
            { "status.searching", "Searching [{0}] 1v1 lobbies... (Elapsed {1})" },
            { "status.found_joining", "Found PCSR 1v1 lobby [{0}], joining..." },
            { "status.joining_code", "Joining room [{0}] via invite code..." },
            { "status.code_empty", "Please enter a valid room invite code!" },
            { "status.creating", "Creating PCSR 1v1 room ({0})..." },
            { "status.creating_private", "Creating private 1v1 room ({0})..." },
            { "status.waiting_opp", "Room [{0}] waiting for opponent (1/2) · {1}" },
            { "status.error", "Matchmaking error: {0}" },
            { "status.kick_vanilla", "Removed non-PeakCSR player [{0}]. Waiting for ranked opponent..." },
            { "status.verified_starting", "Opponent verified ({0} ELO)! Launching synchronized 1v1..." },
            { "status.matched_starting", "Matched vs {0} ({1} ELO)! Starting..." },
            { "status.aborted_early", "Match aborted / left early (DNF)." },
            { "status.ruleset_mismatch", "Ruleset mismatch, left the room (host: {0} / local: {1})." },
            { "status.env_mismatch", "Opponent lobby found but game content differs. Local ver {0} · mapset {1}." },
            { "win.lbl.env", "Env: ver {0} · mapset {1}" },
            { "win.queue.live_info", "Live Queue: ~<b>{0}</b> Players (Waiting <b>{1}</b> · Active <b>{2}</b>)   |   Std:{3}  Asc:{4}  Spr:{5}" },
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
            { "status.loss.dnf", "DEFEAT! You died / timed out (DNF)." },
            { "status.opp_finished_wait", "Opponent finished! Room auto-ends in ~30s, settling..." },
            { "status.win.opp_dnf", "VICTORY! Opponent [{0}] died / forfeited (DNF)!" },
            { "status.win.opp_dc", "VICTORY! Opponent [{0}] disconnected!" },
            { "status.loss.forfeit", "DEFEAT! You forfeited the match." },
            { "status.reconnecting", "Connection lost, reconnecting & verifying match state... ({0}s) Not settled yet." },
            { "status.void_disconnect", "Match result unverifiable ({0}). This match is VOID: no ELO change, no W/L." },
            { "status.void_local_dc", "Your connection dropped. This match is VOID: no ELO change, no W/L." },
            { "status.void_opp_dc", "Opponent [{0}] disconnected. Result unverifiable - this match is VOID for both sides." },
            { "status.ghost_room_skipped", "That room is already in progress (likely a disconnected leftover). Left it automatically - please queue again." },
            { "status.loading_stuck", "Still not in the map after {0}s, auto-retrying room join..." },
            { "status.reroll_waiting_room", "Reroll agreed. Waiting for host to create the new room..." },
            { "status.reroll_joining_room", "Got new room code [{0}], entering new map..." },
            { "status.leaving", "Leaving current room, please wait..." },
            { "status.start_countdown", "Both ready! Entering the map in {0}s...  vs {1}" },
            { "status.waiting_host_start", "Both ready, waiting for host to start the race..." },
            { "status.converging", "Duplicate empty rooms detected, merging into [{0}]..." },
            { "chat.start_countdown", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR] Both players ready. Loading the map in {0}s!") },
            { "chat.converge", PcsrColors.Wrap(PcsrColors.Warning, "[PCSR] Both sides created a room, auto-merging into [{0}]...") },

            { "chat.reroll.req_recv", PcsrColors.Wrap(PcsrColors.Warning, "[PCSR] Opponent [{0}] requested a map reroll (#{1} {2}). Press F10 within 30s to accept/decline!") },
            { "chat.reroll.req_sent", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR] Map reroll vote sent (#{0} {1}). Waiting 30s for opponent...") },
            { "chat.reroll.accepted", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR] Map reroll agreed! Switching to #{0} ({1})...") },
            { "chat.reroll.declined", PcsrColors.Wrap(PcsrColors.DangerBright, "[PCSR] Map reroll declined. Keep climbing!") },
            { "chat.forfeit.opp", PcsrColors.Wrap(PcsrColors.PositiveBright, "[PCSR] Opponent [{0}] forfeited the match! Victory!") },
            { "chat.forfeit.self", PcsrColors.Wrap(PcsrColors.DangerBright, "[PCSR] You forfeited the match.") },
            { "chat.void.announced", PcsrColors.Wrap(PcsrColors.VoidAmber, "[PCSR] Connection lost. This match is VOID - no ELO change for either side.") },

            { "antiboost.unrated", "Unrated Private Match (0 ELO)" },
            { "antiboost.self", "Self-match detected (0 ELO change)" },
            { "antiboost.gap", "Extreme ELO gap (>650), rating weight reduced" },
            { "antiboost.limit", "Anti-boost triggered: >3 matches vs same opponent in 30m (0 ELO)" },
            { "antiboost.half", "Repeated opponent (3rd match in 30m): 50% ELO weight" },
            { "elo.note.progress", "Height Bonus {0}{1}" },

            { "win.subtitle", "PCSR 1v1 Live Ranked Hub" },
            { "win.box.ruleset", "Ruleset & Room" },
            { "win.box.match", "Match State & Live Queue" },
            { "win.box.board", "Global Live Ladder" },
            { "win.box.queue", "Live Matchmaking" },
            { "win.box.history", "My Match History" },
            { "win.board.col.player", "PLAYER" },
            { "win.board.col.tier", "TIER" },
            { "win.board.col.elo", "ELO" },
            { "win.board.col.record", "W/L" },
            { "win.board.col.modes", "PEAK / ASC / SPR" },
            { "win.board.record", "{0}W / {1}L" },
            { "win.board.modes", "Peak {0}-{1} · Asc {2}-{3} · Spr {4}-{5}" },
            { "win.queue.refreshed", "Refreshed {0}" },
            { "win.queue.no_data", "No queue data yet (waiting for first refresh)" },
            { "win.queue.total", "Online <b>{0}</b>   ·   Waiting <b>{1}</b>   ·   Racing <b>{2}</b>" },
            { "win.queue.mine_queuing", "Queuing: {0}   elapsed {1}" },
            { "win.queue.mine_idle", "Current mode: {0}   (not queuing)" },
            { "win.queue.mode.std", "Full Peak" },
            { "win.queue.mode.asc", "Hardcore" },
            { "win.queue.mode.spr", "Biome Sprint" },
            { "win.queue.cell", "{0}p · {1} wait · {2} live" },
            { "win.history.loading", "Loading match history..." },
            { "win.history.count", "Last {0} matches (press refresh to reload)" },
            { "win.history.route", "Sprint: {0}" },
            { "win.history.empty", "No cloud match history yet" },
            { "win.history.win", "WIN" },
            { "win.history.loss", "LOSS" },
            { "win.history.void", "VOID" },
            { "hud.btn.refresh_history", "Refresh History" },
            { "win.lbl.mult2", "ELO Mult {0}x" },
            { "profile.window.title", "Player Profile" },
            { "profile.title", "<b>{0}</b>'s Profile" },
            { "profile.meta", "Global rank {0}   |   " + PcsrColors.WrapBold("{1}", "{2}") + "   |   Current " + PcsrColors.WrapBold(PcsrColors.AccentBlue, "{3}") + " ELO   |   Peak " + PcsrColors.WrapBold("{4}", "{5}") + " ELO" },
            { "profile.body", "Record: " + PcsrColors.WrapBold(PcsrColors.Positive, "{0}W") + " " + PcsrColors.WrapBold(PcsrColors.Danger, "{1}L") + "   WR <b>{2}%</b>   Streak " + PcsrColors.WrapBold("{3}", "{4}") + "   Games <b>{5}</b>" },
            { "profile.modes", PcsrColors.WrapBold(PcsrColors.ModeStd, "Full Peak") + "  <b>{0}</b> games  " + PcsrColors.Wrap(PcsrColors.Muted, "({1}W-{2}L · {3}%)") + "\n" + PcsrColors.WrapBold(PcsrColors.ModeAsc, "Hardcore") + "  <b>{4}</b> games  " + PcsrColors.Wrap(PcsrColors.Muted, "({5}W-{6}L · {7}%)") + "\n" + PcsrColors.WrapBold(PcsrColors.ModeSpr, "Biome Sprint") + "  <b>{8}</b> games  " + PcsrColors.Wrap(PcsrColors.Muted, "({9}W-{10}L · {11}%)") },
            { "profile.favorite", "Fav biome: {0}   Fav difficulty: {1}   Fav mode: {2}" },
            { "profile.biome.label", "Favorite Biome" },
            { "profile.biome.saved", "Favorite biome saved." },
            { "profile.none", "not set" },
            { "profile.btn.set_fav", "Set current as favorite" },
            { "profile.fav_self_only", "You can only edit your own profile." },
            { "profile.fav_saved", "Preferences saved and synced to the ladder." },
            { "profile.nick.label", "PCSR Nickname" },
            { "profile.nick.hint", "2-16 chars: letters, digits, spaces, _ - . ·. Leave empty to use your Steam name. Editable once per 24h." },
            { "profile.nick.save", "Save Nickname" },
            { "profile.nick.saved", "Nickname updated and synced to the ladder." },
            { "profile.nick.saving", "Saving nickname..." },
            { "profile.nick.failed", "Failed to save nickname: {0}" },
            { "profile.nick.offline", "Offline or cloud not reachable, nickname unchanged." },
            { "profile.no_steam", "SteamID unavailable, cannot open cloud profile." },
            { "profile.steam_only", "No cloud profile yet. Play one ranked match first." },
            { "win.btn.profile", "My Profile" },
            { "post.elo.detail2", "Before <b>{0}</b>  ->  After <b>{1}</b>   |   Base: {2}   |   Progress: {3}   |   Floor/Mult: {4}   |   Progress: {5}% vs {6}%" },
            { "post.tier.changed", "   <size=70%>" + PcsrColors.Wrap(PcsrColors.Positive, "(tier changed)") + "</size>" },
            { "post.splits.missing", "not recorded" },
            { "win.lbl.format", "Ruleset" },
            { "win.lbl.ascent", "Difficulty" },
            { "win.lbl.ascent_value", "{0}" },
            { "win.lbl.mult", "ELO Mult {0}x" },
            { "win.lbl.invite", "Friend Invite Code" },
            { "win.room.none", "Not in a room   |   My Rank: " + PcsrColors.WrapBold("{0}", "{1}") + " ({2} ELO · WR {3}%)" },
            { "win.room.info", "Room: <b>{0}</b>   |   Map: <b>#{1} ({2})</b>   |   Diff: <b>{3}</b>   |   Seed: {4}" },
            { "win.board.empty", "No cloud ladder data yet (press refresh)" },
            { "win.board.you_tag", PcsrColors.Wrap("1B8449", " [YOU]") },
            { "win.btn.help", "How to play" },
            { "win.btn.ok", "Got it" },
            { "win.btn.confirm", "Confirm" },
            { "win.btn.cancel", "Cancel" },
            { "win.prompt.forfeit", "Forfeit this match? You will lose ELO." },
            { "win.help.title", "PCSR 1v1 Ranked Rules" },
            { "win.help.body", "· Same map and seed for both players, opponent ghosts hidden.\n· Sudden Death: dying loses the match, no revive.\n· First to the peak wins; leaving early counts as DNF; 30s countdown starts when 1st player finishes.\n· Supports Tenderfoot(0.75x), Standard Peak(1.00x) and Ascents 1~8 across modes.biome sprint is 0.22x.\n· Includes Climb Progress / Height Difference ELO compensation.\n· Press F10 anytime during a match to vote map reroll or forfeit." },
            { "hud.btn.board_toggle", "Top 10" },
            { "hud.board.title", "<b>PCSR 1v1 Global Leaderboard (Top 10)</b>" },
            { "hud.btn.refresh_board", "Refresh" },
            { "cloud.unconfigured", "Cloud API URL not set (Using local signed ELO)" },
            { "cloud.loading", "Syncing global leaderboard..." },
            { "cloud.updated", "Global leaderboard synced ({0} players)" },
            { "cloud.pending", "Signed report sent, awaiting consensus / upstream verify..." },
            { "cloud.settled", "Cloud verified ({1})! New ELO: {0}" },
            { "cloud.voided", "Match voided. Cloud ELO unchanged: {0}" },
            { "cloud.error", "Cloud notice: {0}" },

            { "post.title.win", "PCSR 1v1 VICTORY!" },
            { "post.title.loss", "PCSR 1v1 DEFEAT" },
            { "post.title.void", "PCSR 1v1 MATCH VOID" },
            { "post.subtitle", "Room {0}  ·  {1}  ·  Map #{2} ({3})  ·  Seed {4}" },
            { "post.elo.void_detail", "Before {0} -> Now {0} | MATCH VOID: no ELO change, no W/L | My progress: {1}% vs Opponent: {2}%" },
            { "post.box.elo", "ELO Rating Settlement & Tier Progress" },
            { "post.box.splits", "Splits Comparison" },
            { "post.elo.detail", "Before <b>{0}</b>  ->  After <b>{1}</b>   |   Base: {2}   |   Height Adj: {3}   |   Progress: {4}% vs {5}%" },
            { "post.splits.header", "#   SEGMENT                      YOUR TIME        RIVAL TIME       DELTA" },
            { "post.splits.empty", "No campfire split records in this match" },
            { "post.splits.finish", "PEAK FINISH" },
            { "post.btn.close", "Close Report" },
            { "post.btn.rematch", "Queue Next 1v1" },
            { "post.btn.menu", "Return to Airport / Menu" }
        };

        public static CsrLanguage Current
        {
            get { return _current; }
            set
            {
                if (_current != value)
                {
                    _current = value;
                    Action h = OnLanguageChanged;
                    if (h != null) h();
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
