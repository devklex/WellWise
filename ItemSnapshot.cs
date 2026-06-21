namespace WellWise;

public sealed class ItemSnapshot
{
    public string Source { get; init; } = string.Empty;
    public string BaseName { get; init; } = string.Empty;
    public string UniqueName { get; init; } = string.Empty;
    public string ClassName { get; init; } = string.Empty;
    public int ItemLevel { get; init; }
    public string DisplayName => string.IsNullOrWhiteSpace(UniqueName) ? BaseName : UniqueName;
}