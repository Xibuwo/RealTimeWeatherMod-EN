using System;
using Bulbul;
using HarmonyLib;
using UnityEngine;
using ChillWithYou.EnvSync.Models;
using ChillWithYou.EnvSync.Services;
using ChillWithYou.EnvSync.Utils;

namespace ChillWithYou.EnvSync.Core
{
    public class AutoEnvRunner : MonoBehaviour
    {
        private float _nextWeatherCheckTime;
        private float _nextTimeCheckTime;
        private EnvironmentType? _lastAppliedEnv;
        private bool _isFetching;
        private bool _pendingForceRefresh;

        private bool _firstSyncDone;
        private bool _initialEnvApplied;

        private static AutoEnvRunner _instance;

        private static readonly EnvironmentType[] BaseEnvironments =
            new[] { EnvironmentType.Day, EnvironmentType.Sunset,
                    EnvironmentType.Night, EnvironmentType.Cloudy };

        private static readonly EnvironmentType[] SceneryWeathers =
            new[] { EnvironmentType.ThunderRain, EnvironmentType.HeavyRain,
                    EnvironmentType.LightRain, EnvironmentType.Snow };

        private static readonly EnvironmentType[] MainEnvironments =
            new[] { EnvironmentType.Day, EnvironmentType.Sunset,
                    EnvironmentType.Night, EnvironmentType.Cloudy,
                    EnvironmentType.LightRain, EnvironmentType.HeavyRain,
                    EnvironmentType.ThunderRain, EnvironmentType.Snow };

        // ─────────────────────────────────────────────────────────────
        // Lifecycle
        // ─────────────────────────────────────────────────────────────

        private void Start()
        {
            _instance = this;
            _nextWeatherCheckTime = Time.time + 10f;
            _nextTimeCheckTime = Time.time + 10f;
            ChillEnvPlugin.Log?.LogInfo("Runner starting...");

            CheckAndSyncSunSchedule();
            StartCoroutine(EarlyStartupSync());
        }

        // ─────────────────────────────────────────────────────────────
        // Policy helpers
        // ─────────────────────────────────────────────────────────────

        private SyncPolicySnapshot BuildPolicySnapshot()
        {
            return SyncPolicy.Build(
                ChillEnvPlugin.Cfg_EnableTimeSync.Value,
                ChillEnvPlugin.Cfg_EnableWeatherSync.Value,
                ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value);
        }

        private bool HasUsableApiKey()
        {
            string apiKey = ChillEnvPlugin.Cfg_ApiKey.Value;
            return !string.IsNullOrEmpty(apiKey) || WeatherService.HasDefaultKey;
        }

        private void UpdateUiWeatherString(WeatherInfo weather)
        {
            ChillEnvPlugin.UIWeatherString = WeatherUiState.NextWeatherText(
                ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value,
                ChillEnvPlugin.UIWeatherString,
                weather);
        }

        private float GetConfiguredWeatherRefreshSeconds()
        {
            return Mathf.Max(1, ChillEnvPlugin.Cfg_WeatherRefreshMinutes.Value) * 60f;
        }

        private void ScheduleDefaultWeatherCheck()
        {
            _nextWeatherCheckTime = Time.time + GetConfiguredWeatherRefreshSeconds();
        }

        private void ScheduleNextWeatherCheckFromCache(string location)
        {
            float remainingSeconds;
            if (WeatherService.TryGetCacheRemainingSeconds(location, out remainingSeconds))
            {
                // Wake up just after the cache expires; at least 5 s in the future
                _nextWeatherCheckTime = Time.time + Mathf.Max(5f, remainingSeconds + 1f);
                return;
            }
            ScheduleDefaultWeatherCheck();
        }

        // ─────────────────────────────────────────────────────────────
        // EarlyStartupSync — fixes the 15-second first-load delay
        // ─────────────────────────────────────────────────────────────

        private System.Collections.IEnumerator EarlyStartupSync()
        {
            var policy = BuildPolicySnapshot();
            bool hasKey = HasUsableApiKey();
            string loc = ChillEnvPlugin.Cfg_Location.Value;
            bool needFetch = (policy.CanControlWeather || policy.CanFetchWeatherForUI) && hasKey;

            // Pre-fetch weather in the background so data is ready when the UI appears
            if (needFetch && !WeatherService.HasValidCache(loc))
            {
                StartCoroutine(WeatherService.FetchWeather(
                    ChillEnvPlugin.Cfg_ApiKey.Value, loc, false,
                    (w) => { UpdateUiWeatherString(w); }));
            }

            // Poll until EnvironmentUI is present in the active scene (not just an asset)
            System.Type uiType = AccessTools.TypeByName("Bulbul.EnvironmentUI");
            MonoBehaviour envUI = null;
            float pollTimeout = 30f;

            while (envUI == null && pollTimeout > 0f)
            {
                if (uiType != null)
                {
                    var allUIs = UnityEngine.Resources.FindObjectsOfTypeAll(uiType);
                    if (allUIs != null)
                    {
                        foreach (var obj in allUIs)
                        {
                            var mono = obj as MonoBehaviour;
                            if (mono != null && mono.gameObject.scene.rootCount != 0)
                            {
                                envUI = mono;
                                break;
                            }
                        }
                    }
                }

                if (envUI == null)
                {
                    yield return new WaitForSeconds(0.1f);
                    pollTimeout -= 0.1f;
                }
            }

            // Apply the very first environment change via ChangeTime (no click noise)
            if (envUI != null && policy.CanControlTime && !ChillEnvPlugin.IsInCutscene())
            {
                var changeTimeMethod = AccessTools.Method(envUI.GetType(), "ChangeTime");
                if (changeTimeMethod != null)
                {
                    EnvironmentType target = GetTimeBasedEnvironment();

                    // If weather data already arrived, apply cloudy override now
                    if (policy.CanApplyCloudyOverride
                        && WeatherService.CachedWeather != null
                        && IsBadWeather(WeatherService.CachedWeather.Code)
                        && target != EnvironmentType.Night)
                    {
                        target = EnvironmentType.Cloudy;
                    }

                    try
                    {
                        var paramType = changeTimeMethod.GetParameters()[0].ParameterType;
                        object enumVal = Enum.Parse(paramType, target.ToString());
                        changeTimeMethod.Invoke(envUI, new object[] { enumVal });
                        _initialEnvApplied = true;
                        ChillEnvPlugin.Log?.LogInfo($"[Startup] Zero-click initial env: {target}");
                    }
                    catch (Exception ex)
                    {
                        ChillEnvPlugin.Log?.LogError($"[Startup] ChangeTime failed: {ex.Message}");
                    }
                }
            }

            // Wait until EnvRegistry is populated AND weather data is ready (if needed)
            float readyTimeout = 30f;
            while (readyTimeout > 0f)
            {
                bool gameReady = EnvRegistry.Count > 0 && !ChillEnvPlugin.IsInCutscene();
                bool dataReady = !needFetch || WeatherService.CachedWeather != null;

                if (gameReady && dataReady && !_firstSyncDone)
                    break;

                yield return new WaitForSeconds(0.5f);
                readyTimeout -= 0.5f;
            }

            if (!_firstSyncDone && EnvRegistry.Count > 0 && !ChillEnvPlugin.IsInCutscene())
            {
                _firstSyncDone = true;
                ChillEnvPlugin.Log?.LogInfo("[Startup] First full sync");
                TriggerSync(false, !_initialEnvApplied);

                if (hasKey)
                    ScheduleNextWeatherCheckFromCache(loc);
            }
            else if (!_firstSyncDone)
            {
                ChillEnvPlugin.Log?.LogWarning(
                    "[Startup] Timed out waiting for game ready; Update timer will catch up");
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Sun schedule sync
        // ─────────────────────────────────────────────────────────────

        private void CheckAndSyncSunSchedule()
        {
            if (!ChillEnvPlugin.Cfg_EnableWeatherSync.Value) return;

            string lastSync = ChillEnvPlugin.Cfg_LastSunSyncDate.Value;
            string today = DateTime.Now.ToString("dd-MM-yyyy");

            if (lastSync != today)
                StartCoroutine(SyncSunScheduleRoutine(today));
        }

        private System.Collections.IEnumerator SyncSunScheduleRoutine(string targetDate)
        {
            int retryCount = 0;
            float delay = 1f;
            const int MaxRetries = 10;

            while (retryCount < MaxRetries)
            {
                bool success = false;
                string apiKey = ChillEnvPlugin.Cfg_GeneralAPI.Value;
                string location = ChillEnvPlugin.Cfg_Location.Value;

                yield return WeatherService.FetchSunSchedule(apiKey, location, (data) =>
                {
                    if (data != null)
                    {
                        ChillEnvPlugin.Log?.LogInfo(
                            $"[SunSync] OK: sunrise {data.sunrise} sunset {data.sunset}");

                        ChillEnvPlugin.Cfg_SunriseTime.Value = data.sunrise;
                        ChillEnvPlugin.Cfg_SunsetTime.Value = data.sunset;
                        ChillEnvPlugin.Cfg_LastSunSyncDate.Value = targetDate;
                        ChillEnvPlugin.Instance.Config.Save();
                        success = true;
                    }
                });

                if (success) yield break;

                ChillEnvPlugin.Log?.LogWarning(
                    $"[SunSync] Failed, retry in {delay}s ({retryCount + 1}/{MaxRetries})");
                yield return new WaitForSeconds(delay);
                delay *= 2f;
                retryCount++;
            }

            ChillEnvPlugin.Log?.LogError("[SunSync] Max retries reached, giving up for today.");
        }

        // ─────────────────────────────────────────────────────────────
        // Update loop
        // ─────────────────────────────────────────────────────────────

        private void Update()
        {
            if (!ChillEnvPlugin.Initialized || EnvRegistry.Count == 0) return;

            if (Input.GetKeyDown(KeyCode.F9))
            {
                ChillEnvPlugin.Log?.LogInfo("F9: Force Sync");
                TriggerSync(false, true);
            }
            if (Input.GetKeyDown(KeyCode.F8)) ShowStatus();
            if (Input.GetKeyDown(KeyCode.F7))
            {
                ChillEnvPlugin.Log?.LogInfo("F7: Force Refresh");
                ChillEnvPlugin.Instance.Config.Reload();
                ForceRefreshWeather();
            }

            if (Time.time >= _nextTimeCheckTime)
            {
                _nextTimeCheckTime = Time.time + 30f;
                TriggerSync(false, false);
            }

            if (Time.time >= _nextWeatherCheckTime)
                TriggerSync(true, false);
        }

        // ─────────────────────────────────────────────────────────────
        // Public static entry points
        // ─────────────────────────────────────────────────────────────

        public static void TriggerImmediateSync()
        {
            if (_instance != null && !_instance._firstSyncDone)
                _instance.StartCoroutine(_instance.WaitAndSyncFallback());
        }

        private System.Collections.IEnumerator WaitAndSyncFallback()
        {
            float timeout = 15f;
            while ((EnvRegistry.Count == 0 || ChillEnvPlugin.IsInCutscene()) && timeout > 0f)
            {
                yield return new WaitForSeconds(0.5f);
                timeout -= 0.5f;
            }

            if (!_firstSyncDone && EnvRegistry.Count > 0 && !ChillEnvPlugin.IsInCutscene())
            {
                _firstSyncDone = true;
                TriggerSync(false, true);
            }
        }

        public static void TriggerWeatherRefresh()
        {
            if (_instance != null)
            {
                ChillEnvPlugin.Log?.LogInfo("🔄 External weather refresh triggered");
                _instance.ForceRefreshWeather();
            }
        }

        public static void TriggerUiWeatherRefresh()
        {
            if (_instance != null)
            {
                ChillEnvPlugin.Log?.LogInfo("🌤️ External UI weather refresh triggered");
                _instance.TriggerSync(false, false);
            }
        }

        public static void TriggerSunScheduleRefresh()
        {
            if (_instance != null)
            {
                ChillEnvPlugin.Log?.LogInfo("🌅 External sun schedule refresh triggered");
                string today = DateTime.Now.ToString("dd-MM-yyyy");
                _instance.StartCoroutine(_instance.SyncSunScheduleRoutine(today));
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Status / debug
        // ─────────────────────────────────────────────────────────────

        private void ShowStatus()
        {
            var now = DateTime.Now;
            ChillEnvPlugin.Log?.LogInfo($"--- Status [{now:HH:mm:ss}] ---");
            ChillEnvPlugin.Log?.LogInfo($"Plugin log: {_lastAppliedEnv}");
            ChillEnvPlugin.Log?.LogInfo($"Game actual: {GetCurrentActiveEnvironment()}");
            ChillEnvPlugin.Log?.LogInfo($"UI text: {ChillEnvPlugin.UIWeatherString}");
            if (ChillEnvPlugin.Cfg_DebugMode.Value)
                ChillEnvPlugin.Log?.LogWarning("【Warning】Debug mode is ON!");
        }

        // ─────────────────────────────────────────────────────────────
        // Force refresh
        // ─────────────────────────────────────────────────────────────

        private void ForceRefreshWeather()
        {
            if (_isFetching)
            {
                _pendingForceRefresh = true;
                ChillEnvPlugin.Log?.LogInfo(
                    "ForceRefresh queued — fetch already in progress");
                return;
            }
            ScheduleDefaultWeatherCheck();
            TriggerSync(true, false);
        }

        private void HandlePendingForceRefresh()
        {
            if (_pendingForceRefresh)
            {
                _pendingForceRefresh = false;
                ForceRefreshWeather();
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Core sync dispatcher
        // ─────────────────────────────────────────────────────────────

        private void TriggerSync(bool forceApi, bool forceApply)
        {
            // Cutscene guard — pause all environment mutation during story scenes
            if (ChillEnvPlugin.IsInCutscene())
            {
                _nextTimeCheckTime = Time.time + 30f;
                _nextWeatherCheckTime = Time.time + 30f;
                return;
            }

            var policy = BuildPolicySnapshot();
            bool hasKey = HasUsableApiKey();
            string location = ChillEnvPlugin.Cfg_Location.Value;
            bool hasCache = WeatherService.HasValidCache(location);

            // Keep UI text cleared when the feature is off
            if (!ChillEnvPlugin.Cfg_ShowWeatherOnUI.Value)
                ChillEnvPlugin.UIWeatherString = string.Empty;

            // ── Debug mode: use mock data ──────────────────────────────
            if (ChillEnvPlugin.Cfg_DebugMode.Value)
            {
                ChillEnvPlugin.Log?.LogWarning("[Debug] Using mock weather data");
                int code = ChillEnvPlugin.Cfg_DebugCode.Value;
                var mock = new WeatherInfo
                {
                    Code = code,
                    Temperature = ChillEnvPlugin.Cfg_DebugTemp.Value,
                    Text = ChillEnvPlugin.Cfg_DebugText.Value,
                    Condition = WeatherService.MapCodeToCondition(code),
                    UpdateTime = DateTime.Now
                };
                UpdateUiWeatherString(mock);
                if (policy.CanMutateEnvironment)
                    ApplyByPolicy(policy, mock, forceApply);
                ScheduleDefaultWeatherCheck();
                return;
            }

            // ── Environment mutation is disabled (both syncs off) ──────
            if (!policy.CanMutateEnvironment)
            {
                // Still fetch for the date-bar display if needed
                if (policy.CanFetchWeatherForUI && hasKey)
                {
                    if (!forceApi && hasCache)
                    {
                        UpdateUiWeatherString(WeatherService.CachedWeather);
                        ScheduleNextWeatherCheckFromCache(location);
                        return;
                    }

                    if (_isFetching)
                    {
                        if (forceApi) _pendingForceRefresh = true;
                        ScheduleDefaultWeatherCheck();
                        return;
                    }

                    _isFetching = true;
                    StartCoroutine(WeatherService.FetchWeather(
                        ChillEnvPlugin.Cfg_ApiKey.Value, location, forceApi,
                        (w) =>
                        {
                            _isFetching = false;
                            UpdateUiWeatherString(w);
                            if (w != null) ScheduleNextWeatherCheckFromCache(location);
                            else ScheduleDefaultWeatherCheck();
                            HandlePendingForceRefresh();
                        }));
                }
                else if (policy.NeedWeatherDataForUI && hasCache)
                {
                    UpdateUiWeatherString(WeatherService.CachedWeather);
                    ScheduleNextWeatherCheckFromCache(location);
                }
                else
                {
                    ScheduleDefaultWeatherCheck();
                }
                return;
            }

            // ── Normal path: environment mutation is active ───────────
            bool shouldFetch = (policy.CanControlWeather || policy.CanFetchWeatherForUI) && hasKey;
            bool needNewFetch = shouldFetch && (forceApi || !hasCache);

            if (needNewFetch)
            {
                if (_isFetching)
                {
                    if (forceApi) _pendingForceRefresh = true;
                    ChillEnvPlugin.Log?.LogWarning("TriggerSync: fetch already in progress");
                    ScheduleDefaultWeatherCheck();
                    return;
                }

                _isFetching = true;
                StartCoroutine(WeatherService.FetchWeather(
                    ChillEnvPlugin.Cfg_ApiKey.Value, location, forceApi,
                    (w) =>
                    {
                        _isFetching = false;
                        UpdateUiWeatherString(w);
                        ApplyByPolicy(policy, w, forceApply);
                        if (w != null) ScheduleNextWeatherCheckFromCache(location);
                        else ScheduleDefaultWeatherCheck();
                        HandlePendingForceRefresh();
                    }));
                return;
            }

            // Use existing cache for UI text
            if (policy.NeedWeatherDataForUI && hasCache)
                UpdateUiWeatherString(WeatherService.CachedWeather);

            // Apply environment from cache (or time-only if no weather sync)
            WeatherInfo weatherForApply = (policy.CanControlWeather && hasCache)
                ? WeatherService.CachedWeather
                : null;

            ApplyByPolicy(policy, weatherForApply, forceApply);

            if (shouldFetch && hasCache)
                ScheduleNextWeatherCheckFromCache(location);
            else
                ScheduleDefaultWeatherCheck();
        }

        // ─────────────────────────────────────────────────────────────
        // Environment application
        // ─────────────────────────────────────────────────────────────

        private void ApplyByPolicy(SyncPolicySnapshot policy, WeatherInfo weather, bool force)
        {
            if (SceneryAutomationSystem.IsWhaleSystemTriggered)
            {
                ChillEnvPlugin.Log?.LogInfo(
                    "[Whale Easter Egg] 🐋 System-triggered whale active, skipping weather change.");
                return;
            }

            EnvironmentType timeBase = GetTimeBasedEnvironment();

            if (policy.CanControlTime)
            {
                ApplyBaseEnvironment(timeBase, force);
                _lastAppliedEnv = timeBase;
            }

            if (!policy.CanControlWeather || weather == null) return;

            EnvironmentType baseEnv = policy.CanControlTime
                ? timeBase
                : (GetCurrentBaseEnvironment() ?? GetTimeBasedEnvironment());

            EnvironmentType finalEnv = baseEnv;
            if (policy.CanApplyCloudyOverride
                && IsBadWeather(weather.Code)
                && baseEnv != EnvironmentType.Night)
            {
                finalEnv = EnvironmentType.Cloudy;
                ApplyBaseEnvironment(finalEnv, force);
            }

            ApplyScenery(GetSceneryType(weather.Code), force);
            _lastAppliedEnv = finalEnv;
        }

        private void ApplyBaseEnvironment(EnvironmentType target, bool force)
        {
            if (!force && IsEnvironmentActive(target)) return;

            foreach (var env in BaseEnvironments)
                if (env != target && IsEnvironmentActive(env))
                    SimulateClick(env);

            if (!IsEnvironmentActive(target))
                SimulateClick(target);

            ChillEnvPlugin.CallServiceChangeWeather(target);
            ChillEnvPlugin.Log?.LogInfo($"[Environment] Switching to: {target}");
        }

        private void ApplyScenery(EnvironmentType? target, bool force)
        {
            foreach (var env in SceneryWeathers)
            {
                bool shouldBeActive = target.HasValue && target.Value == env;
                bool isActive = IsEnvironmentActive(env);

                if (shouldBeActive && !isActive)
                {
                    SimulateClick(env);
                    ChillEnvPlugin.Log?.LogInfo($"[Scenery] Enabling: {env}");
                }
                else if (!shouldBeActive && isActive)
                {
                    SimulateClick(env);
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Weather / time helpers
        // ─────────────────────────────────────────────────────────────

        public static bool IsBadWeather(int code)
        {
            if (code == 10 || code == 13 || code == 21 || code == 22) return false;
            if (code == 4) return true;
            if (code >= 7 && code <= 31) return true;
            if (code >= 34 && code <= 36) return true;
            return false;
        }

        private EnvironmentType? GetSceneryType(int code)
        {
            if (code >= 20 && code <= 25) return EnvironmentType.Snow;
            if (code == 11 || code == 12 || (code >= 16 && code <= 18)) return EnvironmentType.ThunderRain;
            if (code == 10 || code == 14 || code == 15) return EnvironmentType.HeavyRain;
            if (code == 13 || code == 19) return EnvironmentType.LightRain;
            return null;
        }

        public static EnvironmentType GetTimeBasedEnvironment()
        {
            TimeSpan cur = DateTime.Now.TimeOfDay;
            TimeSpan sunrise, sunset;
            TimeSpan.TryParse(ChillEnvPlugin.Cfg_SunriseTime.Value, out sunrise);
            TimeSpan.TryParse(ChillEnvPlugin.Cfg_SunsetTime.Value, out sunset);

            if (cur >= sunrise && cur < sunset.Subtract(TimeSpan.FromMinutes(30)))
                return EnvironmentType.Day;
            if (cur >= sunset.Subtract(TimeSpan.FromMinutes(30))
                && cur < sunset.Add(TimeSpan.FromMinutes(30)))
                return EnvironmentType.Sunset;
            return EnvironmentType.Night;
        }

        // ─────────────────────────────────────────────────────────────
        // State query helpers
        // ─────────────────────────────────────────────────────────────

        private EnvironmentType? GetCurrentBaseEnvironment()
        {
            foreach (var env in BaseEnvironments)
                if (IsEnvironmentActive(env)) return env;
            return null;
        }

        private EnvironmentType? GetCurrentActiveEnvironment()
        {
            foreach (var env in MainEnvironments)
            {
                var winType = (WindowViewType)Enum.Parse(typeof(WindowViewType), env.ToString());
                if (WindowViewStateAccessor.TryIsWindowViewActive(winType, out var isActive) && isActive)
                    return env;
            }
            return null;
        }

        private bool IsEnvironmentActive(EnvironmentType env)
        {
            var winType = (WindowViewType)Enum.Parse(typeof(WindowViewType), env.ToString());
            if (WindowViewStateAccessor.TryIsWindowViewActive(winType, out var isActive))
                return isActive;
            return false;
        }

        private void SimulateClick(EnvironmentType env)
        {
            if (EnvRegistry.TryGet(env, out var ctrl))
                ChillEnvPlugin.SimulateClickMainIcon(ctrl);
        }
    }
}
