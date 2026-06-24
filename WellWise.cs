using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExileCore2;
using ExileCore2.PoEMemory;
using ExileCore2.PoEMemory.Elements;
using ExileCore2.PoEMemory.MemoryObjects;
using ImGuiNET;
using RectangleF = ExileCore2.Shared.RectangleF;

namespace WellWise;

public sealed class WellWise : BaseSettingsPlugin<WellWiseSettings>
{
    private sealed record TextSegment(string Text, Color Color);
    private sealed record PanelLine(List<TextSegment> Segments);
    private sealed record ElementSearchHit(string RootName, string Path, Element Element, Element? Parent);
    private sealed record WellOption(int Index, string Path, Element Element, Element TextElement, string Text, string RawText, RectangleF Rect);
    private sealed record WellDrawInfo(WellOption Option, WellOfSoulsTierResult TierResult);
    private sealed record WellState(List<WellOption> Options, ItemSnapshot? ItemContext, bool AwaitingRevealPrompt = false, bool WindowVisible = false);
    private sealed record AreaInfo(string InstanceName, string AreaName, string AreaId, string RawName);

    private static readonly Regex WellRollValueRegex = new(@"(?<prefix>[+-]?)\s*(?<value>\d+(?:\.\d+)?)(?<suffix>%?)", RegexOptions.Compiled);

    private readonly WellOfSoulsTierResolver _resolver = new();
    private readonly Dictionary<string, DateTime> _nextDebugFailureLogAt = new(StringComparer.OrdinalIgnoreCase);
    private List<WellOption> _options = [];
    private List<WellDrawInfo> _drawInfos = [];
    private ItemSnapshot? _itemContext;
    private Element? _cachedWellRoot;
    private DateTime _nextScanAt = DateTime.MinValue;
    private DateTime _nextBroadScanAt = DateTime.MinValue;
    private DateTime _partialCooldownUntil = DateTime.MinValue;
    private int _consecutivePartialReads;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan BroadScanInterval = TimeSpan.FromMilliseconds(4000);
    private static readonly TimeSpan IdleScanInterval = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan IdleBroadScanInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PartialRetryInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PartialBroadRetryInterval = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PartialCooldownInterval = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new() { WriteIndented = true };
    private const int MaxConsecutivePartialReads = 6;
    private static readonly HashSet<string> WellAreaRawNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Abyss_Hub",
        "Abyss_Pinnacle"
    };
    private static readonly string[] WellKnownItemClasses =
    [
        "Body Armour", "Helmet", "Gloves", "Boots", "Shield", "Buckler", "Quiver", "Focus", "Belt", "Ring", "Amulet",
        "Two Hand Mace", "One Hand Mace", "Two Hand Axe", "One Hand Axe", "Two Hand Sword", "One Hand Sword",
        "Quarterstaff", "Warstaff", "Crossbow", "Bow", "Spear", "Flail", "Sceptre", "Wand", "Staff", "Dagger", "Claw", "Talisman", "Jewel"
    ];

    public override bool Initialise()
    {
        LoadData();
        Settings.ReloadData.OnPressed += LoadData;
        Settings.ExportDiagnosticReport.OnPressed += ExportDiagnosticReport;
        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _options = [];
        _drawInfos = [];
        _itemContext = null;
        _cachedWellRoot = null;
        _consecutivePartialReads = 0;
        _nextScanAt = DateTime.MinValue;
        _nextBroadScanAt = DateTime.MinValue;
        _partialCooldownUntil = DateTime.MinValue;
    }

    public override void Render()
    {
        if (!Settings.Enable.Value)
            return;

        var now = DateTime.UtcNow;
        var areaInfo = GetCurrentAreaInfo();
        bool inWellArea = IsWellOfSoulsArea(areaInfo);
        if (!inWellArea)
        {
            ClearWellState();
            Settings.LastStatus.Value = _resolver.LoadStatus;
            Settings.LastContext.Value = "No Well item context";
            Settings.LastOptions.Value = "Outside The Well of Souls area";
            _nextScanAt = now + IdleScanInterval;
            _nextBroadScanAt = now + IdleBroadScanInterval;
            DrawAreaDebugOverlay(areaInfo, false);
            return;
        }

        if (now >= _nextScanAt)
        {
            bool allowBroadScan = now >= _nextBroadScanAt;
            var state = ReadWellState(allowBroadScan);
            if (!state.WindowVisible)
            {
                ClearWellState();
                Settings.LastStatus.Value = _resolver.LoadStatus;
                Settings.LastContext.Value = "No Well item context";
                Settings.LastOptions.Value = "Well of Souls not visible";
                _nextScanAt = now + IdleScanInterval;
                if (allowBroadScan)
                    _nextBroadScanAt = now + IdleBroadScanInterval;
                return;
            }

            if (HandlePartialOptionRead(state, now))
            {
                DrawWellOptions(_drawInfos);
                return;
            }

            if (state.AwaitingRevealPrompt)
            {
                _options = [];
                _itemContext = state.ItemContext;
            }
            else
            {
                if (state.Options.Count > 0)
                    _options = state.Options;
                else if (state.ItemContext == null || !SameWellItemContext(state.ItemContext, _itemContext))
                    _options = [];

                if (state.ItemContext != null || _options.Count == 0)
                    _itemContext = state.ItemContext;
            }

            _drawInfos = BuildDrawInfos(_options, _itemContext);
            Settings.LastStatus.Value = _resolver.LoadStatus;
            Settings.LastContext.Value = FormatContext(_itemContext);
            Settings.LastOptions.Value = _options.Count == 0 ? "No Well options found" : $"{_options.Count} Well options";
            _consecutivePartialReads = 0;
            _partialCooldownUntil = DateTime.MinValue;
            _nextScanAt = now + ScanInterval;
            if (allowBroadScan)
                _nextBroadScanAt = now + BroadScanInterval;
        }

        DrawWellOptions(_drawInfos);
        DrawAreaDebugOverlay(areaInfo, true);
    }

    private void ClearWellState()
    {
        _options = [];
        _drawInfos = [];
        _itemContext = null;
        _cachedWellRoot = null;
        _consecutivePartialReads = 0;
        _partialCooldownUntil = DateTime.MinValue;
    }

    private AreaInfo GetCurrentAreaInfo()
    {
        try
        {
            var area = GameController.Area?.CurrentArea;
            var worldArea = area?.Area;
            return new AreaInfo(
                area?.Name ?? string.Empty,
                ReadObjectStringProperty(worldArea, "Name"),
                ReadObjectStringProperty(worldArea, "Id"),
                ReadObjectStringProperty(worldArea, "RawName"));
        }
        catch
        {
            return new AreaInfo(string.Empty, string.Empty, string.Empty, string.Empty);
        }
    }

    private static bool IsWellOfSoulsArea(AreaInfo area)
    {
        return WellAreaRawNames.Contains(area.AreaId) ||
               WellAreaRawNames.Contains(area.RawName) ||
               string.Equals(area.InstanceName, "The Well of Souls", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(area.AreaName, "The Well of Souls", StringComparison.OrdinalIgnoreCase);
    }

    private void DrawAreaDebugOverlay(AreaInfo area, bool inWellArea)
    {
        if (!Settings.ShowAreaDebugOverlay.Value)
            return;

        string here = FirstNonEmpty(area.InstanceName, area.AreaName, area.AreaId, area.RawName, "unknown area");
        string id = FirstNonEmpty(area.AreaId, area.RawName, "unknown id");
        string line1 = inWellArea
            ? "WellWise: You're in The Well of Souls"
            : $"WellWise: You're not in The Well of Souls, you're here: {here}";
        string line2 = $"Area id: {id}";

        float lineHeight = ImGui.GetTextLineHeight();
        float width = Math.Clamp(Math.Max(ImGui.CalcTextSize(line1).X, ImGui.CalcTextSize(line2).X) + 18f, 260f, 900f);
        float height = lineHeight * 2f + 12f;
        var box = ClampToDisplay(new RectangleF(12f, 96f, width, height));
        var titleColor = inWellArea ? Color.LightGreen : Color.Orange;

        Graphics.DrawBox(box, Color.FromArgb(232, 4, 4, 4));
        Graphics.DrawFrame(box, Color.FromArgb(210, 195, 176, 95), 1);
        Graphics.DrawText(line1, new Vector2(box.X + 9f, box.Y + 6f), titleColor);
        Graphics.DrawText(line2, new Vector2(box.X + 9f, box.Y + 6f + lineHeight), Color.LightGray);
    }

    private static string ReadObjectStringProperty(object? obj, string propertyName)
    {
        if (obj == null)
            return string.Empty;

        try
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null || prop.GetIndexParameters().Length != 0)
                return string.Empty;

            return prop.GetValue(obj)?.ToString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private bool HandlePartialOptionRead(WellState state, DateTime now)
    {
        if (state.Options.Count is 0 or >= 3)
            return false;

        _consecutivePartialReads++;
        bool sameItem = SameWellItemContext(state.ItemContext, _itemContext);
        if (!sameItem || _options.Count < 3)
        {
            _options = [];
            _drawInfos = [];
            _itemContext = state.ItemContext;
        }

        _cachedWellRoot = null;
        Settings.LastStatus.Value = _resolver.LoadStatus;
        Settings.LastContext.Value = FormatContext(_itemContext);

        if (_consecutivePartialReads >= MaxConsecutivePartialReads)
        {
            _partialCooldownUntil = now + PartialCooldownInterval;
            _nextScanAt = _partialCooldownUntil;
            _nextBroadScanAt = _partialCooldownUntil;
            Settings.LastOptions.Value = $"Partial Well options stuck ({state.Options.Count}/3); cooling down {PartialCooldownInterval.TotalSeconds:0}s";
            return true;
        }

        _nextScanAt = now + PartialRetryInterval;
        if (_nextBroadScanAt <= now)
            _nextBroadScanAt = now + PartialBroadRetryInterval;

        Settings.LastOptions.Value = $"Partial Well options ({state.Options.Count}/3); retrying {_consecutivePartialReads}/{MaxConsecutivePartialReads}";
        return true;
    }

    private void LoadData()
    {
        _resolver.Load(DirectoryFullName);
        Settings.LastStatus.Value = _resolver.LoadStatus;
        _options = [];
        _drawInfos = [];
        _itemContext = null;
        _cachedWellRoot = null;
        _consecutivePartialReads = 0;
        _nextScanAt = DateTime.MinValue;
        _nextBroadScanAt = DateTime.MinValue;
        _partialCooldownUntil = DateTime.MinValue;
    }

    private void ExportDiagnosticReport()
    {
        try
        {
            var now = DateTime.UtcNow;
            var areaInfo = GetCurrentAreaInfo();
            var reportDirectory = Path.Combine(DirectoryFullName, "diagnostics");
            Directory.CreateDirectory(reportDirectory);
            string reportPath = Path.Combine(reportDirectory, $"wellwise-diagnostic-{now:yyyyMMdd-HHmmss}.txt");

            var report = new StringBuilder();
            report.AppendLine("WellWise diagnostic report");
            report.AppendLine($"Generated UTC: {now:O}");
            report.AppendLine($"Plugin directory: {DirectoryFullName}");
            report.AppendLine($"Assembly version: {GetType().Assembly.GetName().Version}");
            report.AppendLine();
            report.AppendLine("[Settings]");
            report.AppendLine($"Enable: {Settings.Enable.Value}");
            report.AppendLine($"ShowOptionText: {Settings.ShowOptionText.Value}");
            report.AppendLine($"ShowAreaDebugOverlay: {Settings.ShowAreaDebugOverlay.Value}");
            report.AppendLine($"DebugMode: {Settings.DebugMode.Value}");
            report.AppendLine();
            report.AppendLine("[Area]");
            report.AppendLine($"In Well area: {IsWellOfSoulsArea(areaInfo)}");
            report.AppendLine($"InstanceName: {areaInfo.InstanceName}");
            report.AppendLine($"AreaName: {areaInfo.AreaName}");
            report.AppendLine($"AreaId: {areaInfo.AreaId}");
            report.AppendLine($"RawName: {areaInfo.RawName}");
            report.AppendLine();
            AppendUiRootReport(report);
            report.AppendLine("[Scan schedule]");
            report.AppendLine($"Consecutive partial reads: {_consecutivePartialReads}");
            report.AppendLine($"Next scan: {FormatSchedule(_nextScanAt, now)}");
            report.AppendLine($"Next broad scan: {FormatSchedule(_nextBroadScanAt, now)}");
            report.AppendLine($"Partial cooldown until: {FormatSchedule(_partialCooldownUntil, now)}");
            report.AppendLine($"Cached root: {FormatElement(_cachedWellRoot)}");
            report.AppendLine();
            report.AppendLine("[Last status fields]");
            report.AppendLine($"LastStatus: {Settings.LastStatus.Value}");
            report.AppendLine($"LastContext: {Settings.LastContext.Value}");
            report.AppendLine($"LastOptions: {Settings.LastOptions.Value}");
            report.AppendLine();

            WellState freshState;
            try
            {
                freshState = ReadWellState(allowBroadSearch: true);
            }
            catch (Exception ex)
            {
                freshState = new WellState([], null);
                report.AppendLine("[Fresh broad Well read]");
                report.AppendLine($"Read failed: {ex}");
                report.AppendLine();
            }

            var freshItem = freshState.ItemContext ?? _itemContext;
            AppendWellStateReport(report, "Fresh broad Well read", freshState, freshItem, BuildDrawInfos(freshState.Options, freshItem));

            var cachedState = new WellState(_options, _itemContext, WindowVisible: _options.Count > 0);
            AppendWellStateReport(report, "Cached overlay state", cachedState, _itemContext, _drawInfos);

            AppendCandidateRootReport(report);
            AppendRuntimeProbeReport(report, freshState, freshItem);

            File.WriteAllText(reportPath, report.ToString(), Encoding.UTF8);
            Settings.LastOptions.Value = $"Diagnostic report saved: {reportPath}";
        }
        catch (Exception ex)
        {
            Settings.LastOptions.Value = $"Diagnostic report failed: {ex.Message}";
            LogError($"WellWise diagnostic report failed: {ex}");
        }
    }

    private void AppendRuntimeProbeReport(StringBuilder report, WellState state, ItemSnapshot? item)
    {
        report.AppendLine("[Runtime record probe]");
        if (state.Options.Count == 0)
        {
            report.AppendLine("Skipped: no current Well option text.");
            report.AppendLine();
            return;
        }

        try
        {
            var probe = _resolver.BuildRuntimeProbe(GameController, item, state.Options.Select(option => option.Text), item?.ItemLevel ?? 0);
            report.AppendLine(JsonSerializer.Serialize(probe, DiagnosticJsonOptions));
        }
        catch (Exception ex)
        {
            report.AppendLine($"Runtime probe failed: {ex}");
        }

        report.AppendLine();
    }

    private void AppendCandidateRootReport(StringBuilder report)
    {
        report.AppendLine("[Candidate Well roots]");
        try
        {
            var hits = FindWellElements(12, 80);
            var roots = FindCandidateRoots(hits).Take(12).ToList();
            report.AppendLine($"Text hits: {hits.Count}");
            foreach (var hit in hits.Take(24))
            {
                string text = TrimForReport(string.Join(" ", ReadStringishProperties(hit.Element).Values), 240);
                report.AppendLine($"Hit: {hit.Path} | {FormatElement(hit.Element)} | {text}");
            }

            report.AppendLine($"Candidate roots: {roots.Count}");
            for (int i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                var state = TryReadWellStateFromRoot(root);
                report.AppendLine($"Root {i + 1}: {FormatElement(root)}");
                if (state == null)
                {
                    report.AppendLine("  State: null");
                    continue;
                }

                report.AppendLine($"  State: visible={state.WindowVisible}, awaitingReveal={state.AwaitingRevealPrompt}, options={state.Options.Count}, context={FormatContext(state.ItemContext)}");
                foreach (var option in state.Options)
                    report.AppendLine($"  Option {option.Index}: {TrimForReport(option.Text, 180)} | path={option.Path} | rect={FormatRect(option.Rect)} | element={FormatElement(option.TextElement)}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Candidate root report failed: {ex}");
        }

        report.AppendLine();
    }

    private void AppendUiRootReport(StringBuilder report)
    {
        report.AppendLine("[UI roots]");
        foreach (var (name, root) in GetWellSearchRoots())
            report.AppendLine($"{name}: {FormatElement(root)}");

        report.AppendLine();
    }

    private static void AppendWellStateReport(StringBuilder report, string title, WellState state, ItemSnapshot? item, IReadOnlyList<WellDrawInfo> drawInfos)
    {
        report.AppendLine($"[{title}]");
        report.AppendLine($"WindowVisible: {state.WindowVisible}");
        report.AppendLine($"AwaitingRevealPrompt: {state.AwaitingRevealPrompt}");
        report.AppendLine($"Options: {state.Options.Count}");
        report.AppendLine($"Item: {FormatContext(item)}");
        report.AppendLine($"State item: {FormatContext(state.ItemContext)}");

        foreach (var option in state.Options)
        {
            report.AppendLine($"Option {option.Index}");
            report.AppendLine($"  Text: {option.Text}");
            report.AppendLine($"  RawText: {option.RawText}");
            report.AppendLine($"  Path: {option.Path}");
            report.AppendLine($"  Rect: {FormatRect(option.Rect)}");
            report.AppendLine($"  Element: {FormatElement(option.Element)}");
            report.AppendLine($"  TextElement: {FormatElement(option.TextElement)}");

            var drawInfo = drawInfos.FirstOrDefault(info =>
                info.Option.Index == option.Index &&
                info.Option.Text.Equals(option.Text, StringComparison.OrdinalIgnoreCase));
            if (drawInfo != null)
                AppendTierResultReport(report, drawInfo.TierResult);
        }

        report.AppendLine();
    }

    private static void AppendTierResultReport(StringBuilder report, WellOfSoulsTierResult result)
    {
        report.AppendLine($"  TierKnown: {result.Known}");
        report.AppendLine($"  AffixType: {result.AffixType}");
        report.AppendLine($"  Source: {result.Source}");
        report.AppendLine($"  RuleId: {result.RuleId}");
        report.AppendLine($"  Label: {result.Label}");
        report.AppendLine($"  CurrentValue: {result.CurrentValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? ""}");
        report.AppendLine($"  Summary: {result.Summary}");
        report.AppendLine($"  Detail: {result.Detail}");
        report.AppendLine($"  CurrentTier: {FormatTierForReport(result.CurrentTier)}");
        report.AppendLine($"  CurrentTierMatches: {string.Join(", ", result.CurrentTierMatches.Select(FormatTierForReport))}");
        report.AppendLine($"  BestTier: {FormatTierForReport(result.BestTier)}");
        report.AppendLine($"  AbsoluteBestTier: {FormatTierForReport(result.AbsoluteBestTier)}");
    }

    private static string FormatTierForReport(WellTierRange? tier)
        => tier == null ? "" : WellOfSoulsTierResolver.FormatTier(tier);

    private static string FormatSchedule(DateTime target, DateTime now)
    {
        if (target == DateTime.MinValue)
            return "not scheduled";

        if (target <= now)
            return "due";

        return $"{(target - now).TotalMilliseconds:0}ms";
    }

    private static string FormatElement(Element? element)
    {
        if (element == null)
            return "null";

        try
        {
            return $"0x{(long)element.Address:X} visible={SafeVisible(element)} rect={FormatRect(element.GetClientRectCache)}";
        }
        catch
        {
            return $"0x{(long)element.Address:X}";
        }
    }

    private static string FormatRect(RectangleF rect)
        => $"x={rect.X:0.#}, y={rect.Y:0.#}, w={rect.Width:0.#}, h={rect.Height:0.#}";

    private static string TrimForReport(string value, int maxLength)
    {
        string normalized = NormalizeWhitespace(value);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength] + "...";
    }

    private List<WellDrawInfo> BuildDrawInfos(IReadOnlyList<WellOption> options, ItemSnapshot? item)
    {
        if (options.Count == 0)
            return [];

        return options
            .Select(option => new WellDrawInfo(option, _resolver.Resolve(item, option.Text)))
            .ToList();
    }

    private void DrawWellOptions(IReadOnlyList<WellDrawInfo> drawInfos)
    {
        if (drawInfos.Count == 0)
            return;

        foreach (var drawInfo in drawInfos)
        {
            var option = drawInfo.Option;
            if (!IsDrawableRect(option.Rect))
                continue;

            var lines = BuildTierBadgeLines(drawInfo.TierResult);
            if (Settings.ShowOptionText.Value)
                lines.Insert(0, Line(Segment(option.Text, Color.White)));

            if (lines.Count == 0)
                continue;

            float lineHeight = ImGui.GetTextLineHeight();
            float width = Math.Clamp(lines.Max(MeasureLineWidth) + 18f, 190f, 720f);
            float height = lines.Count * lineHeight + 10f;
            var box = PickTierBadgeRect(option.Rect, width, height);

            Graphics.DrawBox(box, Color.FromArgb(232, 4, 4, 4));
            Graphics.DrawFrame(box, Color.FromArgb(210, 195, 176, 95), 1);

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                float x = box.X + (box.Width - MeasureLineWidth(line)) * 0.5f;
                DrawPanelLine(line, new Vector2(x, box.Y + 5f + i * lineHeight));
            }
        }
    }

    private static List<PanelLine> BuildTierBadgeLines(WellOfSoulsTierResult result)
    {
        if (!result.Known)
            return [Line(Segment("Tier unknown", MutedTextColor()))];

        var lines = new List<PanelLine>();
        var currentTiers = GetCurrentTierMatches(result);
        if (currentTiers.Count > 0)
        {
            var segments = BuildCurrentTierSegments(result, currentTiers);
            segments.Add(Segment(FormatCurrentRollWithRange(result, currentTiers), Color.White));
            lines.Add(Line(segments.ToArray()));
        }
        else
        {
            lines.Add(Line(Segment("Known stat", MutedTextColor())));
        }

        if (result.BestTier != null)
        {
            bool topTierLocked = result.AbsoluteBestTier != null && !SameTierRange(result.BestTier, result.AbsoluteBestTier);
            bool showItemMax = result.CurrentTier == null || !SameTierRange(result.CurrentTier, result.BestTier) || topTierLocked;
            if (showItemMax)
            {
                var maxLine = Line(
                    Segment("Item max ", MutedTextColor()),
                    Segment($"T{result.BestTier.Tier} ", TierColor(result.BestTier.Tier)),
                    Segment(FormatTierRangeOnly(result.BestTier), Color.White));

                if (topTierLocked && result.AbsoluteBestTier != null)
                {
                    maxLine.Segments.Add(Segment("  |  ", MutedTextColor()));
                    maxLine.Segments.Add(Segment($"T{result.AbsoluteBestTier.Tier} ", TierColor(result.AbsoluteBestTier.Tier)));
                    maxLine.Segments.Add(Segment($"needs ilvl {result.AbsoluteBestTier.ItemLevel} ", MutedTextColor()));
                    maxLine.Segments.Add(Segment(FormatTierRangeOnly(result.AbsoluteBestTier), Color.White));
                }

                lines.Add(maxLine);
            }
        }

        return lines;
    }

    private static string FormatCurrentRollWithRange(WellOfSoulsTierResult result, WellTierRange tier)
        => FormatCurrentRollWithRange(result, [tier]);

    private static string FormatCurrentRollWithRange(WellOfSoulsTierResult result, IReadOnlyList<WellTierRange> tiers)
    {
        if (tiers.Count == 0)
            return result.CurrentValue == null ? string.Empty : FormatRoll(result.CurrentValue.Value);

        string rollText = FormatCurrentRollOnly(result, tiers[0]);
        string rangeSeparator = tiers.Any(tier => GetTierValueRanges(tier).Count > 1) ? " | " : " / ";
        string rangeText = string.Join(rangeSeparator, tiers.Select(tier => FormatCurrentRangeOnly(result, tier)));

        return string.IsNullOrWhiteSpace(rollText)
            ? rangeText
            : $"{rollText} ({rangeText})";
    }

    private static string FormatCurrentRollOnly(WellOfSoulsTierResult result, WellTierRange tier)
    {
        var ranges = GetTierValueRanges(tier);
        var rolls = ExtractRollComponents(result.OptionText).Take(ranges.Count).ToList();
        if (rolls.Count == 0 && result.CurrentValue != null)
            rolls.Add((result.CurrentValue.Value, ranges[0].Prefix, ranges[0].Suffix));

        if (rolls.Count == 0)
            return string.Empty;

        int count = Math.Min(rolls.Count, ranges.Count);
        string separator = result.Label.Contains(") to (", StringComparison.OrdinalIgnoreCase) ? " to " : " / ";
        var rollParts = new List<string>();

        for (int i = 0; i < count; i++)
        {
            var roll = rolls[i];
            var range = ranges[i];
            rollParts.Add($"{roll.Prefix}{FormatRoll(roll.Value)}{FirstNonEmpty(roll.Suffix, range.Suffix)}");
        }

        return string.Join(separator, rollParts);
    }

    private static string FormatCurrentRangeOnly(WellOfSoulsTierResult result, WellTierRange tier)
    {
        var ranges = GetTierValueRanges(tier);
        var rolls = ExtractRollComponents(result.OptionText).Take(ranges.Count).ToList();
        string separator = result.Label.Contains(") to (", StringComparison.OrdinalIgnoreCase) ? " to " : " / ";
        return string.Join(separator, ranges.Select((range, index) =>
        {
            var roll = index < rolls.Count ? rolls[index] : ((double Value, string Prefix, string Suffix)?)null;
            return FormatRollRangeForCurrentBadge(range, roll);
        }));
    }

    private static string FormatRollRangeForCurrentBadge(WellTierValueRange range, (double Value, string Prefix, string Suffix)? roll)
    {
        string prefix = roll?.Prefix == "-" || range.Prefix == "-" ? "-" : "";
        string suffix = FirstNonEmpty(roll?.Suffix, range.Suffix);
        return $"{prefix}{FormatRoll(range.Min)}-{FormatRoll(range.Max)}{suffix}";
    }

    private static IReadOnlyList<WellTierRange> GetCurrentTierMatches(WellOfSoulsTierResult result)
    {
        if (result.CurrentTierMatches.Count > 0)
            return result.CurrentTierMatches;

        return result.CurrentTier == null ? [] : [result.CurrentTier];
    }

    private static List<TextSegment> BuildCurrentTierSegments(WellOfSoulsTierResult result, IReadOnlyList<WellTierRange> tiers)
    {
        var segments = new List<TextSegment>();
        string affixType = FormatAffixType(result.AffixType);
        if (!string.IsNullOrWhiteSpace(affixType))
            segments.Add(Segment($"{affixType}  ", MutedTextColor()));

        for (int i = 0; i < tiers.Count; i++)
        {
            if (i > 0)
                segments.Add(Segment("/", MutedTextColor()));

            segments.Add(Segment($"T{tiers[i].Tier}", TierColor(tiers[i].Tier)));
        }

        segments.Add(Segment("  ", MutedTextColor()));
        return segments;
    }

    private static string FormatAffixType(string affixType)
    {
        if (affixType.Equals("prefix", StringComparison.OrdinalIgnoreCase))
            return "Prefix";

        if (affixType.Equals("suffix", StringComparison.OrdinalIgnoreCase))
            return "Suffix";

        return string.Empty;
    }

    private static string FormatTierRangeOnly(WellTierRange tier)
    {
        string formatted = WellOfSoulsTierResolver.FormatTier(tier);
        int split = formatted.IndexOf(' ');
        return split >= 0 && split + 1 < formatted.Length ? formatted[(split + 1)..] : formatted;
    }

    private static IReadOnlyList<WellTierValueRange> GetTierValueRanges(WellTierRange tier)
        => tier.Values.Count > 0
            ? tier.Values
            : [new WellTierValueRange { Min = tier.Min, Max = tier.Max }];

    private static List<(double Value, string Prefix, string Suffix)> ExtractRollComponents(string text)
    {
        var result = new List<(double Value, string Prefix, string Suffix)>();
        foreach (Match match in WellRollValueRegex.Matches(text ?? string.Empty))
        {
            if (!double.TryParse(match.Groups["value"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value))
                continue;

            result.Add((value, match.Groups["prefix"].Value, match.Groups["suffix"].Value));
        }

        return result;
    }

    private static string FormatRoll(double value)
        => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";

    private static bool SameTierRange(WellTierRange left, WellTierRange right)
    {
        if (left.Tier != right.Tier)
            return false;

        var leftValues = left.Values.Count > 0 ? left.Values : [new WellTierValueRange { Min = left.Min, Max = left.Max }];
        var rightValues = right.Values.Count > 0 ? right.Values : [new WellTierValueRange { Min = right.Min, Max = right.Max }];
        if (leftValues.Count != rightValues.Count)
            return false;

        for (int i = 0; i < leftValues.Count; i++)
        {
            if (Math.Abs(leftValues[i].Min - rightValues[i].Min) > 0.0001 ||
                Math.Abs(leftValues[i].Max - rightValues[i].Max) > 0.0001)
                return false;
        }

        return true;
    }

    private WellState ReadWellState(bool allowBroadSearch)
    {
        try
        {
            WellState? partialVisibleState = null;
            Element? partialVisibleRoot = null;
            WellState? emptyVisibleState = null;
            Element? emptyVisibleRoot = null;

            var cachedRoot = _cachedWellRoot;
            var cachedState = TryReadWellStateFromRoot(cachedRoot);
            if (cachedState != null)
            {
                if (HasCompleteOptions(cachedState))
                    return AttachAvailableWellContext(cachedState);

                StoreFallback(cachedState, cachedRoot);
            }

            _cachedWellRoot = null;

            var directState = TryReadLikelyWellState(out var directRoot);
            if (directState != null)
            {
                if (HasCompleteOptions(directState))
                {
                    _cachedWellRoot = directRoot;
                    return AttachAvailableWellContext(directState);
                }

                StoreFallback(directState, directRoot);
            }

            if (!allowBroadSearch)
                return ReturnBestFallback();

            var hits = FindWellElements(12, 80);
            var candidateRoots = FindCandidateRoots(hits).Take(12).ToList();

            foreach (var root in candidateRoots)
            {
                var state = TryReadWellStateFromRoot(root);
                if (state == null)
                    continue;

                if (HasCompleteOptions(state))
                {
                    _cachedWellRoot = root;
                    return AttachAvailableWellContext(state);
                }

                StoreFallback(state, root);
            }

            return ReturnBestFallback();

            void StoreFallback(WellState state, Element? root)
            {
                if (HasPartialOptions(state))
                {
                    partialVisibleState ??= state;
                    partialVisibleRoot ??= root;
                    return;
                }

                emptyVisibleState ??= state;
                emptyVisibleRoot ??= root;
            }

            WellState ReturnBestFallback()
            {
                if (partialVisibleState != null)
                {
                    _cachedWellRoot = partialVisibleRoot;
                    return AttachAvailableWellContext(partialVisibleState);
                }

                if (emptyVisibleState != null)
                {
                    _cachedWellRoot = emptyVisibleRoot;
                    return AttachAvailableWellContext(emptyVisibleState);
                }

                return new WellState([], null);
            }
        }
        catch (Exception ex)
        {
            LogDebugFailureLimited("read Well of Souls state", ex);
        }

        return new WellState([], null);
    }

    private static bool HasCompleteOptions(WellState state)
        => state.Options.Count >= 3;

    private static bool HasPartialOptions(WellState state)
        => state.Options.Count is > 0 and < 3;

    private WellState AttachAvailableWellContext(WellState state)
    {
        if (state.ItemContext != null || state.Options.Count == 0)
            return state;

        var context = BuildWellItemContext();
        return context == null ? state : new WellState(state.Options, context, state.AwaitingRevealPrompt, state.WindowVisible);
    }

    private WellState? TryReadLikelyWellState(out Element? matchedRoot)
    {
        matchedRoot = null;
        WellState? partialVisibleState = null;
        Element? partialVisibleRoot = null;
        WellState? emptyVisibleState = null;
        Element? emptyVisibleRoot = null;

        foreach (var root in GetLikelyWellRoots())
        {
            var state = TryReadWellStateFromRoot(root);
            if (state == null)
                continue;

            if (HasCompleteOptions(state))
            {
                matchedRoot = root;
                return state;
            }

            if (HasPartialOptions(state))
            {
                partialVisibleState ??= state;
                partialVisibleRoot ??= root;
                continue;
            }

            emptyVisibleState ??= state;
            emptyVisibleRoot ??= root;
        }

        if (partialVisibleState != null)
        {
            matchedRoot = partialVisibleRoot;
            return partialVisibleState;
        }

        if (emptyVisibleState != null)
            matchedRoot = emptyVisibleRoot;

        return emptyVisibleState;
    }

    private IEnumerable<Element> GetLikelyWellRoots()
    {
        var result = new List<Element>();
        var seen = new HashSet<long>();
        var ingameUi = GameController.Game.IngameState.IngameUi;

        AddRoot(ingameUi.OpenLeftPanel);
        AddRoot(ingameUi.OpenRightPanel);
        AddRoot(ingameUi.ControllerGeneralMenu);

        int[][] controllerPaths =
        [
            [17, 2, 1, 14],
            [17, 2, 1, 14, 0],
            [17, 2, 1, 14, 0, 3]
        ];

        foreach (var path in controllerPaths)
            AddRoot(FollowChildChain(ingameUi.ControllerGeneralMenu, path));

        return result;

        void AddRoot(Element? root)
        {
            if (root == null || root.Address == 0 || !SafeVisible(root) || !seen.Add((long)root.Address))
                return;

            result.Add(root);
        }
    }

    private static WellState? TryReadWellStateFromRoot(Element? root)
    {
        if (root == null ||
            !IsPlausibleWellWindowCandidate(root) ||
            !ElementSubtreeContainsText(root, ["The Well of Souls"], 8, 240))
            return null;

        var context = BuildWellItemContext(root);
        var options = ReadOptionsFromFixedPaths(root);
        if (options.Count < 3)
            options = MergeOptions(options, SearchOptions(root));

        bool awaitingRevealPrompt = options.Count == 0 &&
            ElementSubtreeContainsText(root, ["Place an item with an Unrevealed Desecrated Modifier"], 8, 240);

        return new WellState(options, context, awaitingRevealPrompt, true);
    }

    private static ItemSnapshot? BuildWellItemContext(Element root)
        => BuildWellItemContext(CollectVisibleTextBlocks(root, 12, 420));

    
    private ItemSnapshot? BuildWellItemContext()
    {
        try
        {
            var roots = FindCandidateRoots(FindWellElements(12, 80)).Take(8);
            foreach (var root in roots)
            {
                var context = BuildWellItemContext(root);
                if (context != null)
                    return context;
            }
        }
        catch { }

        return null;
    }

    private static ItemSnapshot? BuildWellItemContext(IReadOnlyList<string> textBlocks)
    {
        if (textBlocks.Count == 0)
            return null;

        var blocks = textBlocks
            .Where(block => !string.IsNullOrWhiteSpace(block))
            .Select(block => block.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var lines = blocks
            .SelectMany(SplitVisibleTextLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        string className = "";
        string baseName = "";
        string uniqueName = "";
        int itemLevel = 0;

        foreach (string line in lines)
        {
            if (string.IsNullOrWhiteSpace(className))
            {
                className = TryParseWellItemClassAndLevel(line, out int classLineLevel);
                if (classLineLevel > 0)
                    itemLevel = Math.Max(itemLevel, classLineLevel);
            }

            if (itemLevel <= 0)
            {
                int lineLevel = TryParseItemLevel(line);
                if (lineLevel > 0)
                    itemLevel = lineLevel;
            }
        }

        foreach (string block in blocks)
        {
            if (ContainsAnyText(block, ["The Well of Souls", "Options", "Confirm", "Reveal", "Desecrated Modifier", "Take this item"]))
                continue;

            var parts = SplitVisibleTextLines(block)
                .Where(IsLikelyWellItemTitlePart)
                .ToList();
            if (parts.Count < 2)
                continue;

            uniqueName = parts[0];
            baseName = parts[1];
            break;
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            int classIndex = lines.FindIndex(line => !string.IsNullOrWhiteSpace(TryParseWellItemClassAndLevel(line, out _)));
            if (classIndex >= 2 &&
                IsLikelyWellItemTitlePart(lines[classIndex - 2]) &&
                IsLikelyWellItemTitlePart(lines[classIndex - 1]))
            {
                uniqueName = lines[classIndex - 2];
                baseName = lines[classIndex - 1];
            }
        }

        if (string.IsNullOrWhiteSpace(className) && string.IsNullOrWhiteSpace(baseName) && itemLevel <= 0)
            return null;

        if (string.IsNullOrWhiteSpace(className) && !string.IsNullOrWhiteSpace(baseName))
            className = InferItemClassFromBaseName(baseName);

        return new ItemSnapshot
        {
            Source = "well-of-souls-window",
            BaseName = baseName,
            UniqueName = uniqueName,
            ClassName = className,
            ItemLevel = itemLevel
        };
    }

    private static bool SameWellItemContext(ItemSnapshot? left, ItemSnapshot? right)
    {
        if (left == null || right == null)
            return false;

        if (!string.IsNullOrWhiteSpace(left.BaseName) &&
            left.BaseName.Equals(right.BaseName, StringComparison.OrdinalIgnoreCase))
            return true;

        return !string.IsNullOrWhiteSpace(left.UniqueName) &&
               left.UniqueName.Equals(right.UniqueName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<WellOption> ReadOptionsFromFixedPaths(Element root)
    {
        if (!ElementSubtreeContainsText(root, ["Confirm"], 8, 240))
            return [];

        int[][] optionPaths =
        [
            [4, 0, 0, 0],
            [4, 0, 1, 0],
            [4, 0, 2, 0]
        ];

        var options = new List<WellOption>();
        for (int index = 0; index < optionPaths.Length; index++)
        {
            var path = optionPaths[index];
            var container = FollowChildChain(root, path);
            var textElement = FindFirstVisibleTextElementBfs(container);
            if (textElement == null)
                continue;

            var textValues = ReadStringishProperties(textElement);
            string text = textValues.TryGetValue("TextNoTags", out var noTags) ? noTags : string.Join(" ", textValues.Values);
            string rawText = textValues.TryGetValue("Text", out var raw) ? raw : text;
            if (!IsWellOptionText(text))
                continue;

            options.Add(new WellOption(
                index + 1,
                string.Join(",", path),
                container ?? textElement,
                textElement,
                NormalizeWhitespace(text),
                NormalizeWhitespace(rawText),
                textElement.GetClientRectCache));
        }

        return options
            .OrderBy(option => option.Rect.Y)
            .ThenBy(option => option.Rect.X)
            .Select((option, index) => option with { Index = index + 1 })
            .ToList();
    }

    private static List<WellOption> SearchOptions(Element root)
    {
        if (!ElementSubtreeContainsText(root, ["Confirm"], 8, 240))
            return [];

        RectangleF rootRect;
        try { rootRect = root.GetClientRectCache; }
        catch { rootRect = default; }

        var options = new List<WellOption>();
        var seen = new HashSet<long>();
        Search(root, "root", 0);

        return options
            .Where(option => IsLikelyChoiceCandidate(rootRect, option))
            .OrderBy(option => option.Rect.Y)
            .ThenBy(option => option.Rect.X)
            .Take(3)
            .Select((option, index) => option with { Index = index + 1 })
            .ToList();

        void Search(Element element, string path, int depth)
        {
            if (depth > 10 || options.Count >= 80)
                return;

            if (SafeVisible(element) && seen.Add((long)element.Address))
            {
                var textValues = ReadStringishProperties(element);
                string text = textValues.TryGetValue("TextNoTags", out var noTags) ? noTags : string.Join(" ", textValues.Values);
                if (IsWellOptionText(text))
                {
                    string rawText = textValues.TryGetValue("Text", out var raw) ? raw : text;
                    options.Add(new WellOption(
                        options.Count + 1,
                        path,
                        element,
                        element,
                        NormalizeWhitespace(text),
                        NormalizeWhitespace(rawText),
                        element.GetClientRectCache));
                }
            }

            try
            {
                int childIndex = 0;
                foreach (var child in element.Children)
                {
                    Search(child, $"{path}.children[{childIndex}]", depth + 1);
                    childIndex++;
                }
            }
            catch { }
        }
    }

    private static List<WellOption> MergeOptions(IReadOnlyList<WellOption> primary, IReadOnlyList<WellOption> secondary)
    {
        var merged = new List<WellOption>();
        var seenAddresses = new HashSet<long>();
        var seenTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddRange(primary);
        AddRange(secondary);

        return merged
            .OrderBy(option => option.Rect.Y)
            .ThenBy(option => option.Rect.X)
            .Take(3)
            .Select((option, index) => option with { Index = index + 1 })
            .ToList();

        void AddRange(IEnumerable<WellOption> options)
        {
            foreach (var option in options)
            {
                long textAddress = (long)option.TextElement.Address;
                string textKey = NormalizeWhitespace(option.Text);
                if (textAddress != 0 && !seenAddresses.Add(textAddress))
                    continue;

                if (!seenTexts.Add(textKey))
                    continue;

                merged.Add(option);
            }
        }
    }

    private static bool IsLikelyChoiceCandidate(RectangleF rootRect, WellOption option)
    {
        if (!IsDrawableRect(option.Rect))
            return false;

        if (option.Path.Contains(".children[4].children[0].children[", StringComparison.Ordinal))
            return true;

        if (!IsDrawableRect(rootRect))
            return false;

        float choiceAreaTop = rootRect.Y + rootRect.Height * 0.50f;
        float choiceAreaRight = rootRect.X + rootRect.Width * 0.78f;
        return option.Rect.Y >= choiceAreaTop && option.Rect.X <= choiceAreaRight;
    }

    private List<ElementSearchHit> FindWellElements(int maxDepth, int maxHits)
    {
        var hits = new List<ElementSearchHit>();
        var seenRoots = new HashSet<long>();

        foreach (var (name, root) in GetWellSearchRoots())
        {
            if (root == null || root.Address == 0 || !seenRoots.Add((long)root.Address))
                continue;

            FindWellElements(root, null, name, 0, maxDepth, maxHits, hits);
            if (hits.Count >= maxHits)
                break;
        }

        return hits;
    }

    private IEnumerable<(string Name, Element? Element)> GetWellSearchRoots()
    {
        var ingameUi = GameController.Game.IngameState.IngameUi;
        yield return ("openLeftPanel", ingameUi.OpenLeftPanel);
        yield return ("openRightPanel", ingameUi.OpenRightPanel);
        yield return ("controllerMenu", ingameUi.ControllerGeneralMenu);
        yield return ("ingameUi", ingameUi);
        yield return ("ingameUi.Parent", TryGetElementProperty(ingameUi, "Parent"));
    }

    private static void FindWellElements(Element element, Element? parent, string path, int depth, int maxDepth, int maxHits, List<ElementSearchHit> hits)
    {
        if (depth > maxDepth || hits.Count >= maxHits)
            return;

        string text = string.Join(" ", ReadStringishProperties(element).Values);
        if (ContainsAnyText(text, [
                "The Well of Souls",
                "Options",
                "Desecrated Modifier",
                "Reveal",
                "Confirm",
                "Presence",
                "Critical Hit Chance",
                "Mana Regeneration",
                "Life Regeneration",
                "Life Regen",
                "Energy Shield",
                "Recharge Rate",
                "Strength",
                "Dexterity",
                "Intelligence",
                "Spirit",
                "Resistance",
                "Threshold",
                "Deflection"
            ]))
            hits.Add(new ElementSearchHit(path.Split('.')[0], path, element, parent));

        if (depth == maxDepth)
            return;

        try
        {
            int index = 0;
            foreach (var child in element.Children)
            {
                FindWellElements(child, element, $"{path}.children[{index}]", depth + 1, maxDepth, maxHits, hits);
                if (hits.Count >= maxHits)
                    break;
                index++;
            }
        }
        catch { }
    }

    private static List<Element> FindCandidateRoots(IEnumerable<ElementSearchHit> hits)
    {
        var result = new List<Element>();
        var seen = new HashSet<long>();

        foreach (var hit in hits)
        {
            bool titleHit = ContainsAnyText(string.Join(" ", ReadStringishProperties(hit.Element).Values), ["The Well of Souls"]);
            bool optionHit = ContainsAnyText(string.Join(" ", ReadStringishProperties(hit.Element).Values), ["Options", "Confirm", "Reveal"]);
            if (!titleHit && !optionHit)
                continue;

            AddCandidate(hit.Element);
            AddCandidate(hit.Parent);

            var current = hit.Parent ?? hit.Element;
            for (int depth = 0; depth < 8; depth++)
            {
                current = TryGetElementProperty(current, "Parent");
                if (current == null)
                    break;

                AddCandidate(current);
            }
        }

        return result
            .Where(element => IsPlausibleWellWindowCandidate(element) &&
                              ElementSubtreeContainsText(element, ["The Well of Souls"], 8, 240))
            .OrderByDescending(element =>
            {
                try { return Area(element.GetClientRectCache); }
                catch { return 0f; }
            })
            .ToList();

        void AddCandidate(Element? element)
        {
            if (element == null || element.Address == 0)
                return;

            if (seen.Add((long)element.Address))
                result.Add(element);
        }
    }

    private static List<string> CollectVisibleTextBlocks(Element root, int maxDepth, int maxNodes)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int scanned = 0;
        Collect(root, 0);
        return result;

        void Collect(Element element, int depth)
        {
            if (depth > maxDepth || scanned++ >= maxNodes)
                return;

            if (IsReadableTextElement(element))
            {
                foreach (string value in ReadStringishProperties(element).Values)
                {
                    string trimmed = value.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) && seen.Add(trimmed))
                        result.Add(trimmed);
                }
            }

            if (depth == maxDepth)
                return;

            try
            {
                foreach (var child in element.Children)
                    Collect(child, depth + 1);
            }
            catch { }
        }
    }

    private static bool IsReadableTextElement(Element element)
    {
        if (SafeVisible(element) || SafeFlag(element, "IsVisibleLocal") == true)
            return true;

        try { return IsDrawableRect(element.GetClientRectCache); }
        catch { return false; }
    }

    private static bool ElementSubtreeContainsText(Element root, IReadOnlyList<string> needles, int maxDepth, int maxNodes)
    {
        int scanned = 0;
        return Search(root, 0);

        bool Search(Element element, int depth)
        {
            if (depth > maxDepth || scanned++ >= maxNodes || !SafeVisible(element))
                return false;

            string text = string.Join(" ", ReadStringishProperties(element).Values);
            if (ContainsAnyText(text, needles))
                return true;

            try
            {
                foreach (var child in element.Children)
                    if (Search(child, depth + 1))
                        return true;
            }
            catch { }

            return false;
        }
    }

    private static bool IsPlausibleWellWindowCandidate(Element element)
    {
        if (!SafeVisible(element))
            return false;

        RectangleF rect;
        try { rect = element.GetClientRectCache; }
        catch { return false; }

        if (!IsDrawableRect(rect) || rect.Width < 250f || rect.Height < 120f)
            return false;

        var display = ImGui.GetIO().DisplaySize;
        if (display.X > 0 && rect.Width > display.X * 0.98f)
            return false;
        if (display.Y > 0 && rect.Height > display.Y * 0.98f)
            return false;

        return true;
    }

    private static Element? FindFirstVisibleTextElementBfs(Element? element)
    {
        if (element == null)
            return null;

        try
        {
            if (!SafeVisible(element))
                return null;

            var queue = new Queue<Element>();
            queue.Enqueue(element);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!SafeVisible(current))
                    continue;

                var strings = ReadStringishProperties(current);
                if (strings.Values.Any(value => !string.IsNullOrWhiteSpace(value)))
                    return current;

                try
                {
                    foreach (var child in current.Children)
                        queue.Enqueue(child);
                }
                catch { }
            }
        }
        catch { }

        return null;
    }

    private static Element? FollowChildChain(Element? start, IReadOnlyList<int> chain)
    {
        var current = start;
        foreach (int index in chain)
        {
            current = TryGetChildAt(current, index);
            if (current == null)
                return null;
        }

        return current;
    }

    private static Element? TryGetChildAt(Element? element, int index)
    {
        if (element == null || index < 0)
            return null;

        try
        {
            var children = element.Children;
            if (children == null || index >= children.Count)
                return null;

            return children[index];
        }
        catch
        {
            return null;
        }
    }

    private static Element? TryGetElementProperty(object? obj, string propertyName)
    {
        if (obj == null)
            return null;

        try
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop == null || prop.GetIndexParameters().Length != 0)
                return null;
            return prop.GetValue(obj) as Element;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, string> ReadStringishProperties(object obj)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] interesting = ["Text", "TextNoTags", "TooltipText", "Name", "Label", "DisplayName", "DebugName"];
        foreach (var name in interesting)
        {
            try
            {
                var prop = obj.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop == null || prop.GetIndexParameters().Length != 0)
                    continue;
                var value = prop.GetValue(obj);
                if (value == null)
                    continue;
                if (value is string s && !string.IsNullOrWhiteSpace(s))
                    result[name] = s.Length <= 200 ? s : s[..200];
                else if (value is not IEnumerable)
                {
                    string raw = value.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(raw) && raw.Length <= 200)
                        result[name] = raw;
                }
            }
            catch { }
        }

        return result;
    }

    private static IEnumerable<string> SplitVisibleTextLines(string value)
    {
        foreach (string part in value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            string normalized = NormalizeWhitespace(part);
            if (!string.IsNullOrWhiteSpace(normalized))
                yield return normalized;
        }
    }

    private static bool IsLikelyWellItemTitlePart(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line.Length < 3 || line.Length > 70)
            return false;

        if (line.Any(char.IsDigit) || line.Contains(':') || line.Contains('+') || line.Contains('%'))
            return false;

        if (WellKnownItemClasses.Any(known => line.Equals(known, StringComparison.OrdinalIgnoreCase) ||
                                             line.StartsWith(known + " ", StringComparison.OrdinalIgnoreCase)))
            return false;

        return !ContainsAnyText(line,
        [
            "The Well of Souls", "Options", "Confirm", "Reveal", "Desecrated Modifier", "Take this item",
            "Resistance", "Threshold", "Rating", "Regeneration", "Recharge", "Strength", "Dexterity",
            "Intelligence", "Spirit", "Damage", "Quality", "Level", "Requires", "Implicit", "Prefix", "Suffix"
        ]);
    }

    private static string TryParseWellItemClassAndLevel(string line, out int itemLevel)
    {
        itemLevel = TryParseItemLevel(line);
        string normalized = NormalizeWhitespace(line);
        int marker = normalized.IndexOf(": Item Level", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            normalized = normalized[..marker];

        return WellKnownItemClasses.FirstOrDefault(known =>
            normalized.Equals(known, StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(known + " ", StringComparison.OrdinalIgnoreCase)) ?? "";
    }

    private static int TryParseItemLevel(string line)
    {
        var match = Regex.Match(line, @"\bItem\s+Level\s*:?\s*(?<level>\d+)\b", RegexOptions.IgnoreCase);
        return match.Success && int.TryParse(match.Groups["level"].Value, out int parsed) ? parsed : 0;
    }

    private static string InferItemClassFromBaseName(string baseName)
    {
        string normalized = NormalizeWhitespace(baseName);
        if (ContainsAnyText(normalized, ["Raiment", "Robe", "Jacket", "Coat", "Mail", "Vest", "Garb", "Armour", "Plate"]))
            return "Body Armour";
        if (ContainsAnyText(normalized, ["Mitts", "Gauntlets", "Gloves", "Cuffs", "Bracers", "Wraps"]))
            return "Gloves";
        if (ContainsAnyText(normalized, ["Boots", "Shoes", "Slippers", "Greaves", "Sandals"]))
            return "Boots";
        if (ContainsAnyText(normalized, ["Helmet", "Helm", "Crown", "Mask", "Hood", "Cap"]))
            return "Helmet";
        if (ContainsAnyText(normalized, ["Buckler"]))
            return "Buckler";
        if (ContainsAnyText(normalized, ["Shield"]))
            return "Shield";
        if (ContainsAnyText(normalized, ["Quiver"]))
            return "Quiver";
        if (ContainsAnyText(normalized, ["Focus"]))
            return "Focus";
        if (ContainsAnyText(normalized, ["Belt", "Sash"]))
            return "Belt";
        if (ContainsAnyText(normalized, ["Ring"]))
            return "Ring";
        if (ContainsAnyText(normalized, ["Amulet"]))
            return "Amulet";
        if (ContainsAnyText(normalized, ["Greatclub", "Two Hand Mace"]))
            return "Two Hand Mace";
        if (ContainsAnyText(normalized, ["Mace"]))
            return "One Hand Mace";
        if (ContainsAnyText(normalized, ["Warstaff"]))
            return "Warstaff";
        if (ContainsAnyText(normalized, ["Quarterstaff"]))
            return "Quarterstaff";
        if (ContainsAnyText(normalized, ["Crossbow"]))
            return "Crossbow";
        if (ContainsAnyText(normalized, ["Bow"]))
            return "Bow";
        if (ContainsAnyText(normalized, ["Spear"]))
            return "Spear";
        if (ContainsAnyText(normalized, ["Flail"]))
            return "Flail";
        if (ContainsAnyText(normalized, ["Sceptre"]))
            return "Sceptre";
        if (ContainsAnyText(normalized, ["Wand"]))
            return "Wand";
        if (ContainsAnyText(normalized, ["Staff"]))
            return "Staff";
        if (ContainsAnyText(normalized, ["Dagger"]))
            return "Dagger";
        if (ContainsAnyText(normalized, ["Claw"]))
            return "Claw";
        if (ContainsAnyText(normalized, ["Talisman"]))
            return "Talisman";
        if (ContainsAnyText(normalized, ["Ruby", "Emerald", "Sapphire", "Diamond", "Time-Lost", "Jewel"]))
            return "Jewel";

        return "";
    }

    private static bool IsWellOptionText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string normalized = NormalizeWhitespace(text);
        if (normalized.Length < 3 || normalized.Length > 180)
            return false;

        if (ContainsAnyText(normalized, ["The Well of Souls", "Confirm", "Reveal", "Options", "Desecrated Modifier", "Take this item"]))
            return false;

        if (!normalized.Any(char.IsDigit))
            return false;

        return normalized.StartsWith("+", StringComparison.Ordinal) ||
               normalized.StartsWith("-", StringComparison.Ordinal) ||
               normalized.Contains('%') ||
               normalized.Contains(" per second", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" to ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Mana", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" enemy killed", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Magnitude", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Ailments", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Rating", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Resistance", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Spirit", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Strength", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Dexterity", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Intelligence", StringComparison.OrdinalIgnoreCase);
    }

    private static RectangleF PickTierBadgeRect(RectangleF optionRect, float width, float height)
    {
        const float pad = 5f;
        float x = optionRect.X + (optionRect.Width - width) * 0.5f;
        float y = optionRect.Y - height - pad;
        if (y < pad)
            y = optionRect.Bottom + pad;

        return ClampToDisplay(new RectangleF(x, y, width, height));
    }

    private static RectangleF ClampToDisplay(RectangleF rect)
    {
        var display = ImGui.GetIO().DisplaySize;
        float x = Math.Clamp(rect.X, 8f, Math.Max(8f, display.X - rect.Width - 8f));
        float y = Math.Clamp(rect.Y, 8f, Math.Max(8f, display.Y - rect.Height - 8f));
        return new RectangleF(x, y, rect.Width, rect.Height);
    }

    private void DrawPanelLine(PanelLine line, Vector2 pos)
    {
        float x = pos.X;
        foreach (var segment in line.Segments)
        {
            if (string.IsNullOrEmpty(segment.Text))
                continue;

            Graphics.DrawText(segment.Text, new Vector2(x, pos.Y), segment.Color);
            x += ImGui.CalcTextSize(segment.Text).X;
        }
    }

    private static float MeasureLineWidth(PanelLine line)
        => line.Segments.Sum(segment => ImGui.CalcTextSize(segment.Text).X);

    private static PanelLine Line(params TextSegment[] segments)
        => new(segments.ToList());

    private static TextSegment Segment(string text)
        => new(text, Color.White);

    private static TextSegment Segment(string text, Color color)
        => new(text, color);

    private static bool IsDrawableRect(RectangleF rect)
        => rect.Width > 1f &&
           rect.Height > 1f &&
           IsFinite(rect.X) &&
           IsFinite(rect.Y) &&
           IsFinite(rect.Width) &&
           IsFinite(rect.Height);

    private static bool IsFinite(float value)
        => !float.IsNaN(value) && !float.IsInfinity(value);

    private static bool SafeVisible(object? element)
        => SafeFlag(element, "IsVisible") == true;

    private static bool? SafeFlag(object? obj, string propertyName)
    {
        if (obj == null)
            return null;

        try
        {
            var prop = obj.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prop?.PropertyType == typeof(bool))
                return (bool?)prop.GetValue(obj);
        }
        catch { }

        return null;
    }

    private static bool ContainsAnyText(string value, IEnumerable<string> needles)
        => needles.Any(needle => !string.IsNullOrWhiteSpace(needle) && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0);

    private static string NormalizeWhitespace(string value)
        => string.Join(" ", (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static float Area(RectangleF rect)
        => Math.Max(0f, rect.Width) * Math.Max(0f, rect.Height);

    private static string FormatContext(ItemSnapshot? item)
    {
        if (item == null)
            return "No Well item context";

        string name = string.IsNullOrWhiteSpace(item.UniqueName) ? item.BaseName : $"{item.UniqueName} / {item.BaseName}";
        string level = item.ItemLevel > 0 ? $"ilvl {item.ItemLevel}" : "ilvl ?";
        string itemClass = string.IsNullOrWhiteSpace(item.ClassName) ? "class ?" : item.ClassName;
        return $"{name} | {itemClass} | {level}";
    }

    private void LogDebugFailureLimited(string operation, Exception ex)
    {
        if (!Settings.DebugMode.Value)
            return;

        var now = DateTime.UtcNow;
        lock (_nextDebugFailureLogAt)
        {
            if (_nextDebugFailureLogAt.TryGetValue(operation, out var next) && now < next)
                return;

            _nextDebugFailureLogAt[operation] = now + TimeSpan.FromSeconds(30);
        }

        LogError($"WellWise debug path failed during {operation}: {ex.Message}");
    }

    private static Color MutedTextColor()
        => Color.FromArgb(215, 190, 195, 200);

    private static Color TierColor(int tier)
        => tier switch
        {
            1 => Color.FromArgb(255, 255, 70, 100),
            2 => Color.FromArgb(255, 255, 145, 0),
            3 => Color.FromArgb(255, 255, 220, 70),
            _ => Color.FromArgb(255, 176, 190, 197)
        };
}
