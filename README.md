# Chill Env Sync (Real-Time Weather Sync Mod)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![.NET Framework 4.7.2](https://img.shields.io/badge/.NET%20Framework-4.7.2-blue.svg)](https://dotnet.microsoft.com/download/dotnet-framework/net472)
[![BepInEx](https://img.shields.io/badge/BepInEx-Plugin-green.svg)](https://github.com/BepInEx/BepInEx)

A BepInEx plugin for *Chill with You – A Lo-Fi Story with You*. It synchronizes the in-game environment with real-world weather, or simulates day-night cycles based on your local time.

---

[![Chill with You](./Images/CWYLS-EN.jpg)](https://store.steampowered.com/app/3548580/)

> Chill with You Lo-Fi Story is a voiced novel/work-with-me game featuring Satone. You can customize the music, ambience, and scenery to build a focused working environment. As your relationship deepens, something special may or may not happen.

---

**Note:** All code was AI-generated. Human work was limited to decompiling and fixing bugs. This fork is oriented toward using the OpenWeather API for international users.

---

Related mod：[iGPUSaviorMod](https://github.com/Small-tailqwq/iGPUSaviorMod)

# 😵‍💫 Fork made by Xibuwo 

## ✨ Main Features

- 🌤️ **Real-time weather sync** via Seniverse or OpenWeather APIs
- 💥 **Temperature Unit** for both imperial and metric
- 🌍 **Multi-city and multi-provider support**
- 🌓 **Automatic day/night cycle** based on actual sunrise and sunset times
- 🔓 **Optional unlock-all** for environments and decorations
  - Session-only, does not modify save files
  - `UnlockAllEnvironments`: Unlocks all environments
  - `UnlockAllDecorations`: Unlocks all decorations
  - `UnlockPurchasableItems`: Unlocks all the purchasable items
- ⌨️ **Hotkeys**:
  - `F7` – Force refresh weather (bypasses cache)
  - `F8` – Log current environment status
  - `F9` – Force re-apply environment using cached data
 
## ✨ Preview
### 📦 With iGPUSavior

![iGPUSavior](https://github.com/user-attachments/assets/079f3aec-2713-4830-8c46-fa592dfd07e0)
![iGPUSavior](https://github.com/user-attachments/assets/9a57af37-5a9a-4e7e-9dd9-ad42334aa840)

### 🧷 Without iGPUSavior

![RTWM](https://github.com/user-attachments/assets/892e1e87-e6d7-4210-85f9-cd2d2d9f10e5)
![RTWM](https://github.com/user-attachments/assets/c700edba-60d5-47b2-a8ff-941a7072b886)

## ⚠️ Notes

- 💽 **Potential "cheat-like" behavior**: Unlocking environments is temporary but still modifies runtime state.
- 💥 **Potential conflicts** with future game updates or other mods.
- 🧷 **External API dependency** relies on third-party weather providers.
- 😵‍💫 **AI-written code**: Expect errors. This version is currently being tested with OpenWeather's API.

## 🎮 Supported Environment Types

### Base Environments (mutually exclusive)
- ☀️ Day
- 🌅 Sunset
- 🌙 Night
- ☁️ Cloudy

### Precipitation Effects
- 🌧️ LightRain
- 🌧️ HeavyRain
- ⛈️ ThunderRain
- ❄️ Snow

### Scenery Effects (Easter Eggs)
Triggered automatically if seasonal/automation is enabled. Includes seasonal and rare event scenery.

### Sound Effects
Also auto-triggered when conditions are met (e.g., cooking sounds, AC noise, cicadas).

### TO-DO
> 🗒️ Improve and stabilize the mod for OpenWeather API usage.
> 
> 🌧️ Optimise the code
> 
> ⚠️ Fix critical crashing bug

## 📦 Installation

### Requirements
- The game
- [BepInEx 5.4.23.4](https://github.com/BepInEx/BepInEx/releases)

### Steps
1. Install BepInEx into the game's root folder (where the .exe is located).
2. Launch the game once, then exit. This generates the BepInEx folder structure.
3. Place the `RealTimeWeatherMod.dll` file into `BepInEx/plugins/`.
4. Launch the game.
5. Edit the generated config file (see below). Press `F7` in-game to reload the config.

## ⚙️ Configuration

After the first run, a configuration file is generated here: `BepInEx/config/chillwithyou.envsync.cfg`

**Optional**: If you have [iGPU Savior](https://github.com/Small-tailqwq/iGPUSaviorMod) installed, 
some settings will also appear in the in-game MOD Settings tab for convenience.

### Weather API Providers
This fork supports both Seniverse and OpenWeather, but mainly made for OpenWeather. Select one using the `WeatherProvider` field.

| Provider | Location Format | Notes |
| :--- | :--- | :--- |
| **Seniverse** | City name (e.g., `beijing`, `上海`) | Made for Chinese users, lower free tier quota. |
| **OpenWeather** | City name **or** `latitude,longitude` (e.g., `40.7128,-74.0060`) | Global; 60 calls per minute free tier. |

**Example Config Snippets**

*Using Seniverse:*
```ini
[WeatherAPI]
EnableWeatherSync = true
WeatherProvider = Seniverse
ApiKey = YOUR_SENIVERSE_KEY_HERE
# (There's a default Seniverse key already so don't worry.)
Location = beijing
```

*Using OpenWeather:*
```ini
[WeatherAPI]
EnableWeatherSync = true
WeatherProvider = OpenWeather
ApiKey = YOUR_OPENWEATHER_KEY_HERE
Location = 40.7128,-74.0060
# or use a city name:
# Location = New York City
```

*General Settings:*
```ini
[WeatherSync]
RefreshMinutes = 15

[Temperature]
TemperatureUnit = Celsius

[TimeConfig]
Sunrise = 06:30
Sunset = 18:30

[UI]
ShowWeatherOnDate = true

[Automation]
EnableSeasonalEasterEggs = true

[Unlock]
UnlockAllEnvironments = true
UnlockAllDecorations = true
UnlockPurchasableItems = false

[Debug]
EnableDebugMode = false
SimulatedCode = 13
SimulatedTemp = 13
SimulatedText = DebugWeather
```

### Obtaining an API Key
- **Seniverse:**
  1. Visit the [Seniverse Developer Platform](https://www.seniverse.com/).
  2. Register, log in, and go to `Console` -> `Product Management` -> `Free Edition` (or your subscription tier).
  3. Find the **Private Key** for the weather API product and copy it.
  4. Paste the key into the `ApiKey` field in the config file (no quotes needed).
  5. Set `WeatherProvider = Seniverse`.

> 💡 The newer version has a built-in key. You can leave the `ApiKey` field empty to use the default service, or use your own.
> 💡 The default key is only for Seniverse, if you require a global (OpenWeather) key, you'll have to get it yourself, but no worries, it's totally free.

- **OpenWeather:**
  1. Visit [OpenWeather](https://openweathermap.org/) and sign up for a free account.
  2. Go to your [API keys](https://home.openweathermap.org/api_keys) section.
  3. Generate a new key or use the default one provided.
  4. Paste this key into both the `ApiKey` field and the `GeneralAPI` field in the config file.
  5. Set `WeatherProvider = OpenWeather`.

## 🚀 Usage

### Basic Usage (Enabled by Default)
The plugin automatically switches base environments based on your configured (or fetched) sunrise and sunset times:
- **Day**: From 1 hour after sunrise until 30 minutes before sunset.
- **Sunset/Twilight**: From 30 minutes before sunset until 30 minutes after sunset.
- **Night**: From 30 minutes after sunset until 1 hour after sunrise.

### Weather Sync Mode (Requires Manual Enable)
1. Configure your API Key and Location as described above.
2. Set `EnableWeatherSync = true`.
3. The plugin will fetch weather data at the interval set by `RefreshMinutes` (default: 15).

4. **Weather Severity Logic**:
   - **Normal Weather**: Day, Sunset, and Night switch as per the basic time cycle.
   - **Severe Weather** (e.g., heavy rain, thunderstorms, blizzards): During daytime and sunset periods, the environment is forced to **Cloudy**. The sunset effect is skipped. **Nighttime is never overridden by weather**.

5. **Precipitation Effect Logic** (based on internal weather code mapping):

| Effect | Internal Weather Codes (Seniverse) | Condition |
| :--- | :--- | :--- |
| ❄️ Snow | 20–25 | All snow types. |
| ⛈️ ThunderRain | 11, 12, 16–18 | Thunderstorms, heavy rainstorms. |
| 🌧️ HeavyRain | 10, 14, 15 | Showers, moderate to heavy rain. |
| 🌧️ LightRain | 13, 19 | Drizzle, light rain, freezing rain. |
| **Off** | 0–9, 26–39 | Clear, cloudy, foggy, windy (all precipitation effects are turned off). |

6. **OpenWeather ID Mapping**:

| OpenWeather Condition ID | Mapped Internal Code | Result |
| :--- | :--- | :--- |
| 800 | 0 | Clear |
| 801–804 | 4–9 | Cloudy |
| 200–232 | 11 | ThunderRain |
| 300–399, 500–501 | 13 | LightRain |
| 502–504, 520–531 | 10, 14 | HeavyRain |
| 600–622 | 22–25 | Snow |
| 701–781 | 26 | Foggy |

### Hotkey Functions

- **F7 – Force Refresh Weather**
  - **Purpose**: Immediately fetches the latest weather data from the API, ignoring the cache and resetting the refresh timer.
  - **Use when**: The weather has changed outside but the game hasn't updated yet, or after changing the `Location` in the config.

- **F8 – Show Status**
  - **Purpose**: Prints the current plugin state, active environment, and UI weather string to the BepInEx log.

- **F9 – Force Environment Re-application**
  - **Purpose**: Uses the currently cached weather data and local time to forcefully re-calculate and apply the game environment, bypassing any "already active" checks.
  - **Use when**: The game's visual/audio state seems out of sync with the UI, or to test time-based transitions without waiting.

## 🔧 Technical Details

- **Framework**: BepInEx 5.x
- **Target Framework**: .NET Framework 4.7.2
- **Techniques Used**:
  - Harmony patches (for game function injection)
  - Unity coroutines (for asynchronous network requests)
  - Reflection (to access internal game systems)

## 📝 Version History

### v5.4.2
- Added "Unlock Purchasable Items" system (unlocks items requiring currency)
- Added configuration toggle for Purchasable Items (requires restart, as always)
- Improved weather synchronization and refresh reliability
- Fixed class naming typos and controller registration issues
- Enhanced unlock verification and debug monitoring

### v5.4.1
- Added default key
- Minor changes

### v5.4.0
- Complete UI overhaul with in-game configuration support
- Added input fields for Location and API Key (no more manual config editing!)
- Added dropdown menus for Weather Provider and Temperature Unit selection
- Fixed dropdown menus appearing behind other UI elements
- Minor bug fixes and performance optimizations

### v5.3.0
- Staying up to date with the original repository
- Added support for iGPUSavior
- Minor changes

### v5.2.1
- Fixed dynamic sunset and sunrise.
- Added imperial system for all the americans out there... and people from Myanmar.

### v5.2.0
- Added support for international users through OpenWeather's API.
- Translated key parts to English

### v5.1.2
- Added a built-in API key as a fallback.
- Fixed UI control interaction issues.
- Fixed background music not stopping correctly after easter egg activation.
- Fixed weather switches being incorrectly counted in user interaction tracking.

### v5.1.1
- Added detailed time segment display. Replaces simple "AM/PM" with: Midnight, Early Morning, Morning, Noon, Afternoon, Evening, Night.

### v5.0.1
- Internal refactoring. Added missing scenery type enumerations.

### v5.0.0
- Major refactor: Introduced the `SceneryAutomationSystem` as a separate `MonoBehaviour` component to handle seasonal easter eggs and automatic scenery/sound triggers without interfering with core weather sync logic.
- Rule-based system: Scenery rules (e.g., fireworks for Lunar New Year, cooking sounds at meal times) are now defined as configurable `SceneryRule` objects.
- User interaction tracking: If a user manually toggles a scenery element, the system will stop auto-managing it for the rest of the session.

### v4.5.0
- Added display of weather and temperature next to the date in the top-left UI.
- Added config option `ShowWeatherOnDate` to control this feature.
- Requires `Unity.TextMeshPro.dll` reference.

### v4.4.1
- Refined "bad weather" logic. Light rain, snow showers, and intermittent precipitation are no longer considered severe weather that forces a cloudy daytime.

### v4.4.0
- Reworked environment derivation logic. Nighttime is now always preserved as night, regardless of weather conditions.

### v4.3.1 / v4.3.0
- Fixed an issue where pressing `F9` while the Cloudy environment was active could incorrectly close all other environments.

### v4.2.x Series
- Various fixes and adjustments to hotkey logic and logging.
- Added a debug mode for testing weather code mappings.

### v3.7.0 – v4.1.0
- Optimized `F9` key logic and log messages.
- Fixed occasional log deadlocks.
- Corrected sunset transition timing from 1 hour to the intended 30 minutes.
- Implemented caching logic:
  - **Fast Clock (30 sec)**: Uses cached weather data to check for time-based transitions. No network call.
  - **Slow Clock (User-defined, default 30 min)**: Forces a new API call to refresh weather data, regardless of cache state.

*(For detailed history, check the Git commit log.)*

## 🐛 Known Issues

- First load may take ~15 seconds before the initial environment sync occurs.
- Some weather effects may incorrectly trigger the user interaction log message `[User Interaction] User took over [HeavyRain], stopping auto-management.` This does not affect the actual switching logic.
- Game crashing when tabbing in and out of the game window

## 🤝 Contributing

Issues and Pull Requests are welcome!

**When reporting a bug**, please ensure it is reproducible and enable debug logging (set `Logging.Console = true` in `BepInEx/config/BepInEx.cfg`). This provides detailed console output to help diagnose the issue.

## 📄 License

MIT License. See the [LICENSE](LICENSE) file for details.

**Disclaimer**: This plugin is for learning and personal use. The author is not responsible for any issues arising from its use.

## 👨‍💻 Author

- GitHub: [@Small-tailqwq](https://github.com/Small-tailqwq)

## 👨‍💻 Contributor

- Github: [@xibuwo](https://github.com/xibuwo)

## 🙏 Acknowledgments

- The original creator of this (Ko_teiru) if it weren't for him or her, I couldn't have done this with OpenWeather
- The BepInEx team
- Harmony library
- Seniverse & OpenWeather API services
- AI Assistants: Google Gemini, OpenAI GPT, Claude and overall, Deepseek.
- My attention span (Currently I'm watching an IG Reel of Clash Royale with SpongeBob and Patrick threatening someone)

---

> My keyboard is old asf low-key
```
