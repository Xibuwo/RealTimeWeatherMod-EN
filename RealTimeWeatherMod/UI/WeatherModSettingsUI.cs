using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;
using Bulbul;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace ChillWithYou.EnvSync.UI
{
    [HarmonyPatch(typeof(SettingUI), "Setup")]
    public class WeatherModSettingsUI
    {
        private static GameObject modContentParent;
        private static InteractableUI modInteractableUI;
        private static SettingUI cachedSettingUI;
        private static Canvas _rootCanvas;

        static void Postfix(SettingUI __instance)
        {
            try
            {
                cachedSettingUI = __instance;
                _rootCanvas = __instance.GetComponentInParent<Canvas>() ?? Object.FindObjectOfType<Canvas>();

                CreateModSettingsTab(__instance);
                HookIntoTabButtons(__instance);

                modContentParent?.SetActive(false);
            }
            catch (System.Exception e)
            {
                ChillEnvPlugin.Log?.LogError($"Weather MOD UI integration failed: {e.Message}\n{e.StackTrace}");
            }
        }

        static void CreateModSettingsTab(SettingUI settingUI)
        {
            try
            {
                var creditsButton = AccessTools.Field(typeof(SettingUI), "_creditsInteractableUI").GetValue(settingUI) as InteractableUI;
                var creditsParent = AccessTools.Field(typeof(SettingUI), "_creditsParent").GetValue(settingUI) as GameObject;
                if (creditsButton == null || creditsParent == null) return;

                GameObject modTabButton = Object.Instantiate(creditsButton.gameObject);
                modTabButton.name = "WeatherModSettingsTabButton";
                modTabButton.transform.SetParent(creditsButton.transform.parent, false);
                modTabButton.transform.SetSiblingIndex(creditsButton.transform.GetSiblingIndex() + 1);

                modContentParent = Object.Instantiate(creditsParent);
                modContentParent.name = "WeatherModSettingsContent";
                modContentParent.transform.SetParent(creditsParent.transform.parent, false);
                modContentParent.SetActive(false);

                var scrollRect = modContentParent.GetComponentInChildren<ScrollRect>();
                if (scrollRect == null) return;

                var content = scrollRect.content;
                foreach (Transform child in content) Object.Destroy(child.gameObject);

                ConfigureContentLayout(content.gameObject);

                WeatherModUIRunner.Instance.RunDelayed(0.3f, () =>
                {
                    UpdateModButtonText(modTabButton);
                    UpdateModContentText(modContentParent);
                    AdjustTabBarLayout(modTabButton.transform.parent);
                });

                modInteractableUI = modTabButton.GetComponent<InteractableUI>();
                modInteractableUI?.Setup();
                modTabButton.GetComponent<Button>()?.onClick.AddListener(() => SwitchToModTab(settingUI));

                CreateWeatherModSettings(content.gameObject, settingUI);
            }
            catch (System.Exception e)
            {
                ChillEnvPlugin.Log?.LogError($"CreateModSettingsTab failed: {e.Message}");
            }
        }

        static void ConfigureContentLayout(GameObject content)
        {
            var rect = content.GetComponent<RectTransform>();
            if (rect != null)
            {
                rect.anchorMin = new Vector2(0, 1);
                rect.anchorMax = new Vector2(1, 1);
                rect.pivot = new Vector2(0.5f, 1f);
                rect.anchoredPosition = Vector2.zero;
                rect.sizeDelta = new Vector2(0, 0);
                rect.localScale = Vector3.one;
            }

            var vGroup = content.GetComponent<VerticalLayoutGroup>() ?? content.AddComponent<VerticalLayoutGroup>();
            vGroup.spacing = 16f;
            vGroup.padding = new RectOffset(60, 40, 20, 20);
            vGroup.childAlignment = TextAnchor.UpperLeft;
            vGroup.childControlHeight = false;
            vGroup.childControlWidth = true;
            vGroup.childForceExpandHeight = false;
            vGroup.childForceExpandWidth = true;

            var fitter = content.GetComponent<ContentSizeFitter>() ?? content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        }

        static void CreateWeatherModSettings(GameObject content, SettingUI settingUI)
        {
            WeatherModUIRunner.Instance.RunDelayed(0.5f, () =>
            {
                if (content == null || settingUI == null) return;

                // Create header
                CreateSectionHeader(content.transform, "Chill Env Sync", "5.2.1");

                // Get the audio tab to clone toggle style
                Transform audioTabContent = settingUI.transform.Find("MusicAudio/ScrollView/Viewport/Content");
                if (audioTabContent == null) return;

                // Find the Pomodoro toggle as template
                Transform originalRow = null;
                foreach (Transform child in audioTabContent)
                {
                    if (child.name.Contains("Pomodoro") && child.name.Contains("OnOff"))
                    {
                        originalRow = child;
                        break;
                    }
                }

                if (originalRow == null) return;

                // Create toggles
                CreateToggle(content.transform, originalRow, "Enable Weather API Sync",
                    ChillEnvPlugin.Cfg_EnableWeatherSync.Value,
                    (val) => {
                        ChillEnvPlugin.Cfg_EnableWeatherSync.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    });

                CreateToggle(content.transform, originalRow, "Show Weather on Date Bar",
                    ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value,
                    (val) => {
                        ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    });

                CreateToggle(content.transform, originalRow, "Show Detailed Time Segments",
                    ChillEnvPlugin.Cfg_DetailedTimeSegments.Value,
                    (val) => {
                        ChillEnvPlugin.Cfg_DetailedTimeSegments.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    });

                CreateToggle(content.transform, originalRow, "Enable Seasonal Easter Eggs",
                    ChillEnvPlugin.Cfg_EnableEasterEggs.Value,
                    (val) => {
                        ChillEnvPlugin.Cfg_EnableEasterEggs.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    });

                // Force layout rebuild
                LayoutRebuilder.ForceRebuildLayoutImmediate(content.GetComponent<RectTransform>());
            });
        }

        static void CreateSectionHeader(Transform parent, string name, string version)
        {
            GameObject obj = new GameObject($"Header_{name}");
            obj.transform.SetParent(parent, false);

            var rect = obj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, 55);

            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = 55f;
            le.preferredHeight = 55f;
            le.flexibleWidth = 1f;

            var tmp = obj.AddComponent<TextMeshProUGUI>();
            string verStr = string.IsNullOrEmpty(version) ? "" : $" <size=18><color=#888888>v{version}</color></size>";
            tmp.text = $"<size=24><b>{name}</b></size>{verStr}";
            tmp.alignment = TextAlignmentOptions.BottomLeft;
            tmp.color = Color.white;
        }

        static void CreateToggle(Transform parent, Transform templateRow, string label, bool initialValue, System.Action<bool> onValueChanged)
        {
            GameObject toggleRow = Object.Instantiate(templateRow.gameObject);
            toggleRow.name = $"WeatherToggle_{label}";
            toggleRow.transform.SetParent(parent, false);
            toggleRow.SetActive(true);

            // Update label
            var titleTexts = toggleRow.GetComponentsInChildren<TMP_Text>(true);
            if (titleTexts.Length > 0)
            {
                var sortedTexts = titleTexts.OrderBy(t => t.transform.position.x).ToArray();
                sortedTexts[0].text = label;
            }

            // Setup buttons
            Button[] buttons = toggleRow.GetComponentsInChildren<Button>(true);
            if (buttons.Length < 2) return;

            System.Array.Sort(buttons, (a, b) => a.transform.position.x.CompareTo(b.transform.position.x));

            Button btnOn = buttons[0];
            Button btnOff = buttons[1];

            SetButtonText(btnOn, "ON");
            SetButtonText(btnOff, "OFF");

            btnOn.onClick.RemoveAllListeners();
            btnOff.onClick.RemoveAllListeners();

            void UpdateState(bool state)
            {
                btnOn.interactable = !state;
                btnOff.interactable = state;

                var btnOnInteractableUI = btnOn.GetComponent<InteractableUI>();
                var btnOffInteractableUI = btnOff.GetComponent<InteractableUI>();

                if (state)
                {
                    btnOnInteractableUI?.ActivateUseUI(false);
                    btnOffInteractableUI?.DeactivateUseUI(false);
                }
                else
                {
                    btnOnInteractableUI?.DeactivateUseUI(false);
                    btnOffInteractableUI?.ActivateUseUI(false);
                }
            }

            btnOn.onClick.AddListener(() => {
                if (btnOn.interactable)
                {
                    UpdateState(true);
                    onValueChanged?.Invoke(true);
                    PlayClickSound();
                }
            });

            btnOff.onClick.AddListener(() => {
                if (btnOff.interactable)
                {
                    UpdateState(false);
                    onValueChanged?.Invoke(false);
                    PlayClickSound();
                }
            });

            UpdateState(initialValue);
        }

        static void SetButtonText(Button btn, string text)
        {
            var tmp = btn.GetComponentInChildren<TMP_Text>();
            if (tmp != null) tmp.text = text;
        }

        static void UpdateModButtonText(GameObject modTabButton)
        {
            var allTexts = modTabButton.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var text in allTexts) text.text = "MOD";
        }

        static void UpdateModContentText(GameObject modContentParent)
        {
            var titleTransform = modContentParent.transform.Find("Title");
            if (titleTransform != null)
            {
                var t = titleTransform.GetComponent<TextMeshProUGUI>();
                if (t != null) t.text = "MOD";
            }
            var allTexts = modContentParent.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var text in allTexts)
            {
                if (text.text.Contains("Credits")) text.text = "Weather & Environment Settings";
            }
        }

        static void AdjustTabBarLayout(Transform tabBarParent)
        {
            var rectTransform = tabBarParent.GetComponent<RectTransform>();
            if (rectTransform != null)
                LayoutRebuilder.ForceRebuildLayoutImmediate(rectTransform);
        }

        private static void HookIntoTabButtons(SettingUI settingUI)
        {
            var buttons = new[] { "_generalInteractableUI", "_graphicInteractableUI", "_audioInteractableUI", "_creditsInteractableUI" };
            var parents = new[] { "_generalParent", "_graphicParent", "_audioParent", "_creditsParent" };
            for (int i = 0; i < buttons.Length; i++)
            {
                var btn = AccessTools.Field(typeof(SettingUI), buttons[i]).GetValue(settingUI) as InteractableUI;
                var parent = AccessTools.Field(typeof(SettingUI), parents[i]).GetValue(settingUI) as GameObject;
                if (btn != null)
                {
                    var capturedBtn = btn;
                    var capturedParent = parent;
                    btn.GetComponent<Button>()?.onClick.AddListener(() =>
                    {
                        modContentParent?.SetActive(false);
                        modInteractableUI?.DeactivateUseUI(false);
                        if (capturedParent) { capturedParent.SetActive(true); capturedBtn.ActivateUseUI(false); }
                    });
                }
            }
        }

        private static void SwitchToModTab(SettingUI settingUI)
        {
            var parents = new[] { "_generalParent", "_graphicParent", "_audioParent", "_creditsParent" };
            foreach (var p in parents)
                (AccessTools.Field(typeof(SettingUI), p).GetValue(settingUI) as GameObject)?.SetActive(false);

            var buttons = new[] { "_generalInteractableUI", "_graphicInteractableUI", "_audioInteractableUI", "_creditsInteractableUI" };
            foreach (var b in buttons)
                (AccessTools.Field(typeof(SettingUI), b).GetValue(settingUI) as InteractableUI)?.DeactivateUseUI(false);

            PlayClickSound();
            modInteractableUI?.ActivateUseUI(false);
            modContentParent?.SetActive(true);

            var scrollRect = modContentParent?.GetComponentInChildren<ScrollRect>();
            if (scrollRect != null)
            {
                LayoutRebuilder.ForceRebuildLayoutImmediate(modContentParent.GetComponent<RectTransform>());
                scrollRect.verticalNormalizedPosition = 1f;
            }
        }

        private static void PlayClickSound()
        {
            if (cachedSettingUI == null) return;
            var sss = AccessTools.Field(typeof(SettingUI), "_systemSeService").GetValue(cachedSettingUI);
            sss?.GetType().GetMethod("PlayClick")?.Invoke(sss, null);
        }
    }

    public class WeatherModUIRunner : MonoBehaviour
    {
        private static WeatherModUIRunner _instance;

        public static WeatherModUIRunner Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("WeatherModUI_CoroutineRunner");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<WeatherModUIRunner>();
                }
                return _instance;
            }
        }

        public void RunDelayed(float seconds, System.Action action)
        {
            StartCoroutine(DelayedAction(seconds, action));
        }

        private IEnumerator DelayedAction(float seconds, System.Action action)
        {
            yield return new WaitForSeconds(seconds);
            action?.Invoke();
        }
    }

    [HarmonyPatch(typeof(SettingUI), "Activate")]
    public class WeatherModSettingsActivateHandler
    {
        static void Postfix(SettingUI __instance)
        {
            try
            {
                var modContentParent = AccessTools.Field(typeof(WeatherModSettingsUI), "modContentParent").GetValue(null) as GameObject;
                var modInteractableUI = AccessTools.Field(typeof(WeatherModSettingsUI), "modInteractableUI").GetValue(null) as InteractableUI;
                modContentParent?.SetActive(false);
                modInteractableUI?.DeactivateUseUI(false);

                var generalButton = AccessTools.Field(typeof(SettingUI), "_generalInteractableUI").GetValue(__instance) as InteractableUI;
                var generalParent = AccessTools.Field(typeof(SettingUI), "_generalParent").GetValue(__instance) as GameObject;
                generalButton?.ActivateUseUI(false);
                generalParent?.SetActive(true);

                var others = new[] { "_graphicParent", "_audioParent", "_creditsParent" };
                foreach (var o in others)
                    (AccessTools.Field(typeof(SettingUI), o).GetValue(__instance) as GameObject)?.SetActive(false);
            }
            catch { }
        }
    }
}
