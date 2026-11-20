namespace PeakCSR
{
    // RaceProgress 单调平滑修正的纯状态机 (原 ProgressFixPatch 内嵌状态抽离)
    // 补丁层只做 Harmony hook 并把值喂进来, 业务层可安全 Reset, 不反向依赖补丁类
    public static class ProgressMonotonicState
    {
        // 警告输出汇点: 由 Boot 层注入 (Core 不反向依赖 Plugin), 未注入时静默
        public static System.Action<string> WarnSink;
    
        private static void Warn(string message)
        {
            System.Action<string> sink = WarnSink;
            if (sink != null) sink(message);
        }
    
        private static float _lastReported;
        private static bool _hasLast;
        // 平滑后的段内插值缓存, 用于在点灯瞬间延续原本的连续性
        private static float _zSmoothed;
        private static bool _hasZSmoothed;

        public static void Reset()
        {
            _lastReported = 0f;
            _hasLast = false;
            _zSmoothed = 0f;
            _hasZSmoothed = false;
        }

        // 只读给战报/UI 用: 本局真实到达过的最高进度, 不含任何终局强制的 1.0
        public static float PeakReported { get { return _lastReported; } }

        // 修正原始进度值。finishedByMe: 仅当服务端确认"我自己"完赛才允许钳到 1.0
        public static float Correct(float raw, bool finishedByMe, float zRatio)
        {
            float corrected = ComputeContinuous(raw, zRatio);
            corrected = ApplyMonotonic(corrected, finishedByMe);

            if (finishedByMe)
            {
                corrected = 1f;
                _lastReported = 1f;
                _hasLast = true;
            }

            if (corrected < 0f) corrected = 0f;
            if (corrected > 1f) corrected = 1f;
            return corrected;
        }

        // 用 Z 坐标相对进度点连线的投影做连续估计, 消除"点灯瞬间段索引跳变"
        private static float ComputeContinuous(float raw, float zRatio)
        {
            if (zRatio < 0f) return raw;

            // 平滑跟踪 Z 比例, 避免物理抖动
            if (!_hasZSmoothed)
            {
                _zSmoothed = zRatio;
                _hasZSmoothed = true;
            }
            else
            {
                _zSmoothed = _zSmoothed + (zRatio - _zSmoothed) * 0.25f;
            }

            // 取两者较大值: 原逻辑更激进时保留原值, Z 投影更连续时用 Z 投影
            float combined = _zSmoothed > raw ? _zSmoothed : raw;
            // 但 Z 投影只用于补齐点灯瞬间的凹陷, 不应超过合理的上界
            float cap = raw + 0.06f;
            if (combined > cap) combined = cap;
            if (combined < 0f) combined = 0f;
            if (combined > 1f) combined = 1f;
            return combined;
        }

        // 单调不减: 拒绝因索引跳变造成的回退, 但允许极小幅回落吸收噪声
        // 另外提供"缓存被污染"的自愈: 未完赛却缓存着 1.0, 而真实值远低于它时,
        // 说明这一帧读到的是终局伪值 (旧逻辑或官方逻辑写入的), 必须丢弃缓存重来。
        private static float ApplyMonotonic(float value, bool finishedByMe)
        {
            if (!_hasLast)
            {
                _lastReported = value;
                _hasLast = true;
                return value;
            }

            if (!finishedByMe && _lastReported >= 0.999f && value < 0.95f)
            {
                Warn("[PeakCSR] 检测到进度缓存被终局伪值污染 (1.0 -> " + value.ToString("F3") + ")，已重置单调基准。");
                _lastReported = value;
                return value;
            }

            const float Tolerance = 0.002f;
            float best = value - Tolerance;
            if (_lastReported > best) best = _lastReported;
            _lastReported = best;
            return best;
        }
    }
}
