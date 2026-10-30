# DragonTyper

<p align="center">
  <img src="images/icon.png" width="140" alt="DragonTyper" />
</p>

Dalamud plugin for the Gold Saucer **Keybound Brawler** duty. You walk in yourself, hit Start once, and it types the prompts until you Stop (or the duty ends).

Nothing else. No queue bot, no pathing, no registrar spam. Just the typing part.

## Install

Needs XIVLauncher + Dalamud. No other plugins.

1. `/xlsettings` → Experimental  
2. Add this custom repo:

```text
https://raw.githubusercontent.com/Tiberius1OOO/DragonTyper/main/repo.json
```

3. Save, open `/xlplugins`, install **DragonTyper**

Or grab the zip from [Releases](https://github.com/Tiberius1OOO/DragonTyper/releases) if you prefer manual install.

## How to use

Enter the brawler. The window should pop open when you land in the duty (menus are locked in there, so that’s intentional).

- **Start** — keeps typing for the whole run  
- **Stop** — stops  
- Delay slider — wait between words, each wait is jittered by ±20% so it doesn’t look robotic  
- Optional checkbox for longer waits on long sentences  

If you hit X on the window while you’re still in the duty, it doesn’t vanish. You get a small floating logo you can click to open it again. Outside the duty it behaves like a normal window.

## What it actually does

Reads the word balloons from the game UI (all slots, not just whatever happened to be visible in a shallow scan). Picks what to type next in roughly this order:

1. Barrier / defense phrases  
2. Stuff that’s casting / worth interrupting  
3. Whatever’s closest / still alive  

It also:

- Holds when Letterlock is up (Jammer / Cursed Gaze and the “inputs disabled” banner), including a short pause after those casts finish so you don’t eat the penalty  
- Keeps the duty’s typing idle timer from kicking you (UI submit doesn’t count as a real keypress by itself)  
- Uses the client language sheet — JP goes for the reading column, EN/DE/FR strip the optional punctuation the game doesn’t care about  
- Remembers a word for a few seconds if the label flashes or gets obscured  

Status line shows what it’s about to type, hits/misses, and when it’s holding for Letterlock.

## Commands

| | |
| --- | --- |
| `/dragontyper` | open / toggle the window |
| `/dragontyper start` | Start |
| `/dragontyper stop` | Stop |
| `/dragontyper toggle` | flip Start/Stop |
| `/dragontyper dump` | dump live UI junk to `/xllog` |
| `/dragontyper capture` | 5s deep log into the plugin config folder |

There’s also a **Record 5s deep log** button in the UI. Keep the weird word on screen while it runs, then check the file if something still doesn’t get typed.

## If something’s wrong

Open `/xllog` and filter `DragonTyper`. Typed words, Letterlock holds, and submit failures show up there.

Still missing a specific prompt? Run a capture while it’s on screen and poke around the dump — that’s how the slot/array reading got fixed before.

## Notes

- English client is the main path; JP/DE/FR are wired through the sheet readings but I don’t live on those clients every day, so weird edge cases are possible.  
- Delay too low and you’ll look like a macro to anyone watching. Don’t be dumb.  
- Third-party tools can get you in trouble with Square. Your account, your call. Not affiliated with SE.

## Links

- Repo: https://github.com/Tiberius1OOO/DragonTyper  
- Latest: https://github.com/Tiberius1OOO/DragonTyper/releases/latest  
