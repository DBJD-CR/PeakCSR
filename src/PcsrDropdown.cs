using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using RankedPeak.Ui.Native;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 轻量下拉框
    // RANKED 没有现成的 TMP_Dropdown 可复用, 这里用原生 UI 手搓一个:
    // 闭合态就是一个 pill 按钮, 展开时在按钮下方铺一层列表, 点选项回填并收起。
    // 不用 TMP_Dropdown: 它要求完整的 Template/Viewport/Content 层级, 还自带一层
    // 全屏 Block 遮罩, 在这种自绘窗口里容易和 ESC 关闭、点击穿透互相打架。
    public class PcsrDropdown : MonoBehaviour
    {
        private const float RowHeight = 34f;
        private const float RowGap = 2f;

        private RectTransform _buttonRt;
        private TextMeshProUGUI _label;
        private RectTransform _listRoot;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<TextMeshProUGUI> _rowLabels = new List<TextMeshProUGUI>();

        private string[] _options = new string[0];
        private int _value = -1;
        private bool _open;

        public Action<int> OnValueChanged;

        public int Value { get { return _value; } }

        public string ValueText
        {
            get
            {
                if (_options == null || _value < 0 || _value >= _options.Length) return "";
                return _options[_value] ?? "";
            }
        }

        public bool IsOpen { get { return _open; } }

        // 在 parent 的 (x, y) 处建一个宽 width、高 height 的下拉框
        public static PcsrDropdown Create(
            RectTransform parent, string name, float x, float y, float width, float height,
            string[] options, int initialValue, float fontSize = 17f)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = (RectTransform)go.transform;
            NativeUiFactory.Place(rt, x, y, width, height);

            PcsrDropdown dd = go.AddComponent<PcsrDropdown>();
            dd.Build(options, initialValue, fontSize);
            return dd;
        }

        private void Build(string[] options, int initialValue, float fontSize)
        {
            _options = options ?? new string[0];

            // 闭合态的按钮本体
            GameObject btnGo = new GameObject("Field", typeof(RectTransform), typeof(Image), typeof(Button));
            btnGo.transform.SetParent(transform, false);
            _buttonRt = (RectTransform)btnGo.transform;
            _buttonRt.anchorMin = Vector2.zero;
            _buttonRt.anchorMax = Vector2.one;
            _buttonRt.offsetMin = Vector2.zero;
            _buttonRt.offsetMax = Vector2.zero;

            Image bg = btnGo.GetComponent<Image>();
            bg.color = PeakWindowStyle.PillIdle;
            Button btn = btnGo.GetComponent<Button>();
            btn.targetGraphic = bg;
            btn.transition = Selectable.Transition.None;
            btn.onClick.AddListener(new UnityAction(Toggle));

            _label = PeakWindowStyle.CreateText(btnGo.transform, "Label", "", fontSize,
                PeakWindowStyle.TextOnBar, TextAlignmentOptions.Left);
            RectTransform labelRt = (RectTransform)_label.transform;
            labelRt.anchorMin = Vector2.zero;
            labelRt.anchorMax = Vector2.one;
            labelRt.offsetMin = new Vector2(16f, 0f);
            labelRt.offsetMax = new Vector2(-30f, 0f);
            _label.textWrappingMode = TextWrappingModes.NoWrap;
            _label.overflowMode = TextOverflowModes.Ellipsis;

            // 右侧小三角
            TextMeshProUGUI arrow = PeakWindowStyle.CreateText(
                btnGo.transform, "Arrow", "▼", fontSize - 3f, PeakWindowStyle.TextOnBar, TextAlignmentOptions.Right);
            RectTransform arrowRt = (RectTransform)arrow.transform;
            arrowRt.anchorMin = Vector2.zero;
            arrowRt.anchorMax = Vector2.one;
            arrowRt.offsetMin = new Vector2(0f, 0f);
            arrowRt.offsetMax = new Vector2(-14f, 0f);

            // 展开层: 建在 Canvas 根上, 避免被 box 的绘制顺序或裁剪影响
            GameObject listGo = new GameObject(name + "_List", typeof(RectTransform), typeof(Image));
            Canvas canvas = transform.GetComponentInParent<Canvas>();
            Transform listParent = canvas != null ? canvas.transform : transform.root;
            listGo.transform.SetParent(listParent, false);
            listGo.transform.SetAsLastSibling();
            _listRoot = (RectTransform)listGo.transform;

            Image listBg = listGo.GetComponent<Image>();
            listBg.color = PeakWindowStyle.WindowFill;
            listBg.raycastTarget = true;

            BuildRows(this, fontSize);
            SetValue(initialValue, false);
            _listRoot.gameObject.SetActive(false);
        }

        private static void BuildRows(PcsrDropdown dd, float fontSize)
        {
            dd._rows.Clear();
            dd._rowLabels.Clear();
            for (int i = 0; i < dd._options.Length; i++)
            {
                int captured = i;
                GameObject rowGo = new GameObject("Row" + i, typeof(RectTransform), typeof(Image), typeof(Button));
                rowGo.transform.SetParent(dd._listRoot, false);

                Image rowBg = rowGo.GetComponent<Image>();
                rowBg.color = PeakWindowStyle.BoxFill;
                Button rowBtn = rowGo.GetComponent<Button>();
                rowBtn.targetGraphic = rowBg;
                rowBtn.transition = Selectable.Transition.None;
                rowBtn.onClick.AddListener(new UnityAction(() => dd.Pick(captured)));

                TextMeshProUGUI rowLabel = PeakWindowStyle.CreateText(rowGo.transform, "Label",
                    dd._options[i] ?? "", fontSize, PeakWindowStyle.TextPrimary, TextAlignmentOptions.Left);
                RectTransform rowLabelRt = (RectTransform)rowLabel.transform;
                rowLabelRt.anchorMin = Vector2.zero;
                rowLabelRt.anchorMax = Vector2.one;
                rowLabelRt.offsetMin = new Vector2(16f, 0f);
                rowLabelRt.offsetMax = new Vector2(-12f, 0f);
                rowLabel.textWrappingMode = TextWrappingModes.NoWrap;
                rowLabel.overflowMode = TextOverflowModes.Ellipsis;

                dd._rows.Add(rowBtn);
                dd._rowLabels.Add(rowLabel);
            }
        }

        public void SetOptions(string[] options)
        {
            bool changed = options == null || _options == null || options.Length != _options.Length;
            if (!changed)
            {
                for (int i = 0; i < options.Length; i++)
                {
                    if (!string.Equals(options[i], _options[i], StringComparison.Ordinal))
                    {
                        changed = true;
                        break;
                    }
                }
            }
            if (!changed) return;

            _options = options ?? new string[0];
            BuildRows(this, _label != null ? _label.fontSize : 17f);
            ApplyLayout();
            SetValue(_value, false);
        }

        public void SetValue(int value, bool notify)
        {
            int clamped = _options.Length == 0 ? -1 : Mathf.Clamp(value, -1, _options.Length - 1);
            bool changed = clamped != _value;
            _value = clamped;

            if (_label != null)
            {
                _label.text = ValueText;
                // 有选中项才用高亮色, 空值保持灰一点, 视觉上像"未选择"
                _label.color = _value >= 0 ? PeakWindowStyle.TextOnBar : new Color(1f, 1f, 1f, 0.65f);
            }
            for (int i = 0; i < _rowLabels.Count; i++)
            {
                _rowLabels[i].color = i == _value ? PeakWindowStyle.Heading : PeakWindowStyle.TextPrimary;
                _rowLabels[i].fontStyle = i == _value ? FontStyles.Bold : FontStyles.Normal;
            }

            if (notify && changed && OnValueChanged != null) OnValueChanged(_value);
        }

        private void Pick(int index)
        {
            SetValue(index, true);
            Close();
        }

        private void Toggle()
        {
            if (_open)
            {
                Close();
                return;
            }
            Open();
        }

        public void Open()
        {
            if (_listRoot == null || _options.Length == 0) return;
            _open = true;
            _listRoot.gameObject.SetActive(true);
            _listRoot.SetAsLastSibling();
            // 按钮可能刚建好还没跑排布, 先强制刷新一次, 否则 GetWorldCorners 会拿到零尺寸
            Canvas.ForceUpdateCanvases();
            ApplyLayout();
        }

        public void Close()
        {
            _open = false;
            if (_listRoot != null) _listRoot.gameObject.SetActive(false);
        }

        // 把展开层对齐到按钮下方; 下方放不下就翻到按钮上方展开
        private void ApplyLayout()
        {
            if (_buttonRt == null || _listRoot == null) return;

            Vector2 size = _buttonRt.rect.size;
            Vector3[] corners = new Vector3[4];
            _buttonRt.GetWorldCorners(corners);

            float rowH = RowHeight;
            float totalH = _options.Length * rowH + (_options.Length - 1) * RowGap + 8f;
            float minW = Mathf.Max(240f, size.x);

            _listRoot.sizeDelta = new Vector2(minW, totalH);
            _listRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _listRoot.anchorMax = new Vector2(0.5f, 0.5f);

            // corners: 0=左下 1=左上 2=右上 3=右下
            Vector3 bl = corners[0];
            Vector3 tl = corners[1];

            float spaceBelow = GetCanvasBottomY() - bl.y;
            bool openUp = spaceBelow < totalH;

            if (openUp)
            {
                // 以底边对齐按钮顶边, 往上长
                _listRoot.pivot = new Vector2(0f, 0f);
                _listRoot.position = new Vector3(bl.x, tl.y, bl.z);
            }
            else
            {
                _listRoot.pivot = new Vector2(0f, 1f);
                _listRoot.position = new Vector3(bl.x, tl.y, bl.z);
            }

            for (int i = 0; i < _rows.Count; i++)
            {
                RectTransform rt = (RectTransform)_rows[i].transform;
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0f, 1f);
                rt.anchoredPosition = new Vector2(4f, -(4f + i * (rowH + RowGap)));
                rt.sizeDelta = new Vector2(minW - 8f, rowH);
            }
        }

        // 画布底边的世界 y (画布是 Screen Space Overlay, 世界坐标即屏幕像素)
        private float GetCanvasBottomY()
        {
            Canvas canvas = GetComponentInParent<Canvas>();
            if (canvas == null) return 0f;
            RectTransform canvasRt = canvas.transform as RectTransform;
            if (canvasRt == null) return 0f;
            Vector3[] corners = new Vector3[4];
            canvasRt.GetWorldCorners(corners);
            return corners[0].y;
        }

        private void Update()
        {
            // 展开时点一下别处就收起, 不用额外铺全屏遮罩
            if (!_open) return;
            if (Input.GetMouseButtonDown(0))
            {
                Vector2 mouse = Input.mousePosition;
                if (IsInside(_listRoot, mouse) || IsInside(_buttonRt, mouse)) return;
                Close();
            }
        }

        private static bool IsInside(RectTransform rt, Vector2 screenPoint)
        {
            if (rt == null) return false;
            return RectTransformUtility.RectangleContainsScreenPoint(rt, screenPoint, null);
        }

        private void OnDisable()
        {
            Close();
        }

        // 展开层挂在 Canvas 根上而不是本物件下, 必须手动销毁, 否则父窗口重建时会留下孤儿列表
        private void OnDestroy()
        {
            if (_listRoot != null)
            {
                UnityEngine.Object.Destroy(_listRoot.gameObject);
                _listRoot = null;
            }
        }
    }
}
