# AutoSellRunes

Standalone BepInEx 5 plugin for [Hadean Tactics](https://store.steampowered.com/app/1324530/Hadean_Tactics/).

Auto-sells runes when they are added to `RelicManager.currentRunes`, using a Silksong-style Harmony `TargetMethod` + `AccessTools.TypeByName` patch on `InstantiateContainer`.

## Setup

1. Copy `AutoSellRunes.props.example` to `AutoSellRunes.props` and set `HadeanTacticsDir`.
2. Build:

```powershell
dotnet build -c Debug
```

The DLL is copied to `BepInEx/plugins/AutoSellRunes/`.

## Config

`BepInEx/config/AutoSellRunes.cfg`

- **Enabled** — master switch (default true)
- **Debug** — log when the postfix runs / sells

## Note

Disable or remove any older AutoSellRunes code inside DebuggingAdventures so you only have one seller.
