# MapRoller (ExileApi plugin)

Rolls every map in the currently visible stash tab until the in-game stash search highlights all of them, then fills open mods with Exalted Orbs.

## How it works

1. Open your stash on the tab with the maps and press the hotkey (default `F6`).
2. The plugin pastes your regex into the stash search box (`Ctrl+F`, `Ctrl+A`, `Ctrl+V`, `Enter`) and restores your clipboard afterwards.
3. **Rolling phase.** Every map that is not highlighted (or that is not rare yet) gets rolled:
   - It right-clicks an Orb of Alchemy stack so the orb sits on the cursor.
   - Clicks are batched: one sweep scours every magic / rare map with `Shift + Alt + Left click` (the in-game Alt-swap turns the held Alchemy into a Scouring), then one sweep alches every normal map. The plugin does not wait for the server between maps; it waits once per sweep for all clicked maps to change, so the round-trip is paid per sweep, not per map.
   - Shift stays held so the orb stays on the cursor. When the held stack runs dry the sweep stops, lets the pending clicks land, picks up the next stack and continues. A map whose click had no effect is retried in the next pass; three misses in a row abort the run with a diagnosis.
   - This repeats until every rollable map is rare **and** highlighted by the search, or a map hits `Max alchemy rolls per map`.
4. **Exalt phase** (optional). One sweep puts one Exalted Orb on each highlighted rare map with fewer explicit mods than `Target explicit mod count`. If a map stops being highlighted after an exalt it is scoured and rerolled in the next rolling pass (up to `Max rerolls after an exalt brick` times). If an exalt has no effect twice the map is treated as finished.
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
| Take orbs from a separate currency tab / Currency tab | When an orb is not in the map tab or your inventory, the plugin switches to the selected tab with the Left/Right arrow keys, right-clicks the stack, and switches back. The dropdown fills with your stash tabs while the stash is open. Orbs in the map tab or inventory are used first. |
| Tab switch delay (ms) | Pause after a tab switch so the items can load. |
| Keep at least N ... | Reserves. With a reserve set, the run stops once the orbs the plugin can count (visible tab, inventory, configured currency tab) drop to it. At 0 (default) counts never stop the run. |
| Max orbs used per run | Safety cap on the total number of orbs one run may consume. |
| Click delay / Item change timeout / Highlight settle delay / Delay after pasting the regex | Timing. Click delay is the main per-map cost (15-25 ms works on most setups); the other two are paid once per sweep. Raise them if the game lags and clicks get skipped. |

## Notes and limits

- The game window must be in the foreground. The run aborts if focus is lost so no clicks land in other programs.
- Unique, corrupted and unidentified maps are skipped.
- A map that is already rare and highlighted is left alone, so you can rerun on a half-finished tab.
- Only the visible stash tab is processed. For a map stash tab, the currently selected sub-tab is used.
- "Out of orbs" is decided by the cursor, not by counts: while Shift is held the game keeps pulling orbs from stash tabs the plugin cannot see (for example a currency tab that is not selected as such). The run only stops when the cursor drops the orb and no stack can be found in the visible tab, the inventory or the configured currency tab. The "seen" numbers in the overlay are therefore only the orbs the plugin can count: visible tab, inventory, plus a snapshot of the configured currency tab taken on each visit.
- If "Alt + click did not scour" shows up, the Alt-swap did not work for your client or no Orb of Scouring was reachable. Turn off **Scour via Alt-swap** to click scour stacks directly.
