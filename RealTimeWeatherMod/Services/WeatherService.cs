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
        private static readonly TimeSpan CacheExpiry = TimeSpan.FromMinutes(60);
        public static WeatherInfo CachedWeather => _cachedWeather;
        private static readonly string _encryptedDefaultKey = "7Mr4YSR87bFvE4zDgj6NbuBKgz4EiPYEnRTQ0RIaeSU=";
        public static bool HasDefaultKey => !string.IsNullOrEmpty(_encryptedDefaultKey);

        public static IEnumerator FetchWeather(string apiKey, string location, bool force, Action<WeatherInfo> onComplete)
        {
            if (!force && _cachedWeather != null && DateTime.Now - _lastFetchTime < CacheExpiry)
            {
                onComplete?.Invoke(_cachedWeather);
                yield break;
            }

            string provider = ChillEnvPlugin.Cfg_WeatherProvider.Value;
            
            if (provider.Equals("OpenWeather", StringComparison.OrdinalIgnoreCase))
            {
                yield return FetchOpenWeather(apiKey, location, onComplete);
            }
            else
            {
                yield return FetchSeniverseWeather(apiKey, location, onComplete);
            }
        }

        private static IEnumerator FetchSeniverseWeather(string apiKey, string location, Action<WeatherInfo> onComplete)
        {
            string finalKey = apiKey;
            if (string.IsNullOrEmpty(finalKey) && HasDefaultKey)
            {
                finalKey = KeySecurity.Decrypt(_encryptedDefaultKey);
            }

            if (string.IsNullOrEmpty(finalKey))
            {
                ChillEnvPlugin.Log?.LogWarning("[API] No API Key configured and no built-in key");
                onComplete?.Invoke(null);
                yield break;
            }

            string url = $"https://api.seniverse.com/v3/weather/now.json?key={finalKey}&location={UnityWebRequest.EscapeURL(location)}&language=zh-Hans&unit=c";
            ChillEnvPlugin.Log?.LogInfo($"[API] Seniverse request: {location}");

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(request.downloadHandler.text))
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
                        ChillEnvPlugin.Log?.LogInfo($"[API] Data updated: {weather}");
                        onComplete?.Invoke(weather);
                    }
                    else
                    {
                        ChillEnvPlugin.Log?.LogWarning($"[API] Parse failed");
                        onComplete?.Invoke(null);
                    }
                }
                catch { onComplete?.Invoke(null); }
            }
        }

        private static IEnumerator FetchOpenWeather(string apiKey, string location, Action<WeatherInfo> onComplete)
        {
            if (string.IsNullOrEmpty(apiKey))
            {
                ChillEnvPlugin.Log?.LogWarning("[API] OpenWeather requires an API Key");
                onComplete?.Invoke(null);
                yield break;
            }

            string finalLocation = location.Trim();

            // Check if location is a city name (no comma) rather than coordinates
            if (!finalLocation.Contains(","))
            {
                bool geocodingComplete = false;
                string resolvedCoords = null;

                yield return FetchCoordinatesFromCityName(apiKey, finalLocation, (coords) =>
                {
                    geocodingComplete = true;
                    resolvedCoords = coords;
                });

                // Wait for the coroutine to finish
                while (!geocodingComplete)
                    yield return null;

                if (string.IsNullOrEmpty(resolvedCoords))
                {
                    ChillEnvPlugin.Log?.LogError($"[API] Failed to resolve city name to coordinates: {finalLocation}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                finalLocation = resolvedCoords;
            }

            // Parse coordinates (remove any whitespace)
            string[] parts = finalLocation.Replace(" ", "").Split(',');
            if (parts.Length != 2)
            {
                ChillEnvPlugin.Log?.LogWarning($"[API] Invalid location format: {finalLocation}");
                onComplete?.Invoke(null);
                yield break;
            }

            string lat = parts[0].Trim();
            string lon = parts[1].Trim();
            string url = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={apiKey}&units=metric";
            
            ChillEnvPlugin.Log?.LogInfo($"[API] OpenWeather request: lat={lat}, lon={lon}");

            using (UnityWebRequest request = UnityWebRequest.Get(url))
            {
                request.timeout = 10;
                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(request.downloadHandler.text))
                {
                    ChillEnvPlugin.Log?.LogError($"[API] OpenWeather request failed: {request.error}");
                    ChillEnvPlugin.Log?.LogError($"[API] Response: {request.downloadHandler?.text}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    ChillEnvPlugin.Log?.LogDebug($"[API] Raw response: {request.downloadHandler.text}");
                    var weather = ParseOpenWeatherJson(request.downloadHandler.text);
                    if (weather != null)
                    {
                        _cachedWeather = weather;
                        _lastFetchTime = DateTime.Now;
                        ChillEnvPlugin.Log?.LogInfo($"[API] OpenWeather data updated: {weather}");
                        onComplete?.Invoke(weather);
                    }
                    else
                    {
                        ChillEnvPlugin.Log?.LogWarning($"[API] OpenWeather parse returned null");
                        onComplete?.Invoke(null);
                    }
                }
                catch (Exception ex)
                {
                    ChillEnvPlugin.Log?.LogError($"[API] OpenWeather parse error: {ex.Message}");
                    ChillEnvPlugin.Log?.LogError($"[API] Stack trace: {ex.StackTrace}");
                    onComplete?.Invoke(null);
                }
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
