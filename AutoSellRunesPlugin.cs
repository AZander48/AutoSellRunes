using System;
using System.Collections.Generic;
using System.Linq;
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

        Enabled = Config.Bind("General", "Enabled", true, "Auto-sell runes added to currentRunes.");
        DebugLogging = Config.Bind("General", "Debug", true, "Verbose logging.");
        UsePollFallback = Config.Bind(
            "General",
            "Use poll fallback",
            true,
            "If Harmony hooks never fire, poll currentRunes (this is what worked before).");
        PollIntervalSeconds = Config.Bind("General", "Poll interval seconds", 1f, "Poll rate when fallback is on.");

        LogAssemblyCSharpDiagnostics();

        _harmony = new Harmony(PluginInfo.PLUGIN_GUID);

        // Prefer the game's loaded Assembly-CSharp RelicBehavior for the closed generic List<>.Add.
        Type relicBehavior = GameAssemblies.GetType("HadeanTactics.RelicBehavior");
        Type listType = typeof(List<>).MakeGenericType(relicBehavior);
        MethodInfo add = AccessTools.Method(listType, "Add", new[] { relicBehavior })
            ?? throw new Exception("List<RelicBehavior>.Add not found");

        _harmony.Patch(
            add,
            postfix: new HarmonyMethod(typeof(CurrentRunesListAdd_Patch), nameof(CurrentRunesListAdd_Patch.Postfix)));

        // Also patch InstantiateContainer from the same game assembly.
        Type relicManager = GameAssemblies.GetType("HadeanTactics.RelicManager");
        MethodInfo instantiate = AccessTools.Method(relicManager, "InstantiateContainer")
            ?? throw new Exception("InstantiateContainer not found");
        _harmony.Patch(
            instantiate,
            prefix: new HarmonyMethod(typeof(InstantiateContainer_Patch), nameof(InstantiateContainer_Patch.Prefix)),
            postfix: new HarmonyMethod(typeof(InstantiateContainer_Patch), nameof(InstantiateContainer_Patch.Postfix)));

        foreach (MethodBase method in _harmony.GetPatchedMethods())
        {
            Patches? info = Harmony.GetPatchInfo(method);
            Log.LogInfo(
                $"Harmony patched: {method.DeclaringType}.{method.Name} " +
                $"(pre={info?.Prefixes.Count ?? 0}, post={info?.Postfixes.Count ?? 0}) " +
                $"asm={method.DeclaringType?.Assembly.GetName().Name}");
        }

        if (UsePollFallback.Value)
        {
            var go = new GameObject("AutoSellRunes_Poll");
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            go.AddComponent<AutoSellRunesPoller>().Init();
            Log.LogInfo("Poll fallback enabled.");
        }

        Log.LogInfo($"Plugin {PluginInfo.PLUGIN_GUID} loaded (enabled={Enabled.Value}).");
    }

    private static void LogAssemblyCSharpDiagnostics()
    {
        Assembly[] matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "Assembly-CSharp")
            .ToArray();

        Log.LogInfo($"Assembly-CSharp count in AppDomain: {matches.Length}");
        foreach (Assembly asm in matches)
            Log.LogInfo($"  -> Location='{asm.Location}' CodeBase='{asm.CodeBase}'");
    }

    private void OnDestroy()
    {
        _harmony?.UnpatchSelf();
    }
}

internal static class GameAssemblies
{
    public static Assembly GetAssemblyCSharp()
    {
        Assembly[] matches = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => a.GetName().Name == "Assembly-CSharp")
            .ToArray();

        if (matches.Length == 0)
            throw new Exception("Assembly-CSharp is not loaded yet");

        // Prefer the copy from the game Managed folder if multiple exist.
        Assembly? fromGame = matches.FirstOrDefault(a =>
            (a.Location ?? "").Replace('\\', '/').IndexOf("Managed", StringComparison.OrdinalIgnoreCase) >= 0);

        return fromGame ?? matches[0];
    }

    public static Type GetType(string fullName)
    {
        Type? type = GetAssemblyCSharp().GetType(fullName, throwOnError: false);
        if (type != null)
            return type;

        // Fallback: any loaded type with that name.
        type = AccessTools.TypeByName(fullName);
        if (type == null)
            throw new Exception($"Type not found: {fullName}");

        AutoSellRunesPlugin.Log.LogWarning(
            $"Type {fullName} not in preferred Assembly-CSharp; using {type.Assembly.Location}");
        return type;
    }
}

internal static class CurrentRunesListAdd_Patch
{
    private static object? _cachedRelicManager;
    private static object? _cachedCurrentRunes;

    public static void Postfix(object __instance, object __0)
    {
        try
        {
            if (AutoSellRunesPlugin.DebugLogging.Value)
                AutoSellRunesPlugin.Log.LogInfo("[Harmony OK] List<RelicBehavior>.Add postfix");

            if (!AutoSellRunesPlugin.Enabled.Value || __instance == null || __0 == null)
                return;

            if (!IsPlayerCurrentRunesList(__instance))
                return;

            if (IsLoading(_cachedRelicManager))
                return;

            TrySellRune(__0);
        }
        catch (Exception ex)
        {
            AutoSellRunesPlugin.Log.LogError($"List.Add postfix failed: {ex}");
        }
    }

    internal static void TrySellRune(object rune)
    {
        Type runeType = rune.GetType();
        object? relic = AccessTools.Field(runeType, "relic")?.GetValue(rune);
        if (relic == null)
            return;

        object? containerType = AccessTools.Field(relic.GetType(), "containerType")?.GetValue(relic);
        if (containerType == null || containerType.ToString() != "rune")
            return;

        if (AutoSellRunesPlugin.DebugLogging.Value)
        {
            string name = AccessTools.Property(runeType, "name")?.GetValue(rune) as string ?? rune.ToString();
            AutoSellRunesPlugin.Log.LogInfo($"Selling rune: {name}");
        }

        AccessTools.Method(runeType, "OnSell")?.Invoke(rune, null);
    }

    private static bool IsPlayerCurrentRunesList(object list)
    {
        if (ReferenceEquals(list, _cachedCurrentRunes))
            return true;

        Type relicManagerType = GameAssemblies.GetType("HadeanTactics.RelicManager");
        UnityEngine.Object? found = UnityEngine.Object.FindObjectOfType(relicManagerType);
        if (found == null)
            return false;

        object? currentRunes = AccessTools.Field(relicManagerType, "currentRunes")?.GetValue(found);
        _cachedRelicManager = found;
        _cachedCurrentRunes = currentRunes;
        return ReferenceEquals(list, currentRunes);
    }

    private static bool IsLoading(object? relicManager)
    {
        if (relicManager == null)
            return false;

        object? manager = AccessTools.Field(relicManager.GetType(), "_manager")?.GetValue(relicManager);
        if (manager == null)
            return false;

        object? state = AccessTools.Field(manager.GetType(), "state")?.GetValue(manager);
        return state != null && state.ToString() == "Loading";
    }
}

internal static class InstantiateContainer_Patch
{
    public static void Prefix()
    {
        if (AutoSellRunesPlugin.DebugLogging.Value)
            AutoSellRunesPlugin.Log.LogInfo("[Harmony OK] InstantiateContainer PREFIX");
    }

    public static void Postfix(object __instance, object __result)
    {
        try
        {
            if (AutoSellRunesPlugin.DebugLogging.Value)
                AutoSellRunesPlugin.Log.LogInfo("[Harmony OK] InstantiateContainer POSTFIX");

            if (!AutoSellRunesPlugin.Enabled.Value || __instance == null || __result == null)
                return;

            object? currentRunes = AccessTools.Field(__instance.GetType(), "currentRunes")?.GetValue(__instance);
            if (currentRunes is not System.Collections.IList list || !list.Contains(__result))
                return;

            object? manager = AccessTools.Field(__instance.GetType(), "_manager")?.GetValue(__instance);
            object? state = manager != null
                ? AccessTools.Field(manager.GetType(), "state")?.GetValue(manager)
                : null;
            if (state != null && state.ToString() == "Loading")
                return;

            CurrentRunesListAdd_Patch.TrySellRune(__result);
        }
        catch (Exception ex)
        {
            AutoSellRunesPlugin.Log.LogError($"InstantiateContainer postfix failed: {ex}");
        }
    }
}

/// <summary>
/// Proven working path when Harmony detours don't execute on this game.
/// </summary>
public class AutoSellRunesPoller : MonoBehaviour
{
    private RelicManager? _relicManager;
    private float _nextPoll;
    private int _lastCount;

    public void Init() { }

    private void Update()
    {
        if (!AutoSellRunesPlugin.Enabled.Value)
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

        List<RelicBehavior> copy = new List<RelicBehavior>(runes!);
        foreach (RelicBehavior rune in copy)
        {
            if (rune != null && rune.relic != null && rune.relic.containerType == EffectContainerType.rune)
            {
                if (AutoSellRunesPlugin.DebugLogging.Value)
                    AutoSellRunesPlugin.Log.LogInfo($"Poll selling: {rune.name}");
                rune.OnSell();
            }
        }

        _lastCount = _relicManager.currentRunes?.Count ?? 0;
    }
}
