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
        private static bool _integratedWithIGPU = false;

        static void Postfix(SettingUI __instance)
        {
            try
            {
                cachedSettingUI = __instance;
                _rootCanvas = __instance.GetComponentInParent<Canvas>() ?? Object.FindObjectOfType<Canvas>();

                WeatherModUIRunner.Instance.RunDelayed(0.8f, () =>
                {
                    if (TryIntegrateWithIGPU())
                    {
                        ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Detected iGPU Savior - integrating into shared MOD tab");
                        _integratedWithIGPU = true;
                        return;
                    }

                    ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Running in standalone mode");
                    CreateModSettingsTab(__instance);
                    HookIntoTabButtons(__instance);
                    modContentParent?.SetActive(false);
                });
            }
            catch (System.Exception e)
            {
                ChillEnvPlugin.Log?.LogError($"Weather MOD UI integration failed: {e.Message}\n{e.StackTrace}");
            }
        }

        static bool TryIntegrateWithIGPU()
        {
            try
            {
                var allTypes = System.AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => {
                        try { return a.GetTypes(); }
                        catch { return new System.Type[0]; }
                    });

                var managerType = allTypes.FirstOrDefault(t =>
                    t.Name == "ModSettingsManager" && t.Namespace == "ModShared");

                if (managerType == null)
                {
                    ChillEnvPlugin.Log?.LogInfo("[Weather MOD] ModSettingsManager type not found - iGPU Savior not installed");
                    return false;
                }

                var instanceProp = managerType.GetProperty("Instance",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);

                if (instanceProp == null)
                {
                    ChillEnvPlugin.Log?.LogWarning("[Weather MOD] ModSettingsManager.Instance property not found");
                    return false;
                }

                var managerInstance = instanceProp.GetValue(null);
                if (managerInstance == null)
                {
                    ChillEnvPlugin.Log?.LogInfo("[Weather MOD] ModSettingsManager instance is null - not yet initialized, waiting longer...");
                    WeatherModUIRunner.Instance.RunDelayed(1.5f, () => RetryIntegration());
                    return false;
                }

                var initProp = managerType.GetProperty("IsInitialized");
                if (initProp != null)
                {
                    bool isInit = (bool)initProp.GetValue(managerInstance);
                    if (!isInit)
                    {
                        ChillEnvPlugin.Log?.LogInfo("[Weather MOD] ModSettingsManager not yet initialized, retrying...");
                        WeatherModUIRunner.Instance.RunDelayed(1.5f, () => RetryIntegration());
                        return false;
                    }
                }

                RegisterWithIGPU(managerInstance, managerType);
                return true;
            }
            catch (System.Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"[Weather MOD] Integration check failed: {ex.Message}");
                return false;
            }
        }

        static void TriggerForceRefresh()
        {
            try
            {
                ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Force refresh triggered");
                ChillEnvPlugin.Instance.Config.Reload();

                var runner = Object.FindObjectOfType<Core.AutoEnvRunner>();
                if (runner != null)
                {
                    var method = runner.GetType().GetMethod("ForceRefreshWeather",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    if (method != null)
                    {
                        method.Invoke(runner, null);
                        ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Weather data force refreshed");
                    }
                }
            }
            catch (System.Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"[Weather MOD] Force refresh failed: {ex.Message}");
            }
        }

        static void RetryIntegration()
        {
            if (_integratedWithIGPU)
                return;

            if (TryIntegrateWithIGPU())
            {
                ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Successfully integrated with iGPU Savior on retry");
                _integratedWithIGPU = true;
            }
            else
            {
                ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Integration failed after retries, creating standalone tab");
                CreateModSettingsTab(cachedSettingUI);
                HookIntoTabButtons(cachedSettingUI);
                modContentParent?.SetActive(false);
            }
        }

        static void RegisterWithIGPU(object managerInstance, System.Type managerType)
        {
            try
            {
                var isBuildingField = managerType.GetField("_isBuildingUI",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                if (isBuildingField != null)
                {
                    bool isBuilding = (bool)isBuildingField.GetValue(managerInstance);
                    if (isBuilding)
                    {
                        ChillEnvPlugin.Log?.LogInfo("[Weather MOD] ModSettingsManager is currently building UI, waiting...");
                        WeatherModUIRunner.Instance.RunDelayed(0.5f, () => RegisterWithIGPU(managerInstance, managerType));
                        return;
                    }
                }

                var registerMethod = managerType.GetMethod("RegisterMod");
                if (registerMethod != null)
                {
                    registerMethod.Invoke(managerInstance, new object[] { "Chill Env Sync", "5.3.0" });
                    ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Registered with ModSettingsManager");
                }

                var addDropdownMethod = managerType.GetMethod("AddDropdown");
                if (addDropdownMethod != null)
                {
                    // Temperature Unit
                    var tempOptions = new List<string> { "Celsius (°C)", "Fahrenheit (°F)", "Kelvin (K)" };
                    int currentIndex = 0;
                    string currentUnit = ChillEnvPlugin.Cfg_TemperatureUnit.Value;
                    if (currentUnit.Equals("Fahrenheit", System.StringComparison.OrdinalIgnoreCase))
                        currentIndex = 1;
                    else if (currentUnit.Equals("Kelvin", System.StringComparison.OrdinalIgnoreCase))
                        currentIndex = 2;

                    addDropdownMethod.Invoke(managerInstance, new object[] {
                        "Temperature Unit",
                        tempOptions,
                        currentIndex,
                        (System.Action<int>)((index) => {
                            string[] units = { "Celsius", "Fahrenheit", "Kelvin" };
                            ChillEnvPlugin.Cfg_TemperatureUnit.Value = units[index];
                            ChillEnvPlugin.Instance.Config.Save();
                            ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Temperature unit changed to: {units[index]}");
                            TriggerForceRefresh();
                        })
                    });

                    // Weather Provider
                    var providerOptions = new List<string> { "Seniverse", "OpenWeather" };
                    int providerIndex = ChillEnvPlugin.Cfg_WeatherProvider.Value.Equals("OpenWeather", System.StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                    addDropdownMethod.Invoke(managerInstance, new object[] {
                        "Weather Provider",
                        providerOptions,
                        providerIndex,
                        (System.Action<int>)((index) => {
                            string[] providers = { "Seniverse", "OpenWeather" };
                            ChillEnvPlugin.Cfg_WeatherProvider.Value = providers[index];
                            ChillEnvPlugin.Instance.Config.Save();
                            ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Weather provider changed to: {providers[index]}");
                            TriggerForceRefresh();
                        })
                    });
                }

                var addToggleMethod = managerType.GetMethod("AddToggle");
                if (addToggleMethod != null)
                {
                    addToggleMethod.Invoke(managerInstance, new object[] {
                        "Enable Weather API Sync",
                        ChillEnvPlugin.Cfg_EnableWeatherSync.Value,
                        (System.Action<bool>)((val) => {
                            ChillEnvPlugin.Cfg_EnableWeatherSync.Value = val;
                            ChillEnvPlugin.Instance.Config.Save();
                        })
                    });

                    addToggleMethod.Invoke(managerInstance, new object[] {
                        "Show Weather on Date Bar",
                        ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value,
                        (System.Action<bool>)((val) => {
                            ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value = val;
                            ChillEnvPlugin.Instance.Config.Save();
                        })
                    });

                    addToggleMethod.Invoke(managerInstance, new object[] {
                        "Show Detailed Time Segments",
                        ChillEnvPlugin.Cfg_DetailedTimeSegments.Value,
                        (System.Action<bool>)((val) => {
                            ChillEnvPlugin.Cfg_DetailedTimeSegments.Value = val;
                            ChillEnvPlugin.Instance.Config.Save();
                        })
                    });

                    addToggleMethod.Invoke(managerInstance, new object[] {
                        "Enable Seasonal Easter Eggs",
                        ChillEnvPlugin.Cfg_EnableEasterEggs.Value,
                        (System.Action<bool>)((val) => {
                            ChillEnvPlugin.Cfg_EnableEasterEggs.Value = val;
                            ChillEnvPlugin.Instance.Config.Save();
                        })
                    });

                    addToggleMethod.Invoke(managerInstance, new object[] {
                        "Unlock All Environments",
                        ChillEnvPlugin.Cfg_UnlockEnvironments.Value,
                        (System.Action<bool>)((val) => {
                            ChillEnvPlugin.Cfg_UnlockEnvironments.Value = val;
                            ChillEnvPlugin.Instance.Config.Save();
                            ChillEnvPlugin.Log?.LogWarning("Please restart the game for environment unlock changes to take effect");
                        })
                    });

                    addToggleMethod.Invoke(managerInstance, new object[] {
                        "Unlock All Decorations",
                        ChillEnvPlugin.Cfg_UnlockDecorations.Value,
                        (System.Action<bool>)((val) => {
                            ChillEnvPlugin.Cfg_UnlockDecorations.Value = val;
                            ChillEnvPlugin.Instance.Config.Save();
                            ChillEnvPlugin.Log?.LogWarning("Please restart the game for decoration unlock changes to take effect");
                        })
                    });
                }

                WeatherModUIRunner.Instance.RunDelayed(0.2f, () =>
                {
                    var rebuildMethod = managerType.GetMethod("RebuildUI");
                    if (rebuildMethod != null && cachedSettingUI != null)
                    {
                        var modContent = cachedSettingUI.transform.Find("ModSettingsContent/ScrollView/Viewport/Content");
                        if (modContent != null)
                        {
                            rebuildMethod.Invoke(managerInstance, new object[] { modContent, cachedSettingUI.transform });
                            ChillEnvPlugin.Log?.LogInfo("[Weather MOD] UI rebuilt successfully");

                            try
                            {
                                var modSettingsRoot = cachedSettingUI.transform.Find("ModSettingsContent");
                                if (modSettingsRoot != null)
                                {
                                    var titleTrans = modSettingsRoot.Find("Title");
                                    if (titleTrans != null)
                                    {
                                        var tmp = titleTrans.GetComponent<TextMeshProUGUI>();
                                        if (tmp != null)
                                        {
                                            tmp.alignment = TextAlignmentOptions.Center;
                                            tmp.text = "<size=20><b>Chill Env Sync (iGPU Savior Active) <color=#888888>v5.3.0 + 1.6.0</color></b></size>";
                                            ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Title forcibly updated and centered");
                                        }
                                    }
                                }

                                if (modContent != null)
                                {
                                    var vGroup = modContent.GetComponent<VerticalLayoutGroup>();
                                    if (vGroup != null)
                                    {
                                        vGroup.childAlignment = TextAnchor.UpperCenter;
                                        ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Content centered");
                                    }

                                    LayoutRebuilder.ForceRebuildLayoutImmediate(modContent as RectTransform);
                                }
                            }
                            catch (System.Exception ex)
                            {
                                ChillEnvPlugin.Log?.LogError($"[Weather MOD] Failed to update title/center: {ex.Message}");
                            }
                        }
                    }
                });

                ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Successfully integrated with iGPU Savior");
            }
            catch (System.Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"[Weather MOD] Registration failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // === STANDALONE MODE METHODS ===

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
            vGroup.padding = new RectOffset(40, 40, 20, 20);
            vGroup.childAlignment = TextAnchor.UpperCenter;
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

                CreateSectionHeader(content.transform, "Chill Env Sync", "5.3.0");

                Transform audioTabContent = settingUI.transform.Find("MusicAudio/ScrollView/Viewport/Content");
                if (audioTabContent == null) return;

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

                // === API CONFIGURATION SECTION ===
                CreateSubHeader(content.transform, "API Configuration");

                // Input field for Location
                CreateInputField(content.transform, settingUI, "Location", 
                    ChillEnvPlugin.Cfg_Location.Value,
                    (newValue) => {
                        ChillEnvPlugin.Cfg_Location.Value = newValue;
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Location changed to: {newValue}");
                        TriggerForceRefresh();
                    });

                // Input field for API Key
                CreateInputField(content.transform, settingUI, "API Key", 
                    ChillEnvPlugin.Cfg_GeneralAPI.Value,
                    (newValue) => {
                        ChillEnvPlugin.Cfg_GeneralAPI.Value = newValue;
                        ChillEnvPlugin.Cfg_ApiKey.Value = newValue; // Keep both in sync
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] API Key updated");
                        TriggerForceRefresh();
                    },
                    true); // Password mode

                // Weather Provider Dropdown
                CreateWeatherProviderDropdown(content.transform, settingUI);

                // Temperature Unit Dropdown
                CreateTemperatureDropdown(content.transform, settingUI);

                // === WEATHER & TIME SECTION ===
                CreateSubHeader(content.transform, "Weather & Time");

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

                // === FEATURES SECTION ===
                CreateSubHeader(content.transform, "Features (Must restart game)");

                CreateToggle(content.transform, originalRow, "Enable Seasonal Easter Eggs",
                    ChillEnvPlugin.Cfg_EnableEasterEggs.Value,
                    (val) => {
                        ChillEnvPlugin.Cfg_EnableEasterEggs.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    });

                CreateToggle(content.transform, originalRow, "Unlock All Environments",
                    ChillEnvPlugin.Cfg_UnlockEnvironments.Value,
                    (val) => {
                        ChillEnvPlugin.Cfg_UnlockEnvironments.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogWarning("Please restart the game for environment unlock changes to take effect");
                    });

                CreateToggle(content.transform, originalRow, "Unlock All Decorations",
                    ChillEnvPlugin.Cfg_UnlockDecorations.Value,
                    (val) => {
                        ChillEnvPlugin.Cfg_UnlockDecorations.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogWarning("Please restart the game for decoration unlock changes to take effect");
                    });

                LayoutRebuilder.ForceRebuildLayoutImmediate(content.GetComponent<RectTransform>());
            });
        }

        /// <summary>
        /// Creates an input field similar to the FPS input fields shown in the screenshot
        /// </summary>
        static void CreateInputField(Transform parent, SettingUI settingUI, string label, string initialValue, 
            System.Action<string> onValueChanged, bool isPassword = false)
        {
            // Create container
            GameObject container = new GameObject($"InputField_{label}");
            container.transform.SetParent(parent, false);

            var rect = container.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, 60);

            var layout = container.AddComponent<LayoutElement>();
            layout.preferredHeight = 60f;
            layout.minHeight = 60f;
            layout.flexibleWidth = 1f;

            var hGroup = container.AddComponent<HorizontalLayoutGroup>();
            hGroup.spacing = 20f;
            hGroup.childAlignment = TextAnchor.MiddleLeft;
            hGroup.childControlWidth = false;
            hGroup.childControlHeight = true;
            hGroup.childForceExpandWidth = false;
            hGroup.childForceExpandHeight = false;

            // Create label
            GameObject labelObj = new GameObject("Label");
            labelObj.transform.SetParent(container.transform, false);

            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(400, 60);

            var labelLayout = labelObj.AddComponent<LayoutElement>();
            labelLayout.minWidth = 400f;
            labelLayout.preferredWidth = 400f;

            var labelText = labelObj.AddComponent<TextMeshProUGUI>();
            labelText.text = label;
            labelText.fontSize = 18;
            labelText.alignment = TextAlignmentOptions.MidlineLeft;
            labelText.color = Color.white;

            // Create input field
            GameObject inputObj = new GameObject("InputField");
            inputObj.transform.SetParent(container.transform, false);

            var inputRect = inputObj.AddComponent<RectTransform>();
            inputRect.sizeDelta = new Vector2(350, 45);

            var inputLayout = inputObj.AddComponent<LayoutElement>();
            inputLayout.minWidth = 350f;
            inputLayout.preferredWidth = 350f;
            inputLayout.minHeight = 45f;
            inputLayout.preferredHeight = 45f;

            // Add background image
            var inputBg = inputObj.AddComponent<Image>();
            inputBg.color = new Color(0.15f, 0.15f, 0.15f, 1f);

            // Add input field component
            var inputField = inputObj.AddComponent<TMP_InputField>();
            inputField.textViewport = inputRect;

            // Create text component
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(inputObj.transform, false);

            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 5);
            textRect.offsetMax = new Vector2(-10, -5);

            var textComp = textObj.AddComponent<TextMeshProUGUI>();
            textComp.fontSize = 16;
            textComp.color = Color.white;
            textComp.alignment = TextAlignmentOptions.MidlineLeft;

            // Create placeholder
            GameObject placeholderObj = new GameObject("Placeholder");
            placeholderObj.transform.SetParent(inputObj.transform, false);

            var placeholderRect = placeholderObj.AddComponent<RectTransform>();
            placeholderRect.anchorMin = Vector2.zero;
            placeholderRect.anchorMax = Vector2.one;
            placeholderRect.offsetMin = new Vector2(10, 5);
            placeholderRect.offsetMax = new Vector2(-10, -5);

            var placeholderText = placeholderObj.AddComponent<TextMeshProUGUI>();
            placeholderText.text = $"Enter {label}...";
            placeholderText.fontSize = 16;
            placeholderText.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            placeholderText.alignment = TextAlignmentOptions.MidlineLeft;
            placeholderText.fontStyle = FontStyles.Italic;

            // Configure input field
            inputField.textComponent = textComp;
            inputField.placeholder = placeholderText;
            inputField.text = initialValue;
            
            if (isPassword)
            {
                inputField.contentType = TMP_InputField.ContentType.Password;
                inputField.inputType = TMP_InputField.InputType.Password;
            }
            else
            {
                inputField.contentType = TMP_InputField.ContentType.Standard;
            }

            inputField.onEndEdit.AddListener((value) => {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    onValueChanged?.Invoke(value.Trim());
                    PlayClickSound();
                }
            });

            // Set colors
            var colors = inputField.colors;
            colors.normalColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            colors.highlightedColor = new Color(0.2f, 0.2f, 0.2f, 1f);
            colors.selectedColor = new Color(0.25f, 0.25f, 0.25f, 1f);
            inputField.colors = colors;
        }

        /// <summary>
        /// Creates a dropdown for Weather Provider selection
        /// </summary>
        static void CreateWeatherProviderDropdown(Transform parent, SettingUI settingUI)
        {
            try
            {
                Transform graphicsContent = settingUI.transform.Find("Graphics/ScrollView/Viewport/Content");
                if (graphicsContent == null) return;

                Transform originalDropdown = graphicsContent.Find("GraphicQualityPulldownList");
                if (originalDropdown == null) return;

                GameObject dropdown = Object.Instantiate(originalDropdown.gameObject);
                dropdown.name = "WeatherProviderDropdown";
                dropdown.transform.SetParent(parent, false);
                dropdown.SetActive(false);

                var titlePaths = new[] { "TitleText", "Title/Text", "Text" };
                foreach (var path in titlePaths)
                {
                    var titleTransform = dropdown.transform.Find(path);
                    if (titleTransform != null)
                    {
                        var tmp = titleTransform.GetComponent<TMP_Text>();
                        if (tmp != null)
                        {
                            tmp.text = "Weather Provider";
                            break;
                        }
                    }
                }

                Transform content = dropdown.transform.Find("PulldownList/Pulldown/CurrentSelectText (TMP)/Content");
                if (content == null) return;

                int childCount = content.childCount;
                for (int i = childCount - 1; i >= 0; i--)
                {
                    Object.Destroy(content.GetChild(i).gameObject);
                }

                content.gameObject.SetActive(true);

                Transform firstButton = graphicsContent.Find("GraphicQualityPulldownList/PulldownList/Pulldown/CurrentSelectText (TMP)/Content");
                if (firstButton != null && firstButton.childCount > 0)
                {
                    GameObject buttonTemplate = Object.Instantiate(firstButton.GetChild(0).gameObject);
                    buttonTemplate.name = "SelectButtonTemplate";
                    buttonTemplate.SetActive(false);

                    string[] providerOptions = { "Seniverse", "OpenWeather" };
                    int currentIndex = ChillEnvPlugin.Cfg_WeatherProvider.Value.Equals("OpenWeather", System.StringComparison.OrdinalIgnoreCase) ? 1 : 0;

                    for (int i = 0; i < providerOptions.Length; i++)
                    {
                        GameObject newButton = Object.Instantiate(buttonTemplate, content);
                        newButton.name = $"SelectButton_{providerOptions[i]}";
                        newButton.SetActive(true);

                        TMP_Text buttonText = newButton.GetComponentInChildren<TMP_Text>();
                        if (buttonText != null)
                        {
                            buttonText.text = providerOptions[i];
                        }

                        var images = newButton.GetComponentsInChildren<Image>(true);
                        foreach (var img in images)
                        {
                            img.raycastTarget = true;
                        }

                        Button button = newButton.GetComponent<Button>();
                        if (button != null)
                        {
                            int index = i;
                            button.onClick.RemoveAllListeners();
                            button.onClick.AddListener(() =>
                            {
                                ChillEnvPlugin.Cfg_WeatherProvider.Value = providerOptions[index];
                                ChillEnvPlugin.Instance.Config.Save();
                                ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Weather provider changed to: {providerOptions[index]}");

                                UpdateDropdownSelectedText(dropdown, providerOptions[index]);
                                CloseDropdown(dropdown);
                                PlayClickSound();
                                TriggerForceRefresh();
                            });

                            if (!button.interactable) button.interactable = true;
                            if (button.targetGraphic == null)
                            {
                                var graphic = newButton.GetComponent<Image>();
                                if (graphic != null) button.targetGraphic = graphic;
                            }
                        }
                    }

                    Object.Destroy(buttonTemplate);
                    UpdateDropdownSelectedText(dropdown, providerOptions[currentIndex]);
                    ConfigureDropdownUI(dropdown, originalDropdown, content);
                }

                dropdown.SetActive(true);
            }
            catch (System.Exception e)
            {
                ChillEnvPlugin.Log?.LogError($"CreateWeatherProviderDropdown failed: {e.Message}");
            }
        }

        static void CreateTemperatureDropdown(Transform parent, SettingUI settingUI)
        {
            try
            {
                Transform graphicsContent = settingUI.transform.Find("Graphics/ScrollView/Viewport/Content");
                if (graphicsContent == null) return;

                Transform originalDropdown = graphicsContent.Find("GraphicQualityPulldownList");
                if (originalDropdown == null) return;

                GameObject dropdown = Object.Instantiate(originalDropdown.gameObject);
                dropdown.name = "TemperatureUnitDropdown";
                dropdown.transform.SetParent(parent, false);
                dropdown.SetActive(false);

                var titlePaths = new[] { "TitleText", "Title/Text", "Text" };
                foreach (var path in titlePaths)
                {
                    var titleTransform = dropdown.transform.Find(path);
                    if (titleTransform != null)
                    {
                        var tmp = titleTransform.GetComponent<TMP_Text>();
                        if (tmp != null)
                        {
                            tmp.text = "Temperature Unit";
                            break;
                        }
                    }
                }

                Transform content = dropdown.transform.Find("PulldownList/Pulldown/CurrentSelectText (TMP)/Content");
                if (content == null) return;

                int childCount = content.childCount;
                for (int i = childCount - 1; i >= 0; i--)
                {
                    Object.Destroy(content.GetChild(i).gameObject);
                }

                content.gameObject.SetActive(true);

                Transform firstButton = graphicsContent.Find("GraphicQualityPulldownList/PulldownList/Pulldown/CurrentSelectText (TMP)/Content");
                if (firstButton != null && firstButton.childCount > 0)
                {
                    GameObject buttonTemplate = Object.Instantiate(firstButton.GetChild(0).gameObject);
                    buttonTemplate.name = "SelectButtonTemplate";
                    buttonTemplate.SetActive(false);

                    string[] tempOptions = { "Celsius (°C)", "Fahrenheit (°F)", "Kelvin (K)" };
                    string[] tempUnits = { "Celsius", "Fahrenheit", "Kelvin" };

                    string currentUnit = ChillEnvPlugin.Cfg_TemperatureUnit.Value;
                    int currentIndex = 0;
                    if (currentUnit.Equals("Fahrenheit", System.StringComparison.OrdinalIgnoreCase))
                        currentIndex = 1;
                    else if (currentUnit.Equals("Kelvin", System.StringComparison.OrdinalIgnoreCase))
                        currentIndex = 2;

                    for (int i = 0; i < tempOptions.Length; i++)
                    {
                        GameObject newButton = Object.Instantiate(buttonTemplate, content);
                        newButton.name = $"SelectButton_{tempOptions[i]}";
                        newButton.SetActive(true);

                        TMP_Text buttonText = newButton.GetComponentInChildren<TMP_Text>();
                        if (buttonText != null)
                        {
                            buttonText.text = tempOptions[i];
                        }

                        var images = newButton.GetComponentsInChildren<Image>(true);
                        foreach (var img in images)
                        {
                            img.raycastTarget = true;
                        }

                        Button button = newButton.GetComponent<Button>();
                        if (button != null)
                        {
                            int index = i;
                            button.onClick.RemoveAllListeners();
                            button.onClick.AddListener(() =>
                            {
                                ChillEnvPlugin.Cfg_TemperatureUnit.Value = tempUnits[index];
                                ChillEnvPlugin.Instance.Config.Save();
                                ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Temperature unit changed to: {tempUnits[index]}");

                                UpdateDropdownSelectedText(dropdown, tempOptions[index]);
                                CloseDropdown(dropdown);
                                PlayClickSound();
                                TriggerForceRefresh();
                            });

                            if (!button.interactable) button.interactable = true;
                            if (button.targetGraphic == null)
                            {
                                var graphic = newButton.GetComponent<Image>();
                                if (graphic != null) button.targetGraphic = graphic;
                            }
                        }
                    }

                    Object.Destroy(buttonTemplate);
                    UpdateDropdownSelectedText(dropdown, tempOptions[currentIndex]);
                    ConfigureDropdownUI(dropdown, originalDropdown, content);
                }

                dropdown.SetActive(true);
            }
            catch (System.Exception e)
            {
                ChillEnvPlugin.Log?.LogError($"CreateTemperatureDropdown failed: {e.Message}");
            }
        }

        static void UpdateDropdownSelectedText(GameObject dropdown, string text)
        {
            var paths = new[] { "PulldownList/Pulldown/CurrentSelectText (TMP)", "CurrentSelectText (TMP)" };
            foreach (var path in paths)
            {
                var transform = dropdown.transform.Find(path);
                if (transform != null)
                {
                    var tmp = transform.GetComponent<TMP_Text>();
                    if (tmp != null)
                    {
                        tmp.text = text;
                        return;
                    }
                }
            }
        }

        static void CloseDropdown(GameObject dropdown)
        {
            try
            {
                var pulldownUI = dropdown.GetComponentsInChildren<Component>(true)
                    .FirstOrDefault(c => c.GetType().Name == "PulldownListUI");

                if (pulldownUI != null)
                {
                    var closeMethod = pulldownUI.GetType().GetMethod("ClosePullDown",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

                    if (closeMethod != null)
                    {
                        closeMethod.Invoke(pulldownUI, new object[] { false });
                    }
                }
            }
            catch (System.Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"CloseDropdown failed: {ex.Message}");
            }
        }

        static void ConfigureDropdownUI(GameObject dropdown, Transform originalDropdown, Transform content)
        {
            try
            {
                var pulldownUIType = System.AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => {
                        try { return a.GetTypes(); }
                        catch { return new System.Type[0]; }
                    })
                    .FirstOrDefault(t => t.Name == "PulldownListUI");

                if (pulldownUIType == null) return;

                Transform pulldownList = dropdown.transform.Find("PulldownList");
                Transform pulldown = dropdown.transform.Find("PulldownList/Pulldown");
                Transform pulldownButton = dropdown.transform.Find("PulldownList/PulldownButton");
                Transform currentSelectText = dropdown.transform.Find("PulldownList/Pulldown/CurrentSelectText (TMP)");

                GameObject uiHost = (pulldownList != null) ? pulldownList.gameObject : dropdown;
                Component pulldownUI = uiHost.GetComponent(pulldownUIType);
                if (pulldownUI == null) pulldownUI = uiHost.AddComponent(pulldownUIType);

                Button pulldownButtonComp = pulldownButton?.GetComponent<Button>();
                TMP_Text currentSelectTextComp = currentSelectText?.GetComponent<TMP_Text>();
                RectTransform pulldownParentRect = pulldown?.GetComponent<RectTransform>();
                RectTransform pulldownButtonRect = pulldownButton?.GetComponent<RectTransform>();
                RectTransform contentRect = content?.GetComponent<RectTransform>();

                if (pulldownButtonComp == null || currentSelectTextComp == null || pulldownParentRect == null) return;

                void SetField(string fieldName, object value)
                {
                    if (value == null) return;
                    pulldownUIType.GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.SetValue(pulldownUI, value);
                }

                int childCount = content.childCount;
                float itemHeight = 40f;
                if (childCount > 0)
                {
                    var firstChild = content.GetChild(0).GetComponent<RectTransform>();
                    if (firstChild != null && firstChild.rect.height > 10) itemHeight = firstChild.rect.height;
                }

                float realContentHeight = childCount * itemHeight;
                float finalViewHeight = realContentHeight;
                float headerHeight = pulldownParentRect.rect.height;
                float openSize = headerHeight + finalViewHeight + 10f;

                if (contentRect != null)
                {
                    contentRect.anchorMin = Vector2.zero;
                    contentRect.anchorMax = new Vector2(1f, 0f);
                    contentRect.pivot = new Vector2(0.5f, 1f);
                    contentRect.sizeDelta = new Vector2(0, realContentHeight);
                    contentRect.anchoredPosition = Vector2.zero;
                }

                Canvas rootCanvas = dropdown.GetComponent<Canvas>();
                if (rootCanvas == null)
                {
                    rootCanvas = dropdown.AddComponent<Canvas>();
                    rootCanvas.overrideSorting = true;
                    rootCanvas.sortingOrder = 40000;

                    if (dropdown.GetComponent<GraphicRaycaster>() == null)
                        dropdown.AddComponent<GraphicRaycaster>();
                }
                else
                {
                    rootCanvas.overrideSorting = true;
                    rootCanvas.sortingOrder = 40000;
                }

                SetField("_currentSelectContentText", currentSelectTextComp);
                SetField("_pullDownParentRect", pulldownParentRect);
                SetField("_openPullDownSizeDeltaY", openSize);
                SetField("_pullDownOpenCloseSeconds", 0.3f);
                SetField("_pullDownOpenButton", pulldownButtonComp);
                SetField("_pullDownButtonRect", pulldownButtonRect);
                SetField("_isOpen", false);

                pulldownUIType.GetMethod("Setup")?.Invoke(pulldownUI, null);
            }
            catch (System.Exception e)
            {
                ChillEnvPlugin.Log?.LogError($"ConfigureDropdownUI failed: {e.Message}");
            }
        }

        static void CreateSubHeader(Transform parent, string text)
        {
            GameObject obj = new GameObject($"SubHeader_{text}");
            obj.transform.SetParent(parent, false);

            var rect = obj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, 35);

            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = 35f;
            le.preferredHeight = 35f;
            le.flexibleWidth = 1f;

            var tmp = obj.AddComponent<TextMeshProUGUI>();
            tmp.text = $"<size=16><color=#AAAAAA>{text}</color></size>";
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = new Color(0.67f, 0.67f, 0.67f, 1f);
        }

        static void CreateSectionHeader(Transform parent, string name, string version)
        {
            GameObject obj = new GameObject($"Header_{name}");
            obj.transform.SetParent(parent, false);

            var rect = obj.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, 50);

            var le = obj.AddComponent<LayoutElement>();
            le.minHeight = 50f;
            le.preferredHeight = 50f;
            le.flexibleWidth = 1f;

            var tmp = obj.AddComponent<TextMeshProUGUI>();
            string verStr = string.IsNullOrEmpty(version) ? "" : $" <size=16><color=#888888>v{version}</color></size>";
            tmp.text = $"<size=20><b>{name}</b></size>{verStr}";
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
        }

        static void CreateToggle(Transform parent, Transform templateRow, string label, bool initialValue, System.Action<bool> onValueChanged)
        {
            GameObject toggleRow = Object.Instantiate(templateRow.gameObject);
            toggleRow.name = $"WeatherToggle_{label}";
            toggleRow.transform.SetParent(parent, false);
            toggleRow.SetActive(true);

            var layoutElement = toggleRow.GetComponent<LayoutElement>();
            if (layoutElement == null)
                layoutElement = toggleRow.AddComponent<LayoutElement>();

            layoutElement.preferredWidth = 800f;
            layoutElement.minWidth = 800f;

            var titleTexts = toggleRow.GetComponentsInChildren<TMP_Text>(true);
            if (titleTexts.Length > 0)
            {
                var sortedTexts = titleTexts.OrderBy(t => t.transform.position.x).ToArray();
                sortedTexts[0].text = label;
                sortedTexts[0].alignment = TextAlignmentOptions.MidlineLeft;
            }

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
