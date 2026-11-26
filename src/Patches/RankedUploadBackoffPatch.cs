using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace PeakCSR
{
    // RANKED 上传队列的紧循环止血
    [HarmonyPatch(typeof(RankedPeak.Services.RunUploadService), "UploadOnce")]
    public static class RankedUploadBackoffPatch
    {
        private const float BaseBackoffSeconds = 30f;
        private const float MaxBackoffSeconds = 600f;

        // runId -> 最早可再次尝试的时刻 / 当前退避时长
        private static readonly Dictionary<string, float> NextAllowedAt = new Dictionary<string, float>();
        private static readonly Dictionary<string, float> Backoff = new Dictionary<string, float>();
        private static bool _hooked;

        public static bool Prefix(RankedPeak.Services.RunUploadService.RunUploadDto dto, ref IEnumerator __result)
        {
            try
            {
                HookSettledOnce();
                if (dto == null) return true;

                string id = dto.id ?? "";
                if (string.IsNullOrEmpty(id)) return true;

                float now = Time.realtimeSinceStartup;
                float nextAt;
                if (NextAllowedAt.TryGetValue(id, out nextAt) && now < nextAt)
                {
                    __result = Sleep(nextAt - now);
                    return false;
                }

                float cur;
                if (!Backoff.TryGetValue(id, out cur) || cur < BaseBackoffSeconds)
                {
                    cur = BaseBackoffSeconds;
                }
                NextAllowedAt[id] = now + cur;
                Backoff[id] = Mathf.Min(cur * 2f, MaxBackoffSeconds);
                return true;
            }
            catch
            {
                // 补丁自身异常绝不能影响游戏原有流程
                return true;
            }
        }

        private static void HookSettledOnce()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                RankedPeak.Services.RunUploadService.UploadSettled += OnSettled;
            }
            catch
            {
            }
        }

        // 上传真正有结论 (成功或被服务端拒收) 时清掉该 id 的退避记录
        private static void OnSettled(string runId, string status)
        {
            if (string.IsNullOrEmpty(runId)) return;
            NextAllowedAt.Remove(runId);
            Backoff.Remove(runId);
        }

        private static IEnumerator Sleep(float seconds)
        {
            if (seconds < 1f) seconds = 1f;
            if (seconds > MaxBackoffSeconds) seconds = MaxBackoffSeconds;
            yield return new WaitForSecondsRealtime(seconds);
        }
    }
}
