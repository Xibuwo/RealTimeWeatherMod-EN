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
  [BepInPlugin("chillwithyou.envsync", "Chill Env Sync", "5.2.1")]
  public class ChillEnvPlugin : BaseUnityPlugin
  {
    internal static ChillEnvPlugin Instance;
    internal static ManualLogSource Log;
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
    internal static ConfigEntry<bool> Cfg_UnlockEnvironments;
    internal static ConfigEntry<bool> Cfg_UnlockDecorations;
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

      Log.LogInfo("【5.2.1】Starting - Weather, Sunrise & Sunset Edition (OpenWeather Support)");

      try
      {
        var harmony = new Harmony("ChillWithYou.EnvSync");
        harmony.PatchAll();
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
      Cfg_WeatherRefreshMinutes = Config.Bind("WeatherSync", "RefreshMinutes", 10, "Weather API refresh interval (minutes)");
      Cfg_SunriseTime = Config.Bind("TimeConfig", "Sunrise", "06:30", "Sunrise time");
      Cfg_SunsetTime = Config.Bind("TimeConfig", "Sunset", "18:30", "Sunset time");
      Cfg_GeneralAPI = Config.Bind("WeatherAPI", "GeneralAPI", "", "General API Key, put your API Key here (The GeneralAPI Key is the same as APIKey, which means, you have to put your API on both)");

      Cfg_EnableWeatherSync = Config.Bind("WeatherAPI", "EnableWeatherSync", true, "Enable weather API sync");
      Cfg_WeatherProvider = Config.Bind("WeatherAPI", "WeatherProvider", "OpenWeather", "Weather provider: Seniverse or OpenWeather");
      Cfg_ApiKey = Config.Bind("WeatherAPI", "ApiKey", "", "API Key (Same as GeneralAPI, Seniverse or OpenWeather)");
      Cfg_Location = Config.Bind("WeatherAPI", "Location", "Madrid", "Location (city name for Seniverse and for OpenWeather as well. You can use lon,lat for OpenWeather)");

      Cfg_UnlockEnvironments = Config.Bind("Unlock", "UnlockAllEnvironments", true, "Auto unlock environments");
      Cfg_UnlockDecorations = Config.Bind("Unlock", "UnlockAllDecorations", true, "Auto unlock decorations");

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
    }

    internal static void CallServiceChangeWeather(EnvironmentType envType)
    {
      if (WindowViewServiceInstance == null || ChangeWeatherMethod == null) return;
      try
      {
        var parameters = ChangeWeatherMethod.GetParameters();
        if (parameters.Length == 0) return;
        Type windowViewEnumType = parameters[0].ParameterType;
        object enumValue = Enum.Parse(windowViewEnumType, envType.ToString());
        ChangeWeatherMethod.Invoke(WindowViewServiceInstance, new object[] { enumValue });
      }
      catch (Exception ex) { Log?.LogError($"Service call failed: {ex.Message}"); }
    }

    internal static void SimulateClickMainIcon(EnviromentController ctrl)
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
      try
      {
        var decoProp = svc.GetType().GetProperty("Decoration");
        if (decoProp == null) return;
        var unlockDecoObj = decoProp.GetValue(svc);
        if (unlockDecoObj == null) return;
        var dictField = unlockDecoObj.GetType().GetField("_decorationDic", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (dictField == null) return;
        var dict = dictField.GetValue(unlockDecoObj) as System.Collections.IDictionary;
        if (dict == null) return;
        int count = 0;
        foreach (System.Collections.DictionaryEntry entry in dict)
        {
          var data = entry.Value;
          var lockField = data.GetType().GetField("_isLocked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
          if (lockField == null) continue;
          var reactive = lockField.GetValue(data);
          if (reactive == null) continue;
          var propValue = reactive.GetType().GetProperty("Value");
          if (propValue == null) continue;
          propValue.SetValue(reactive, false, null);
          count++;
        }
        Log?.LogInfo($"✅ Unlocked {count} decorations");
      }
      catch { }
    }
  }
}
