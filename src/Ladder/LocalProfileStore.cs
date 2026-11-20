using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json;

namespace PeakCSR
{
    // 本地档案持久化: Newtonsoft.Json 序列化 + SHA256 签名 + 旧版平铺存档自动迁移
    // (原 EloCalculator 手写 StringBuilder JSON + 正则读取的替换实现)
    //
    // 迁移护栏 (护栏 3): 旧版平铺 JSON (含 winsAllPeak 等字段且无 schemaVersion) 必须先用
    // 旧版签名公式校验通过后无损迁移并重写为新格式, 绝不能让老玩家本地分数被判定为篡改而清零。
    public sealed class LocalProfileStore
    {
        private const string IntegritySalt = "PEAKCSR_1V1_SALT_2026";
        // 新格式版本号: 首次加载旧版文件后迁移写入
        private const int CurrentSchemaVersion = 2;

        private static readonly string SaveDir = Path.Combine(Paths.PluginPath, "PeakCSR");
        private static readonly string SavePath = Path.Combine(SaveDir, "elo_profile.json");

        // 序列化 DTO (camelCase 与旧存档字段名完全对齐)
        private sealed class ProfileDto
        {
            [JsonProperty("schemaVersion")] public int SchemaVersion = CurrentSchemaVersion;
            [JsonProperty("elo")] public int Elo;
            [JsonProperty("peakElo")] public int PeakElo;
            [JsonProperty("seedElo")] public int SeedElo;
            [JsonProperty("seededFromVanilla")] public bool SeededFromVanilla;
            [JsonProperty("wins")] public int Wins;
            [JsonProperty("losses")] public int Losses;
            [JsonProperty("draws")] public int Draws;
            [JsonProperty("streak")] public int Streak;
            // 新格式: 分赛制战绩数组 (0=全山 1=高难 2=单群系)
            [JsonProperty("modes")] public ModeRecordDto[] Modes;

            [JsonProperty("favoriteMap")] public int FavoriteMap = -1;
            [JsonProperty("favoriteAscent")] public int FavoriteAscent = -999;
            [JsonProperty("favoriteFormat")] public int FavoriteFormat = -1;
            [JsonProperty("ascentGames")] public int[] AscentGames;
            [JsonProperty("formatGames")] public int[] FormatGames;
            [JsonProperty("sig")] public string Sig;
            [JsonProperty("updatedUtc")] public string UpdatedUtc;
        }

        private sealed class ModeRecordDto
        {
            [JsonProperty("wins")] public int Wins;
            [JsonProperty("losses")] public int Losses;
            [JsonProperty("games")] public int Games;
        }

        public string SelfSteamId
        {
            get
            {
                try { return SteamIdentitySafe(); } catch { return "LOCAL"; }
            }
        }

        private static string SteamIdentitySafe()
        {
            string id = RankedPeak.Services.SteamIdentity.GetSteamId64();
            return string.IsNullOrEmpty(id) ? "LOCAL" : id;
        }

        public void Load(PlayerRecord record, int defaultElo)
        {
            record.Elo = defaultElo;
            record.PeakElo = defaultElo;
            record.SeedElo = defaultElo;
            record.SeededFromVanilla = false;
            try
            {
                if (!File.Exists(SavePath))
                {
                    Save(record);
                }
                else
                {
                    LoadFromJson(record, defaultElo);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取本地 ELO 存档失败，使用默认分数: " + ex.Message);
            }
        }

        private void LoadFromJson(PlayerRecord record, int defaultElo)
        {
            string json = File.ReadAllText(SavePath);

            // 先按新格式 (schemaVersion=2) 解析; 缺 schemaVersion 视为旧版平铺文件
            ProfileDto dto = null;
            try
            {
                dto = JsonConvert.DeserializeObject<ProfileDto>(json);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 本地存档 JSON 解析失败: " + ex.Message);
                return;
            }
            if (dto == null) return;

            if (dto.SchemaVersion < CurrentSchemaVersion)
            {
                MigrateFromLegacy(record, dto, defaultElo);
                return;
            }

            // 新格式签名校验: 覆盖全部计分字段
            int modeWins = 0, modeLosses = 0;
            if (dto.Modes != null)
            {
                for (int i = 0; i < dto.Modes.Length && i < 3; i++)
                {
                    modeWins += dto.Modes[i].Wins;
                    modeLosses += dto.Modes[i].Losses;
                }
            }
            string expectedSig = ComputeSignature(dto.Elo, dto.PeakElo, dto.Wins, dto.Losses, dto.Draws, modeWins, modeLosses);
            if (!string.IsNullOrEmpty(dto.Sig) && !string.Equals(dto.Sig, expectedSig, StringComparison.OrdinalIgnoreCase))
            {
                PcsrLog.LogWarning("[PeakCSR] 检测到本地 ELO 存档签名校验不一致，已重置为初始分数。");
                record.ResetToDefault(defaultElo);
                Save(record);
                return;
            }

            record.Elo = Clamp(dto.Elo, 100, 5000);
            record.PeakElo = Math.Max(record.Elo, dto.PeakElo);
            record.SeedElo = Clamp(dto.SeedElo, 850, 1500);
            record.SeededFromVanilla = dto.SeededFromVanilla;
            record.Wins = Math.Max(0, dto.Wins);
            record.Losses = Math.Max(0, dto.Losses);
            record.Draws = Math.Max(0, dto.Draws);
            record.Streak = dto.Streak;

            if (dto.Modes != null)
            {
                for (int i = 0; i < 3; i++)
                {
                    ModeRecordDto m = i < dto.Modes.Length ? dto.Modes[i] : null;
                    if (m == null) continue;
                    record.Modes[i] = new ModeRecord(Math.Max(0, m.Wins), Math.Max(0, m.Losses), Math.Max(0, m.Games));
                }
            }

            // 旧存档里 favoriteMap 存的是地图槽位, 可能远大于群系数; 超范围一律当未设置
            record.FavoriteMap = (dto.FavoriteMap >= 0 && dto.FavoriteMap < PcsrBiomes.Count) ? dto.FavoriteMap : -1;
            record.FavoriteAscent = dto.FavoriteAscent;
            record.FavoriteFormat = dto.FavoriteFormat;

            ApplyPreferenceStats(record, dto.AscentGames, dto.FormatGames);
        }

        // 旧版平铺 JSON 无损迁移 (签名公式与旧 EloCalculator 完全一致)
        private void MigrateFromLegacy(PlayerRecord record, ProfileDto legacy, int defaultElo)
        {
            int winsAllPeak = 0, lossesAllPeak = 0, winsHardcore = 0, lossesHardcore = 0, winsSprint = 0, lossesSprint = 0;

            // 旧版字段用 Newtonsoft 宽松解析 (手写 JSON 读取平铺字段)
            JObjectReader r = new JObjectReader(File.ReadAllText(SavePath));
            int elo = r.Int("elo", defaultElo);
            int peak = r.Int("peakElo", elo);
            int seed = r.Int("seedElo", elo);
            bool seeded = r.Int("seededFromVanilla", 0) != 0;
            int wins = r.Int("wins", 0);
            int losses = r.Int("losses", 0);
            int draws = r.Int("draws", 0);
            int streak = r.Int("streak", 0);
            winsAllPeak = r.Int("winsAllPeak", 0);
            lossesAllPeak = r.Int("lossesAllPeak", 0);
            winsHardcore = r.Int("winsHardcore", 0);
            lossesHardcore = r.Int("lossesHardcore", 0);
            winsSprint = r.Int("winsSprint", 0);
            lossesSprint = r.Int("lossesSprint", 0);
            int gamesAllPeak = r.Int("gamesAllPeak", Math.Max(0, winsAllPeak + lossesAllPeak));
            int gamesHardcore = r.Int("gamesHardcore", Math.Max(0, winsHardcore + lossesHardcore));
            int gamesSprint = r.Int("gamesSprint", Math.Max(0, winsSprint + lossesSprint));
            int favoriteMap = r.Int("favoriteMap", -1);
            int favoriteAscent = r.Int("favoriteAscent", -999);
            int favoriteFormat = r.Int("favoriteFormat", -1);
            string sig = r.Str("sig");

            // 旧版签名公式: (elo, peak, wins, losses, draws, modeWins, modeLosses)
            string expectedSig = ComputeSignature(elo, peak, wins, losses, draws,
                winsAllPeak + winsHardcore + winsSprint, lossesAllPeak + lossesHardcore + lossesSprint);

            if (!string.IsNullOrEmpty(sig) && !string.Equals(sig, expectedSig, StringComparison.OrdinalIgnoreCase))
            {
                PcsrLog.LogWarning("[PeakCSR] 检测到本地 ELO 存档签名校验不一致，已重置为初始分数。");
                record.ResetToDefault(defaultElo);
                Save(record);
                return;
            }

            record.Elo = Clamp(elo, 100, 5000);
            record.PeakElo = Math.Max(record.Elo, peak);
            record.SeedElo = Clamp(seed, 850, 1500);
            record.SeededFromVanilla = seeded;
            record.Wins = Math.Max(0, wins);
            record.Losses = Math.Max(0, losses);
            record.Draws = Math.Max(0, draws);
            record.Streak = streak;
            record.Modes[0] = new ModeRecord(Math.Max(0, winsAllPeak), Math.Max(0, lossesAllPeak), Math.Max(0, gamesAllPeak));
            record.Modes[1] = new ModeRecord(Math.Max(0, winsHardcore), Math.Max(0, lossesHardcore), Math.Max(0, gamesHardcore));
            record.Modes[2] = new ModeRecord(Math.Max(0, winsSprint), Math.Max(0, lossesSprint), Math.Max(0, gamesSprint));
            record.FavoriteMap = (favoriteMap >= 0 && favoriteMap < PcsrBiomes.Count) ? favoriteMap : -1;
            record.FavoriteAscent = favoriteAscent;
            record.FavoriteFormat = favoriteFormat;

            // 旧版偏好统计: ascentGames_0..9 / formatGames_0..2
            for (int i = 0; i < record.AscentGames.Length; i++)
            {
                record.AscentGames[i] = Math.Max(0, r.Int("ascentGames_" + i, 0));
            }
            for (int i = 0; i < record.FormatGames.Length; i++)
            {
                record.FormatGames[i] = Math.Max(0, r.Int("formatGames_" + i, 0));
            }

            PcsrLog.LogInfo("[PeakCSR] 已将旧版平铺存档无损迁移为新格式 (schemaVersion=" + CurrentSchemaVersion + ")。");
            Save(record);
        }

        private static void ApplyPreferenceStats(PlayerRecord record, int[] ascentGames, int[] formatGames)
        {
            Array.Clear(record.AscentGames, 0, record.AscentGames.Length);
            Array.Clear(record.FormatGames, 0, record.FormatGames.Length);
            if (ascentGames != null)
            {
                for (int i = 0; i < record.AscentGames.Length && i < ascentGames.Length; i++)
                {
                    record.AscentGames[i] = Math.Max(0, ascentGames[i]);
                }
            }
            if (formatGames != null)
            {
                for (int i = 0; i < record.FormatGames.Length && i < formatGames.Length; i++)
                {
                    record.FormatGames[i] = Math.Max(0, formatGames[i]);
                }
            }
        }

        public void Save(PlayerRecord record)
        {
            try
            {
                Directory.CreateDirectory(SaveDir);

                int modeWins = record.Modes[0].Wins + record.Modes[1].Wins + record.Modes[2].Wins;
                int modeLosses = record.Modes[0].Losses + record.Modes[1].Losses + record.Modes[2].Losses;
                string sig = ComputeSignature(record.Elo, record.PeakElo, record.Wins, record.Losses, record.Draws, modeWins, modeLosses);

                ProfileDto dto = new ProfileDto
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Elo = record.Elo,
                    PeakElo = record.PeakElo,
                    SeedElo = record.SeedElo,
                    SeededFromVanilla = record.SeededFromVanilla,
                    Wins = record.Wins,
                    Losses = record.Losses,
                    Draws = record.Draws,
                    Streak = record.Streak,
                    Modes = new ModeRecordDto[]
                    {
                        new ModeRecordDto { Wins = record.Modes[0].Wins, Losses = record.Modes[0].Losses, Games = record.Modes[0].Games },
                        new ModeRecordDto { Wins = record.Modes[1].Wins, Losses = record.Modes[1].Losses, Games = record.Modes[1].Games },
                        new ModeRecordDto { Wins = record.Modes[2].Wins, Losses = record.Modes[2].Losses, Games = record.Modes[2].Games }
                    },
                    FavoriteMap = record.FavoriteMap,
                    FavoriteAscent = record.FavoriteAscent,
                    FavoriteFormat = record.FavoriteFormat,
                    AscentGames = (int[])record.AscentGames.Clone(),
                    FormatGames = (int[])record.FormatGames.Clone(),
                    Sig = sig,
                    UpdatedUtc = DateTime.UtcNow.ToString("O")
                };

                File.WriteAllText(SavePath, JsonConvert.SerializeObject(dto, Formatting.Indented));
            }
            catch (Exception ex)
            {
                PcsrLog.LogError("[PeakCSR] 保存本地 ELO 存档失败: " + ex.Message);
            }
        }

        // 签名公式与旧版逐字节兼容 (迁移校验与新格式校验共用)
        private static string ComputeSignature(int elo, int peak, int wins, int losses, int draws, int modeWins, int modeLosses)
        {
            string steamId = "LOCAL";
            try
            {
                string id = RankedPeak.Services.SteamIdentity.GetSteamId64();
                steamId = string.IsNullOrEmpty(id) ? "LOCAL" : id;
            }
            catch
            {
            }
            string raw = steamId + ":" + elo + ":" + peak + ":" + wins + ":" + losses + ":" + draws
                + ":" + modeWins + ":" + modeLosses + ":" + IntegritySalt;
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                StringBuilder sb = new StringBuilder(16);
                for (int i = 0; i < 8; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        private static int Clamp(int v, int min, int max)
        {
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        // 旧版平铺字段的宽松读取器 (仅迁移路径使用)
        private sealed class JObjectReader
        {
            private readonly Newtonsoft.Json.Linq.JObject _root;

            public JObjectReader(string json)
            {
                try { _root = Newtonsoft.Json.Linq.JObject.Parse(json); }
                catch { _root = new Newtonsoft.Json.Linq.JObject(); }
            }

            public int Int(string key, int fallback)
            {
                if (_root == null) return fallback;
                Newtonsoft.Json.Linq.JToken v = _root[key];
                if (v == null || v.Type == Newtonsoft.Json.Linq.JTokenType.Null) return fallback;
                try { return (int)v; } catch { return fallback; }
            }

            public string Str(string key)
            {
                if (_root == null) return null;
                Newtonsoft.Json.Linq.JToken v = _root[key];
                return (v != null && v.Type != Newtonsoft.Json.Linq.JTokenType.Null) ? v.ToString() : null;
            }
        }
    }
}
