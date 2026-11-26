using System;

namespace PeakCSR
{
    // 最佳可能时间 (Best Possible Time)
    //
    // 全山路线按"互斥层级"组织, 每一层只能经过其中一个候选:
    //   海岸                                  (必经)
    //   雨林 / 森蕈                            (二选一)
    //   雪山 / 方山                            (二选一)
    //   (火山 + 熔炉) / (雾沼 + 城塞)          (两组互斥, 组内两段连续)
    //
    // 顶峰不单独成段: 它总是并入前一段 (见 BiomeSegmenter 的 PEAK 合并逻辑)
    // 天底保留统计价值 (玩家可以专门去刷), 但不在常规全山路线上, 因此不参与 BPT
    //
    // BPT = 每层取最优候选之和; 任一层没有任何可用赛段 PB 时返回 0 (表示还算不出来)
    public static class BptCalculator
    {
        // 一个候选路径: 组内若干连续赛段, 耗时为各段之和
        // 例: 火山+熔炉 这个候选由两段组成
        private static readonly int[][][] ExclusivePaths = new int[][][]
        {
            // 层 1: 海岸 (必经)
            new[] { new[] { Idx("SHORE") } },
            // 层 2: 雨林 / 森蕈
            new[] { new[] { Idx("TROPICS") }, new[] { Idx("ROOTS") } },
            // 层 3: 雪山 / 方山
            new[] { new[] { Idx("ALPINE") }, new[] { Idx("MESA") } },
            // 层 4: (火山+熔炉) / (雾沼+城塞)
            new[]
            {
                new[] { Idx("CALDERA"), Idx("THE KILN") },
                new[] { Idx("GLOOM"), Idx("THE CITADEL") }
            }
        };

        private static int Idx(string englishName)
        {
            for (int i = 0; i < PcsrBiomes.All.Length; i++)
            {
                if (string.Equals(PcsrBiomes.All[i].En, englishName, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        // 该群系是否能作为独立赛段被采集与展示
        //
        // 只有顶峰不单独成段: 玩家爬到顶峰点信号棒即完赛, 耗时归入前一段。
        // 天底保留在赛段 PB 列表里 (它是独立的一段, 有意与 BPT 解耦)。
        public static bool IsCollectibleSegmentBiome(int biomeIndex)
        {
            if (biomeIndex < 0 || biomeIndex >= PcsrBiomes.All.Length) return false;
            // 'P' = 顶峰
            return PcsrBiomes.All[biomeIndex].Sig != 'P';
        }

        // 该群系的赛段 PB 是否参与 BPT 常规路线计算
        public static bool CountsTowardBpt(int biomeIndex)
        {
            if (biomeIndex < 0 || biomeIndex >= PcsrBiomes.All.Length) return false;
            char sig = PcsrBiomes.All[biomeIndex].Sig;
            // 'P' = 顶峰, 'N' = 天底
            return sig != 'P' && sig != 'N';
        }

        // 可采集赛段的群系索引 (供 UI 列表遍历)
        public static int[] TrackableBiomes()
        {
            int count = 0;
            for (int i = 0; i < PcsrBiomes.All.Length; i++)
            {
                if (IsCollectibleSegmentBiome(i)) count++;
            }
            int[] result = new int[count];
            int cursor = 0;
            for (int i = 0; i < PcsrBiomes.All.Length; i++)
            {
                if (IsCollectibleSegmentBiome(i)) result[cursor++] = i;
            }
            return result;
        }

        // 计算全山最佳可能时间
        //
        // segmentMs: 给定群系索引返回赛段 PB 毫秒, 查不到返回 0
        // 返回 0 表示数据不足, 无法给出 BPT
        public static long ComputeBestPossible(Func<int, long> segmentMs)
        {
            int missingLayer;
            return ComputeBestPossible(segmentMs, out missingLayer);
        }

        // missingLayer: 返回 -1 表示算出来了; 否则是第一个凑不齐候选的层下标
        // 有了它 UI 才能区分"完全没跑过"与"只差某一层"
        public static long ComputeBestPossible(Func<int, long> segmentMs, out int missingLayer)
        {
            missingLayer = -1;
            if (segmentMs == null) return 0L;

            long total = 0L;
            for (int layer = 0; layer < ExclusivePaths.Length; layer++)
            {
                long best;
                if (!TryBestPathMs(ExclusivePaths[layer], segmentMs, out best))
                {
                    missingLayer = layer;
                    return 0L;
                }
                total += best;
            }
            return total;
        }

        // 某一层缺数据时, 列出该层所有候选路径的可读名称
        public static string DescribeLayer(int layer, bool chinese)
        {
            if (layer < 0 || layer >= ExclusivePaths.Length) return "";
            int[][] candidates = ExclusivePaths[layer];
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int c = 0; c < candidates.Length; c++)
            {
                if (c > 0) sb.Append(chinese ? " 或 " : " or ");
                int[] path = candidates[c];
                for (int s = 0; s < path.Length; s++)
                {
                    if (s > 0) sb.Append("+");
                    sb.Append(PcsrBiomes.Label(path[s], chinese));
                }
            }
            return sb.ToString();
        }

        // 某一层的最优候选耗时
        // 返回 false 表示该层没有任何完整候选 (缺赛段数据)
        private static bool TryBestPathMs(int[][] candidates, Func<int, long> segmentMs, out long bestMs)
        {
            bestMs = 0L;
            bool any = false;
            for (int c = 0; c < candidates.Length; c++)
            {
                int[] path = candidates[c];
                long sum = 0L;
                bool complete = true;
                for (int s = 0; s < path.Length; s++)
                {
                    long ms = segmentMs(path[s]);
                    if (ms <= 0L)
                    {
                        complete = false;
                        break;
                    }
                    sum += ms;
                }
                if (!complete) continue;
                if (!any || sum < bestMs)
                {
                    bestMs = sum;
                    any = true;
                }
            }
            return any;
        }

        // 诊断用: 描述每一层选中的候选与实际耗时
        public static string Describe(Func<int, long> segmentMs, bool chinese)
        {
            if (segmentMs == null) return "";
            if (ComputeBestPossible(segmentMs) <= 0L) return "";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            for (int layer = 0; layer < ExclusivePaths.Length; layer++)
            {
                int[][] candidates = ExclusivePaths[layer];
                int[] bestPath = null;
                long bestMs = 0L;
                for (int c = 0; c < candidates.Length; c++)
                {
                    int[] path = candidates[c];
                    long sum = 0L;
                    bool complete = true;
                    for (int s = 0; s < path.Length; s++)
                    {
                        long ms = segmentMs(path[s]);
                        if (ms <= 0L)
                        {
                            complete = false;
                            break;
                        }
                        sum += ms;
                    }
                    if (!complete) continue;
                    if (bestPath == null || sum < bestMs)
                    {
                        bestMs = sum;
                        bestPath = path;
                    }
                }
                if (bestPath == null) continue;

                if (sb.Length > 0) sb.Append("  +  ");
                for (int s = 0; s < bestPath.Length; s++)
                {
                    if (s > 0) sb.Append(" → ");
                    sb.Append(PcsrBiomes.Label(bestPath[s], chinese));
                }
            }
            return sb.ToString();
        }
    }
}
