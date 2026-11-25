using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace PeakCSR
{
    // 本地战报归档
    //
    // 用途: 战绩历史点击回看时, 优先从这里取完整分段。
    // 云端 match_splits 只作为跨设备兜底, 本地命中时不必发请求。
    // 只保留最近 N 局, 避免无限增长 (分段数据比 PB 大得多)。
    public sealed class LocalReportArchive
    {
        private const int MaxEntries = 60;
        // v2 增补了 ELO 明细字段; 旧版按空归档重建, 反正 v1 里也没有明细可留
        private const int CurrentVersion = 2;
        private const int MinSupportedVersion = 1;
        private const string FileName = "report_archive.json";

        private sealed class SplitDto
        {
            [JsonProperty("k")] public string Key;
            [JsonProperty("seg")] public long SegMs;
            [JsonProperty("cum")] public long CumMs;
            [JsonProperty("est")] public bool Estimated;
            [JsonProperty("reach")] public bool Reached;
            // 对手半边: 本地归档是唯一零请求的完整来源, 丢掉对手会让回看页面信息更少
            [JsonProperty("oseg")] public long OppSegMs;
            [JsonProperty("ocum")] public long OppCumMs;
            [JsonProperty("oest")] public bool OppEstimated;
            [JsonProperty("oreach")] public bool OppReached;
        }

        private sealed class ReportDto
        {
            [JsonProperty("code")] public string InviteCode;
            [JsonProperty("seed")] public int Seed;
            [JsonProperty("won")] public bool Won;
            [JsonProperty("void")] public bool IsVoid;
            [JsonProperty("reason")] public string Reason;
            [JsonProperty("oppName")] public string OpponentName;
            [JsonProperty("oppElo")] public int OpponentElo;
            [JsonProperty("lv")] public int LevelIndex;
            [JsonProperty("biome")] public string BiomeLabel;
            [JsonProperty("asc")] public int Ascent;
            [JsonProperty("fmt")] public int Format;
            // 单群系冲刺的本局路线号
            [JsonProperty("rt")] public int Route;
            [JsonProperty("oldElo")] public int OldElo;
            [JsonProperty("newElo")] public int NewElo;
            [JsonProperty("delta")] public int TotalDelta;
            // ELO 明细: 云端权威分回填会覆盖总变动, 但基础分/进度补偿/保底修正
            // 是本地唯一一次算出来的, 不落盘就永久丢失
            [JsonProperty("base")] public int BaseDelta;
            [JsonProperty("bonus")] public int ProgressBonus;
            [JsonProperty("floor")] public int FloorAdjust;
            [JsonProperty("minWin")] public int MinWinDelta;
            [JsonProperty("modeMul")] public double ModeMultiplier;
            [JsonProperty("antiBoost")] public string AntiBoostNote;
            [JsonProperty("rated")] public bool IsRatedMatch;
            [JsonProperty("summited")] public bool SelfSummited;
            [JsonProperty("myMs")] public long MyTotalMs;
            [JsonProperty("oppMs")] public long OppTotalMs;
            [JsonProperty("myProg")] public float MyProgress;
            [JsonProperty("oppProg")] public float OppProgress;
            [JsonProperty("at")] public long CreatedAtMs;
            [JsonProperty("splits")] public List<SplitDto> Splits;
        }

        private sealed class FileDto
        {
            [JsonProperty("version")] public int Version;
            [JsonProperty("reports")] public List<ReportDto> Reports;
        }

        private readonly List<ReportDto> _reports = new List<ReportDto>();
        private bool _loaded;

        // 数据放 config: plugins 目录是部署产物, 升级时会被整体清理
        private static string SavePath { get { return PcsrDataPaths.Resolve(FileName); } }

        public static LocalReportArchive Instance { get; private set; }

        public static void Initialize()
        {
            if (Instance != null) return;
            Instance = new LocalReportArchive();
            Instance.Load();
        }

        // 归档键: 与云端 match_key 同构 (房间号|种子), 双方一致
        public static string ArchiveKey(string inviteCode, int seed)
        {
            return (inviteCode ?? "").Trim().ToUpperInvariant() + "|" + seed;
        }

        public void Append(CsrPostMatchData data)
        {
            if (data == null) return;
            // 作废局也存, 回看时能看到"本局作废"的完整信息
            string code = data.InviteCode ?? "";
            if (string.IsNullOrEmpty(code)) return;

            ReportDto dto = BuildDto(data);
            if (dto == null) return;

            // 同一局重复归档 (重开战报窗口) 时先删旧的
            for (int i = _reports.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_reports[i].InviteCode, dto.InviteCode, StringComparison.OrdinalIgnoreCase)
                    && _reports[i].Seed == dto.Seed)
                {
                    _reports.RemoveAt(i);
                }
            }
            // 最新的放最前
            _reports.Insert(0, dto);
            while (_reports.Count > MaxEntries) _reports.RemoveAt(_reports.Count - 1);

            Save();
        }

        private static ReportDto BuildDto(CsrPostMatchData data)
        {
            ReportDto dto = new ReportDto
            {
                InviteCode = data.InviteCode ?? "",
                Seed = data.Seed,
                Won = data.IWon,
                IsVoid = data.IsVoid,
                Reason = data.Reason ?? "",
                OpponentName = data.OpponentName ?? "",
                OpponentElo = data.OpponentElo,
                LevelIndex = data.LevelIndex,
                BiomeLabel = data.BiomeLabel ?? "",
                Ascent = data.Ascent,
                Format = (int)data.Format,
                Route = data.Route,
                OldElo = data.OldElo,
                NewElo = data.NewElo,
                TotalDelta = data.TotalDelta,
                BaseDelta = data.BaseDelta,
                ProgressBonus = data.ProgressBonus,
                FloorAdjust = data.FloorAdjust,
                MinWinDelta = data.MinWinDelta,
                ModeMultiplier = data.ModeMultiplier,
                AntiBoostNote = data.AntiBoostNote ?? "",
                IsRatedMatch = data.IsRatedMatch,
                SelfSummited = data.SelfSummited,
                MyTotalMs = data.MyTotalMs,
                OppTotalMs = data.OppTotalMs,
                MyProgress = data.MyProgress,
                OppProgress = data.OppProgress,
                // 优先用快照自带的真实发生时刻, 缺失时(老调用方)再退回当前时间
                CreatedAtMs = data.CreatedAtMs > 0
                    ? data.CreatedAtMs
                    : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Splits = new List<SplitDto>()
            };

            if (data.Splits != null)
            {
                for (int i = 0; i < data.Splits.Count; i++)
                {
                    CsrSplitRow row = data.Splits[i];
                    if (row == null) continue;
                    dto.Splits.Add(new SplitDto
                    {
                        Key = row.Key ?? "",
                        SegMs = row.MyReached ? row.MySegMs : 0L,
                        CumMs = row.MyReached ? row.MyMs : 0L,
                        Estimated = row.MyEstimated,
                        Reached = row.MyReached,
                        OppSegMs = row.OppReached ? row.OppSegMs : 0L,
                        OppCumMs = row.OppReached ? row.OppMs : 0L,
                        OppEstimated = row.OppEstimated,
                        OppReached = row.OppReached
                    });
                }
            }
            return dto;
        }

        // 供历史数据重扫使用: 把归档里每一局都投影成战报快照
        public List<CsrPostMatchData> ExportAll()
        {
            List<CsrPostMatchData> result = new List<CsrPostMatchData>();
            for (int i = 0; i < _reports.Count; i++)
            {
                CsrPostMatchData data = ToPostMatch(_reports[i]);
                if (data != null) result.Add(data);
            }
            return result;
        }

        // 查本地是否有该局完整战报, 命中则投影成回看用数据
        public CsrReportLookup TryFind(string inviteCode, int seed)
        {
            for (int i = 0; i < _reports.Count; i++)
            {
                ReportDto r = _reports[i];
                if (string.Equals(r.InviteCode, inviteCode ?? "", StringComparison.OrdinalIgnoreCase)
                    && r.Seed == seed)
                {
                    return CsrReportLookup.Hit(ToPostMatch(r));
                }
            }
            return CsrReportLookup.Miss();
        }

        private static CsrPostMatchData ToPostMatch(ReportDto r)
        {
            CsrPostMatchData data = new CsrPostMatchData
            {
                IWon = r.Won,
                IsVoid = r.IsVoid,
                Reason = r.Reason ?? "",
                OpponentName = r.OpponentName ?? "",
                OpponentElo = r.OpponentElo,
                InviteCode = r.InviteCode ?? "",
                LevelIndex = r.LevelIndex,
                BiomeLabel = r.BiomeLabel ?? "",
                Seed = r.Seed,
                Ascent = r.Ascent,
                Format = (PcsrRuleFormat)r.Format,
                Route = r.Route,
                OldElo = r.OldElo,
                NewElo = r.NewElo,
                TotalDelta = r.TotalDelta,
                // 云端权威分只覆盖总分, 明细三项原样还原, 战报因此不会变成"只剩结论"
                BaseDelta = r.BaseDelta,
                ProgressBonus = r.ProgressBonus,
                FloorAdjust = r.FloorAdjust,
                MinWinDelta = r.MinWinDelta,
                ModeMultiplier = r.ModeMultiplier > 0 ? r.ModeMultiplier : 1.0,
                AntiBoostNote = r.AntiBoostNote ?? "",
                IsRatedMatch = r.IsRatedMatch,
                SelfSummited = r.SelfSummited,
                MyTotalMs = r.MyTotalMs,
                OppTotalMs = r.OppTotalMs,
                MyProgress = r.MyProgress,
                OppProgress = r.OppProgress,
                // 还原原始发生时刻: 历史重扫据此写回正确的 PB 达成时间, 而不是"今天"
                CreatedAtMs = r.CreatedAtMs
            };

            if (r.Splits != null)
            {
                for (int i = 0; i < r.Splits.Count; i++)
                {
                    SplitDto s = r.Splits[i];
                    CsrSplitRow row = new CsrSplitRow
                    {
                        Key = s.Key ?? "",
                        Label = BiomeSegmenter.LocalizeKey(s.Key),
                        MyMs = s.CumMs,
                        MySegMs = s.SegMs,
                        MyEstimated = s.Estimated,
                        MyReached = s.Reached,
                        // 对手半边
                        OppMs = s.OppCumMs,
                        OppSegMs = s.OppSegMs,
                        OppEstimated = s.OppEstimated,
                        OppReached = s.OppReached,
                        IsFinal = i == r.Splits.Count - 1
                    };
                    data.Splits.Add(row);
                }
            }
            return data;
        }

        private void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                string path = SavePath;
                if (!File.Exists(path)) return;
                FileDto dto = JsonConvert.DeserializeObject<FileDto>(File.ReadAllText(path));
                if (dto == null || dto.Reports == null) return;
                // 低版本仍然可读: 缺失的明细字段反序列化后为 0/默认值,
                // 只是那几局的战报不再显示修正项, 没必要因此清空整份归档
                if (dto.Version < MinSupportedVersion || dto.Version > CurrentVersion)
                {
                    PcsrLog.LogWarning("[PeakCSR] 战报归档版本 " + dto.Version + " 不受支持，按空归档重建。");
                    return;
                }
                _reports.AddRange(dto.Reports);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取战报归档失败 (按空归档处理): " + ex.Message);
            }
        }

        private void Save()
        {
            string path = PcsrDataPaths.PrepareSave(FileName);
            string tmpPath = path + ".tmp";
            try
            {
                FileDto dto = new FileDto { Version = CurrentVersion, Reports = _reports };
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(dto));
                if (File.Exists(path))
                {
                    File.Replace(tmpPath, path, null);
                }
                else
                {
                    File.Move(tmpPath, path);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogError("[PeakCSR] 保存战报归档失败: " + ex.Message);
                try
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
                catch
                {
                }
            }
        }
    }

    // 战报查找结果: Hit 区分"本地命中"与"未命中需走云端"
    public struct CsrReportLookup
    {
        public bool Found;
        public CsrPostMatchData Data;

        public static CsrReportLookup Hit(CsrPostMatchData data)
        {
            return new CsrReportLookup { Found = true, Data = data };
        }

        public static CsrReportLookup Miss()
        {
            return new CsrReportLookup { Found = false, Data = null };
        }
    }
}
