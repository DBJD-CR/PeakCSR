using System;
using System.Collections.Generic;
using RankedPeak.Services;
using UnityEngine;

namespace PeakCSR
{
    // PB 采集与查询服务 (应用层)
    //
    // 数据来源刻意选 CsrPostMatchData 而不是游戏原生 RunRecord:
    //   - 战报快照已经包含赛制/天阶/地图/种子/总用时/分段, 字段完整且都是我们自己的口径
    //   - 不依赖 RankedPeak.Models 里未知的内部字段, 也不受游戏版本改动影响
    //   - 练习局的快照 IsRatedMatch = false, 天然可在这里过滤
    //
    // 台账按 steamId 隔离:
    //   本地玩家的台账会落盘; 别人的台账只存在内存里 (从云端拉来供档案卡展示),
    //   且永不影响本地玩家的 PB。
    public sealed class PersonalBestService
    {
        public static PersonalBestService Instance { get; private set; }

        // 本人的台账: 唯一会写盘的
        private readonly PersonalBestLedger _local = new PersonalBestLedger();
        // 他人台账: 纯内存缓存, 按需从云端拉取
        private readonly Dictionary<string, PersonalBestLedger> _remote =
            new Dictionary<string, PersonalBestLedger>(StringComparer.Ordinal);
        private readonly LocalPbStore _store = new LocalPbStore();
        private bool _loaded;

        // 本轮有刷新时置位, 供云端同步服务取走
        private readonly List<PersonalBest> _pendingUpload = new List<PersonalBest>();

        public static void Initialize()
        {
            if (Instance != null) return;
            Instance = new PersonalBestService();
            Instance.Load();
        }

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            _store.Load(_local);
            // 上次退出时没推成功的 PB 捡回来, 下次结算继续补传
            List<PersonalBest> restored = _store.LoadPending();
            if (restored != null) _pendingUpload.AddRange(restored);
            PcsrLog.LogInfo("[PeakCSR] 已加载本地 PB 台账 (" + _local.Count + " 条, 待补传 "
                + _pendingUpload.Count + " 条)。");
        }

        // 把台账与待补传队列一起落盘
        private void PersistAll()
        {
            _store.Save(_local, _pendingUpload);
        }

        // 单例生命周期收尾: 退出前把待补传队列落盘
        public void FlushOnQuit()
        {
            PersistAll();
        }

        // 某个玩家的 PB 快照; steamId 为空或等于自己时返回本地台账
        public PersonalBestSnapshot SnapshotFor(string steamId)
        {
            if (string.IsNullOrEmpty(steamId) || IsSelf(steamId)) return new PersonalBestSnapshot(_local);
            PersonalBestLedger ledger;
            if (_remote.TryGetValue(steamId, out ledger) && ledger != null) return new PersonalBestSnapshot(ledger);
            return PersonalBestSnapshot.Empty;
        }

        public PersonalBestSnapshot LocalSnapshot { get { return new PersonalBestSnapshot(_local); } }

        private static bool IsSelf(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return false;
            try { return string.Equals(steamId, SteamIdentity.GetSteamId64(), StringComparison.Ordinal); }
            catch { return false; }
        }

        // ===== 采集入口 =====

        // 从战报快照推算并合并 PB (只写本地台账)
        // 返回本次刷新的 PB 条数
        public int ObservePostMatch(CsrPostMatchData data)
        {
            if (data == null) return 0;
            if (data.IsVoid) return 0;
            // 非计分/练习局不计入 PB
            if (!data.IsRatedMatch) return 0;

            long achievedAt = ResolveAchievedAt(data);
            int refreshed = 0;

            // ---- 整局 PB (仅在自己真的登顶完赛时才算) ----
            // 判据必须是 SelfSummited, 不能只看 IWon
            bool summited = data.IWon && data.SelfSummited && data.MyTotalMs > 0;
            if (summited)
            {
                refreshed += Upsert(BuildAllPeakPb(data, achievedAt));
                refreshed += Upsert(BuildSprintPb(data, achievedAt));
                refreshed += Upsert(BuildAscentPb(data, achievedAt));
            }

            // ---- 赛段 PB (任何模式下经过该群系都参与竞争) ----
            if (data.Splits != null)
            {
                for (int i = 0; i < data.Splits.Count; i++)
                {
                    CsrSplitRow row = data.Splits[i];
                    if (row == null || !row.MyReached) continue;
                    // 估算时间精度不足, 不进 PB
                    if (row.MyEstimated) continue;

                    int biome = ResolveBiomeFromSegmentKey(row.Key);
                    if (biome < 0) continue;
                    // 顶峰不单独成段, 采集侧直接跳过
                    if (!BptCalculator.IsCollectibleSegmentBiome(biome)) continue;

                    // 单段净用时; 首段没有上一段, 用累计值
                    long segMs = i == 0 ? row.MyMs : row.MySegMs;
                    if (segMs <= 0) continue;
                    if (segMs > PersonalBestLedger.MaxPlausibleMs) continue;

                    refreshed += Upsert(new PersonalBest
                    {
                        Key = PbKey.BiomeSegment(biome),
                        TimeMs = segMs,
                        LevelIndex = data.LevelIndex,
                        Seed = data.Seed,
                        Ascent = data.Ascent,
                        Format = data.Format,
                        AchievedAtMs = achievedAt
                    });
                }
            }

            if (refreshed > 0)
            {
                // 台账与待上传队列一起写, 保证崩溃/退出都不丢
                PersistAll();
                PcsrLog.LogInfo("[PeakCSR] PB 刷新 " + refreshed + " 条 (台账共 " + _local.Count + " 条)。");
            }
            return refreshed;
        }

        // 标准全山 PB: 只认"标准赛制 + 非天阶难度"
        private PersonalBest BuildAllPeakPb(CsrPostMatchData data, long achievedAt)
        {
            if (data.Format != PcsrRuleFormat.StandardAllPeak) return null;
            if (data.Ascent > 0) return null;
            return MakeRunPb(PbKey.AllPeak, data.MyTotalMs, data, achievedAt);
        }

        // 单群系冲刺 PB: 整局只有一个赛段, 群系由地图签名首字符决定
        private PersonalBest BuildSprintPb(CsrPostMatchData data, long achievedAt)
        {
            if (data.Format != PcsrRuleFormat.BiomeSprint) return null;
            int biome = ResolveSprintBiome(data);
            if (biome < 0) return null;
            return MakeRunPb(PbKey.SprintBiome(biome), data.MyTotalMs, data, achievedAt);
        }

        // 天阶 PB: 仅高难天阶模式, 按天阶号细分
        private PersonalBest BuildAscentPb(CsrPostMatchData data, long achievedAt)
        {
            if (data.Format != PcsrRuleFormat.HardcoreAscent4) return null;
            int ascent = data.Ascent;
            if (ascent < 1) return null;
            return MakeRunPb(PbKey.Ascent(ascent), data.MyTotalMs, data, achievedAt);
        }

        private static PersonalBest MakeRunPb(PbKey key, long timeMs, CsrPostMatchData data, long achievedAt)
        {
            return new PersonalBest
            {
                Key = key,
                TimeMs = timeMs,
                LevelIndex = data.LevelIndex,
                Seed = data.Seed,
                Ascent = data.Ascent,
                Format = data.Format,
                AchievedAtMs = achievedAt
            };
        }

        // 只补达成时间, 不动成绩 (重扫专用)
        private bool BackfillAchievedAt(PbKey key, long timeMs, long achievedAtMs)
        {
            if (achievedAtMs <= 0) return false;
            PersonalBest existing;
            if (!_local.TryGet(key, out existing) || existing == null) return false;
            // 只有"同一个成绩"才认为是同一次达成, 避免把别的局的时刻错挂上来
            if (existing.TimeMs != timeMs) return false;
            if (existing.AchievedAtMs > 0) return false;
            existing.AchievedAtMs = achievedAtMs;
            // 补上的时间也推云端
            for (int i = _pendingUpload.Count - 1; i >= 0; i--)
            {
                if (_pendingUpload[i].Key.Equals(existing.Key)) _pendingUpload.RemoveAt(i);
            }
            _pendingUpload.Add(existing);
            return true;
        }

        // 返回本条的刷新条数 (0 或 1), 便于调用方直接累加
        private int Upsert(PersonalBest pb)
        {
            if (pb == null) return 0;
            if (!_local.UpsertIfBetter(pb)) return 0;
            // 同一批里同一 Key 只留最后(最优)那条, 避免重复上传
            for (int i = _pendingUpload.Count - 1; i >= 0; i--)
            {
                if (_pendingUpload[i].Key.Equals(pb.Key)) _pendingUpload.RemoveAt(i);
            }
            _pendingUpload.Add(pb);
            return 1;
        }

        // 冲刺局的群系归属
        private static int ResolveSprintBiome(CsrPostMatchData data)
        {
            if (data.Splits != null)
            {
                for (int i = 0; i < data.Splits.Count; i++)
                {
                    CsrSplitRow row = data.Splits[i];
                    if (row == null || !row.MyReached) continue;
                    int biome = ResolveBiomeFromSegmentKey(row.Key);
                    if (biome >= 0) return biome;
                }
            }

            if (data.Route > 0)
            {
                // 原始签名: 显示签名会把非首位的 'S' 改写成 'G'
                string sig = MapCatalogHelper.RawBiomeSignature(data.LevelIndex);
                int idx = data.Route - 1;
                if (!string.IsNullOrEmpty(sig) && idx >= 0 && idx < sig.Length)
                {
                    int biome = PcsrBiomes.IndexOfSig(sig[idx]);
                    if (biome >= 0) return biome;
                }
            }
            return -1;
        }

        // 段名 -> 群系索引 (段名真值就是群系英文名)
        //
        // 这里不能用 BiomeSegmenter.NormalizeTitle: 它把 NADIR 归一成空串,
        // 天底的赛段 PB 会被整条丢掉。天底有独立统计价值, 必须能识别出来。
        public static int ResolveBiomeFromSegmentKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return -1;
            string normalized = key.Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(normalized)) return -1;
            for (int i = 0; i < PcsrBiomes.All.Length; i++)
            {
                if (string.Equals(PcsrBiomes.All[i].En, normalized, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return -1;
        }

        // 达成时间取值: 优先快照记录的真实发生时刻, 缺失时才退回当前时间
        private static long ResolveAchievedAt(CsrPostMatchData data)
        {
            if (data != null && data.CreatedAtMs > 0) return data.CreatedAtMs;
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        // ===== 历史数据重扫 =====

        // 用本地战报归档按当前口径补全 PB
        public int RescanArchive()
        {
            LocalReportArchive archive = LocalReportArchive.Instance;
            if (archive == null) return 0;
            List<CsrPostMatchData> all = archive.ExportAll();
            if (all == null || all.Count == 0) return 0;

            int refreshed = 0;
            // 诊断计数: 刷新 0 条时必须能看出是哪一层过滤掉的
            int ratedCount = 0;
            int summitedCount = 0;
            int segRowCount = 0;
            int timeBackfill = 0;
            for (int i = 0; i < all.Count; i++)
            {
                CsrPostMatchData data = all[i];
                if (data == null || data.IsVoid) continue;
                if (!data.IsRatedMatch) continue;
                ratedCount++;

                // 达成时间按这一局真实发生的时刻写回, 不能统一成"扫描时的当前时间"
                long achievedAt = ResolveAchievedAt(data);
                // 与实时采集同口径: 必须是"我方登顶完赛"才算有效成绩
                if (data.IWon && data.SelfSummited && data.MyTotalMs > 0)
                {
                    summitedCount++;
                    UpsertRunPb(BuildAllPeakPb(data, achievedAt), ref refreshed, ref timeBackfill);
                    UpsertRunPb(BuildSprintPb(data, achievedAt), ref refreshed, ref timeBackfill);
                    UpsertRunPb(BuildAscentPb(data, achievedAt), ref refreshed, ref timeBackfill);
                }
                RescanSegments(data, achievedAt, ref segRowCount, ref refreshed, ref timeBackfill);
            }

            // 时间补全也算一次变更, 必须落盘, 否则下次启动又要重算
            if (refreshed > 0 || timeBackfill > 0)
            {
                PersistAll();
            }
            PcsrLog.LogInfo("[PeakCSR] 历史重扫完成, PB 刷新 " + refreshed + " 条, 补达成时间 "
                + timeBackfill + " 条 (归档 " + all.Count + " 局, 计分 " + ratedCount + " 局, 登顶 "
                + summitedCount + " 局, 有效赛段行 " + segRowCount + ", 台账共 " + _local.Count + " 条)。");
            return refreshed + timeBackfill;
        }

        // 整局 PB 的重扫入库: 先试更优覆盖, 再试"只补时间"
        private void UpsertRunPb(PersonalBest pb, ref int refreshed, ref int timeBackfill)
        {
            if (pb == null) return;
            if (Upsert(pb) > 0)
            {
                refreshed++;
                return;
            }
            if (BackfillAchievedAt(pb.Key, pb.TimeMs, pb.AchievedAtMs)) timeBackfill++;
        }

        // 赛段 PB 重扫: 用段名真值定位群系, 完全不依赖索引
        // 过滤口径必须与实时采集 ObservePostMatch 一致, 否则估算值会污染台账
        private void RescanSegments(CsrPostMatchData data, long achievedAt,
            ref int validRowCount, ref int refreshed, ref int timeBackfill)
        {
            if (data.Splits == null) return;
            for (int i = 0; i < data.Splits.Count; i++)
            {
                CsrSplitRow row = data.Splits[i];
                if (row == null) continue;
                if (!row.MyReached) continue;
                // 估算时间精度不足, 不进 PB (与实时采集一致)
                if (row.MyEstimated) continue;
                // 归档里 Reached=false 的行其 SegMs 也是 0, 用用时判定兜底
                if (row.MySegMs <= 0 && row.MyMs <= 0) continue;

                int biome = ResolveBiomeFromSegmentKey(row.Key);
                if (biome < 0) continue;
                if (!BptCalculator.IsCollectibleSegmentBiome(biome)) continue;

                long segMs = row.MySegMs > 0 ? row.MySegMs : (i == 0 ? row.MyMs : 0L);
                if (segMs <= 0) continue;
                if (segMs > PersonalBestLedger.MaxPlausibleMs) continue;
                validRowCount++;

                UpsertRunPb(new PersonalBest
                {
                    Key = PbKey.BiomeSegment(biome),
                    TimeMs = segMs,
                    LevelIndex = data.LevelIndex,
                    Seed = data.Seed,
                    Ascent = data.Ascent,
                    Format = data.Format,
                    AchievedAtMs = achievedAt
                }, ref refreshed, ref timeBackfill);
            }
        }

        // ===== 云端同步支持 =====

        // 取出并清空本轮待上传的 PB
        // 注意: 取出后就从持久化队列里移除了, 上传失败必须 Requeue, 否则这一批会丢
        public List<PersonalBest> DrainPendingUpload()
        {
            if (_pendingUpload.Count == 0) return null;
            List<PersonalBest> copy = new List<PersonalBest>(_pendingUpload);
            _pendingUpload.Clear();
            PersistAll();
            return copy;
        }

        // 上传失败时放回队列并立即落盘, 下次结算/启动再推一次
        public void RequeuePendingUpload(List<PersonalBest> items)
        {
            if (items == null) return;
            for (int i = 0; i < items.Count; i++)
            {
                PersonalBest pb = items[i];
                if (pb == null || !pb.IsValid) continue;
                bool exists = false;
                for (int k = 0; k < _pendingUpload.Count; k++)
                {
                    if (_pendingUpload[k].Key.Equals(pb.Key)) { exists = true; break; }
                }
                if (!exists) _pendingUpload.Add(pb);
            }
            PersistAll();
        }

        // 云端回灌: 自己的 PB 合并进本地台账并落盘; 别人的只做内存缓存
        public void MergeFromCloud(string steamId, IEnumerable<PersonalBest> remote)
        {
            if (remote == null) return;

            bool self = string.IsNullOrEmpty(steamId) || IsSelf(steamId);
            PersonalBestLedger target;
            if (self)
            {
                target = _local;
            }
            else
            {
                if (!_remote.TryGetValue(steamId, out target) || target == null)
                {
                    target = new PersonalBestLedger();
                    _remote[steamId] = target;
                }
            }

            int added = 0;
            foreach (PersonalBest pb in remote)
            {
                if (pb == null || !pb.IsValid) continue;
                // 带类别判定: 云端残留的脏值不得灌进本地台账
                if (!PersonalBestLedger.IsPlausible(pb.Key, pb.TimeMs)) continue;

                PersonalBest local;
                if (target.TryGet(pb.Key, out local) && local != null && local.TimeMs > 0)
                {
                    // 已有记录: 只有云端更优才采用
                    if (local.TimeMs <= pb.TimeMs) continue;
                    // 云端成绩更好但缺达成时间(老数据 achieved_at 为 0)时,
                    // 沿用本地的达成时间, 避免时间列凭空消失
                    if (pb.AchievedAtMs <= 0 && local.AchievedAtMs > 0)
                    {
                        pb.AchievedAtMs = local.AchievedAtMs;
                    }
                }
                target.Restore(pb);
                added++;
            }

            // 只有本人的台账才落盘
            if (self && added > 0) PersistAll();
        }

        // 清掉某个他人的缓存 (退出档案卡时可选调用)
        public void ForgetRemote(string steamId)
        {
            if (string.IsNullOrEmpty(steamId)) return;
            _remote.Remove(steamId);
        }
    }
}
