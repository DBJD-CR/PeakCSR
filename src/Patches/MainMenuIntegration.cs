using System;
using System.Collections;
using HarmonyLib;
using RankedPeak.Services;
using RankedPeak.Ui.Menu;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 在主菜单右侧 RANKED 路牌柱原生追加一块 PCSR 1v1 木牌 (原样迁移, 只改订阅源)
    [HarmonyPatch(typeof(MainMenuMainPage), "Start")]
    public static class MainMenuIntegration
    {
        internal const string SignName = "RankedSign_PeakCSR1v1";

        internal const float SignEdgeX = -110f;
        internal const float SignTailOffset = 100f;
        internal const float SignHeight = 52f;
        internal const float SignFontSize = 24f;
        internal const float SignHeadPadding = 70f;
        internal const float SignTailPadding = 22f;
        internal const float MinSignWidth = 190f;
        internal const float MaxSignWidth = 440f;
        internal const float SignY = 262f;

        private static Button _signButton;

        public static void Postfix(MainMenuMainPage __instance)
        {
            if (__instance == null) return;
            try
            {
                if (__instance.gameObject.GetComponent<CsrSignInjector>() == null)
                {
                    __instance.gameObject.AddComponent<CsrSignInjector>();
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] PCSR 木牌注入器挂载失败: " + ex.Message);
            }
        }

        internal static bool TryAttachSign()
        {
            Transform mast = FindMast();
            if (mast == null) return false;

            Transform buttonsParent = mast.Find("Buttons") ?? mast;
            if (buttonsParent == null) return false;

            Transform existing = buttonsParent.Find(SignName);
            if (existing != null)
            {
                if (!existing.gameObject.activeSelf) existing.gameObject.SetActive(true);
                Button existBtn = existing.GetComponent<Button>();
                if (existBtn != null)
                {
                    _signButton = existBtn;
                    EnsureListener(existBtn);
                    EnsureUpdater(existing.gameObject);
                    RefreshSignLabel();
                }
                return true;
            }

            Transform template = buttonsParent.Find("RankedSign_Settings")
                ?? buttonsParent.Find("RankedSign_GhostDuell")
                ?? buttonsParent.Find("RankedSign_LiveRace");
            if (template == null) return false;

            GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, buttonsParent);
            clone.name = SignName;
            clone.SetActive(true);
            clone.transform.SetAsLastSibling();

            RectTransform rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localScale = Vector3.one;
                rt.sizeDelta = new Vector2(MaxSignWidth, SignHeight);
                rt.anchoredPosition = new Vector2(SignEdgeX + MaxSignWidth / 2f, SignY);
            }

            Button btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                _signButton = btn;
                EnsureListener(btn);
            }

            EnsureUpdater(clone);
            RefreshSignLabel();
            PcsrLog.LogInfo("[PeakCSR] 已在主菜单右侧路牌柱追加原生 PCSR 1v1 木牌！");
            return true;
        }

        private static void EnsureListener(Button btn)
        {
            btn.onClick.RemoveAllListeners();
            btn.onClick.AddListener(new UnityAction(OnCsrSignClicked));
        }

        private static void EnsureUpdater(GameObject sign)
        {
            if (sign.GetComponent<CsrSignUpdater>() == null)
            {
                sign.AddComponent<CsrSignUpdater>();
            }
        }

        private static Transform FindMast()
        {
            GameObject go = GameObject.Find("RankedSignpostContainer");
            if (go != null) return go.transform;

            MainMenuMainPage page = UnityEngine.Object.FindAnyObjectByType<MainMenuMainPage>();
            if (page == null) return null;
            Transform direct = page.transform.Find("RankedSignpostContainer");
            if (direct != null) return direct;
            if (page.transform.parent != null)
            {
                Transform sibling = page.transform.parent.Find("RankedSignpostContainer");
                if (sibling != null) return sibling;
            }
            return null;
        }

        private static void OnCsrSignClicked()
        {
            PcsrMenuWindow.ShowWindow();
        }

        // 刷新木牌文案与段位图标, 并让木牌宽度自适应文字
        // withIcon = false 时只更新计时文案 (每秒 tick 走这条, 避免反复查 sprite)
        internal static void RefreshSignLabel()
        {
            RefreshSignLabel(true);
        }

        internal static void RefreshSignLabel(bool withIcon)
        {
            Button btn = _signButton;
            if (btn == null) return;

            MatchCoordinator mc = MatchCoordinator.Instance;
            int elo = 1000;
            PlayerProfileBridge profile = mc != null ? mc.Profile : null;
            if (profile != null) elo = profile.CurrentElo;
            string tierKey = TierCatalog.GetVanillaTierKey(elo);

            // 队列表态才显示"匹配中 (mm:ss)"; 其余一律回到 ELO 展示
            bool queuing = mc != null && mc.State != CsMatchState.Idle && mc.State != CsMatchState.Finished;
            string label;
            if (queuing)
            {
                label = CsrLoc.T("sign.queuing", CsrFormat.FormatElapsed(mc.QueueElapsedSeconds));
            }
            else
            {
                string tierHex = TierCatalog.GetTierHexColorOnLight(elo);
                label = CsrLoc.T("sign.idle", tierHex, elo.ToString("N0"));
            }

            TextMeshProUGUI text = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                // 文案没变就不重排
                if (text.text != label)
                {
                    text.text = label;
                    FitSignWidth(btn, text);
                }
            }

            if (!withIcon) return;

            Transform iconTr = (btn.transform.Find("Hinge") ?? btn.transform).Find("Icons/RankedIcon0");
            Image icon = iconTr != null ? iconTr.GetComponent<Image>() : null;
            if (icon != null)
            {
                icon.sprite = RankedMenuIcons.GetTierIcon(tierKey);
                icon.color = RankedMenuIcons.GetTierColorOnLight(tierKey);
            }
        }

        private static void FitSignWidth(Button btn, TextMeshProUGUI text)
        {
            RectTransform rt = btn.transform as RectTransform;
            if (rt == null) return;

            text.fontSize = SignFontSize;
            text.ForceMeshUpdate(false, false);
            float preferred = text.preferredWidth;

            float maxTextWidth = MaxSignWidth - SignHeadPadding - SignTailPadding;
            if (preferred > maxTextWidth && preferred > 0f)
            {
                text.fontSize = SignFontSize * maxTextWidth / preferred;
                text.ForceMeshUpdate(false, false);
                preferred = text.preferredWidth;
            }

            float width = Mathf.Clamp(SignHeadPadding + preferred + SignTailPadding, MinSignWidth, MaxSignWidth);
            rt.sizeDelta = new Vector2(width, rt.sizeDelta.y);
            rt.anchoredPosition = new Vector2(SignEdgeX + width / 2f, SignY);
        }

        internal sealed class CsrSignInjector : MonoBehaviour
        {
            private const float WaitSeconds = 8f;
            private const float GuardSeconds = 5f;

            private Coroutine _routine;
            private float _guardUntil;

            private void OnEnable()
            {
                if (_routine == null)
                {
                    _routine = StartCoroutine(EnsureSignRoutine());
                }
            }

            private void OnDisable()
            {
                if (_routine != null)
                {
                    StopCoroutine(_routine);
                    _routine = null;
                }
            }

            private IEnumerator EnsureSignRoutine()
            {
                float deadline = Time.realtimeSinceStartup + WaitSeconds;
                while (Time.realtimeSinceStartup < deadline)
                {
                    bool ok = false;
                    try
                    {
                        ok = TryAttachSign();
                    }
                    catch (Exception ex)
                    {
                        PcsrLog.LogWarning("[PeakCSR] PCSR 木牌注入异常: " + ex.Message);
                        _routine = null;
                        yield break;
                    }

                    if (ok)
                    {
                        _guardUntil = Time.realtimeSinceStartup + GuardSeconds;
                        _routine = null;
                        yield break;
                    }
                    yield return null;
                }

                PcsrLog.LogWarning("[PeakCSR] 未找到 RANKED 路牌柱, PCSR 木牌注入跳过 (仍可按 F10 打开面板)。");
                _routine = null;
            }

            private void LateUpdate()
            {
                if (_signButton == null) return;
                if (Time.realtimeSinceStartup > _guardUntil) return;
                if (!_signButton.gameObject.activeSelf)
                {
                    _signButton.gameObject.SetActive(true);
                }
            }
        }

        private sealed class CsrSignUpdater : MonoBehaviour
        {
            // 秒表刷新间隔: 0.25s 足够让"秒"跟手, 又不会每帧都算一次文案
            private const float TickInterval = 0.25f;
            private float _nextTick;
            // 上一次实际显示的秒数; 只有秒数变了才重绘
            private int _shownSeconds = -1;
            private CsMatchState _shownState = CsMatchState.Idle;

            private void OnEnable()
            {
                // 事件回调带参数的委托不能直接挂无参方法, 这里用 lambda 包一层
                EventBus.OnRatingChanged += OnFullRefresh;
                EventBus.OnMatchStateChanged += OnFullRefresh;
                CsrLoc.OnLanguageChanged += OnFullRefresh;
                ResetTick();
                RefreshSignLabel();
            }

            private void OnDisable()
            {
                EventBus.OnRatingChanged -= OnFullRefresh;
                EventBus.OnMatchStateChanged -= OnFullRefresh;
                CsrLoc.OnLanguageChanged -= OnFullRefresh;
            }

            private void OnFullRefresh()
            {
                ResetTick();
                RefreshSignLabel();
            }

            private void ResetTick()
            {
                _shownSeconds = -1;
                _nextTick = 0f;
            }

            // 没有任何事件通知排队时长在走, 这里自己按节拍推进,
            // 并且只在"秒数或状态真的变了"时才动 UI
            private void Update()
            {
                if (_signButton == null) return;
                if (Time.unscaledTime < _nextTick) return;
                _nextTick = Time.unscaledTime + TickInterval;

                MatchCoordinator mc = MatchCoordinator.Instance;
                CsMatchState state = mc != null ? mc.State : CsMatchState.Idle;
                if (state == CsMatchState.Idle || state == CsMatchState.Finished) return;
                if (state != _shownState)
                {
                    _shownState = state;
                    _shownSeconds = -1;
                    RefreshSignLabel();
                    return;
                }

                int seconds = Mathf.FloorToInt(mc.QueueElapsedSeconds);
                if (seconds == _shownSeconds) return;
                _shownSeconds = seconds;
                RefreshSignLabel(false);
            }
        }
    }
}
