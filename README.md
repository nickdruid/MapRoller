# MapRoller (ExileApi plugin)

Rolls every map in the currently visible stash tab until the in-game stash search highlights all of them, then fills open mods with Exalted Orbs.

## How it works

1. Open your stash on the tab with the maps and press the hotkey (default `F6`).
2. The plugin pastes your regex into the stash search box (`Ctrl+F`, `Ctrl+A`, `Ctrl+V`, `Enter`) and restores your clipboard afterwards.
3. **Rolling phase.** Every map that is not highlighted (or that is not rare yet) gets rolled:
   - It right-clicks an Orb of Alchemy stack so the orb sits on the cursor.
   - Magic / rare maps are scoured first with `Shift + Alt + Left click` (the in-game Alt-swap turns the held Alchemy into a Scouring).
   - Normal maps are alched with `Shift + Left click`. Shift keeps the orb on the cursor so it does not have to be picked up again.
   - This repeats until every rollable map is rare **and** highlighted by the search, or a map hits `Max alchemy rolls per map`.
4. **Exalt phase** (optional). Each highlighted rare map with fewer explicit mods than `Target explicit mod count` gets one Exalted Orb per pass. If a map stops being highlighted after an exalt it is scoured and rerolled in the next rolling pass (up to `Max rerolls after an exalt brick` times). If an exalt has no effect (no open affix) the map is treated as finished.
5. The run ends when all maps are done, when the currency runs out (respecting the reserves), when the hotkey is pressed again, or when the stash is closed. The cursor is cleared (Escape cancels the held orb) and the mouse returns to where it was.

Currency is read live from the visible stash tab and, optionally, from your inventory. The overlay next to the stash shows the phase, maps done, and how many Alchemy / Scouring / Exalted Orbs were used and are still left. Frames around maps: green = done, yellow = waiting for exalts, orange = still needs rolling, red = given up, grey = cannot be rolled (unique, corrupted or unidentified).

## Install

Easiest: open the PluginUpdater plugin in the HUD, go to its **Add** tab and paste `https://github.com/nickdruid/MapRoller`. It clones the repo into `Plugins/Source/MapRoller` and keeps it updated.

Manual: copy this folder to `<ExileApi>/Plugins/Source/MapRoller`. Either way the HUD compiles plugins in `Plugins/Source` when it starts (it sets `exapiPackage` itself). Enable **MapRoller** in the plugin list, set the regex and the hotkey.

To build by hand, point `exapiPackage` at your ExileApi folder:

```powershell
$env:exapiPackage = "C:\path\to\ExileApi"; dotnet build -c Release
```

## Settings

| Setting | Meaning |
| --- | --- |
| Start / Stop hotkey | Starts a run while the stash is open; pressing it again stops the run. |
| Map regex | The text pasted into the stash search. A map counts as good once the game highlights it. Keep it under the search box limit (about 250 characters) or the game truncates it. |
| Paste regex into stash search | Turn off to type the search yourself before pressing the hotkey. |
| Scour via Alt-swap | Hold Alt while clicking with an Orb of Alchemy to use an Orb of Scouring instead. Turn off and the plugin picks up Orbs of Scouring as their own stack. |
| Exalt highlighted maps / Target explicit mod count | Exalt phase on/off and how many explicit mods to aim for (max 6). |
| Max alchemy rolls per map | Give up on a map after this many alchs. Maps the regex can never match (wrong tier, wrong name) burn this many orbs, so keep the tab clean. |
| Max rerolls after an exalt brick | How often a map may be scoured and rerolled because an exalt broke the regex match. |
| Use currency from player inventory | Also count and click orb stacks in your inventory. |
| Keep at least N ... | Reserves. The run stops before the remaining amount of that orb drops below the reserve. |
| Max orbs used per run | Safety cap on the total number of orbs one run may consume. |
| Click delay / Item change timeout / Highlight settle delay / Delay after pasting the regex | Timing. Raise them if the game lags and actions get skipped. |

## Notes and limits

- The game window must be in the foreground. The run aborts if focus is lost so no clicks land in other programs.
- Unique, corrupted and unidentified maps are skipped.
- A map that is already rare and highlighted is left alone, so you can rerun on a half-finished tab.
- Only the visible stash tab is processed. For a map stash tab, the currently selected sub-tab is used.
- If "Alt + click did not scour" shows up, the Alt-swap did not work for your client or no Orb of Scouring was reachable. Turn off **Scour via Alt-swap** to click scour stacks directly.
