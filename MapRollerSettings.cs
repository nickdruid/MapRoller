using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;

namespace MapRoller;

public class MapRollerSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(false);

    [Menu("Start / Stop hotkey", "Press once while the stash is open to roll every map in the visible tab. Press again to stop.")]
    public HotkeyNodeV2 RunHotkey { get; set; } = new(Keys.F6);

    [Menu("Map regex", "Pasted into the stash search box when a run starts. A map counts as good once the game highlights it.")]
    public TextNode MapRegex { get; set; } = new("");

    [Menu("Paste regex into stash search", "Turn off if you prefer to type the search yourself before pressing the hotkey.")]
    public ToggleNode ApplyRegexToSearch { get; set; } = new(true);

    [Menu("Scour via Alt-swap", "Hold Alt while clicking with an Orb of Alchemy to use an Orb of Scouring instead (in-game feature). Turn off to pick up Orbs of Scouring as their own stack.")]
    public ToggleNode UseAltScourSwap { get; set; } = new(true);

    [Menu("Exalt highlighted maps", "Once every map is highlighted, add mods with Exalted Orbs until the target mod count is reached. A map that stops matching after an exalt is scoured and rerolled.")]
    public ToggleNode UseExalts { get; set; } = new(true);

    [Menu("Target explicit mod count", "Exalt until the map has this many explicit mods (a rare map holds at most 6).")]
    public RangeNode<int> TargetModCount { get; set; } = new(6, 1, 6);

    [Menu("Max alchemy rolls per map", "Give up on a map after this many Orbs of Alchemy.")]
    public RangeNode<int> MaxRollsPerMap { get; set; } = new(30, 1, 500);

    [Menu("Max rerolls after an exalt brick", "How often a map may be scoured and rerolled because an exalt made it stop matching the regex.")]
    public RangeNode<int> MaxBricksPerMap { get; set; } = new(3, 0, 50);

    [Menu("Use currency from player inventory", "Also count and click Alchemy / Scouring / Exalted stacks in your inventory, not only the visible stash tab.")]
    public ToggleNode UseInventoryCurrency { get; set; } = new(true);

    [Menu("Keep at least N Orbs of Alchemy", "The run stops before the remaining amount drops below this.")]
    public RangeNode<int> AlchemyReserve { get; set; } = new(0, 0, 5000);

    [Menu("Keep at least N Orbs of Scouring")]
    public RangeNode<int> ScouringReserve { get; set; } = new(0, 0, 5000);

    [Menu("Keep at least N Exalted Orbs")]
    public RangeNode<int> ExaltReserve { get; set; } = new(0, 0, 5000);

    [Menu("Max orbs used per run", "Safety cap on the total number of orbs one run may consume.")]
    public RangeNode<int> MaxActionsPerRun { get; set; } = new(1000, 1, 20000);

    [Menu("Click delay (ms)", "Pause between mouse and keyboard actions.")]
    public RangeNode<int> ClickDelay { get; set; } = new(80, 20, 1000);

    [Menu("Item change timeout (ms)", "How long to wait for a map to change after an orb was used before treating it as failed.")]
    public RangeNode<int> ChangeTimeout { get; set; } = new(1500, 200, 10000);

    [Menu("Highlight settle delay (ms)", "Pause after a map changed before trusting the search highlight.")]
    public RangeNode<int> HighlightSettleDelay { get; set; } = new(120, 0, 2000);

    [Menu("Delay after pasting the regex (ms)")]
    public RangeNode<int> RegexDelay { get; set; } = new(300, 50, 3000);

    [Menu("Show status overlay")]
    public ToggleNode ShowOverlay { get; set; } = new(true);

    [Menu("Draw frames around maps", "Green = done, yellow = waiting for exalts, orange = needs rolling, red = given up, grey = cannot be rolled.")]
    public ToggleNode DrawMapFrames { get; set; } = new(true);
}
