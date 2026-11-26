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
        // 天底阶段需要一次合法的"回落"
        // 单调保护只削偶发抖动, 这个是一次性的语义位移, 必须显式放行。
        private static bool _allowOneRollback;
        // 上一帧是否因为完赛被强制推到 1.0
        private static bool _lockedToFinish;
        // 锁定后连续读到"未完赛"的帧数: 用于区分"完赛状态抖动"与"锁定了伪值"
        private static int _unfinishedFramesSinceLock;
        // 观测到的真实峰值 (不含强制值)
        private static float _observedPeak;
        private static bool _hasObserved;

        // 解锁所需的连续未完赛帧数 (约 0.5 秒 @60fps)
        private const int UnlockAfterUnfinishedFrames = 30;

        public static void Reset()
        {
            _lastReported = 0f;
            _hasLast = false;
            _lockedToFinish = false;
            _unfinishedFramesSinceLock = 0;
            _observedPeak = 0f;
            _hasObserved = false;
            _allowOneRollback = false;
        }

        // 放行一次大跨度回落 (天底阶段切换)
        // 只影响下一帧的单调判定, 不会修改已记录观测峰值
        public static void AllowOneRollback()
        {
            _allowOneRollback = true;
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
            // NaN 在这里拦掉
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                // 保留上一帧的已知值, 没有历史值时按 0 处理
                return _hasLast ? _lastReported : 0f;
            }
            if (value < 0f) value = 0f;
            if (value > 1f) value = 1f;

            if (finished)
            {
                // 确认登顶: 返回值强制 1.0, 但观测峰值只记真实采样值
                Observe(value);
                _lastReported = 1f;
                _hasLast = true;
                _lockedToFinish = true;
                _unfinishedFramesSinceLock = 0;
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
                _lockedToFinish = false;
                return value;
            }

            if (_lockedToFinish)
            {
                // 完赛状态抖动: 短时间内还不能认定 1.0 是伪值, 保持现状
                _unfinishedFramesSinceLock++;
                if (value < 0.95f && _unfinishedFramesSinceLock >= 2)
                {
                    // 真实回退幅度很大: 立刻丢弃缓存以真实值为新基准
                    Warn("[PeakCSR] 检测到进度缓存被终局伪值污染 (1.0 -> " + value.ToString("F3") + ")，已重置单调基准。");
                    _lastReported = value;
                    _lockedToFinish = false;
                    _unfinishedFramesSinceLock = 0;
                    return value;
                }
                if (value < 0.99f && _unfinishedFramesSinceLock >= UnlockAfterUnfinishedFrames)
                {
                    // 持续未完赛且进度明显没到顶: 之前的 finished 是伪值。
                    // 阈值取 0.99 而不是 0.999, 避免"已经在终点附近"的局被误判成未完成而掉回 99.8%
                    Warn("[PeakCSR] 完赛状态已消失且进度未达顶 (" + value.ToString("F3") + ")，解除 100% 强制锁。");
                    _lastReported = value;
                    _lockedToFinish = false;
                    _unfinishedFramesSinceLock = 0;
                    return value;
                }
                // 仍处于锁定: 对外保持 1.0
                _lastReported = 1f;
                return 1f;
            }

            // 天底切换: 一次性放行回落, 用真实值重设基准, 之后继续单调
            if (_allowOneRollback)
            {
                _allowOneRollback = false;
                _lastReported = value;
                return value;
            }

            // 单调不减。容差只能削"回落", 不能削"上升"
            const float Tolerance = 0.002f;
            if (value > _lastReported) _lastReported = value;
            else if (value < _lastReported - Tolerance) _lastReported = value;
            return _lastReported;
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
