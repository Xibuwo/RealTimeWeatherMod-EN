using System;
using UnityEngine;
using Bulbul;
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

        private static readonly EnvironmentType[] BaseEnvironments = new[] { EnvironmentType.Day, EnvironmentType.Sunset, EnvironmentType.Night, EnvironmentType.Cloudy };
        private static readonly EnvironmentType[] SceneryWeathers = new[] { EnvironmentType.ThunderRain, EnvironmentType.HeavyRain, EnvironmentType.LightRain, EnvironmentType.Snow };
        private static readonly EnvironmentType[] MainEnvironments = new[] { EnvironmentType.Day, EnvironmentType.Sunset, EnvironmentType.Night, EnvironmentType.Cloudy, EnvironmentType.LightRain, EnvironmentType.HeavyRain, EnvironmentType.ThunderRain, EnvironmentType.Snow };

        private void Start()
        {
            _nextWeatherCheckTime = Time.time + 10f;
            _nextTimeCheckTime = Time.time + 10f;
            ChillEnvPlugin.Log?.LogInfo("Runner starting...");

            CheckAndSyncSunSchedule();
        }

        private void CheckAndSyncSunSchedule()
        {
            if (!ChillEnvPlugin.Cfg_EnableWeatherSync.Value) return;

            string lastSync = ChillEnvPlugin.Cfg_LastSunSyncDate.Value;
            string today = DateTime.Now.ToString("dd-MM-yyyy");

            if (lastSync != today)
            {
                StartCoroutine(SyncSunScheduleRoutine(today));
            }
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
                        ChillEnvPlugin.Log?.LogInfo($"[SunSync] Sync successful: Sunrise {data.sunrise} Sunset {data.sunset}");

                        ChillEnvPlugin.Cfg_SunriseTime.Value = data.sunrise;
                        ChillEnvPlugin.Cfg_SunsetTime.Value = data.sunset;
                        ChillEnvPlugin.Cfg_LastSunSyncDate.Value = targetDate;

                        ChillEnvPlugin.Instance.Config.Save();
                        success = true;
                    }
                });

                if (success) yield break;

                ChillEnvPlugin.Log?.LogWarning($"[SunSync] Sync failed, retrying in {delay} seconds ({retryCount + 1}/{MaxRetries})");
                yield return new WaitForSeconds(delay);

                delay *= 2f;
                retryCount++;
            }
            ChillEnvPlugin.Log?.LogError("[SunSync] Max retries reached, giving up sync for today.");
        }

        private void Update()
        {
            if (!ChillEnvPlugin.Initialized || EnvRegistry.Count == 0) return;
            if (Input.GetKeyDown(KeyCode.F9)) { ChillEnvPlugin.Log?.LogInfo("F9: Force Sync"); TriggerSync(false, true); }
            if (Input.GetKeyDown(KeyCode.F8)) ShowStatus();
            if (Input.GetKeyDown(KeyCode.F7)) { ChillEnvPlugin.Log?.LogInfo("F7: Force Refresh"); ChillEnvPlugin.Instance.Config.Reload(); ForceRefreshWeather(); }
            if (Time.time >= _nextTimeCheckTime) { _nextTimeCheckTime = Time.time + 30f; TriggerSync(false, false); }
            if (Time.time >= _nextWeatherCheckTime) { _nextWeatherCheckTime = Time.time + (Mathf.Max(1, ChillEnvPlugin.Cfg_WeatherRefreshMinutes.Value) * 60f); TriggerSync(true, false); }
        }

        private void ShowStatus()
        {
            var now = DateTime.Now;
            ChillEnvPlugin.Log?.LogInfo($"--- Status [{now:HH:mm:ss}] ---");
            ChillEnvPlugin.Log?.LogInfo($"Plugin Log: {_lastAppliedEnv}");
            var currentActive = GetCurrentActiveEnvironment();
            ChillEnvPlugin.Log?.LogInfo($"Game Actual: {currentActive}");
            ChillEnvPlugin.Log?.LogInfo($"UI Text: {ChillEnvPlugin.UIWeatherString}");
            if (ChillEnvPlugin.Cfg_DebugMode.Value) ChillEnvPlugin.Log?.LogWarning("【Warning】Debug mode is ON!");
        }

        private void ForceRefreshWeather() { _nextWeatherCheckTime = Time.time + (ChillEnvPlugin.Cfg_WeatherRefreshMinutes.Value * 60f); TriggerSync(true, false); }

        private void TriggerSync(bool forceApi, bool forceApply)
        {
            if (ChillEnvPlugin.Cfg_DebugMode.Value)
            {
                ChillEnvPlugin.Log?.LogWarning("[Debug Mode] Using mock data...");
                int mockCode = ChillEnvPlugin.Cfg_DebugCode.Value;
                var mockWeather = new WeatherInfo { Code = mockCode, Temperature = ChillEnvPlugin.Cfg_DebugTemp.Value, Text = ChillEnvPlugin.Cfg_DebugText.Value, Condition = WeatherService.MapCodeToCondition(mockCode), UpdateTime = DateTime.Now };
                ApplyEnvironment(mockWeather, forceApply); return;
            }
            bool weatherEnabled = ChillEnvPlugin.Cfg_EnableWeatherSync.Value;
            string apiKey = ChillEnvPlugin.Cfg_GeneralAPI.Value;
            if (weatherEnabled && (!string.IsNullOrEmpty(apiKey) || WeatherService.HasDefaultKey))
            {
                string location = ChillEnvPlugin.Cfg_Location.Value;
                if (forceApi || WeatherService.CachedWeather == null)
                {
                    if (_isFetching) return; _isFetching = true;
                    StartCoroutine(WeatherService.FetchWeather(apiKey, location, forceApi, (weather) => {
                        _isFetching = false;
                        if (weather != null) ApplyEnvironment(weather, forceApply);
                        else { ChillEnvPlugin.Log?.LogWarning("[API Error] Falling back to time-based environment."); ApplyTimeBasedEnvironment(forceApply); }
                    }));
                }
                else { ApplyEnvironment(WeatherService.CachedWeather, forceApply); }
            }
            else { ApplyTimeBasedEnvironment(forceApply); }
        }

        private EnvironmentType? GetCurrentActiveEnvironment()
        {
            try { var dict = SaveDataManager.Instance.WindowViewDic; foreach (var env in MainEnvironments) { var winType = (WindowViewType)Enum.Parse(typeof(WindowViewType), env.ToString()); if (dict.ContainsKey(winType) && dict[winType].IsActive) return env; } } catch { }
            return null;
        }

        private bool IsEnvironmentActive(EnvironmentType env)
        {
            try { var dict = SaveDataManager.Instance.WindowViewDic; var winType = (WindowViewType)Enum.Parse(typeof(WindowViewType), env.ToString()); if (dict.ContainsKey(winType)) return dict[winType].IsActive; } catch { }
            return false;
        }

        private void SimulateClick(EnvironmentType env) { if (EnvRegistry.TryGet(env, out var ctrl)) ChillEnvPlugin.SimulateClickMainIcon(ctrl); }

        private bool IsBadWeather(int code)
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

        private EnvironmentType GetTimeBasedEnvironment()
        {
            DateTime now = DateTime.Now; TimeSpan cur = now.TimeOfDay;
            TimeSpan sunrise, sunset; TimeSpan.TryParse(ChillEnvPlugin.Cfg_SunriseTime.Value, out sunrise); TimeSpan.TryParse(ChillEnvPlugin.Cfg_SunsetTime.Value, out sunset);
            if (cur >= sunrise && cur < sunset.Subtract(TimeSpan.FromMinutes(30))) return EnvironmentType.Day;
            else if (cur >= sunset.Subtract(TimeSpan.FromMinutes(30)) && cur < sunset.Add(TimeSpan.FromMinutes(30))) return EnvironmentType.Sunset;
            else return EnvironmentType.Night;
        }

        private void ApplyBaseEnvironment(EnvironmentType target, bool force)
        {
            if (!force && IsEnvironmentActive(target)) return;
            foreach (var env in BaseEnvironments) if (env != target && IsEnvironmentActive(env)) SimulateClick(env);
            if (!IsEnvironmentActive(target)) SimulateClick(target);
            ChillEnvPlugin.CallServiceChangeWeather(target);
            ChillEnvPlugin.Log?.LogInfo($"[Environment] Switching to: {target}");
        }

        private void ApplyScenery(EnvironmentType? target, bool force)
        {
            foreach (var env in SceneryWeathers)
            {
                bool shouldBeActive = (target.HasValue && target.Value == env);
                bool isActive = IsEnvironmentActive(env);
                if (shouldBeActive && !isActive) { SimulateClick(env); ChillEnvPlugin.Log?.LogInfo($"[Scenery] Enabling: {env}"); }
                else if (!shouldBeActive && isActive) { SimulateClick(env); }
            }
        }

        private string FormatTemperature(int tempCelsius)
        {
            string unit = ChillEnvPlugin.Cfg_TemperatureUnit.Value;
            
            if (unit.Equals("Fahrenheit", StringComparison.OrdinalIgnoreCase))
            {
                int tempFahrenheit = (int)Math.Round(tempCelsius * 9.0 / 5.0 + 32);
                return $"{tempFahrenheit}°F";
            }
            else if (unit.Equals("Kelvin", StringComparison.OrdinalIgnoreCase))
            {
                int tempKelvin = (int)Math.Round(tempCelsius + 273.15);
                return $"{tempKelvin}K";
            }
            
            // Default to Celsius
            return $"{tempCelsius}°C";
        }

        private void ApplyEnvironment(WeatherInfo weather, bool force)
        {
            if (Core.SceneryAutomationSystem.IsWhaleSystemTriggered)
            {
                ChillEnvPlugin.Log?.LogInfo("[Whale Easter Egg] 🐋 System-triggered whale is active, skipping weather change.");
                return;
            }

            if (force || _lastAppliedEnv == null) ChillEnvPlugin.Log?.LogInfo($"[Decision] Weather:{weather.Text}(Code:{weather.Code})");
            ChillEnvPlugin.UIWeatherString = $"{weather.Text} {FormatTemperature(weather.Temperature)}";
            EnvironmentType baseEnv = GetTimeBasedEnvironment();
            EnvironmentType finalEnv = baseEnv;
            if (IsBadWeather(weather.Code)) { if (baseEnv != EnvironmentType.Night) finalEnv = EnvironmentType.Cloudy; }
            ApplyBaseEnvironment(finalEnv, force);
            ApplyScenery(GetSceneryType(weather.Code), force);
            _lastAppliedEnv = finalEnv;
        }

        private void ApplyTimeBasedEnvironment(bool force)
        {
            if (Core.SceneryAutomationSystem.IsWhaleSystemTriggered)
            {
                ChillEnvPlugin.Log?.LogInfo("[Whale Easter Egg] 🐋 System-triggered whale is active, skipping weather change.");
                return;
            }

            ChillEnvPlugin.UIWeatherString = "";
            EnvironmentType targetEnv = GetTimeBasedEnvironment();
            ApplyBaseEnvironment(targetEnv, force);
            ApplyScenery(null, force);
        }
    }
}
