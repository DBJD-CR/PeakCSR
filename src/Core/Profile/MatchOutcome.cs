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
        public string Label;
        // 该段累计用时 (不是净用时), 与计时器口径一致
        public long MyMs;
        public long OppMs;
        // true 表示这条时间来自"跳帧回填", UI 上用 ~ 前缀提示精度略低
        public bool MyEstimated;
        public bool OppEstimated;
        // false 表示该玩家没到达这一段 (区别于"到达但没记录")
        public bool MyReached;
        public bool OppReached;
        // 本局路线终点段 (单群系迷你跑通常只有这一段)
        public bool IsFinal;
    }

    // PCSR 专属赛后战报数据快照 (原 MatchController.CsrPostMatchData, 零行为变化)
    public class CsrPostMatchData
    {
        public bool IWon;
        // 作废局: 不分胜负、不加减分
        public bool IsVoid;
        public string Reason = "";
        public string OpponentName = "";
        public int OpponentElo = 1000;
        public string InviteCode = "";
        public int LevelIndex;
        public string BiomeLabel = "";
        public int Seed;
        public int Ascent;
        public McsrRuleFormat Format;
        public int OldElo;
        public int NewElo;
        public int TotalDelta;
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
    }
}
