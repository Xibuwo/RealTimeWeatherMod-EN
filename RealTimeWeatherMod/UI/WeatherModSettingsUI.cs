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

                // Delay to allow iGPU Savior's ModSettingsManager to initialize first
                WeatherModUIRunner.Instance.RunDelayed(0.8f, () =>
                {
                    // Check if ModSettingsManager exists (iGPU Savior is installed)
                    if (TryIntegrateWithIGPU())
                    {
                        ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Detected iGPU Savior - integrating into shared MOD tab");
                        _integratedWithIGPU = true;
                        return;
                    }

                    // Fallback: Create standalone tab if iGPU Savior is not present
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

        /// <summary>
        /// Try to integrate with iGPU Savior's ModSettingsManager
        /// </summary>
        static bool TryIntegrateWithIGPU()
        {
            try
            {
                // Use reflection to find ModSettingsManager
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

                // Get the Instance property
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
                    // Try again after another delay
                    WeatherModUIRunner.Instance.RunDelayed(1.5f, () => RetryIntegration());
                    return false;
                }

                // Check if IsInitialized
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
        /// <summary>
        /// Trigger F7-like force refresh (reload config and force weather sync)
        /// </summary>
        static void TriggerForceRefresh()
        {
            try
            {
                ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Force refresh triggered by temperature unit change");

                // Reload config
                ChillEnvPlugin.Instance.Config.Reload();

                // Find AutoEnvRunner and trigger force sync
                var runner = Object.FindObjectOfType<Core.AutoEnvRunner>();
                if (runner != null)
                {
                    // Use reflection to call ForceRefreshWeather method
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
                return; // Already integrated

            if (TryIntegrateWithIGPU())
            {
                ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Successfully integrated with iGPU Savior on retry");
                _integratedWithIGPU = true;
            }
            else
            {
                // Final fallback - create standalone UI
                ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Integration failed after retries, creating standalone tab");
                CreateModSettingsTab(cachedSettingUI);
                HookIntoTabButtons(cachedSettingUI);
                modContentParent?.SetActive(false);
            }
        }

        /// <summary>
        /// Register weather mod settings with iGPU Savior's ModSettingsManager
        /// </summary>
        static void RegisterWithIGPU(object managerInstance, System.Type managerType)
        {
            try
            {
                // Check if UI is currently being built
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

                // Register the mod with Chill Env Sync as primary name
                var registerMethod = managerType.GetMethod("RegisterMod");
                if (registerMethod != null)
                {
                    // Register with the combined name
                    registerMethod.Invoke(managerInstance, new object[] { "Chill Env Sync", "5.2.1" });
                    ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Registered with ModSettingsManager");
                }

                // Add dropdown for temperature unit
                var addDropdownMethod = managerType.GetMethod("AddDropdown");
                if (addDropdownMethod != null)
                {
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
                }

                // Add toggles
                var addToggleMethod = managerType.GetMethod("AddToggle");
                if (addToggleMethod != null)
                {
                    // Weather Settings Section
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

                    // Features Section (must restart game)
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

                // Trigger UI rebuild and FORCE TITLE UPDATE
                WeatherModUIRunner.Instance.RunDelayed(0.2f, () =>
                {
                    var rebuildMethod = managerType.GetMethod("RebuildUI");
                    if (rebuildMethod != null && cachedSettingUI != null)
                    {
                        // Find the MOD content parent (should be created by iGPU Savior)
                        var modContent = cachedSettingUI.transform.Find("ModSettingsContent/ScrollView/Viewport/Content");
                        if (modContent != null)
                        {
                            rebuildMethod.Invoke(managerInstance, new object[] { modContent, cachedSettingUI.transform });
                            ChillEnvPlugin.Log?.LogInfo("[Weather MOD] UI rebuilt successfully");

                            // === FORCE TITLE UPDATE + CENTER CONTENT ===
                            try
                            {
                                // Update title
                                var modSettingsRoot = cachedSettingUI.transform.Find("ModSettingsContent");
                                if (modSettingsRoot != null)
                                {
                                    var titleTrans = modSettingsRoot.Find("Title");
                                    if (titleTrans != null)
                                    {
                                        var tmp = titleTrans.GetComponent<TextMeshProUGUI>();
                                        if (tmp != null)
                                        {
                                            // Center the text
                                            tmp.alignment = TextAlignmentOptions.Center;
                                            // Set the combined title with version on same line
                                            tmp.text = "<size=20><b>Chill Env Sync (iGPU Savior Active) <color=#888888>v5.2.1 + 1.6.0</color></b></size>";
                                            ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Title forcibly updated and centered");
                                        }
                                    }
                                }

                                // *** NEW: CENTER ALL CONTENT ***
                                if (modContent != null)
                                {
                                    var vGroup = modContent.GetComponent<VerticalLayoutGroup>();
                                    if (vGroup != null)
                                    {
                                        vGroup.childAlignment = TextAnchor.UpperCenter;
                                        ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Content centered");
                                    }

                                    // Force rebuild layout
                                    LayoutRebuilder.ForceRebuildLayoutImmediate(modContent as RectTransform);
                                }
                            }
                            catch (System.Exception ex)
                            {
                                ChillEnvPlugin.Log?.LogError($"[Weather MOD] Failed to update title/center: {ex.Message}");
                            }
                            // === END UPDATE ===
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

        // === STANDALONE MODE METHODS (used when iGPU Savior is not present) ===

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
            vGroup.childAlignment = TextAnchor.UpperCenter; // CENTER ALIGNMENT
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

                // Version on same line as title
                CreateSectionHeader(content.transform, "Chill Env Sync", "5.2.1");

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

                CreateSubHeader(content.transform, "Weather & Time");

                // Temperature Unit Dropdown
                CreateTemperatureDropdown(content.transform, settingUI);

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

        static void CreateTemperatureDropdown(Transform parent, SettingUI settingUI)
        {
            try
            {
                // Clone the graphics quality dropdown as template
                Transform graphicsContent = settingUI.transform.Find("Graphics/ScrollView/Viewport/Content");
                if (graphicsContent == null) return;

                Transform originalDropdown = graphicsContent.Find("GraphicQualityPulldownList");
                if (originalDropdown == null) return;

                // Clone the dropdown
                GameObject dropdown = Object.Instantiate(originalDropdown.gameObject);
                dropdown.name = "TemperatureUnitDropdown";
                dropdown.transform.SetParent(parent, false);
                dropdown.SetActive(false); // Hide while configuring

                // Update title
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

                // Find the Content container
                Transform content = dropdown.transform.Find("PulldownList/Pulldown/CurrentSelectText (TMP)/Content");
                if (content == null) return;

                // Clear existing options
                int childCount = content.childCount;
                for (int i = childCount - 1; i >= 0; i--)
                {
                    Object.Destroy(content.GetChild(i).gameObject);
                }

                // Keep Content always active
                content.gameObject.SetActive(true);

                // Get button template
                Transform firstButton = graphicsContent.Find("GraphicQualityPulldownList/PulldownList/Pulldown/CurrentSelectText (TMP)/Content");
                if (firstButton != null && firstButton.childCount > 0)
                {
                    GameObject buttonTemplate = Object.Instantiate(firstButton.GetChild(0).gameObject);
                    buttonTemplate.name = "SelectButtonTemplate";
                    buttonTemplate.SetActive(false);

                    // Temperature options
                    string[] tempOptions = { "Celsius (°C)", "Fahrenheit (°F)", "Kelvin (K)" };
                    string[] tempUnits = { "Celsius", "Fahrenheit", "Kelvin" };

                    // Determine current selection
                    string currentUnit = ChillEnvPlugin.Cfg_TemperatureUnit.Value;
                    int currentIndex = 0;
                    if (currentUnit.Equals("Fahrenheit", System.StringComparison.OrdinalIgnoreCase))
                        currentIndex = 1;
                    else if (currentUnit.Equals("Kelvin", System.StringComparison.OrdinalIgnoreCase))
                        currentIndex = 2;

                    // Add options
                    for (int i = 0; i < tempOptions.Length; i++)
                    {
                        GameObject newButton = Object.Instantiate(buttonTemplate, content);
                        newButton.name = $"SelectButton_{tempOptions[i]}";
                        newButton.SetActive(true);

                        // Set button text
                        TMP_Text buttonText = newButton.GetComponentInChildren<TMP_Text>();
                        if (buttonText != null)
                        {
                            buttonText.text = tempOptions[i];
                        }

                        // Ensure raycast enabled
                        var images = newButton.GetComponentsInChildren<Image>(true);
                        foreach (var img in images)
                        {
                            img.raycastTarget = true;
                        }

                        // Setup button click
                        Button button = newButton.GetComponent<Button>();
                        if (button != null)
                        {
                            int index = i; // Capture for closure
                            button.onClick.RemoveAllListeners();
                            button.onClick.AddListener(() =>
                            {
                                ChillEnvPlugin.Cfg_TemperatureUnit.Value = tempUnits[index];
                                ChillEnvPlugin.Instance.Config.Save();
                                ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Temperature unit changed to: {tempUnits[index]}");

                                // Update selected text and close dropdown
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

                    // Set initial selected text
                    UpdateDropdownSelectedText(dropdown, tempOptions[currentIndex]);

                    // Configure dropdown UI component WITH HIGHER Z-ORDER
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
                // Get PulldownListUI type
                var pulldownUIType = System.AppDomain.CurrentDomain.GetAssemblies()
                    .SelectMany(a => {
                        try { return a.GetTypes(); }
                        catch { return new System.Type[0]; }
                    })
                    .FirstOrDefault(t => t.Name == "PulldownListUI");

                if (pulldownUIType == null) return;

                // Find nodes
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

                // Calculate heights
                int childCount = content.childCount;
                float itemHeight = 40f;
                if (childCount > 0)
                {
                    var firstChild = content.GetChild(0).GetComponent<RectTransform>();
                    if (firstChild != null && firstChild.rect.height > 10) itemHeight = firstChild.rect.height;
                }

                float realContentHeight = childCount * itemHeight;
                float maxVisibleItems = 6f;
                float maxViewHeight = maxVisibleItems * itemHeight;
                float finalViewHeight = realContentHeight;
                float headerHeight = pulldownParentRect.rect.height;
                float openSize = headerHeight + finalViewHeight + 10f;

                // Set content size
                if (contentRect != null)
                {
                    contentRect.anchorMin = Vector2.zero;
                    contentRect.anchorMax = new Vector2(1f, 0f);
                    contentRect.pivot = new Vector2(0.5f, 1f);
                    contentRect.sizeDelta = new Vector2(0, realContentHeight);
                    contentRect.anchoredPosition = Vector2.zero;
                }

                // *** FIX: Add Canvas with HIGH sorting order to dropdown root ***
                Canvas rootCanvas = dropdown.GetComponent<Canvas>();
                if (rootCanvas == null)
                {
                    rootCanvas = dropdown.AddComponent<Canvas>();
                    rootCanvas.overrideSorting = true;
                    rootCanvas.sortingOrder = 40000; // Higher than toggles below

                    if (dropdown.GetComponent<GraphicRaycaster>() == null)
                        dropdown.AddComponent<GraphicRaycaster>();
                }
                else
                {
                    rootCanvas.overrideSorting = true;
                    rootCanvas.sortingOrder = 40000; // Ensure high order
                }

                // Set fields
                SetField("_currentSelectContentText", currentSelectTextComp);
                SetField("_pullDownParentRect", pulldownParentRect);
                SetField("_openPullDownSizeDeltaY", openSize);
                SetField("_pullDownOpenCloseSeconds", 0.3f);
                SetField("_pullDownOpenButton", pulldownButtonComp);
                SetField("_pullDownButtonRect", pulldownButtonRect);
                SetField("_isOpen", false);

                // Call Setup
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
            tmp.alignment = TextAlignmentOptions.Center; // CENTERED
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
            // VERSION ON SAME LINE
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
                sortedTexts[0].alignment = TextAlignmentOptions.MidlineLeft; // Keep left-aligned for toggles
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
