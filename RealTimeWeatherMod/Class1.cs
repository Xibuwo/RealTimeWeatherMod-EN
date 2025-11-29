using BepInEx;
using BepInEx.Logging;
using Bulbul;
using HarmonyLib;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace ChillEnvMod
{
    [BepInPlugin("com.chillenv.plugin", "ChillEnv", "1.0.0")]
    public class ChillEnvPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static FacilityEnviroment Facility;
        private static Harmony _harmony;

        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("[ChillEnv] 插件加载中...");

            _harmony = new Harmony("com.chillenv.plugin");
            _harmony.PatchAll();

            Log.LogInfo("[ChillEnv] Harmony 补丁已应用");
        }

        private void Start()
        {
            AutoEnvRunner.StartLoop();
        }

        private void Update()
        {
            // 执行需要在主线程运行的操作
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try { action?.Invoke(); }
                catch (Exception ex) { Log?.LogError("[MainThread] 执行队列操作失败: " + ex.Message); }
            }
        }

        internal static void EnqueueOnMainThread(Action action)
        {
            if (action != null) _mainThreadQueue.Enqueue(action);
        }
    }

    // ============ 捕获 FacilityEnviroment 实例 ============
    [HarmonyPatch(typeof(FacilityEnviroment), "Setup")]
    internal static class FacilityEnviromentSetupPatch
    {
        static void Postfix(FacilityEnviroment __instance)
        {
            ChillEnvPlugin.Facility = __instance;
            ChillEnvPlugin.Log?.LogInfo("[AutoEnv] 捕获 FacilityEnviroment 实例");
        }
    }

    // ============ 主运行器 ============
    internal static class AutoEnvRunner
    {
        // Open-Meteo API（无需 API Key）
        private const string API_URL =
            "https://api.open-meteo.com/v1/forecast?latitude=31.23&longitude=121.47&current=weather_code,is_day&timezone=auto";

        private const int CHECK_INTERVAL_SECONDS = 300; // 5分钟检查一次

        // ============ 环境分组 ============
        private static readonly WindowViewType[] PrecipitationViews =
        {
            WindowViewType.LightRain,
            WindowViewType.HeavyRain,
            WindowViewType.ThunderRain
        };

        // ============ 主循环 ============
        public static void StartLoop()
        {
            ChillEnvPlugin.Log?.LogInfo("[AutoEnv] 等待游戏初始化...");
            Task.Run(async () =>
            {
                // 等待 FacilityEnviroment 被捕获
                while (ChillEnvPlugin.Facility == null)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                }

                ChillEnvPlugin.Log?.LogInfo("[AutoEnv] 开始天气同步循环");

                while (true)
                {
                    try
                    {
                        await FetchAndApplyWeather();
                    }
                    catch (Exception ex)
                    {
                        ChillEnvPlugin.Log?.LogError($"[AutoEnv] 天气同步出错: {ex.Message}");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(CHECK_INTERVAL_SECONDS));
                }
            });
        }

        // ============ 获取并应用天气 ============
        private static async Task FetchAndApplyWeather()
        {
            ChillEnvPlugin.Log?.LogInfo("[AutoEnv] 正在获取天气数据...");

            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.GetStringAsync(API_URL);

                // 简单解析 JSON（避免依赖 Newtonsoft）
                // 示例: {"current":{"weather_code":0,"is_day":1,...}}
                int weatherCode = TryExtractInt(response, "\\\"weather_code\\\"\\s*:\\s*(?<v>-?\\d+)");
                int isDay = TryExtractInt(response, "\\\"is_day\\\"\\s*:\\s*(?<v>-?\\d+)");

                ChillEnvPlugin.Log?.LogInfo($"[AutoEnv] 天气代码={weatherCode}, 白天={isDay}");

                // 在主线程应用环境
                ChillEnvPlugin.EnqueueOnMainThread(() =>
                {
                    ApplyWeather(weatherCode, isDay == 1);
                });
            }
        }

        private static int TryExtractInt(string text, string pattern)
        {
            try
            {
                var m = Regex.Match(text, pattern);
                if (m.Success)
                {
                    int v;
                    if (int.TryParse(m.Groups["v"].Value, out v)) return v;
                }
            }
            catch { }
            return 0;
        }

        // ============ 状态检查 ============
        private static bool IsWindowActive(WindowViewType type)
        {
            var dic = SaveDataManager.Instance?.WindowViewDic;
            if (dic == null) return false;

            WindowViewData data;
            return dic.TryGetValue(type, out data) && data.IsActive;
        }

        private static void LogCurrentState()
        {
            var activePrecip = new List<string>();
            foreach (var t in PrecipitationViews)
            {
                if (IsWindowActive(t))
                    activePrecip.Add(t.ToString());
            }

            var timeState = "未知";
            if (IsWindowActive(WindowViewType.Day)) timeState = "Day";
            else if (IsWindowActive(WindowViewType.Sunset)) timeState = "Sunset";
            else if (IsWindowActive(WindowViewType.Night)) timeState = "Night";
            else if (IsWindowActive(WindowViewType.Cloudy)) timeState = "Cloudy";

            bool snowActive = IsWindowActive(WindowViewType.Snow);

            ChillEnvPlugin.Log?.LogInfo($"[状态] 时间={timeState}, 降水={(activePrecip.Count == 0 ? "无" : string.Join(", ", activePrecip))}, 雪景={snowActive}");
        }

        // ============ 清除所有降水 ============
        private static void ClearPrecipitation(FacilityEnviroment fac)
        {
            foreach (var w in PrecipitationViews)
            {
                if (IsWindowActive(w))
                {
                    ChillEnvPlugin.Log?.LogInfo($"[操作] 关闭降水: {w}");
                    fac.ChangeWindowView(ChangeType.Deactivate, w);
                }
            }
        }

        // ============ 设置单一降水类型 ============
        private static void SetPrecipitation(FacilityEnviroment fac, WindowViewType target)
        {
            // 先关闭其他降水
            foreach (var w in PrecipitationViews)
            {
                if (w != target && IsWindowActive(w))
                {
                    ChillEnvPlugin.Log?.LogInfo($"[操作] 关闭其他降水: {w}");
                    fac.ChangeWindowView(ChangeType.Deactivate, w);
                }
            }

            // 激活目标降水
            if (!IsWindowActive(target))
            {
                ChillEnvPlugin.Log?.LogInfo($"[操作] 激活降水: {target}");
                fac.ChangeWindowView(ChangeType.Activate, target);
            }
        }

        // ============ 设置基础时间 ============
        private static void SetBaseTime(FacilityEnviroment fac, WindowViewType target)
        {
            // 检查是否已经是目标状态
            if (IsWindowActive(target))
            {
                ChillEnvPlugin.Log?.LogInfo($"[跳过] 时间已是: {target}");
                return;
            }

            ChillEnvPlugin.Log?.LogInfo($"[操作] 切换时间: {target}");

            switch (target)
            {
                case WindowViewType.Day:
                    fac.OnClickButtonChangeTimeDay();
                    break;
                case WindowViewType.Sunset:
                    fac.OnClickButtonChangeTimeSunset();
                    break;
                case WindowViewType.Night:
                    fac.OnClickButtonChangeTimeNight();
                    break;
                case WindowViewType.Cloudy:
                    fac.OnClickButtonChangeTimeCloudy();
                    break;
            }
        }

        // ============ 主应用方法 ============
        private static void ApplyWeather(int weatherCode, bool isDay)
        {
            var fac = ChillEnvPlugin.Facility;
            if (fac == null)
            {
                ChillEnvPlugin.Log?.LogWarning("[AutoEnv] FacilityEnviroment 为空，跳过");
                return;
            }

            // 记录当前状态
            LogCurrentState();

            ChillEnvPlugin.Log?.LogInfo($"[AutoEnv] 应用天气: Code={weatherCode}, IsDay={isDay}");

            // Open-Meteo WMO Weather Code 解析
            switch (weatherCode)
            {
                // ======== 晴天 (0-3) ========
                case 0:  // Clear sky
                case 1:  // Mainly clear
                case 2:  // Partly cloudy
                case 3:  // Overcast
                    if (weatherCode <= 1)
                    {
                        // 真正的晴天
                        SetBaseTime(fac, isDay ? WindowViewType.Day : WindowViewType.Night);
                    }
                    else
                    {
                        // 多云/阴天
                        SetBaseTime(fac, WindowViewType.Cloudy);
                    }
                    ClearPrecipitation(fac);
                    ClearSnowIfNotSnowing(fac, weatherCode);
                    break;

                // ======== 雾 (45, 48) ========
                case 45: // Fog
                case 48: // Depositing rime fog
                    SetBaseTime(fac, WindowViewType.Cloudy);
                    ClearPrecipitation(fac);
                    break;

                // ======== 毛毛雨 (51, 53, 55, 56, 57) ========
                case 51: // Light drizzle
                case 53: // Moderate drizzle
                case 55: // Dense drizzle
                case 56: // Light freezing drizzle
                case 57: // Dense freezing drizzle
                    SetBaseTime(fac, WindowViewType.Cloudy);
                    SetPrecipitation(fac, WindowViewType.LightRain);
                    break;

                // ======== 雨 (61, 63, 65, 66, 67, 80, 81, 82) ========
                case 61: // Slight rain
                    SetBaseTime(fac, WindowViewType.Cloudy);
                    SetPrecipitation(fac, WindowViewType.LightRain);
                    break;
                case 63: // Moderate rain
                case 65: // Heavy rain
                case 66: // Light freezing rain
                case 67: // Heavy freezing rain
                    SetBaseTime(fac, WindowViewType.Cloudy);
                    SetPrecipitation(fac, WindowViewType.HeavyRain);
                    break;
                case 80: // Slight rain showers
                    SetBaseTime(fac, WindowViewType.Cloudy);
                    SetPrecipitation(fac, WindowViewType.LightRain);
                    break;
                case 81: // Moderate rain showers
                case 82: // Violent rain showers
                    SetBaseTime(fac, WindowViewType.Cloudy);
                    SetPrecipitation(fac, WindowViewType.HeavyRain);
                    break;

                // ======== 雪 (71, 73, 75, 77, 85, 86) ========
                case 71: // Slight snow fall
                case 73: // Moderate snow fall
                case 75: // Heavy snow fall
                case 77: // Snow grains
                case 85: // Slight snow showers
                case 86: // Heavy snow showers
                    SetBaseTime(fac, WindowViewType.Cloudy);
                    ClearPrecipitation(fac);
                    // 激活雪景
                    if (!IsWindowActive(WindowViewType.Snow))
                    {
                        ChillEnvPlugin.Log?.LogInfo("[操作] 激活雪景");
                        fac.ChangeWindowView(ChangeType.Activate, WindowViewType.Snow);
                    }
                    break;

                // ======== 雷雨 (95, 96, 99) ========
                case 95: // Thunderstorm
                case 96: // Thunderstorm with slight hail
                case 99: // Thunderstorm with heavy hail
                    SetBaseTime(fac, WindowViewType.Cloudy);
                    SetPrecipitation(fac, WindowViewType.ThunderRain);
                    break;

                // ======== 默认 ========
                default:
                    ChillEnvPlugin.Log?.LogWarning($"[AutoEnv] 未知天气代码: {weatherCode}，使用默认");
                    SetBaseTime(fac, isDay ? WindowViewType.Day : WindowViewType.Night);
                    ClearPrecipitation(fac);
                    break;
            }

            // 再次记录状态
            LogCurrentState();
        }

        // ============ 非雪天时关闭雪景 ============
        private static void ClearSnowIfNotSnowing(FacilityEnviroment fac, int weatherCode)
        {
            // 只有雪天代码才保留雪景
            bool isSnowWeather = weatherCode == 71 || weatherCode == 73 || weatherCode == 75
                              || weatherCode == 77 || weatherCode == 85 || weatherCode == 86;

            if (!isSnowWeather && IsWindowActive(WindowViewType.Snow))
            {
                ChillEnvPlugin.Log?.LogInfo("[操作] 关闭雪景（非雪天）");
                fac.ChangeWindowView(ChangeType.Deactivate, WindowViewType.Snow);
            }
        }
    }
}