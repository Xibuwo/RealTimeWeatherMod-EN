using System;
using System.Collections;
using UnityEngine.Networking;
using ChillWithYou.EnvSync.Models;
using Bulbul;

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

    string finalLocation = location;

    // Check if location is a city name (no comma) rather than coordinates
    if (!location.Contains(","))
    {
        bool geocodingComplete = false;
        string resolvedCoords = null;

        yield return FetchCoordinatesFromCityName(apiKey, location, (coords) =>
        {
            geocodingComplete = true;
            resolvedCoords = coords;
        });

        // Wait for the coroutine to finish
        while (!geocodingComplete)
            yield return null;

        if (string.IsNullOrEmpty(resolvedCoords))
        {
            onComplete?.Invoke(null);
            yield break;
        }

        finalLocation = resolvedCoords;
    }

    string[] parts = finalLocation.Split(',');
    if (parts.Length != 2)
    {
        ChillEnvPlugin.Log?.LogWarning($"[API] Invalid location format: {finalLocation}");
        onComplete?.Invoke(null);
        yield break;
    }

    string lat = parts[0].Trim();
    string lon = parts[1].Trim();
    string url = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={apiKey}&units=metric";
}
{
    if (string.IsNullOrEmpty(apiKey))
    {
        // Optional: Add fallback key logic here for consistency with Seniverse
        ChillEnvPlugin.Log?.LogWarning("[API] OpenWeather requires an API Key");
        onComplete?.Invoke(null);
        yield break;
    }

    string[] parts = location.Split(',');
    if (parts.Length != 2) { /* ... error ... */ }

    string lat = parts[0].Trim();
    string lon = parts[1].Trim();

    // CHANGE: Use One Call API 3.0 and request only current weather
    string url = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={apiKey}";
    ChillEnvPlugin.Log?.LogInfo($"[API] OpenWeather request: {location}");

    using (UnityWebRequest request = UnityWebRequest.Get(url))
    {
        request.timeout = 10;
        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success || string.IsNullOrEmpty(request.downloadHandler.text))
        {
            ChillEnvPlugin.Log?.LogWarning($"[API] OpenWeather request failed: {request.error}");
            onComplete?.Invoke(null);
            yield break;
        }

        try
        {
            // CHANGE: Use the new parser
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
                ChillEnvPlugin.Log?.LogWarning($"[API] OpenWeather parse failed");
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
        private static string CapitalizeFirst(string str)
        {
            if (string.IsNullOrEmpty(str)) return str;
            if (str.Length == 1) return str.ToUpper();
            return char.ToUpper(str[0]) + str.Substring(1);
        }

        /// <summary>
        /// Maps OpenWeather weather condition ID to internal Seniverse-like code
        /// https://openweathermap.org/weather-conditions
        /// </summary>
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
        // CHANGE: Call the correct method for OpenWeather.
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
    // Re-use the main weather API call logic to get data.
    // We'll simply parse the cached weather response if available and recent.
    if (_cachedWeather != null && (DateTime.Now - _lastFetchTime).TotalMinutes < 5)
    {
        // Use a helper to extract sun data from the last API response.
        // This requires storing the raw JSON or having a shared data object.
        // For now, we'll make a new call if needed.
    }

    // Fallback: if no recent cache, make a new call.
    // The following is a simplified direct call.
    string[] parts = location.Split(',');
    if (parts.Length != 2) { onComplete?.Invoke(null); yield break; }

    string lat = parts[0].Trim();
    string lon = parts[1].Trim();
    string url = $"https://api.openweathermap.org/data/2.5/weather?lat={lat}&lon={lon}&appid={apiKey}";

    using (UnityWebRequest request = UnityWebRequest.Get(url))
    {
        request.timeout = 15;
        yield return request.SendWebRequest();

        if (request.result == UnityWebRequest.Result.Success)
        {
            var response = JsonUtility.FromJson<OpenWeatherCurrentResponse>(request.downloadHandler.text);
            if (response?.current != null)
            {
                var sunData = new SunData
                {
                    sunrise = DateTimeOffset.FromUnixTimeSeconds(response.current.sunrise).ToLocalTime().ToString("HH:mm"),
                    sunset = DateTimeOffset.FromUnixTimeSeconds(response.current.sunset).ToLocalTime().ToString("HH:mm")
                };
                onComplete?.Invoke(sunData);
                yield break;
            }
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

        private static SunData ParseOpenWeatherSunJson(string json)
        {
            // OpenWeather returns sunrise/sunset as Unix timestamps in "sys" object
            // {"sys":{"sunrise":1640151234,"sunset":1640189456}}
            
            string sunriseTimestamp = ExtractStringValue(json, "\"sunrise\":", ",");
            string sunsetTimestamp = ExtractStringValue(json, "\"sunset\":", "}");
            
            if (string.IsNullOrEmpty(sunriseTimestamp) || string.IsNullOrEmpty(sunsetTimestamp))
                return null;

            try
            {
                long sunriseUnix = long.Parse(sunriseTimestamp);
                long sunsetUnix = long.Parse(sunsetTimestamp);
                
                DateTime sunriseTime = DateTimeOffset.FromUnixTimeSeconds(sunriseUnix).ToLocalTime().DateTime;
                DateTime sunsetTime = DateTimeOffset.FromUnixTimeSeconds(sunsetUnix).ToLocalTime().DateTime;
                
                return new SunData
                {
                    sunrise = sunriseTime.ToString("HH:mm"),
                    sunset = sunsetTime.ToString("HH:mm")
                };
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"[OpenWeather Sun Parse] Error: {ex.Message}");
                return null;
            }
        }
        private static WeatherInfo ParseOpenWeatherJson(string json)
{
    try
    {
        // Deserialize the response from /data/2.5/weather
        var response = JsonUtility.FromJson<OpenWeatherCurrentResponse>(json);
        if (response?.weather == null || response.weather.Length == 0) return null;

        var primaryWeather = response.weather[0];
        int internalCode = MapOpenWeatherIdToInternalCode(primaryWeather.id);
        string description = primaryWeather.description ?? "Unknown";

        return new WeatherInfo
        {
            Code = internalCode,
            Text = CapitalizeFirst(description),
            Temperature = (int)Math.Round(response.main.temp),
            Condition = MapSeniverseCodeToCondition(internalCode),
            UpdateTime = DateTime.Now
        };
    }
    catch (Exception ex)
    {
        ChillEnvPlugin.Log?.LogError($"[OpenWeather Parse] Error: {ex.Message}");
        return null;
    }
}
        private static int ExtractIntValue(string json, string prefix, string suffix)
        {
            int start = json.IndexOf(prefix); 
            if (start < 0) return 0; 
            start += prefix.Length;
            int end = json.IndexOf(suffix, start); 
            if (end < 0) return 0;
            string val = json.Substring(start, end - start); 
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
            return json.Substring(start, end - start);
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
private static IEnumerator FetchCoordinatesFromCityName(string apiKey, string cityName, Action<string> onComplete)
{
    if (string.IsNullOrEmpty(apiKey))
    {
        ChillEnvPlugin.Log?.LogWarning("[Geocoding] No API Key provided.");
        onComplete?.Invoke(null);
        yield break;
    }

    string url = $"https://api.openweathermap.org/geo/1.0/direct?q={UnityWebRequest.EscapeURL(cityName)}&limit=1&appid={apiKey}";

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
            // Manually wrap the JSON array for JsonUtility
            string wrappedJson = $"{{\"array\":{request.downloadHandler.text}}}";
            var response = JsonUtility.FromJson<OpenWeatherGeocodingResponseList>(wrappedJson);

            if (response.array != null && response.array.Length > 0)
            {
                var location = response.array[0];
                string coordString = $"{location.lat},{location.lon}";
                ChillEnvPlugin.Log?.LogInfo($"[Geocoding] Resolved '{cityName}' to {coordString}");
                onComplete?.Invoke(coordString);
            }
            else
            {
                ChillEnvPlugin.Log?.LogWarning($"[Geocoding] No results for '{cityName}'.");
                onComplete?.Invoke(null);
            }
        }
        catch (Exception ex)
        {
            ChillEnvPlugin.Log?.LogError($"[Geocoding] Parse error: {ex.Message}");
            onComplete?.Invoke(null);
        }
    }
}
[System.Serializable]
public class OpenWeatherCurrentResponse
{
    public WeatherDesc[] weather;
    public MainData main;
    public SysData sys; // For sunrise/sunset if needed elsewhere
}

[System.Serializable]
public class MainData
{
    public float temp;
    // ... other fields like humidity, pressure if needed
}

[System.Serializable]
public class SysData
{
    public long sunrise;
    public long sunset;
}

[System.Serializable]
public class CurrentData
{
    public long dt;
    public long sunrise;
    public long sunset;
    public float temp;
    public WeatherDesc[] weather; // Renamed to avoid conflict
}

[System.Serializable]
public class WeatherDesc // Renamed from WeatherDescription
{
    public int id;
    public string main;
    public string description;
}
[System.Serializable]
public class OpenWeatherGeocodingResponse
{
    public string name;
    public float lat;
    public float lon;
    public string country;
}

[System.Serializable]
public class OpenWeatherGeocodingResponseList
{
    public OpenWeatherGeocodingResponse[] array;
    // JsonUtility needs a wrapper for arrays
}
