namespace PeakCSR
{
    // 进度单调状态机 (纯状态, 不做任何插值/平滑)
    //
    // 职责边界 (这是本类存在的唯一理由):
    //   几何归一只由 ProgressProjection 负责 —— 索引取自玩家真实 Center.z,
    //   所以点灯/切段的离散跳变已经在源头消失了, 这里不需要也不允许再做平滑。
    //   本类只做两件事:
    //     1) 单调不减: 兜住偶发回退 (采样交错/传送瞬间), 且这个值会同步给对手
    //     2) 只有确认登顶才允许置 1.0
    public static class ProgressMonotonicState
    {
        // 警告输出汇点: 由 Boot 层注入 (Core 不反向依赖 Plugin), 未注入时静默
        public static System.Action<string> WarnSink;

        private static float _lastReported;
        private static bool _hasLast;
        // 上一帧是否因为完赛被强制推到 1.0
        private static bool _lockedToFinish;
        // 观测到的真实峰值 (不含强制值)
        private static float _observedPeak;
        private static bool _hasObserved;

        public static void Reset()
        {
            _lastReported = 0f;
            _hasLast = false;
            _lockedToFinish = false;
            _observedPeak = 0f;
            _hasObserved = false;
        }

        // 本局真实到达过的最高进度 (战报与上报的唯一真值)
        // 契约是"观测到的峰值": 完赛强制出来的 1.0 不算观测值, 只有真采样到才算
        public static float PeakReported { get { return _hasObserved ? _observedPeak : 0f; } }

        // 对外展示/上报用的进度 (确认完赛后为 1.0)
        public static float ReportedProgress { get { return _hasLast ? _lastReported : 0f; } }

        // value:    已经过几何修正的进度 (ProgressProjection 或 RANKED 兜底值)
        // finished: 确认"我自己"已经登顶完赛
        public static float Correct(float value, bool finished)
        {
            if (value < 0f) value = 0f;
            if (value > 1f) value = 1f;

            if (finished)
            {
                // 确认登顶: 观测峰值直接记 1.0, 返回值也强制 1.0
                Observe(1f);
                _lastReported = 1f;
                _hasLast = true;
                _lockedToFinish = true;
                return 1f;
            }

            // 未确认登顶却报满: 几何进度越过最后一点到服务端标 finished 之间会出现这种值,
            // 100% 只留给确认完赛, 先压到 0.999 再进缓存
            if (value >= 0.999f) value = 0.999f;
            Observe(value);

            if (!_hasLast)
            {
                _lastReported = value;
                _hasLast = true;
                return value;
            }

            // 上一帧被终局强制成 1.0, 本帧未完赛且真实值远低于它:
            // 说明那是结算后残留的伪值, 丢弃缓存以真实值为新基准。
            if (_lockedToFinish && value < 0.95f)
            {
                Warn("[PeakCSR] 检测到进度缓存被终局伪值污染 (1.0 -> " + value.ToString("F3") + ")，已重置单调基准。");
                _lastReported = value;
                _lockedToFinish = false;
                return value;
            }

            // 单调不减, 容忍极小幅回落吸收采样噪声
            const float Tolerance = 0.002f;
            float best = value - Tolerance;
            if (_lastReported > best) best = _lastReported;
            _lastReported = best;
            return best;
        }

        // 记录观测峰值 (只增不减)
        private static void Observe(float value)
        {
            if (!_hasObserved || value > _observedPeak)
            {
                _observedPeak = value;
                _hasObserved = true;
            }
        }

        private static void Warn(string message)
        {
            System.Action<string> sink = WarnSink;
            if (sink != null) sink(message);
        }
    }
}
