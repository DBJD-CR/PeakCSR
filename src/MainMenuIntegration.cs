using System;
using System.Collections;
using HarmonyLib;
using RankedPeak.Ui.Menu;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace PeakCSR
{
    // 在主菜单右侧 RANKED 路牌柱末尾原生追加一块 PCSR 1v1 木牌
    // 注意: 必须挂 MainMenuMainPage.Start 的 Postfix 并用实例参数 __instance,
    // 直接 patch RANKED 的 SignpostPatch.Postfix 时 __instance 恒为 null 会静默失败
    [HarmonyPatch(typeof(MainMenuMainPage), "Start")]
    public static class MainMenuIntegration
    {
        internal const string SignName = "RankedSign_PeakCSR1v1";

        // 与 RANKED 的 SignpostPatch 保持完全一致的木牌布局常量
        internal const float SignEdgeX = -110f;
        internal const float SignTailOffset = 100f;
        internal const float SignHeight = 52f;
        internal const float SignFontSize = 24f;
        internal const float SignHeadPadding = 70f;
        internal const float SignTailPadding = 22f;
        internal const float MinSignWidth = 190f;
        internal const float MaxSignWidth = 440f;
        // SignY(5): 121 - 5*62 = -189, 组间距 -18, 工具区间距 -16 => -223
        internal const float SignY = -223f;

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
                Plugin.Log.LogWarning("[PeakCSR] PCSR 木牌注入器挂载失败: " + ex.Message);
            }
        }

        // RANKED 会先在自己的 Postfix 里建好 RankedSignpostContainer,
        // 这里逐帧重试等待, 确保永远追加在 RANKED 木牌之后
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
            clone.transform.SetSiblingIndex(template.GetSiblingIndex() + 1);

            RectTransform rt = clone.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.anchorMin = new Vector2(0f, 1f);
                rt.anchorMax = new Vector2(0f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localScale = Vector3.one;
                rt.sizeDelta = new Vector2(MaxSignWidth, SignHeight);
                rt.anchoredPosition = new Vector2(SignEdgeX + SignTailOffset + MaxSignWidth / 2f, SignY);
            }

            Button btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                _signButton = btn;
                EnsureListener(btn);
            }

            EnsureUpdater(clone);
            RefreshSignLabel();
            Plugin.Log.LogInfo("[PeakCSR] 已在主菜单右侧路牌柱追加原生 PCSR 1v1 木牌！");
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

            // 兜底: 主菜单可能挂在非激活层, 用全场景扫描一次
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
            // 与 RANKED 原生木牌一致: 打开 PCSR 排位中心窗口 (其中可一键开始匹配)
            PcsrMenuWindow.ShowWindow();
        }

        // 刷新木牌文案与段位图标, 并让木牌宽度自适应文字
        internal static void RefreshSignLabel()
        {
            Button btn = _signButton;
            if (btn == null) return;

            MatchController mc = MatchController.Instance;
            int elo = EloCalculator.CurrentElo;
            string tierKey = EloCalculator.GetVanillaTierKey(elo);

            string label;
            if (mc != null && mc.State != CsMatchState.Idle && mc.State != CsMatchState.Finished)
            {
                label = CsrLoc.T("sign.queuing");
            }
            else
            {
                // 复用 RANKED 的浅底段位配色, 保证与相邻木牌观感一致
                string tierHex = ColorUtility.ToHtmlStringRGB(RankedMenuIcons.GetTierColorOnLight(tierKey));
                label = CsrLoc.T("sign.idle", tierHex, elo.ToString("N0"));
            }

            TextMeshProUGUI text = btn.GetComponentInChildren<TextMeshProUGUI>(true);
            if (text != null)
            {
                text.text = label;
                FitSignWidth(btn, text);
            }

            Transform iconTr = (btn.transform.Find("Hinge") ?? btn.transform).Find("Icons/RankedIcon0");
            Image icon = iconTr != null ? iconTr.GetComponent<Image>() : null;
            if (icon != null)
            {
                icon.sprite = RankedMenuIcons.GetTierIcon(tierKey);
                icon.color = RankedMenuIcons.GetTierColorOnLight(tierKey);
            }
        }

        // 复刻 RANKED 的 FitSignToLabel: 字号与宽度联动, 保持与其它木牌同一基线
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
            rt.anchoredPosition = new Vector2(SignEdgeX + SignTailOffset + width / 2f, SignY);
        }

        // 挂在 MainMenuMainPage 上, 负责等待 RANKED 木牌柱建好后注入并守护它不被隐藏
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
                        Plugin.Log.LogWarning("[PeakCSR] PCSR 木牌注入异常: " + ex.Message);
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

                Plugin.Log.LogWarning("[PeakCSR] 未找到 RANKED 路牌柱, PCSR 木牌注入跳过 (仍可按 F10 打开面板)。");
                _routine = null;
            }

            private void LateUpdate()
            {
                // RANKED 会隐藏柱子里未登记的按钮, 注入后短时间内兜底恢复
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
            private void OnEnable()
            {
                EloCalculator.OnRatingChanged += RefreshSignLabel;
                MatchController.OnStateChanged += RefreshSignLabel;
                CsrLoc.OnLanguageChanged += RefreshSignLabel;
                RefreshSignLabel();
            }

            private void OnDisable()
            {
                EloCalculator.OnRatingChanged -= RefreshSignLabel;
                MatchController.OnStateChanged -= RefreshSignLabel;
                CsrLoc.OnLanguageChanged -= RefreshSignLabel;
            }
        }
    }
}
