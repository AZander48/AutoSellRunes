using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HadeanTactics;
using UnityEngine;

namespace AutoSellRunes;

[BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
public class AutoSellRunesPlugin : BaseUnityPlugin
{
    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<bool> Enabled = null!;
    internal static ConfigEntry<bool> DebugLogging = null!;

    private Harmony? _harmony;

    private void Awake()
    {
        Log = Logger;

        InitializeConfig();
        _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginInfo.PLUGIN_GUID);


        Log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} loaded (enabled={Enabled.Value}.");
    }

    internal static void SellLatestRune(RelicManager manager, string source = "SellLatestRune")
    {
        List<RelicBehavior>? runes = manager.currentRunes;
        if (runes == null || runes.Count == 0)
        {
            if (DebugLogging.Value) Log.LogInfo($"{source}: no runes in currentRunes.");
            return;
        }

        RelicBehavior rune = runes[runes.Count - 1];
        SellRune(rune, source);
    }

    internal static void SellRune(RelicBehavior rune, string source = "SellRune")
    {
        if (DebugLogging.Value) Log.LogInfo($"{source}: OnSell -> {rune.name}");

        try
        {
            rune.OnSell();
        }
        catch (Exception ex)
        {
            Log.LogError($"{source}: OnSell failed: {ex}");
        }
    }

    private void SellAllRunes()
    {
        RelicManager relicManager = FindObjectOfType<RelicManager>();

        if (relicManager._manager != null && relicManager._manager.state == GameState.Loading)
        {
            if (DebugLogging.Value) Log.LogInfo("SellAllRunes: skip sell (Loading).");
            return;
        }

        if (relicManager != null) 
        {
            while (relicManager.currentRunes?.Count > 0)
            {
                SellLatestRune(relicManager, "SellAllRunes");
            }
        }
    }

    private void InitializeConfig()
    {
        Enabled = Config.Bind("General", "Enabled", true, new ConfigDescription("Auto-sell runes when CreateRune adds one.", null, new ConfigurationManagerAttributes { Order = 1 }));
        DebugLogging = Config.Bind("Debug", "Debug", true, new ConfigDescription("Verbose logging.", null, new ConfigurationManagerAttributes { Order = 2 }));
        Config.Bind(
            "General",
            "Sell All Runes",
            0,
            new ConfigDescription(
                "Sell all runes currently held.",
                null,
                new ConfigurationManagerAttributes
                {
                    CustomDrawer = _ =>
                    {
                        if (GUILayout.Button("Sell", GUILayout.ExpandWidth(true)))
                            SellAllRunes();
                    },
                    HideDefaultButton = true, // hides Reset
                }
            )
        );
    }
}

[HarmonyPatch]
internal static class RelicManager_InstantiateContainer_Patch
{
    private static MethodBase TargetMethod()
    {
        Type type = AccessTools.TypeByName("HadeanTactics.RelicManager") ?? throw new Exception("Could not find RelicManager");
        MethodInfo? method = AccessTools.Method(
            type,
            "InstantiateContainer",
            [
                typeof(EffectContainer),
                typeof(Transform),
                typeof(List<RelicBehavior>),
                typeof(bool),
                typeof(bool),
                typeof(TeamType),
            ]
        ) ?? throw new Exception("Could not find RelicManager.InstantiateContainer");
        return method;
    }

    private static void Postfix(RelicManager __instance, List<RelicBehavior> listToAdd, RelicBehavior __result)
    {
        if (!AutoSellRunesPlugin.Enabled.Value) return;

        if (__result == null) {
            if (AutoSellRunesPlugin.DebugLogging.Value) AutoSellRunesPlugin.Log.LogInfo($" __result == null: {__result}");
            return;
        }

        if (!ReferenceEquals(listToAdd, __instance.currentRunes))
        {
            if (AutoSellRunesPlugin.DebugLogging.Value) AutoSellRunesPlugin.Log.LogInfo($"listToAdd not currentRunes.");
            return;
        }
        

        if (__result.relic != null && __result.relic.containerType != EffectContainerType.rune) {
            if (AutoSellRunesPlugin.DebugLogging.Value) AutoSellRunesPlugin.Log.LogInfo($"Rune not selected: {__result.relic}");
            return;
        }

        if (listToAdd == null) {
            if (AutoSellRunesPlugin.DebugLogging.Value) AutoSellRunesPlugin.Log.LogInfo($" listToAdd == null: {listToAdd}");
            return;
        }

        if (__instance._manager == null) {
            if (AutoSellRunesPlugin.DebugLogging.Value) AutoSellRunesPlugin.Log.LogInfo($"__instance._manager == null: {__instance._manager}");
            return;
        }

        if (__instance._manager != null && __instance._manager.state == GameState.Loading) {
            if (AutoSellRunesPlugin.DebugLogging.Value) AutoSellRunesPlugin.Log.LogInfo($"GameState is Loading.");
            return;
        }

        
        if (AutoSellRunesPlugin.DebugLogging.Value) AutoSellRunesPlugin.Log.LogInfo($"[Harmony OK] InstantiateContainer");

        AutoSellRunesPlugin.SellRune(__result, "InstantiateContainer");
    }
}