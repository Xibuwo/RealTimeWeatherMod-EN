using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Networking;
using Bulbul;

namespace ChillWithYou.EnvSync
{
    [BepInPlugin("chillwithyou.envsync", "Chill Env Sync", "4.3.1")]
    public class ChillEnvPlugin : BaseUnityPlugin
    {
        internal static ChillEnvPlugin Instance;
        internal static ManualLogSource Log;
        internal static UnlockItemService UnlockItemServiceInstance;

        internal static object WindowViewServiceInstance;
        internal static MethodInfo ChangeWeatherMethod;

        internal static bool Initialized;

        // --- 配置项 ---
        internal static ConfigEntry<int> Cfg_WeatherRefreshMinutes;
        internal static ConfigEntry<string> Cfg_SunriseTime;
        internal static ConfigEntry<string> Cfg_SunsetTime;
        internal static ConfigEntry<string> Cfg_SeniverseKey;
        internal static ConfigEntry<string> Cfg_Location;
        internal static ConfigEntry<bool> Cfg_EnableWeatherSync;
        internal static ConfigEntry<bool> Cfg_UnlockEnvironments;
        internal static ConfigEntry<bool> Cfg_UnlockDecorations;

        // 调试配置
        internal static ConfigEntry<bool> Cfg_DebugMode;
        internal static ConfigEntry<int> Cfg_DebugCode;
        internal static ConfigEntry<int> Cfg_DebugTemp;
        internal static ConfigEntry<string> Cfg_DebugText;

        private static AutoEnvRunner _runner;
        private static GameObject _runnerGO;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            Log.LogInfo("【4.3.1】启动 - 彻底移除降水天气对Service的误调用");

            try
            {
                var harmony = new Harmony("ChillWithYou.EnvSync");
                harmony.PatchAll();
            }
            catch (Exception ex)
            {
                Log.LogError($"Harmony 失败: {ex}");
            }

            InitConfig();

            try
            {
                _runnerGO = new GameObject("ChillEnvSyncRunner");
                _runnerGO.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_runnerGO);
                _runnerGO.SetActive(true);
                _runner = _runnerGO.AddComponent<AutoEnvRunner>();
                _runner.enabled = true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Runner 创建失败: {ex}");
            }
        }

        private void InitConfig()
        {
            Cfg_WeatherRefreshMinutes = Config.Bind("WeatherSync", "RefreshMinutes", 30, "天气API刷新间隔(分钟)");
            Cfg_SunriseTime = Config.Bind("TimeConfig", "Sunrise", "06:30", "日出时间");
            Cfg_SunsetTime = Config.Bind("TimeConfig", "Sunset", "18:30", "日落时间");

            Cfg_EnableWeatherSync = Config.Bind("WeatherAPI", "EnableWeatherSync", false, "是否启用天气API同步（需要填写API Key）");
            Cfg_SeniverseKey = Config.Bind("WeatherAPI", "SeniverseKey", "", "心知天气 API Key");
            Cfg_Location = Config.Bind("WeatherAPI", "Location", "beijing", "城市名称（拼音或中文，如 beijing、上海、ip 表示自动定位）");

            Cfg_UnlockEnvironments = Config.Bind("Unlock", "UnlockAllEnvironments", true, "是否自动解锁所有环境场景");
            Cfg_UnlockDecorations = Config.Bind("Unlock", "UnlockAllDecorations", true, "是否自动解锁所有装饰品");

            Cfg_DebugMode = Config.Bind("Debug", "EnableDebugMode", false, "是否开启调试模式");
            Cfg_DebugCode = Config.Bind("Debug", "SimulatedCode", 1, "模拟天气代码");
            Cfg_DebugTemp = Config.Bind("Debug", "SimulatedTemp", 25, "模拟温度");
            Cfg_DebugText = Config.Bind("Debug", "SimulatedText", "DebugWeather", "模拟天气描述");
        }

        internal static void TryInitializeOnce(UnlockItemService svc)
        {
            if (Initialized || svc == null) return;

            if (Cfg_UnlockEnvironments.Value) ForceUnlockAllEnvironments(svc);
            if (Cfg_UnlockDecorations.Value) ForceUnlockAllDecorations(svc);

            Initialized = true;
            Log?.LogInfo("初始化完成");
        }

        internal static void CallServiceChangeWeather(EnvironmentType envType)
        {
            if (WindowViewServiceInstance == null || ChangeWeatherMethod == null) return;

            try
            {
                var parameters = ChangeWeatherMethod.GetParameters();
                if (parameters.Length == 0) return;

                Type windowViewEnumType = parameters[0].ParameterType;
                object enumValue = null;

                try
                {
                    enumValue = Enum.Parse(windowViewEnumType, envType.ToString());
                }
                catch { return; }

                ChangeWeatherMethod.Invoke(WindowViewServiceInstance, new object[] { enumValue });
            }
            catch (Exception ex)
            {
                Log?.LogError($"调用 Service 失败: {ex.Message}");
            }
        }

        internal static void SimulateClickMainIcon(EnviromentController ctrl)
        {
            if (ctrl == null) return;
            try
            {
                MethodInfo clickMethod = ctrl.GetType().GetMethod("OnClickButtonMainIcon", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (clickMethod != null)
                {
                    clickMethod.Invoke(ctrl, null);
                }
            }
            catch (Exception ex)
            {
                Log?.LogError($"模拟点击失败: {ex.Message}");
            }
        }

        private static void ForceUnlockAllEnvironments(UnlockItemService svc)
        {
            try
            {
                var envProp = svc.GetType().GetProperty("Environment");
                var unlockEnvObj = envProp.GetValue(svc);
                var dictField = unlockEnvObj.GetType().GetField("_environmentDic", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var dict = dictField.GetValue(unlockEnvObj) as IDictionary;
                int count = 0;
                foreach (DictionaryEntry entry in dict)
                {
                    var data = entry.Value;
                    var lockField = data.GetType().GetField("_isLocked", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    var reactive = lockField.GetValue(data);
                    var propValue = reactive.GetType().GetProperty("Value");
                    propValue.SetValue(reactive, false, null);
                    count++;
                }
                Log?.LogInfo($"✅ 已解锁 {count} 个环境");
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
                var dict = dictField.GetValue(unlockDecoObj) as IDictionary;
                if (dict == null) return;
                int count = 0;
                foreach (DictionaryEntry entry in dict)
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
                Log?.LogInfo($"✅ 已解锁 {count} 个装饰品");
            }
            catch { }
        }
    }

    public enum WeatherCondition
    {
        Clear, Cloudy, Rainy, Snowy, Foggy, Unknown
    }

    public class WeatherInfo
    {
        public WeatherCondition Condition;
        public int Temperature;
        public string Text;
        public int Code;
        public DateTime UpdateTime;
        public override string ToString() => $"{Text}({Condition}), {Temperature}°C, Code={Code}";
    }

    public class WeatherService
    {
        private static WeatherInfo _cachedWeather;
        private static DateTime _lastFetchTime;
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(60);

        private static readonly Dictionary<string, EnvironmentType> WeatherToEnvironment = new Dictionary<string, EnvironmentType>(StringComparer.OrdinalIgnoreCase)
        {
            {"Clear", EnvironmentType.Day},
            {"Clouds", EnvironmentType.Cloudy},
            {"Drizzle", EnvironmentType.LightRain},
            {"Rain", EnvironmentType.HeavyRain},
            {"Thunderstorm", EnvironmentType.ThunderRain},
            {"Snow", EnvironmentType.Snow},
            {"Mist", EnvironmentType.Cloudy},
            {"Fog", EnvironmentType.Cloudy},
            {"Haze", EnvironmentType.Cloudy},
            {"Dust", EnvironmentType.Wind},
            {"Sand", EnvironmentType.Wind},
            {"Squall", EnvironmentType.ThunderRain},
            {"Tornado", EnvironmentType.ThunderRain},
        };

        public static WeatherInfo CachedWeather => _cachedWeather;

        public static IEnumerator FetchWeather(string apiKey, string location, bool force, Action<WeatherInfo> onComplete)
        {
            if (!force && _cachedWeather != null && DateTime.Now - _lastFetchTime < CacheExpiry)
            {
                onComplete?.Invoke(_cachedWeather);
                yield break;
            }

            string url = $"https://api.seniverse.com/v3/weather/now.json?key={apiKey}&location={UnityWebRequest.EscapeURL(location)}&language=zh-Hans&unit=c";

            ChillEnvPlugin.Log?.LogInfo($"[API] 发起请求: {location}");

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    ChillEnvPlugin.Log?.LogWarning($"[API] 网络错误: {request.result}, Code: {request.responseCode}, Error: {request.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                string json = request.downloadHandler.text;
                if (string.IsNullOrEmpty(json))
                {
                    ChillEnvPlugin.Log?.LogWarning("[API] 错误: 返回内容为空");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    var weather = ParseWeatherJson(json);
                    if (weather != null)
                    {
                        _cachedWeather = weather;
                        _lastFetchTime = DateTime.Now;
                        ChillEnvPlugin.Log?.LogInfo($"[API] 数据更新: {weather}");
                        onComplete?.Invoke(weather);
                    }
                    else
                    {
                        ChillEnvPlugin.Log?.LogWarning($"[API] 解析失败! 原始返回: {json}");
                        onComplete?.Invoke(null);
                    }
                }
                catch (Exception ex)
                {
                    ChillEnvPlugin.Log?.LogError($"[API] 异常: {ex.Message}");
                    onComplete?.Invoke(null);
                }
            }
        }

        private static WeatherInfo ParseWeatherJson(string json)
        {
            try
            {
                if (json.Contains("\"status\"") && !json.Contains("\"results\""))
                {
                    return null;
                }

                int nowIndex = json.IndexOf("\"now\"");
                if (nowIndex < 0) return null;

                int code = ExtractIntValue(json, "\"code\":\"", "\"");
                int temp = ExtractIntValue(json, "\"temperature\":\"", "\"");
                string text = ExtractStringValue(json, "\"text\":\"", "\"");

                if (string.IsNullOrEmpty(text)) return null;

                return new WeatherInfo
                {
                    Code = code,
                    Text = text,
                    Temperature = temp,
                    Condition = MapCodeToCondition(code),
                    UpdateTime = DateTime.Now
                };
            }
            catch { return null; }
        }

        private static int ExtractIntValue(string json, string prefix, string suffix)
        {
            int start = json.IndexOf(prefix);
            if (start < 0) return 0;
            start += prefix.Length;
            int end = json.IndexOf(suffix, start);
            if (end < 0) return 0;
            string value = json.Substring(start, end - start);
            int.TryParse(value, out int result);
            return result;
        }

        private static string ExtractStringValue(string json, string prefix, string suffix)
        {
            int start = json.IndexOf(prefix);
            if (start < 0) return null;
            start += prefix.Length;
            int end = json.IndexOf(suffix, start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        public static WeatherCondition MapCodeToCondition(int code)
        {
            if (code >= 0 && code <= 3) return WeatherCondition.Clear;
            if (code >= 4 && code <= 9) return WeatherCondition.Cloudy;
            if (code >= 10 && code <= 20) return WeatherCondition.Rainy;
            if (code >= 21 && code <= 25) return WeatherCondition.Snowy;
            if (code >= 26 && code <= 29) return WeatherCondition.Cloudy;
            if (code >= 30 && code <= 31) return WeatherCondition.Foggy;
            if (code >= 32 && code <= 36) return WeatherCondition.Cloudy;
            if (code >= 37 && code <= 38) return WeatherCondition.Clear;
            return WeatherCondition.Unknown;
        }
    }

    public class AutoEnvRunner : MonoBehaviour
    {
        private float _nextWeatherCheckTime;
        private float _nextTimeCheckTime;

        private EnvironmentType? _lastAppliedEnv;
        private bool _isFetching;

        private static readonly EnvironmentType[] BaseEnvironments = new[]
        {
            EnvironmentType.Day, EnvironmentType.Sunset, EnvironmentType.Night, EnvironmentType.Cloudy
        };

        private static readonly EnvironmentType[] PrecipitationWeathers = new[]
        {
            EnvironmentType.ThunderRain, EnvironmentType.HeavyRain, EnvironmentType.LightRain, EnvironmentType.Snow
        };

        private static readonly EnvironmentType[] MainEnvironments = new[]
        {
            EnvironmentType.Day, EnvironmentType.Sunset, EnvironmentType.Night, EnvironmentType.Cloudy,
            EnvironmentType.LightRain, EnvironmentType.HeavyRain, EnvironmentType.ThunderRain, EnvironmentType.Snow
        };

        private void Start()
        {
            _nextWeatherCheckTime = Time.time + 10f;
            _nextTimeCheckTime = Time.time + 10f;
            ChillEnvPlugin.Log?.LogInfo("Runner 启动，等待首次同步...");
        }

        private void Update()
        {
            if (!ChillEnvPlugin.Initialized || EnvRegistry.Count == 0) return;

            if (Input.GetKeyDown(KeyCode.F9))
            {
                ChillEnvPlugin.Log?.LogInfo("F9: 手动强制同步状态");
                TriggerSync(forceApi: false, forceApply: true);
            }
            if (Input.GetKeyDown(KeyCode.F8)) ShowStatus();
            if (Input.GetKeyDown(KeyCode.F7))
            {
                ChillEnvPlugin.Log?.LogInfo("F7: 强制重载 (尝试读取最新配置/API)");
                ChillEnvPlugin.Instance.Config.Reload();
                ForceRefreshWeather();
            }

            if (Time.time >= _nextTimeCheckTime)
            {
                _nextTimeCheckTime = Time.time + 30f;
                TriggerSync(forceApi: false, forceApply: false);
            }

            if (Time.time >= _nextWeatherCheckTime)
            {
                int minutes = Mathf.Max(1, ChillEnvPlugin.Cfg_WeatherRefreshMinutes.Value);
                _nextWeatherCheckTime = Time.time + (minutes * 60f);
                TriggerSync(forceApi: true, forceApply: false);
            }
        }

        private void ShowStatus()
        {
            var now = DateTime.Now;
            ChillEnvPlugin.Log?.LogInfo($"--- 状态 [{now:HH:mm:ss}] ---");
            ChillEnvPlugin.Log?.LogInfo($"插件记录: {_lastAppliedEnv}");
            var currentActive = GetCurrentActiveEnvironment();
            ChillEnvPlugin.Log?.LogInfo($"游戏实际: {currentActive}");

            if (ChillEnvPlugin.Cfg_DebugMode.Value)
            {
                ChillEnvPlugin.Log?.LogWarning("【警告】调试模式已开启！API请求被屏蔽。");
            }
        }

        private void ForceRefreshWeather()
        {
            _nextWeatherCheckTime = Time.time + (ChillEnvPlugin.Cfg_WeatherRefreshMinutes.Value * 60f);
            TriggerSync(forceApi: true, forceApply: false);
        }

        private void TriggerSync(bool forceApi, bool forceApply)
        {
            if (ChillEnvPlugin.Cfg_DebugMode.Value)
            {
                ChillEnvPlugin.Log?.LogWarning("[调试模式] 使用模拟数据...");
                int mockCode = ChillEnvPlugin.Cfg_DebugCode.Value;
                var mockWeather = new WeatherInfo
                {
                    Code = mockCode,
                    Temperature = ChillEnvPlugin.Cfg_DebugTemp.Value,
                    Text = ChillEnvPlugin.Cfg_DebugText.Value,
                    Condition = WeatherService.MapCodeToCondition(mockCode),
                    UpdateTime = DateTime.Now
                };
                ApplyEnvironment(mockWeather, forceApply);
                return;
            }

            bool weatherEnabled = ChillEnvPlugin.Cfg_EnableWeatherSync.Value;
            string apiKey = ChillEnvPlugin.Cfg_SeniverseKey.Value;

            if (weatherEnabled && !string.IsNullOrEmpty(apiKey))
            {
                string location = ChillEnvPlugin.Cfg_Location.Value;

                if (forceApi || WeatherService.CachedWeather == null)
                {
                    if (_isFetching) return;
                    _isFetching = true;
                    StartCoroutine(WeatherService.FetchWeather(apiKey, location, forceApi, (weather) =>
                    {
                        _isFetching = false;
                        if (weather != null) ApplyEnvironment(weather, forceApply);
                        else
                        {
                            ChillEnvPlugin.Log?.LogWarning("[API异常] 启用时间兜底");
                            ApplyTimeBasedEnvironment(forceApply);
                        }
                    }));
                }
                else
                {
                    ApplyEnvironment(WeatherService.CachedWeather, forceApply);
                }
            }
            else
            {
                ApplyTimeBasedEnvironment(forceApply);
            }
        }

        private EnvironmentType? GetCurrentActiveEnvironment()
        {
            try
            {
                var windowViewDic = SaveDataManager.Instance.WindowViewDic;
                foreach (var envType in MainEnvironments)
                {
                    WindowViewType windowType;
                    if (Enum.TryParse(envType.ToString(), out windowType))
                    {
                        if (windowViewDic.ContainsKey(windowType) && windowViewDic[windowType].IsActive) return envType;
                    }
                }
            }
            catch { }
            return null;
        }

        private bool IsEnvironmentActive(EnvironmentType envType)
        {
            try
            {
                var windowViewDic = SaveDataManager.Instance.WindowViewDic;
                WindowViewType windowType;
                if (Enum.TryParse(envType.ToString(), out windowType))
                {
                    return windowViewDic.ContainsKey(windowType) && windowViewDic[windowType].IsActive;
                }
            }
            catch { }
            return false;
        }

        private void ActivateEnvironment(EnvironmentType envType)
        {
            if (EnvRegistry.TryGet(envType, out var ctrl))
            {
                if (!IsEnvironmentActive(envType))
                {
                    ChillEnvPlugin.SimulateClickMainIcon(ctrl);
                    // 【修复】移除这里的 CallServiceChangeWeather，改为在 ActivateEnvironmentWithMutex 中统一管理
                }
            }
        }

        private void DeactivateEnvironment(EnvironmentType envType)
        {
            if (EnvRegistry.TryGet(envType, out var ctrl))
            {
                if (IsEnvironmentActive(envType))
                {
                    ChillEnvPlugin.SimulateClickMainIcon(ctrl);
                }
            }
        }

        private void ForceActivateEnvironment(EnvironmentType envType)
        {
            if (EnvRegistry.TryGet(envType, out var ctrl))
            {
                if (!IsEnvironmentActive(envType))
                {
                    ChillEnvPlugin.SimulateClickMainIcon(ctrl);
                }
                // 【修复】移除 CallServiceChangeWeather
            }
        }

        private void ActivateEnvironmentWithMutex(EnvironmentType target, bool force)
        {
            if (!force && IsEnvironmentActive(target)) return;

            bool isBaseEnv = Array.IndexOf(BaseEnvironments, target) >= 0;
            bool isPrecipitation = Array.IndexOf(PrecipitationWeathers, target) >= 0;

            if (isBaseEnv)
            {
                foreach (var env in BaseEnvironments)
                    if (env != target) DeactivateEnvironment(env);
            }

            if (isPrecipitation)
            {
                foreach (var env in PrecipitationWeathers)
                    if (env != target) DeactivateEnvironment(env);
            }

            if (force)
            {
                ForceActivateEnvironment(target);
                ChillEnvPlugin.Log?.LogInfo($"[强制决策] 刷新至 {target}");
            }
            else
            {
                ActivateEnvironment(target);
                ChillEnvPlugin.Log?.LogInfo($"[决策] 切换至 {target}");
            }

            // 【关键修复】只有基础环境才允许调用 Service，防止 Precipitation (Snow/Rain) 覆盖掉背景
            if (isBaseEnv)
            {
                ChillEnvPlugin.CallServiceChangeWeather(target);
            }
        }

        private void ClearAllWeatherEffects()
        {
            foreach (var env in PrecipitationWeathers)
            {
                if (IsEnvironmentActive(env))
                {
                    DeactivateEnvironment(env);
                }
            }
        }

        private EnvironmentType GetTimeBasedEnvironment()
        {
            DateTime now = DateTime.Now;
            TimeSpan currentTime = now.TimeOfDay;
            TimeSpan sunrise, sunset;
            if (!TimeSpan.TryParse(ChillEnvPlugin.Cfg_SunriseTime.Value, out sunrise)) sunrise = new TimeSpan(6, 30, 0);
            if (!TimeSpan.TryParse(ChillEnvPlugin.Cfg_SunsetTime.Value, out sunset)) sunset = new TimeSpan(18, 30, 0);

            TimeSpan sunsetStart = sunset.Subtract(TimeSpan.FromMinutes(30));
            TimeSpan sunsetEnd = sunset.Add(TimeSpan.FromMinutes(30));

            if (currentTime >= sunrise && currentTime < sunsetStart) return EnvironmentType.Day;
            else if (currentTime >= sunsetStart && currentTime < sunsetEnd) return EnvironmentType.Sunset;
            else return EnvironmentType.Night;
        }

        private void ApplyEnvironment(WeatherInfo weather, bool force)
        {
            if (force || _lastAppliedEnv == null)
            {
                ChillEnvPlugin.Log?.LogInfo($"[决策] 天气:{weather.Text}(Code:{weather.Code}) 温度:{weather.Temperature}°C");
            }

            EnvironmentType timeEnv = GetTimeBasedEnvironment();
            int code = weather.Code;

            if (code >= 0 && code <= 3)
            {
                ClearAllWeatherEffects();
                ActivateEnvironmentWithMutex(timeEnv, force);
            }
            else if (code >= 4 && code <= 9)
            {
                ClearAllWeatherEffects();
                ActivateEnvironmentWithMutex(EnvironmentType.Cloudy, force);
            }
            else if (code >= 10 && code <= 12)
            {
                ActivateEnvironmentWithMutex(EnvironmentType.Cloudy, force);
                ActivateEnvironmentWithMutex(EnvironmentType.LightRain, force);
            }
            else if (code >= 13 && code <= 14)
            {
                ActivateEnvironmentWithMutex(EnvironmentType.Cloudy, force);
                ActivateEnvironmentWithMutex(EnvironmentType.HeavyRain, force);
            }
            else if (code >= 15 && code <= 18)
            {
                ActivateEnvironmentWithMutex(EnvironmentType.Cloudy, force);
                ActivateEnvironmentWithMutex(EnvironmentType.ThunderRain, force);
            }
            else if (code >= 21 && code <= 25)
            {
                ActivateEnvironmentWithMutex(EnvironmentType.Cloudy, force);
                ActivateEnvironmentWithMutex(EnvironmentType.Snow, force);
            }
            else
            {
                ClearAllWeatherEffects();
                ActivateEnvironmentWithMutex(timeEnv, force);
            }

            _lastAppliedEnv = timeEnv;
        }

        private void ApplyTimeBasedEnvironment(bool force)
        {
            EnvironmentType targetEnv = GetTimeBasedEnvironment();
            ClearAllWeatherEffects();
            ActivateEnvironmentWithMutex(targetEnv, force);
        }
    }

    internal static class EnvRegistry
    {
        private static readonly Dictionary<EnvironmentType, EnviromentController> _map = new Dictionary<EnvironmentType, EnviromentController>();
        internal static int Count => _map.Count;

        internal static void Register(EnvironmentType type, EnviromentController ctrl)
        {
            if (ctrl != null && !_map.ContainsKey(type)) _map[type] = ctrl;
        }

        internal static bool TryGet(EnvironmentType type, out EnviromentController ctrl)
        {
            return _map.TryGetValue(type, out ctrl);
        }
    }

    [HarmonyPatch(typeof(UnlockItemService), "Setup")]
    internal static class UnlockServicePatch
    {
        static void Postfix(UnlockItemService __instance)
        {
            ChillEnvPlugin.UnlockItemServiceInstance = __instance;
            ChillEnvPlugin.TryInitializeOnce(__instance);
        }
    }

    [HarmonyPatch(typeof(EnviromentController), "Setup")]
    internal static class EnvControllerPatch
    {
        static void Postfix(EnviromentController __instance)
        {
            EnvRegistry.Register(__instance.EnvironmentType, __instance);
        }
    }

    [HarmonyPatch(typeof(FacilityEnviroment), "Setup")]
    internal static class FacilityEnvPatch
    {
        static void Postfix(FacilityEnviroment __instance)
        {
            try
            {
                FieldInfo field = typeof(FacilityEnviroment).GetField("_windowViewService", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field != null)
                {
                    object service = field.GetValue(__instance);
                    if (service != null)
                    {
                        ChillEnvPlugin.WindowViewServiceInstance = service;
                        ChillEnvPlugin.ChangeWeatherMethod = service.GetType().GetMethod("ChangeWeatherAndTime", BindingFlags.Instance | BindingFlags.Public);

                        if (ChillEnvPlugin.ChangeWeatherMethod != null)
                        {
                            ChillEnvPlugin.Log?.LogInfo("✅ 成功捕获 WindowViewService.ChangeWeatherAndTime");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"捕获 Service 失败: {ex}");
            }
        }
    }
}