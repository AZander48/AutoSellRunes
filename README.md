# AutoSellRunes

Standalone BepInEx 5 plugin for [Hadean Tactics](https://store.steampowered.com/app/1324530/Hadean_Tactics/).

Auto-sells runes via Harmony postfixes on `RelicManager.CreateRune` that call `RelicBehavior.OnSell()`.

## Setup

1. Copy `AutoSellRunes.props.example` to `AutoSellRunes.props` and set `HadeanTacticsDir`.
2. Build:

```powershell
dotnet build -c Debug
```

DLL is copied to `BepInEx/plugins/AutoSellRunes/`.

## Config

`BepInEx/config/AutoSellRunes.cfg`

| Setting | Default | Meaning |
|---------|---------|---------|
| Enabled | true | Master switch |
| Debug | true | Log Harmony hits / sells |
| Use poll fallback | **false** | Poll `currentRunes` if Harmony misses |
| Poll interval seconds | 1 | Only used when poll is on |

To test Harmony-only, set `Use poll fallback = false` (change it in the cfg if an older install saved `true`).

## Harmony hooks

- `CreateRune(EffectContainer)` — reward/shop path; sells last `currentRunes` entry
- `CreateRune(string)` — id/save path; sells `__result`

With Debug on, a successful Harmony sell looks like:

```
[Harmony OK] CreateRune(EffectContainer) id=...
CreateRune(EffectContainer): OnSell -> RelicPrefab(Clone)
```
