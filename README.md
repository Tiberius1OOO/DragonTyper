# DragonTyper

Dalamud plugin that automatically types words in **Keybound Brawler**.

You register and enter the duty yourself. While **Active** is on, DragonTyper watches the in-game input window and submits words with a configurable delay (±20% random jitter). Outside the minigame it does nothing.

## Features

- **Active** toggle — must be enabled before any typing
- Word delay slider (ms) with ±20% random jitter
- Optional longer wait for long sentences
- Skips typing during Letterlock and briefly around Letterlock casts
- Prefers casting enemies, then the nearest (larger) target
- Passive when logged out or when the Keybound Brawler input is closed

## Install (dev plugin)

1. Build the project (see below) or download a release zip.
2. In Dalamud: `/xlsettings` → Experimental → Dev Plugin Locations → add the folder that contains `DragonTyper.json` and `DragonTyper.dll`.
3. Enable **DragonTyper** in `/xlplugins`.

## Usage

- `/dragontyper` — open the config window
- `/dragontyper on` / `off` / `toggle` — set Active
- `/dragontyper dump` — log raw `KTGInput` values (for troubleshooting)

Turn **Active** on, queue into Keybound Brawler manually, and let the plugin type.

## Build

Requirements:

- [.NET 9+ SDK](https://dotnet.microsoft.com/download)
- XIVLauncher + Dalamud (game launched at least once)

```bash
dotnet build DragonTyper/DragonTyper.csproj -c Release
```

Output is under `DragonTyper/bin/Release/`.

## Disclaimer

Third-party tools can violate FINAL FANTASY XIV’s terms of service. Use at your own risk. This project is for personal use and is not affiliated with Square Enix.
