using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using MelonLoader;
using UnityEngine;
using UnityEngine.Networking;
using Bulbul;

namespace ChillEnvSync
{
    public class ChillEnvSync : MelonMod
    {
        // ========== 配置 ==========
        private MelonPreferences_Category _configCategory;
        private MelonPreferences_Entry<bool> _enableWeatherSync;
        private MelonPreferences_Entry<string> _cityName;
        private MelonPreferences_Entry<string> _apiKey;
        private MelonPreferences_Entry<int> _syncIntervalMinutes;

        // ========== 状态 ==========
        private float _lastSyncTime = -9999f;
        private WindowViewService _windowViewService;
        private bool _isInitialized = false;

        // ========== 互斥组定义 ==========
        private static readonly HashSet<WindowViewType> BaseTimeWeather = new HashSet<WindowViewType>
        {
            WindowViewType.Day,
            WindowViewType.Sunset,
            WindowViewType.Night,
            WindowViewType.Cloudy
        };

        private static readonly HashSet<WindowViewType> PrecipitationWeather = new HashSet<WindowViewType>
        {
            WindowViewType.LightRain,
            WindowViewType.HeavyRain,
            WindowViewType.ThunderRain
        };

        // ========== 初始化 ==========
        public override void OnInitializeMelon()
        {
            _configCategory = MelonPreferences.CreateCategory("ChillEnvSync");
            _enableWeatherSync = _configCategory.CreateEntry("EnableWeatherSync", true, "启用天气同步");
            _cityName = _configCategory.CreateEntry("CityName", "北京", "城市名称");
            _apiKey = _configCategory.CreateEntry("ApiKey", "S-xxxxxxxx", "心知天气API密钥");
            _syncIntervalMinutes = _configCategory.CreateEntry("SyncIntervalMinutes", 30, "同步间隔(分钟)");

            MelonLogger.Msg("Chill Env Sync 已加载");
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            _isInitialized = false;
            _windowViewService = null;
            MelonLogger.Msg($"场景加载: {sceneName}");
        }

        public override void OnUpdate()
        {
            if (!_enableWeatherSync.Value) return;

            // 尝试初始化
            if (!_isInitialized)
            {
                TryInitialize();
                return;
            }

            // 定时同步
            float interval = _syncIntervalMinutes.Value * 60f;
            if (Time.time - _lastSyncTime >= interval)
            {
                _lastSyncTime = Time.time;
                MelonCoroutines.Start(FetchAndApplyWeather());
            }
        }

        private void TryInitialize()
        {
            try
            {
                _windowViewService = UnityEngine.Object.FindObjectOfType<WindowViewService>();
                if (_windowViewService != null)
                {
                    _isInitialized = true;
                    MelonLogger.Msg("WindowViewService 已找到，初始化完成");

                    // 立即执行一次同步
                    _lastSyncTime = Time.time;
                    MelonCoroutines.Start(FetchAndApplyWeather());
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"初始化失败: {ex.Message}");
            }
        }

        // ========== 天气获取 ==========
        private IEnumerator FetchAndApplyWeather()
        {
            string city = _cityName.Value;
            string apiKey = _apiKey.Value;

            MelonLogger.Msg($"请求天气: {city}");

            string url = $"https://api.seniverse.com/v3/weather/now.json?key={apiKey}&location={UnityWebRequest.EscapeURL(city)}&language=zh-Hans&unit=c";

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    MelonLogger.Error($"天气请求失败: {request.error}");
                    yield break;
                }

                string json = request.downloadHandler.text;
                MelonLogger.Msg($"天气API返回: {json}");

                // 手动解析JSON
                var weatherData = ParseWeatherJson(json);
                if (weatherData.HasValue)
                {
                    var data = weatherData.Value;
                    MelonLogger.Msg($"天气解析成功: {data.Text}, {data.Temperature}°C, Code={data.Code}");

                    // 判断时段
                    bool isDay = IsDaytime();
                    string timeOfDay = isDay ? "Day" : "Night";
                    MelonLogger.Msg($"[天气决策] {data.Text} + 当前时间 -> {timeOfDay}");

                    // 应用天气
                    ApplyWeather(data.Code, isDay);
                }
                else
                {
                    MelonLogger.Error("天气解析失败");
                }
            }
        }

        private struct WeatherData
        {
            public string Text;
            public string Code;
            public string Temperature;
        }

        private WeatherData? ParseWeatherJson(string json)
        {
            try
            {
                // 使用正则提取
                var textMatch = Regex.Match(json, "\"text\"\\s*:\\s*\"([^\"]+)\"");
                var codeMatch = Regex.Match(json, "\"code\"\\s*:\\s*\"([^\"]+)\"");
                var tempMatch = Regex.Match(json, "\"temperature\"\\s*:\\s*\"([^\"]+)\"");

                if (textMatch.Success && codeMatch.Success && tempMatch.Success)
                {
                    return new WeatherData
                    {
                        Text = textMatch.Groups[1].Value,
                        Code = codeMatch.Groups[1].Value,
                        Temperature = tempMatch.Groups[1].Value
                    };
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"JSON解析异常: {ex.Message}");
            }
            return null;
        }

        private bool IsDaytime()
        {
            int hour = DateTime.Now.Hour;
            return hour >= 6 && hour < 18;
        }

        // ========== 天气映射 ==========
        private struct WeatherMapping
        {
            public WindowViewType BaseEnvironment;           // 基础环境 (Day/Night/Sunset/Cloudy)
            public WindowViewType? PrecipitationType;        // 降水类型 (可选)
            public WindowViewType? SpecialEffect;            // 特殊效果 (如Snow)
            public bool ClearAllPrecipitation;               // 是否清除所有降水
        }

        private WeatherMapping GetWeatherMapping(string weatherCode, bool isDay)
        {
            WindowViewType baseEnv = isDay ? WindowViewType.Day : WindowViewType.Night;

            switch (weatherCode)
            {
                // ===== 晴天 =====
                case "0":  // 晴
                case "1":  // 晴（夜间）
                case "2":  // 晴
                case "3":  // 晴（夜间）
                    return new WeatherMapping
                    {
                        BaseEnvironment = baseEnv,
                        PrecipitationType = null,
                        SpecialEffect = null,
                        ClearAllPrecipitation = true
                    };

                // ===== 多云/阴 =====
                case "4":  // 多云
                case "5":  // 晴间多云（夜）
                case "6":  // 晴间多云
                case "7":  // 大部多云（夜）
                case "8":  // 大部多云
                case "9":  // 阴
                    return new WeatherMapping
                    {
                        BaseEnvironment = WindowViewType.Cloudy,
                        PrecipitationType = null,
                        SpecialEffect = null,
                        ClearAllPrecipitation = true
                    };

                // ===== 小雨/阵雨 =====
                case "10": // 阵雨
                case "13": // 小雨
                case "14": // 中雨
                case "19": // 冻雨
                    return new WeatherMapping
                    {
                        BaseEnvironment = WindowViewType.Cloudy,
                        PrecipitationType = WindowViewType.LightRain,
                        SpecialEffect = null,
                        ClearAllPrecipitation = false
                    };

                // ===== 大雨/暴雨 =====
                case "15": // 大雨
                case "16": // 暴雨
                case "17": // 大暴雨
                case "18": // 特大暴雨
                    return new WeatherMapping
                    {
                        BaseEnvironment = WindowViewType.Cloudy,
                        PrecipitationType = WindowViewType.HeavyRain,
                        SpecialEffect = null,
                        ClearAllPrecipitation = false
                    };

                // ===== 雷雨 =====
                case "11": // 雷阵雨
                case "12": // 雷阵雨伴有冰雹
                    return new WeatherMapping
                    {
                        BaseEnvironment = WindowViewType.Cloudy,
                        PrecipitationType = WindowViewType.ThunderRain,
                        SpecialEffect = null,
                        ClearAllPrecipitation = false
                    };

                // ===== 雪 =====
                case "20": // 雨夹雪
                case "21": // 小雪
                case "22": // 中雪
                case "23": // 雨夹雪
                case "24": // 大雪
                case "25": // 暴雪
                    return new WeatherMapping
                    {
                        BaseEnvironment = WindowViewType.Cloudy,
                        PrecipitationType = null,
                        SpecialEffect = WindowViewType.Snow,
                        ClearAllPrecipitation = true
                    };

                // ===== 雾/霾/沙尘 =====
                case "30": // 雾
                case "31": // 霾
                case "32": // 扬沙
                case "33": // 浮尘
                case "34": // 沙尘暴
                case "35": // 强沙尘暴
                    return new WeatherMapping
                    {
                        BaseEnvironment = WindowViewType.Cloudy,
                        PrecipitationType = null,
                        SpecialEffect = null,
                        ClearAllPrecipitation = true
                    };

                // ===== 默认 =====
                default:
                    MelonLogger.Warning($"未知天气代码: {weatherCode}, 使用默认映射");
                    return new WeatherMapping
                    {
                        BaseEnvironment = baseEnv,
                        PrecipitationType = null,
                        SpecialEffect = null,
                        ClearAllPrecipitation = true
                    };
            }
        }

        // ========== 应用天气（核心逻辑） ==========
        private void ApplyWeather(string weatherCode, bool isDay)
        {
            if (_windowViewService == null)
            {
                MelonLogger.Error("WindowViewService 未初始化");
                return;
            }

            var mapping = GetWeatherMapping(weatherCode, isDay);

            MelonLogger.Msg($"[应用天气] 基础环境={mapping.BaseEnvironment}, " +
                           $"降水={mapping.PrecipitationType?.ToString() ?? "无"}, " +
                           $"特效={mapping.SpecialEffect?.ToString() ?? "无"}, " +
                           $"清除降水={mapping.ClearAllPrecipitation}");

            try
            {
                // 1. 处理基础环境（Day/Sunset/Night/Cloudy 互斥）
                ApplyBaseEnvironment(mapping.BaseEnvironment);

                // 2. 处理降水（互斥）
                ApplyPrecipitation(mapping.PrecipitationType, mapping.ClearAllPrecipitation);

                // 3. 处理特殊效果（如雪）
                if (mapping.SpecialEffect.HasValue)
                {
                    ApplySpecialEffect(mapping.SpecialEffect.Value);
                }

                // 4. 保存到存档
                SaveEnvironmentState(mapping);

                MelonLogger.Msg("[应用天气] 完成");
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"应用天气失败: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private void ApplyBaseEnvironment(WindowViewType target)
        {
            // 检查当前状态
            WindowViewType? currentBase = null;
            foreach (var env in BaseTimeWeather)
            {
                if (_windowViewService.IsActiveWindow(env))
                {
                    currentBase = env;
                    break;
                }
            }

            if (currentBase == target)
            {
                MelonLogger.Msg($"[基础环境] {target} 已激活，跳过");
                return;
            }

            // 使用 ChangeWeatherAndTime，它会自动处理互斥
            MelonLogger.Msg($"[基础环境] {currentBase?.ToString() ?? "无"} -> {target}");
            _windowViewService.ChangeWeatherAndTime(target);
        }

        private void ApplyPrecipitation(WindowViewType? target, bool clearAll)
        {
            if (clearAll)
            {
                // 清除所有降水
                foreach (var precip in PrecipitationWeather)
                {
                    if (_windowViewService.IsActiveWindow(precip))
                    {
                        MelonLogger.Msg($"[降水] 关闭: {precip}");
                        _windowViewService.DeactivateWindow(precip);
                    }
                }
            }
            else if (target.HasValue)
            {
                // 关闭其他降水，激活目标降水
                foreach (var precip in PrecipitationWeather)
                {
                    bool isActive = _windowViewService.IsActiveWindow(precip);
                    bool shouldBeActive = (precip == target.Value);

                    if (shouldBeActive && !isActive)
                    {
                        MelonLogger.Msg($"[降水] 激活: {precip}");
                        _windowViewService.ActivateWindow(precip);
                    }
                    else if (!shouldBeActive && isActive)
                    {
                        MelonLogger.Msg($"[降水] 关闭: {precip}");
                        _windowViewService.DeactivateWindow(precip);
                    }
                }
            }
        }

        private void ApplySpecialEffect(WindowViewType effect)
        {
            if (!_windowViewService.IsActiveWindow(effect))
            {
                MelonLogger.Msg($"[特效] 激活: {effect}");
                _windowViewService.ActivateWindow(effect);
            }
        }

        private void SaveEnvironmentState(WeatherMapping mapping)
        {
            try
            {
                var saveData = SaveDataManager.Instance;
                if (saveData?.WindowViewDic == null) return;

                // 保存基础环境状态
                foreach (var env in BaseTimeWeather)
                {
                    if (saveData.WindowViewDic.ContainsKey(env))
                    {
                        saveData.WindowViewDic[env].IsActive = (env == mapping.BaseEnvironment);
                    }
                }

                // 保存降水状态
                foreach (var precip in PrecipitationWeather)
                {
                    if (saveData.WindowViewDic.ContainsKey(precip))
                    {
                        bool shouldBeActive = mapping.PrecipitationType.HasValue &&
                                             mapping.PrecipitationType.Value == precip;
                        saveData.WindowViewDic[precip].IsActive = shouldBeActive;
                    }
                }

                // 保存特效状态
                if (mapping.SpecialEffect.HasValue &&
                    saveData.WindowViewDic.ContainsKey(mapping.SpecialEffect.Value))
                {
                    saveData.WindowViewDic[mapping.SpecialEffect.Value].IsActive = true;
                }

                saveData.SaveEnviroment();
                MelonLogger.Msg("[存档] 环境状态已保存");
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"保存存档失败: {ex.Message}");
            }
        }
    }
}