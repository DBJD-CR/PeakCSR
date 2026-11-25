using System.IO;
using BepInEx;

namespace PeakCSR
{
    // 玩家数据文件的统一落点
    //
    // 历史教训: 这些文件过去放在 plugins\PeakCSR 下, 而 build.ps1 会把该目录
    // 当成"遗留部署目录"整个删掉, 结果是每次重新部署都静默抹掉玩家的
    // ELO 档案 / 战绩 / PB 台账。plugins 是部署产物目录, 玩家数据必须放 config。
    //
    // 老用户的文件还在旧位置, Resolve 会先尝试搬迁, 搬不动就继续读旧位置,
    // 绝不因为搬迁失败而读到空数据。
    public static class PcsrDataPaths
    {
        private const string FolderName = "PeakCSR";

        public static string DataDir
        {
            get { return Path.Combine(Paths.ConfigPath, FolderName); }
        }

        private static string LegacyDir
        {
            get { return Path.Combine(Paths.PluginPath, FolderName); }
        }

        // 返回某个数据文件当前应读写的完整路径, 必要时把旧位置的文件搬过来
        public static string Resolve(string fileName)
        {
            string target = Path.Combine(DataDir, fileName);
            try
            {
                string legacy = Path.Combine(LegacyDir, fileName);
                if (File.Exists(target) || !File.Exists(legacy)) return target;
                Directory.CreateDirectory(DataDir);
                File.Move(legacy, target);
                PcsrLog.LogInfo("[PeakCSR] 数据文件已迁到 config 目录: " + fileName);
                return target;
            }
            catch (System.Exception ex)
            {
                // 搬迁失败不阻断启动: 原文件仍在旧位置, 直接读它
                PcsrLog.LogWarning("[PeakCSR] 迁移数据文件失败 (" + fileName + "): " + ex.Message);
                return Path.Combine(LegacyDir, fileName);
            }
        }

        // 保存前确保目录存在
        public static string PrepareSave(string fileName)
        {
            string path = Resolve(fileName);
            try
            {
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }
            catch
            {
            }
            return path;
        }
    }
}
