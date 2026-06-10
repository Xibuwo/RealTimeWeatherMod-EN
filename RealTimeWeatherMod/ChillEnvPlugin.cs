using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Bulbul;

namespace ChillWithYou.EnvSync
{
    [BepInPlugin("chillwithyou.envsync", "Chill Env Sync", PluginVersion)]
    public class ChillEnvPlugin : BaseUnityPlugin
    {
        internal static ChillEnvPlugin Instance;
        internal static ManualLogSource Log;
        public const string PluginVersion = "5.5.0";
        internal static UnlockItemService UnlockItemServiceInstance;

        internal static object WindowViewServiceInstance;
        internal static MethodInfo ChangeWeatherMethod;
        internal static string UIWeatherString = "";
        internal static bool Initialized;

        // --- Configuration ---
        internal static ConfigEntry<int> Cfg_WeatherRefreshMinutes;
        internal static ConfigEntry<string> Cfg_SunriseTime;
        internal static ConfigEntry<string> Cfg_SunsetTime;
        internal static ConfigEntry<string> Cfg_ApiKey;
        internal static ConfigEntry<string> Cfg_Location;
        internal static ConfigEntry<bool> Cfg_EnableWeatherSync;
        internal static ConfigEntry<bool> Cfg_EnableTimeSync;
        internal static ConfigEntry<bool> Cfg_UnlockEnvironments;
        internal static ConfigEntry<bool> Cfg_UnlockDecorations;
        internal static ConfigEntry<bool> Cfg_UnlockPurchasableItems;
        internal static ConfigEntry<string> Cfg_WeatherProvider;
        internal static ConfigEntry<string> Cfg_GeneralAPI;

        // UI Configuration
        internal static ConfigEntry<bool> Cfg_ShowWeatherOnUI;
        internal static ConfigEntry<bool> Cfg_DetailedTimeSegments;
        internal static ConfigEntry<string> Cfg_TemperatureUnit;

        internal static ConfigEntry<bool> Cfg_EnableEasterEggs;

        // Debug Configuration
        internal static ConfigEntry<bool> Cfg_DebugMode;
        internal static ConfigEntry<int> Cfg_DebugCode;
        internal static ConfigEntry<int> Cfg_DebugTemp;
        internal static ConfigEntry<string> Cfg_DebugText;

        // [Hidden] Last sync date for sunrise/sunset
        internal static ConfigEntry<string> Cfg_LastSunSyncDate;

        private static GameObject _runnerGO;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Log.LogInfo($"【{PluginVersion}】Starting - Weather, Sunrise & Sunset Edition (International Support)");


            try
            {
                var harmony = new Harmony("ChillWithYou.EnvSync");
                harmony.PatchAll();
                Patches.UnlockConditionGodMode.ApplyPatches(harmony);
            }
            catch (Exception ex)
            {
                Log.LogError($"Harmony failed: {ex}");
            }

            InitConfig();

            try
            {
                _runnerGO = new GameObject("ChillEnvSyncRunner");
                _runnerGO.hideFlags = HideFlags.HideAndDontSave;
                DontDestroyOnLoad(_runnerGO);
                _runnerGO.SetActive(true);

                _runnerGO.AddComponent<Core.AutoEnvRunner>();
                _runnerGO.AddComponent<Core.SceneryAutomationSystem>();
            }
            catch (Exception ex)
            {
                Log.LogError($"Runner creation failed: {ex}");
            }
        }

        private void InitConfig()
        {
            Cfg_WeatherRefreshMinutes = Config.Bind("WeatherSync", "RefreshMinutes", 15, "Weather API refresh interval (minutes)");
            Cfg_SunriseTime = Config.Bind("TimeConfig", "Sunrise", "06:30", "Sunrise time");
            Cfg_SunsetTime = Config.Bind("TimeConfig", "Sunset", "18:30", "Sunset time");
            Cfg_GeneralAPI = Config.Bind("WeatherAPI", "GeneralAPI", "fb54bc28f5545a10b8e5421869cf3bc5", "General API Key, put your API Key here (The GeneralAPI Key is the same as APIKey, which means, you have to put your API on both)");

            Cfg_EnableWeatherSync = Config.Bind("WeatherAPI", "EnableWeatherSync", true, "Enable weather API sync");
            Cfg_EnableTimeSync = Config.Bind("TimeSync", "EnableTimeSync", true, "Enable time-based day/night cycling (works without an API key)");
            Cfg_WeatherProvider = Config.Bind("WeatherAPI", "WeatherProvider", "OpenMeteo", "Weather provider: Seniverse, OpenWeather, or OpenMeteo");
            Cfg_ApiKey = Config.Bind("WeatherAPI", "ApiKey", "fb54bc28f5545a10b8e5421869cf3bc5", "API Key (Same as GeneralAPI, Seniverse or OpenWeather) (OpenMeteo doesn't require an API Key)");
            Cfg_Location = Config.Bind("WeatherAPI", "Location", "Madrid", "Location (city name or you can use lon,lat)");

            Cfg_UnlockEnvironments = Config.Bind("Unlock", "UnlockAllEnvironments", true, "Auto unlock environments");
            Cfg_UnlockDecorations = Config.Bind("Unlock", "UnlockAllDecorations", true, "Auto unlock decorations");
            Cfg_UnlockPurchasableItems = Config.Bind("Unlock", "UnlockPurchasableItems", false, "Auto unlock purchasable items");

            Cfg_ShowWeatherOnUI = Config.Bind("UI", "ShowWeatherOnDate", true, "Show weather on date bar");
            Cfg_DetailedTimeSegments = Config.Bind("UI", "DetailedTimeSegments", true, "Show detailed time segments in 12-hour format");
            Cfg_TemperatureUnit = Config.Bind("UI", "TemperatureUnit", "Celsius", "Temperature unit: Celsius, Fahrenheit, or Kelvin");

            Cfg_EnableEasterEggs = Config.Bind("Automation", "EnableSeasonalEasterEggs", true, "Enable seasonal easter eggs & automatic environment sound management");

            Cfg_DebugMode = Config.Bind("Debug", "EnableDebugMode", false, "Debug mode");
            Cfg_DebugCode = Config.Bind("Debug", "SimulatedCode", 1, "Simulated weather code");
            Cfg_DebugTemp = Config.Bind("Debug", "SimulatedTemp", 25, "Simulated temperature");
            Cfg_DebugText = Config.Bind("Debug", "SimulatedText", "DebugWeather", "Simulated description");

            Cfg_LastSunSyncDate = Config.Bind("Internal", "LastSunSyncDate", "", "Last sync date");
        }

        internal static void TryInitializeOnce(UnlockItemService svc)
        {
            if (Initialized || svc == null) return;

            if (Cfg_UnlockEnvironments.Value) ForceUnlockAllEnvironments(svc);
            if (Cfg_UnlockDecorations.Value) ForceUnlockAllDecorations(svc);

            Initialized = true;
            Log?.LogInfo("Initialization complete");

            if (Cfg_DebugMode.Value && Instance != null)
            {
                Instance.StartCoroutine(VerifyUnlockAfterDelay(svc, 3f));
            }
        }

        private static System.Collections.IEnumerator VerifyUnlockAfterDelay(UnlockItemService svc, float delay)
        {
            yield return new WaitForSeconds(delay);
            Log?.LogInfo($"[Debug] Verifying unlock status after {delay} seconds...");

            int lockedEnvCount = 0;
            int lockedDecoCount = 0;

            try
            {
                var envProp = svc.GetType().GetProperty("Environment");
                var unlockEnvObj = envProp.GetValue(svc);
                var dictField = unlockEnvObj.GetType().GetField("_environmentDic", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var dict = dictField.GetValue(unlockEnvObj) as System.Collections.IDictionary;

                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var data = entry.Value;
                    var lockField = data.GetType().GetField("_isLocked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var reactive = lockField.GetValue(data);
                    var propValue = reactive.GetType().GetProperty("Value");
                    bool isLocked = (bool)propValue.GetValue(reactive, null);
                    if (isLocked)
                    {
                        lockedEnvCount++;
                        Log?.LogWarning($"[Debug] ⚠️ Environment {entry.Key} was re-locked!");
                    }
                }
            }
            catch (Exception ex) { Log?.LogError($"[Debug] Environment verification failed: {ex.Message}"); }

            try
            {
                var decoProp = svc.GetType().GetProperty("Decoration");
                var unlockDecoObj = decoProp?.GetValue(svc);
                if (unlockDecoObj != null)
                {
                    var dictField = unlockDecoObj.GetType().GetField("_decorationDic", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var dict = dictField?.GetValue(unlockDecoObj) as System.Collections.IDictionary;

                    if (dict != null)
                    {
                        foreach (System.Collections.DictionaryEntry entry in dict)
                        {
                            var data = entry.Value;
                            var lockField = data.GetType().GetField("_isLocked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                            var reactive = lockField?.GetValue(data);
                            if (reactive != null)
                            {
                                var propValue = reactive.GetType().GetProperty("Value");
                                bool isLocked = (bool)propValue.GetValue(reactive, null);
                                if (isLocked)
                                {
                                    lockedDecoCount++;
                                    Log?.LogWarning($"[Debug] ⚠️ Decoration {entry.Key} was re-locked!");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log?.LogError($"[Debug] Decoration verification failed: {ex.Message}"); }

            if (lockedEnvCount == 0 && lockedDecoCount == 0)
            {
                Log?.LogInfo($"[Debug] ✅ Verification passed: All unlock statuses remain normal");
            }
            else
            {
                Log?.LogError($"[Debug] ❌ Issues found: {lockedEnvCount} environments and {lockedDecoCount} decorations were re-locked");
                Log?.LogError($"[Debug] Possible cause: Game reloaded save data after initialization");
            }
        }
        internal static void CallServiceChangeWeather(EnvironmentType envType)
        {
            MonoBehaviour targetUI = null;

            Type uiType = AccessTools.TypeByName("Bulbul.EnvironmentUI");
            if (uiType != null)
            {
                var allUIs = UnityEngine.Resources.FindObjectsOfTypeAll(uiType);
                if (allUIs != null && allUIs.Length > 0)
                {
                    foreach (var obj in allUIs)
                    {
                        var mono = obj as MonoBehaviour;
                        if (mono != null && mono.gameObject.scene.rootCount != 0)
                        {
                            targetUI = mono;
                            break;
                        }
                    }
                }
            }

            if (targetUI == null) return;

            try
            {
                var method = AccessTools.Method(targetUI.GetType(), "OnClickButtonChangeTime");
                if (method != null)
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length > 0)
                    {
                        Type targetEnumType = parameters[0].ParameterType;
                        object enumValue = Enum.Parse(targetEnumType, envType.ToString());
                        method.Invoke(targetUI, new object[] { enumValue });
                        Log?.LogInfo($"[Service] Environment switched: {envType}");
                    }
                }
                else
                {
                    Log?.LogError("[Service] ❌ The OnClickButtonChangeTime method was not found");
                }
            }
            catch (Exception ex)
            {
                Log?.LogError($"[Service] ❌ Call failed: { ex.Message}");
            }
        }

        internal static void SimulateClickMainIcon(EnvironmentController ctrl)
        {
            if (ctrl == null) return;
            try
            {
                Log?.LogInfo($"[SimulateClick] Preparing to click: {ctrl.name} (Type: {ctrl.GetType().Name})");
                MethodInfo clickMethod = ctrl.GetType().GetMethod("OnClickButtonMainIcon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (clickMethod != null)
                {
                    Patches.UserInteractionPatch.IsSimulatingClick = true;
                    clickMethod.Invoke(ctrl, null);
                    Patches.UserInteractionPatch.IsSimulatingClick = false;
                    Log?.LogInfo($"[SimulateClick] Click invoked: {ctrl.name}");
                }
                else
                {
                    Log?.LogError($"[SimulateClick] ❌ OnClickButtonMainIcon method not found: {ctrl.name}");
                }
            }
            catch (Exception ex) { Log?.LogError($"Simulated click failed: {ex.Message}"); }
        }

        private static void ForceUnlockAllEnvironments(UnlockItemService svc)
        {
            try
            {
                var envProp = svc.GetType().GetProperty("Environment");
                var unlockEnvObj = envProp.GetValue(svc);
                var dictField = unlockEnvObj.GetType().GetField("_environmentDic", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var dict = dictField.GetValue(unlockEnvObj) as System.Collections.IDictionary;
                int count = 0;
                foreach (System.Collections.DictionaryEntry entry in dict)
                {
                    var data = entry.Value;
                    var lockField = data.GetType().GetField("_isLocked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var reactive = lockField.GetValue(data);
                    var propValue = reactive.GetType().GetProperty("Value");
                    propValue.SetValue(reactive, false, null);
                    count++;
                }
                Log?.LogInfo($"✅ Unlocked {count} environments");
            }
            catch { }
        }
        private static void ForceUnlockAllDecorations(UnlockItemService svc)
        {
            if (svc == null) return;

            Log?.LogInfo("☢️ Launching Universal Unlock Nuclear Bomb v2 (Drill Mode)...");
            int totalUnlocked = 0;

            try
            {
                var serviceFields = AccessTools.GetDeclaredFields(svc.GetType());

                foreach (var field in serviceFields)
                {
                    if (field.FieldType.Name.Contains("MasterData") || field.FieldType.Name.Contains("Loader"))
                        continue;

                    object componentObj = field.GetValue(svc);
                    if (componentObj == null) continue;

                    var subFields = AccessTools.GetDeclaredFields(componentObj.GetType());

                    foreach (var subField in subFields)
                    {
                        if (!typeof(System.Collections.IDictionary).IsAssignableFrom(subField.FieldType)) continue;

                        var dict = subField.GetValue(componentObj) as System.Collections.IDictionary;
                        if (dict == null || dict.Count == 0) continue;

                        int groupCount = 0;

                        foreach (System.Collections.DictionaryEntry entry in dict)
                        {
                            var dataItem = entry.Value;
                            if (dataItem == null) continue;

                            var lockField = AccessTools.Field(dataItem.GetType(), "_isLocked");
                            if (lockField == null) continue;

                            var reactiveBool = lockField.GetValue(dataItem);
                            if (reactiveBool == null) continue;

                            var valueProp = reactiveBool.GetType().GetProperty("Value");
                            if (valueProp == null) continue;

                            bool isLocked = (bool)valueProp.GetValue(reactiveBool, null);
                            if (isLocked)
                            {
                                valueProp.SetValue(reactiveBool, false, null);
                                groupCount++;
                                totalUnlocked++;
                                if (Cfg_DebugMode.Value)
                                {
                                    Log?.LogInfo($"   🔓 Unlocked: {entry.Key} (in {field.Name}.{subField.Name})");
                                }
                            }
                        }

                        if (groupCount > 0)
                        {
                            Log?.LogInfo($"✅ Unlocked {groupCount} items in {field.Name} -> {subField.Name}");
                        }
                    }
                }
                Log?.LogInfo($"🎉 Nuclear Bomb v2 deployment complete, total {totalUnlocked} items unlocked!");
            }
            catch (Exception ex)
            {
                Log?.LogError($"❌ Universal Unlock v2 failed: {ex}");
            }
        }
        internal static bool IsInCutscene()
        {
            try
            {
                RoomGameManager roomGM = UnityEngine.GameObject.FindObjectOfType<RoomGameManager>();
                if (roomGM == null)
                {
                    var all = UnityEngine.Resources.FindObjectsOfTypeAll<RoomGameManager>();
                    if (all == null || all.Length == 0) return false;
                    roomGM = all[0];
                }

                if (roomGM == null) return false;

                var prop = typeof(RoomGameManager).GetProperty(
                    "CurrentMainState",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null) return false;

                object stateObj = prop.GetValue(roomGM, null);
                if (stateObj == null) return false;

                int state;
                if (stateObj is int)
                    state = (int)stateObj;
                else if (stateObj.GetType().IsEnum)
                    state = Convert.ToInt32(stateObj);
                else if (!int.TryParse(stateObj.ToString(), out state))
                    return false;

                // 14 = normal gameplay state; anything else = cutscene / loading / menu
                return state != 14;
            }
            catch
            {
                return false;
            }
        }
    }
}
