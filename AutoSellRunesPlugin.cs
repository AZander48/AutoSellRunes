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
    internal static ConfigEntry<bool> UsePollFallback = null!;
    internal static ConfigEntry<float> PollIntervalSeconds = null!;

    private Harmony? _harmony;

    private void Awake()
    {
        Log = Logger;

        InitializeConfig();
        _harmony = Harmony.CreateAndPatchAll(Assembly.GetExecutingAssembly(), PluginInfo.PLUGIN_GUID);

        // Always create the poller — it no-ops until UsePollFallback is on (including mid-game toggles).
        var go = new GameObject("AutoSellRunes_Poll");
        DontDestroyOnLoad(go);
        go.hideFlags = HideFlags.HideAndDontSave;
        go.AddComponent<AutoSellRunesPoller>();

        UsePollFallback.SettingChanged += (_, __) =>
            Log.LogInfo($"Poll fallback {(UsePollFallback.Value ? "enabled" : "disabled")}.");

        Log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} loaded (enabled={Enabled.Value}, poll={UsePollFallback.Value}).");
    }

    internal static void SellLatestRune(RelicManager manager, string source)
    {
        if (!Enabled.Value || manager == null)
            return;

        if (manager._manager != null && manager._manager.state == GameState.Loading)
        {
            if (DebugLogging.Value) Log.LogInfo($"{source}: skip sell (Loading).");
            return;
        }

        List<RelicBehavior>? runes = manager.currentRunes;
        if (runes == null || runes.Count == 0)
        {
            if (DebugLogging.Value) Log.LogInfo($"{source}: no runes in currentRunes.");
            return;
        }

        RelicBehavior rune = runes[runes.Count - 1];
        SellRune(rune, source);
    }

    internal static void SellRune(RelicBehavior? rune, string source)
    {
        if (!Enabled.Value || rune == null)
            return;

        if (rune.relic != null && rune.relic.containerType != EffectContainerType.rune)
            return;

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
        if (relicManager != null) 
        {
            while (relicManager.currentRunes?.Count > 0)
            {
                SellLatestRune(relicManager, "Sell All Runes");
            }
        }
    }

    private void InitializeConfig()
    {
        Enabled = Config.Bind("General", "Enabled", true, new ConfigDescription("Auto-sell runes when CreateRune adds one.", null, new ConfigurationManagerAttributes { Order = 1 }));
        DebugLogging = Config.Bind("Debug", "Debug", true, new ConfigDescription("Verbose logging.", null, new ConfigurationManagerAttributes { Order = 2 }));
        UsePollFallback = Config.Bind("Backup", "Use poll fallback", false, new ConfigDescription("Poll currentRunes if Harmony CreateRune hooks miss. Leave off to test Harmony-only.", null, new ConfigurationManagerAttributes { Order = 3 }));
        PollIntervalSeconds = Config.Bind("Backup", "Poll interval seconds", 1f, new ConfigDescription("Poll rate when fallback is on.", null, new ConfigurationManagerAttributes { Order = 4 }));
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
        Type type = AccessTools.TypeByName("HadeanTactics.RelicManager")
            ?? throw new Exception("Could not find RelicManager");
        MethodInfo? method = AccessTools.Method(
            type,
            "InstantiateContainer",
            new[]
            {
                typeof(EffectContainer),
                typeof(Transform),
                typeof(List<RelicBehavior>),
                typeof(bool),
                typeof(bool),
                typeof(TeamType),
            });
        if (method == null)
            throw new Exception("Could not find RelicManager.InstantiateContainer");
        return method;
    }

    private static void Postfix(RelicManager __instance, List<RelicBehavior> __2, RelicBehavior __result)
    {
        if (!AutoSellRunesPlugin.Enabled.Value || __result == null) return;

        if (__2 == null || !ReferenceEquals(__2, __instance.currentRunes)) return;

        if (__instance._manager != null && __instance._manager.state == GameState.Loading) return;

        if (__result.relic != null && __result.relic.containerType != EffectContainerType.rune) return;
        
        if (AutoSellRunesPlugin.DebugLogging.Value) AutoSellRunesPlugin.Log.LogInfo($"[Harmony OK] InstantiateContainer");

        AutoSellRunesPlugin.SellRune(__result, "InstantiateContainer");
    }
}

/// <summary>
/// Fallback poll — only if config enabled.
/// </summary>
public class AutoSellRunesPoller : MonoBehaviour
{
    private RelicManager? _relicManager;
    private float _nextPoll;
    private int _lastCount;

    private void Update()
    {

        if (!AutoSellRunesPlugin.Enabled.Value || !AutoSellRunesPlugin.UsePollFallback.Value)
            return;

        float now = Time.unscaledTime;
        if (now < _nextPoll)
            return;

        _nextPoll = now + Mathf.Max(0.25f, AutoSellRunesPlugin.PollIntervalSeconds.Value);

        if (_relicManager == null)
            _relicManager = FindObjectOfType<RelicManager>();

        if (_relicManager == null)
            return;

        List<RelicBehavior>? runes = _relicManager.currentRunes;
        int count = runes?.Count ?? 0;

        if (count > _lastCount && AutoSellRunesPlugin.DebugLogging.Value)
            AutoSellRunesPlugin.Log.LogInfo($"Poll: detected +{count - _lastCount} rune(s).");

        _lastCount = count;
        if (count == 0)
            return;

        if (_relicManager._manager != null && _relicManager._manager.state == GameState.Loading)
            return;

        // Sell all runes currently held.
        while ((_relicManager.currentRunes?.Count ?? 0) > 0)
        {
            int before = _relicManager.currentRunes!.Count;
            AutoSellRunesPlugin.SellLatestRune(_relicManager, "Poll");
            if ((_relicManager.currentRunes?.Count ?? 0) >= before)
                break;
        }

        _lastCount = _relicManager.currentRunes?.Count ?? 0;
    }
}
