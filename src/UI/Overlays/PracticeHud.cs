using PeakCSR.Practice;
using RankedPeak.Ui.Native;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace PeakCSR
{
    // 游戏内练习 HUD
    //
    // 只承担一件事: 让玩家随时看到"后台已匹配多久 + 当前探针状态"。
    // 爬图用时由 RANKED 自带的计时面板负责, 这里不重复显示。
    //
    // 位置: 紧贴 RANKED 左上角计时卡片正下方, 与它同宽同左缘对齐。
    // RANKED 那张卡高度随分段数量变化, 所以每帧低频回读它的实际高度再定位,
    // 保证永远不叠在一起。非练习态整块隐藏, 不影响正常对局。
    public sealed class PracticeHud : MonoBehaviour
    {
        private const float CardWidth = 340f;
        // 卡高按"标题 + 赛制 + 状态 + 时长标签 + 时长 + 脚注"六行留足
        private const float CardHeight = 112f;
        private const float RefreshInterval = 0.1f;
        private const float LayoutInterval = 0.4f;

        // 与 RANKED 计时面板保持同一基准: x=10 起, 间隔 8px
        private const float BaseLeft = 10f;
        private const float BaseTop = 10f;
        private const float GapBelowTimer = 8f;
        private const string TimerPanelName = "RankedPeak_TimerPanel";

        // 回读不到 RANKED 计时卡高度时的保守估计
        private const float FallbackTimerHeight = 120f;

        private static readonly Color CardBg = new Color(0.05f, 0.06f, 0.08f, 0.88f);
        private static readonly Color CardEdge = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color Accent = new Color(0.45f, 0.72f, 1f);
        private static readonly Color Gold = new Color(1f, 0.82f, 0.25f);
        private static readonly Color TextPrimary = new Color(0.95f, 0.96f, 0.98f);
        private static readonly Color TextMuted = new Color(0.62f, 0.66f, 0.72f);

        private static PracticeHud _instance;

        private RectTransform _panel;
        private Image _accentBar;
        private Image _dot;
        private TMP_Text _titleLabel;
        private TMP_Text _rulesetLabel;
        private TMP_Text _statusLabel;
        private TMP_Text _clockCaption;
        private TMP_Text _clockLabel;
        private TMP_Text _captionLabel;
        private float _nextRefresh;
        private float _nextLayout;
        private bool _built;

        // 上次成功读到的计时卡高度
        private float _lastTimerHeight = FallbackTimerHeight;

        // 命中阶段的对手名缓存: 进入 Switching 后 StatusMessage 已换文案, 这里留住展示
        private string _lastFoundHost = "";

        public static void Ensure()
        {
            if (_instance != null) return;
            GameObject go = new GameObject("PeakCSR_PracticeHud");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<PracticeHud>();
        }

        private void Awake()
        {
            _instance = this;
        }

        private void OnDestroy()
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        private void Update()
        {
            PracticeCoordinator pc = PracticeCoordinator.Instance;
            // 匹配相关的全部状态从启动到交棒全程可见
            bool visible = pc != null
                && (pc.Phase == PracticePhase.Preparing
                    || pc.Phase == PracticePhase.Running
                    || pc.Phase == PracticePhase.MatchFound
                    || pc.Phase == PracticePhase.Switching);

            if (!visible)
            {
                if (_built && _panel != null) _panel.gameObject.SetActive(false);
                return;
            }

            // 加载流程每帧自保
            NativeUiFactory.EnsureCanvasAlive();

            // 场景切换后旧画布会被销毁, 需要重新构建
            if (_built && (_panel == null || _panel.parent == null))
            {
                ResetRefs();
            }
            if (!_built && !Build()) return;

            if (_panel != null) _panel.gameObject.SetActive(true);

            float now = Time.unscaledTime;
            // 位置跟随 RANKED 面板高度, 用低频避免每帧 GameObject.Find
            if (now >= _nextLayout)
            {
                _nextLayout = now + LayoutInterval;
                ApplyFollowPosition();
            }

            if (now < _nextRefresh) return;
            _nextRefresh = now + RefreshInterval;
            UpdateContent(pc);
        }

        private void ResetRefs()
        {
            _built = false;
            _panel = null;
            _accentBar = null;
            _dot = null;
            _titleLabel = null;
            _rulesetLabel = null;
            _statusLabel = null;
            _clockCaption = null;
            _clockLabel = null;
            _captionLabel = null;
        }

        // 贴在 RANKED 计时卡下方; 读不到高度时沿用上次的值
        private void ApplyFollowPosition()
        {
            if (_panel == null) return;

            float timerHeight = 0f;
            try
            {
                GameObject timerGo = GameObject.Find(TimerPanelName);
                if (timerGo != null && timerGo.activeInHierarchy)
                {
                    RectTransform timerRt = timerGo.transform as RectTransform;
                    if (timerRt != null) timerHeight = timerRt.sizeDelta.y;
                }
            }
            catch
            {
            }

            // RANKED 计时卡在非计时态是未激活的, 这时沿用上次读到的高度
            if (timerHeight > 1f) _lastTimerHeight = timerHeight;
            float top = BaseTop + _lastTimerHeight + GapBelowTimer;
            _panel.anchoredPosition = new Vector2(BaseLeft, -top);
        }

        private void UpdateContent(PracticeCoordinator pc)
        {
            bool alarm = pc.Phase == PracticePhase.MatchFound || pc.Phase == PracticePhase.Switching;

            if (pc.Phase == PracticePhase.MatchFound)
            {
                string host = pc.FoundHostName;
                if (!string.IsNullOrEmpty(host)) _lastFoundHost = host;
            }
            else if (pc.Phase == PracticePhase.Running || pc.Phase == PracticePhase.Preparing)
            {
                _lastFoundHost = "";
            }

            // 第一行: 赛制 (练习期间唯一能被玩家记住的配置)
            if (_rulesetLabel != null)
            {
                _rulesetLabel.text = CsrLoc.T("practice.hud.ruleset", pc.Ruleset.DisplayName ?? "");
            }

            if (_statusLabel != null)
            {
                _statusLabel.text = ResolvePhaseText(pc);
                _statusLabel.color = alarm ? Gold : TextMuted;
            }

            if (_clockCaption != null)
            {
                _clockCaption.text = CsrLoc.T("practice.hud.clock_caption");
                _clockCaption.color = TextMuted;
            }

            if (_clockLabel != null)
            {
                _clockLabel.text = CsrFormat.FormatElapsed(pc.QueueElapsedSeconds);
                _clockLabel.color = alarm ? Gold : TextPrimary;
            }

            if (_captionLabel != null)
            {
                _captionLabel.text = CsrLoc.T(alarm ? "practice.hud.matched_found" : "practice.hud.note");
                _captionLabel.color = TextMuted;
            }

            if (_accentBar != null) _accentBar.color = alarm ? Gold : Accent;

            // 状态点: 搜索态呼吸闪烁, 命中/切换常亮强调
            if (_dot != null)
            {
                float alpha = alarm ? 1f : 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Time.unscaledTime * 2.2f));
                Color c = alarm ? Gold : Accent;
                c.a = alpha;
                _dot.color = c;
            }
        }

        // 阶段文案: 编排器写的实时进度优先 (含探针/校验/重连细节)
        private string ResolvePhaseText(PracticeCoordinator pc)
        {
            string live = pc.StatusMessage;
            if (!string.IsNullOrEmpty(live)) return live;

            switch (pc.Phase)
            {
                case PracticePhase.Preparing:
                    return CsrLoc.T("practice.hud.phase.preparing");
                case PracticePhase.MatchFound:
                    // 命中时把对手名与倒计时一起带出来, 这是玩家最需要的信息
                    return CsrLoc.T("practice.hud.found_host",
                        _lastFoundHost.Length > 0 ? _lastFoundHost : CsrLoc.T("practice.hud.phase.match_found"));
                case PracticePhase.Switching:
                    return CsrLoc.T("practice.hud.phase.switching");
                default:
                    return CsrLoc.T("practice.hud.phase.running");
            }
        }

        private bool Build()
        {
            // 重建逻辑必须在 null 检查之前: EnsureCanvasAlive 的意义就是"没了就重建"
            NativeUiFactory.EnsureCanvasAlive();
            Canvas canvas = NativeUiFactory.GetCanvas();
            if (canvas == null) return false;

            GameObject rootGo = new GameObject("PracticeHudRoot", typeof(RectTransform));
            rootGo.transform.SetParent(canvas.transform, false);
            _panel = (RectTransform)rootGo.transform;
            // 与 RANKED 面板同一锚点体系: 左上角 (0,1), 左上 pivot
            _panel.anchorMin = new Vector2(0f, 1f);
            _panel.anchorMax = new Vector2(0f, 1f);
            _panel.pivot = new Vector2(0f, 1f);
            _panel.sizeDelta = new Vector2(CardWidth, CardHeight);
            _panel.anchoredPosition = new Vector2(BaseLeft, -(BaseTop + FallbackTimerHeight + GapBelowTimer));

            // 描边底 + 实心卡片: 一层比一层小 1px, 形成细边
            Image edge = PeakWindowStyle.CreateSurface(_panel, "Edge", CardEdge, 10);
            NativeUiFactory.Place((RectTransform)edge.transform, 0f, 0f, CardWidth, CardHeight);
            Image fill = PeakWindowStyle.CreateSurface((RectTransform)edge.transform, "Fill", CardBg, 10);
            NativeUiFactory.Place((RectTransform)fill.transform, 1f, 1f, CardWidth - 2f, CardHeight - 2f);

            RectTransform card = (RectTransform)edge.transform;

            // 左侧强调竖条
            _accentBar = PeakWindowStyle.CreateSurface(card, "Accent", Accent, 3);
            NativeUiFactory.Place((RectTransform)_accentBar.transform, 9f, 12f, 4f, CardHeight - 26f);

            // 状态呼吸点
            _dot = PeakWindowStyle.CreateSurface(card, "Dot", Accent, 5);
            NativeUiFactory.Place((RectTransform)_dot.transform, 23f, 18f, 8f, 8f);

            _titleLabel = UiKit.CreateText(card, "LblTitle", CsrLoc.T("practice.hud.title"),
                14f, TextPrimary, 38f, 9f, 150f, 20f);
            _titleLabel.fontStyle = FontStyles.Bold;

            // 赛制行
            _rulesetLabel = UiKit.CreateText(card, "LblRuleset", CsrLoc.T("practice.hud.ruleset", ""),
                12f, TextMuted, 38f, 30f, CardWidth - 60f, 20f);
            ApplyAlwaysDraw(_rulesetLabel);

            // 匹配状态行: 这里承载"后台寻找对手中 / 正在确认对手房间 / 已找到对手"
            _statusLabel = UiKit.CreateText(card, "LblStatus", CsrLoc.T("practice.hud.phase.running"),
                12f, TextMuted, 38f, 50f, CardWidth - 60f, 20f);
            ApplyAlwaysDraw(_statusLabel);

            // 时长标签: 与右侧数字同属一块计时区, 右对齐并共用同一右缘
            _clockCaption = UiKit.CreateTextRight(card, "LblClockCaption", CsrLoc.T("practice.hud.clock_caption"),
                11f, TextMuted, CardWidth - 118f, 74f, 104f, 18f);
            ApplyAlwaysDraw(_clockCaption);

            // 右下角小标签: 搜索态显示"离线单人·不计分", 命中后改成"已找到对手"
            _captionLabel = UiKit.CreateTextRight(card, "LblCaption", CsrLoc.T("practice.hud.note"),
                11f, TextMuted, 38f, 93f, CardWidth - 60f, 18f);
            ApplyAlwaysDraw(_captionLabel);

            // 右侧: 已匹配计时
            _clockLabel = UiKit.CreateTextRight(card, "LblClock", CsrFormat.FormatElapsed(0),
                22f, TextPrimary, CardWidth - 118f, 58f, 104f, 32f);
            ApplyAlwaysDraw(_clockLabel);

            _built = true;
            ApplyFollowPosition();
            return true;
        }

        // 行高保底 + 单行截断
        private static void ApplyAlwaysDraw(TMP_Text label)
        {
            if (label == null) return;
            label.overflowMode = TextOverflowModes.Ellipsis;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            RectTransform rt = label.transform as RectTransform;
            if (rt == null) return;
            float need = Mathf.Max(20f, label.fontSize * 1.5f);
            if (rt.sizeDelta.y < need)
            {
                rt.sizeDelta = new Vector2(rt.sizeDelta.x, need);
            }
        }
    }
}
