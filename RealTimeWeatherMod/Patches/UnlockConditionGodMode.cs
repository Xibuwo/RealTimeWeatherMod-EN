using HarmonyLib;
using System;
using System.Reflection;
using Bulbul;

namespace ChillWithYou.EnvSync.Patches
{
    public static class UnlockConditionGodMode
    {
        public static void ApplyPatches(Harmony harmony)
        {
            try
            {
                ChillEnvPlugin.Log?.LogInfo("🛡️ Deploying God Mode (Ultimate Edition)...");

                Type serviceType = AccessTools.TypeByName("Bulbul.UnlockConditionService");

                Type skinEnumType = null;
                Type unlockDecoType = AccessTools.TypeByName("UnlockDecoration");

                if (unlockDecoType != null)
                {
                    MethodInfo purchaseMethod = AccessTools.Method(unlockDecoType, "Purchase");
                    if (purchaseMethod != null)
                    {
                        var parameters = purchaseMethod.GetParameters();
                        if (parameters.Length > 0)
                        {
                            skinEnumType = parameters[0].ParameterType;
                            ChillEnvPlugin.Log?.LogInfo($"✅ Successfully captured Enum: {skinEnumType.Name}");
                        }
                    }
                }

                if (serviceType == null || skinEnumType == null)
                {
                    ChillEnvPlugin.Log?.LogError("❌ Type resolution failed, patch cancelled.");
                    return;
                }

                MethodInfo isUnlockedOrigin = AccessTools.Method(serviceType, "IsUnlocked")?.MakeGenericMethod(skinEnumType);
                MethodInfo isUnlockedPrefix = typeof(UnlockConditionGodMode).GetMethod(nameof(IsUnlockedPrefix));

                if (isUnlockedOrigin != null)
                {
                    harmony.Patch(isUnlockedOrigin, prefix: new HarmonyMethod(isUnlockedPrefix));
                    ChillEnvPlugin.Log?.LogInfo("✅ IsUnlocked interception successful");
                }

                MethodInfo isPurchasableOrigin = AccessTools.Method(serviceType, "IsPurchasableItem")?.MakeGenericMethod(skinEnumType);
                MethodInfo isPurchasablePrefix = typeof(UnlockConditionGodMode).GetMethod(nameof(IsPurchasablePrefix));

                if (isPurchasableOrigin != null)
                {
                    harmony.Patch(isPurchasableOrigin, prefix: new HarmonyMethod(isPurchasablePrefix));
                    ChillEnvPlugin.Log?.LogInfo("✅ IsPurchasableItem interception successful");
                }
            }
            catch (Exception ex)
            {
                ChillEnvPlugin.Log?.LogError($"❌ God Mode deployment failed: {ex}");
            }
        }

        public static bool IsUnlockedPrefix(ref ValueTuple<bool, bool> __result)
        {
            if (!ChillEnvPlugin.Cfg_UnlockDecorations.Value) return true;
            __result = new ValueTuple<bool, bool>(true, true);
            return false;
        }
        public static bool IsPurchasablePrefix(ref int price, ref bool __result)
        {
            if (!ChillEnvPlugin.Cfg_UnlockPurchasableItems.Value) return true;

            price = 0;
            __result = false;

            return false; 
        }
    }
}
