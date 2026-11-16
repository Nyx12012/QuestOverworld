# QuestOverworld 0.1.0

Lets Quest players join a server that runs the PC map. The headset loads the PC overworld, Forest included, and plays
by the server's PC rules: combat trials in the mines, Metal Bow and Spyglass loot, gems for alloys and full creature
populations.

## What you need

- **Server:** `QuestOverworld.dll` in the server's `Mods` folder. The server runs the PC map (not `/questScene`),
  TavernLib 1.5.3 or newer, and is listed with no password: the headset board only shows those.
- **Quest players:** QuestOverworld from the Download mods board in the main menu. Take QuestPCContent from the board
  too, so the trial walls in the mines draw instead of showing black.
- **PC players:** nothing.

A Quest player without the mod is turned away at join with a message that says so. With the mod, a Quest player can
still join any other server: it only does something on servers that run QuestOverworld.

## What it does

- On the server: tells the server list it is a Quest server, so it shows on the headset board while PC launchers still
  load the PC map. Lets Quest players with the mod in, which the game refuses on a PC map server, and turns away Quest
  players without it.
- On the headset: when the server runs QuestOverworld, puts the headset's map in the PC map's order, builds the
  Forest, and loads two chunks beside the Forest that the Quest map leaves out, so the headset and the server agree on
  every object.

## Settings

In the server's `UserData/MelonPreferences.cfg`, section `[QuestOverworld]`:

| Setting | Default | |
|---|---|---|
| `ListForHeadsets` | true | shows the server on the headset board |

## Install

Download `QuestOverworld-0.1.0.zip` from the Releases page and put `QuestOverworld.dll` in the server's `Mods` folder.
Quest players install QuestOverworld from the Download mods board. PC players install nothing.

## Build

Needs Windows, Python 3 and an A Township Tale install with MelonLoader. In PowerShell:

```
.\build.ps1 -Game "<your A Township Tale folder>"
```

The DLL lands in `build\`.

## Licence

MIT. See `LICENSE`.
