using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace PeakCSR
{
    // 控件多语言绑定器 (方案 3.3.3: 语言切换时原地刷新文本, 不销毁 UI 树)
    // 静态 UI 标签创建时 Bind(locKey), 语言切换时统一重刷已绑定的 TMP 控件;
    // 动态数据行不受 binder 管理 (由各面板 RefreshDynamic 每 0.2s 全量重刷, 行为不变)
    public static class LocalizedTextBinder
    {
        private sealed class Binding
        {
            public TMP_Text Text;
            public string LocKey;
            // 带参数的动态静态标签 (如标题 {0} = 快捷键), 参数由创建方闭包提供
            public Func<object[]> Args;
            // 语言切换后的回调 (如段位图标刷新), 可为 null
            public Action Extra;
        }

        private static readonly List<Binding> _bindings = new List<Binding>();

        public static void Bind(TMP_Text text, string locKey)
        {
            Bind(text, locKey, null, null);
        }

        public static void Bind(TMP_Text text, string locKey, Func<object[]> args, Action extra)
        {
            if (text == null || string.IsNullOrEmpty(locKey)) return;
            _bindings.Add(new Binding { Text = text, LocKey = locKey, Args = args, Extra = extra });
            ApplyTo(text, locKey, args);
            if (extra != null) extra();
        }

        // 原地刷新所有绑定控件 (语言切换事件触发), 已销毁的控件自动摘除
        public static void RefreshAll()
        {
            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                Binding b = _bindings[i];
                if (b.Text == null)
                {
                    _bindings.RemoveAt(i);
                    continue;
                }
                ApplyTo(b.Text, b.LocKey, b.Args);
                if (b.Extra != null) b.Extra();
            }
        }

        // Pill 按钮绑定: 直接取按钮上的 TMP_Text 子控件
        public static void BindPill(UnityEngine.UI.Button pill, string locKey)
        {
            if (pill == null) return;
            TMP_Text label = pill.GetComponentInChildren<TMP_Text>(true);
            Bind(label, locKey);
        }

        // 分区 Box 标题绑定: PeakWindowStyle.CreateBox 的标题子节点名为 "Heading"
        public static void BindBoxHeading(RectTransform box, string locKey)
        {
            if (box == null) return;
            Transform heading = box.Find("Heading");
            if (heading == null) return;
            TMP_Text label = heading.GetComponent<TMP_Text>();
            Bind(label, locKey);
        }

        public static void UnbindAll(GameObjectScope scope)
        {
            if (scope == null || scope.Root == null) return;
            for (int i = _bindings.Count - 1; i >= 0; i--)
            {
                Binding b = _bindings[i];
                if (b.Text == null)
                {
                    _bindings.RemoveAt(i);
                    continue;
                }
                if (b.Text.transform.IsChildOf(scope.Root.transform))
                {
                    _bindings.RemoveAt(i);
                }
            }
        }

        public sealed class GameObjectScope
        {
            public UnityEngine.Transform Root;
            public GameObjectScope(UnityEngine.Transform root) { Root = root; }
        }

        private static void ApplyTo(TMP_Text text, string locKey, Func<object[]> args)
        {
            try
            {
                if (args != null)
                {
                    text.text = CsrLoc.T(locKey, args());
                }
                else
                {
                    text.text = CsrLoc.T(locKey);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 本地化刷新失败 (" + locKey + "): " + ex.Message);
            }
        }
    }
}
