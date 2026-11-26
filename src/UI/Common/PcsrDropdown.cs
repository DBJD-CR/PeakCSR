using System;
using System.Collections.Generic;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 轻量下拉框
    // RANKED 没有现成的 TMP_Dropdown 可复用, 这里用原生 UI 手搓一个:
    // 闭合态就是一个 pill 按钮, 展开时在按钮下方铺一层列表, 点选项回填并收起。
    // 不用 TMP_Dropdown: 它要求完整的 Template/Viewport/Content 层级, 还自带一层
    // 全屏 Block 遮罩, 在这种自绘窗口里容易和 ESC 关闭、点击穿透互相打架。
    //
    // 列表超过 MaxListHeight 时在内部滚动 (RectMask2D 裁剪 + 滚轮),
    // 否则国别这类几十项的长列表会一路铺到屏幕外面去。
    public class PcsrDropdown : MonoBehaviour
    {
        private const float RowHeight = 34f;
        private const float RowGap = 2f;
        // 列表展开的最大高度: 再多就滚动, 保证上下翻转后仍放得进 1080 画布
        private const float MaxListHeight = 452f;
        // 列表内边距 (给圆角描边留出空间)
        private const float ListPad = 4f;

        private RectTransform _buttonRt;
        private TextMeshProUGUI _label;
        private RectTransform _listRoot;
        private RectTransform _viewport;
        private RectTransform _content;
        private readonly List<Button> _rows = new List<Button>();
        private readonly List<TextMeshProUGUI> _rowLabels = new List<TextMeshProUGUI>();

        private string[] _options = new string[0];
        private int _value = -1;
        private bool _open;
        // 已滚过的像素: 越大表示内容越往上移
        private float _scrollY;

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

            // 视口: RectMask2D 裁剪, 不带 Image, 零额外材质
            GameObject vpGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            vpGo.transform.SetParent(_listRoot, false);
            _viewport = (RectTransform)vpGo.transform;
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = new Vector2(ListPad, ListPad);
            _viewport.offsetMax = new Vector2(-ListPad, -ListPad);

            // 内容层: 靠 anchoredPosition.y 滚动, 高度按真实行数算
            GameObject contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(_viewport, false);
            _content = (RectTransform)contentGo.transform;
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.anchoredPosition = Vector2.zero;

            BuildRows(this, fontSize);
            SetValue(initialValue, false);
            _listRoot.gameObject.SetActive(false);
        }

        private static void BuildRows(PcsrDropdown dd, float fontSize)
        {
            // 选项变化时整体重建行, 先清掉旧行
            for (int i = 0; i < dd._rows.Count; i++)
            {
                if (dd._rows[i] != null) UnityEngine.Object.Destroy(dd._rows[i].gameObject);
            }
            dd._rows.Clear();
            dd._rowLabels.Clear();

            for (int i = 0; i < dd._options.Length; i++)
            {
                int captured = i;
                GameObject rowGo = new GameObject("Row" + i, typeof(RectTransform), typeof(Image), typeof(Button));
                rowGo.transform.SetParent(dd._content, false);

                RectTransform rowRt = (RectTransform)rowGo.transform;
                rowRt.anchorMin = new Vector2(0f, 1f);
                rowRt.anchorMax = new Vector2(1f, 1f);
                rowRt.pivot = new Vector2(0f, 1f);
                rowRt.anchoredPosition = new Vector2(0f, -(i * (RowHeight + RowGap)));
                rowRt.sizeDelta = new Vector2(0f, RowHeight);

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

            if (dd._content != null)
            {
                float h = dd.OptionsContentHeight;
                dd._content.sizeDelta = new Vector2(0f, h);
            }
        }

        private float Stride { get { return RowHeight + RowGap; } }

        // 最后一行没有 gap, 所以要减掉一个 RowGap
        private float OptionsContentHeight
        {
            get
            {
                if (_options.Length == 0) return 0f;
                return _options.Length * RowHeight + (_options.Length - 1) * RowGap;
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
            // 每次展开都回到当前选中项附近, 长列表不至于总从顶部开始翻
            _scrollY = SelectedScrollTarget();
            ApplyLayout();
        }

        public void Close()
        {
            _open = false;
            if (_listRoot != null) _listRoot.gameObject.SetActive(false);
        }

        // 让选中项尽量居中; 未选中时停在顶部
        private float SelectedScrollTarget()
        {
            float viewH = _viewport != null ? _viewport.rect.height : 0f;
            if (viewH <= 0f || _value < 0) return 0f;
            float rowCenter = _value * Stride + RowHeight * 0.5f;
            return Mathf.Clamp(rowCenter - viewH * 0.5f, 0f, MaxScroll);
        }

        private float MaxScroll
        {
            get
            {
                float viewH = _viewport != null ? _viewport.rect.height : 0f;
                return Mathf.Max(0f, OptionsContentHeight - viewH);
            }
        }

        // 把展开层对齐到按钮下方; 下方放不下就翻到按钮上方展开
        private void ApplyLayout()
        {
            if (_buttonRt == null || _listRoot == null || _viewport == null) return;

            Vector2 size = _buttonRt.rect.size;
            Vector3[] corners = new Vector3[4];
            _buttonRt.GetWorldCorners(corners);

            // 高度按内容算, 但封顶在 MaxListHeight, 超出的部分靠内部滚动看
            float contentH = OptionsContentHeight;
            float listH = Mathf.Min(contentH + ListPad * 2f, MaxListHeight);
            float minW = Mathf.Max(240f, size.x);

            // corners: 0=左下 1=左上 2=右上 3=右下
            Vector3 bl = corners[0];
            Vector3 tl = corners[1];

            float spaceBelow = GetCanvasBottomY() - bl.y;
            bool openUp = spaceBelow < listH;

            _listRoot.sizeDelta = new Vector2(minW, listH);
            _listRoot.anchorMin = new Vector2(0.5f, 0.5f);
            _listRoot.anchorMax = new Vector2(0.5f, 0.5f);

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

            // 视口尺寸要等 listRoot 排好才准, 这里显式刷一次
            Canvas.ForceUpdateCanvases();

            _content.sizeDelta = new Vector2(0f, contentH);
            _scrollY = Mathf.Clamp(_scrollY, 0f, MaxScroll);
            _content.anchoredPosition = new Vector2(0f, _scrollY);
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
                return;
            }

            // 滚轮只在内容超长时生效, 且必须指针在列表上
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) < 0.01f) return;
            if (MaxScroll <= 0f) return;
            if (!IsInside(_viewport, Input.mousePosition)) return;

            // 滚轮向上 = 内容下移 = 看到更早的行, 因此取负
            _scrollY = Mathf.Clamp(_scrollY - wheel * Stride * 2f, 0f, MaxScroll);
            if (_content != null) _content.anchoredPosition = new Vector2(0f, _scrollY);
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
