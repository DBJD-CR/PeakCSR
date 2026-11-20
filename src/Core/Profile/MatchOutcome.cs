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
    public class CsrSplitRow
    {
        public string Label;
        public long MyMs;
        public long OppMs;
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
