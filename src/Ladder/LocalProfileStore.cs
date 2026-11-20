using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BepInEx;
using Newtonsoft.Json;

namespace PeakCSR
{
    // 本地档案持久化: Newtonsoft.Json 序列化 + SHA256 签名 + 原子写入
    // 旧版平铺存档格式已废弃 (测试期无存量玩家), 无 schemaVersion 的文件直接重置为默认档。
    //
    // 签名不含 SteamID: 本地文件是"本机唯一"的, 签名只为拦住手改分数,
    // 而 SteamID 在启动早期可能取不到 (回退 LOCAL), 一旦进签名会让同一次存档
    // "保存时算出来的值"和"下次加载时算出来的值"不一致, 从而把好档误判成篡改。
    public sealed class LocalProfileStore
    {
        private const string IntegritySalt = "PEAKCSR_1V1_SALT_2026";
        // 当前存档格式版本号
        private const int CurrentSchemaVersion = 2;

        private static readonly string SaveDir = Path.Combine(Paths.PluginPath, "PeakCSR");
        private static readonly string SavePath = Path.Combine(SaveDir, "elo_profile.json");

        // 序列化 DTO (camelCase 与存档字段名对齐)
        private sealed class ProfileDto
        {
            // 默认 0: 缺键时必然小于 CurrentSchemaVersion, 走"重置为默认档"而不是被当成有效档
            [JsonProperty("schemaVersion")] public int SchemaVersion;
            [JsonProperty("elo")] public int Elo;
            [JsonProperty("peakElo")] public int PeakElo;
            [JsonProperty("seedElo")] public int SeedElo;
            [JsonProperty("seededFromVanilla")] public bool SeededFromVanilla;
            [JsonProperty("wins")] public int Wins;
            [JsonProperty("losses")] public int Losses;
            [JsonProperty("draws")] public int Draws;
            [JsonProperty("streak")] public int Streak;
            // 分赛制战绩数组 (0=全山 1=高难 2=单群系)
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

        private static int EloMin { get { return RatingRules.Instance.Elo.ClampMin; } }
        private static int EloMax { get { return RatingRules.Instance.Elo.ClampMax; } }
        private static int SeedEloMin { get { return RatingRules.Instance.Seed.Min; } }
        private static int SeedEloMax { get { return RatingRules.Instance.Seed.Max; } }

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

            ProfileDto dto = null;
            try
            {
                dto = JsonConvert.DeserializeObject<ProfileDto>(json);
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 本地存档 JSON 解析失败，重置为默认档: " + ex.Message);
                record.ResetToDefault(defaultElo);
                Save(record);
                return;
            }
            if (dto == null)
            {
                record.ResetToDefault(defaultElo);
                Save(record);
                return;
            }

            // 旧版/损坏档: 不做迁移, 直接重置并重写
            if (dto.SchemaVersion != CurrentSchemaVersion)
            {
                PcsrLog.LogWarning("[PeakCSR] 本地存档版本号为 " + dto.SchemaVersion + " (期望 "
                    + CurrentSchemaVersion + ")，旧格式不再迁移，已重置为初始分数。");
                record.ResetToDefault(defaultElo);
                Save(record);
                return;
            }

            // 签名校验: 覆盖全部计分字段, 缺签名与签名不符同样按篡改处理
            int modeWins = 0, modeLosses = 0;
            if (dto.Modes != null)
            {
                for (int i = 0; i < dto.Modes.Length && i < 3; i++)
                {
                    if (dto.Modes[i] == null) continue;
                    modeWins += dto.Modes[i].Wins;
                    modeLosses += dto.Modes[i].Losses;
                }
            }
            string expectedSig = ComputeSignature(dto.Elo, dto.PeakElo, dto.Wins, dto.Losses, dto.Draws, modeWins, modeLosses);
            if (string.IsNullOrEmpty(dto.Sig) || !string.Equals(dto.Sig, expectedSig, StringComparison.OrdinalIgnoreCase))
            {
                PcsrLog.LogWarning("[PeakCSR] 检测到本地 ELO 存档签名校验不通过，已重置为初始分数。");
                record.ResetToDefault(defaultElo);
                Save(record);
                return;
            }

            record.Elo = Clamp(dto.Elo, EloMin, EloMax);
            record.PeakElo = Math.Max(record.Elo, dto.PeakElo);
            record.SeedElo = Clamp(dto.SeedElo, SeedEloMin, SeedEloMax);
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

            // favoriteMap 存的是群系索引; 超范围一律当未设置
            record.FavoriteMap = (dto.FavoriteMap >= 0 && dto.FavoriteMap < PcsrBiomes.Count) ? dto.FavoriteMap : -1;
            record.FavoriteAscent = dto.FavoriteAscent;
            record.FavoriteFormat = dto.FavoriteFormat;

            ApplyPreferenceStats(record, dto.AscentGames, dto.FormatGames);
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
            string tmpPath = SavePath + ".tmp";
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

                // 先写临时文件再原子替换: 直接写目标文件会先截断, 写到一半失败就把唯一存档写没了
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(dto, Formatting.Indented));
                if (File.Exists(SavePath))
                {
                    File.Replace(tmpPath, SavePath, null);
                }
                else
                {
                    File.Move(tmpPath, SavePath);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogError("[PeakCSR] 保存本地 ELO 存档失败: " + ex.Message);
                try
                {
                    if (File.Exists(tmpPath)) File.Delete(tmpPath);
                }
                catch
                {
                }
            }
        }

        // 签名公式: 只覆盖本地计分字段, 不掺 SteamID (见类注释)
        private static string ComputeSignature(int elo, int peak, int wins, int losses, int draws, int modeWins, int modeLosses)
        {
            string raw = elo + ":" + peak + ":" + wins + ":" + losses + ":" + draws
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
    }
}
