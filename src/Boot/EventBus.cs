using System;
using System.Collections.Generic;

namespace PeakCSR
{
    // 领域事件定义 (进程内广播, 由 EventBus 派发)
    // 用普通静态委托字段而非 event: 允许 EventBus 做统一清理与遍历
    public static class CsrEvents
    {
        // 对局状态机任何可见变化 (原 MatchController.OnStateChanged)
        public static Action MatchStateChanged;

        // 结算画面关闭 / 返回机场 (原 PcsrMenuWindow 每帧 FindAnyObjectByType<EndScreen>() 的替代通知)
        public static Action EndScreenClosed;

        // 本地 ELO / 档案变化 (原 EloCalculator.OnRatingChanged)
        public static Action RatingChanged;

        internal static void RaiseMatchStateChanged()
        {
            Action h = MatchStateChanged;
            if (h != null) h();
        }

        internal static void RaiseEndScreenClosed()
        {
            Action h = EndScreenClosed;
            if (h != null) h();
        }

        internal static void RaiseRatingChanged()
        {
            Action h = RatingChanged;
            if (h != null) h();
        }
    }

    // 轻量级强类型事件派发器 (取代分散的 static event)
    public static class EventBus
    {
        // 兼容旧事件名的转发订阅
        public static event Action OnMatchStateChanged
        {
            add { CsrEvents.MatchStateChanged += value; }
            remove { CsrEvents.MatchStateChanged -= value; }
        }

        public static event Action OnEndScreenClosed
        {
            add { CsrEvents.EndScreenClosed += value; }
            remove { CsrEvents.EndScreenClosed -= value; }
        }

        public static event Action OnRatingChanged
        {
            add { CsrEvents.RatingChanged += value; }
            remove { CsrEvents.RatingChanged -= value; }
        }

        public static void PublishMatchStateChanged()
        {
            CsrEvents.RaiseMatchStateChanged();
        }

        public static void PublishEndScreenClosed()
        {
            CsrEvents.RaiseEndScreenClosed();
        }

        public static void PublishRatingChanged()
        {
            CsrEvents.RaiseRatingChanged();
        }

        // 防泄漏辅助: 摘除所有静态订阅 (主要用于域重载调试场景)
        public static void ClearStaticSubscriptions()
        {
            CsrEvents.MatchStateChanged = null;
            CsrEvents.EndScreenClosed = null;
            CsrEvents.RatingChanged = null;
        }
    }
}
