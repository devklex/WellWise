using ExileCore2.Shared.Interfaces;
using ExileCore2.Shared.Nodes;

namespace WellWise;

public sealed class WellWiseSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new(true);
    public ToggleNode ShowOptionText { get; set; } = new(false);
    public ToggleNode DebugMode { get; set; } = new(false);
    public ButtonNode ReloadData { get; set; } = new();
    public TextNode LastStatus { get; set; } = new("");
    public TextNode LastContext { get; set; } = new("");
    public TextNode LastOptions { get; set; } = new("");
}