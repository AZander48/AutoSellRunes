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

        Enabled = Config.Bind("General", "Enabled", true, "Auto-sell runes when CreateRune adds one.");
        DebugLogging = Config.Bind("General", "Debug", true, "Verbose logging.");
        UsePollFallback = Config.Bind(
            "General",
            "Use poll fallback",
            false,
            "Poll currentRunes if Harmony CreateRune hooks miss. Leave off to test Harmony-only.");
        PollIntervalSeconds = Config.Bind("General", "Poll interval seconds", 1f, "Poll rate when fallback is on.");

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
}

/// <summary>
/// Reward / shop path: CreateRune(EffectContainer) -> InstantiateContainer -> currentRunes.
/// Void return — sell the last entry after the method finishes.
/// </summary>
[HarmonyPatch]
internal static class RelicManager_CreateRune_EffectContainer_Patch
{
    private static MethodBase TargetMethod()
    {
        // Prefer full name — short name can miss or hit the wrong type.
        Type? type = AccessTools.TypeByName("HadeanTactics.RelicManager")
            ?? throw new Exception("Could not find type HadeanTactics.RelicManager");

        // Must disambiguate: CreateRune(string) vs CreateRune(EffectContainer).
        MethodInfo? method = AccessTools.Method(type, "CreateRune", new[] { typeof(EffectContainer) });
        if (method == null)
            throw new Exception("Could not find RelicManager.CreateRune(EffectContainer)");

        return method;
    }

    private static void Postfix(object __instance)
    {
        // CreateRune(EffectContainer) is void — no __result. Sell last currentRunes entry.
        if (AutoSellRunesPlugin.DebugLogging.Value)
            AutoSellRunesPlugin.Log.LogInfo("[Harmony OK] CreateRune(EffectContainer)");

        if (__instance is RelicManager manager)
            AutoSellRunesPlugin.SellLatestRune(manager, "CreateRune(EffectContainer)");
    }
}

/// <summary>
/// Save / id path — returns the created RelicBehavior.
/// </summary>
[HarmonyPatch]
internal static class RelicManager_CreateRune_String_Patch
{
    private static MethodBase TargetMethod()
    {
        // Prefer full name — short name can miss or hit the wrong type.
        Type? type = AccessTools.TypeByName("HadeanTactics.RelicManager")
            ?? throw new Exception("Could not find type HadeanTactics.RelicManager");

        // Must disambiguate: CreateRune(string) vs CreateRune(EffectContainer).
        MethodInfo? method = AccessTools.Method(type, "CreateRune", new[] { typeof(string) });
        if (method == null)
            throw new Exception("Could not find RelicManager.CreateRune(string)");

        return method;
    }

    private static void Postfix(object __instance, object __result)
    {
        if (AutoSellRunesPlugin.DebugLogging.Value)
            AutoSellRunesPlugin.Log.LogInfo($"[Harmony OK] CreateRune(string) result={__result}");

        // Optional: keep typed helpers by casting if you still reference HadeanTactics.
        if (__result is RelicBehavior rune)
            AutoSellRunesPlugin.SellRune(rune, "CreateRune(string)");
        else if (__instance is RelicManager manager)
            AutoSellRunesPlugin.SellLatestRune(manager, "CreateRune(string)");
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
