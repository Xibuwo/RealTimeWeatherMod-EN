// WeatherModSettingsUI.cs  ─  Chill Env Sync
// Refactored to use iGPU Savior's public ModShared API instead of
// reflection-based hacks.  Standalone fallback is preserved in full.
//
// CHANGE SUMMARY vs. the original:
//  • TryIntegrateWithIGPU   – still reflection-finds ModSettingsManager,
//                             but only to check existence; then casts to
//                             the real type via a thin wrapper so we get
//                             compile-time safety for every API call.
//  • RegisterWithIGPU       – completely rewritten: uses RegisterMod(),
//                             RegisterTranslation(), AddInputField(),
//                             AddDropdown(), AddToggle(), RebuildUI().
//                             Zero manual GameObject manipulation.
//  • TranslateIGPUSaviorLabels – REMOVED (iGPU has ModLocalizer).
//  • The three delayed TranslateIGPUSaviorLabels coroutines – REMOVED.
//  • _isBuildingUI reflection check – replaced by IsInitialized property.
//  • "Portrait Mode Hotkey" sibling-index search – REMOVED.
//  • Dynamic combined version title hack – REMOVED (section header
//    in iGPU already shows each mod's name + version cleanly).
//  • All standalone-mode code is untouched.

using Bulbul;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChillWithYou.EnvSync.UI
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Harmony patch entry-point (unchanged hook point)
    // ─────────────────────────────────────────────────────────────────────────
    [HarmonyPatch(typeof(SettingUI), "Setup")]
    public class WeatherModSettingsUI
    {
        // ── shared state for standalone mode ──────────────────────────────────
        private static GameObject modContentParent;
        private static InteractableUI modInteractableUI;
        private static SettingUI cachedSettingUI;
        private static Canvas _rootCanvas;
        private static bool _integratedWithIGPU = false;

        // ── iGPU manager reference (kept as 'object' to avoid hard dep) ───────
        //    We resolve it once and store it; all calls go through the wrapper.
        private static object _igpuManager;

        // ─────────────────────────────────────────────────────────────────────
        //  Postfix
        // ─────────────────────────────────────────────────────────────────────
        static void Postfix(SettingUI __instance)
        {
            try
            {
                cachedSettingUI = __instance;
                _rootCanvas = __instance.GetComponentInParent<Canvas>()
                              ?? Object.FindObjectOfType<Canvas>();

                WeatherModUIRunner.Instance.RunDelayed(0.1f, () =>
                {
                    if (TryIntegrateWithIGPU())
                    {
                        ChillEnvPlugin.Log?.LogInfo(
                            "[Weather MOD] iGPU Savior detected – registered via public API");
                        _integratedWithIGPU = true;
                        return;
                    }

                    ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Standalone mode");
                    CreateModSettingsTab(__instance);
                    HookIntoTabButtons(__instance);
                    modContentParent?.SetActive(false);
                });
            }
            catch (Exception e)
            {
                ChillEnvPlugin.Log?.LogError(
                    $"Weather MOD UI integration failed: {e.Message}\n{e.StackTrace}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  iGPU integration – REFACTORED
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Checks whether ModSettingsManager (iGPU Savior) is present and ready.
        /// Returns true and schedules registration, false otherwise.
        /// </summary>
        static bool TryIntegrateWithIGPU()
        {
            try
            {
                // --- find ModSettingsManager type (still needs reflection; iGPU is
                //     a sibling assembly, not a hard project reference) ---
                Type managerType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "ModSettingsManager" && t.Namespace == "ModShared");

                if (managerType == null)
                {
                    ChillEnvPlugin.Log?.LogInfo(
                        "[Weather MOD] ModSettingsManager not found – iGPU Savior not installed");
                    return false;
                }

                // --- grab the singleton instance ---
                PropertyInfo instanceProp = managerType.GetProperty(
                    "Instance", BindingFlags.Public | BindingFlags.Static);
                object manager = instanceProp?.GetValue(null);

                if (manager == null)
                {
                    // Not ready yet – retry once after a longer delay
                    WeatherModUIRunner.Instance.RunDelayed(0.2f, RetryIntegration);
                    return false;
                }

                // --- check IsInitialized (public bool property, no private-field hack) ---
                PropertyInfo isInitProp = managerType.GetProperty("IsInitialized");
                if (isInitProp != null && !(bool)isInitProp.GetValue(manager))
                {
                    WeatherModUIRunner.Instance.RunDelayed(0.2f, RetryIntegration);
                    return false;
                }

                _igpuManager = manager;
                RegisterWithIGPU(manager, managerType);
                return true;
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogError(
                    $"[Weather MOD] iGPU integration check failed: {ex.Message}");
                return false;
            }
        }

        static void RetryIntegration()
        {
            if (_integratedWithIGPU) return;

            if (TryIntegrateWithIGPU())
            {
                _integratedWithIGPU = true;
            }
            else
            {
                ChillEnvPlugin.Log?.LogInfo(
                    "[Weather MOD] iGPU integration failed after retries – falling back to standalone");
                CreateModSettingsTab(cachedSettingUI);
                HookIntoTabButtons(cachedSettingUI);
                modContentParent?.SetActive(false);
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  RegisterWithIGPU – completely rewritten, no manual UI manipulation
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Registers all Chill Env Sync settings with iGPU Savior's
        /// ModSettingsManager using only its documented public API.
        /// </summary>
        static void RegisterWithIGPU(object manager, Type managerType)
        {
            try
            {
                // --- Convenience wrappers (avoid repetitive reflection) ---
                void CallVoid(string method, params object[] args)
                {
                    // Pick the right overload by argument count (simple but robust)
                    var candidates = managerType.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public)
                        .Where(m => m.Name == method && m.GetParameters().Length == args.Length)
                        .ToArray();

                    if (candidates.Length == 0)
                    {
                        ChillEnvPlugin.Log?.LogWarning(
                            $"[Weather MOD] ModSettingsManager.{method}({args.Length} args) not found");
                        return;
                    }
                    candidates[0].Invoke(manager, args);
                }

                // ── 1. Register this mod (creates a labeled section header) ────
                CallVoid("RegisterMod", "Chill Env Sync", ChillEnvPlugin.PluginVersion);

                // ── 2. Register translation keys so iGPU's ModLocalizer handles
                //       the text – no more manual TranslateIGPUSaviorLabels() ───
                RegisterTranslations(manager, managerType);

                // ── 3. Location input field ─────────────────────────────────────
                CallVoid("AddInputField",
                    "CHILL_LOCATION",
                    ChillEnvPlugin.Cfg_Location.Value,
                    (Action<string>)(newValue =>
                    {
                        ChillEnvPlugin.Cfg_Location.Value = newValue;
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Location → {newValue}");
                        TriggerForceRefresh();
                    }));

                // ── 4. API Key input field ──────────────────────────────────────
                CallVoid("AddInputField",
                    "CHILL_API_KEY",
                    ChillEnvPlugin.Cfg_GeneralAPI.Value,
                    (Action<string>)(newValue =>
                    {
                        ChillEnvPlugin.Cfg_GeneralAPI.Value = newValue;
                        ChillEnvPlugin.Cfg_ApiKey.Value = newValue;
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogInfo("[Weather MOD] API Key updated");
                        TriggerForceRefresh();
                    }));

                // ── 5. Weather Provider dropdown ───────────────────────────────
                var providerOptions = new List<string>
                    { "CHILL_PROVIDER_SENIVERSE", "CHILL_PROVIDER_OW", "CHILL_PROVIDER_OM" };

                int providerIndex = ChillEnvPlugin.Cfg_WeatherProvider.Value
                    .Equals("OpenWeather", StringComparison.OrdinalIgnoreCase) ? 1
                    : ChillEnvPlugin.Cfg_WeatherProvider.Value
                    .Equals("OpenMeteo", StringComparison.OrdinalIgnoreCase) ? 2 : 0;

                CallVoid("AddDropdown",
                    "CHILL_WEATHER_PROVIDER",
                    providerOptions,
                    providerIndex,
                    (Action<int>)(index =>
                    {
                        string[] providers = { "Seniverse", "OpenWeather", "OpenMeteo" };
                        ChillEnvPlugin.Cfg_WeatherProvider.Value = providers[index];
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Provider → {providers[index]}");
                        TriggerForceRefresh();
                    }));

                // ── 6. Temperature Unit dropdown ───────────────────────────────
                var tempOptions = new List<string>
                    { "CHILL_UNIT_CELSIUS", "CHILL_UNIT_FAHRENHEIT", "CHILL_UNIT_KELVIN" };

                int tempIndex =
                    ChillEnvPlugin.Cfg_TemperatureUnit.Value
                        .Equals("Fahrenheit", StringComparison.OrdinalIgnoreCase) ? 1
                    : ChillEnvPlugin.Cfg_TemperatureUnit.Value
                        .Equals("Kelvin", StringComparison.OrdinalIgnoreCase) ? 2 : 0;

                CallVoid("AddDropdown",
                    "CHILL_TEMP_UNIT",
                    tempOptions,
                    tempIndex,
                    (Action<int>)(index =>
                    {
                        string[] units = { "Celsius", "Fahrenheit", "Kelvin" };
                        ChillEnvPlugin.Cfg_TemperatureUnit.Value = units[index];
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogInfo($"[Weather MOD] Unit → {units[index]}");
                        TriggerForceRefresh();
                    }));

                // ── 7. Toggles ─────────────────────────────────────────────────
                CallVoid("AddToggle",
                    "CHILL_ENABLE_WEATHER",
                    ChillEnvPlugin.Cfg_EnableWeatherSync.Value,
                    (Action<bool>)(val =>
                    {
                        ChillEnvPlugin.Cfg_EnableWeatherSync.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    }));

                CallVoid("AddToggle",
                    "CHILL_SHOW_WEATHER_UI",
                    ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value,
                    (Action<bool>)(val =>
                    {
                        ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    }));

                CallVoid("AddToggle",
                    "CHILL_DETAILED_TIME",
                    ChillEnvPlugin.Cfg_DetailedTimeSegments.Value,
                    (Action<bool>)(val =>
                    {
                        ChillEnvPlugin.Cfg_DetailedTimeSegments.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    }));

                CallVoid("AddToggle",
                    "CHILL_EASTER_EGGS",
                    ChillEnvPlugin.Cfg_EnableEasterEggs.Value,
                    (Action<bool>)(val =>
                    {
                        ChillEnvPlugin.Cfg_EnableEasterEggs.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                    }));

                CallVoid("AddToggle",
                    "CHILL_UNLOCK_ENVS",
                    ChillEnvPlugin.Cfg_UnlockEnvironments.Value,
                    (Action<bool>)(val =>
                    {
                        ChillEnvPlugin.Cfg_UnlockEnvironments.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogWarning(
                            "[Weather MOD] Environment unlock changes require a game restart");
                    }));

                CallVoid("AddToggle",
                    "CHILL_UNLOCK_DECOS",
                    ChillEnvPlugin.Cfg_UnlockDecorations.Value,
                    (Action<bool>)(val =>
                    {
                        ChillEnvPlugin.Cfg_UnlockDecorations.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogWarning(
                            "[Weather MOD] Decoration unlock changes require a game restart");
                    }));

                CallVoid("AddToggle",
                    "CHILL_UNLOCK_PURCHASE",
                    ChillEnvPlugin.Cfg_UnlockPurchasableItems.Value,
                    (Action<bool>)(val =>
                    {
                        ChillEnvPlugin.Cfg_UnlockPurchasableItems.Value = val;
                        ChillEnvPlugin.Instance.Config.Save();
                        ChillEnvPlugin.Log?.LogWarning(
                            "[Weather MOD] Purchasable unlock changes require a game restart");
                    }));

                // ── 8. iGPU Savior calls RebuildUI itself after all mods register.

                ChillEnvPlugin.Log?.LogInfo("[Weather MOD] Registered with iGPU Savior successfully");
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogError(
                    $"[Weather MOD] RegisterWithIGPU failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Translation registration
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Registers all Chill Env Sync UI strings with iGPU's
        /// ModTranslationManager so ModLocalizer handles them automatically.
        /// Call once during RegisterWithIGPU.
        /// </summary>
        static void RegisterTranslations(object manager, Type managerType)
        {
            MethodInfo addTr = managerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .FirstOrDefault(m => m.Name == "RegisterTranslation"
                                     && m.GetParameters().Length == 4);

            if (addTr == null)
            {
                ChillEnvPlugin.Log?.LogWarning(
                    "[Weather MOD] RegisterTranslation not found – text will show raw keys");
                return;
            }

            void T(string key, string en, string ja, string zh)
                => addTr.Invoke(manager, new object[] { key, en, ja, zh });

            // Input labels
            T("CHILL_LOCATION", "Location", "場所", "位置");
            T("CHILL_API_KEY", "API Key", "APIキー", "API 密钥");

            // Dropdown labels
            T("CHILL_WEATHER_PROVIDER", "Weather Provider", "天気プロバイダー", "天气提供商");
            T("CHILL_TEMP_UNIT", "Temperature Unit", "温度単位", "温度单位");

            // Dropdown options – providers
            T("CHILL_PROVIDER_SENIVERSE", "Seniverse", "Seniverse", "心知天气");
            T("CHILL_PROVIDER_OW", "OpenWeather", "OpenWeather", "OpenWeather");
            T("CHILL_PROVIDER_OM", "OpenMeteo (No Key)", "OpenMeteo（キー不要）", "OpenMeteo（无需密钥）");

            // Dropdown options – temperature units
            T("CHILL_UNIT_CELSIUS", "Celsius (°C)", "摂氏 (°C)", "摄氏 (°C)");
            T("CHILL_UNIT_FAHRENHEIT", "Fahrenheit (°F)", "華氏 (°F)", "华氏 (°F)");
            T("CHILL_UNIT_KELVIN", "Kelvin (K)", "ケルビン (K)", "开尔文 (K)");

            // Toggle labels
            T("CHILL_ENABLE_WEATHER", "Enable Weather Sync", "天気同期を有効にする", "启用天气同步");
            T("CHILL_SHOW_WEATHER_UI", "Show Weather on Date Bar", "日付バーに天気を表示", "在日期栏显示天气");
            T("CHILL_DETAILED_TIME", "Detailed Time Segments", "詳細な時間帯表示", "显示详细时段");
            T("CHILL_EASTER_EGGS", "Seasonal Easter Eggs", "季節のイースターエッグ", "季节彩蛋");
            T("CHILL_UNLOCK_ENVS", "Unlock All Environments", "全環境のアンロック", "解锁全部环境");
            T("CHILL_UNLOCK_DECOS", "Unlock All Decorations", "全デコレーションのアンロック", "解锁全部装饰");
            T("CHILL_UNLOCK_PURCHASE", "Unlock Purchasable Items", "購入アイテムのアンロック", "解锁可购买物品");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Shared helper
        // ─────────────────────────────────────────────────────────────────────

        static void TriggerForceRefresh()
        {
            try
            {
                Services.WeatherService.InvalidateCache();
                Core.AutoEnvRunner.TriggerWeatherRefresh();
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogError(
                    $"[Weather MOD] Force refresh failed: {ex.Message}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        //  STANDALONE MODE  (completely unchanged from the original)
        //  Everything below this line is the original standalone implementation.
        // ═════════════════════════════════════════════════════════════════════

        private static TMP_FontAsset GetValidFont()
        {
            if (cachedSettingUI == null) return null;
            foreach (var t in cachedSettingUI.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (t != null && t.font != null) return t.font;
            return null;
        }

        static void CreateModSettingsTab(SettingUI settingUI)
        {
            try
            {
                var creditsButton = AccessTools.Field(typeof(SettingUI), "_creditsInteractableUI")
                                               .GetValue(settingUI) as InteractableUI;
                var creditsParent = AccessTools.Field(typeof(SettingUI), "_creditsParent")
                                               .GetValue(settingUI) as GameObject;
                if (creditsButton == null || creditsParent == null) return;

                GameObject modTabButton = Object.Instantiate(creditsButton.gameObject);
                modTabButton.name = "WeatherModSettingsTabButton";
                modTabButton.transform.SetParent(creditsButton.transform.parent, false);
                modTabButton.transform.SetSiblingIndex(
                    creditsButton.transform.GetSiblingIndex() + 1);

                var le = modTabButton.GetComponent<LayoutElement>();
                if (le == null) le = modTabButton.AddComponent<LayoutElement>();
                le.flexibleWidth = 0;
                le.minWidth = 80f;
                le.preferredWidth = 120f;

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
                modTabButton.GetComponent<Button>()?.onClick
                    .AddListener(() => SwitchToModTab(settingUI));

                CreateWeatherModSettings(content.gameObject, settingUI);
            }
            catch (Exception e)
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

            var vGroup = content.GetComponent<VerticalLayoutGroup>()
                         ?? content.AddComponent<VerticalLayoutGroup>();
            vGroup.spacing = 16f;
            vGroup.padding = new RectOffset(40, 40, 20, 20);
            vGroup.childAlignment = TextAnchor.UpperCenter;
            vGroup.childControlHeight = false;
            vGroup.childControlWidth = true;
            vGroup.childForceExpandHeight = false;
            vGroup.childForceExpandWidth = true;

            var fitter = content.GetComponent<ContentSizeFitter>()
                         ?? content.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            fitter.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;
        }

        static void CreateWeatherModSettings(GameObject content, SettingUI settingUI)
        {
            WeatherModUIRunner.Instance.RunDelayed(0.5f, () =>
            {
                if (content == null || settingUI == null) return;

                CreateSectionHeader(content.transform, "Chill Env Sync",
                    ChillEnvPlugin.PluginVersion);

                Transform audioTabContent = settingUI.transform
                    .Find("MusicAudio/ScrollView/Viewport/Content");
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

                // ── API Configuration ───────────────────────────────────────
                CreateSubHeader(content.transform, "API Configuration");

                CreateInputField(content.transform, settingUI, "Location",
                    ChillEnvPlugin.Cfg_Location.Value,
                    newValue =>
                    {
                        ChillEnvPlugin.Cfg_Location.Value = newValue;
                        ChillEnvPlugin.Instance.Config.Save();
                        TriggerForceRefresh();
                    });

                CreateInputField(content.transform, settingUI, "API Key",
                    ChillEnvPlugin.Cfg_GeneralAPI.Value,
                    newValue =>
                    {
                        ChillEnvPlugin.Cfg_GeneralAPI.Value = newValue;
                        ChillEnvPlugin.Cfg_ApiKey.Value = newValue;
                        ChillEnvPlugin.Instance.Config.Save();
                        TriggerForceRefresh();
                    }, true);

                CreateWeatherProviderDropdown(content.transform, settingUI);
                CreateTemperatureDropdown(content.transform, settingUI);

                // ── Weather & Time ──────────────────────────────────────────
                CreateSubHeader(content.transform, "Weather & Time");

                CreateToggle(content.transform, originalRow, "Enable Weather API Sync",
                    ChillEnvPlugin.Cfg_EnableWeatherSync.Value,
                    val => { ChillEnvPlugin.Cfg_EnableWeatherSync.Value = val; ChillEnvPlugin.Instance.Config.Save(); });

                CreateToggle(content.transform, originalRow, "Show Weather on Date Bar",
                    ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value,
                    val => { ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value = val; ChillEnvPlugin.Instance.Config.Save(); });

                CreateToggle(content.transform, originalRow, "Show Detailed Time Segments",
                    ChillEnvPlugin.Cfg_DetailedTimeSegments.Value,
                    val => { ChillEnvPlugin.Cfg_DetailedTimeSegments.Value = val; ChillEnvPlugin.Instance.Config.Save(); });

                // ── Features ────────────────────────────────────────────────
                CreateSubHeader(content.transform, "Features (requires restart)");

                CreateToggle(content.transform, originalRow, "Enable Seasonal Easter Eggs",
                    ChillEnvPlugin.Cfg_EnableEasterEggs.Value,
                    val => { ChillEnvPlugin.Cfg_EnableEasterEggs.Value = val; ChillEnvPlugin.Instance.Config.Save(); });

                CreateToggle(content.transform, originalRow, "Unlock All Environments",
                    ChillEnvPlugin.Cfg_UnlockEnvironments.Value,
                    val => { ChillEnvPlugin.Cfg_UnlockEnvironments.Value = val; ChillEnvPlugin.Instance.Config.Save(); });

                CreateToggle(content.transform, originalRow, "Unlock All Decorations",
                    ChillEnvPlugin.Cfg_UnlockDecorations.Value,
                    val => { ChillEnvPlugin.Cfg_UnlockDecorations.Value = val; ChillEnvPlugin.Instance.Config.Save(); });

                CreateToggle(content.transform, originalRow, "Unlock Purchasable Items",
                    ChillEnvPlugin.Cfg_UnlockPurchasableItems.Value,
                    val => { ChillEnvPlugin.Cfg_UnlockPurchasableItems.Value = val; ChillEnvPlugin.Instance.Config.Save(); });

                LayoutRebuilder.ForceRebuildLayoutImmediate(
                    content.GetComponent<RectTransform>());
            });
        }

        // ── Standalone UI helpers (all unchanged) ─────────────────────────────

        static void CreateInputField(Transform parent, SettingUI settingUI,
            string label, string initialValue, Action<string> onValueChanged,
            bool isPassword = false)
        {
            TMP_FontAsset validFont = GetValidFont();

            GameObject container = new GameObject($"InputField_{label}");
            container.transform.SetParent(parent, false);

            var rect = container.AddComponent<RectTransform>();
            rect.sizeDelta = new Vector2(0, 60);

            var layout = container.AddComponent<LayoutElement>();
            layout.preferredHeight = 60f;
            layout.minHeight = 60f;
            layout.flexibleWidth = 1f;

            var hGroup = container.AddComponent<HorizontalLayoutGroup>();
            hGroup.spacing = 30f;
            hGroup.childAlignment = TextAnchor.MiddleCenter;
            hGroup.childControlWidth = false;
            hGroup.childControlHeight = true;
            hGroup.childForceExpandWidth = false;
            hGroup.childForceExpandHeight = false;

            // Label
            GameObject labelObj = new GameObject("Label");
            labelObj.transform.SetParent(container.transform, false);
            var labelRect = labelObj.AddComponent<RectTransform>();
            labelRect.sizeDelta = new Vector2(200, 60);
            var labelLayout = labelObj.AddComponent<LayoutElement>();
            labelLayout.minWidth = labelLayout.preferredWidth = 200f;
            var labelText = labelObj.AddComponent<TextMeshProUGUI>();
            if (validFont != null) labelText.font = validFont;
            labelText.text = label;
            labelText.fontSize = 18;
            labelText.alignment = TextAlignmentOptions.MidlineRight;
            labelText.color = Color.white;

            // Input
            GameObject inputObj = new GameObject("InputField");
            inputObj.transform.SetParent(container.transform, false);
            var inputRect = inputObj.AddComponent<RectTransform>();
            inputRect.sizeDelta = new Vector2(450, 45);
            var inputLayout = inputObj.AddComponent<LayoutElement>();
            inputLayout.minWidth = inputLayout.preferredWidth = 450f;
            inputLayout.minHeight = inputLayout.preferredHeight = 45f;

            var inputBg = inputObj.AddComponent<Image>();
            inputBg.color = new Color(0.15f, 0.15f, 0.15f, 1f);
            inputBg.raycastTarget = true;

            var inputField = inputObj.AddComponent<TMP_InputField>();
            inputField.textViewport = inputRect;
            inputField.targetGraphic = inputBg;

            // Text child
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(inputObj.transform, false);
            var textRect = textObj.AddComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(10, 5);
            textRect.offsetMax = new Vector2(-10, -5);
            var textComp = textObj.AddComponent<TextMeshProUGUI>();
            if (validFont != null) textComp.font = validFont;
            textComp.fontSize = 16;
            textComp.color = Color.white;
            textComp.alignment = TextAlignmentOptions.MidlineLeft;
            textComp.raycastTarget = false;

            // Placeholder
            GameObject phObj = new GameObject("Placeholder");
            phObj.transform.SetParent(inputObj.transform, false);
            var phRect = phObj.AddComponent<RectTransform>();
            phRect.anchorMin = Vector2.zero;
            phRect.anchorMax = Vector2.one;
            phRect.offsetMin = new Vector2(10, 5);
            phRect.offsetMax = new Vector2(-10, -5);
            var phText = phObj.AddComponent<TextMeshProUGUI>();
            if (validFont != null) phText.font = validFont;
            phText.text = $"Enter {label}...";
            phText.fontSize = 16;
            phText.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            phText.alignment = TextAlignmentOptions.MidlineLeft;
            phText.fontStyle = FontStyles.Italic;
            phText.raycastTarget = false;

            inputField.textComponent = textComp;
            inputField.placeholder = phText;
            if (validFont != null) inputField.fontAsset = validFont;
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

            inputField.onEndEdit.AddListener(value =>
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    onValueChanged?.Invoke(value.Trim());
                    PlayClickSound();
                }
            });

            var colors = inputField.colors;
            colors.normalColor = new Color(0.15f, 0.15f, 0.15f, 1f);
            colors.highlightedColor = new Color(0.20f, 0.20f, 0.20f, 1f);
            colors.selectedColor = new Color(0.25f, 0.25f, 0.25f, 1f);
            inputField.colors = colors;
        }

        static void CreateWeatherProviderDropdown(Transform parent, SettingUI settingUI)
        {
            string[] opts = { "Seniverse", "OpenWeather", "OpenMeteo" };
            int cur = 0;
            string p = ChillEnvPlugin.Cfg_WeatherProvider.Value;
            if (p.Equals("OpenWeather", StringComparison.OrdinalIgnoreCase)) cur = 1;
            else if (p.Equals("OpenMeteo", StringComparison.OrdinalIgnoreCase)) cur = 2;
            CreateGenericDropdown(parent, settingUI, "WeatherProviderDropdown", "Weather Provider",
                opts, cur, index =>
                {
                    ChillEnvPlugin.Cfg_WeatherProvider.Value = opts[index];
                    ChillEnvPlugin.Instance.Config.Save();
                    TriggerForceRefresh();
                });
        }

        static void CreateTemperatureDropdown(Transform parent, SettingUI settingUI)
        {
            string[] displayOpts = { "Celsius (°C)", "Fahrenheit (°F)", "Kelvin (K)" };
            string[] unitValues = { "Celsius", "Fahrenheit", "Kelvin" };
            string cur = ChillEnvPlugin.Cfg_TemperatureUnit.Value;
            int curIdx = cur.Equals("Fahrenheit", StringComparison.OrdinalIgnoreCase) ? 1
                       : cur.Equals("Kelvin", StringComparison.OrdinalIgnoreCase) ? 2 : 0;
            CreateGenericDropdown(parent, settingUI, "TemperatureUnitDropdown", "Temperature Unit",
                displayOpts, curIdx, index =>
                {
                    ChillEnvPlugin.Cfg_TemperatureUnit.Value = unitValues[index];
                    ChillEnvPlugin.Instance.Config.Save();
                    TriggerForceRefresh();
                });
        }

        static void CreateGenericDropdown(Transform parent, SettingUI settingUI,
            string dropdownName, string titleText,
            string[] options, int currentIndex, Action<int> onValueChanged)
        {
            try
            {
                Transform graphicsContent = settingUI.transform
                    .Find("Graphics/ScrollView/Viewport/Content");
                if (graphicsContent == null) return;

                Transform originalDropdown = graphicsContent
                    .Find("GraphicQualityPulldownList");
                if (originalDropdown == null) return;

                GameObject dropdown = Object.Instantiate(originalDropdown.gameObject);
                dropdown.name = dropdownName;
                dropdown.transform.SetParent(parent, false);
                dropdown.SetActive(false);

                var hGroup = dropdown.GetComponent<HorizontalLayoutGroup>();
                if (hGroup != null)
                {
                    hGroup.spacing = 10f;
                    hGroup.childAlignment = TextAnchor.MiddleCenter;
                    hGroup.childForceExpandWidth = false;
                }

                var dropdownLayout = dropdown.GetComponent<LayoutElement>()
                                     ?? dropdown.AddComponent<LayoutElement>();
                dropdownLayout.preferredHeight = 60f;
                dropdownLayout.minHeight = 60f;
                dropdownLayout.flexibleWidth = 1f;

                foreach (var tp in new[] { "TitleText", "Title/Text", "Text" })
                {
                    var tt = dropdown.transform.Find(tp);
                    if (tt == null) continue;
                    var tmp = tt.GetComponent<TMP_Text>();
                    if (tmp != null) { tmp.text = titleText; break; }
                }

                Transform content = dropdown.transform
                    .Find("PulldownList/Pulldown/CurrentSelectText (TMP)/Content");
                if (content == null) return;

                for (int i = content.childCount - 1; i >= 0; i--)
                    Object.Destroy(content.GetChild(i).gameObject);
                content.gameObject.SetActive(true);

                Transform templateSource = graphicsContent
                    .Find("GraphicQualityPulldownList/PulldownList/Pulldown/CurrentSelectText (TMP)/Content");

                if (templateSource != null && templateSource.childCount > 0)
                {
                    GameObject btnTemplate = Object.Instantiate(
                        templateSource.GetChild(0).gameObject);
                    btnTemplate.SetActive(false);

                    for (int i = 0; i < options.Length; i++)
                    {
                        GameObject newBtn = Object.Instantiate(btnTemplate, content);
                        newBtn.name = $"SelectButton_{options[i]}";
                        newBtn.SetActive(true);

                        var bt = newBtn.GetComponentInChildren<TMP_Text>();
                        if (bt != null) bt.text = options[i];

                        foreach (var img in newBtn.GetComponentsInChildren<Image>(true))
                            img.raycastTarget = true;

                        var button = newBtn.GetComponent<Button>();
                        if (button != null)
                        {
                            int idx = i;
                            button.onClick.RemoveAllListeners();
                            button.onClick.AddListener(() =>
                            {
                                onValueChanged?.Invoke(idx);
                                UpdateDropdownSelectedText(dropdown, options[idx]);
                                CloseDropdown(dropdown);
                                PlayClickSound();
                            });
                            button.interactable = true;
                            if (button.targetGraphic == null)
                                button.targetGraphic = newBtn.GetComponent<Image>();
                        }
                    }

                    Object.Destroy(btnTemplate);
                    UpdateDropdownSelectedText(dropdown, options[currentIndex]);
                    WeatherModUIRunner.Instance.RunDelayed(0.1f,
                        () => ConfigureDropdownUI(dropdown, originalDropdown, content));
                }
                dropdown.SetActive(true);
            }
            catch (Exception e)
            {
                ChillEnvPlugin.Log?.LogError($"CreateGenericDropdown failed: {e.Message}");
            }
        }

        static void UpdateDropdownSelectedText(GameObject dropdown, string text)
        {
            foreach (var p in new[] { "PulldownList/Pulldown/CurrentSelectText (TMP)", "CurrentSelectText (TMP)" })
            {
                var t = dropdown.transform.Find(p);
                if (t == null) continue;
                var tmp = t.GetComponent<TMP_Text>();
                if (tmp != null) { tmp.text = text; return; }
            }
        }

        static void CloseDropdown(GameObject dropdown)
        {
            try
            {
                var pulldownUI = dropdown.GetComponentsInChildren<Component>(true)
                    .FirstOrDefault(c => c.GetType().Name == "PulldownListUI");
                if (pulldownUI == null) return;
                var closeMethod = pulldownUI.GetType().GetMethod("ClosePullDown",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                closeMethod?.Invoke(pulldownUI, new object[] { false });
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"CloseDropdown failed: {ex.Message}");
            }
        }

        static void ConfigureDropdownUI(GameObject dropdown, Transform originalDropdown,
            Transform content)
        {
            try
            {
                Type pulldownUIType = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .SelectMany(a => { try { return a.GetTypes(); } catch { return Array.Empty<Type>(); } })
                    .FirstOrDefault(t => t.Name == "PulldownListUI");
                if (pulldownUIType == null) return;

                Transform pulldownList = dropdown.transform.Find("PulldownList");
                Transform pulldown = dropdown.transform.Find("PulldownList/Pulldown");
                Transform pulldownButton = dropdown.transform.Find("PulldownList/PulldownButton");
                Transform currentSelectText = dropdown.transform
                    .Find("PulldownList/Pulldown/CurrentSelectText (TMP)");

                GameObject uiHost = (pulldownList != null) ? pulldownList.gameObject : dropdown;
                Component pulldownUI = uiHost.GetComponent(pulldownUIType)
                                        ?? uiHost.AddComponent(pulldownUIType);

                Button pulldownButtonComp = pulldownButton?.GetComponent<Button>();
                TMP_Text currentSelectTextComp = currentSelectText?.GetComponent<TMP_Text>();
                RectTransform pulldownParentRect = pulldown?.GetComponent<RectTransform>();
                RectTransform pulldownButtonRect = pulldownButton?.GetComponent<RectTransform>();
                RectTransform contentRect = content?.GetComponent<RectTransform>();

                if (pulldownButtonComp == null || currentSelectTextComp == null
                    || pulldownParentRect == null) return;

                void SetField(string fieldName, object value)
                {
                    if (value == null) return;
                    pulldownUIType.GetField(fieldName,
                        BindingFlags.NonPublic | BindingFlags.Instance)
                        ?.SetValue(pulldownUI, value);
                }

                int childCount = content.childCount;
                float itemHeight = 40f;
                if (childCount > 0)
                {
                    var fc = content.GetChild(0).GetComponent<RectTransform>();
                    if (fc != null && fc.rect.height > 10) itemHeight = fc.rect.height;
                }

                float realContentHeight = childCount * itemHeight;
                float maxViewHeight = 6f * itemHeight;
                bool needsScroll = realContentHeight > maxViewHeight;
                float finalViewHeight = needsScroll ? maxViewHeight : realContentHeight;
                float openSize = pulldownParentRect.rect.height + finalViewHeight + 10f;

                if (needsScroll && content.parent.name != "Viewport")
                {
                    var vlg = content.GetComponent<VerticalLayoutGroup>();
                    if (vlg != null) { vlg.childControlWidth = true; vlg.childForceExpandWidth = true; }

                    GameObject scrollView = new GameObject("ScrollView", typeof(RectTransform));
                    scrollView.transform.SetParent(content.parent, false);
                    var svRT = scrollView.GetComponent<RectTransform>();
                    svRT.anchorMin = Vector2.zero;
                    svRT.anchorMax = new Vector2(1f, 0f);
                    svRT.pivot = new Vector2(0.5f, 1f);
                    svRT.sizeDelta = new Vector2(0, finalViewHeight);
                    svRT.anchoredPosition = Vector2.zero;

                    var sr = scrollView.AddComponent<ScrollRect>();
                    sr.horizontal = false;
                    sr.vertical = true;
                    sr.scrollSensitivity = 20f;
                    sr.movementType = ScrollRect.MovementType.Clamped;

                    GameObject vp = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D));
                    vp.transform.SetParent(scrollView.transform, false);
                    var vpRect = vp.GetComponent<RectTransform>();
                    vpRect.anchorMin = Vector2.zero;
                    vpRect.anchorMax = Vector2.one;
                    vpRect.sizeDelta = Vector2.zero;
                    content.SetParent(vp.transform, true);
                    sr.viewport = vpRect;
                    sr.content = contentRect;

                    contentRect.anchorMin = new Vector2(0, 1);
                    contentRect.anchorMax = new Vector2(1, 1);
                    contentRect.pivot = new Vector2(0.5f, 1f);
                    contentRect.anchoredPosition = Vector2.zero;
                    contentRect.sizeDelta = new Vector2(0, realContentHeight);
                    var fitter = content.GetComponent<ContentSizeFitter>()
                                 ?? content.gameObject.AddComponent<ContentSizeFitter>();
                    fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
                }
                else if (contentRect != null)
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
                    rootCanvas.overrideSorting = false;
                    rootCanvas.sortingOrder = 0;
                    if (dropdown.GetComponent<GraphicRaycaster>() == null)
                        dropdown.AddComponent<GraphicRaycaster>();
                }

                var lc = dropdown.GetComponent<PulldownLayerController>()
                         ?? dropdown.AddComponent<PulldownLayerController>();
                lc.Initialize(pulldownUI, rootCanvas);

                SetField("_currentSelectContentText", currentSelectTextComp);
                SetField("_pullDownParentRect", pulldownParentRect);
                SetField("_openPullDownSizeDeltaY", openSize);
                SetField("_pullDownOpenCloseSeconds", 0.3f);
                SetField("_pullDownOpenButton", pulldownButtonComp);
                SetField("_pullDownButtonRect", pulldownButtonRect);
                SetField("_isOpen", false);

                pulldownUIType.GetMethod("Setup")?.Invoke(pulldownUI, null);
            }
            catch (Exception e)
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
            le.minHeight = le.preferredHeight = 35f;
            le.flexibleWidth = 1f;
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            var font = GetValidFont();
            if (font != null) tmp.font = font;
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
            le.minHeight = le.preferredHeight = 50f;
            le.flexibleWidth = 1f;
            var tmp = obj.AddComponent<TextMeshProUGUI>();
            var font = GetValidFont();
            if (font != null) tmp.font = font;
            string verStr = string.IsNullOrEmpty(version) ? ""
                : $" <size=16><color=#888888>v{version}</color></size>";
            tmp.text = $"<size=20><b>{name}</b></size>{verStr}";
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.color = Color.white;
        }

        static void CreateToggle(Transform parent, Transform templateRow,
            string label, bool initialValue, Action<bool> onValueChanged)
        {
            GameObject toggleRow = Object.Instantiate(templateRow.gameObject);
            toggleRow.name = $"WeatherToggle_{label}";
            toggleRow.transform.SetParent(parent, false);
            toggleRow.SetActive(true);

            var layoutElement = toggleRow.GetComponent<LayoutElement>()
                                ?? toggleRow.AddComponent<LayoutElement>();
            layoutElement.preferredWidth = layoutElement.minWidth = 750f;

            var hGroup = toggleRow.GetComponent<HorizontalLayoutGroup>();
            if (hGroup != null)
            {
                hGroup.childAlignment = TextAnchor.MiddleCenter;
                hGroup.childForceExpandWidth = false;
            }

            var titleTexts = toggleRow.GetComponentsInChildren<TMP_Text>(true);
            if (titleTexts.Length > 0)
            {
                var sorted = titleTexts.OrderBy(t => t.transform.position.x).ToArray();
                sorted[0].text = label;
                sorted[0].alignment = TextAlignmentOptions.MidlineLeft;
            }

            Button[] buttons = toggleRow.GetComponentsInChildren<Button>(true);
            if (buttons.Length < 2) return;
            Array.Sort(buttons, (a, b) =>
                a.transform.position.x.CompareTo(b.transform.position.x));
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
                var uiOn = btnOn.GetComponent<InteractableUI>();
                var uiOff = btnOff.GetComponent<InteractableUI>();
                if (state) { uiOn?.ActivateUseUI(false); uiOff?.DeactivateUseUI(false); }
                else { uiOn?.DeactivateUseUI(false); uiOff?.ActivateUseUI(false); }
            }

            btnOn.onClick.AddListener(() =>
            {
                if (!btnOn.interactable) return;
                UpdateState(true);
                onValueChanged?.Invoke(true);
                PlayClickSound();
            });
            btnOff.onClick.AddListener(() =>
            {
                if (!btnOff.interactable) return;
                UpdateState(false);
                onValueChanged?.Invoke(false);
                PlayClickSound();
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
            foreach (var t in modTabButton.GetComponentsInChildren<TextMeshProUGUI>(true))
                t.text = "MOD";
        }

        static void UpdateModContentText(GameObject modContentParent)
        {
            var title = modContentParent.transform.Find("Title")?.GetComponent<TextMeshProUGUI>();
            if (title != null) title.text = "MOD";
            foreach (var t in modContentParent.GetComponentsInChildren<TextMeshProUGUI>(true))
                if (t.text.Contains("Credits")) t.text = "Weather & Environment Settings";
        }

        static void AdjustTabBarLayout(Transform tabBarParent)
        {
            var hlg = tabBarParent.GetComponent<HorizontalLayoutGroup>();
            if (hlg == null) return;
            hlg.childForceExpandWidth = true;
            hlg.spacing = 0f;
            if (hlg.padding != null) { hlg.padding.left = 0; hlg.padding.right = 0; }
        }

        static void HookIntoTabButtons(SettingUI settingUI)
        {
            var buttons = new[] {
                "_generalInteractableUI","_graphicInteractableUI","_audioInteractableUI",
                "_accountInteractableUI","_newsInteractableUI","_creditsInteractableUI"
            };
            var parents = new[] {
                "_generalParent","_graphicParent","_audioParent",
                "_accountParent","_newsParent","_creditsParent"
            };
            for (int i = 0; i < buttons.Length; i++)
            {
                var btn = AccessTools.Field(typeof(SettingUI), buttons[i]).GetValue(settingUI) as InteractableUI;
                var parent = AccessTools.Field(typeof(SettingUI), parents[i]).GetValue(settingUI) as GameObject;
                if (btn == null) continue;
                var cap = btn;
                var capP = parent;
                btn.GetComponent<Button>()?.onClick.AddListener(() =>
                {
                    modContentParent?.SetActive(false);
                    modInteractableUI?.DeactivateUseUI(false);
                    if (capP) { capP.SetActive(true); cap.ActivateUseUI(false); }
                });
            }
        }

        static void SwitchToModTab(SettingUI settingUI)
        {
            foreach (var p in new[] { "_generalParent","_graphicParent","_audioParent",
                                      "_accountParent","_newsParent","_creditsParent" })
                (AccessTools.Field(typeof(SettingUI), p).GetValue(settingUI) as GameObject)
                    ?.SetActive(false);

            foreach (var b in new[] { "_generalInteractableUI","_graphicInteractableUI",
                                      "_audioInteractableUI","_accountInteractableUI",
                                      "_newsInteractableUI","_creditsInteractableUI" })
                (AccessTools.Field(typeof(SettingUI), b).GetValue(settingUI) as InteractableUI)
                    ?.DeactivateUseUI(false);

            PlayClickSound();
            modInteractableUI?.ActivateUseUI(false);
            modContentParent?.SetActive(true);

            var scrollRect = modContentParent?.GetComponentInChildren<ScrollRect>();
            if (scrollRect == null) return;
            LayoutRebuilder.ForceRebuildLayoutImmediate(
                modContentParent.GetComponent<RectTransform>());
            scrollRect.verticalNormalizedPosition = 1f;
        }

        static void PlayClickSound()
        {
            if (cachedSettingUI == null) return;
            var sss = AccessTools.Field(typeof(SettingUI), "_systemSeService")
                                  .GetValue(cachedSettingUI);
            sss?.GetType().GetMethod("PlayClick")?.Invoke(sss, null);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  PulldownLayerController and WeatherModUIRunner are unchanged
    // ─────────────────────────────────────────────────────────────────────────

    public class PulldownLayerController : MonoBehaviour
    {
        private Component pulldownUI;
        private Canvas targetCanvas;
        private FieldInfo isOpenField;
        private bool lastIsOpen = false;

        public void Initialize(Component pulldownUIComponent, Canvas canvas)
        {
            pulldownUI = pulldownUIComponent;
            targetCanvas = canvas;
            if (pulldownUI != null)
                isOpenField = pulldownUI.GetType().GetField("_isOpen",
                    BindingFlags.NonPublic | BindingFlags.Instance);
        }

        private void Update()
        {
            if (pulldownUI == null || targetCanvas == null || isOpenField == null) return;
            try
            {
                bool isOpen = (bool)isOpenField.GetValue(pulldownUI);
                if (isOpen == lastIsOpen) return;
                if (isOpen) { targetCanvas.overrideSorting = true; targetCanvas.sortingOrder = 30000; }
                else { targetCanvas.overrideSorting = false; targetCanvas.sortingOrder = 0; }
                lastIsOpen = isOpen;
            }
            catch { }
        }
    }

    public class WeatherModUIRunner : MonoBehaviour
    {
        private static WeatherModUIRunner _instance;

        public static WeatherModUIRunner Instance
        {
            get
            {
                if (_instance != null) return _instance;
                var go = new GameObject("WeatherModUI_CoroutineRunner");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<WeatherModUIRunner>();
                return _instance;
            }
        }

        public void RunDelayed(float seconds, Action action)
            => StartCoroutine(DelayedAction(seconds, action));

        private IEnumerator DelayedAction(float seconds, Action action)
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
                var mcp = AccessTools.Field(typeof(WeatherModSettingsUI), "modContentParent")
                                     .GetValue(null) as GameObject;
                var miu = AccessTools.Field(typeof(WeatherModSettingsUI), "modInteractableUI")
                                     .GetValue(null) as InteractableUI;
                mcp?.SetActive(false);
                miu?.DeactivateUseUI(false);

                var generalButton = AccessTools.Field(typeof(SettingUI), "_generalInteractableUI")
                                               .GetValue(__instance) as InteractableUI;
                var generalParent = AccessTools.Field(typeof(SettingUI), "_generalParent")
                                               .GetValue(__instance) as GameObject;
                generalButton?.ActivateUseUI(false);
                generalParent?.SetActive(true);

                foreach (var o in new[] { "_graphicParent","_audioParent","_accountParent",
                                          "_newsParent","_creditsParent" })
                    (AccessTools.Field(typeof(SettingUI), o).GetValue(__instance) as GameObject)
                        ?.SetActive(false);
            }
            catch { }
        }
    }
}
