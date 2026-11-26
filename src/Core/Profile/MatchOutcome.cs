using System;
using System.Collections.Generic;

namespace PeakCSR
{
    // ELO 结算明细值对象 (原 EloSettlementDetail)
    public struct SettlementDetail
    {
        public int OldElo;
        public int NewElo;
        public int TotalDelta;
        public int BaseDelta;
        public int ProgressBonus;
        // 保底 / 上限钳制带来的附加修正 (总变动 - 基础 - 进度补偿)
        public int FloorAdjust;
        public int MinWinDelta;
        public double ModeMultiplier;
        public double AntiBoostWeight;
        public bool IsPlacement;
        public string OldTierFull;
        public string NewTierFull;
        public string AntiBoostNote;
        // 断线 / 无法确认结果时置位: 不改变 ELO, 也不计入胜负
        public bool Void;
    }

    // 赛后战报中的单个赛段对比行
    // Label 是稳定段名 (progressPoints[i].title), 可直接当 best time 字典的 key
    public class CsrSplitRow
    {
        // 屏幕显示名 (中文)
        public string Label;
        // 稳定英文段名: best time / 历史比对一律用它, 不受本地化影响
        public string Key;
        // 该段累计用时 (不是净用时), 与计时器口径一致
        public long MyMs;
        public long OppMs;
        // 该段单独用时 (本段累计 - 上一段累计), 首次计算后缓存
        public long MySegMs;
        public long OppSegMs;
        // true 表示这条时间来自"跳帧回填", UI 上用 ~ 前缀提示精度略低
        public bool MyEstimated;
        public bool OppEstimated;
        // false 表示该玩家没到达这一段 (区别于"到达但没记录")
        public bool MyReached;
        public bool OppReached;
        // 未到达时该段内的爬升比例 (0..1)
        public float MyFraction;
        public float OppFraction;
        // 本局路线终点段 (单群系迷你跑通常只有这一段)
        public bool IsFinal;
    }

    // PCSR 专属赛后战报数据快照 (原 MatchController.CsrPostMatchData, 零行为变化)
    public class CsrPostMatchData
    {
        public bool IWon;
        // 作废局: 不分胜负、不加减分
        public bool IsVoid;
        // 我方是否真的登顶完赛 (到顶峰点信号棒触发 stats.won → SelfPeakMs > 0)
        // 唯一可信的"有效成绩"判据: 我方赢下这局不代表我方爬到过顶
        // (对手阵亡/掉线/投降时我方也可能只跑到半山腰)
        public bool SelfSummited;
        // 非计分局永远不会上报云端, 也不会出现"待云端核验", 战报需要区分
        public bool IsRatedMatch = true;
        public string Reason = "";
        public string OpponentName = "";
        public int OpponentElo = 1000;
        public string InviteCode = "";
        public int LevelIndex;
        public string BiomeLabel = "";
        public int Seed;
        public int Ascent;
        // 单群系冲刺的本局路线号 (1 起, 0 = 全山)
        // 冲刺局的群系归属靠它反查地图签名串, 不能取签名首字符
        public int Route;
        public PcsrRuleFormat Format;
        public int OldElo;
        public int NewElo;
        public int TotalDelta;
        // 云端权威结算回来后的最终值: 与本地预估不一致时用它重绘战报
        public int AuthNewElo;
        public bool CloudSettled;
        public int BaseDelta;
        public int ProgressBonus;
        // 保底 / 上限钳制带来的附加修正 (总变动 - 基础 - 进度补偿)
        public int FloorAdjust;
        public int MinWinDelta;
        public float MyProgress;
        public float OppProgress;
        public double ModeMultiplier = 1.0;
        public bool IsPlacement;
        public string OldTier = "";
        public string NewTier = "";
        public string AntiBoostNote = "";
        public long MyTotalMs;
        public long OppTotalMs;
        public readonly List<CsrSplitRow> Splits = new List<CsrSplitRow>();
        public float CreatedAt;
        // 本局真实发生时刻 (Unix 毫秒), 用于 PB 达成时间
        public long CreatedAtMs;
    }
}
