using System;
using HarmonyLib;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // 修正 RANKED 原生 RaceProgress 的两个进度计算缺陷
    //
    // 1) 全山模式: 进度 = (段索引 + 段内插值) / 总段数。点亮一次篝火时
    //    MountainProgressHandler.SetSegmentComplete 把 maxProgressPointReached 加 1,
    //    段内插值从 1 归零、段索引 +1, 于是画面瞬间跳 1/总段数。
    //    全山约 25 个进度点 → 正好 +4%。这就是"水平垂直位置没变, 进度却瞬间涨 4%"的原因。
    //
    // 2) 单群系模式: 返回值只有段内插值, 但段索引用的是整座山的绝对索引。
    //    点亮本群系终点篝火后索引跳到"下一群系"的进度点, 插值立刻崩回 0,
    //    结算页于是显示"点灯前一帧"的 81.4% 这种数字。
    //
    // 修正策略 (只在本机是 PCSR 对局时生效, 不污染普通 RANKED 与单人速通):
    //   · 记录历史最大值, 进度单调不减 (拒绝因索引跳变导致的回退)
    //   · 只有"我方自己真正登顶完赛"才强制 1.0
    //     (绝不能因为 race.Status == finished 或已经结算就置 1: 掉线作废、对手完赛、
    //      超时收尾都会把 status 变成 finished, 那样会给对手和自己刷出一个假的 100%)
    //   · 用当前 Z 坐标相对最近两个进度点的投影做连续估计, 抹平"点灯瞬间跳变"
    [HarmonyPatch(typeof(RaceProgress), "Current")]
    public static class ProgressMonotonicPatch
    {
        public static void Postfix(ref float __result)
        {
            try
            {
                // 静态闸门 (护栏 2): 只在 PCSR 对局内生效
                if (!MatchCoordinator.IsCurrentPeakCsrMatch()) return;
                if (__result < 0f) return;

                bool finishedByMe = HasSelfClimbedToPeak();
                float zRatio = ComputeZRatio();
                __result = ProgressMonotonicState.Correct(__result, finishedByMe, zRatio);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 进度修正补丁异常: " + ex.Message);
            }
        }

        // 唯一允许把进度强制成 100% 的条件: 服务端确认"我自己"已经完赛。
        // 注意不能用 race.Status == finished / Session.State == Finished:
        // 掉线作废、对手先登顶、AutoEndMs 超时收尾都会满足那两个条件, 而那些场景下
        // 我一个进度点都没点, 却会被显示成 100%, 并随实时帧同步给对手。
        private static bool HasSelfClimbedToPeak()
        {
            try
            {
                RaceService svc = RaceService.Instance;
                if (svc == null) return false;
                RaceParticipant self = svc.Self;
                if (self == null) return false;
                return self.State == "finished";
            }
            catch
            {
                return false;
            }
        }

        private static float ComputeZRatio()
        {
            try
            {
                Character local = Character.localCharacter;
                if (local == null) return -1f;

                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return -1f;
                ProgressPointEx[] pts = ProgressPointEx.From(handler.progressPoints);
                if (pts.Length < 2) return -1f;

                // 过滤掉 NADIR 这种终局点后的辅助点
                int last = pts.Length - 1;
                while (last > 0 && pts[last].IsNadir) last--;
                if (last < 1) return -1f;

                Vector3 pos = local.Center;

                // 找到最后一个已越过的点 (按 Z)
                int reached = -1;
                for (int i = 0; i <= last; i++)
                {
                    if (pts[i].HasPosition && pos.z > pts[i].Z) reached = i;
                }
                if (reached >= last) return 1f;
                if (reached < 0) reached = 0;
                if (!pts[reached].HasPosition || !pts[reached + 1].HasPosition) return -1f;

                float zA = pts[reached].Z;
                float zB = pts[reached + 1].Z;
                float span = zB - zA;
                float frac = span > 0.5f ? Mathf.Clamp01((pos.z - zA) / span) : 1f;
                return Mathf.Clamp01((reached + frac) / Mathf.Max(1, last));
            }
            catch
            {
                return -1f;
            }
        }
    }

    // MountainProgressHandler.ProgressPoint 是公有嵌套类型, 这里做一个轻量投影避免反射开销
    internal struct ProgressPointEx
    {
        public bool HasPosition;
        public float Z;
        public bool IsNadir;

        public static ProgressPointEx[] From(Array points)
        {
            if (points == null) return new ProgressPointEx[0];
            int len = points.Length;
            ProgressPointEx[] arr = new ProgressPointEx[len];
            for (int i = 0; i < len; i++)
            {
                object p = points.GetValue(i);
                ProgressPointEx item = new ProgressPointEx();
                if (p != null)
                {
                    var pt = p as MountainProgressHandler.ProgressPoint;
                    if (pt != null)
                    {
                        item.IsNadir = string.Equals(pt.title, "NADIR", StringComparison.OrdinalIgnoreCase);
                        if (pt.transform != null)
                        {
                            item.HasPosition = true;
                            item.Z = pt.transform.position.z;
                        }
                    }
                }
                arr[i] = item;
            }
            return arr;
        }
    }
}
