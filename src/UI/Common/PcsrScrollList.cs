using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using RankedPeak.Ui.Native;

namespace PeakCSR
{
    // 轻量滚动列表 (行池复用)
    //
    // 方案: RectMask2D (无材质) + 行池 + 滚轮事件
    public sealed class PcsrScrollList : MonoBehaviour
    {
        // 行高与间距
        private float _rowHeight = 26f;
        private float _rowGap = 0f;
        // 视口高度
        private float _viewHeight = 100f;

        private RectTransform _viewport;
        private RectTransform _content;
        private RectTransform _host;

        private readonly List<RectTransform> _rows = new List<RectTransform>();
        private int _rowCount;
        private int _dataCount;
        // 已滚过的像素: 越大表示内容越往上移 (看到越靠后的数据)
        private float _scrollY;

        // 行内容填充回调: (行索引, 行 RectTransform), 由使用方负责建行内控件
        private Action<int, RectTransform> _onBindRow;
        // 点击回调: 传入数据项索引
        private Action<int> _onClickRow;
        // 可视窗口变化回调: 使用方据此重绘可见行内容
        private Action _onScrollChanged;

        public int RowCount { get { return _rowCount; } }

        // 在 parent 的 (x, y) 处创建列表; 行高/间距在创建时固定
        public static PcsrScrollList Create(
            RectTransform parent, string name, float x, float y, float width, float height,
            float rowHeight, int rowCount, float rowGap = 0f)
        {
            GameObject go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            RectTransform rt = (RectTransform)go.transform;
            NativeUiFactory.Place(rt, x, y, width, height);

            PcsrScrollList list = go.AddComponent<PcsrScrollList>();
            list.Build(rt, width, height, rowHeight, rowCount, rowGap);
            return list;
        }

        private void Build(RectTransform host, float width, float height, float rowHeight, int rowCount, float rowGap)
        {
            _host = host;
            _rowHeight = rowHeight;
            _rowGap = rowGap;
            // 行池按"视口能装下多少行 + 1"兜底
            int needed = Mathf.CeilToInt(height / Mathf.Max(0.01f, rowHeight + rowGap)) + 1;
            _rowCount = Mathf.Max(1, Mathf.Max(rowCount, needed));
            _viewHeight = height;

            // 视口: RectMask2D 裁剪, 不带任何 Image, 零额外材质
            GameObject vpGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
            vpGo.transform.SetParent(host, false);
            _viewport = (RectTransform)vpGo.transform;
            _viewport.anchorMin = Vector2.zero;
            _viewport.anchorMax = Vector2.one;
            _viewport.offsetMin = Vector2.zero;
            _viewport.offsetMax = Vector2.zero;

            // 内容层: 靠 anchoredPosition.y 滚动
            GameObject contentGo = new GameObject("Content", typeof(RectTransform));
            contentGo.transform.SetParent(_viewport, false);
            _content = (RectTransform)contentGo.transform;
            _content.anchorMin = new Vector2(0f, 1f);
            _content.anchorMax = new Vector2(1f, 1f);
            _content.pivot = new Vector2(0f, 1f);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = new Vector2(0f, 0f);

            // 行池: 一次性建满可见行数, 数据变化时只改内容与位置
            for (int i = 0; i < _rowCount; i++)
            {
                // 必须带一个 Graphic, 否则 GraphicRaycaster 不会把这行算进射线检测, Button 永远收不到点击
                GameObject rowGo = new GameObject("Row" + i, typeof(RectTransform), typeof(Image), typeof(Button));
                rowGo.transform.SetParent(_content, false);
                RectTransform rowRt = (RectTransform)rowGo.transform;
                rowRt.anchorMin = new Vector2(0f, 1f);
                rowRt.anchorMax = new Vector2(1f, 1f);
                rowRt.pivot = new Vector2(0f, 1f);
                rowRt.sizeDelta = new Vector2(0f, _rowHeight);

                Image hit = rowGo.GetComponent<Image>();
                hit.color = new Color(1f, 1f, 1f, 0f);
                hit.raycastTarget = true;

                Button btn = rowGo.GetComponent<Button>();
                btn.targetGraphic = hit;
                btn.transition = Selectable.Transition.None;
                int captured = i;
                btn.onClick.AddListener(delegate { OnRowClicked(captured); });
                _rows.Add(rowRt);
            }

        }

        // 设置每一行的控件构建方式: 传入的是"行池索引", 每个池行只建一次
        public void SetRowBinder(Action<int, RectTransform> binder)
        {
            _onBindRow = binder;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (_onBindRow != null) _onBindRow(i, _rows[i]);
            }
        }

        public void SetClickHandler(Action<int> onClick)
        {
            _onClickRow = onClick;
        }

        // 数据条数变化: 只做显隐与滚动的钳制, 不重建行
        public void SetItemCount(int count)
        {
            _dataCount = Mathf.Max(0, count);
            ClampScroll();
            ApplyLayout();
        }

        public RectTransform GetRow(int index)
        {
            if (index < 0 || index >= _rows.Count) return null;
            return _rows[index];
        }

        private void Update()
        {
            // 数据超过可视行数时才允许滚动
            if (_dataCount <= _rowCount) return;
            float wheel = Input.mouseScrollDelta.y;
            if (Mathf.Abs(wheel) < 0.01f) return;
            if (!PointerOverList()) return;

            int before = FirstDataIndex;
            // 滚轮向上 = 回到更早的行, 因此取负
            float step = (_rowHeight + _rowGap) * 2f;
            ScrollBy(-wheel * step);
            // 虚拟起点变了就必须通知使用方重绘内容, 否则池行还是旧数据
            if (FirstDataIndex != before) NotifyScrollChanged();
        }

        // 使用方注册"可视窗口变化"回调 (滚轮滚动、外部 ScrollBy 都会触发)
        public void SetScrollChangedHandler(Action onChanged)
        {
            _onScrollChanged = onChanged;
        }

        private void NotifyScrollChanged()
        {
            if (_onScrollChanged != null) _onScrollChanged();
        }

        // 指针是否在本列表范围内: 避免多个列表同时响应同一个滚轮事件
        private bool PointerOverList()
        {
            if (_host == null) return false;
            Vector2 mouse = Input.mousePosition;
            return RectTransformUtility.RectangleContainsScreenPoint(_host, mouse, null);
        }

        public void ScrollBy(float delta)
        {
            int before = FirstDataIndex;
            _scrollY += delta;
            ClampScroll();
            ApplyLayout();
            if (FirstDataIndex != before) NotifyScrollChanged();
        }

        // 当前滚动偏移量 (像素), 供使用方同步虚拟起点
        public float ScrollOffset { get { return _scrollY; } }

        // 每行的步进 (行高 + 间距)
        public float RowStride { get { return _rowHeight + _rowGap; } }

        // 虚拟起点: 当前视口第一行对应的数据索引
        public int VisibleStartIndex { get { return FirstDataIndex; } }

        private float Stride
        {
            get { return _rowHeight + _rowGap; }
        }

        // 内容总高按"真实数据条数"算, 而不是行池大小
        private float ContentHeight
        {
            get { return _dataCount * Stride; }
        }

        // 当前视口第一行对应的数据索引 (虚拟滚动起点)
        private int FirstDataIndex
        {
            get { return Stride <= 0f ? 0 : Mathf.FloorToInt(_scrollY / Stride); }
        }

        private float MaxScroll
        {
            get { return Mathf.Max(0f, ContentHeight - _viewHeight); }
        }

        private void ClampScroll()
        {
            _scrollY = Mathf.Clamp(_scrollY, 0f, MaxScroll);
        }

        private void ApplyLayout()
        {
            if (_content == null) return;

            int first = FirstDataIndex;
            // 平滑位移: 内容整体按 _scrollY 上移, 池行按"绝对行号"定位。
            // 不能用 first * Stride 做整行吸附, 那样滚动量不是行高整数倍时
            // 首行会被裁掉一截, 观感是跳动而不是连续滚动。
            _content.anchoredPosition = new Vector2(0f, _scrollY);

            for (int i = 0; i < _rows.Count; i++)
            {
                RectTransform row = _rows[i];
                if (row == null) continue;
                int dataIndex = first + i;
                bool visible = dataIndex < _dataCount;
                if (row.gameObject.activeSelf != visible) row.gameObject.SetActive(visible);
                // 池行放在"它代表的那条数据"的绝对位置, 由 content 的整体偏移带动
                row.anchoredPosition = new Vector2(0f, -(dataIndex * Stride));
            }
        }

        private void OnRowClicked(int rowIndex)
        {
            int dataIndex = FirstDataIndex + rowIndex;
            if (dataIndex < 0 || dataIndex >= _dataCount) return;
            if (_onClickRow != null) _onClickRow(dataIndex);
        }
    }
}
