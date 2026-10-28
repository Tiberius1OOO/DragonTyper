# DragonTyper

<p align="center">
  <img src="images/icon.png" width="160" alt="DragonTyper logo" />
</p>

Dalamud plugin for **Keybound Brawler**. Press **Start** once, then the plugin types the words that appear for the whole minigame until you press **Stop** (or leave). You still enter the duty yourself.

## Requirements

DragonTyper runs **solo**.

- Needs **XIVLauncher + Dalamud** only
- **No other plugins** are required
- No movement / queue automation: only word typing

## What it does

- **Start / Stop**: one click to run the whole minigame typer
- Word delay in milliseconds, each wait randomized by ±20%
- Optional longer wait for long sentences
- Holds during Letterlock and briefly around Letterlock casts
- Prefers casting enemies when entity ids are available

## Logging and troubleshooting

Open `/xllog` and filter for `DragonTyper`.

- Info: Start, addon open/close, typed words
- Warnings: input open but no words found
- Errors: submit / FireCallback failures

Use **Dump live data to log** (or `/dragontyper dump`) while words are on screen so the log shows arrays, text nodes, and open `KTG*` addons.

## Commands

| Command | Action |
| --- | --- |
| `/dragontyper` | Open the window |
| `/dragontyper start` | Start typing |
| `/dragontyper stop` | Stop |
| `/dragontyper toggle` | Toggle |
| `/dragontyper dump` | Dump live addon/array data to `/xllog` |

## Install via custom plugin repository

1. `/xlsettings` → **Experimental**
2. Add this Custom Plugin Repository URL:

```text
https://raw.githubusercontent.com/Tiberius1OOO/DragonTyper/main/repo.json
```

3. Save, then install **DragonTyper** from `/xlplugins`

## Disclaimer

Third-party tools can violate FINAL FANTASY XIV terms of service. Use at your own risk. Not affiliated with Square Enix.
