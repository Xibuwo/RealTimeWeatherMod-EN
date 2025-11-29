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
    [BepInPlugin("chillwithyou.envsync", "Chill Env Sync", "4.4.1")]
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

            Log.LogInfo("【4.4.1】启动 - 优化坏天气判定 (允许太阳雨/雪)");

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

        private static readonly EnvironmentType[] SceneryWeathers = new[]
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

        private void SimulateClick(EnvironmentType envType)
        {
            if (EnvRegistry.TryGet(envType, out var ctrl))
            {
                ChillEnvPlugin.SimulateClickMainIcon(ctrl);
            }
        }

        // --- 逻辑 Helper 方法 ---

        private bool IsBadWeather(int code)
        {
            // 排除项 (太阳雨/雪): 10(阵雨), 13(小雨), 21(阵雪), 22(小雪)
            if (code == 10 || code == 13 || code == 21 || code == 22) return false;

            // 原始 BadWeather 定义: [4, 7-31, 34-36]
            if (code == 4) return true;
            if (code >= 7 && code <= 31) return true;
            if (code >= 34 && code <= 36) return true;

            return false;
        }

        private EnvironmentType? GetSceneryType(int code)
        {
            // Snow: 20-25
            if (code >= 20 && code <= 25) return EnvironmentType.Snow;
            // ThunderRain: 11, 12, 16-18
            if (code == 11 || code == 12 || (code >= 16 && code <= 18)) return EnvironmentType.ThunderRain;
            // HeavyRain: 10, 14, 15
            if (code == 10 || code == 14 || code == 15) return EnvironmentType.HeavyRain;
            // LightRain: 13, 19
            if (code == 13 || code == 19) return EnvironmentType.LightRain;

            return null;
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

            // Night: 晚于 SunsetEnd 或 早于 Sunrise
            if (currentTime >= sunsetEnd || currentTime < sunrise) return EnvironmentType.Night;
            // Sunset: 在 SunsetStart 和 SunsetEnd 之间
            else if (currentTime >= sunsetStart && currentTime < sunsetEnd) return EnvironmentType.Sunset;
            // Day: 其他
            else return EnvironmentType.Day;
        }

        // --- 执行逻辑 ---

        // 应用基础环境 (Day/Sunset/Night/Cloudy)
        // 规则: 互斥，且必须调用 Service
        private void ApplyBaseEnvironment(EnvironmentType target, bool force)
        {
            // 只有当目标和当前不一致，或者强制刷新时，才操作
            if (!force && IsEnvironmentActive(target)) return;

            // 关闭其他所有基础环境
            foreach (var env in BaseEnvironments)
            {
                if (env != target && IsEnvironmentActive(env))
                {
                    SimulateClick(env); // 关掉旧的
                }
            }

            // 开启新的
            if (!IsEnvironmentActive(target))
            {
                SimulateClick(target); // 开启新的
            }
            else if (force)
            {
                // 已经是开启状态，但在强制模式下，可能需要修复画面
                // 但对于 BaseEnv，通常不需要额外操作，因为 CallServiceChangeWeather 会处理
            }

            // 关键：通知 Service 切换背景
            ChillEnvPlugin.CallServiceChangeWeather(target);
            ChillEnvPlugin.Log?.LogInfo($"[环境] 切换至: {target}");
        }

        // 应用景色 (Rain/Snow)
        // 规则: 互斥，但【绝对不调用】Service
        private void ApplyScenery(EnvironmentType? target, bool force)
        {
            // 遍历所有可能的景色
            foreach (var env in SceneryWeathers)
            {
                bool shouldBeActive = (target.HasValue && target.Value == env);
                bool isActive = IsEnvironmentActive(env);

                if (shouldBeActive)
                {
                    // 如果应该开，但没开 -> 点开
                    if (!isActive)
                    {
                        SimulateClick(env);
                        ChillEnvPlugin.Log?.LogInfo($"[景色] 开启: {env}");
                    }
                    // 如果应该开，且开了，但在强制模式 -> 啥也不做 (避免 Toggle Off)
                    // 景色通常不需要强制唤醒画面，只要 UI 亮了，粒子就会出来
                }
                else
                {
                    // 如果不该开，但开了 -> 关掉
                    if (isActive)
                    {
                        SimulateClick(env); // 关掉
                        // ChillEnvPlugin.Log?.LogInfo($"[景色] 关闭: {env}");
                    }
                }
            }
        }

        private void ApplyEnvironment(WeatherInfo weather, bool force)
        {
            if (force || _lastAppliedEnv == null)
            {
                ChillEnvPlugin.Log?.LogInfo($"[决策] 天气:{weather.Text}(Code:{weather.Code})");
            }

            // Step 1: 计算时间基准环境
            EnvironmentType baseEnv = GetTimeBasedEnvironment();
            EnvironmentType finalEnv = baseEnv;

            // Step 2: 天气覆盖 (Bad Weather Override)
            // 规则: 如果是坏天气，且当前不是夜晚，强制覆盖为 Cloudy
            if (IsBadWeather(weather.Code))
            {
                if (baseEnv != EnvironmentType.Night)
                {
                    finalEnv = EnvironmentType.Cloudy;
                    // ChillEnvPlugin.Log?.LogInfo($"[覆盖] 坏天气检测，环境重定向至 Cloudy");
                }
                else
                {
                    // ChillEnvPlugin.Log?.LogInfo($"[覆盖] 坏天气检测，但由于是夜晚，保持 Night");
                }
            }

            // Step 3: 景色判断 (Scenery)
            EnvironmentType? targetScenery = GetSceneryType(weather.Code);

            // 执行
            ApplyBaseEnvironment(finalEnv, force);
            ApplyScenery(targetScenery, force);

            _lastAppliedEnv = finalEnv;
        }

        private void ApplyTimeBasedEnvironment(bool force)
        {
            // 无API数据时，只按时间走，且没有景色
            EnvironmentType targetEnv = GetTimeBasedEnvironment();
            ApplyBaseEnvironment(targetEnv, force);
            ApplyScenery(null, force); // 关闭所有雨雪
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