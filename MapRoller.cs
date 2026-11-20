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
/// Rolls every map in the visible stash tab with Orbs of Alchemy / Scouring (or Chaos Orbs) until the in-game
/// stash search (regex) highlights all of them, then fills open mods with Exalted Orbs.
/// </summary>
public class MapRoller : BaseSettingsPlugin<MapRollerSettings>
{
    private enum Orb { Alchemy, Scouring, Chaos, Exalted }
    private enum Phase { Idle, Preparing, Rolling, Exalting, Finished, Aborted }

    private const string MapPathPrefix = "Metadata/Items/Maps/";

    private static readonly Dictionary<Orb, (string Path, string Name)> OrbIds = new()
    {
        [Orb.Alchemy] = ("Metadata/Items/Currency/CurrencyUpgradeToRare", "Orb of Alchemy"),
        [Orb.Scouring] = ("Metadata/Items/Currency/CurrencyConvertToNormal", "Orb of Scouring"),
        [Orb.Chaos] = ("Metadata/Items/Currency/CurrencyRerollRare", "Chaos Orb"),
        [Orb.Exalted] = ("Metadata/Items/Currency/CurrencyAddModToRare", "Exalted Orb"),
    };

    private sealed class MapState
    {
        public int Rolls;
        public int Scours;
        public int Exalts;
        public int Bricks;
        public int Misses; // clicks in a row that had no effect on this map
        public bool GaveUp;
        public bool ExaltDone;
        public string Note = "";
    }

    private sealed record SweepTarget((int X, int Y) Key, Func<MapInfo, bool> Changed);

    private sealed record MapInfo(
        NormalInventoryItem Element,
        (int X, int Y) Key,
        string Name,
        ItemRarity Rarity,
        int ExplicitMods,
        string ModSignature,
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
    private int _usedAlch, _usedScour, _usedChaos, _usedExalt, _actions;
    private Orb? _held;
    private Vector2Num _savedMouse;
    private string _savedClipboard;

    // currency tab support
    private readonly List<string> _tabLabels = new();
    private string _tabLabelsKey = "";
    private int _mapTabIndex = -1;
    private readonly Dictionary<Orb, int> _currencyTabCounts = new();
    private bool _currencyTabCounted;

    // ------------------------------------------------------------------ plugin lifecycle

    public override bool Initialise()
    {
        Name = "MapRoller";
        Input.RegisterKey(Settings.RunHotkey.Value);
        Settings.RunHotkey.OnValueChanged += () => Input.RegisterKey(Settings.RunHotkey.Value);
        return true;
    }

    /// <summary>
    /// Set when the HUD unloads the plugin (hot reload, disable, shutdown). Every wait loop checks it so a running
    /// task winds down within a frame instead of living on in the HUD's frame queue and pinning the old assembly.
    /// </summary>
    private bool _shutdown;

    public override void OnPluginDestroyForHotReload() => Shutdown();

    public override void OnClose() => Shutdown();

    public override void OnUnload() => Shutdown();

    private void Shutdown()
    {
        _shutdown = true;
        _cancelRequested = true;
        try
        {
            ReleaseModifierKeys();
        }
        catch
        {
            // best effort while the HUD tears the plugin down
        }
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
            if (!StashPanelVisible) _cancelRequested = true;
            TaskUtils.RunOrRestart(ref _run, () => null);
        }
        else if (!StashPanelVisible && _phase != Phase.Idle)
        {
            _phase = Phase.Idle;
        }

        if (StashPanelVisible) RefreshTabList();

        return null;
    }

    /// <summary>Feeds the player's stash tab names into the "Currency tab" dropdown while the stash is open.</summary>
    private void RefreshTabList()
    {
        try
        {
            var names = Stash.Inventories?.Select(x => x.TabName).ToList();
            if (names == null || names.Count == 0) return;

            var key = string.Join("\u0001", names);
            if (key == _tabLabelsKey) return;
            _tabLabelsKey = key;

            _tabLabels.Clear();
            var seen = new HashSet<string>();
            for (var i = 0; i < names.Count; i++)
            {
                var label = string.IsNullOrWhiteSpace(names[i]) ? $"(tab {i + 1})" : names[i];
                if (!seen.Add(label)) label = $"{label} ({i + 1})";
                _tabLabels.Add(label);
            }

            Settings.CurrencyTab.SetListValues(new List<string>(_tabLabels));
        }
        catch
        {
            // stash data can be mid-update; try again next frame
        }
    }

    private int ResolveCurrencyTabIndex()
    {
        var wanted = Settings.CurrencyTab.Value;
        if (string.IsNullOrEmpty(wanted)) return -1;
        return _tabLabels.IndexOf(wanted);
    }

    private bool CurrencyTabMode => Settings.UseCurrencyTab.Value;
    private int CurrentTabIndex => Stash?.IndexVisibleStash ?? -1;

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
    private bool StashPanelVisible => Stash is { IsVisible: true };
    private bool StashOpen => StashPanelVisible && VisibleStash != null;
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
                ModSignature(mods),
                element.isHighlighted,
                mods?.Identified ?? true,
                baseComponent?.isCorrupted ?? false));
        }

        // grid order: top row first, left to right (the HUD returns items in memory order)
        result.Sort((a, b) => a.Key.Y != b.Key.Y ? a.Key.Y.CompareTo(b.Key.Y) : a.Key.X.CompareTo(b.Key.X));
        return result;
    }

    /// <summary>Explicit mods and their rolled values as one string, so a Chaos Orb reroll shows up even when the mod count stays the same.</summary>
    private static string ModSignature(Mods mods)
    {
        var explicitMods = mods?.ExplicitMods;
        if (explicitMods == null || explicitMods.Count == 0) return "";
        return string.Join("|", explicitMods.Select(m => m.RawName + ":" + string.Join(",", m.Values ?? new List<int>())));
    }

    private MapInfo FindMap((int X, int Y) key)
    {
        return ScanMaps().FirstOrDefault(m => m.Key == key);
    }

    private Dictionary<(int X, int Y), MapInfo> MapsByKey()
    {
        var result = new Dictionary<(int X, int Y), MapInfo>();
        foreach (var map in ScanMaps()) result[map.Key] = map;
        return result;
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

    /// <summary>Orbs reachable right now: visible tab + inventory, plus the last count seen in the currency tab (if that tab is not the visible one).</summary>
    private int CountOrbs(Orb orb)
    {
        var count = OrbStacks(orb).Sum(StackSize);
        if (CurrencyTabMode && _currencyTabCounted && CurrentTabIndex != ResolveCurrencyTabIndex())
        {
            count += _currencyTabCounts.GetValueOrDefault(orb);
        }

        return count;
    }

    /// <summary>Called after an orb was consumed; keeps the currency tab snapshot in step until the next visit refreshes it.</summary>
    private void NoteOrbConsumed(Orb orb)
    {
        if (!CurrencyTabMode || !_currencyTabCounted) return;
        if (OrbStacks(orb).Count > 0) return; // it came from the visible tab or the inventory, which are counted live
        if (_currencyTabCounts.TryGetValue(orb, out var left) && left > 0) _currencyTabCounts[orb] = left - 1;
    }

    private int Reserve(Orb orb) => orb switch
    {
        Orb.Alchemy => Settings.AlchemyReserve.Value,
        Orb.Scouring => Settings.ScouringReserve.Value,
        Orb.Chaos => Settings.ChaosReserve.Value,
        _ => Settings.ExaltReserve.Value,
    };

    /// <summary>
    /// True when a reserve is configured and the orbs the plugin can count have dropped to it.
    /// Without a reserve the counts never gate the run: while Shift is held the game pulls orbs from
    /// stash tabs the plugin cannot see, so "out of orbs" is only decided when the cursor really loses the orb.
    /// </summary>
    private bool ReserveReached(Orb orb) => Reserve(orb) > 0 && CountOrbs(orb) - Reserve(orb) < 1;

    private string ReserveMessage(Orb orb) =>
        $"Stopped: {CountOrbs(orb)} {OrbIds[orb].Name}s counted, {Reserve(orb)} kept as reserve.";

    private string OutOfOrbsMessage(Orb orb)
    {
        var where = CurrencyTabMode
            ? "the currency tab, the map tab or your inventory"
            : Settings.UseInventoryCurrency.Value ? "the visible stash tab or your inventory" : "the visible stash tab";
        return $"Out of {OrbIds[orb].Name}s: none left in {where}.";
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
        while (stopwatch.ElapsedMilliseconds < milliseconds && !_shutdown)
        {
            await TaskUtils.NextFrame();
        }

        return true;
    }

    private async SyncTask<bool> WaitUntil(Func<bool> condition, int timeoutMilliseconds)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds && !_shutdown)
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

    /// <summary>
    /// Left-clicks the current mouse position. Shift (and Alt for scouring) are expected to be held already.
    /// No trailing pause: the sweep moves straight on to the next map and verifies all of them afterwards.
    /// </summary>
    private async SyncTask<bool> LeftClick()
    {
        Input.LeftDown();
        await Wait(15);
        Input.LeftUp();
        return true;
    }

    private async SyncTask<bool> HoldAlt()
    {
        Input.KeyDown(Keys.LMenu);
        await WaitUntil(() => Input.IsKeyDown(Keys.LMenu), 150);
        await Wait(10);
        return true;
    }

    private async SyncTask<bool> ReleaseAlt()
    {
        await Wait(15);
        Input.KeyUp(Keys.LMenu);
        await WaitUntil(() => !Input.IsKeyDown(Keys.LMenu), 150);
        return true;
    }

    private bool _shiftHeld;

    /// <summary>Presses and keeps Shift down so every click keeps the orb on the cursor. Waits until the game sees the key.</summary>
    private async SyncTask<bool> HoldShift()
    {
        if (_shiftHeld && Input.IsKeyDown(Keys.LShiftKey)) return true;
        Input.KeyDown(Keys.LShiftKey);
        _shiftHeld = true;
        if (!await WaitUntil(() => Input.IsKeyDown(Keys.LShiftKey), 200))
        {
            Debug("Shift key state did not register after KeyDown");
        }

        await Wait(30);
        return true;
    }

    private async SyncTask<bool> ReleaseShift()
    {
        if (!_shiftHeld && !Input.IsKeyDown(Keys.LShiftKey)) return true;
        Input.KeyUp(Keys.LShiftKey);
        _shiftHeld = false;
        await WaitUntil(() => !Input.IsKeyDown(Keys.LShiftKey), 200);
        await Wait(30);
        return true;
    }

    private void Debug(string message)
    {
        if (Settings.DebugLog.Value) LogMessage($"MapRoller: {message}", 5);
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
        _shiftHeld = false;
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

        await ReleaseShift();
        Input.KeyPressRelease(Keys.Escape);
        var ok = await WaitUntil(() => !CursorBusy(), Settings.ChangeTimeout.Value);
        if (ok) _held = null;
        return ok;
    }

    private async SyncTask<bool> EnsureHolding(Orb orb)
    {
        if (Holding(orb)) return true;

        if (Settings.DebugLog.Value)
        {
            var cursor = CursorElement;
            string itemName = null;
            try { itemName = cursor?.ItemType?.BaseName; } catch { }
            Debug($"Need {OrbIds[orb].Name}; cursor action={cursor?.Action}, item='{itemName ?? "?"}', shiftHeld={_shiftHeld}, shiftDown={Input.IsKeyDown(Keys.LShiftKey)}");
        }

        if (!await ReleaseCursor())
            return Fail("Could not clear the cursor: Escape did not cancel the held orb.");

        // Shift + right click on a stack would open the split dialog instead of picking the orb up
        await ReleaseShift();

        var stack = OrbStacks(orb).OrderByDescending(StackSize).FirstOrDefault();
        var switchedTab = false;

        if (stack == null && CurrencyTabMode)
        {
            var tabIndex = ResolveCurrencyTabIndex();
            if (tabIndex < 0) return Fail("Currency tab is enabled but no tab is selected in the settings.");

            _status = $"Fetching {OrbIds[orb].Name} from the currency tab";
            if (!await SwitchToTab(tabIndex)) return false;
            switchedTab = true;
            SnapshotCurrencyTab();
            stack = OrbStacks(orb).OrderByDescending(StackSize).FirstOrDefault();
        }

        if (stack == null) return Fail(OutOfOrbsMessage(orb));

        if (!GameFocused()) return Fail("The game window is not in the foreground.");

        await MoveTo(stack.GetClientRect());
        await RightClick();

        if (!await WaitUntil(CursorBusy, Settings.ChangeTimeout.Value))
            return Fail($"Right-clicking the {OrbIds[orb].Name} stack did not put it on the cursor.");

        _held = orb;
        if (!Holding(orb))
            return Fail($"The cursor holds something other than {OrbIds[orb].Name}.");

        if (switchedTab)
        {
            // the orb stays on the cursor while the tab changes; go back to the maps
            if (!await SwitchToTab(_mapTabIndex)) return false;
            if (!await WaitUntil(() => ScanMaps().Count > 0, Settings.ChangeTimeout.Value))
                return Fail("The maps did not show up again after switching back to the map tab.");
        }

        return true;
    }

    /// <summary>Records how many orbs of each kind sit in the currently visible tab (used while on the currency tab).</summary>
    private void SnapshotCurrencyTab()
    {
        foreach (var orb in OrbIds.Keys)
        {
            _currencyTabCounts[orb] = OrbStacks(orb).Where(s => VisibleStashItems().Contains(s)).Sum(StackSize);
        }

        _currencyTabCounted = true;
    }

    /// <summary>Switches the stash to the given tab index with the arrow keys (the default PoE binding) and waits for it to load.</summary>
    private async SyncTask<bool> SwitchToTab(int tabIndex)
    {
        if (tabIndex < 0) return Fail("No stash tab to switch to.");

        await ReleaseShift();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = CurrentTabIndex;
            if (current == tabIndex) break;
            if (!GameFocused()) return Fail("The game window is not in the foreground.");

            var distance = tabIndex - current;
            var key = distance < 0 ? Keys.Left : Keys.Right;
            for (var i = 0; i < Math.Abs(distance); i++)
            {
                Input.KeyPressRelease(key);
                await Wait(Math.Max(20, Settings.ClickDelay.Value / 2));
            }

            await WaitUntil(() => CurrentTabIndex == tabIndex, Settings.ChangeTimeout.Value);
        }

        if (CurrentTabIndex != tabIndex)
            return Fail($"Could not switch to stash tab {tabIndex + 1} (now on {CurrentTabIndex + 1}). Is the stash search box still focused?");

        await Wait(Settings.TabSwitchDelay.Value);
        if (!await WaitUntil(() => VisibleStash?.VisibleInventoryItems != null, Settings.ChangeTimeout.Value))
            return Fail($"Stash tab {tabIndex + 1} did not load its items.");

        return true;
    }

    /// <summary>Visits the currency tab once to learn how many orbs it holds, then returns to the maps.</summary>
    private async SyncTask<bool> RefreshCurrencyTabCounts()
    {
        var tabIndex = ResolveCurrencyTabIndex();
        if (tabIndex < 0) return Fail("Currency tab is enabled but no tab is selected in the settings.");
        if (tabIndex == _mapTabIndex)
        {
            _currencyTabCounted = false; // same tab: everything is counted live
            return true;
        }

        _status = "Counting orbs in the currency tab";
        if (!await SwitchToTab(tabIndex)) return false;
        SnapshotCurrencyTab();
        if (!await SwitchToTab(_mapTabIndex)) return false;
        return await WaitUntil(() => ScanMaps().Count > 0, Settings.ChangeTimeout.Value)
               || Fail("The maps did not show up again after switching back to the map tab.");
    }

    /// <summary>
    /// Uses one orb on the map at <paramref name="key"/> and waits until <paramref name="changed"/> is true.
    /// Returns false without aborting when the map did not change within the timeout.
    /// </summary>
    /// <summary>
    /// Uses one <paramref name="orb"/> on every target, in grid order, without waiting for the server between maps.
    /// After the sweep it waits once for all clicked maps to change. The game processes each click in order, so
    /// the round-trip is paid once per sweep instead of once per map.
    /// Returns, for every map that was clicked, whether it changed. Maps that were not clicked (sweep cut short)
    /// are absent. Returns null when the run aborted (cancel, lost focus, or the orb ran out everywhere).
    /// </summary>
    private async SyncTask<Dictionary<(int X, int Y), bool>> Sweep(Orb orb, List<SweepTarget> targets, string verb)
    {
        var useAlt = orb == Orb.Scouring && Settings.UseAltScourSwap.Value;
        var orbToHold = useAlt ? Orb.Alchemy : orb;
        var results = new Dictionary<(int X, int Y), bool>();
        var index = 0;
        var pickups = 0;
        var altHeld = false;

        try
        {
            while (index < targets.Count)
            {
                if (_cancelRequested)
                {
                    Fail(StashOpen ? "Stopped by hotkey." : "The stash was closed.");
                    return null;
                }

                // first iteration: pick the orb up; later iterations: the stack ran dry and a new one is needed
                if (!await EnsureHolding(orbToHold)) return null;
                if (++pickups > targets.Count + 2)
                {
                    Fail($"The {OrbIds[orbToHold].Name} keeps dropping off the cursor right after it is picked up.");
                    return null;
                }

                // with a reserve configured, do not click more maps than the counted spare orbs allow
                var budget = Reserve(orb) > 0 ? Math.Max(0, CountOrbs(orb) - Reserve(orb)) : int.MaxValue;
                if (budget == 0)
                {
                    _status = ReserveMessage(orb);
                    break;
                }

                var maps = MapsByKey();
                var pending = new List<SweepTarget>();

                await HoldShift();
                if (useAlt && !altHeld)
                {
                    await HoldAlt();
                    altHeld = true;
                }

                for (; index < targets.Count; index++)
                {
                    if (_cancelRequested || !GameFocused()) break;
                    if (pending.Count >= budget) break;
                    if (_actions + pending.Count >= Settings.MaxActionsPerRun.Value) break;

                    var target = targets[index];
                    if (!maps.TryGetValue(target.Key, out var map)) continue;

                    // the stack ran dry: stop clicking, let the pending clicks land, then pick a new stack up
                    if (!CursorBusy()) break;

                    _status = $"{verb} {index + 1}/{targets.Count}: {map.Name}";
                    await MoveTo(map.Element.GetClientRect());
                    await LeftClick();
                    pending.Add(target);
                }

                if (pending.Count > 0)
                {
                    _status = $"{verb}: waiting for {pending.Count} maps to change";
                    var timeout = Settings.ChangeTimeout.Value + 25 * pending.Count;
                    await WaitUntil(() =>
                    {
                        var now = MapsByKey();
                        return pending.All(t => now.TryGetValue(t.Key, out var m) && t.Changed(m));
                    }, timeout);
                    await Wait(Settings.HighlightSettleDelay.Value);

                    var after = MapsByKey();
                    foreach (var target in pending)
                    {
                        var changed = after.TryGetValue(target.Key, out var m) && target.Changed(m);
                        results[target.Key] = changed;
                        if (!changed) continue;
                        _actions++;
                        NoteOrbConsumed(orb);
                    }
                }

                if (_cancelRequested)
                {
                    Fail(StashOpen ? "Stopped by hotkey." : "The stash was closed.");
                    return null;
                }

                if (!GameFocused())
                {
                    Fail("The game window is not in the foreground.");
                    return null;
                }

                if (_actions >= Settings.MaxActionsPerRun.Value) break;
                if (pending.Count >= budget && budget != int.MaxValue) break;

                if (index < targets.Count && !CursorBusy())
                {
                    _held = null;
                    Debug($"The {OrbIds[orbToHold].Name} stack ran dry after {pending.Count} clicks; looking for another one.");
                }
            }
        }
        finally
        {
            if (altHeld) await ReleaseAlt();
        }

        return results;
    }

    // ------------------------------------------------------------------ stash search

    private async SyncTask<bool> EnterSearchText(string text)
    {
        _savedClipboard = GetClipboardText();
        SetClipboardText(text);

        await ReleaseShift();
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
        _usedAlch = _usedScour = _usedChaos = _usedExalt = _actions = 0;
        _runTimer.Restart();
        _savedMouse = Input.ForceMousePositionNum;
        _savedClipboard = null;
        _mapTabIndex = CurrentTabIndex;
        _currencyTabCounts.Clear();
        _currencyTabCounted = false;

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

        if (CurrencyTabMode && !await RefreshCurrencyTabCounts()) return false;

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
        var used = Settings.UseChaosOrbs.Value
            ? $"{_usedChaos} chaos, {_usedAlch} alch, {_usedScour} scour, {_usedExalt} exalt"
            : $"{_usedAlch} alch, {_usedScour} scour, {_usedExalt} exalt";
        _status = gaveUp == 0
            ? $"Done: {done} maps rolled ({used})."
            : $"Done: {done} maps rolled, {gaveUp} given up ({used}).";
        return true;
    }

    private const int MaxMisses = 3;

    /// <summary>
    /// One pass over the maps that do not match the regex yet. Alch + Scour mode: one scour sweep over everything
    /// that is magic or rare, then one alch sweep over everything that is normal. Chaos mode: one scour sweep over
    /// magic maps only, one alch sweep over normal maps, then one chaos sweep over the rare maps. Maps whose click
    /// had no effect are retried in the next pass; after <see cref="MaxMisses"/> misses in a row the run aborts with
    /// a diagnosis.
    /// </summary>
    private async SyncTask<bool> RollPass(List<MapInfo> toRoll)
    {
        var chaosMode = Settings.UseChaosOrbs.Value;
        var keys = new List<(int X, int Y)>();
        foreach (var map in toRoll)
        {
            var state = State(map);
            if (state.Rolls >= Settings.MaxRollsPerMap.Value)
            {
                state.GaveUp = true;
                state.Note = $"gave up after {state.Rolls} rolls";
                continue;
            }

            keys.Add(map.Key);
        }

        if (keys.Count == 0) return true;

        // 1. scour sweep (in chaos mode only magic maps need it; rare maps are rerolled in place)
        var maps = MapsByKey();
        var toScour = keys
            .Where(k => maps.TryGetValue(k, out var m) && m.Rarity != ItemRarity.Normal && !(chaosMode && m.Rarity == ItemRarity.Rare))
            .Select(k => new SweepTarget(k, m => m.Rarity == ItemRarity.Normal))
            .ToList();

        if (toScour.Count > 0)
        {
            if (ReserveReached(Orb.Scouring))
            {
                _status = ReserveMessage(Orb.Scouring);
                return false;
            }

            var scoured = await Sweep(Orb.Scouring, toScour, "Scouring");
            if (scoured == null) return false;

            foreach (var (key, changed) in scoured)
            {
                var state = State(key);
                if (changed)
                {
                    state.Misses = 0;
                    state.Scours++;
                    _usedScour++;
                    continue;
                }

                if (++state.Misses < MaxMisses) continue;
                var name = maps.TryGetValue(key, out var m) ? m.Name : "a map";
                return Fail(Settings.UseAltScourSwap.Value
                    ? $"Alt + click did not scour {name} {MaxMisses} times: either no Orb of Scouring is reachable (out of scours) or the Alt-swap does not work for you. Turn off 'Scour via Alt-swap' to click scour stacks directly."
                    : $"The Orb of Scouring had no effect on {name} {MaxMisses} times.");
            }

            if (_actions >= Settings.MaxActionsPerRun.Value) return true;
        }

        // 2. alch sweep over everything that is normal now
        maps = MapsByKey();
        var toAlch = keys
            .Where(k => maps.TryGetValue(k, out var m) && m.Rarity == ItemRarity.Normal)
            .Select(k => new SweepTarget(k, m => m.Rarity == ItemRarity.Rare))
            .ToList();

        if (toAlch.Count > 0)
        {
            if (ReserveReached(Orb.Alchemy))
            {
                _status = ReserveMessage(Orb.Alchemy);
                return false;
            }

            var alched = await Sweep(Orb.Alchemy, toAlch, "Alching");
            if (alched == null) return false;

            foreach (var (key, changed) in alched)
            {
                var state = State(key);
                if (changed)
                {
                    state.Misses = 0;
                    state.Rolls++;
                    state.ExaltDone = false;
                    _usedAlch++;
                    continue;
                }

                if (++state.Misses < MaxMisses) continue;
                var name = maps.TryGetValue(key, out var m) ? m.Name : "a map";
                return Fail($"The Orb of Alchemy had no effect on {name} {MaxMisses} times.");
            }

            if (_actions >= Settings.MaxActionsPerRun.Value) return true;
        }

        if (!chaosMode) return true;

        // 3. chaos sweep over the rare maps that still do not match (only the ones that were rare before this
        //    pass; a map that was just alched gets its highlight checked in the next pass first)
        maps = MapsByKey();
        var toChaos = new List<SweepTarget>();
        foreach (var key in keys)
        {
            if (!maps.TryGetValue(key, out var map) || map.Rarity != ItemRarity.Rare || map.Highlighted) continue;
            if (toAlch.Any(t => t.Key == key)) continue;
            var before = map.ModSignature;
            toChaos.Add(new SweepTarget(key, m => m.ModSignature != before || m.Rarity != ItemRarity.Rare));
        }

        if (toChaos.Count == 0) return true;

        if (ReserveReached(Orb.Chaos))
        {
            _status = ReserveMessage(Orb.Chaos);
            return false;
        }

        var chaosed = await Sweep(Orb.Chaos, toChaos, "Chaosing");
        if (chaosed == null) return false;

        foreach (var (key, changed) in chaosed)
        {
            var state = State(key);
            if (changed)
            {
                state.Misses = 0;
                state.Rolls++;
                state.ExaltDone = false;
                _usedChaos++;
                continue;
            }

            if (++state.Misses < MaxMisses) continue;
            var name = maps.TryGetValue(key, out var m) ? m.Name : "a map";
            return Fail($"The Chaos Orb had no effect on {name} {MaxMisses} times.");
        }

        return true;
    }

    /// <summary>One exalt sweep over the highlighted maps that still have open mods.</summary>
    private async SyncTask<bool> ExaltPass(List<MapInfo> toExalt)
    {
        var maps = MapsByKey();
        var targets = new List<SweepTarget>();
        foreach (var snapshot in toExalt)
        {
            if (!maps.TryGetValue(snapshot.Key, out var map) || !NeedsExalt(map)) continue;
            var modsBefore = map.ModSignature;
            targets.Add(new SweepTarget(map.Key, m => m.ModSignature != modsBefore || m.Rarity != ItemRarity.Rare));
        }

        if (targets.Count == 0) return true;

        if (ReserveReached(Orb.Exalted))
        {
            _status = ReserveMessage(Orb.Exalted);
            return false;
        }

        var results = await Sweep(Orb.Exalted, targets, "Exalting");
        if (results == null) return false;

        var after = MapsByKey();
        foreach (var (key, changed) in results)
        {
            var state = State(key);
            if (!changed)
            {
                // a rare map below 6 mods always has an open affix, so a miss is most likely a click that did
                // not register; give it another pass before treating the map as finished
                if (++state.Misses >= 2)
                {
                    state.ExaltDone = true;
                    state.Note = "exalt had no effect twice (no open affix?)";
                }

                continue;
            }

            state.Misses = 0;
            state.Exalts++;
            _usedExalt++;

            if (after.TryGetValue(key, out var map) && !map.Highlighted)
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
        }

        return true;
    }

    private async SyncTask<bool> Cleanup()
    {
        try
        {
            ReleaseModifierKeys();
            // during a HUD unload only let go of the keys; no Escape, tab switches or mouse moves in the game
            if (!_shutdown && GameFocused())
            {
                await ReleaseCursor();
                if (StashPanelVisible && _mapTabIndex >= 0 && CurrentTabIndex != _mapTabIndex)
                {
                    var keepStatus = _status;
                    var wasAborted = _aborted;
                    await SwitchToTab(_mapTabIndex);
                    _status = keepStatus;
                    _aborted = wasAborted;
                }

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
        };
        if (Settings.UseChaosOrbs.Value) lines.Add($"Chaos     used {_usedChaos,4}   seen {CountOrbs(Orb.Chaos),5}");
        lines.Add($"Alchemy   used {_usedAlch,4}   seen {CountOrbs(Orb.Alchemy),5}");
        lines.Add($"Scouring  used {_usedScour,4}   seen {CountOrbs(Orb.Scouring),5}");
        lines.Add($"Exalted   used {_usedExalt,4}   seen {CountOrbs(Orb.Exalted),5}");
        if (CurrencyTabMode)
        {
            var tabIndex = ResolveCurrencyTabIndex();
            lines.Add(tabIndex >= 0
                ? $"Currency tab: {Settings.CurrencyTab.Value}" + (_currencyTabCounted ? "" : " (not counted yet)")
                : "Currency tab: none selected!");
        }

        lines.Add(_status);

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
