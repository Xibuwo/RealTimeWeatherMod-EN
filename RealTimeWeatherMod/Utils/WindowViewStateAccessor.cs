using System;
using System.Collections.Generic;
using System.Reflection;
using Bulbul;

namespace ChillWithYou.EnvSync.Utils
{
    internal static class WindowViewStateAccessor
    {
        private static bool _loggedFallbackOnce;

        internal static bool TryIsWindowViewActive(WindowViewType viewType, out bool isActive)
        {
            isActive = false;
            if (!TryGetWindowViewDic(out var dict)) return false;
            if (!dict.TryGetValue(viewType, out var data) || data == null) return false;

            isActive = data.IsActive;
            return true;
        }

        private static bool TryGetWindowViewDic(out Dictionary<WindowViewType, WindowViewData> dict)
        {
            dict = null;

            try
            {
                var saveData = SaveDataManager.Instance;
                if (saveData == null) return false;
                var envData = saveData.EnviromentData;
                if (envData != null && envData.WindowViewDic != null)
                {
                    dict = envData.WindowViewDic;
                    return true;
                }
                var oldProp = saveData.GetType().GetProperty("WindowViewDic", BindingFlags.Instance | BindingFlags.Public);
                if (oldProp != null)
                {
                    dict = oldProp.GetValue(saveData) as Dictionary<WindowViewType, WindowViewData>;
                    if (dict != null)
                    {
                        if (!_loggedFallbackOnce)
                        {
                            _loggedFallbackOnce = true;
                            ChillEnvPlugin.Log?.LogWarning("[Backward Compatibility] Use the legacy SaveDataManager.WindowViewDic to read the window view state.");
                        }
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogDebug($"[Compatibility] Failed to read window state: {ex.Message}");
            }

            return false;
        }
    }
}
