using System;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 极简原生风格确认/提示弹窗 (原 PcsrMenuWindow 内嵌 PeakCsrPrompt 拆分为独立文件)
    public static class PeakCsrPrompt
    {
        private static GameObject _root;

        public static void Confirm(string message, Action onConfirm)
        {
            Show("PEAK CSR", message, new[]
            {
                new Option(CsrLoc.T("win.btn.confirm"), onConfirm),
                new Option(CsrLoc.T("win.btn.cancel"), null)
            });
        }

        public static void Info(string title, string message)
        {
            Show(title, message, new[] { new Option(CsrLoc.T("win.btn.ok"), null) });
        }

        private struct Option
        {
            public string Label;
            public Action Callback;
            public Option(string label, Action callback)
            {
                Label = label;
                Callback = callback;
            }
        }

        private static void Show(string title, string message, Option[] options)
        {
            Destroy();
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return;

            _root = new GameObject("PcsrPromptRoot", typeof(RectTransform));
            _root.transform.SetParent(canvas.transform, false);
            RectTransform rootRt = (RectTransform)_root.transform;
            rootRt.anchorMin = Vector2.zero;
            rootRt.anchorMax = Vector2.one;
            rootRt.offsetMin = Vector2.zero;
            rootRt.offsetMax = Vector2.zero;

            PeakWindowStyle.CreateBackdrop(rootRt, "Backdrop");

            const float w = 780f;
            const float h = 400f;
            PeakWindowStyle.WindowParts win = PeakWindowStyle.CreateWindow(
                rootRt, "PcsrPrompt", w, h, 74f, title, "");

            TextMeshProUGUI titleText = win.Title;
            if (titleText != null)
            {
                RectTransform titleRt = titleText.transform as RectTransform;
                if (titleRt != null)
                {
                    NativeUiFactory.Place(titleRt, 30f, 0f, w - 60f - 110f, 74f);
                }
                titleText.textWrappingMode = TextWrappingModes.NoWrap;
                titleText.overflowMode = TextOverflowModes.Overflow;
                titleText.alignment = TextAlignmentOptions.Left;
            }

            PeakWindowStyle.CreateScrollingText(
                win.Body, "Body", message, 21f, PeakWindowStyle.TextPrimary,
                26f, 16f, w - 52f, h - 74f - 100f, 2f, 6f);

            float btnW = 180f;
            float totalW = options.Length * btnW + (options.Length - 1) * 14f;
            float startX = (w - totalW) / 2f;
            for (int i = 0; i < options.Length; i++)
            {
                Option opt = options[i];
                Button btn = PeakWindowStyle.CreatePill(
                    win.Body, "PromptBtn" + i, opt.Label,
                    startX + i * (btnW + 14f), h - 74f - 88f, btnW, 46f, 19f);
                if (opt.Callback != null)
                {
                    Action cb = opt.Callback;
                    btn.onClick.AddListener(new UnityAction(() =>
                    {
                        Destroy();
                        cb();
                    }));
                }
                else
                {
                    btn.onClick.AddListener(new UnityAction(Destroy));
                }
            }
        }

        private static void Destroy()
        {
            if (_root != null)
            {
                UnityEngine.Object.Destroy(_root);
                _root = null;
            }
        }
    }
}
