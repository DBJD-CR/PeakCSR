using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace PeakCSR
{
    // 逐个安全挂载 Harmony 补丁 + 输出启动自检报告 (方案 3.4.2)
    // RANKED 版本更新导致个别方法签名变化时, 启动日志能一眼定位失效补丁
    public static class PatchRegistry
    {
        private static Harmony _harmony;

        public static void PatchAll(Harmony harmony)
        {
            _harmony = harmony;
            List<string> ok = new List<string>();
            List<string> failed = new List<string>();

            Type[] types = typeof(PatchRegistry).Assembly.GetTypes();
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (!HasHarmonyPatch(type)) continue;
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok.Add(type.Name);
                }
                catch (Exception ex)
                {
                    failed.Add(type.Name + " (" + ex.Message + ")");
                    PcsrLog.LogWarning("[PeakCSR] 补丁挂载失败 " + type.Name + ": " + ex.Message);
                }
            }

            PcsrLog.LogInfo("[PeakCSR] 补丁自检: 成功挂载 " + ok.Count + " 个 [" + string.Join(", ", ok.ToArray()) + "]");
            if (failed.Count > 0)
            {
                PcsrLog.LogWarning("[PeakCSR] 失效补丁 " + failed.Count + " 个 [" + string.Join(", ", failed.ToArray()) + "]");
            }
        }

        public static void UnpatchSelf()
        {
            if (_harmony != null) _harmony.UnpatchSelf();
        }

        private static bool HasHarmonyPatch(Type type)
        {
            object[] any = type.GetCustomAttributes(false);
            for (int i = 0; i < any.Length; i++)
            {
                if (any[i] is HarmonyPatch) return true;
            }
            return false;
        }
    }
}
