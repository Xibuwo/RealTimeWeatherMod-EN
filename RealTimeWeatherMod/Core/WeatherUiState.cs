using ChillWithYou.EnvSync.Models;

namespace ChillWithYou.EnvSync.Core
{
    internal static class WeatherUiState
    {
        /// <summary>
        /// Computes the next weather text for the date bar.
        /// - Returns empty if ShowWeatherOnDate is off.
        /// - Returns previous text (not empty) on fetch failure, so the bar doesn't flash blank.
        /// - Returns formatted text when a fresh WeatherInfo arrives.
        /// </summary>
        internal static string NextWeatherText(
            bool showWeatherOnDate,
            string currentText,
            WeatherInfo latestWeather)
        {
            if (!showWeatherOnDate)
                return string.Empty;

            if (latestWeather == null)
            {
                // Keep showing the last known value; don't blank out on transient failure
                return string.IsNullOrEmpty(currentText) ? string.Empty : currentText;
            }

            return latestWeather.Text + " " + latestWeather.Temperature + "°C";
        }
    }
}
