using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using Newtonsoft.Json;

namespace PeakCSR
{
    // 待补传战报的磁盘持久化
    internal static class PendingReportStore
    {
        private const string FileName = "pending_reports.json";

        // 数据放 config: plugins 目录是部署产物, 升级时会被整体清理
        private static string SavePath { get { return PcsrDataPaths.Resolve(FileName); } }

        // 序列化载体: 与 LadderSyncService.PendingReport 字段一一对应
        internal sealed class Record
        {
            public string InviteCode = "";
            public string OpponentSteamId = "";
            public string OpponentName = "";
            public int OpponentElo;
            public string WinnerSteamId = "";
            public int Seed;
            public int LevelIndex;
            public int Ascent;
            public int Format;
            public int Route;
            public long DurationMs;
            public float MyProgress;
            public float OppProgress;
            public bool IsVoid;
            public string VoidReason = "";
            public int Attempts;
        }

        public static List<Record> Load()
        {
            List<Record> list = new List<Record>();
            try
            {
                string path = SavePath;
                if (!File.Exists(path)) return list;
                string json = File.ReadAllText(path);
                if (string.IsNullOrEmpty(json)) return list;

                List<Record> parsed = JsonConvert.DeserializeObject<List<Record>>(json);
                if (parsed == null) return list;

                // 脏数据不该把整个队列带崩: 逐条校验, 只留能用来上报的
                for (int i = 0; i < parsed.Count; i++)
                {
                    Record r = parsed[i];
                    if (r == null) continue;
                    if (string.IsNullOrEmpty(r.InviteCode) || string.IsNullOrEmpty(r.OpponentSteamId)) continue;
                    if (r.Seed <= 0) continue;
                    list.Add(r);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取待补传队列失败，本次按空队列处理: " + ex.Message);
            }
            return list;
        }

        public static void Save(List<Record> records)
        {
            try
            {
                string path = SavePath;
                if (records == null || records.Count == 0)
                {
                    // 队列清空时把文件也删掉, 避免下次启动读到一堆过期条目
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }

                path = PcsrDataPaths.PrepareSave(FileName);
                string tmpPath = path + ".tmp";
                File.WriteAllText(tmpPath, JsonConvert.SerializeObject(records, Formatting.Indented));
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
                // 持久化失败绝不能影响内存里的重投流程
                PcsrLog.LogWarning("[PeakCSR] 保存待补传队列失败 (不影响本次上报): " + ex.Message);
            }
        }
    }
}
