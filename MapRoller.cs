using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.Elements;
using ExileCore.PoEMemory.Elements.InventoryElements;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using SharpDX;
using PoeCursor = ExileCore.PoEMemory.MemoryObjects.Cursor;
using Vector2 = SharpDX.Vector2;
using Vector2Num = System.Numerics.Vector2;

namespace MapRoller;

/// <summary>
/// Rolls every map in the visible stash tab with Orbs of Alchemy / Scouring until the in-game
/// stash search (regex) highlights all of them, then fills open mods with Exalted Orbs.
/// </summary>
public class MapRoller : BaseSettingsPlugin<MapRollerSettings>
{
    private enum Orb { Alchemy, Scouring, Exalted }
    private enum Phase { Idle, Preparing, Rolling, Exalting, Finished, Aborted }

    private const string MapPathPrefix = "Metadata/Items/Maps/";

    private static readonly Dictionary<Orb, (string Path, string Name)> OrbIds = new()
    {
        [Orb.Alchemy] = ("Metadata/Items/Currency/CurrencyUpgradeToRare", "Orb of Alchemy"),
        [Orb.Scouring] = ("Metadata/Items/Currency/CurrencyConvertToNormal", "Orb of Scouring"),
        [Orb.Exalted] = ("Metadata/Items/Currency/CurrencyAddModToRare", "Exalted Orb"),
    };

    private sealed class MapState
    {
        public int Rolls;
        public int Scours;
        public int Exalts;
        public int Bricks;
        public bool GaveUp;
        public bool ExaltDone;
        public string Note = "";
    }

    private sealed record MapInfo(
        NormalInventoryItem Element,
        (int X, int Y) Key,
        string Name,
        ItemRarity Rarity,
        int ExplicitMods,
        bool Highlighted,
        bool Identified,
        bool Corrupted)
    {
        public bool Rollable => Rarity != ItemRarity.Unique && !Corrupted && Identified;
    }

    private readonly Dictionary<(int X, int Y), MapState> _states = new();
    private SyncTask<bool> _run;
    private bool _cancelRequested;
    private bool _aborted;
    private Phase _phase = Phase.Idle;
    private string _status = "Idle";
    private readonly Stopwatch _runTimer = new();
    private int _usedAlch, _usedScour, _usedExalt, _actions;
    private Orb? _held;
    private Vector2Num _savedMouse;
    private string _savedClipboard;

    // ------------------------------------------------------------------ plugin lifecycle

    public override bool Initialise()
    {
        Name = "MapRoller";
        Input.RegisterKey(Settings.RunHotkey.Value);
        Settings.RunHotkey.OnValueChanged += () => Input.RegisterKey(Settings.RunHotkey.Value);
        return true;
    }

    public override Job Tick()
    {
        if (Settings.RunHotkey.PressedOnce())
        {
            if (_run != null)
            {
                _cancelRequested = true;
                _status = "Stopping...";
            }
            else if (StashOpen)
            {
                _cancelRequested = false;
                _run = RunAsync();
            }
            else
            {
                LogMessage("MapRoller: open your stash first.", 3);
            }
        }

        if (_run != null)
        {
            if (!StashOpen) _cancelRequested = true;
            TaskUtils.RunOrRestart(ref _run, () => null);
        }
        else if (!StashOpen && _phase != Phase.Idle)
        {
            _phase = Phase.Idle;
        }

        return null;
    }

    public override void Render()
    {
        if (!StashOpen || _phase == Phase.Idle) return;

        List<MapInfo> maps;
        try
        {
            maps = ScanMaps();
        }
        catch
        {
            return;
        }

        if (Settings.DrawMapFrames.Value) DrawFrames(maps);
        if (Settings.ShowOverlay.Value) DrawOverlay(maps);
    }

    // ------------------------------------------------------------------ game state helpers

    private StashElement Stash => GameController.IngameState.IngameUi.StashElement;
    private Inventory VisibleStash => Stash?.VisibleStash;
    private bool StashOpen => Stash is { IsVisible: true } && VisibleStash != null;
    private Vector2 WindowOffset => GameController.Window.GetWindowRectangle().TopLeft;
    private PoeCursor CursorElement => GameController.IngameState.IngameUi.Cursor;

    private IEnumerable<NormalInventoryItem> VisibleStashItems()
    {
        return VisibleStash?.VisibleInventoryItems ?? (IEnumerable<NormalInventoryItem>)Array.Empty<NormalInventoryItem>();
    }

    private IEnumerable<NormalInventoryItem> PlayerInventoryItems()
    {
        if (!Settings.UseInventoryCurrency.Value) return Array.Empty<NormalInventoryItem>();
        var panel = GameController.IngameState.IngameUi.InventoryPanel;
        if (panel is not { IsVisible: true }) return Array.Empty<NormalInventoryItem>();
        return panel[InventoryIndex.PlayerInventory]?.VisibleInventoryItems
               ?? (IEnumerable<NormalInventoryItem>)Array.Empty<NormalInventoryItem>();
    }

    private static bool IsMap(Entity entity)
    {
        return entity?.Path != null && entity.Path.StartsWith(MapPathPrefix, StringComparison.Ordinal);
    }

    private List<MapInfo> ScanMaps()
    {
        var result = new List<MapInfo>();
        foreach (var element in VisibleStashItems())
        {
            var entity = element?.Item;
            if (entity == null || !IsMap(entity)) continue;

            entity.TryGetComponent<Mods>(out var mods);
            entity.TryGetComponent<Base>(out var baseComponent);
            var rect = element.GetClientRect();

            result.Add(new MapInfo(
                element,
                ((int)MathF.Round(rect.Center.X), (int)MathF.Round(rect.Center.Y)),
                baseComponent?.Name ?? entity.Path.Substring(MapPathPrefix.Length),
                mods?.ItemRarity ?? ItemRarity.Normal,
                mods?.ExplicitMods?.Count ?? 0,
                element.isHighlighted,
                mods?.Identified ?? true,
                baseComponent?.isCorrupted ?? false));
        }

        return result;
    }

    private MapInfo FindMap((int X, int Y) key)
    {
        return ScanMaps().FirstOrDefault(m => m.Key == key);
    }

    private MapState State(MapInfo map) => State(map.Key);

    private MapState State((int X, int Y) key)
    {
        if (!_states.TryGetValue(key, out var state))
        {
            state = new MapState();
            _states[key] = state;
        }

        return state;
    }

    private bool IsDone(MapInfo map)
    {
        if (!map.Highlighted || map.Rarity != ItemRarity.Rare) return false;
        if (!Settings.UseExalts.Value) return true;
        return map.ExplicitMods >= Settings.TargetModCount.Value || State(map).ExaltDone;
    }

    private bool NeedsRoll(MapInfo map)
    {
        return map.Rollable && !State(map).GaveUp && (!map.Highlighted || map.Rarity != ItemRarity.Rare);
    }

    private bool NeedsExalt(MapInfo map)
    {
        var state = State(map);
        return map.Rollable && !state.GaveUp && !state.ExaltDone && map.Highlighted && map.Rarity == ItemRarity.Rare
               && map.ExplicitMods < Settings.TargetModCount.Value;
    }

    // ------------------------------------------------------------------ currency helpers

    private static bool IsOrb(Entity entity, Orb orb)
    {
        if (entity == null) return false;
        var (path, name) = OrbIds[orb];
        if (entity.Path == path) return true;
        return entity.TryGetComponent<Base>(out var baseComponent) && baseComponent.Name == name;
    }

    private List<NormalInventoryItem> OrbStacks(Orb orb)
    {
        return VisibleStashItems().Concat(PlayerInventoryItems())
            .Where(element => IsOrb(element?.Item, orb))
            .ToList();
    }

    private static int StackSize(NormalInventoryItem element)
    {
        return element.Item.TryGetComponent<Stack>(out var stack) ? Math.Max(stack.Size, 1) : 1;
    }

    private int CountOrbs(Orb orb)
    {
        return OrbStacks(orb).Sum(StackSize);
    }

    private int Reserve(Orb orb) => orb switch
    {
        Orb.Alchemy => Settings.AlchemyReserve.Value,
        Orb.Scouring => Settings.ScouringReserve.Value,
        _ => Settings.ExaltReserve.Value,
    };

    private bool HasSpare(Orb orb) => CountOrbs(orb) - Reserve(orb) >= 1;

    private string OutOfOrbsMessage(Orb orb)
    {
        var reserve = Reserve(orb);
        var left = CountOrbs(orb);
        return reserve > 0
            ? $"Out of {OrbIds[orb].Name}s: {left} left, {reserve} kept as reserve."
            : $"Out of {OrbIds[orb].Name}s.";
    }

    private bool Holding(Orb orb)
    {
        var cursor = CursorElement;
        if (cursor == null || cursor.Action != MouseActionType.UseItem)
        {
            _held = null;
            return false;
        }

        string baseName = null;
        try
        {
            baseName = cursor.ItemType?.BaseName;
        }
        catch
        {
            // some client versions do not expose the held item; fall back to our own bookkeeping
        }

        if (!string.IsNullOrEmpty(baseName)) return baseName == OrbIds[orb].Name;
        return _held == orb;
    }

    private bool CursorBusy()
    {
        var cursor = CursorElement;
        return cursor != null && cursor.Action != MouseActionType.Free;
    }

    // ------------------------------------------------------------------ input helpers

    private async SyncTask<bool> Wait(int milliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            await TaskUtils.NextFrame();
        }

        return true;
    }

    private async SyncTask<bool> WaitUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
        {
            if (SafeCheck(condition)) return true;
            await TaskUtils.NextFrame();
        }

        return SafeCheck(condition);
    }

    private static bool SafeCheck(Func<bool> condition)
    {
        try
        {
            return condition();
        }
        catch
        {
            return false;
        }
    }

    private async SyncTask<bool> MoveTo(RectangleF rect)
    {
        Input.SetCursorPos((rect.Center + WindowOffset).ToVector2Num());
        Input.MouseMove();
        await Wait(Settings.ClickDelay.Value);
        return true;
    }

    private async SyncTask<bool> LeftClick(bool shift, bool alt)
    {
        if (shift) Input.KeyDown(Keys.LShiftKey);
        if (alt) Input.KeyDown(Keys.LMenu);
        await Wait(30);
        Input.LeftDown();
        await Wait(25);
        Input.LeftUp();
        await Wait(30);
        if (alt) Input.KeyUp(Keys.LMenu);
        if (shift) Input.KeyUp(Keys.LShiftKey);
        await Wait(Settings.ClickDelay.Value);
        return true;
    }

    private async SyncTask<bool> RightClick()
    {
        Input.RightDown();
        await Wait(25);
        Input.RightUp();
        await Wait(Settings.ClickDelay.Value);
        return true;
    }

    private void ReleaseModifierKeys()
    {
        Input.KeyUp(Keys.LShiftKey);
        Input.KeyUp(Keys.LMenu);
        Input.KeyUp(Keys.LControlKey);
    }

    private bool Fail(string reason)
    {
        _aborted = true;
        _status = reason;
        return false;
    }

    private bool GameFocused() => GameController.Window.IsForeground();

    // ------------------------------------------------------------------ cursor / orb handling

    /// <summary>Cancels "use item" mode so the cursor is empty again (the orb returns to its stack).</summary>
    private async SyncTask<bool> ReleaseCursor()
    {
        if (!CursorBusy())
        {
            _held = null;
            return true;
        }

        Input.KeyPressRelease(Keys.Escape);
        var ok = await WaitUntil(() => !CursorBusy(), Settings.ChangeTimeout.Value);
        if (ok) _held = null;
        return ok;
    }

    private async SyncTask<bool> EnsureHolding(Orb orb)
    {
        if (Holding(orb)) return true;

        if (!await ReleaseCursor())
            return Fail("Could not clear the cursor: Escape did not cancel the held orb.");

        var stack = OrbStacks(orb).OrderByDescending(StackSize).FirstOrDefault();
        if (stack == null)
        {
            var where = Settings.UseInventoryCurrency.Value ? "the visible stash tab or your inventory" : "the visible stash tab";
            return Fail($"No {OrbIds[orb].Name} found in {where}.");
        }

        if (!GameFocused()) return Fail("The game window is not in the foreground.");

        await MoveTo(stack.GetClientRect());
        await RightClick();

        if (!await WaitUntil(CursorBusy, Settings.ChangeTimeout.Value))
            return Fail($"Right-clicking the {OrbIds[orb].Name} stack did not put it on the cursor.");

        _held = orb;
        if (!Holding(orb))
            return Fail($"The cursor holds something other than {OrbIds[orb].Name}.");

        return true;
    }

    /// <summary>
    /// Uses one orb on the map at <paramref name="key"/> and waits until <paramref name="changed"/> is true.
    /// Returns false without aborting when the map did not change within the timeout.
    /// </summary>
    private async SyncTask<bool> ApplyOrb(Orb orb, (int X, int Y) key, Func<MapInfo, bool> changed)
    {
        var useAlt = orb == Orb.Scouring && Settings.UseAltScourSwap.Value;
        var orbToHold = useAlt ? Orb.Alchemy : orb;

        if (!await EnsureHolding(orbToHold)) return false;

        var map = FindMap(key);
        if (map == null) return Fail($"The map at {key.X},{key.Y} disappeared from the stash tab.");
        if (!GameFocused()) return Fail("The game window is not in the foreground.");

        await MoveTo(map.Element.GetClientRect());
        await LeftClick(shift: true, alt: useAlt);

        var applied = await WaitUntil(() => FindMap(key) is { } m && changed(m), Settings.ChangeTimeout.Value);
        if (!applied) return false;

        _actions++;
        await Wait(Settings.HighlightSettleDelay.Value);
        return true;
    }

    // ------------------------------------------------------------------ stash search

    private async SyncTask<bool> EnterSearchText(string text)
    {
        _savedClipboard = GetClipboardText();
        SetClipboardText(text);

        Input.KeyDown(Keys.LControlKey);
        await Wait(30);
        Input.KeyPressRelease(Keys.F);
        await Wait(Settings.ClickDelay.Value);
        Input.KeyPressRelease(Keys.A);
        await Wait(30);
        Input.KeyPressRelease(Keys.V);
        await Wait(30);
        Input.KeyUp(Keys.LControlKey);
        await Wait(Settings.ClickDelay.Value);
        Input.KeyPressRelease(Keys.Enter);
        await Wait(Settings.RegexDelay.Value);
        return true;
    }

    private static string GetClipboardText()
    {
        string result = null;
        RunSta(() =>
        {
            if (Clipboard.ContainsText()) result = Clipboard.GetText();
        });
        return result;
    }

    private static void SetClipboardText(string text)
    {
        RunSta(() => Clipboard.SetText(text));
    }

    private static void RunSta(Action action)
    {
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch
            {
                // clipboard access is best effort
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join(2000);
    }

    // ------------------------------------------------------------------ the run

    private async SyncTask<bool> RunAsync()
    {
        _phase = Phase.Preparing;
        _status = "Starting";
        _states.Clear();
        _aborted = false;
        _held = null;
        _usedAlch = _usedScour = _usedExalt = _actions = 0;
        _runTimer.Restart();
        _savedMouse = Input.ForceMousePositionNum;
        _savedClipboard = null;

        try
        {
            var result = await RunCore();
            if (_aborted)
            {
                _phase = Phase.Aborted;
                LogError($"MapRoller stopped: {_status}", 8);
            }
            else
            {
                _phase = Phase.Finished;
                LogMessage($"MapRoller: {_status}", 8);
            }

            return result;
        }
        catch (Exception ex)
        {
            _phase = Phase.Aborted;
            _status = $"Error: {ex.Message}";
            LogError($"MapRoller crashed: {ex}", 10);
            return false;
        }
        finally
        {
            await Cleanup();
            _runTimer.Stop();
        }
    }

    private async SyncTask<bool> RunCore()
    {
        if (!StashOpen) return Fail("The stash is not open.");
        if (!GameFocused()) return Fail("The game window is not in the foreground.");

        var regex = Settings.MapRegex.Value?.Trim() ?? "";
        if (Settings.ApplyRegexToSearch.Value)
        {
            if (regex.Length == 0) return Fail("No regex configured. Set 'Map regex' in the plugin settings.");
            _status = "Entering regex into the stash search";
            await EnterSearchText(regex);
        }

        var initial = ScanMaps();
        if (initial.Count == 0) return Fail("No maps in the visible stash tab.");
        if (!initial.Any(m => m.Rollable)) return Fail("Every map in this tab is unique, corrupted or unidentified.");

        foreach (var map in initial.Where(m => !m.Rollable))
        {
            State(map).Note = map.Corrupted ? "corrupted" : map.Rarity == ItemRarity.Unique ? "unique" : "unidentified";
        }

        while (true)
        {
            if (_cancelRequested) return Fail(StashOpen ? "Stopped by hotkey." : "The stash was closed.");
            if (_actions >= Settings.MaxActionsPerRun.Value)
            {
                _status = $"Reached the cap of {Settings.MaxActionsPerRun.Value} orbs for this run.";
                return true;
            }

            var maps = ScanMaps();
            var toRoll = maps.Where(NeedsRoll).ToList();
            if (toRoll.Count > 0)
            {
                _phase = Phase.Rolling;
                if (!await RollPass(toRoll)) return false;
                continue;
            }

            if (!Settings.UseExalts.Value) break;

            var toExalt = maps.Where(NeedsExalt).ToList();
            if (toExalt.Count == 0) break;

            _phase = Phase.Exalting;
            if (!await ExaltPass(toExalt)) return false;
        }

        var final = ScanMaps();
        var done = final.Count(IsDone);
        var gaveUp = final.Count(m => m.Rollable && State(m).GaveUp);
        _status = gaveUp == 0
            ? $"Done: {done} maps rolled ({_usedAlch} alch, {_usedScour} scour, {_usedExalt} exalt)."
            : $"Done: {done} maps rolled, {gaveUp} given up ({_usedAlch} alch, {_usedScour} scour, {_usedExalt} exalt).";
        return true;
    }

    /// <summary>One pass over the maps that do not match the regex yet: scour if needed, then alch once.</summary>
    private async SyncTask<bool> RollPass(List<MapInfo> toRoll)
    {
        foreach (var snapshot in toRoll)
        {
            if (_cancelRequested) return Fail(StashOpen ? "Stopped by hotkey." : "The stash was closed.");

            var key = snapshot.Key;
            var state = State(key);
            var map = FindMap(key);
            if (map == null || !NeedsRoll(map)) continue;

            if (state.Rolls >= Settings.MaxRollsPerMap.Value)
            {
                state.GaveUp = true;
                state.Note = $"gave up after {state.Rolls} rolls";
                continue;
            }

            if (map.Rarity != ItemRarity.Normal)
            {
                if (!HasSpare(Orb.Scouring))
                {
                    _status = OutOfOrbsMessage(Orb.Scouring);
                    return false;
                }

                _status = $"Scouring {map.Name} (roll {state.Rolls + 1})";
                if (!await ApplyOrb(Orb.Scouring, key, m => m.Rarity == ItemRarity.Normal))
                {
                    if (_aborted) return false;
                    return Fail(Settings.UseAltScourSwap.Value
                        ? $"Alt + click did not scour {map.Name}. Check that Orbs of Scouring are reachable and that the Alt-swap works for you, or turn off 'Scour via Alt-swap'."
                        : $"The Orb of Scouring had no effect on {map.Name}.");
                }

                state.Scours++;
                _usedScour++;
            }

            if (!HasSpare(Orb.Alchemy))
            {
                _status = OutOfOrbsMessage(Orb.Alchemy);
                return false;
            }

            _status = $"Alching {map.Name} (roll {state.Rolls + 1})";
            if (!await ApplyOrb(Orb.Alchemy, key, m => m.Rarity == ItemRarity.Rare))
            {
                if (_aborted) return false;
                return Fail($"The Orb of Alchemy had no effect on {map.Name}.");
            }

            state.Rolls++;
            state.ExaltDone = false;
            _usedAlch++;

            if (_actions >= Settings.MaxActionsPerRun.Value) return true;
        }

        return true;
    }

    /// <summary>One pass over the highlighted maps that still have open mods: one exalt each.</summary>
    private async SyncTask<bool> ExaltPass(List<MapInfo> toExalt)
    {
        foreach (var snapshot in toExalt)
        {
            if (_cancelRequested) return Fail(StashOpen ? "Stopped by hotkey." : "The stash was closed.");

            var key = snapshot.Key;
            var state = State(key);
            var map = FindMap(key);
            if (map == null || !NeedsExalt(map)) continue;

            if (!HasSpare(Orb.Exalted))
            {
                _status = OutOfOrbsMessage(Orb.Exalted);
                return false;
            }

            var modsBefore = map.ExplicitMods;
            _status = $"Exalting {map.Name} ({modsBefore} -> {modsBefore + 1} mods)";
            if (!await ApplyOrb(Orb.Exalted, key, m => m.ExplicitMods != modsBefore || m.Rarity != ItemRarity.Rare))
            {
                if (_aborted) return false;
                state.ExaltDone = true;
                state.Note = $"exalt had no effect at {modsBefore} mods (no open affix)";
                continue;
            }

            state.Exalts++;
            _usedExalt++;

            var after = FindMap(key);
            if (after != null && !after.Highlighted)
            {
                state.Bricks++;
                if (state.Bricks > Settings.MaxBricksPerMap.Value)
                {
                    state.GaveUp = true;
                    state.Note = $"bricked by exalts {state.Bricks} times";
                }
                else
                {
                    state.Note = $"bricked by an exalt ({state.Bricks}), rerolling";
                }
            }

            if (_actions >= Settings.MaxActionsPerRun.Value) return true;
        }

        return true;
    }

    private async SyncTask<bool> Cleanup()
    {
        try
        {
            ReleaseModifierKeys();
            if (GameFocused())
            {
                await ReleaseCursor();
                Input.SetCursorPos(_savedMouse);
                Input.MouseMove();
            }
        }
        catch (Exception ex)
        {
            LogError($"MapRoller cleanup failed: {ex.Message}", 5);
        }

        if (_savedClipboard != null)
        {
            SetClipboardText(_savedClipboard);
            _savedClipboard = null;
        }

        return true;
    }

    // ------------------------------------------------------------------ overlay

    private void DrawFrames(List<MapInfo> maps)
    {
        var target = Settings.TargetModCount.Value;
        foreach (var map in maps)
        {
            Color color;
            if (!map.Rollable) color = Color.Gray;
            else if (State(map).GaveUp) color = Color.Red;
            else if (IsDone(map)) color = Color.LimeGreen;
            else if (map.Highlighted && map.Rarity == ItemRarity.Rare && map.ExplicitMods < target) color = Color.Yellow;
            else color = Color.Orange;

            var rect = map.Element.GetClientRect();
            rect.Inflate(-2, -2);
            Graphics.DrawFrame(rect.TopLeft.ToVector2Num(), rect.BottomRight.ToVector2Num(), color, 2);
        }
    }

    private void DrawOverlay(List<MapInfo> maps)
    {
        var rollable = maps.Where(m => m.Rollable).ToList();
        var done = rollable.Count(IsDone);
        var gaveUp = rollable.Count(m => State(m).GaveUp);
        var skipped = maps.Count - rollable.Count;
        var bricks = _states.Values.Sum(s => s.Bricks);

        var summary = $"Maps: {done} / {rollable.Count} done";
        if (gaveUp > 0) summary += $", {gaveUp} given up";
        if (skipped > 0) summary += $", {skipped} skipped";
        if (bricks > 0) summary += $", {bricks} exalt bricks";

        var lines = new List<string>
        {
            $"MapRoller  [{_phase}]  {_runTimer.Elapsed:mm\\:ss}",
            summary,
            $"Alchemy   used {_usedAlch,4}   left {CountOrbs(Orb.Alchemy),5}",
            $"Scouring  used {_usedScour,4}   left {CountOrbs(Orb.Scouring),5}",
            $"Exalted   used {_usedExalt,4}   left {CountOrbs(Orb.Exalted),5}",
            _status,
        };

        var anchor = VisibleStash.InventoryUIElement?.GetClientRect() ?? Stash.GetClientRect();
        var position = new Vector2Num(anchor.Right + 12, anchor.Top);
        var statusColor = _phase switch
        {
            Phase.Aborted => Color.OrangeRed,
            Phase.Finished => Color.LimeGreen,
            _ => Color.White,
        };

        for (var i = 0; i < lines.Count; i++)
        {
            var color = i == lines.Count - 1 ? statusColor : Color.White;
            var size = Graphics.DrawTextWithBackground(lines[i], position, color, new Color(0, 0, 0, 180));
            position.Y += size.Y + 2;
        }
    }
}
