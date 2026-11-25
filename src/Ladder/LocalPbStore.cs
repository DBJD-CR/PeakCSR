using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace PeakCSR
{
    // PB 台账持久化
    //
    // 刻意与 elo_profile.json 分离:
    //   - PB 体积比档案大得多, 且是只读统计, 不该进 ELO 签名体系
    //   - 存档损坏时不应该连累分数, 反之亦然
    public sealed class LocalPbStore
    {
        // PB 不做跨版本迁移: 版本号不符时整份忽略并重新积累, 避免读到脏数据
        private const int CurrentVersion = 1;
        private const string FileName = "personal_best.json";

        private sealed class EntryDto
        {
            [JsonProperty("c")] public int Category;
            [JsonProperty("k")] public int SubKey;
            [JsonProperty("ms")] public long TimeMs;
            [JsonProperty("lv")] public int LevelIndex;
            [JsonProperty("sd")] public int Seed;
            [JsonProperty("asc")] public int Ascent;
            [JsonProperty("fmt")] public int Format;
            [JsonProperty("at")] public long AchievedAtMs;
        }

        private sealed class FileDto
        {
            [JsonProperty("version")] public int Version;
            [JsonProperty("entries")] public List<EntryDto> Entries;
            // 尚未成功同步到云端的 PB: 退出游戏也不丢, 下次启动继续补传
            [JsonProperty("pending")] public List<EntryDto> Pending;
        }

        // 数据放 config: plugins 目录是部署产物, 升级时会被整体清理
        private static string SavePath { get { return PcsrDataPaths.Resolve(FileName); } }

        // 读取待补传队列 (与台账一起落盘, 避免退出后这批 PB 永远上不了云)
        public List<PersonalBest> LoadPending()
        {
            List<PersonalBest> result = new List<PersonalBest>();
            try
            {
                string path = SavePath;
                if (!File.Exists(path)) return result;
                FileDto dto = JsonConvert.DeserializeObject<FileDto>(File.ReadAllText(path));
                if (dto == null || dto.Version != CurrentVersion || dto.Pending == null) return result;
                for (int i = 0; i < dto.Pending.Count; i++)
                {
                    PersonalBest pb = FromDto(dto.Pending[i]);
                    if (pb != null) result.Add(pb);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取 PB 待补传队列失败 (按空处理): " + ex.Message);
            }
            return result;
        }

        private static PersonalBest FromDto(EntryDto e)
        {
            if (e == null || e.TimeMs <= 0) return null;
            return new PersonalBest
            {
                Key = new PbKey((PbCategory)e.Category, e.SubKey),
                TimeMs = e.TimeMs,
                LevelIndex = e.LevelIndex,
                Seed = e.Seed,
                Ascent = e.Ascent,
                Format = (PcsrRuleFormat)e.Format,
                AchievedAtMs = e.AchievedAtMs
            };
        }

        private static EntryDto ToDto(PersonalBest pb)
        {
            if (pb == null || !pb.IsValid) return null;
            return new EntryDto
            {
                Category = (int)pb.Key.Category,
                SubKey = pb.Key.SubKey,
                TimeMs = pb.TimeMs,
                LevelIndex = pb.LevelIndex,
                Seed = pb.Seed,
                Ascent = pb.Ascent,
                Format = (int)pb.Format,
                AchievedAtMs = pb.AchievedAtMs
            };
        }

        public void Load(PersonalBestLedger ledger)
        {
            if (ledger == null) return;
            try
            {
                string path = SavePath;
                if (!File.Exists(path)) return;
                FileDto dto = JsonConvert.DeserializeObject<FileDto>(File.ReadAllText(path));
                if (dto == null || dto.Entries == null) return;
                if (dto.Version != CurrentVersion)
                {
                    PcsrLog.LogWarning("[PeakCSR] PB 存档版本为 " + dto.Version + " (期望 "
                        + CurrentVersion + ")，按空台账重建。");
                    return;
                }
                for (int i = 0; i < dto.Entries.Count; i++)
                {
                    PersonalBest pb = FromDto(dto.Entries[i]);
                    // 存档里可能残留旧缺陷写下的脏值读取时按类别下限挡掉
                    if (pb != null && PersonalBestLedger.IsPlausible(pb.Key, pb.TimeMs))
                    {
                        ledger.Restore(pb);
                    }
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取 PB 存档失败 (按空台账处理): " + ex.Message);
            }
        }

        public void Save(PersonalBestLedger ledger)
        {
            Save(ledger, null);
        }

        // pending 一并落盘: 上传失败的那几条 PB 退出游戏后不会丢
        public void Save(PersonalBestLedger ledger, IEnumerable<PersonalBest> pending)
        {
            if (ledger == null) return;
            string path = PcsrDataPaths.PrepareSave(FileName);
            string tmpPath = path + ".tmp";
            try
            {
                FileDto dto = new FileDto
                {
                    Version = CurrentVersion,
                    Entries = new List<EntryDto>(),
                    Pending = new List<EntryDto>()
                };

                foreach (KeyValuePair<PbKey, PersonalBest> kv in ledger.All)
                {
                    EntryDto entry = ToDto(kv.Value);
                    if (entry != null) dto.Entries.Add(entry);
                }

                if (pending != null)
                {
                    foreach (PersonalBest pb in pending)
                    {
                        EntryDto entry = ToDto(pb);
                        if (entry != null) dto.Pending.Add(entry);
                    }
                }

                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(dto, Formatting.Indented));
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
                PcsrLog.LogError("[PeakCSR] 保存 PB 存档失败: " + ex.Message);
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
}
