using System;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakCSR
{
    // 通用控件构建辅助与文本截断 FitText (原 PcsrMenuWindow 私有静态工具收口)
    public static class UiKit
    {
        public static TextMeshProUGUI CreateText(
            Transform parent, string name, string text, float size, Color color,
            float x, float y, float w, float h, bool wrap = false)
        {
            TextMeshProUGUI label = PeakWindowStyle.CreateText(
                parent, name, text, size, color, TextAlignmentOptions.Left);
            float minHeight = size * 1.35f;
            NativeUiFactory.Place((RectTransform)label.transform, x, y, w, Mathf.Max(h, minHeight));
            label.textWrappingMode = wrap ? TextWrappingModes.Normal : TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.margin = Vector4.zero;
            return label;
        }

        public static TextMeshProUGUI CreateTextRight(
            Transform parent, string name, string text, float size, Color color,
            float x, float y, float w, float h)
        {
            TextMeshProUGUI label = PeakWindowStyle.CreateText(
                parent, name, text, size, color, TextAlignmentOptions.Right);
            float minHeight = size * 1.35f;
            NativeUiFactory.Place((RectTransform)label.transform, x, y, w, Mathf.Max(h, minHeight));
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.overflowMode = TextOverflowModes.Overflow;
            label.margin = Vector4.zero;
            return label;
        }

        // PeakWindowStyle.CreateInputField 内部把 contentType 写死为 IntegerNumber(2),
        // 导致所有输入框都拒收非数字。这里统一按用途重设, 并保持富文本开启:
        // TMP_InputField 靠富文本的 <u> 标签绘制光标与选区, 把 richText 关掉就会把标签当普通文字显示出来。
        public static void ConfigurePlainInput(TMP_InputField input, int characterLimit, bool alphanumericOnly)
        {
            if (input == null) return;

            input.characterLimit = characterLimit;
            input.contentType = alphanumericOnly
                ? TMP_InputField.ContentType.Alphanumeric
                : TMP_InputField.ContentType.Standard;
            input.characterValidation = alphanumericOnly
                ? TMP_InputField.CharacterValidation.Alphanumeric
                : TMP_InputField.CharacterValidation.None;
            input.lineType = TMP_InputField.LineType.SingleLine;

            if (input.textComponent != null)
            {
                // 必须保留富文本, 否则光标/选区的 <u> 标签会露出来
                input.textComponent.richText = true;
                input.textComponent.textWrappingMode = TextWrappingModes.NoWrap;
                input.textComponent.overflowMode = TextOverflowModes.Truncate;
            }
        }

        // 标题行右侧要放刷新按钮时, 把标题可用宽度收窄到按钮左侧, 防止长标题被压住
        public static void ShortenBoxHeading(RectTransform box, float boxW, float rightReserve)
        {
            if (box == null) return;
            Transform heading = box.Find("Heading");
            if (heading == null) return;
            RectTransform rt = heading as RectTransform;
            if (rt == null) return;
            // 与旧版一致: 标题起始于 BoxPad, 右侧也要留出 BoxPad 边距
            float w = boxW - 24f * 2f - rightReserve;
            if (w > 40f) rt.sizeDelta = new Vector2(w, rt.sizeDelta.y);
        }

        // 按目标控件的实际像素宽度截断文本并补省略号, 中文按两倍宽估算
        public static string FitText(TMP_Text target, string text, float reservePx)
        {
            if (target == null || string.IsNullOrEmpty(text)) return text ?? "";

            RectTransform rt = target.transform as RectTransform;
            float available = (rt != null ? rt.rect.width : 200f) - reservePx - 6f;
            if (available <= 0f) return text;

            float fontSize = target.fontSize > 0f ? target.fontSize : 16f;
            float asciiWidth = fontSize * 0.55f;

            float used = 0f;
            int cut = text.Length;
            for (int i = 0; i < text.Length; i++)
            {
                float w = text[i] > 0x2E80 ? asciiWidth * 2f : asciiWidth;
                if (used + w > available)
                {
                    cut = i;
                    break;
                }
                used += w;
            }
            if (cut >= text.Length) return text;
            return cut <= 1 ? "…" : text.Substring(0, cut - 1) + "…";
        }

        public static void SetActive(Component comp, bool active)
        {
            if (comp != null && comp.gameObject.activeSelf != active)
            {
                comp.gameObject.SetActive(active);
            }
        }

        // 面板刷新字段异常统一收口 (保留原 Safe 语义, 日志带上下文)
        public static void Safe(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 面板刷新字段异常: " + ex.Message);
            }
        }

        public static string SafeBiomeLabel(int levelIndex)
        {
            try
            {
                return RankedPeak.Services.MapCatalog.GetBiomeLabel(levelIndex) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // 确保存在可用 EventSystem (原 PcsrMenuWindow.EnsureEventSystem 迁移)
        public static void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;

            GameObject go = new GameObject("PeakCSR_EventSystem");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<UnityEngine.EventSystems.EventSystem>();

            System.Type moduleType = System.Type.GetType("UnityEngine.InputSystem.UI.InputSystemUIInputModule, Unity.InputSystem");
            if (moduleType != null)
            {
                try
                {
                    go.AddComponent(moduleType);
                    return;
                }
                catch (System.Exception ex)
                {
                    PcsrLog.LogWarning("[PeakCSR] 挂载 InputSystemUIInputModule 失败: " + ex.Message);
                }
            }

            try
            {
                go.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            }
            catch (System.Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 无可用输入模块, 窗口可能无法点击: " + ex.Message);
            }
        }

        // 标题栏按钮统一半透明白 (浅蓝标题栏上的可读性)
        public static void TintOnBar(Button button)
        {
            Image img = button.GetComponent<Image>();
            if (img != null) img.color = new Color(1f, 1f, 1f, 0.22f);
        }
    }
}
