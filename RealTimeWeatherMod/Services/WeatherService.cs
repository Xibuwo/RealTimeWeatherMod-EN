using System;
using System.Collections;
using UnityEngine.Networking;
using ChillWithYou.EnvSync.Models;
using Bulbul;
using UnityEngine;

namespace ChillWithYou.EnvSync.Services
{
    public class WeatherService
    {
        private static WeatherInfo _cachedWeather;
        private static DateTime _lastFetchTime;
        private static string _lastLocation;
        private static TimeSpan CacheExpiry => TimeSpan.FromMinutes(Mathf.Max(1, ChillEnvPlugin.Cfg_WeatherRefreshMinutes.Value)); public static WeatherInfo CachedWeather => _cachedWeather;
        private static readonly string _encryptedDefaultKey = "7Mr4YSR87bFvE4zDgj6NbuBKgz4EiPYEnRTQ0RIaeSU=";
        public static bool HasDefaultKey => !string.IsNullOrEmpty(_encryptedDefaultKey);
        private static int _fetchGeneration = 0;   // incremented to cancel in-flight fetches

        private static string NormalizeLocation(string location)
        {
            return location?.Trim() ?? string.Empty;
        }

        private static bool HasValidCacheNormalized(string normalizedLocation)
        {
            return _cachedWeather != null
                && DateTime.Now - _lastFetchTime < CacheExpiry
                && string.Equals(_lastLocation, normalizedLocation,
                                 StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Returns true if there is a valid cached result for this location.</summary>
        public static bool HasValidCache(string location)
        {
            return HasValidCacheNormalized(NormalizeLocation(location));
        }

        /// <summary>
        /// Returns true and fills <paramref name="seconds"/> with how many seconds
        /// remain before the cache expires for this location.
        /// </summary>
        public static bool TryGetCacheRemainingSeconds(string location, out float seconds)
        {
            seconds = 0f;
            string norm = NormalizeLocation(location);
            if (!HasValidCacheNormalized(norm)) return false;

            TimeSpan elapsed = DateTime.Now - _lastFetchTime;
            TimeSpan remaining = CacheExpiry - elapsed;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            seconds = (float)remaining.TotalSeconds;
            return true;
        }

        /// <summary>Clears the weather cache (e.g. when the city is changed).</summary>
        public static void InvalidateCache()
        {
            ++_fetchGeneration;      // cancel any in-flight fetch
            _cachedWeather = null;
            _lastLocation = null;
            _lastFetchTime = DateTime.MinValue;
        }

        public static IEnumerator FetchWeather(string apiKey, string location, bool force, Action<WeatherInfo> onComplete)
        {
            string normalizedLocation = NormalizeLocation(location);

            if (!force && HasValidCacheNormalized(normalizedLocation))
            {
                onComplete?.Invoke(_cachedWeather);
                yield break;
            }

            // Stamp this fetch with the current generation; any older fetch will self-abort
            int myGeneration = ++_fetchGeneration;

            string provider = ChillEnvPlugin.Cfg_WeatherProvider.Value;

            if (provider.Equals("OpenWeather", StringComparison.OrdinalIgnoreCase))
                yield return FetchOpenWeather(apiKey, normalizedLocation, myGeneration, onComplete);
            else if (provider.Equals("OpenMeteo", StringComparison.OrdinalIgnoreCase))
                yield return FetchOpenMeteoWeather(normalizedLocation, myGeneration, onComplete);
            else
                yield return FetchSeniverseWeather(apiKey, normalizedLocation, myGeneration, onComplete);
        }

        private static IEnumerator FetchSeniverseWeather(string apiKey, string location, int generation, Action<WeatherInfo> onComplete)
        {
            string finalKey = apiKey;
            if (string.IsNullOrEmpty(finalKey) && HasDefaultKey)
                finalKey = KeySecurity.Decrypt(_encryptedDefaultKey);

            if (string.IsNullOrEmpty(finalKey))
            {
                ChillEnvPlugin.Log?.LogWarning("[API] No API Key configured and no built-in key");
                onComplete?.Invoke(null);
                yield break;
            }

            string url = $"https://api.seniverse.com/v3/weather/now.json"
                       + $"?key={finalKey}&location="
                       + $"{UnityEngine.Networking.UnityWebRequest.EscapeURL(location)}"
                       + $"&language=zh-Hans&unit=c";

            ChillEnvPlugin.Log?.LogInfo($"[API] Seniverse request: {location}");

            using (UnityEngine.Networking.UnityWebRequest request =
                   UnityEngine.Networking.UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (generation != _fetchGeneration)
                {
                    ChillEnvPlugin.Log?.LogInfo(
                        $"[API] Seniverse fetch gen {generation} superseded, discarding.");
                    onComplete?.Invoke(null);
                    yield break;
                }

                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success
                    || string.IsNullOrEmpty(request.downloadHandler.text))
                {
                    ChillEnvPlugin.Log?.LogWarning($"[API] Request failed: {request.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    var weather = ParseSeniverseJson(request.downloadHandler.text);
                    if (weather != null)
                    {
                        _cachedWeather = weather;
                        _lastFetchTime = DateTime.Now;
                        _lastLocation = NormalizeLocation(location);
                        ChillEnvPlugin.Log?.LogInfo($"[API] Data updated: {weather}");
                        onComplete?.Invoke(weather);
                    }
                    else
                    {
                        ChillEnvPlugin.Log?.LogWarning("[API] Parse failed");
                        onComplete?.Invoke(null);
                    }
                }
                catch { onComplete?.Invoke(null); }
            }
        }

        private static IEnumerator FetchOpenWeather(string apiKey, string location, int generation, Action<WeatherInfo> onComplete)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                ChillEnvPlugin.Log?.LogWarning("[API] OpenWeather requires an API Key");
                onComplete?.Invoke(null);
                yield break;
            }

            string finalLocation = location.Trim();

            if (!finalLocation.Contains(","))
            {
                bool geocodingComplete = false;
                string resolvedCoords = null;

                yield return FetchCoordinatesFromCityName(apiKey, finalLocation, (coords) =>
                {
                    geocodingComplete = true;
                    resolvedCoords = coords;
                });

                // ← Abort if a newer fetch was started while we were geocoding
                if (generation != _fetchGeneration)
                {
                    ChillEnvPlugin.Log?.LogInfo(
                        $"[API] Fetch gen {generation} superseded by {_fetchGeneration}, aborting.");
                    onComplete?.Invoke(null);
                    yield break;
                }

                if (string.IsNullOrEmpty(resolvedCoords))
                {
                    ChillEnvPlugin.Log?.LogError(
                        $"[API] Failed to resolve city: {finalLocation}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                finalLocation = resolvedCoords;
            }

            string[] parts = finalLocation.Replace(" ", "").Split(',');
            if (parts.Length != 2)
            {
                ChillEnvPlugin.Log?.LogWarning($"[API] Invalid location format: {finalLocation}");
                onComplete?.Invoke(null);
                yield break;
            }

            string lat = parts[0].Trim();
            string lon = parts[1].Trim();
            string url = $"https://api.openweathermap.org/data/2.5/weather"
                       + $"?lat={lat}&lon={lon}&appid={apiKey}&units=metric";

            ChillEnvPlugin.Log?.LogInfo($"[API] OpenWeather request: lat={lat}, lon={lon}");

            using (UnityEngine.Networking.UnityWebRequest request =
                   UnityEngine.Networking.UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                // ← Abort if superseded
                if (generation != _fetchGeneration)
                {
                    ChillEnvPlugin.Log?.LogInfo(
                        $"[API] Fetch gen {generation} superseded, discarding result.");
                    onComplete?.Invoke(null);
                    yield break;
                }

                if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success
                    || string.IsNullOrEmpty(request.downloadHandler.text))
                {
                    ChillEnvPlugin.Log?.LogError(
                        $"[API] OpenWeather request failed: {request.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    var weather = ParseOpenWeatherJson(request.downloadHandler.text);
                    if (weather != null)
                    {
                        _cachedWeather = weather;
                        _lastFetchTime = DateTime.Now;
                        _lastLocation = NormalizeLocation(location);
                        ChillEnvPlugin.Log?.LogInfo($"[API] OpenWeather data updated: {weather}");
                        onComplete?.Invoke(weather);
                    }
                    else
                    {
                        ChillEnvPlugin.Log?.LogWarning("[API] OpenWeather parse returned null");
                        onComplete?.Invoke(null);
                    }
                }
                catch (Exception ex)
                {
                    ChillEnvPlugin.Log?.LogError($"[API] OpenWeather parse error: {ex.Message}");
                    onComplete?.Invoke(null);
                }
            }
        }
        // ── Open-Meteo geocoding ──────────────────────────────────────────
        private static IEnumerator FetchOpenMeteoCoordinates(string cityName, Action<string> onComplete)
        {
            string url = $"https://geocoding-api.open-meteo.com/v1/search?name={UnityWebRequest.EscapeURL(cityName)}&count=1&language=en&format=json";
            ChillEnvPlugin.Log?.LogInfo($"[OpenMeteo Geocoding] Resolving city: {cityName}");

            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(req.downloadHandler.text))
                {
                    ChillEnvPlugin.Log?.LogWarning($"[OpenMeteo Geocoding] Request failed: {req.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    string json = req.downloadHandler.text;
                    // Response: {"results":[{"latitude":40.71,"longitude":-74.01,...}]}
                    if (json.Contains("\"results\":[]") || !json.Contains("\"latitude\""))
                    {
                        ChillEnvPlugin.Log?.LogWarning($"[OpenMeteo Geocoding] No results for '{cityName}'");
                        onComplete?.Invoke(null);
                        yield break;
                    }

                    string latStr = ExtractStringValue(json, "\"latitude\":", ",");
                    string lonStr = ExtractStringValue(json, "\"longitude\":", ",");

                    if (string.IsNullOrEmpty(latStr) || string.IsNullOrEmpty(lonStr))
                    {
                        ChillEnvPlugin.Log?.LogWarning("[OpenMeteo Geocoding] Could not parse lat/lon");
                        onComplete?.Invoke(null);
                        yield break;
                    }

                    string coords = $"{latStr.Trim()},{lonStr.Trim()}";
                    ChillEnvPlugin.Log?.LogInfo($"[OpenMeteo Geocoding] Resolved '{cityName}' to {coords}");
                    onComplete?.Invoke(coords);
                }
                catch (Exception ex)
                {
                    ChillEnvPlugin.Log?.LogError($"[OpenMeteo Geocoding] Parse error: {ex.Message}");
                    onComplete?.Invoke(null);
                }
            }
        }

        // ── Open-Meteo weather fetch ──────────────────────────────────────
        private static IEnumerator FetchOpenMeteoWeather(string location, int generation, Action<WeatherInfo> onComplete)
        {
            string finalLocation = location.Trim();

            // Geocode city names
            if (!finalLocation.Contains(","))
            {
                string resolvedCoords = null;
                yield return FetchOpenMeteoCoordinates(finalLocation, (coords) => { resolvedCoords = coords; });

                if (generation != _fetchGeneration) { onComplete?.Invoke(null); yield break; }
                if (string.IsNullOrEmpty(resolvedCoords))
                {
                    ChillEnvPlugin.Log?.LogError($"[OpenMeteo] Could not resolve city: {finalLocation}");
                    onComplete?.Invoke(null);
                    yield break;
                }
                finalLocation = resolvedCoords;
            }

            string[] parts = finalLocation.Replace(" ", "").Split(',');
            if (parts.Length != 2) { onComplete?.Invoke(null); yield break; }

            string lat = parts[0].Trim();
            string lon = parts[1].Trim();

            // current_weather gives: temperature, weathercode, is_day
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}&current_weather=true&temperature_unit=celsius";

            ChillEnvPlugin.Log?.LogInfo($"[OpenMeteo] Fetching weather: lat={lat} lon={lon}");

            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (generation != _fetchGeneration) { onComplete?.Invoke(null); yield break; }

                if (req.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(req.downloadHandler.text))
                {
                    ChillEnvPlugin.Log?.LogError($"[OpenMeteo] Request failed: {req.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    string json = req.downloadHandler.text;
                    // {"current_weather":{"temperature":18.5,"windspeed":12.1,"weathercode":3,"is_day":1,...}}
                    int cwIndex = json.IndexOf("\"current_weather\"");
                    if (cwIndex < 0) { onComplete?.Invoke(null); yield break; }
                    string cwSection = json.Substring(cwIndex);

                    string tempStr = ExtractStringValue(cwSection, "\"temperature\":", ",");
                    string wmoStr = ExtractStringValue(cwSection, "\"weathercode\":", ",");

                    float tempFloat = 0;
                    float.TryParse(tempStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out tempFloat);

                    int wmoCode = 0;
                    // Try with comma first, fall back without (last field in object)
                    if (string.IsNullOrEmpty(wmoStr))
                        wmoStr = ExtractStringValue(cwSection, "\"weathercode\":", "}");
                    int.TryParse(wmoStr?.Trim(), out wmoCode);

                    int internalCode = MapWmoCodeToInternalCode(wmoCode);
                    string description = WmoCodeToDescription(wmoCode);

                    ChillEnvPlugin.Log?.LogDebug($"[OpenMeteo] wmo={wmoCode} temp={tempFloat} -> internal={internalCode}");

                    var weather = new WeatherInfo
                    {
                        Code = internalCode,
                        Text = description,
                        Temperature = (int)Math.Round(tempFloat),
                        Condition = MapSeniverseCodeToCondition(internalCode),
                        UpdateTime = DateTime.Now
                    };

                    _cachedWeather = weather;
                    _lastFetchTime = DateTime.Now;
                    _lastLocation = NormalizeLocation(location);
                    ChillEnvPlugin.Log?.LogInfo($"[OpenMeteo] Data updated: {weather}");
                    onComplete?.Invoke(weather);
                }
                catch (Exception ex)
                {
                    ChillEnvPlugin.Log?.LogError($"[OpenMeteo] Parse error: {ex.Message}");
                    onComplete?.Invoke(null);
                }
            }
        }

        // ── Open-Meteo sun schedule ───────────────────────────────────────
        private static IEnumerator FetchOpenMeteoSunSchedule(string location, Action<SunData> onComplete)
        {
            string finalLocation = location.Trim();

            if (!finalLocation.Contains(","))
            {
                string resolvedCoords = null;
                yield return FetchOpenMeteoCoordinates(finalLocation, (coords) => { resolvedCoords = coords; });
                if (string.IsNullOrEmpty(resolvedCoords)) { onComplete?.Invoke(null); yield break; }
                finalLocation = resolvedCoords;
            }

            string[] parts = finalLocation.Replace(" ", "").Split(',');
            if (parts.Length != 2) { onComplete?.Invoke(null); yield break; }

            string lat = parts[0].Trim();
            string lon = parts[1].Trim();
            string today = DateTime.Now.ToString("yyyy-MM-dd");

            // daily=sunrise,sunset returns local times as strings like "2026-06-07T06:12"
            string url = $"https://api.open-meteo.com/v1/forecast?latitude={lat}&longitude={lon}" +
                         $"&daily=sunrise,sunset&timezone=auto&start_date={today}&end_date={today}";

            ChillEnvPlugin.Log?.LogInfo($"[OpenMeteo SunSync] Fetching sunrise/sunset for lat={lat} lon={lon}");

            using (UnityWebRequest req = UnityWebRequest.Get(url))
            {
                req.timeout = 10;
                yield return req.SendWebRequest();

                if (req.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(req.downloadHandler.text))
                {
                    ChillEnvPlugin.Log?.LogError($"[OpenMeteo SunSync] Request failed: {req.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    string json = req.downloadHandler.text;
                    // daily.sunrise: ["2026-06-07T06:12"], daily.sunset: ["2026-06-07T21:05"]
                    string sunriseRaw = ExtractStringValue(json, "\"sunrise\":[\"", "\"");
                    string sunsetRaw = ExtractStringValue(json, "\"sunset\":[\"", "\"");

                    if (string.IsNullOrEmpty(sunriseRaw) || string.IsNullOrEmpty(sunsetRaw))
                    {
                        ChillEnvPlugin.Log?.LogError("[OpenMeteo SunSync] Could not parse sunrise/sunset");
                        onComplete?.Invoke(null);
                        yield break;
                    }

                    // Format is "2026-06-07T06:12" — take only the HH:mm part
                    string sunriseTime = sunriseRaw.Length >= 16 ? sunriseRaw.Substring(11, 5) : sunriseRaw;
                    string sunsetTime = sunsetRaw.Length >= 16 ? sunsetRaw.Substring(11, 5) : sunsetRaw;

                    ChillEnvPlugin.Log?.LogInfo($"[OpenMeteo SunSync] sunrise={sunriseTime} sunset={sunsetTime}");
                    onComplete?.Invoke(new SunData { sunrise = sunriseTime, sunset = sunsetTime });
                }
                catch (Exception ex)
                {
                    ChillEnvPlugin.Log?.LogError($"[OpenMeteo SunSync] Parse error: {ex.Message}");
                    onComplete?.Invoke(null);
                }
            }
        }

        // ── WMO → internal code mapper ────────────────────────────────────
        // Open-Meteo WMO codes (subset of WMO 4677 used by the API):
        //   0        Clear sky
        //   1,2,3    Mainly clear / partly cloudy / overcast
        //   45,48    Fog / rime fog
        //   51,53,55 Drizzle light/moderate/dense
        //   56,57    Freezing drizzle light/dense
        //   61,63,65 Rain light/moderate/heavy
        //   66,67    Freezing rain light/heavy
        //   71,73,75 Snow fall light/moderate/heavy
        //   77       Snow grains
        //   80,81,82 Rain showers slight/moderate/violent
        //   85,86    Snow showers slight/heavy
        //   95       Thunderstorm slight/moderate
        //   96,99    Thunderstorm with slight/heavy hail
        private static int MapWmoCodeToInternalCode(int wmo)
        {
            if (wmo == 0) return 0;   // Clear
            if (wmo == 1 || wmo == 2) return 5;   // Partly cloudy
            if (wmo == 3) return 9;   // Overcast
            if (wmo == 45 || wmo == 48) return 30;  // Fog
            if (wmo == 51 || wmo == 56) return 13;  // Light drizzle / freezing drizzle light → LightRain
            if (wmo == 53 || wmo == 57) return 13;  // Moderate drizzle / freezing drizzle dense
            if (wmo == 55) return 13;  // Dense drizzle
            if (wmo == 61 || wmo == 66) return 13;  // Light rain / freezing rain light → LightRain
            if (wmo == 63) return 14;  // Moderate rain → HeavyRain
            if (wmo == 65 || wmo == 67) return 10;  // Heavy rain / freezing rain heavy
            if (wmo == 71 || wmo == 77) return 22;  // Light snow / snow grains
            if (wmo == 73) return 23;  // Moderate snow
            if (wmo == 75) return 24;  // Heavy snow
            if (wmo == 80) return 13;  // Rain showers slight → LightRain
            if (wmo == 81) return 14;  // Rain showers moderate → HeavyRain
            if (wmo == 82) return 10;  // Rain showers violent → HeavyRain (severe)
            if (wmo == 85) return 22;  // Snow showers slight
            if (wmo == 86) return 24;  // Snow showers heavy
            if (wmo == 95) return 11;  // Thunderstorm
            if (wmo == 96 || wmo == 99) return 12;  // Thunderstorm with hail (maps to ThunderRain)
            return 0; // Unknown → treat as clear
        }

        private static string WmoCodeToDescription(int wmo)
        {
            switch (wmo)
            {
                case 0: return "Clear Sky";
                case 1: return "Mainly Clear";
                case 2: return "Partly Cloudy";
                case 3: return "Overcast Clouds";
                case 45: return "Foggy";
                case 48: return "Rime Fog";
                case 51: return "Light Drizzle";
                case 53: return "Moderate Drizzle";
                case 55: return "Dense Drizzle";
                case 56: return "Light Freezing Drizzle";
                case 57: return "Heavy Freezing Drizzle";
                case 61: return "Light Rain";
                case 63: return "Moderate Rain";
                case 65: return "Heavy Rain";
                case 66: return "Light Freezing Rain";
                case 67: return "Heavy Freezing Rain";
                case 71: return "Light Snow";
                case 73: return "Moderate Snow";
                case 75: return "Heavy Snow";
                case 77: return "Snow Grains";
                case 80: return "Light Showers";
                case 81: return "Moderate Showers";
                case 82: return "Heavy Showers";
                case 85: return "Light Snow Showers";
                case 86: return "Heavy Snow Showers";
                case 95: return "Thunderstorm";
                case 96: return "Thunderstorm w/ Hail";
                case 99: return "Heavy Thunderstorm w/ Hail";
                default: return "Unknown";
            }
        }
        private static WeatherInfo ParseSeniverseJson(string json)
        {
            try
            {
                if (json.Contains("\"status\"") && !json.Contains("\"results\"")) return null;
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
                    Condition = MapSeniverseCodeToCondition(code),
                    UpdateTime = DateTime.Now
                };
            }
            catch { return null; }
        }

        private static WeatherInfo ParseOpenWeatherJson(string json)
        {
            try
            {
                if (string.IsNullOrEmpty(json))
                {
                    ChillEnvPlugin.Log?.LogError("[OpenWeather Parse] Empty JSON");
                    return null;
                }

                // Manual parsing due to JsonUtility limitations with nested structures
                int weatherId = ExtractIntValue(json, "\"id\":", ",");
                string description = ExtractStringValue(json, "\"description\":\"", "\"");
                
                // Find the "main" object and extract temp
                int mainIndex = json.IndexOf("\"main\":");
                if (mainIndex < 0)
                {
                    ChillEnvPlugin.Log?.LogError("[OpenWeather Parse] Cannot find 'main' object");
                    return null;
                }
                
                string tempStr = ExtractStringValue(json.Substring(mainIndex), "\"temp\":", ",");
                if (string.IsNullOrEmpty(tempStr))
                {
                    // Try without comma (might be last value)
                    tempStr = ExtractStringValue(json.Substring(mainIndex), "\"temp\":", "}");
                }
                
                float tempFloat = 0;
                if (!float.TryParse(tempStr, System.Globalization.NumberStyles.Float, 
                    System.Globalization.CultureInfo.InvariantCulture, out tempFloat))
                {
                    ChillEnvPlugin.Log?.LogError($"[OpenWeather Parse] Failed to parse temperature: '{tempStr}'");
                    return null;
                }

                int internalCode = MapOpenWeatherIdToInternalCode(weatherId);

                ChillEnvPlugin.Log?.LogDebug($"[OpenWeather Parse] weatherId={weatherId}, temp={tempFloat}, desc={description}, internalCode={internalCode}");

                return new WeatherInfo
                {
                    Code = internalCode,
                    Text = CapitalizeFirst(description ?? "Unknown"),
                    Temperature = (int)Math.Round(tempFloat),
                    Condition = MapSeniverseCodeToCondition(internalCode),
                    UpdateTime = DateTime.Now
                };
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"[OpenWeather Parse] Exception: {ex.Message}");
                ChillEnvPlugin.Log?.LogError($"[OpenWeather Parse] Stack: {ex.StackTrace}");
                return null;
            }
        }

        private static string CapitalizeFirst(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            if (str.Length == 1) return str.ToUpper();
            return char.ToUpper(str[0]) + str.Substring(1);
        }

        private static int MapOpenWeatherIdToInternalCode(int openWeatherId)
        {
            // Thunderstorm (2xx) -> 11 (Thunderstorm)
            if (openWeatherId >= 200 && openWeatherId < 300) return 11;
            
            // Drizzle (3xx) -> 13 (Light Rain)
            if (openWeatherId >= 300 && openWeatherId < 400) return 13;
            
            // Rain (5xx)
            if (openWeatherId >= 500 && openWeatherId < 600)
            {
                if (openWeatherId == 500 || openWeatherId == 501) return 13; // Light to moderate rain
                if (openWeatherId >= 502 && openWeatherId <= 504) return 10; // Heavy rain
                if (openWeatherId >= 520 && openWeatherId <= 531) return 14; // Shower rain
                return 10; // Default to heavy rain
            }
            
            // Snow (6xx) -> 22-25 (Snow)
            if (openWeatherId >= 600 && openWeatherId < 700)
            {
                if (openWeatherId == 600 || openWeatherId == 620) return 22; // Light snow
                if (openWeatherId == 601 || openWeatherId == 621) return 23; // Moderate snow
                if (openWeatherId == 602 || openWeatherId == 622) return 24; // Heavy snow
                if (openWeatherId >= 611 && openWeatherId <= 616) return 25; // Sleet
                return 22; // Default to light snow
            }
            
            // Atmosphere (7xx) -> 26-30 (Fog/Mist)
            if (openWeatherId >= 700 && openWeatherId < 800) return 26;
            
            // Clear (800) -> 0-3 (Clear/Sunny)
            if (openWeatherId == 800) return 0; // Clear sky
            
            // Clouds (80x) -> 4-9 (Cloudy)
            if (openWeatherId >= 801 && openWeatherId <= 804)
            {
                if (openWeatherId == 801) return 5; // Few clouds
                if (openWeatherId == 802) return 7; // Scattered clouds
                if (openWeatherId == 803) return 8; // Broken clouds
                if (openWeatherId == 804) return 9; // Overcast
                return 4; // Default to cloudy
            }
            
            return 99; // Unknown
        }

        public static IEnumerator FetchSunSchedule(string apiKey, string location, Action<SunData> onComplete)
        {
            string provider = ChillEnvPlugin.Cfg_WeatherProvider.Value;

            if (provider.Equals("OpenWeather", StringComparison.OrdinalIgnoreCase))
            {
                yield return FetchOpenWeatherSunSchedule(apiKey, location, onComplete);
            }
            else if (provider.Equals("OpenMeteo", StringComparison.OrdinalIgnoreCase))
            {
                yield return FetchOpenMeteoSunSchedule(location, onComplete);
            }
            else
            {
                yield return FetchSeniverseSunSchedule(apiKey, location, onComplete);
            }
        }

        private static IEnumerator FetchSeniverseSunSchedule(string apiKey, string location, Action<SunData> onComplete)
        {
            string finalKey = apiKey;
            if (string.IsNullOrEmpty(finalKey) && HasDefaultKey)
            {
                finalKey = KeySecurity.Decrypt(_encryptedDefaultKey);
            }

            if (string.IsNullOrEmpty(finalKey))
            {
                onComplete?.Invoke(null);
                yield break;
            }

            string url = $"https://api.seniverse.com/v3/geo/sun.json?key={finalKey}&location={UnityWebRequest.EscapeURL(location)}&language=zh-Hans&start=0&days=1";
            ChillEnvPlugin.Log?.LogInfo($"[API] Seniverse sun schedule request: {location}");

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 15;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(request.downloadHandler.text))
                {
                    ChillEnvPlugin.Log?.LogWarning($"[API] Sun schedule request failed: {request.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    var sunData = ParseSeniverseSunJson(request.downloadHandler.text);
                    onComplete?.Invoke(sunData);
                }
                catch (Exception ex)
                {
                    ChillEnvPlugin.Log?.LogError($"[API] Sun schedule parse failed: {ex}");
                    onComplete?.Invoke(null);
                }
            }
        }

        private static IEnumerator FetchOpenWeatherSunSchedule(string apiKey, string location, Action<SunData> onComplete)
        {
            string finalLocation = location.Trim();
            
            // If city name, resolve to coordinates first
            if (!finalLocation.Contains(","))
            {
                bool geocodingComplete = false;
                string resolvedCoords = null;

                yield return FetchCoordinatesFromCityName(apiKey, finalLocation, (coords) =>
                {
                    geocodingComplete = true;
                    resolvedCoords = coords;
                });

                while (!geocodingComplete)
                    yield return null;

                if (string.IsNullOrEmpty(resolvedCoords))
                {
                    ChillEnvPlugin.Log?.LogError($"[SunSync] Failed to resolve city: {finalLocation}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                finalLocation = resolvedCoords;
            }

            // Parse coordinates
            string[] parts = finalLocation.Replace(" ", "").Split(',');
            if (parts.Length != 2) 
            { 
                ChillEnvPlugin.Log?.LogError($"[SunSync] Invalid coordinates format: {finalLocation}");
                onComplete?.Invoke(null); 
                yield break; 
            }

            string lat = parts[0].Trim();
            string lon = parts[1].Trim();
            string url = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={apiKey}";

            ChillEnvPlugin.Log?.LogInfo($"[SunSync] OpenWeather request: lat={lat}, lon={lon}");

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 15;
                yield return request.SendWebRequest();

                if (request.result == UnityWebRequest.Result.Success)
                {
                    try
                    {
                        string json = request.downloadHandler.text;
                        ChillEnvPlugin.Log?.LogDebug($"[SunSync] Raw response: {json}");
                        
                        int sysIndex = json.IndexOf("\"sys\":");
                        if (sysIndex < 0)
                        {
                            ChillEnvPlugin.Log?.LogError("[SunSync] Cannot find 'sys' object in response");
                            onComplete?.Invoke(null);
                            yield break;
                        }
                        
                        string sunriseStr = ExtractNumericValue(json.Substring(sysIndex), "\"sunrise\":");
                        string sunsetStr = ExtractNumericValue(json.Substring(sysIndex), "\"sunset\":");
                        
                        ChillEnvPlugin.Log?.LogDebug($"[SunSync] Extracted sunrise: '{sunriseStr}', sunset: '{sunsetStr}'");
                        
                        if (string.IsNullOrEmpty(sunriseStr) || string.IsNullOrEmpty(sunsetStr))
                        {
                            ChillEnvPlugin.Log?.LogError($"[SunSync] Failed to extract sunrise/sunset times");
                            onComplete?.Invoke(null);
                            yield break;
                        }
                        
                        long sunriseUnix;
                        long sunsetUnix;
                        
                        if (!long.TryParse(sunriseStr, out sunriseUnix) || !long.TryParse(sunsetStr, out sunsetUnix))
                        {
                            ChillEnvPlugin.Log?.LogError($"[SunSync] Failed to parse Unix timestamps: sunrise='{sunriseStr}', sunset='{sunsetStr}'");
                            onComplete?.Invoke(null);
                            yield break;
                        }
                        
                        var sunData = new SunData
                        {
                            sunrise = DateTimeOffset.FromUnixTimeSeconds(sunriseUnix).ToLocalTime().ToString("HH:mm"),
                            sunset = DateTimeOffset.FromUnixTimeSeconds(sunsetUnix).ToLocalTime().ToString("HH:mm")
                        };
                        
                        ChillEnvPlugin.Log?.LogInfo($"[SunSync] Success: sunrise={sunData.sunrise}, sunset={sunData.sunset}");
                        onComplete?.Invoke(sunData);
                        yield break;
                    }
                    catch (Exception ex)
                    {
                        ChillEnvPlugin.Log?.LogError($"[SunSync] Parse error: {ex.Message}");
                        ChillEnvPlugin.Log?.LogError($"[SunSync] Stack trace: {ex.StackTrace}");
                    }
                }
                else
                {
                    ChillEnvPlugin.Log?.LogError($"[SunSync] Request failed: {request.error}");
                }
                
                onComplete?.Invoke(null);
            }
        }

        private static SunData ParseSeniverseSunJson(string json)
        {
            int sunIndex = json.IndexOf("\"sun\"");
            if (sunIndex < 0) return null;

            string sunrise = ExtractStringValue(json, "\"sunrise\":\"", "\"");
            string sunset = ExtractStringValue(json, "\"sunset\":\"", "\"");

            if (!string.IsNullOrEmpty(sunrise) && !string.IsNullOrEmpty(sunset))
            {
                return new SunData { sunrise = sunrise, sunset = sunset };
            }
            return null;
        }

        private static IEnumerator FetchCoordinatesFromCityName(string apiKey, string cityName, Action<string> onComplete)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                ChillEnvPlugin.Log?.LogWarning("[Geocoding] No API Key provided.");
                onComplete?.Invoke(null);
                yield break;
            }

            string url = $"https://api.openweathermap.org/geo/1.0/direct?q={UnityWebRequest.EscapeURL(cityName)}&limit=1&appid={apiKey}";

            ChillEnvPlugin.Log?.LogInfo($"[Geocoding] Request URL: {url}");

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(request.downloadHandler.text))
                {
                    ChillEnvPlugin.Log?.LogWarning($"[Geocoding] Request failed: {request.error}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    string json = request.downloadHandler.text;
                    ChillEnvPlugin.Log?.LogDebug($"[Geocoding] Raw response: {json}");
                    
                    // Check if response is empty array
                    if (json.Trim() == "[]")
                    {
                        ChillEnvPlugin.Log?.LogWarning($"[Geocoding] No results for '{cityName}'.");
                        onComplete?.Invoke(null);
                        yield break;
                    }
                    
                    // Manual parsing since it's an array
                    string latStr = ExtractStringValue(json, "\"lat\":", ",");
                    string lonStr = ExtractStringValue(json, "\"lon\":", ",");
                    
                    if (string.IsNullOrEmpty(latStr) || string.IsNullOrEmpty(lonStr))
                    {
                        ChillEnvPlugin.Log?.LogWarning($"[Geocoding] Failed to parse lat/lon from response");
                        onComplete?.Invoke(null);
                        yield break;
                    }
                    
                    string coordString = $"{latStr},{lonStr}";
                    ChillEnvPlugin.Log?.LogInfo($"[Geocoding] Resolved '{cityName}' to {coordString}");
                    onComplete?.Invoke(coordString);
                }
                catch (Exception ex)
                {
                    ChillEnvPlugin.Log?.LogError($"[Geocoding] Parse error: {ex.Message}");
                    onComplete?.Invoke(null);
                }
            }
        }

        private static int ExtractIntValue(string json, string prefix, string suffix)
        {
            int start = json.IndexOf(prefix); 
            if (start < 0) return 0; 
            start += prefix.Length;
            int end = json.IndexOf(suffix, start); 
            if (end < 0) return 0;
            string val = json.Substring(start, end - start).Trim(); 
            int.TryParse(val, out int res); 
            return res;
        }

        private static string ExtractStringValue(string json, string prefix, string suffix)
        {
            int start = json.IndexOf(prefix); 
            if (start < 0) return null; 
            start += prefix.Length;
            int end = json.IndexOf(suffix, start); 
            if (end < 0) return null;
            return json.Substring(start, end - start).Trim();
        }

        private static string ExtractNumericValue(string json, string prefix)
        {
            int start = json.IndexOf(prefix);
            if (start < 0) return null;
            start += prefix.Length;
            
            while (start < json.Length && (json[start] == ' ' || json[start] == '\t'))
                start++;
            
            int end = start;
            while (end < json.Length)
            {
                char c = json[end];
                if (c == ',' || c == '}' || c == ']' || c == ' ' || c == '\t' || c == '\r' || c == '\n')
                    break;
                end++;
            }
            
            if (end <= start) return null;
            return json.Substring(start, end - start).Trim();
        }

        public static WeatherCondition MapCodeToCondition(int code)
        {
            return MapSeniverseCodeToCondition(code);
        }

        private static WeatherCondition MapSeniverseCodeToCondition(int code)
        {
            if (code >= 0 && code <= 3) return WeatherCondition.Clear;
            if (code >= 4 && code <= 9) return WeatherCondition.Cloudy;
            if (code >= 10 && code <= 20) return WeatherCondition.Rainy;
            if (code >= 21 && code <= 25) return WeatherCondition.Snowy;
            if (code >= 26 && code <= 36) return WeatherCondition.Foggy;
            return WeatherCondition.Unknown;
        }
    }
}
