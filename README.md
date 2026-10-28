# DragonTyper

<p align="center">
  <img src="images/icon.png" width="160" alt="DragonTyper logo" />
</p>

Dalamud plugin for **Keybound Brawler**. You queue and enter the duty yourself. Once **Active** is on, DragonTyper watches the in-game word input and submits words for you with a configurable delay (±20% random jitter). Outside the minigame it stays passive.

## What it does

- **Active** toggle: typing only runs when you turn it on
- Word delay in milliseconds, each wait randomized by ±20%
- Optional longer wait for long sentences
- Holds during Letterlock and briefly around Letterlock casts
- Prefers casting enemies, then the nearest larger target
- Does nothing when logged out or when the Keybound Brawler input is closed

## Commands

| Command | Action |
| --- | --- |
| `/dragontyper` | Open the window |
| `/dragontyper on` | Enable Active |
| `/dragontyper off` | Disable Active |
| `/dragontyper toggle` | Toggle Active |
| `/dragontyper dump` | Log raw `KTGInput` values to `/xllog` |

## Install via custom plugin repository

This plugin is **not** on the official Dalamud plugin list. Add it as a custom repository.

1. Open Dalamud settings with `/xlsettings`
2. Go to the **Experimental** tab
3. Under **Custom Plugin Repositories**, add this URL:

```text
https://raw.githubusercontent.com/Tiberius1OOO/DragonTyper/main/repo.json
```

4. Click the **+** button, then the save / disk icon
5. Open `/xlplugins`, enable **Show all plugins** / custom plugins if needed
6. Find **DragonTyper**, install it, then enable it

After install, open `/dragontyper`, turn **Active** on, and enter Keybound Brawler manually.

## Disclaimer

Third-party tools can violate FINAL FANTASY XIV terms of service. Use at your own risk. Not affiliated with Square Enix.
