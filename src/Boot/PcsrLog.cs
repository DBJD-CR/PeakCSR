using BepInEx.Logging;

namespace PeakCSR
{
    // 带时间戳的日志包装:
    // BepInEx 5 的 DiskLogListener 写 LogOutput.log 时格式固定为 [Level : Source] Message,
    // 没有_time戳配置项, tail 窗口里完全看不出先后节奏, 这里在行首补本地时间
    public static class PcsrLog
    {
        private static ManualLogSource _src;

        public static void Initialize(ManualLogSource src)
        {
            _src = src;
        }

        private static string Stamp()
        {
            return System.DateTime.Now.ToString("HH:mm:ss");
        }

        public static void LogInfo(object message)
        {
            if (_src != null) _src.LogInfo(Stamp() + " " + message);
        }

        public static void LogWarning(object message)
        {
            if (_src != null) _src.LogWarning(Stamp() + " " + message);
        }

        public static void LogError(object message)
        {
            if (_src != null) _src.LogError(Stamp() + " " + message);
        }
    }
}
