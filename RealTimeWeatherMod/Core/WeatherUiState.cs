using ChillWithYou.EnvSync.Models;

namespace ChillWithYou.EnvSync.Core
{
    internal static class WeatherUiState
    {
        internal static string NextWeatherText(
            bool showWeatherOnDate,
            string currentText,
            WeatherInfo latestWeather)
        {
            if (!showWeatherOnDate)
                return string.Empty;

            if (latestWeather == null)
                return string.IsNullOrEmpty(currentText) ? string.Empty : currentText;

            string unit = ChillEnvPlugin.Cfg_TemperatureUnit?.Value ?? "Celsius";
            int tempC = latestWeather.Temperature;

            string tempDisplay;
            if (unit.Equals("Fahrenheit", System.StringComparison.OrdinalIgnoreCase))
            {
                int tempF = (int)System.Math.Round(tempC * 9.0 / 5.0 + 32);
                tempDisplay = tempF + "°F";
            }
            else if (unit.Equals("Kelvin", System.StringComparison.OrdinalIgnoreCase))
            {
                int tempK = tempC + 273;
                tempDisplay = tempK + "K";
            }
            else
            {
                tempDisplay = tempC + "°C";
            }

            return latestWeather.Text + " " + tempDisplay;
        }
    }
}
