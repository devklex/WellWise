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
    private sealed record ElementCandidate(Element Element, string Path, RectangleF Rect);
    private sealed record ChoiceRowMatch(Element RowElement, Element TextElement, string Path, string Text, string RawText, RectangleF RowRect, RectangleF TextRect);
    private sealed record WellOption(int Index, string Path, Element Element, Element TextElement, string Text, string RawText, RectangleF Rect, string ModKey = "");
    private sealed record TypedWellOptionCandidate(int SourceIndex, Element Element, string Text, string RawText, string ModKey, RectangleF Rect);
    private sealed record WellDrawInfo(WellOption Option, WellOfSoulsTierResult TierResult);
    private sealed record WellState(
        List<WellOption> Options,
        ItemSnapshot? ItemContext,
        bool AwaitingRevealPrompt = false,
        bool WindowVisible = false,
        bool RevealButtonVisible = false,
        bool ConfirmButtonVisible = false,
        bool PromptTextVisible = false);
    private sealed record WellRootCandidate(Element Root, WellState State, string Source, int Order);
    private sealed record AreaInfo(string InstanceName, string AreaName, string AreaId, string RawName);
    private sealed record TextCandidate(Element Element, string Path, string Text, string RawText, RectangleF Rect);

    private static readonly Regex WellRollValueRegex = new(@"(?<prefix>[+-]?)\s*(?<value>\d+(?:\.\d+)?)(?<suffix>%?)", RegexOptions.Compiled);
    private static readonly Regex TypedWellTranslationTokenRegex = new(@"\[(?<first>[^\]|]+)(?:\|(?<second>[^\]]+))?\]", RegexOptions.Compiled);

    private readonly WellOfSoulsTierResolver _resolver = new();
    private readonly Dictionary<string, DateTime> _nextDebugFailureLogAt = new(StringComparer.OrdinalIgnoreCase);
    private List<WellOption> _options = [];
    private List<WellDrawInfo> _drawInfos = [];
    private ItemSnapshot? _itemContext;
    private Element? _cachedWellRoot;
    private DateTime _nextScanAt = DateTime.MinValue;
    private DateTime _nextBroadScanAt = DateTime.MinValue;
    private DateTime _partialCooldownUntil = DateTime.MinValue;
    private DateTime _wellWindowMissingSince = DateTime.MinValue;
    private DateTime _activeWellTransitionUntil = DateTime.MinValue;
    private DateTime _softRebindUntil = DateTime.MinValue;
    private int _consecutivePartialReads;
    private int _consecutiveIncompleteChoiceReads;
    private int _consecutiveSoftRebinds;
    private bool _outsideWellIdle;
    private string? _diagnosticScanPath;
    private DateTime _nextDiagnosticScanAt = DateTime.MinValue;
    private int _diagnosticScanSequence;
    private string? _staleChoiceUiNotice;
    private DateTime _staleChoiceUiNoticeUntil = DateTime.MinValue;

    private static readonly TimeSpan ScanInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan BroadScanInterval = TimeSpan.FromMilliseconds(4000);
    private static readonly TimeSpan IdleScanInterval = TimeSpan.FromMilliseconds(2000);
    private static readonly TimeSpan IdleBroadScanInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DiagnosticScanInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan DiagnosticScanIdleInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PartialRetryInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PartialBroadRetryInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan PartialCooldownInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MissingWellRetryInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan MissingWellBroadRetryInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan MissingWellContextGraceInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ActiveTransitionScanInterval = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ActiveTransitionBroadInterval = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan ActiveTransitionWindow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SoftRebindRetryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan SoftRebindBroadInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SoftRebindWindow = TimeSpan.FromSeconds(2);
    private static readonly JsonSerializerOptions DiagnosticJsonOptions = new() { WriteIndented = true };
    private const int MaxConsecutivePartialReads = 6;
    private const int PartialSoftRebindThreshold = 2;
    private const int MaxSoftRebindBurstsBeforeManualRefresh = 4;
    private static readonly TimeSpan StaleChoiceUiRetryInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StaleChoiceUiBroadRetryInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StaleChoiceUiNoticeInterval = TimeSpan.FromSeconds(6);
    private static readonly string[] WellPrimarySearchTerms =
    {
        "The Well of Souls",
        "Desecrated Modifier",
        "Unrevealed Desecrated Modifier",
        "Take this item",
        "Place an item with",
        "Reveal",
        "Confirm"
    };
    private static readonly string[] WellFallbackSearchTerms =
    {
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
    };
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
        _consecutiveIncompleteChoiceReads = 0;
        _nextScanAt = DateTime.MinValue;
        _nextBroadScanAt = DateTime.MinValue;
        _partialCooldownUntil = DateTime.MinValue;
        _activeWellTransitionUntil = DateTime.MinValue;
        _softRebindUntil = DateTime.MinValue;
        _consecutiveSoftRebinds = 0;
        ClearStaleChoiceUiNotice();
        _outsideWellIdle = false;
        ResetDiagnosticScanSession();
    }

    public override void Render()
    {
        if (!Settings.Enable.Value)
            return;

        var now = DateTime.UtcNow;
        var areaInfo = GetCurrentAreaInfo();
        bool inWellArea = IsWellOfSoulsArea(areaInfo);
        MaybeRecordDiagnosticScan(now, areaInfo, inWellArea);
        if (!inWellArea)
        {
            if (!_outsideWellIdle)
            {
                ClearWellState();
                Settings.LastStatus.Value = _resolver.LoadStatus;
                Settings.LastContext.Value = "No Well item context";
                Settings.LastOptions.Value = "Outside The Well of Souls area";
                _nextScanAt = now + IdleScanInterval;
                _nextBroadScanAt = now + IdleBroadScanInterval;
                _outsideWellIdle = true;
            }

            DrawAreaDebugOverlay(areaInfo, false);
            return;
        }

        _outsideWellIdle = false;

        if (now >= _nextScanAt)
        {
            bool allowBroadScan = now >= _nextBroadScanAt;
            var state = ReadWellState(allowBroadScan);
            if (!state.WindowVisible)
            {
                if (IsManualRefreshNoticeActive(now))
                {
                    Settings.LastStatus.Value = _resolver.LoadStatus;
                    Settings.LastContext.Value = FormatContext(_itemContext);
                    Settings.LastOptions.Value = _staleChoiceUiNotice ?? "Well choice UI is stale; close and reopen the Well window to refresh";
                    _nextScanAt = now + StaleChoiceUiRetryInterval;
                    ScheduleBroadScanNoLaterThan(now + StaleChoiceUiBroadRetryInterval);
                    DrawStaleChoiceUiNotice(now);
                    return;
                }

                if (_wellWindowMissingSince == DateTime.MinValue)
                    _wellWindowMissingSince = now;

                bool withinMissingGrace = now - _wellWindowMissingSince < MissingWellContextGraceInterval;
                bool hasRecoverableSession = withinMissingGrace && (_itemContext != null || _options.Count > 0 || _drawInfos.Count > 0);
                if (!withinMissingGrace)
                    ClearWellState(resetMissingWindowTimer: false);

                Settings.LastStatus.Value = _resolver.LoadStatus;
                Settings.LastContext.Value = FormatContext(_itemContext);
                Settings.LastOptions.Value = "Well of Souls not visible";
                if (hasRecoverableSession)
                {
                    bool activeTransition = now <= _activeWellTransitionUntil;
                    _nextScanAt = now + (activeTransition ? ActiveTransitionScanInterval : MissingWellRetryInterval);
                    ScheduleBroadScanNoLaterThan(now + (activeTransition ? ActiveTransitionBroadInterval : MissingWellBroadRetryInterval));
                }
                else
                {
                    _nextScanAt = now + IdleScanInterval;
                    ScheduleBroadScanNoLaterThan(now + BroadScanInterval);
                }

                return;
            }

            _wellWindowMissingSince = DateTime.MinValue;

            if (HandlePartialOptionRead(state, now))
            {
                DrawWellOptions(_drawInfos);
                DrawStaleChoiceUiNotice(now);
                return;
            }

            bool useActiveTransitionCadence = false;
            if (state.AwaitingRevealPrompt)
            {
                _options = [];
                _drawInfos = [];
                _consecutivePartialReads = 0;
                _consecutiveIncompleteChoiceReads = 0;
                _consecutiveSoftRebinds = 0;
                _softRebindUntil = DateTime.MinValue;
                ClearStaleChoiceUiNotice();
                _activeWellTransitionUntil = now + ActiveTransitionWindow;
                useActiveTransitionCadence = true;
                if (state.ItemContext != null)
                    PreserveOrUpdateItemContext(state.ItemContext, now);
            }
            else
            {
                if (state.Options.Count > 0)
                    _options = state.Options;
                else if (state.ItemContext == null || !SameWellItemContext(state.ItemContext, _itemContext))
                    _options = [];

                if (state.ItemContext != null)
                    PreserveOrUpdateItemContext(state.ItemContext, now);

                if (state.Options.Count >= 3)
                {
                    _activeWellTransitionUntil = DateTime.MinValue;
                    _softRebindUntil = DateTime.MinValue;
                    _consecutiveSoftRebinds = 0;
                    _consecutiveIncompleteChoiceReads = 0;
                    ClearStaleChoiceUiNotice();
                }
                else if (state.Options.Count == 0 && _itemContext != null && now <= _activeWellTransitionUntil)
                    useActiveTransitionCadence = true;
            }

            _drawInfos = BuildDrawInfos(_options, _itemContext);
            Settings.LastStatus.Value = _resolver.LoadStatus;
            Settings.LastContext.Value = FormatContext(_itemContext);
            Settings.LastOptions.Value = state.AwaitingRevealPrompt
                ? "Well prompt visible; waiting for reveal"
                : _options.Count == 0 ? "No Well options found" : $"{_options.Count} Well options";
            _consecutivePartialReads = 0;
            if (state.Options.Count >= 3 || state.AwaitingRevealPrompt)
                _consecutiveIncompleteChoiceReads = 0;
            _partialCooldownUntil = DateTime.MinValue;
            _nextScanAt = now + (useActiveTransitionCadence ? ActiveTransitionScanInterval : ScanInterval);
            if (allowBroadScan)
                _nextBroadScanAt = now + BroadScanInterval;
            if (useActiveTransitionCadence)
                ScheduleBroadScanNoLaterThan(now + ActiveTransitionBroadInterval);
        }

        DrawWellOptions(_drawInfos);
        DrawStaleChoiceUiNotice(now);
        DrawAreaDebugOverlay(areaInfo, true);
    }

    private void ClearWellState(bool resetMissingWindowTimer = true)
    {
        _options = [];
        _drawInfos = [];
        _itemContext = null;
        _cachedWellRoot = null;
        _consecutivePartialReads = 0;
        _consecutiveIncompleteChoiceReads = 0;
        _partialCooldownUntil = DateTime.MinValue;
        _activeWellTransitionUntil = DateTime.MinValue;
        _softRebindUntil = DateTime.MinValue;
        _consecutiveSoftRebinds = 0;
        ClearStaleChoiceUiNotice();
        if (resetMissingWindowTimer)
            _wellWindowMissingSince = DateTime.MinValue;
    }

    private void ScheduleBroadScanNoLaterThan(DateTime target)
    {
        if (_nextBroadScanAt == DateTime.MinValue || _nextBroadScanAt > target)
            _nextBroadScanAt = target;
    }

    private void ForceWellUiSoftRebind(DateTime now, string status)
    {
        _cachedWellRoot = null;
        _options = [];
        _drawInfos = [];
        _partialCooldownUntil = DateTime.MinValue;
        _activeWellTransitionUntil = now + ActiveTransitionWindow;
        _softRebindUntil = now + SoftRebindWindow;
        _consecutiveSoftRebinds++;
        _nextScanAt = now + SoftRebindRetryInterval;
        _nextBroadScanAt = now + SoftRebindBroadInterval;
        Settings.LastStatus.Value = _resolver.LoadStatus;
        Settings.LastContext.Value = FormatContext(_itemContext);
        Settings.LastOptions.Value = status;
    }

    private void MarkWellUiNeedsManualRefresh(DateTime now, int optionCount)
    {
        _cachedWellRoot = null;
        _options = [];
        _drawInfos = [];
        _softRebindUntil = DateTime.MinValue;
        _partialCooldownUntil = DateTime.MinValue;
        _nextScanAt = now + StaleChoiceUiRetryInterval;
        _nextBroadScanAt = now + StaleChoiceUiBroadRetryInterval;
        Settings.LastStatus.Value = _resolver.LoadStatus;
        Settings.LastContext.Value = FormatContext(_itemContext);
        string message = optionCount <= 0
            ? "Well choice UI is visible but ExileCore exposes 0/3 rows; close and reopen the Well window to refresh"
            : $"Well choice UI is stale ({optionCount}/3 rows); close and reopen the Well window to refresh";
        Settings.LastOptions.Value = message;
        SetStaleChoiceUiNotice(now, message);
    }

    private void SetStaleChoiceUiNotice(DateTime now, string message)
    {
        _staleChoiceUiNotice = message;
        _staleChoiceUiNoticeUntil = now + StaleChoiceUiNoticeInterval;
    }

    private void ClearStaleChoiceUiNotice()
    {
        _staleChoiceUiNotice = null;
        _staleChoiceUiNoticeUntil = DateTime.MinValue;
    }

    private bool IsManualRefreshNoticeActive(DateTime now)
        => !string.IsNullOrWhiteSpace(_staleChoiceUiNotice) && now <= _staleChoiceUiNoticeUntil;

    private void ResetDiagnosticScanSession()
    {
        _diagnosticScanPath = null;
        _nextDiagnosticScanAt = DateTime.MinValue;
        _diagnosticScanSequence = 0;
    }

    private void MaybeRecordDiagnosticScan(DateTime now, AreaInfo areaInfo, bool inWellArea)
    {
        if (!Settings.RecordDiagnosticScan.Value)
        {
            if (_diagnosticScanPath != null)
                Settings.LastDiagnosticCapture.Value = $"Diagnostic scan stopped: {_diagnosticScanPath}";

            ResetDiagnosticScanSession();
            return;
        }

        if (now < _nextDiagnosticScanAt)
            return;

        _nextDiagnosticScanAt = now + (inWellArea ? DiagnosticScanInterval : DiagnosticScanIdleInterval);

        try
        {
            EnsureDiagnosticScanPath(now);
            if (_diagnosticScanPath == null)
                return;

            var report = new StringBuilder();
            report.AppendLine($"--- Capture {++_diagnosticScanSequence} {now:O} ---");
            report.AppendLine($"In Well area: {inWellArea}");
            report.AppendLine($"InstanceName: {areaInfo.InstanceName}");
            report.AppendLine($"AreaName: {areaInfo.AreaName}");
            report.AppendLine($"AreaId: {areaInfo.AreaId}");
            report.AppendLine($"RawName: {areaInfo.RawName}");
            report.AppendLine();
            report.AppendLine("[Scan schedule]");
            report.AppendLine($"Consecutive partial reads: {_consecutivePartialReads}");
            report.AppendLine($"Consecutive incomplete choice reads: {_consecutiveIncompleteChoiceReads}");
            report.AppendLine($"Next scan: {FormatSchedule(_nextScanAt, now)}");
            report.AppendLine($"Next broad scan: {FormatSchedule(_nextBroadScanAt, now)}");
            report.AppendLine($"Partial cooldown until: {FormatSchedule(_partialCooldownUntil, now)}");
            report.AppendLine($"Soft rebind until: {FormatSchedule(_softRebindUntil, now)}");
            report.AppendLine($"Consecutive soft rebinds: {_consecutiveSoftRebinds}");
            report.AppendLine($"Cached root: {FormatElement(_cachedWellRoot)}");
            report.AppendLine();
            report.AppendLine("[Last status fields]");
            report.AppendLine($"LastStatus: {Settings.LastStatus.Value}");
            report.AppendLine($"LastContext: {Settings.LastContext.Value}");
            report.AppendLine($"LastOptions: {Settings.LastOptions.Value}");
            report.AppendLine();

            if (!inWellArea)
            {
                report.AppendLine("Skipped Well scan: outside The Well of Souls area.");
                report.AppendLine();
            }
            else
            {
                AppendUiRootReport(report);

                WellState freshState;
                try
                {
                    freshState = ReadWellState(allowBroadSearch: true, updateCache: false);
                }
                catch (Exception ex)
                {
                    freshState = new WellState([], null);
                    report.AppendLine("[Continuous fresh broad Well read]");
                    report.AppendLine($"Read failed: {ex}");
                    report.AppendLine();
                }

                var freshItem = freshState.ItemContext ?? _itemContext;
                AppendWellStateReport(report, "Continuous fresh broad Well read", freshState, freshItem, BuildDrawInfos(freshState.Options, freshItem));

                var cachedState = new WellState(_options, _itemContext, WindowVisible: _options.Count > 0);
                AppendWellStateReport(report, "Continuous cached overlay state", cachedState, _itemContext, _drawInfos);

                AppendTypedWellApiProbeReport(report);
                AppendCandidateRootReport(report);
                AppendRuntimeProbeReport(report, freshState, freshItem);
            }

            File.AppendAllText(_diagnosticScanPath, report.ToString(), Encoding.UTF8);
            Settings.LastDiagnosticCapture.Value = $"Diagnostic scan #{_diagnosticScanSequence}: {_diagnosticScanPath}";
        }
        catch (Exception ex)
        {
            Settings.LastDiagnosticCapture.Value = $"Diagnostic scan failed: {ex.Message}";
            LogDebugFailureLimited("record diagnostic scan", ex);
        }
    }

    private void EnsureDiagnosticScanPath(DateTime now)
    {
        if (_diagnosticScanPath != null)
            return;

        var reportDirectory = Path.Combine(DirectoryFullName, "diagnostics");
        Directory.CreateDirectory(reportDirectory);
        _diagnosticScanPath = Path.Combine(reportDirectory, $"wellwise-scan-{now:yyyyMMdd-HHmmss}.txt");

        var header = new StringBuilder();
        header.AppendLine("WellWise continuous diagnostic scan");
        header.AppendLine($"Started UTC: {now:O}");
        header.AppendLine($"Plugin directory: {DirectoryFullName}");
        header.AppendLine($"Assembly version: {GetType().Assembly.GetName().Version}");
        header.AppendLine("Toggle RecordDiagnosticScan off to stop this file.");
        header.AppendLine();
        File.WriteAllText(_diagnosticScanPath, header.ToString(), Encoding.UTF8);
        Settings.LastDiagnosticCapture.Value = $"Diagnostic scan started: {_diagnosticScanPath}";
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

    private void DrawStaleChoiceUiNotice(DateTime now)
    {
        if (!IsManualRefreshNoticeActive(now))
            return;

        string line1 = "WellWise: Well choice UI is stale";
        string line2 = "Close and reopen the Well window to refresh labels.";
        string line3 = "No partial labels are drawn to avoid wrong rows.";
        float lineHeight = ImGui.GetTextLineHeight();
        float width = Math.Clamp(new[] { line1, line2, line3 }.Max(line => ImGui.CalcTextSize(line).X) + 18f, 360f, 900f);
        float height = lineHeight * 3f + 14f;
        var display = ImGui.GetIO().DisplaySize;
        var box = ClampToDisplay(new RectangleF(display.X * 0.5f - width * 0.5f, display.Y * 0.16f, width, height));

        Graphics.DrawBox(box, Color.FromArgb(236, 4, 4, 4));
        Graphics.DrawFrame(box, Color.FromArgb(230, 235, 170, 70), 1);
        Graphics.DrawText(line1, new Vector2(box.X + 9f, box.Y + 6f), Color.Orange);
        Graphics.DrawText(line2, new Vector2(box.X + 9f, box.Y + 6f + lineHeight), Color.LightGray);
        Graphics.DrawText(line3, new Vector2(box.X + 9f, box.Y + 6f + lineHeight * 2f), Color.LightGray);
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

    private static object? TryReadObjectProperty(object? obj, string propertyName)
    {
        if (obj == null)
            return null;

        try
        {
            var type = obj.GetType();
            var properties = type
                .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(prop => prop.Name == propertyName && prop.GetIndexParameters().Length == 0 && prop.CanRead)
                .ToList();

            if (properties.Count == 0)
                return null;

            var prop =
                properties.FirstOrDefault(prop => prop.DeclaringType == type) ??
                properties.FirstOrDefault();

            if (prop == null)
                return null;

            return prop.GetValue(obj);
        }
        catch
        {
            return null;
        }
    }

    private bool HandlePartialOptionRead(WellState state, DateTime now)
    {
        bool activeChoiceState = state.WindowVisible &&
                                 state.ConfirmButtonVisible &&
                                 !state.RevealButtonVisible &&
                                 !state.AwaitingRevealPrompt;

        if (state.Options.Count >= 3)
            return false;

        // A visible Confirm state with 0/3, 1/3, or 2/3 rows is the stale-UI failure seen
        // after Heart multi-reveal transitions. Treat every incomplete active choice read
        // as unsafe: never draw partial labels, never preserve partial row order, and retry
        // only by dropping cached Element handles and forcing re-acquisition.
        if (activeChoiceState && (_itemContext != null || state.ItemContext != null || state.Options.Count > 0))
        {
            if (TryGetTypedFallbackItemContext(state, out var currentItemContext) &&
                TryApplyTypedWellFallbackFromPartialState(state, currentItemContext, now))
            {
                return true;
            }

            _consecutiveIncompleteChoiceReads++;
            _consecutivePartialReads = state.Options.Count > 0 ? _consecutivePartialReads + 1 : 0;
            _options = [];
            _drawInfos = [];

            if (state.ItemContext != null)
            {
                bool sameItem = _itemContext == null || SameWellItemContext(state.ItemContext, _itemContext);
                if (!sameItem)
                {
                    _cachedWellRoot = null;
                    Settings.LastStatus.Value = _resolver.LoadStatus;
                    Settings.LastContext.Value = FormatContext(_itemContext);
                    Settings.LastOptions.Value = $"Ignoring incomplete Well choices from different item ({state.Options.Count}/3); retrying";
                    _nextScanAt = now + PartialRetryInterval;
                    ScheduleBroadScanNoLaterThan(now + PartialBroadRetryInterval);
                    return true;
                }

                PreserveOrUpdateItemContext(state.ItemContext, now);
            }

            Settings.LastStatus.Value = _resolver.LoadStatus;
            Settings.LastContext.Value = FormatContext(_itemContext);

            if (_consecutiveSoftRebinds >= MaxSoftRebindBurstsBeforeManualRefresh)
            {
                MarkWellUiNeedsManualRefresh(now, state.Options.Count);
                return true;
            }

            if (_consecutiveIncompleteChoiceReads >= PartialSoftRebindThreshold && now >= _softRebindUntil)
            {
                ForceWellUiSoftRebind(now, state.Options.Count <= 0
                    ? "Incomplete Well choice UI (0/3); soft-rebinding Well UI state"
                    : $"Incomplete Well choice UI ({state.Options.Count}/3); soft-rebinding Well UI state");
                return true;
            }

            bool inSoftRebindBurst = now < _softRebindUntil;
            _nextScanAt = now + (inSoftRebindBurst ? SoftRebindRetryInterval : PartialRetryInterval);
            ScheduleBroadScanNoLaterThan(now + (inSoftRebindBurst ? SoftRebindBroadInterval : ActiveTransitionBroadInterval));
            Settings.LastOptions.Value = inSoftRebindBurst
                ? $"Incomplete Well choice UI ({state.Options.Count}/3); soft rebind active, retrying without labels"
                : $"Incomplete Well choice UI ({state.Options.Count}/3); retrying without labels";
            return true;
        }

        if (state.Options.Count == 0)
            return false;

        _consecutivePartialReads++;
        _consecutiveIncompleteChoiceReads++;

        bool sameItemForPartial = state.ItemContext != null && SameWellItemContext(state.ItemContext, _itemContext);
        bool canInheritCurrentContext = state.ItemContext == null && _itemContext != null;

        if (state.ItemContext != null)
        {
            if (_itemContext != null && !sameItemForPartial)
            {
                _options = [];
                _drawInfos = [];
                _cachedWellRoot = null;
                Settings.LastStatus.Value = _resolver.LoadStatus;
                Settings.LastContext.Value = FormatContext(_itemContext);
                Settings.LastOptions.Value = $"Ignoring partial Well options from different item ({state.Options.Count}/3); retrying";
                _nextScanAt = now + PartialRetryInterval;
                ScheduleBroadScanNoLaterThan(now + PartialBroadRetryInterval);
                return true;
            }

            PreserveOrUpdateItemContext(state.ItemContext, now);
        }
        else if (!canInheritCurrentContext)
        {
            _options = [];
            _drawInfos = [];
        }

        // Outside the active Confirm state, partial reads are still not safe to draw.
        _options = [];
        _drawInfos = [];
        _cachedWellRoot = null;

        Settings.LastStatus.Value = _resolver.LoadStatus;
        Settings.LastContext.Value = FormatContext(_itemContext);

        if (_consecutivePartialReads >= MaxConsecutivePartialReads)
        {
            if (now <= _activeWellTransitionUntil)
            {
                _nextScanAt = now + PartialRetryInterval;
                ScheduleBroadScanNoLaterThan(now + ActiveTransitionBroadInterval);
                Settings.LastOptions.Value = $"Partial Well options during reveal transition ({state.Options.Count}/3); retrying without cooldown";
                return true;
            }

            _partialCooldownUntil = now + PartialCooldownInterval;
            _nextScanAt = _partialCooldownUntil;
            _nextBroadScanAt = _partialCooldownUntil;
            Settings.LastOptions.Value = $"Partial Well options stuck ({state.Options.Count}/3); cooling down {PartialCooldownInterval.TotalSeconds:0}s";
            return true;
        }

        _nextScanAt = now + PartialRetryInterval;
        ScheduleBroadScanNoLaterThan(now + PartialBroadRetryInterval);

        Settings.LastOptions.Value = $"Partial Well options ({state.Options.Count}/3); retrying {_consecutivePartialReads}/{MaxConsecutivePartialReads}";
        return true;
    }

    private bool TryGetTypedFallbackItemContext(WellState state, out ItemSnapshot itemContext)
    {
        itemContext = null!;

        if (_itemContext != null)
        {
            if (state.ItemContext != null && !SameWellItemContext(state.ItemContext, _itemContext))
                return false;

            itemContext = _itemContext;
            return true;
        }

        if (state.ItemContext == null)
            return false;

        itemContext = state.ItemContext;
        return true;
    }

    private bool TryApplyTypedWellFallbackFromPartialState(WellState state, ItemSnapshot currentItemContext, DateTime now)
    {
        if (!state.WindowVisible ||
            state.AwaitingRevealPrompt ||
            state.RevealButtonVisible ||
            !state.ConfirmButtonVisible ||
            state.Options.Count >= 3)
        {
            return false;
        }

        if (!TryBuildTypedWellOptions(out var typedOptions))
            return false;

        _options = typedOptions;
        _itemContext = currentItemContext;
        _drawInfos = BuildDrawInfos(_options, _itemContext);
        _consecutivePartialReads = 0;
        _consecutiveIncompleteChoiceReads = 0;
        _consecutiveSoftRebinds = 0;
        _partialCooldownUntil = DateTime.MinValue;
        _softRebindUntil = DateTime.MinValue;
        _activeWellTransitionUntil = DateTime.MinValue;
        ClearStaleChoiceUiNotice();

        Settings.LastStatus.Value = _resolver.LoadStatus;
        Settings.LastContext.Value = FormatContext(_itemContext);
        Settings.LastOptions.Value = "3 Well options (typed fallback)";
        _nextScanAt = now + ScanInterval;
        _nextBroadScanAt = now + BroadScanInterval;
        return true;
    }

    private bool TryBuildTypedWellOptions(out List<WellOption> options)
    {
        options = [];

        Element? typedWindow;
        try
        {
            typedWindow = TryGetElementProperty(GameController.Game.IngameState.IngameUi, "WellOfSoulsWindow");
        }
        catch
        {
            return false;
        }

        if (typedWindow == null ||
            typedWindow.Address == 0 ||
            !SafeVisible(typedWindow) ||
            !TryGetRect(typedWindow, out var windowRect) ||
            !IsDrawableRect(windowRect))
        {
            return false;
        }

        if (TryReadObjectProperty(typedWindow, "Item") == null)
            return false;

        var confirmButton = TryGetElementProperty(typedWindow, "ConfirmButton");
        if (confirmButton == null || confirmButton.Address == 0 || !SafeVisible(confirmButton))
            return false;

        if (!TryGetRect(confirmButton, out var confirmRect) || !IsDrawableRect(confirmRect))
            return false;

        if (TryReadObjectProperty(typedWindow, "RevealOptions") is not IEnumerable revealOptionsEnumerable)
            return false;

        var rawOptions = new List<object?>();
        foreach (var rawOption in revealOptionsEnumerable)
        {
            rawOptions.Add(rawOption);
            if (rawOptions.Count > 3)
                return false;
        }

        if (rawOptions.Count != 3)
            return false;

        var candidates = new List<TypedWellOptionCandidate>();
        for (int i = 0; i < rawOptions.Count; i++)
        {
            if (!TryBuildTypedWellOption(rawOptions[i], i, windowRect, out var candidate))
                return false;

            candidates.Add(candidate);
        }

        var orderedCandidates = candidates
            .OrderBy(candidate => candidate.Rect.Y)
            .ThenBy(candidate => candidate.Rect.X)
            .ToList();

        if (!TypedWellRowsAreCompatible(orderedCandidates, windowRect))
            return false;

        options = orderedCandidates
            .Select((candidate, index) => new WellOption(
                index + 1,
                $"typed:RevealOptions[{candidate.SourceIndex}]",
                candidate.Element,
                candidate.Element,
                candidate.Text,
                candidate.RawText,
                candidate.Rect,
                candidate.ModKey))
            .ToList();
        return true;
    }

    private static bool TryBuildTypedWellOption(object? rawOption, int sourceIndex, RectangleF windowRect, out TypedWellOptionCandidate candidate)
    {
        candidate = null!;

        if (rawOption is not Element optionElement ||
            optionElement.Address == 0 ||
            !SafeVisible(optionElement) ||
            !TryGetRect(optionElement, out var optionRect) ||
            !LooksLikeTypedWellOptionRowRect(windowRect, optionRect))
        {
            return false;
        }

        var modInstance = TryReadObjectProperty(rawOption, "ModInstance");
        if (modInstance == null)
            return false;

        string rawTranslation = ReadObjectStringProperty(modInstance, "Translation");
        string text = NormalizeTypedWellTranslation(rawTranslation);
        if (!IsWellRowOptionText(text) || LooksLikeTooltipOrItemText(text))
            return false;

        string modKey = TryGetTypedModKey(rawOption, modInstance);
        candidate = new TypedWellOptionCandidate(
            sourceIndex,
            optionElement,
            text,
            NormalizeWhitespace(rawTranslation),
            modKey,
            optionRect);
        return true;
    }

    private static string NormalizeTypedWellTranslation(string translation)
    {
        if (string.IsNullOrWhiteSpace(translation))
            return string.Empty;

        string displayText = TypedWellTranslationTokenRegex.Replace(translation, match =>
        {
            var display = match.Groups["second"];
            if (display.Success && !string.IsNullOrWhiteSpace(display.Value))
                return display.Value;

            return match.Groups["first"].Value;
        });

        return NormalizeWhitespace(displayText);
    }

    private static string TryGetTypedModKey(object option, object modInstance)
    {
        var modRecord = TryReadObjectProperty(modInstance, "ModRecord");
        string modKey = NormalizeWhitespace(ReadObjectStringProperty(modRecord, "Key"));
        if (!string.IsNullOrWhiteSpace(modKey))
            return modKey;

        var mod = TryReadObjectProperty(option, "Mod");
        return NormalizeWhitespace(ReadObjectStringProperty(mod, "Key"));
    }

    private static bool LooksLikeTypedWellOptionRowRect(RectangleF windowRect, RectangleF rowRect)
    {
        if (!IsDrawableRect(rowRect))
            return false;

        if (!RectCenterInside(ExpandRect(windowRect, 24f), rowRect))
            return false;

        if (rowRect.Width < windowRect.Width * 0.35f)
            return false;

        if (rowRect.Height < 26f || rowRect.Height > Math.Max(220f, windowRect.Height * 0.28f))
            return false;

        if (rowRect.Width > windowRect.Width * 0.98f && rowRect.Height > windowRect.Height * 0.75f)
            return false;

        return true;
    }

    private static bool TypedWellRowsAreCompatible(IReadOnlyList<TypedWellOptionCandidate> rows, RectangleF windowRect)
    {
        if (rows.Count != 3)
            return false;

        for (int i = 1; i < rows.Count; i++)
        {
            var previous = rows[i - 1].Rect;
            var current = rows[i].Rect;
            float previousCenter = previous.Y + previous.Height * 0.5f;
            float currentCenter = current.Y + current.Height * 0.5f;
            if (currentCenter - previousCenter < 20f)
                return false;
        }

        float minCenterX = rows.Min(row => row.Rect.X + row.Rect.Width * 0.5f);
        float maxCenterX = rows.Max(row => row.Rect.X + row.Rect.Width * 0.5f);
        if (maxCenterX - minCenterX > windowRect.Width * 0.35f)
            return false;

        float minWidth = rows.Min(row => row.Rect.Width);
        float maxWidth = rows.Max(row => row.Rect.Width);
        float averageWidth = rows.Average(row => row.Rect.Width);
        if (maxWidth - minWidth > Math.Max(180f, averageWidth * 0.35f))
            return false;

        float minHeight = rows.Min(row => row.Rect.Height);
        float maxHeight = rows.Max(row => row.Rect.Height);
        float averageHeight = rows.Average(row => row.Rect.Height);
        return maxHeight - minHeight <= Math.Max(80f, averageHeight * 0.6f);
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
        _consecutiveIncompleteChoiceReads = 0;
        _nextScanAt = DateTime.MinValue;
        _nextBroadScanAt = DateTime.MinValue;
        _partialCooldownUntil = DateTime.MinValue;
        _activeWellTransitionUntil = DateTime.MinValue;
        _softRebindUntil = DateTime.MinValue;
        _consecutiveSoftRebinds = 0;
        ClearStaleChoiceUiNotice();
        _outsideWellIdle = false;
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
            report.AppendLine($"RecordDiagnosticScan: {Settings.RecordDiagnosticScan.Value}");
            report.AppendLine($"LastDiagnosticCapture: {Settings.LastDiagnosticCapture.Value}");
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
            report.AppendLine($"Consecutive incomplete choice reads: {_consecutiveIncompleteChoiceReads}");
            report.AppendLine($"Next scan: {FormatSchedule(_nextScanAt, now)}");
            report.AppendLine($"Next broad scan: {FormatSchedule(_nextBroadScanAt, now)}");
            report.AppendLine($"Partial cooldown until: {FormatSchedule(_partialCooldownUntil, now)}");
            report.AppendLine($"Soft rebind until: {FormatSchedule(_softRebindUntil, now)}");
            report.AppendLine($"Consecutive soft rebinds: {_consecutiveSoftRebinds}");
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
                freshState = ReadWellState(allowBroadSearch: true, updateCache: false);
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

            AppendTypedWellApiProbeReport(report);
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

    private void AppendTypedWellApiProbeReport(StringBuilder report)
    {
        report.AppendLine("[Typed Well API probe]");
        try
        {
            var window = GameController.Game.IngameState.IngameUi.WellOfSoulsWindow;
            report.AppendLine($"Window: {FormatElement(window)}");
            report.AppendLine($"WindowRuntimeType: {window?.GetType().FullName ?? "null"}");
            if (window == null)
            {
                report.AppendLine();
                return;
            }

            AppendSafeReportValue(report, "", "Item", () => ReadDiagnosticProperty(window, "Item") == null ? "null" : "present");
            AppendSafeReportValue(report, "", "ItemSlot", () => FormatElement(ReadDiagnosticProperty(window, "ItemSlot") as Element));
            AppendSafeReportValue(report, "", "ConfirmButton", () => FormatElement(ReadDiagnosticProperty(window, "ConfirmButton") as Element));

            IEnumerable? revealOptions = null;
            try
            {
                revealOptions = ReadDiagnosticProperty(window, "RevealOptions") as IEnumerable;
                report.AppendLine($"RevealOptions: {(revealOptions == null ? "null" : "present")} count={GetReportEnumerableCount(revealOptions)}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"RevealOptions: failed: {ex.Message}");
            }

            if (revealOptions != null)
            {
                int index = 1;
                foreach (var option in revealOptions)
                    AppendTypedWellRevealOptionReport(report, index++, option);
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"Typed Well API probe failed: {ex}");
        }

        report.AppendLine();
    }

    private static void AppendTypedWellRevealOptionReport(StringBuilder report, int index, object? option)
    {
        report.AppendLine($"Option {index}: {FormatElement(option as Element)}");
        if (option == null)
            return;

        try
        {
            var mod = ReadDiagnosticProperty(option, "Mod");
            report.AppendLine($"  Mod: {(mod == null ? "null" : "present")}");
            if (mod != null)
            {
                AppendSafeReportValue(report, "  ", "Mod.Key", () => ReadDiagnosticProperty(mod, "Key"));
                AppendSafeReportValue(report, "  ", "Mod.UserFriendlyName", () => ReadDiagnosticProperty(mod, "UserFriendlyName"));
                AppendSafeReportValue(report, "  ", "Mod.Tier", () => ReadDiagnosticProperty(mod, "Tier"));
                AppendSafeReportValue(report, "  ", "Mod.AffixType", () => ReadDiagnosticProperty(mod, "AffixType"));
                AppendSafeReportValue(report, "  ", "Mod.MinLevel", () => ReadDiagnosticProperty(mod, "MinLevel"));
                AppendSafeReportValue(report, "  ", "Mod.StatNames", () => ReadDiagnosticProperty(mod, "StatNames"));
                AppendSafeReportValue(report, "  ", "Mod.StatRange", () => ReadDiagnosticProperty(mod, "StatRange"));
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Mod: failed: {ex.Message}");
        }

        try
        {
            var modInstance = ReadDiagnosticProperty(option, "ModInstance");
            report.AppendLine($"  ModInstance: {(modInstance == null ? "null" : "present")}");
            if (modInstance == null)
                return;

            report.AppendLine($"  ModInstanceRuntimeType: {modInstance.GetType().FullName}");
            AppendSafeReportValue(report, "  ", "ModInstance.RawName", () => ReadDiagnosticProperty(modInstance, "RawName"));
            AppendSafeReportValue(report, "  ", "ModInstance.Name", () => ReadDiagnosticProperty(modInstance, "Name"));
            AppendSafeReportValue(report, "  ", "ModInstance.DisplayName", () => ReadDiagnosticProperty(modInstance, "DisplayName"));
            AppendSafeReportValue(report, "  ", "ModInstance.Translation", () => ReadDiagnosticProperty(modInstance, "Translation"));
            AppendSafeReportValue(report, "  ", "ModInstance.Values", () => ReadDiagnosticProperty(modInstance, "Values"));
            AppendSafeReportValue(report, "  ", "ModInstance.ValuesMinMax", () => ReadDiagnosticProperty(modInstance, "ValuesMinMax"));

            object? modRecord = null;
            try
            {
                modRecord = ReadDiagnosticProperty(modInstance, "ModRecord");
                report.AppendLine($"  ModInstance.ModRecord: {(modRecord == null ? "null" : "present")}");
            }
            catch (Exception ex)
            {
                report.AppendLine($"  ModInstance.ModRecord: failed: {ex.Message}");
            }

            if (modRecord != null)
            {
                AppendSafeReportValue(report, "  ", "ModInstance.ModRecord.Key", () => ReadDiagnosticProperty(modRecord, "Key"));
                AppendSafeReportValue(report, "  ", "ModInstance.ModRecord.StatNames", () => ReadDiagnosticProperty(modRecord, "StatNames"));
                AppendSafeReportValue(report, "  ", "ModInstance.ModRecord.StatRange", () => ReadDiagnosticProperty(modRecord, "StatRange"));
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  ModInstance: failed: {ex.Message}");
        }
    }

    private static object? ReadDiagnosticProperty(object? source, string propertyName)
    {
        if (source == null)
            return null;

        var type = source.GetType();
        var properties = type
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.Name == propertyName && p.GetIndexParameters().Length == 0)
            .ToList();

        if (properties.Count == 0)
            throw new MissingMemberException(type.FullName, propertyName);

        var property =
            properties.FirstOrDefault(p => p.DeclaringType == type && p.CanRead) ??
            properties.FirstOrDefault(p => p.CanRead);

        if (property == null)
            throw new MissingMemberException(type.FullName, propertyName);

        return property.GetValue(source);
    }

    private static int GetReportEnumerableCount(IEnumerable? enumerable)
    {
        if (enumerable == null)
            return 0;

        if (enumerable is ICollection collection)
            return collection.Count;

        int count = 0;
        foreach (var _ in enumerable)
            count++;

        return count;
    }

    private static void AppendSafeReportValue(StringBuilder report, string indent, string label, Func<object?> read)
    {
        try
        {
            report.AppendLine($"{indent}{label}: {FormatReportValue(read())}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"{indent}{label}: failed: {ex.Message}");
        }
    }

    private static string FormatReportValue(object? value, int maxLength = 220)
    {
        if (value == null)
            return "null";

        try
        {
            if (value is string text)
                return TrimForReport(text, maxLength);

            if (value is IEnumerable enumerable)
            {
                var parts = new List<string>();
                int count = 0;
                foreach (var item in enumerable)
                {
                    if (count++ >= 8)
                    {
                        parts.Add("...");
                        break;
                    }

                    parts.Add(TrimForReport(item?.ToString() ?? "null", 80));
                }

                return TrimForReport("[" + string.Join(", ", parts) + "]", maxLength);
            }

            return TrimForReport(value.ToString() ?? "", maxLength);
        }
        catch (Exception ex)
        {
            return $"failed: {ex.Message}";
        }
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

                report.AppendLine($"  State: visible={state.WindowVisible}, awaitingReveal={state.AwaitingRevealPrompt}, options={state.Options.Count}, context={FormatContext(state.ItemContext)}, score={ScoreWellState(state)}, cacheable={IsVisibleWellAnchorState(state)}");
                AppendCandidateRootButtonTextProbe(report, root);
                if (state.Options.Count < 3)
                {
                    AppendCandidateRootChoiceTextProbe(report, root);
                    AppendCandidateRootFixedPathProbe(report, root);
                }
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

    private void AppendCandidateRootButtonTextProbe(StringBuilder report, Element root)
    {
        try
        {
            bool revealButtonVisible = ElementSubtreeContainsExactVisibleText(root, ["Reveal"], 10, 320);
            bool confirmButtonVisible = ElementSubtreeContainsExactVisibleText(root, ["Confirm"], 10, 320);
            bool promptTextVisible = ElementSubtreeContainsText(root, ["Place an item with an Unrevealed Desecrated Modifier"], 8, 240);
            report.AppendLine($"  Buttons/text: revealButton={revealButtonVisible}, confirmButton={confirmButtonVisible}, promptText={promptTextVisible}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Buttons/text: failed: {ex.Message}");
        }
    }

    private void AppendCandidateRootChoiceTextProbe(StringBuilder report, Element root)
    {
        try
        {
            if (!TryGetChoiceTextFallbackArea(root, out var choiceArea, out var reason))
            {
                report.AppendLine($"  Choice-text probe: unavailable ({reason})");
                return;
            }

            if (!TryGetRect(root, out var rootRect) || !IsDrawableRect(rootRect))
            {
                report.AppendLine($"  Choice-text probe: unavailable (bad root rect)");
                return;
            }

            var localCandidates = CollectChoiceTextStackCandidates(root, choiceArea)
                .OrderBy(candidate => candidate.Rect.Y)
                .ThenBy(candidate => candidate.Rect.X)
                .Take(8)
                .ToList();
            var screenCandidates = CanUseScreenLocalChoiceFallback(root)
                ? CollectChoiceTextStackScreenCandidates(choiceArea, rootRect)
                    .OrderBy(candidate => candidate.Rect.Y)
                    .ThenBy(candidate => candidate.Rect.X)
                    .Take(8)
                    .ToList()
                : new List<TextCandidate>();
            var knownListOptions = ReadOptionsFromKnownOptionList(root)
                .OrderBy(option => option.Rect.Y)
                .ThenBy(option => option.Rect.X)
                .Take(8)
                .ToList();

            report.AppendLine($"  Choice-text probe: area={FormatRect(choiceArea)}, localCandidates={localCandidates.Count}, screenCandidates={screenCandidates.Count}, knownListOptions={knownListOptions.Count}");
            foreach (var candidate in localCandidates.Take(5))
                report.AppendLine($"    local text: {TrimForReport(candidate.Text, 120)} | path={candidate.Path} | rect={FormatRect(candidate.Rect)}");
            foreach (var candidate in screenCandidates.Take(5))
                report.AppendLine($"    screen text: {TrimForReport(candidate.Text, 120)} | path={candidate.Path} | rect={FormatRect(candidate.Rect)}");
            foreach (var option in knownListOptions.Take(5))
                report.AppendLine($"    known-list option: {TrimForReport(option.Text, 120)} | path={option.Path} | rect={FormatRect(option.Rect)}");
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Choice-text probe failed: {ex.Message}");
        }
    }


    private void AppendCandidateRootFixedPathProbe(StringBuilder report, Element root)
    {
        try
        {
            int[][] optionPaths =
            [
                [4, 0, 0, 0],
                [4, 0, 1, 0],
                [4, 0, 2, 0]
            ];

            bool hasChoiceArea = TryGetChoiceTextFallbackArea(root, out var choiceArea, out _);
            report.AppendLine("  Fixed-path probe:");
            for (int index = 0; index < optionPaths.Length; index++)
            {
                var path = optionPaths[index];
                var container = FollowChildChain(root, path);
                if (container == null || container.Address == 0)
                {
                    report.AppendLine($"    {string.Join(",", path)}: missing");
                    continue;
                }

                var textElement = FindChoiceTextElement(container);
                var hiddenTextElement = textElement == null ? FindChoiceTextElementIncludingNonVisibleText(container) : null;
                var inspectedTextElement = textElement ?? hiddenTextElement;
                bool hiddenOnly = textElement == null && hiddenTextElement != null;
                string acceptedText = string.Empty;
                string acceptedRaw = string.Empty;
                RectangleF textRect = default;
                bool hasTextRect = false;
                if (inspectedTextElement != null)
                {
                    (acceptedText, acceptedRaw) = ReadElementText(inspectedTextElement);
                    hasTextRect = TryGetRect(inspectedTextElement, out textRect) && IsDrawableRect(textRect);
                }

                bool slotMatches = !hasChoiceArea || !hasTextRect || ChoiceTextRectMatchesFixedPathSlot(index, textRect, choiceArea);
                bool accepted = !hiddenOnly &&
                                hasTextRect &&
                                IsAcceptedWellChoiceText(acceptedText, textRect, choiceArea, hasChoiceArea) &&
                                slotMatches;
                string rejectReason = accepted ? "" : ExplainRejectedChoiceText(acceptedText, textRect, choiceArea, hasChoiceArea, hasTextRect);
                if (hiddenOnly)
                    rejectReason = string.IsNullOrEmpty(rejectReason) || rejectReason == "unknown" ? "hidden-stale-text" : rejectReason + ";hidden-stale-text";
                if (!accepted && hasChoiceArea && hasTextRect && !slotMatches)
                    rejectReason = string.IsNullOrEmpty(rejectReason) || rejectReason == "unknown" ? "wrong-fixed-slot" : rejectReason + ";wrong-fixed-slot";
                report.AppendLine($"    {string.Join(",", path)}: container={FormatElement(container)} textElement={FormatElement(inspectedTextElement)} accepted={accepted} slotMatches={slotMatches} reject={rejectReason} text={TrimForReport(acceptedText, 140)} raw={TrimForReport(acceptedRaw, 140)}");
            }
        }
        catch (Exception ex)
        {
            report.AppendLine($"  Fixed-path probe failed: {ex.Message}");
        }
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

    private void PreserveOrUpdateItemContext(ItemSnapshot candidate, DateTime now)
    {
        if (_itemContext == null)
        {
            _itemContext = candidate;
            return;
        }

        if (SameWellItemContext(candidate, _itemContext))
        {
            _itemContext = MergePreferRicherContext(_itemContext, candidate);
            return;
        }

        if (now <= _activeWellTransitionUntil && ContextRichness(_itemContext) >= ContextRichness(candidate))
            return;

        _itemContext = candidate;
    }

    private static ItemSnapshot MergePreferRicherContext(ItemSnapshot current, ItemSnapshot candidate)
        => new()
        {
            Source = FirstNonEmpty(candidate.Source, current.Source),
            BaseName = PreferNonEmpty(candidate.BaseName, current.BaseName),
            UniqueName = PreferNonEmpty(candidate.UniqueName, current.UniqueName),
            ClassName = PreferNonEmpty(candidate.ClassName, current.ClassName),
            ItemLevel = candidate.ItemLevel > 0 ? candidate.ItemLevel : current.ItemLevel
        };

    private static string PreferNonEmpty(string preferred, string fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static int ContextRichness(ItemSnapshot? item)
    {
        if (item == null)
            return 0;

        int score = 0;
        if (!string.IsNullOrWhiteSpace(item.UniqueName))
            score += 4;
        if (!string.IsNullOrWhiteSpace(item.BaseName))
            score += 4;
        if (!string.IsNullOrWhiteSpace(item.ClassName))
            score += 2;
        if (item.ItemLevel > 0)
            score += 1;
        return score;
    }

    private List<WellDrawInfo> BuildDrawInfos(IReadOnlyList<WellOption> options, ItemSnapshot? item)
    {
        if (options.Count == 0)
            return [];

        return options
            .Where(option => IsSafeOptionForDrawing(option))
            .Select(option => new WellDrawInfo(option, ResolveOptionTier(option, item)))
            .ToList();
    }

    private WellOfSoulsTierResult ResolveOptionTier(WellOption option, ItemSnapshot? item)
    {
        var textResult = _resolver.Resolve(item, option.Text);
        if (textResult.Known || string.IsNullOrWhiteSpace(option.ModKey))
            return textResult;

        var modKeyResult = _resolver.ResolveByModKey(item, option.ModKey, option.Text);
        return modKeyResult.Known ? modKeyResult : textResult;
    }

    private static bool IsSafeOptionForDrawing(WellOption option)
        => IsDrawableRect(option.Rect) &&
           SafeVisible(option.Element) &&
           SafeVisible(option.TextElement) &&
           IsWellRowOptionText(option.Text) &&
           !LooksLikeTooltipOrItemText(option.Text);

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
            var anchor = GetTierBadgeAnchor(option, drawInfos);
            var box = anchor.HasRowRect
                ? PickTierBadgeRectInsideRow(anchor.RowRect, anchor.TextRect, width, height)
                : PickTierBadgeRect(anchor.TextRect, width, height);

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

            if (tiers[i].Legacy)
                segments.Add(Segment("Legacy ", MutedTextColor()));

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

    private WellState ReadWellState(bool allowBroadSearch, bool updateCache = true)
    {
        try
        {
            var candidates = new List<WellRootCandidate>();
            var seenRoots = new HashSet<long>();
            int order = 0;

            var cachedRoot = _cachedWellRoot;
            TryAddCandidateRoot(cachedRoot, "cached");
            foreach (var root in GetCachedRootNeighborhood(cachedRoot))
                TryAddCandidateRoot(root, "cached-neighborhood");

            foreach (var root in GetLikelyWellRoots())
                TryAddCandidateRoot(root, "likely-root");

            if (allowBroadSearch)
            {
                var hits = FindWellElements(12, 80);
                var candidateRoots = FindCandidateRoots(hits).Take(12).ToList();
                foreach (var root in candidateRoots)
                    TryAddCandidateRoot(root, "broad-root");
            }

            if (updateCache)
                _cachedWellRoot = null;

            var best = SelectBestWellCandidate(candidates);
            if (best == null)
                return new WellState([], null);

            var bestState = AttachBestCandidateContext(best.State, candidates);
            if (updateCache && IsVisibleWellAnchorState(bestState))
                _cachedWellRoot = best.Root;

            return AttachAvailableWellContext(bestState);

            void TryAddCandidateRoot(Element? root, string source)
            {
                if (root == null || root.Address == 0)
                    return;

                if (!seenRoots.Add((long)root.Address))
                    return;

                var state = TryReadWellStateFromRoot(root);
                if (state == null)
                    return;

                candidates.Add(new WellRootCandidate(root, state, source, order++));
            }
        }
        catch (Exception ex)
        {
            LogDebugFailureLimited("read Well of Souls state", ex);
        }

        return new WellState([], null);
    }

    private WellRootCandidate? SelectBestWellCandidate(IReadOnlyList<WellRootCandidate> candidates)
    {
        WellRootCandidate? best = null;
        int bestScore = int.MinValue;

        foreach (var candidate in candidates)
        {
            if (IsPartialFromDifferentKnownItem(candidate.State))
                continue;

            int score = ScoreWellState(candidate.State);
            if (best == null || score > bestScore || (score == bestScore && candidate.Order < best.Order))
            {
                best = candidate;
                bestScore = score;
            }
        }

        return best;
    }

    private int ScoreWellState(WellState state)
    {
        if (!state.WindowVisible)
            return -10000;

        int score = 0;
        if (state.Options.Count >= 3)
            score += 10000;
        else if (state.Options.Count > 0)
            score += 2000 + state.Options.Count * 100;

        if (state.AwaitingRevealPrompt)
            score += 1000;

        // Reveal/Confirm buttons prove this is an active Well window even when the
        // choice text is temporarily exposed through a sibling subtree. Prefer these
        // roots over item-tooltip-only roots that merely contain "Well of Souls" text.
        if (state.ConfirmButtonVisible)
            score += 700;
        else if (state.RevealButtonVisible)
            score += 400;

        if (state.PromptTextVisible && (state.RevealButtonVisible || state.ConfirmButtonVisible))
            score += 50;

        if (state.ItemContext != null)
            score += 200 + ContextRichness(state.ItemContext) * 20;

        if (state.ItemContext != null && _itemContext != null && SameWellItemContext(state.ItemContext, _itemContext))
            score += 300;

        if (state.Options.Count == 0 && !state.AwaitingRevealPrompt && state.ItemContext != null)
            score -= 500;

        return score;
    }

    private WellState AttachBestCandidateContext(WellState state, IReadOnlyList<WellRootCandidate> candidates)
    {
        if (state.ItemContext != null || state.Options.Count == 0)
            return state;

        var context = candidates
            .Where(candidate => candidate.State.ItemContext != null)
            .Where(candidate => candidate.State.AwaitingRevealPrompt || candidate.State.Options.Count > 0)
            .Where(candidate => !IsPartialFromDifferentKnownItem(candidate.State))
            .Where(candidate => _itemContext == null ||
                                SameWellItemContext(candidate.State.ItemContext, _itemContext) ||
                                DateTime.UtcNow > _activeWellTransitionUntil)
            .Select(candidate => candidate.State.ItemContext!)
            .OrderByDescending(ContextRichness)
            .FirstOrDefault();

        return context == null
            ? state
            : state with { ItemContext = context };
    }

    private bool IsPartialFromDifferentKnownItem(WellState state)
        => _itemContext != null &&
           state.Options.Count is > 0 and < 3 &&
           state.ItemContext != null &&
           !SameWellItemContext(state.ItemContext, _itemContext);

    private IEnumerable<Element> GetCachedRootNeighborhood(Element? root)
    {
        var result = new List<Element>();
        if (root == null || root.Address == 0)
            return result;

        bool hasCachedRect = TryGetRect(root, out var cachedRect) && IsDrawableRect(cachedRect);
        var seen = new HashSet<long>();
        var parent = TryGetElementProperty(root, "Parent");

        for (int depth = 0; depth < 2 && parent != null && parent.Address != 0; depth++)
        {
            IEnumerable<Element> children;
            try { children = parent.Children; }
            catch { break; }

            foreach (var child in children)
            {
                if (child == null || child.Address == 0 || !seen.Add((long)child.Address))
                    continue;

                if (!IsPlausibleWellWindowCandidate(child))
                    continue;

                if (hasCachedRect && TryGetRect(child, out var childRect) && IsDrawableRect(childRect) && !IsSameOrOverlappingWellRect(cachedRect, childRect))
                    continue;

                result.Add(child);
            }

            parent = TryGetElementProperty(parent, "Parent");
        }

        return result;
    }

    private static bool IsSameOrOverlappingWellRect(RectangleF a, RectangleF b)
    {
        if (!IsDrawableRect(a) || !IsDrawableRect(b))
            return true;

        float dx = Math.Abs(a.X - b.X);
        float dy = Math.Abs(a.Y - b.Y);
        float dw = Math.Abs(a.Width - b.Width);
        float dh = Math.Abs(a.Height - b.Height);
        if (dx <= 80f && dy <= 80f && dw <= 120f && dh <= 120f)
            return true;

        float left = Math.Max(a.Left, b.Left);
        float top = Math.Max(a.Top, b.Top);
        float right = Math.Min(a.Right, b.Right);
        float bottom = Math.Min(a.Bottom, b.Bottom);
        float overlap = Math.Max(0f, right - left) * Math.Max(0f, bottom - top);
        float smaller = Math.Min(Area(a), Area(b));
        return smaller > 0f && overlap / smaller >= 0.65f;
    }

    private static bool IsVisibleWellAnchorState(WellState state)
        => state.WindowVisible &&
           (state.AwaitingRevealPrompt ||
            state.Options.Count > 0 ||
            state.RevealButtonVisible ||
            state.ConfirmButtonVisible);

    private static bool HasCompleteOptions(WellState state)
        => state.Options.Count >= 3;

    private static bool HasPartialOptions(WellState state)
        => state.Options.Count is > 0 and < 3;

    private WellState AttachAvailableWellContext(WellState state)
    {
        if (state.ItemContext != null || state.Options.Count == 0)
            return state;

        if (_itemContext != null && DateTime.UtcNow <= _activeWellTransitionUntil)
            return state with { ItemContext = _itemContext };

        return state;
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

    private WellState? TryReadWellStateFromRoot(Element? root)
    {
        if (root == null ||
            !IsPlausibleWellWindowCandidate(root) ||
            !ElementSubtreeContainsText(root, ["The Well of Souls"], 8, 240))
            return null;

        var context = BuildWellItemContext(root);
        var options = ReadOptionsFromFixedPaths(root);

        bool revealButtonVisible = ElementSubtreeContainsExactVisibleText(root, ["Reveal"], 10, 320);
        bool confirmButtonVisible = ElementSubtreeContainsExactVisibleText(root, ["Confirm"], 10, 320);
        bool promptTextVisible = ElementSubtreeContainsText(root, ["Place an item with an Unrevealed Desecrated Modifier"], 8, 240);

        // The instructional prompt text can remain in the subtree after options/Confirm are visible.
        // Treat "awaiting reveal" as the specific button state, not merely the presence of the prompt text.
        bool awaitingRevealPrompt = options.Count == 0 &&
            promptTextVisible &&
            revealButtonVisible &&
            !confirmButtonVisible;

        return new WellState(options, context, awaitingRevealPrompt, true, revealButtonVisible, confirmButtonVisible, promptTextVisible);
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

        bool sameBase = !string.IsNullOrWhiteSpace(left.BaseName) &&
                        left.BaseName.Equals(right.BaseName, StringComparison.OrdinalIgnoreCase);
        bool sameUnique = !string.IsNullOrWhiteSpace(left.UniqueName) &&
                          left.UniqueName.Equals(right.UniqueName, StringComparison.OrdinalIgnoreCase);
        if (!sameBase && !sameUnique)
            return false;

        if (!string.IsNullOrWhiteSpace(left.ClassName) &&
            !string.IsNullOrWhiteSpace(right.ClassName) &&
            !left.ClassName.Equals(right.ClassName, StringComparison.OrdinalIgnoreCase))
            return false;

        if (left.ItemLevel > 0 && right.ItemLevel > 0 && left.ItemLevel != right.ItemLevel)
            return false;

        return true;
    }

    private List<WellOption> ReadOptionsFromFixedPaths(Element root)
    {
        if (!ElementSubtreeContainsText(root, ["Confirm"], 8, 240))
            return [];

        bool hasChoiceArea = TryGetChoiceTextFallbackArea(root, out var choiceArea, out _);

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
            if (container == null || !SafeVisible(container))
                continue;

            if (hasChoiceArea &&
                TryGetRect(container, out var containerRect) &&
                IsDrawableRect(containerRect) &&
                !RectCenterInside(ExpandRect(choiceArea, 72f), containerRect))
            {
                continue;
            }

            var textElement = FindChoiceTextElement(container);
            if (textElement == null)
                continue;

            if (!TryGetRect(textElement, out var textRect) || !IsDrawableRect(textRect))
                continue;

            var (text, rawText) = ReadElementText(textElement);
            if (!IsAcceptedWellChoiceText(text, textRect, choiceArea, hasChoiceArea))
                continue;

            if (hasChoiceArea && !ChoiceTextRectMatchesFixedPathSlot(index, textRect, choiceArea))
                continue;

            var option = new WellOption(
                index + 1,
                string.Join(",", path),
                container,
                textElement,
                NormalizeWhitespace(text),
                NormalizeWhitespace(rawText),
                textRect);
            options.Add(option);
        }

        if (options.Count < 3)
            options = MergeOptions(options, ReadOptionsFromKnownOptionList(root));

        if (options.Count < 3)
            options = MergeOptions(options, SearchOptionsInChoiceArea(root, useScreenFallback: false));

        if (options.Count < 3)
            options = MergeOptions(options, SearchOptionsInChoiceTextStack(root));

        if (options.Count < 3)
            options = MergeOptions(options, SearchOptionsInChoiceArea(root, useScreenFallback: true));

        return options
            .OrderBy(option => option.Rect.Y)
            .ThenBy(option => option.Rect.X)
            .Select((option, index) => option with { Index = index + 1 })
            .ToList();
    }

    private static List<WellOption> MergeOptions(IReadOnlyList<WellOption> primary, IReadOnlyList<WellOption> fallback)
    {
        var result = new List<WellOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddRange(primary);
        AddRange(fallback);
        return result
            .OrderBy(option => option.Rect.Y)
            .ThenBy(option => option.Rect.X)
            .Take(3)
            .Select((option, index) => option with { Index = index + 1 })
            .ToList();

        void AddRange(IEnumerable<WellOption> options)
        {
            foreach (var option in options)
            {
                if (!IsWellRowOptionText(option.Text) || LooksLikeTooltipOrItemText(option.Text))
                    continue;

                string key = NormalizeWhitespace(option.Text);
                if (seen.Add(key))
                    result.Add(option);
            }
        }
    }

    private List<WellOption> ReadOptionsFromKnownOptionList(Element root)
    {
        if (!TryGetChoiceTextFallbackArea(root, out var choiceArea, out _))
            return [];

        if (!TryGetRect(root, out var rootRect) || !IsDrawableRect(rootRect))
            return [];

        var optionList = FollowChildChain(root, [4, 0]);
        if (optionList == null || !SafeVisible(optionList))
            return [];

        var matches = new List<ChoiceRowMatch>();
        foreach (var candidate in CollectVisibleElementCandidates(optionList, maxDepth: 8, maxNodes: 1400))
        {
            if (!LooksLikeChoiceRowCandidate(rootRect, choiceArea, candidate.Rect))
                continue;

            if (!TryBuildChoiceRowMatch(candidate.Element, $"known-list:{candidate.Path}", candidate.Rect, choiceArea, out var match))
                continue;

            matches.Add(match);
        }

        return matches
            .GroupBy(match => NormalizeWhitespace(match.Text), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(match => Math.Abs(CenterX(match.TextRect) - (choiceArea.Left + choiceArea.Width * 0.5f)))
                .ThenBy(match => Area(match.RowRect))
                .First())
            .OrderBy(match => match.TextRect.Y)
            .ThenBy(match => match.TextRect.X)
            .Take(3)
            .Select((match, index) => new WellOption(
                index + 1,
                match.Path,
                match.RowElement,
                match.TextElement,
                match.Text,
                match.RawText,
                match.TextRect))
            .ToList();
    }

    private bool TryBuildChoiceRowMatch(Element rowElement, string path, RectangleF rowRect, RectangleF choiceArea, out ChoiceRowMatch match)
    {
        match = null!;

        var textElement = FindChoiceTextElementRelaxed(rowElement, rowRect);
        if (textElement != null)
        {
            var (text, rawText) = ReadElementText(textElement);
            if (TryGetRect(textElement, out var textRect) &&
                IsDrawableRect(textRect) &&
                IsAcceptedWellChoiceText(text, textRect, choiceArea, requireChoiceArea: true))
            {
                match = new ChoiceRowMatch(
                    rowElement,
                    textElement,
                    path,
                    NormalizeWhitespace(text),
                    NormalizeWhitespace(rawText),
                    rowRect,
                    textRect);
                return true;
            }
        }

        if (TryReadMergedChoiceText(rowElement, rowRect, choiceArea, out var merged))
        {
            match = new ChoiceRowMatch(
                rowElement,
                merged.Element,
                path + ":merged",
                merged.Text,
                merged.RawText,
                rowRect,
                merged.Rect);
            return true;
        }

        return false;
    }

    private static bool TryReadMergedChoiceText(Element rowElement, RectangleF rowRect, RectangleF choiceArea, out TextCandidate merged)
    {
        merged = null!;
        if (!IsDrawableRect(rowRect))
            return false;

        var expandedRow = ExpandRect(rowRect, 28f);
        var fragments = CollectTextCandidates(rowElement, maxDepth: 18, maxNodes: 1200)
            .Where(candidate => IsChoiceAreaCandidate(expandedRow, candidate.Rect))
            .Where(candidate => IsChoiceAreaCandidate(ExpandRect(choiceArea, 24f), candidate.Rect))
            .Where(candidate => IsPotentialChoiceTextFragment(candidate.Text))
            .OrderBy(candidate => candidate.Rect.Y)
            .ThenBy(candidate => candidate.Rect.X)
            .ToList();

        if (fragments.Count == 0)
            return false;

        var direct = fragments
            .Where(candidate => IsAcceptedWellChoiceText(candidate.Text, candidate.Rect, choiceArea, requireChoiceArea: true))
            .OrderByDescending(candidate => candidate.Text.Length)
            .ThenByDescending(candidate => candidate.Rect.Width)
            .FirstOrDefault();
        if (direct != null)
        {
            merged = direct;
            return true;
        }

        var pieces = new List<TextCandidate>();
        foreach (var fragment in fragments)
        {
            string text = NormalizeWhitespace(fragment.Text);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            // Avoid doubling text when both a parent and its word/phrase children are exposed.
            if (pieces.Any(existing => TextContains(existing.Text, text) && Area(existing.Rect) >= Area(fragment.Rect)))
                continue;

            pieces.RemoveAll(existing => TextContains(text, existing.Text) && Area(fragment.Rect) >= Area(existing.Rect));
            pieces.Add(fragment with { Text = text, RawText = NormalizeWhitespace(fragment.RawText) });
        }

        pieces = pieces
            .OrderBy(piece => piece.Rect.Y)
            .ThenBy(piece => piece.Rect.X)
            .Take(24)
            .ToList();

        if (pieces.Count == 0)
            return false;

        var rect = pieces.Select(piece => piece.Rect).Aggregate(UnionRect);
        string combined = NormalizeWhitespace(string.Join(" ", pieces.Select(piece => piece.Text)));
        if (!IsAcceptedWellChoiceText(combined, rect, choiceArea, requireChoiceArea: true))
            return false;

        var element = pieces.First().Element;
        string raw = NormalizeWhitespace(string.Join(" ", pieces.Select(piece => string.IsNullOrWhiteSpace(piece.RawText) ? piece.Text : piece.RawText)));
        merged = new TextCandidate(element, "merged-row-text", combined, raw, rect);
        return true;
    }

    private static bool IsPotentialChoiceTextFragment(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string normalized = NormalizeWhitespace(text);
        if (normalized.Length < 1 || normalized.Length > 220)
            return false;

        if (LooksLikeTooltipOrItemText(normalized))
            return false;

        if (ContainsAnyText(normalized,
            [
                "Confirm", "Reveal", "Options", "Place an item", "Unrevealed Desecrated Modifier",
                "The Well of Souls", "Heart of the Well", "Prefix", "Suffix", "Tier", "Current", "Best"
            ]))
            return false;

        return normalized.Any(char.IsLetterOrDigit) ||
               normalized.Contains('%') ||
               normalized.StartsWith("+", StringComparison.Ordinal) ||
               normalized.StartsWith("-", StringComparison.Ordinal);
    }

    private static bool TextContains(string haystack, string needle)
    {
        haystack = NormalizeWhitespace(haystack);
        needle = NormalizeWhitespace(needle);
        return needle.Length > 0 && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static RectangleF UnionRect(RectangleF a, RectangleF b)
    {
        float left = Math.Min(a.Left, b.Left);
        float top = Math.Min(a.Top, b.Top);
        float right = Math.Max(a.Right, b.Right);
        float bottom = Math.Max(a.Bottom, b.Bottom);
        return new RectangleF(left, top, right - left, bottom - top);
    }

    private List<WellOption> SearchOptionsInChoiceArea(Element root, bool useScreenFallback)
    {
        // The old screen-wide fallback could pick text from overlapping item tooltips.
        // Keep normal recovery root-local and row/container-driven; broad screen text is diagnostics-only now.
        if (useScreenFallback)
            return [];

        return ReadOptionsFromChoiceRows(root);
    }

    private List<WellOption> SearchOptionsInChoiceTextStack(Element root)
    {
        if (!TryGetChoiceTextFallbackArea(root, out var choiceArea, out _))
            return [];

        if (!TryGetRect(root, out var rootRect) || !IsDrawableRect(rootRect))
            return [];

        var candidates = CollectChoiceTextStackCandidates(root, choiceArea);
        var rows = PickBestChoiceTextStack(candidates, choiceArea);

        // Heart multi-reveal can leave the visible choice text in a sibling subtree
        // while the Well title/Confirm button remain under the cached root. Recover
        // by scanning only the same computed Well choice rectangle, never the whole
        // screen as accepted option text.
        if (rows.Count != 3 && CanUseScreenLocalChoiceFallback(root))
        {
            var screenCandidates = CollectChoiceTextStackScreenCandidates(choiceArea, rootRect);
            rows = PickBestChoiceTextStack(screenCandidates, choiceArea);
        }

        if (rows.Count != 3)
            return [];

        return rows
            .Select((candidate, index) => new WellOption(
                index + 1,
                $"text-fallback:{candidate.Path}",
                candidate.Element,
                candidate.Element,
                candidate.Text,
                candidate.RawText,
                candidate.Rect))
            .ToList();
    }

    private static bool CanUseScreenLocalChoiceFallback(Element root)
        => ElementSubtreeContainsExactVisibleText(root, ["Confirm"], 10, 320) &&
           !ElementSubtreeContainsExactVisibleText(root, ["Reveal"], 10, 320);

    private bool TryGetChoiceTextFallbackArea(Element root, out RectangleF choiceArea, out string reason)
    {
        choiceArea = default;
        reason = string.Empty;

        if (!TryGetRect(root, out var rootRect) || !IsDrawableRect(rootRect))
        {
            reason = "bad root rect";
            return false;
        }

        var confirmCandidates = CollectTextCandidates(root, maxDepth: 14, maxNodes: 1800)
            .Where(candidate => IsExactText(candidate.Text, "Confirm"))
            .ToList();

        if (confirmCandidates.Count == 0)
        {
            reason = "no visible Confirm button";
            return false;
        }

        float confirmTop = confirmCandidates.Min(candidate => candidate.Rect.Top);
        float choiceTop = rootRect.Top + rootRect.Height * 0.42f;
        float choiceBottom = confirmTop - 6f;
        if (choiceBottom <= choiceTop + 24f)
        {
            reason = "choice area collapsed";
            return false;
        }

        choiceArea = new RectangleF(
            rootRect.Left + rootRect.Width * 0.04f,
            choiceTop,
            rootRect.Width * 0.88f,
            choiceBottom - choiceTop);

        return true;
    }

    private List<TextCandidate> CollectChoiceTextStackCandidates(Element root, RectangleF choiceArea)
    {
        if (!TryGetRect(root, out var rootRect) || !IsDrawableRect(rootRect))
            return [];

        return FilterChoiceTextStackCandidates(
            CollectTextCandidates(root, maxDepth: 14, maxNodes: 3200),
            choiceArea,
            rootRect,
            pathPrefix: string.Empty);
    }

    private List<TextCandidate> CollectChoiceTextStackScreenCandidates(RectangleF choiceArea, RectangleF rootRect)
        => FilterChoiceTextStackCandidates(
            CollectScreenTextCandidates(choiceArea),
            choiceArea,
            rootRect,
            pathPrefix: "screen:");

    private static List<TextCandidate> FilterChoiceTextStackCandidates(
        IEnumerable<TextCandidate> source,
        RectangleF choiceArea,
        RectangleF rootRect,
        string pathPrefix)
    {
        var candidates = new List<TextCandidate>();
        foreach (var candidate in source)
        {
            if (!IsChoiceAreaCandidate(choiceArea, candidate.Rect))
                continue;

            if (!IsChoiceTextStackOptionText(candidate.Text))
                continue;

            if (LooksLikeTooltipOrItemText(candidate.Text))
                continue;

            if (candidate.Rect.Width < 120f || candidate.Rect.Width > rootRect.Width * 0.90f)
                continue;

            if (candidate.Rect.Height < 20f || candidate.Rect.Height > 145f)
                continue;

            candidates.Add(string.IsNullOrEmpty(pathPrefix)
                ? candidate
                : candidate with { Path = pathPrefix + candidate.Path });
        }

        return candidates
            .GroupBy(candidate => NormalizeWhitespace(candidate.Text), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderBy(candidate => candidate.Rect.Width * candidate.Rect.Height).First())
            .ToList();
    }

    private static List<TextCandidate> PickBestChoiceTextStack(IReadOnlyList<TextCandidate> candidates, RectangleF choiceArea)
    {
        if (candidates.Count < 3)
            return [];

        var ordered = candidates
            .OrderBy(candidate => candidate.Rect.Y)
            .ThenBy(candidate => candidate.Rect.X)
            .Take(18)
            .ToList();

        List<TextCandidate>? best = null;
        float bestScore = float.MaxValue;

        for (int i = 0; i < ordered.Count - 2; i++)
        {
            for (int j = i + 1; j < ordered.Count - 1; j++)
            {
                for (int k = j + 1; k < ordered.Count; k++)
                {
                    var trio = new List<TextCandidate> { ordered[i], ordered[j], ordered[k] };
                    if (!ChoiceTextRowsLookStacked(trio, choiceArea))
                        continue;

                    float c0 = CenterY(trio[0].Rect);
                    float c1 = CenterY(trio[1].Rect);
                    float c2 = CenterY(trio[2].Rect);
                    float d1 = c1 - c0;
                    float d2 = c2 - c1;
                    float centerSpread = trio.Max(candidate => CenterX(candidate.Rect)) - trio.Min(candidate => CenterX(candidate.Rect));
                    float score = Math.Abs(d1 - d2) * 2f +
                                  centerSpread +
                                  Math.Abs((d1 + d2) * 0.5f - choiceArea.Height * 0.28f) +
                                  Math.Abs(CenterX(trio[1].Rect) - (choiceArea.Left + choiceArea.Width * 0.5f)) * 0.25f;

                    if (score < bestScore)
                    {
                        best = trio;
                        bestScore = score;
                    }
                }
            }
        }

        return best ?? [];
    }

    private static bool ChoiceTextRowsLookStacked(IReadOnlyList<TextCandidate> rows, RectangleF choiceArea)
    {
        if (rows.Count != 3)
            return false;

        float c0 = CenterY(rows[0].Rect);
        float c1 = CenterY(rows[1].Rect);
        float c2 = CenterY(rows[2].Rect);
        float d1 = c1 - c0;
        float d2 = c2 - c1;

        if (d1 < 45f || d2 < 45f || d1 > choiceArea.Height * 0.55f || d2 > choiceArea.Height * 0.55f)
            return false;

        float centerSpread = rows.Max(row => CenterX(row.Rect)) - rows.Min(row => CenterX(row.Rect));
        if (centerSpread > choiceArea.Width * 0.35f)
            return false;

        return true;
    }

    private static bool ChoiceTextRectMatchesFixedPathSlot(int zeroBasedIndex, RectangleF textRect, RectangleF choiceArea)
    {
        if (!IsDrawableRect(textRect) || !IsDrawableRect(choiceArea) || choiceArea.Height <= 1f)
            return true;

        float normalizedCenterY = (CenterY(textRect) - choiceArea.Top) / choiceArea.Height;
        return zeroBasedIndex switch
        {
            0 => normalizedCenterY >= 0.18f && normalizedCenterY <= 0.48f,
            1 => normalizedCenterY >= 0.42f && normalizedCenterY <= 0.73f,
            2 => normalizedCenterY >= 0.64f && normalizedCenterY <= 0.98f,
            _ => true
        };
    }

    private static float CenterX(RectangleF rect)
        => rect.X + rect.Width * 0.5f;

    private static float CenterY(RectangleF rect)
        => rect.Y + rect.Height * 0.5f;

    private static bool LooksLikeTooltipOrItemText(string text)
    {
        string normalized = NormalizeWhitespace(text);
        if (normalized.Equals("Heart of the Well Diamond", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Heart of the Well", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Diamond", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("Jewel", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsAnyText(normalized,
        [
            "Heart of the Well",
            "Limited to:",
            "Limited to",
            "Item Level",
            "Requires Level",
            "Quality:",
            "Countless souls",
            "Place into",
            "Right click",
            "Take this item",
            "Desecrated Modifier",
            "Unrevealed Desecrated Modifier",
            "The Well of Souls"
        ]);
    }

    private List<WellOption> ReadOptionsFromChoiceRows(Element root)
    {
        if (!TryGetRect(root, out var rootRect) || !IsDrawableRect(rootRect))
            return [];

        var rootTextCandidates = CollectTextCandidates(root, maxDepth: 14, maxNodes: 1600);
        var confirmCandidates = rootTextCandidates
            .Where(candidate => ContainsAnyText(candidate.Text, ["Confirm"]))
            .ToList();
        if (confirmCandidates.Count == 0)
            return [];

        float confirmTop = confirmCandidates.Min(candidate => candidate.Rect.Top);
        float choiceTop = rootRect.Top + rootRect.Height * 0.25f;
        float choiceBottom = confirmTop - 6f;
        if (choiceBottom <= choiceTop + 24f)
            return [];

        var choiceArea = new RectangleF(
            rootRect.Left + rootRect.Width * 0.02f,
            choiceTop,
            rootRect.Width * 0.96f,
            choiceBottom - choiceTop);

        var rowMatches = new List<ChoiceRowMatch>();
        foreach (var rowCandidate in CollectVisibleElementCandidates(root, maxDepth: 14, maxNodes: 1600))
        {
            if (!LooksLikeChoiceRowCandidate(rootRect, choiceArea, rowCandidate.Rect))
                continue;

            if (!TryBuildChoiceRowMatch(rowCandidate.Element, rowCandidate.Path, rowCandidate.Rect, choiceArea, out var match))
                continue;

            rowMatches.Add(match);
        }

        var uniqueRows = rowMatches
            .GroupBy(match => (long)match.TextElement.Address)
            .Select(group => group
                .OrderBy(match => ChoiceRowContainerScore(match, rootRect))
                .First())
            .OrderByDescending(match => match.RowRect.Y)
            .Take(3)
            .OrderBy(match => match.RowRect.Y)
            .ToList();

        if (uniqueRows.Count != 3 || !ChoiceRowsLookStacked(uniqueRows, rootRect))
            return [];

        return uniqueRows
            .Select((match, index) => new WellOption(
                index + 1,
                $"row-fallback:{match.Path}",
                match.RowElement,
                match.TextElement,
                match.Text,
                match.RawText,
                match.RowRect))
            .ToList();
    }

    private static bool LooksLikeChoiceRowCandidate(RectangleF rootRect, RectangleF choiceArea, RectangleF rowRect)
    {
        if (!IsDrawableRect(rowRect))
            return false;

        if (!RectCenterInside(choiceArea, rowRect))
            return false;

        if (rowRect.Width < rootRect.Width * 0.35f)
            return false;

        if (rowRect.Height < 26f || rowRect.Height > Math.Max(220f, rootRect.Height * 0.28f))
            return false;

        if (rowRect.Width > rootRect.Width * 0.98f && rowRect.Height > choiceArea.Height * 0.75f)
            return false;

        return true;
    }

    private static float ChoiceRowContainerScore(ChoiceRowMatch match, RectangleF rootRect)
    {
        float desiredWidth = rootRect.Width * 0.62f;
        float desiredHeight = Math.Clamp(rootRect.Height * 0.07f, 42f, 100f);
        return Math.Abs(match.RowRect.Width - desiredWidth) +
               Math.Abs(match.RowRect.Height - desiredHeight) * 2f +
               Area(match.RowRect) * 0.0001f;
    }

    private static bool ChoiceRowsLookStacked(IReadOnlyList<ChoiceRowMatch> rows, RectangleF rootRect)
    {
        if (rows.Count != 3)
            return false;

        for (int i = 1; i < rows.Count; i++)
        {
            float previousCenter = rows[i - 1].RowRect.Y + rows[i - 1].RowRect.Height * 0.5f;
            float currentCenter = rows[i].RowRect.Y + rows[i].RowRect.Height * 0.5f;
            if (currentCenter - previousCenter < 20f)
                return false;
        }

        float minCenterX = rows.Min(row => row.RowRect.X + row.RowRect.Width * 0.5f);
        float maxCenterX = rows.Max(row => row.RowRect.X + row.RowRect.Width * 0.5f);
        return maxCenterX - minCenterX <= rootRect.Width * 0.35f;
    }

    private List<TextCandidate> CollectScreenTextCandidates(RectangleF choiceArea)
    {
        var result = new List<TextCandidate>();
        var seen = new HashSet<long>();
        var ingameUi = GameController.Game.IngameState.IngameUi;

        AddCandidates(ingameUi);
        AddCandidates(ingameUi.Parent);

        return result;

        void AddCandidates(Element? root)
        {
            if (root == null || root.Address == 0)
                return;

            foreach (var candidate in CollectTextCandidates(root, maxDepth: 14, maxNodes: 6000))
            {
                if (!IsChoiceAreaCandidate(choiceArea, candidate.Rect))
                    continue;

                if (seen.Add((long)candidate.Element.Address))
                    result.Add(candidate);
            }
        }
    }

    private static bool IsChoiceAreaCandidate(RectangleF choiceArea, RectangleF rect)
    {
        if (!IsDrawableRect(rect))
            return false;

        float centerX = rect.X + rect.Width * 0.5f;
        float centerY = rect.Y + rect.Height * 0.5f;
        return centerX >= choiceArea.Left &&
               centerX <= choiceArea.Right &&
               centerY >= choiceArea.Top &&
               centerY <= choiceArea.Bottom;
    }

    private static List<ElementCandidate> CollectVisibleElementCandidates(Element root, int maxDepth, int maxNodes)
    {
        var result = new List<ElementCandidate>();
        int scanned = 0;
        Collect(root, "root", 0);
        return result;

        void Collect(Element element, string path, int depth)
        {
            if (depth > maxDepth || scanned++ >= maxNodes)
                return;

            if (SafeVisible(element) && TryGetRect(element, out var rect) && IsDrawableRect(rect))
                result.Add(new ElementCandidate(element, path, rect));

            if (depth == maxDepth)
                return;

            try
            {
                int index = 0;
                foreach (var child in element.Children)
                {
                    Collect(child, $"{path}.children[{index}]", depth + 1);
                    if (scanned >= maxNodes)
                        return;

                    index++;
                }
            }
            catch { }
        }
    }

    private static List<TextCandidate> CollectTextCandidates(Element root, int maxDepth, int maxNodes)
    {
        var result = new List<TextCandidate>();
        int scanned = 0;
        Collect(root, "root", 0);
        return result;

        void Collect(Element element, string path, int depth)
        {
            if (depth > maxDepth || scanned++ >= maxNodes)
                return;

            if (SafeVisible(element) && TryGetRect(element, out var rect) && IsDrawableRect(rect))
            {
                var strings = ReadStringishProperties(element);
                string text = strings.TryGetValue("TextNoTags", out var noTags) ? noTags : string.Join(" ", strings.Values);
                string rawText = strings.TryGetValue("Text", out var raw) ? raw : text;
                text = NormalizeWhitespace(text);
                if (!string.IsNullOrWhiteSpace(text))
                    result.Add(new TextCandidate(element, path, text, NormalizeWhitespace(rawText), rect));
            }

            if (depth == maxDepth)
                return;

            try
            {
                int index = 0;
                foreach (var child in element.Children)
                {
                    Collect(child, $"{path}.children[{index}]", depth + 1);
                    if (scanned >= maxNodes)
                        return;

                    index++;
                }
            }
            catch { }
        }
    }

    private static Element? FindChoiceTextElement(Element slot)
    {
        TryGetRect(slot, out var slotRect);
        return FindChoiceTextElementRelaxed(slot, slotRect);
    }

    private static Element? FindChoiceTextElementRelaxed(Element slot, RectangleF slotRect)
    {
        var queue = new Queue<Element>();
        queue.Enqueue(slot);
        int visited = 0;
        while (queue.Count > 0 && visited++ < 160)
        {
            var current = queue.Dequeue();
            if (SafeVisible(current))
            {
                var (text, _) = ReadElementText(current);
                if (IsWellRowOptionText(text))
                {
                    if (!IsDrawableRect(slotRect))
                        return current;

                    TryGetRect(current, out var textRect);
                    if (IsDrawableRect(textRect) && RectCenterInside(ExpandRect(slotRect, 16f), textRect))
                        return current;
                }
            }

            try
            {
                foreach (var child in current.Children)
                    queue.Enqueue(child);
            }
            catch { }
        }

        return null;
    }

    private static Element? FindChoiceTextElementIncludingNonVisibleText(Element slot)
    {
        TryGetRect(slot, out var slotRect);
        var queue = new Queue<Element>();
        queue.Enqueue(slot);
        int visited = 0;
        while (queue.Count > 0 && visited++ < 220)
        {
            var current = queue.Dequeue();
            if (TryGetRect(current, out var textRect) && IsDrawableRect(textRect))
            {
                var (text, _) = ReadElementText(current);
                if (IsWellRowOptionText(text) &&
                    (!IsDrawableRect(slotRect) || RectCenterInside(ExpandRect(slotRect, 24f), textRect)))
                {
                    return current;
                }
            }

            try
            {
                foreach (var child in current.Children)
                    queue.Enqueue(child);
            }
            catch { }
        }

        return null;
    }

    private static (string Text, string RawText) ReadElementText(Element element)
    {
        var strings = ReadStringishProperties(element);
        string text = strings.TryGetValue("TextNoTags", out var noTags) ? noTags : string.Join(" ", strings.Values);
        string rawText = strings.TryGetValue("Text", out var raw) ? raw : text;
        return (NormalizeWhitespace(text), NormalizeWhitespace(rawText));
    }

    private static RectangleF ExpandRect(RectangleF rect, float amount)
        => new(rect.X - amount, rect.Y - amount, rect.Width + amount * 2f, rect.Height + amount * 2f);

    private static bool RectCenterInside(RectangleF outer, RectangleF inner)
    {
        float x = inner.X + inner.Width * 0.5f;
        float y = inner.Y + inner.Height * 0.5f;
        return x >= outer.Left && x <= outer.Right && y >= outer.Top && y <= outer.Bottom;
    }

    private static bool TryGetRect(Element element, out RectangleF rect)
    {
        try
        {
            rect = element.GetClientRectCache;
            return true;
        }
        catch
        {
            rect = default;
            return false;
        }
    }

    private List<ElementSearchHit> FindWellElements(int maxDepth, int maxHits)
    {
        var primaryHits = FindWellElements(maxDepth, maxHits, WellPrimarySearchTerms);
        if (FindCandidateRoots(primaryHits).Count > 0)
            return primaryHits;

        var fallbackHits = FindWellElements(maxDepth, maxHits, WellFallbackSearchTerms);
        return MergeSearchHits(primaryHits, fallbackHits, maxHits);
    }

    private List<ElementSearchHit> FindWellElements(int maxDepth, int maxHits, IReadOnlyList<string> terms)
    {
        var hits = new List<ElementSearchHit>();
        var seenRoots = new HashSet<long>();

        foreach (var (name, root) in GetWellSearchRoots())
        {
            if (root == null || root.Address == 0 || !seenRoots.Add((long)root.Address))
                continue;

            FindWellElements(root, null, name, 0, maxDepth, maxHits, terms, hits);
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

    private static List<ElementSearchHit> MergeSearchHits(IEnumerable<ElementSearchHit> primaryHits, IEnumerable<ElementSearchHit> fallbackHits, int maxHits)
    {
        var merged = new List<ElementSearchHit>();
        var seen = new HashSet<long>();

        AddRange(primaryHits);
        AddRange(fallbackHits);
        return merged;

        void AddRange(IEnumerable<ElementSearchHit> hits)
        {
            foreach (var hit in hits)
            {
                if (merged.Count >= maxHits)
                    return;

                if (hit.Element.Address != 0 && !seen.Add((long)hit.Element.Address))
                    continue;

                merged.Add(hit);
                if (merged.Count >= maxHits)
                    return;
            }
        }
    }

    private static void FindWellElements(Element element, Element? parent, string path, int depth, int maxDepth, int maxHits, IReadOnlyList<string> terms, List<ElementSearchHit> hits)
    {
        if (depth > maxDepth || hits.Count >= maxHits)
            return;

        string text = string.Join(" ", ReadStringishProperties(element).Values);
        if (ContainsAnyText(text, terms))
            hits.Add(new ElementSearchHit(path.Split('.')[0], path, element, parent));

        if (depth == maxDepth)
            return;

        try
        {
            int index = 0;
            foreach (var child in element.Children)
            {
                FindWellElements(child, element, $"{path}.children[{index}]", depth + 1, maxDepth, maxHits, terms, hits);
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

    private static bool ElementSubtreeContainsExactVisibleText(Element root, IReadOnlyList<string> needles, int maxDepth, int maxNodes)
    {
        int scanned = 0;
        return Search(root, 0);

        bool Search(Element element, int depth)
        {
            if (depth > maxDepth || scanned++ >= maxNodes || !SafeVisible(element))
                return false;

            var (text, _) = ReadElementText(element);
            if (!string.IsNullOrWhiteSpace(text) && needles.Any(needle => IsExactText(text, needle)))
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

    private static bool IsExactText(string? text, string expected)
        => NormalizeWhitespace(text ?? string.Empty).Equals(NormalizeWhitespace(expected), StringComparison.OrdinalIgnoreCase);

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
        var type = obj.GetType();
        foreach (var name in interesting)
        {
            try
            {
                var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                    AddStringish(result, name, prop.GetValue(obj));
            }
            catch { }

            try
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    AddStringish(result, name + "Field", field.GetValue(obj));
            }
            catch { }
        }

        foreach (string methodName in new[] { "GetText", "GetTextNoTags", "GetTooltipText" })
        {
            try
            {
                var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method != null && method.ReturnType != typeof(void))
                    AddStringish(result, methodName, method.Invoke(obj, null));
            }
            catch { }
        }

        return result;
    }

    private static void AddStringish(Dictionary<string, string> result, string name, object? value)
    {
        if (value == null)
            return;

        if (value is string s)
        {
            AddString(result, name, s);
            return;
        }

        if (value is IEnumerable enumerable)
        {
            var parts = new List<string>();
            int count = 0;
            foreach (var item in enumerable)
            {
                if (count++ >= 24)
                    break;

                if (item == null)
                    continue;

                if (item is string itemString)
                {
                    if (!string.IsNullOrWhiteSpace(itemString))
                        parts.Add(itemString);
                }
                else if (item.GetType().IsPrimitive || item is decimal)
                {
                    parts.Add(item.ToString() ?? string.Empty);
                }
                else
                {
                    string raw = item.ToString() ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(raw) && raw.Length <= 80 && !LooksLikeTypeName(raw))
                        parts.Add(raw);
                }
            }

            AddString(result, name, string.Join(" ", parts));
            return;
        }

        string text = value.ToString() ?? string.Empty;
        if (!LooksLikeTypeName(text))
            AddString(result, name, text);
    }

    private static void AddString(Dictionary<string, string> result, string name, string value)
    {
        string normalized = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return;

        if (normalized.Length > 320)
            normalized = normalized[..320];

        if (!result.TryGetValue(name, out var existing) || normalized.Length > existing.Length)
            result[name] = normalized;
    }

    private static bool LooksLikeTypeName(string value)
    {
        string normalized = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized))
            return true;

        return !normalized.Contains(' ') &&
               normalized.Count(ch => ch == '.') >= 2 &&
               normalized.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '`' or '[' or ']');
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

        if (line.Contains(',') || line.Contains('.') || line.Contains(';'))
            return false;

        if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6)
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

    private static bool IsAcceptedWellChoiceText(string? text, RectangleF textRect, RectangleF choiceArea, bool requireChoiceArea)
    {
        if (!IsWellRowOptionText(text))
            return false;

        if (LooksLikeTooltipOrItemText(text ?? string.Empty))
            return false;

        if (requireChoiceArea && !IsChoiceAreaCandidate(ExpandRect(choiceArea, 24f), textRect))
            return false;

        return true;
    }

    private static string ExplainRejectedChoiceText(string? text, RectangleF textRect, RectangleF choiceArea, bool requireChoiceArea, bool hasTextRect)
    {
        if (!hasTextRect)
            return "no-text-rect";
        if (!IsWellRowOptionText(text))
            return "not-option-text";
        if (LooksLikeTooltipOrItemText(text ?? string.Empty))
            return "tooltip-or-item-text";
        if (requireChoiceArea && !IsChoiceAreaCandidate(ExpandRect(choiceArea, 24f), textRect))
            return "outside-choice-area";
        return "unknown";
    }

    private static bool IsWellRowOptionText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string normalized = NormalizeWhitespace(text);
        if (normalized.Length < 3 || normalized.Length > 220)
            return false;

        if (LooksLikeTooltipOrItemText(normalized))
            return false;

        return !ContainsAnyText(normalized,
        [
            "The Well of Souls", "Heart of the Well", "Confirm", "Reveal", "Options",
            "Desecrated Modifier", "Take this item", "Place an item", "Place into",
            "Right click", "Unrevealed Desecrated Modifier", "Item Level", "Requires Level",
            "Limited to:", "Countless souls", "Quality:"
        ]);
    }

    private static bool IsChoiceTextStackOptionText(string? text)
    {
        if (!IsWellRowOptionText(text))
            return false;

        string normalized = NormalizeWhitespace(text);
        return normalized.Any(char.IsDigit);
    }

    private static bool IsStrictScreenOptionText(string? text)
    {
        if (!IsWellRowOptionText(text))
            return false;

        string normalized = NormalizeWhitespace(text);
        if (!normalized.Any(char.IsDigit))
            return false;

        return normalized.StartsWith("+", StringComparison.Ordinal) ||
               normalized.StartsWith("-", StringComparison.Ordinal) ||
               normalized.Contains('%') ||
               normalized.Contains(" per second", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" to ", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Mana", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(" Rage", StringComparison.OrdinalIgnoreCase) ||
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

    private static bool IsWellOptionText(string? text)
        => IsStrictScreenOptionText(text);

    private static RectangleF PickTierBadgeRect(RectangleF optionRect, float width, float height)
    {
        const float pad = 5f;
        float x = optionRect.X + (optionRect.Width - width) * 0.5f;
        float y = optionRect.Y - height - pad;
        if (y < pad)
            y = optionRect.Bottom + pad;

        return ClampToDisplay(new RectangleF(x, y, width, height));
    }

    private static RectangleF PickTierBadgeRectInsideRow(RectangleF rowRect, RectangleF textRect, float width, float height)
    {
        const float pad = 5f;
        float minX = rowRect.Left + pad;
        float maxX = rowRect.Right - width - pad;
        float x = CenterX(textRect) - width * 0.5f;
        x = maxX >= minX ? Math.Clamp(x, minX, maxX) : rowRect.X + (rowRect.Width - width) * 0.5f;

        float minY = rowRect.Top + pad;
        float maxY = rowRect.Bottom - height - pad;
        float y;
        if (maxY >= minY)
        {
            float belowTextY = textRect.Bottom + pad;
            float aboveTextY = textRect.Top - height - pad;
            if (belowTextY >= minY && belowTextY <= maxY)
                y = belowTextY;
            else if (aboveTextY >= minY && aboveTextY <= maxY)
                y = aboveTextY;
            else
                y = Math.Clamp(rowRect.Bottom - height - pad, minY, maxY);
        }
        else
        {
            y = rowRect.Y + (rowRect.Height - height) * 0.5f;
        }

        return ClampToDisplay(new RectangleF(x, y, width, height));
    }

    private static (RectangleF TextRect, RectangleF RowRect, bool HasRowRect) GetTierBadgeAnchor(WellOption option, IReadOnlyList<WellDrawInfo> drawInfos)
    {
        var textRect = GetTierBadgeTextRect(option);
        if (!IsDrawableRect(textRect))
            return (option.Rect, default, false);

        if (TryGetOwnTierBadgeRowRect(option, textRect, out var rowRect))
            return (textRect, rowRect, true);

        if (TryInferTierBadgeRowRectFromSiblings(option, textRect, drawInfos, out rowRect))
            return (textRect, rowRect, true);

        return (textRect, default, false);
    }

    private static RectangleF GetTierBadgeTextRect(WellOption option)
    {
        if (SafeVisible(option.TextElement) &&
            TryGetRect(option.TextElement, out var textRect) &&
            LooksLikeTierBadgeTextRect(textRect, option.Rect))
        {
            return textRect;
        }

        return option.Rect;
    }

    private static bool LooksLikeTierBadgeTextRect(RectangleF candidate, RectangleF optionRect)
    {
        if (!IsDrawableRect(candidate))
            return false;

        if (candidate.Height > 90f || candidate.Width > 1100f)
            return false;

        return !IsDrawableRect(optionRect) || RectCenterInside(ExpandRect(optionRect, 8f), candidate);
    }

    private static bool TryGetOwnTierBadgeRowRect(WellOption option, RectangleF textRect, out RectangleF rowRect)
    {
        if (LooksLikeTierBadgeRowRect(option.Rect, textRect))
        {
            rowRect = option.Rect;
            return true;
        }

        if (TryGetVisibleTierBadgeRowRect(option.Element, textRect, out rowRect))
            return true;

        foreach (var element in GetShortParentChain(option.TextElement, maxDepth: 4))
            if (TryGetVisibleTierBadgeRowRect(element, textRect, out rowRect))
                return true;

        foreach (var element in GetShortParentChain(option.Element, maxDepth: 3))
            if (TryGetVisibleTierBadgeRowRect(element, textRect, out rowRect))
                return true;

        rowRect = default;
        return false;
    }

    private static bool TryInferTierBadgeRowRectFromSiblings(WellOption option, RectangleF textRect, IReadOnlyList<WellDrawInfo> drawInfos, out RectangleF rowRect)
    {
        var knownRows = new List<(int Index, RectangleF RowRect, RectangleF TextRect)>();
        foreach (var drawInfo in drawInfos)
        {
            var sibling = drawInfo.Option;
            if (sibling.Index == option.Index)
                continue;

            var siblingTextRect = GetTierBadgeTextRect(sibling);
            if (TryGetOwnTierBadgeRowRect(sibling, siblingTextRect, out var siblingRowRect))
                knownRows.Add((sibling.Index, siblingRowRect, siblingTextRect));
        }

        var beforeRows = knownRows
            .Where(row => row.Index < option.Index)
            .OrderByDescending(row => row.Index)
            .ToList();
        var afterRows = knownRows
            .Where(row => row.Index > option.Index)
            .OrderBy(row => row.Index)
            .ToList();

        if (beforeRows.Count > 0 && afterRows.Count > 0)
        {
            var before = beforeRows[0];
            var after = afterRows[0];
            if (TierBadgeRowsCompatible(before.RowRect, after.RowRect))
            {
                float t = (float)(option.Index - before.Index) / (after.Index - before.Index);
                var inferred = new RectangleF(
                    before.RowRect.X + (after.RowRect.X - before.RowRect.X) * t,
                    before.RowRect.Y + (after.RowRect.Y - before.RowRect.Y) * t,
                    before.RowRect.Width + (after.RowRect.Width - before.RowRect.Width) * t,
                    before.RowRect.Height + (after.RowRect.Height - before.RowRect.Height) * t);

                if (LooksLikeTierBadgeRowRect(inferred, textRect))
                {
                    rowRect = inferred;
                    return true;
                }
            }
        }

        foreach (var sibling in knownRows.OrderBy(row => Math.Abs(row.Index - option.Index)))
        {
            int indexDelta = option.Index - sibling.Index;
            if (indexDelta == 0)
                continue;

            float textCenterStep = (CenterY(textRect) - CenterY(sibling.TextRect)) / indexDelta;
            float absStep = Math.Abs(textCenterStep);
            if (absStep < 80f || absStep > 240f)
                continue;

            if (Math.Abs(absStep - sibling.RowRect.Height) > Math.Max(36f, sibling.RowRect.Height * 0.35f))
                continue;

            var inferred = new RectangleF(
                sibling.RowRect.X,
                sibling.RowRect.Y + textCenterStep * indexDelta,
                sibling.RowRect.Width,
                sibling.RowRect.Height);

            if (LooksLikeTierBadgeRowRect(inferred, textRect))
            {
                rowRect = inferred;
                return true;
            }
        }

        rowRect = default;
        return false;
    }

    private static bool TierBadgeRowsCompatible(RectangleF left, RectangleF right)
    {
        float averageHeight = (left.Height + right.Height) * 0.5f;
        float averageWidth = (left.Width + right.Width) * 0.5f;
        return Math.Abs(left.Height - right.Height) <= Math.Max(24f, averageHeight * 0.25f) &&
               Math.Abs(left.Width - right.Width) <= Math.Max(96f, averageWidth * 0.20f) &&
               Math.Abs(left.X - right.X) <= Math.Max(96f, averageWidth * 0.15f);
    }

    private static IEnumerable<Element> GetShortParentChain(Element? element, int maxDepth)
    {
        var seen = new HashSet<long>();
        var current = element;
        for (int depth = 0; depth < maxDepth && current != null && current.Address != 0; depth++)
        {
            current = TryGetElementProperty(current, "Parent");
            if (current == null || current.Address == 0 || !seen.Add((long)current.Address))
                yield break;

            yield return current;
        }
    }

    private static bool TryGetVisibleTierBadgeRowRect(Element? element, RectangleF textRect, out RectangleF rect)
    {
        rect = default;
        if (element == null || !SafeVisible(element))
            return false;

        if (!TryGetRect(element, out var candidate) || !IsDrawableRect(candidate))
            return false;

        if (!LooksLikeTierBadgeRowRect(candidate, textRect))
            return false;

        rect = candidate;
        return true;
    }

    private static bool LooksLikeTierBadgeRowRect(RectangleF candidate, RectangleF textRect)
    {
        if (!IsDrawableRect(candidate) || !IsDrawableRect(textRect))
            return false;

        if (candidate.Width < textRect.Width - 4f)
            return false;

        if (candidate.Width < 350f || candidate.Width > 1250f)
            return false;

        if (candidate.Height < 90f || candidate.Height > 220f)
            return false;

        if (!RectCenterInside(ExpandRect(candidate, 8f), textRect))
            return false;

        var overlap = IntersectionRect(candidate, textRect);
        return Area(overlap) >= Area(textRect) * 0.6f;
    }

    private static RectangleF IntersectionRect(RectangleF a, RectangleF b)
    {
        float left = Math.Max(a.Left, b.Left);
        float top = Math.Max(a.Top, b.Top);
        float right = Math.Min(a.Right, b.Right);
        float bottom = Math.Min(a.Bottom, b.Bottom);
        if (right <= left || bottom <= top)
            return default;

        return new RectangleF(left, top, right - left, bottom - top);
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
