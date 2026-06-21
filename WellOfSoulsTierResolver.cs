using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExileCore2;

namespace WellWise;

public sealed class WellOfSoulsTierResolver
{
    private static readonly Regex NumberRegex = new(@"[-+]?\d+(?:\.\d+)?", RegexOptions.Compiled);
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "the", "to", "of", "per", "for", "you", "your", "with", "from", "increased", "reduced"
    };

    private WellTierDatabase _database = new();
    private string _loadStatus = "not loaded";

    public string LoadStatus => _loadStatus;

    public void Load(string pluginDirectory)
    {
        string path = Path.Combine(pluginDirectory, "data", "well_of_souls_tiers.json");
        try
        {
            if (!File.Exists(path))
            {
                _database = new WellTierDatabase();
                _loadStatus = "missing data/well_of_souls_tiers.json";
                return;
            }

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            _database = JsonSerializer.Deserialize<WellTierDatabase>(File.ReadAllText(path), options) ?? new WellTierDatabase();
            _loadStatus = $"loaded {_database.Rules.Count} Well tier rules";
        }
        catch (Exception ex)
        {
            _database = new WellTierDatabase();
            _loadStatus = "load failed: " + ex.Message;
        }
    }

    public WellOfSoulsTierResult Resolve(ItemSnapshot? item, string optionText, int fallbackItemLevel = 0)
    {
        string text = Normalize(optionText);
        var values = ExtractValues(text);
        double? value = values.Count > 0 ? values[0] : null;
        if (string.IsNullOrWhiteSpace(text))
            return WellOfSoulsTierResult.Unknown(text, value, _loadStatus);

        int rankingItemLevel = item?.ItemLevel > 0 ? item.ItemLevel : Math.Max(0, fallbackItemLevel);
        if (!HasItemContext(item) && _database.Rules.Any(rule => RuleMatchesText(rule, text)))
            return WellOfSoulsTierResult.Unknown(text, value, "missing Well item class context");

        var rule = _database.Rules
            .Where(rule => RuleMatchesItem(rule, item))
            .Where(rule => RuleMatchesText(rule, text))
            .Select(rule => new
            {
                Rule = rule,
                ItemScore = RuleItemSpecificityScore(rule, item),
                TextScore = rule.TextContainsAll.Count,
                DecorationScore = RuleValueDecorationScore(rule, text),
                Distance = RuleValueDistance(rule, text, rankingItemLevel)
            })
            .OrderByDescending(match => match.ItemScore)
            .ThenByDescending(match => match.TextScore)
            .ThenByDescending(match => match.DecorationScore)
            .ThenBy(match => match.Distance)
            .ThenBy(match => match.Rule.DesecratedOnly ? 1 : 0)
            .Select(match => match.Rule)
            .FirstOrDefault();

        if (rule == null)
            return WellOfSoulsTierResult.Unknown(text, value, "no local tier rule");

        values = ExtractValuesForRule(rule, text);
        value = values.Count > 0 ? values[0] : null;
        int itemLevel = item?.ItemLevel > 0 ? item.ItemLevel : Math.Max(0, fallbackItemLevel);
        var availableTiers = rule.Tiers
            .Where(tier => itemLevel > 0 ? tier.ItemLevel <= itemLevel : true)
            .OrderBy(tier => tier.Tier)
            .ToList();

        if (availableTiers.Count == 0)
        {
            var absolute = rule.Tiers.OrderBy(tier => tier.Tier).FirstOrDefault();
            return new WellOfSoulsTierResult(
                Known: true,
                Source: _database.Source,
                RuleId: rule.Id,
                Label: rule.Label,
                OptionText: text,
                CurrentValue: value,
                CurrentTier: null,
                BestTier: null,
                AbsoluteBestTier: absolute,
                Summary: "known stat; no tier unlocked at this ilvl",
                Detail: $"needs ilvl {rule.Tiers.Min(tier => tier.ItemLevel)}+");
        }

        List<WellTierRange> currentTierMatches = values.Count == 0
            ? []
            : availableTiers.Where(tier => tier.Contains(values)).ToList();
        WellTierRange? currentTier = currentTierMatches.FirstOrDefault();
        currentTier ??= values.Count == 0 ? null : FindClosestTier(availableTiers, values);
        if (currentTierMatches.Count == 0 && currentTier != null)
            currentTierMatches.Add(currentTier);
        var bestTier = availableTiers.First();
        var absoluteBestTier = rule.Tiers
            .OrderBy(tier => tier.Tier)
            .FirstOrDefault();

        string summary = BuildSummary(currentTier, bestTier, absoluteBestTier, rule, itemLevel);

        return new WellOfSoulsTierResult(
            Known: true,
            Source: _database.Source,
            RuleId: rule.Id,
            Label: rule.Label,
            OptionText: text,
            CurrentValue: value,
            CurrentTier: currentTier,
            BestTier: bestTier,
            AbsoluteBestTier: absoluteBestTier,
            Summary: summary,
            Detail: rule.DesecratedOnly ? "repoe-fork desecrated mod data" : "repoe-fork item mod data")
        {
            CurrentTierMatches = currentTierMatches
        };
    }

    public object BuildRuntimeProbe(GameController gameController, ItemSnapshot? item, IEnumerable<string> optionTexts, int fallbackItemLevel = 0)
    {
        var options = optionTexts
            .Select(Normalize)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();

        if (options.Count == 0)
            return new { status = "no option text" };

        try
        {
            var modsTable = gameController.Files.GetType().GetProperty("Mods")?.GetValue(gameController.Files);
            var recordsByTier = GetMemberValue(modsTable, "recordsByTier") ?? GetMemberValue(modsTable, "RecordsByTier");
            if (recordsByTier == null)
                return new { status = "GameController.Files.Mods.recordsByTier not found", modsType = modsTable?.GetType().FullName };

            var flatRecords = EnumerateRecords(recordsByTier)
                .Take(12000)
                .Select(ToRuntimeRecordProbe)
                .Where(record => !string.IsNullOrWhiteSpace(record.TextBlob))
                .ToList();

            return new
            {
                status = "runtime records scanned",
                itemClass = item?.ClassName,
                itemLevel = item?.ItemLevel > 0 ? item.ItemLevel : fallbackItemLevel,
                recordsScanned = flatRecords.Count,
                options = options.Select(option => new
                {
                    text = option,
                    local = Resolve(item, option, fallbackItemLevel),
                    runtimeCandidates = flatRecords
                        .Select(record => new { record, score = MatchScore(option, record.TextBlob) })
                        .Where(x => x.score > 0)
                        .OrderByDescending(x => x.score)
                        .ThenBy(x => x.record.Level)
                        .Take(8)
                        .Select(x => new
                        {
                            x.score,
                            x.record.TypeName,
                            x.record.Group,
                            x.record.UserFriendlyName,
                            x.record.Level,
                            x.record.AffixType,
                            x.record.Domain,
                            x.record.Stats,
                            x.record.TextBlob
                        })
                        .ToList()
                }).ToList()
            };
        }
        catch (Exception ex)
        {
            return new { status = "runtime probe failed", error = ex.Message };
        }
    }

    private static bool RuleMatchesItem(WellTierRule rule, ItemSnapshot? item)
    {
        if (rule.ItemClassContains.Count == 0)
            return false;

        if (!HasItemContext(item))
            return false;

        return ItemClassMatcher.MatchesAny(item.ClassName, rule.ItemClassContains);
    }

    private static bool RuleMatchesText(WellTierRule rule, string text)
    {
        if (rule.TextContainsAll.Count == 0)
            return false;

        return rule.TextContainsAll.All(part => text.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    private static int RuleItemSpecificityScore(WellTierRule rule, ItemSnapshot? item)
    {
        if (rule.ItemClassContains.Count == 0)
            return -100;

        if (!HasItemContext(item))
            return -100;

        return ItemClassMatcher.MatchesAny(item.ClassName, rule.ItemClassContains) ? 100 : -100;
    }

    private static bool HasItemContext(ItemSnapshot? item)
        => item != null &&
           !string.IsNullOrWhiteSpace(item.ClassName);

    private static double RuleValueDistance(WellTierRule rule, string text, int itemLevel)
    {
        var values = ExtractValuesForRule(rule, text);
        if (values.Count == 0)
            return 0d;

        var tiers = rule.Tiers
            .Where(tier => itemLevel > 0 ? tier.ItemLevel <= itemLevel : true)
            .ToList();

        if (tiers.Count == 0)
            tiers = rule.Tiers;

        return tiers.Count == 0 ? 0d : tiers.Min(tier => tier.DistanceTo(values));
    }

    private static int RuleValueDecorationScore(WellTierRule rule, string text)
    {
        var components = ExtractValueComponentsForRule(rule, text);
        if (components.Count == 0)
            return 0;

        var referenceTier = rule.Tiers.OrderBy(tier => tier.Tier).FirstOrDefault();
        if (referenceTier == null)
            return 0;

        var ranges = GetValueRanges(referenceTier);
        if (ranges.Count == 0)
            return 0;

        int count = Math.Min(components.Count, ranges.Count);
        bool useRuleDecorationFallback = referenceTier.Values.Count == 0;
        int score = 0;
        for (int i = 0; i < count; i++)
        {
            string rulePrefix = EffectivePrefix(ranges[i], rule, useRuleDecorationFallback);
            string ruleSuffix = EffectiveSuffix(ranges[i], rule, useRuleDecorationFallback);
            var component = components[i];

            bool optionPercent = component.Suffix == "%";
            bool rulePercent = ruleSuffix == "%";
            score += optionPercent == rulePercent ? 6 : -18;

            bool optionPlus = component.Prefix == "+";
            bool rulePlus = rulePrefix == "+";
            score += optionPlus == rulePlus ? 2 : -4;
        }

        return score;
    }

    private static List<double> ExtractValues(string text)
        => ExtractValueComponents(text)
            .Select(component => component.Value)
            .ToList();

    private static List<double> ExtractValuesForRule(WellTierRule rule, string text)
        => ExtractValueComponentsForRule(rule, text)
            .Select(component => component.Value)
            .ToList();

    private static List<TextValueComponent> ExtractValueComponentsForRule(WellTierRule rule, string text)
    {
        var values = ExtractValueComponents(text);
        int expectedCount = rule.Tiers
            .Select(tier => GetValueRanges(tier).Count)
            .DefaultIfEmpty(1)
            .Max();

        var componentIndexes = LabelComponentIndexes(rule);
        if (componentIndexes.Count == expectedCount && componentIndexes.Count > 0 && values.Count > componentIndexes.Max())
            return componentIndexes.Select(index => values[index]).ToList();

        return expectedCount > 0 && values.Count > expectedCount
            ? values.Take(expectedCount).ToList()
            : values;
    }

    private static List<TextValueComponent> ExtractValueComponents(string text)
    {
        var result = new List<TextValueComponent>();
        foreach (Match match in NumberRegex.Matches(text))
        {
            if (!double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;

            string prefix = match.Value.StartsWith("+", StringComparison.Ordinal)
                ? "+"
                : match.Value.StartsWith("-", StringComparison.Ordinal)
                    ? "-"
                    : "";
            string suffix = "";
            int after = match.Index + match.Length;
            while (after < text.Length && char.IsWhiteSpace(text[after]))
                after++;
            if (after < text.Length && text[after] == '%')
                suffix = "%";

            result.Add(new TextValueComponent(Math.Abs(value), prefix, suffix));
        }

        return result;
    }

    private static List<int> LabelComponentIndexes(WellTierRule rule)
    {
        var firstTier = rule.Tiers.OrderBy(tier => tier.Tier).FirstOrDefault();
        if (firstTier == null)
            return [];

        var ranges = GetValueRanges(firstTier);
        if (ranges.Count == 0)
            return [];

        var labelNumbers = NumberRegex.Matches(rule.Label)
            .Select((match, index) => new LabelNumber(index, ParseNumber(match.Value)))
            .Where(value => !double.IsNaN(value.Value))
            .ToList();

        var used = new HashSet<int>();
        var indexes = new List<int>();
        foreach (var range in ranges)
        {
            int index = labelNumbers
                .Where(number => !used.Contains(number.Index) &&
                                 (AlmostEquals(number.Value, range.Min) || AlmostEquals(number.Value, range.Max)))
                .Select(number => number.Index)
                .FirstOrDefault(-1);

            if (index < 0)
                return [];

            used.Add(index);
            indexes.Add(index);
        }

        return indexes.OrderBy(index => index).ToList();
    }

    private static double ParseNumber(string value)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Abs(parsed)
            : double.NaN;

    private static bool AlmostEquals(double left, double right)
        => Math.Abs(left - right) < 0.0001;

    private sealed record LabelNumber(int Index, double Value);

    private static WellTierRange? FindClosestTier(IEnumerable<WellTierRange> tiers, IReadOnlyList<double> values)
        => tiers
            .OrderBy(tier => tier.DistanceTo(values))
            .ThenBy(tier => tier.Tier)
            .FirstOrDefault();

    public static string FormatTier(WellTierRange tier, WellTierRule? rule = null)
    {
        return $"T{tier.Tier} {FormatRange(tier, rule)}";
    }

    private static string FormatTierWithItemLevel(WellTierRange tier, WellTierRule? rule = null)
        => $"T{tier.Tier}@ilvl{tier.ItemLevel} {FormatRange(tier, rule)}";

    private static string FormatRange(WellTierRange tier, WellTierRule? rule = null)
    {
        var ranges = GetValueRanges(tier);
        bool useRuleDecorationFallback = tier.Values.Count == 0;
        if (ranges.Count == 1)
            return FormatValueRange(ranges[0], rule, useRuleDecorationFallback);

        string separator = rule?.Label.Contains(") to (", StringComparison.OrdinalIgnoreCase) == true ? " to " : " / ";
        return string.Join(separator, ranges.Select(range => FormatValueRange(range, rule, useRuleDecorationFallback)));
    }

    private static string BuildSummary(WellTierRange? currentTier, WellTierRange bestTier, WellTierRange? absoluteBestTier, WellTierRule rule, int itemLevel)
    {
        var parts = new List<string>
        {
            currentTier == null ? "known stat" : FormatTier(currentTier, rule)
        };

        bool itemMaxIsCurrent = currentTier != null &&
                                bestTier.Tier == currentTier.Tier &&
                                TierRangesEqual(bestTier, currentTier);
        bool absoluteNeedsHigherLevel = absoluteBestTier != null &&
                                        itemLevel > 0 &&
                                        absoluteBestTier.ItemLevel > itemLevel;

        if (!itemMaxIsCurrent || absoluteNeedsHigherLevel)
            parts.Add($"item max {FormatTier(bestTier, rule)}");

        if (absoluteNeedsHigherLevel && absoluteBestTier != null)
            parts.Add(FormatTierWithItemLevel(absoluteBestTier, rule));

        return string.Join("; ", parts);
    }

    private static string FormatValueRange(WellTierValueRange range, WellTierRule? rule = null, bool useRuleDecorationFallback = true)
    {
        string prefix = EffectivePrefix(range, rule, useRuleDecorationFallback);
        string suffix = EffectiveSuffix(range, rule, useRuleDecorationFallback);
        return $"{prefix}{FormatNumber(range.Min)}-{FormatNumber(range.Max)}{suffix}";
    }

    private static string EffectivePrefix(WellTierValueRange range, WellTierRule? rule, bool useRuleDecorationFallback)
        => useRuleDecorationFallback && string.IsNullOrWhiteSpace(range.Prefix) ? rule?.Prefix ?? "" : range.Prefix;

    private static string EffectiveSuffix(WellTierValueRange range, WellTierRule? rule, bool useRuleDecorationFallback)
        => useRuleDecorationFallback && string.IsNullOrWhiteSpace(range.Suffix) ? rule?.Suffix ?? "" : range.Suffix;

    private static IReadOnlyList<WellTierValueRange> GetValueRanges(WellTierRange tier)
        => tier.Values.Count > 0
            ? tier.Values
            : [new WellTierValueRange { Min = tier.Min, Max = tier.Max }];

    private static bool TierRangesEqual(WellTierRange left, WellTierRange right)
    {
        var leftRanges = GetValueRanges(left);
        var rightRanges = GetValueRanges(right);
        if (leftRanges.Count != rightRanges.Count)
            return false;

        for (int i = 0; i < leftRanges.Count; i++)
        {
            if (Math.Abs(leftRanges[i].Min - rightRanges[i].Min) > 0.0001 ||
                Math.Abs(leftRanges[i].Max - rightRanges[i].Max) > 0.0001)
                return false;
        }

        return true;
    }

    private static string FormatNumber(double value)
        => value.ToString(value % 1 == 0 ? "0" : "0.#", CultureInfo.InvariantCulture);

    private static string Normalize(string? text)
        => string.Join(" ", (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int MatchScore(string optionText, string blob)
    {
        var words = ImportantWords(optionText).ToList();
        if (words.Count == 0)
            return 0;

        int score = 0;
        foreach (var word in words)
            if (blob.Contains(word, StringComparison.OrdinalIgnoreCase))
                score += word.Length >= 8 ? 3 : 1;

        return score >= Math.Min(3, words.Count) ? score : 0;
    }

    private static IEnumerable<string> ImportantWords(string text)
        => Regex.Matches(text, @"[A-Za-z]+")
            .Select(match => match.Value)
            .Where(word => word.Length > 2 && !StopWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<object> EnumerateRecords(object recordsByTier)
    {
        foreach (var entry in ToEnumerable(recordsByTier))
        {
            var family = GetMemberValue(entry, "Value") ?? entry;
            foreach (var record in ToEnumerable(family))
                if (record != null)
                    yield return record;
        }
    }

    private static RuntimeRecordProbe ToRuntimeRecordProbe(object record)
    {
        string typeName = ReadString(record, "TypeName");
        string group = ReadString(record, "Group");
        string friendly = ReadString(record, "UserFriendlyName");
        string affixType = ReadString(record, "AffixType");
        string domain = ReadString(record, "Domain");
        int level = ReadInt(record, "Level");
        string stats = ReadStats(record);

        return new RuntimeRecordProbe(
            TypeName: typeName,
            Group: group,
            UserFriendlyName: friendly,
            Level: level,
            AffixType: affixType,
            Domain: domain,
            Stats: stats,
            TextBlob: Normalize($"{typeName} {group} {friendly} {affixType} {domain} {stats} {record}"));
    }

    private static string ReadStats(object record)
    {
        var stats = GetMemberValue(record, "StatNames") ?? GetMemberValue(record, "Stats");
        if (stats == null)
            return string.Empty;

        var values = new List<string>();
        foreach (var stat in ToEnumerable(stats).Take(8))
            values.Add(Normalize($"{ReadString(stat, "Id")} {ReadString(stat, "Key")} {ReadString(stat, "Name")} {ReadString(stat, "Translation")} {stat}"));

        return string.Join("; ", values.Where(value => !string.IsNullOrWhiteSpace(value)));
    }

    private static string ReadString(object? obj, string name)
    {
        var value = GetMemberValue(obj, name);
        return value?.ToString() ?? string.Empty;
    }

    private static int ReadInt(object? obj, string name)
    {
        var value = GetMemberValue(obj, name);
        if (value == null)
            return 0;

        return int.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
            ? result
            : 0;
    }

    private static object? GetMemberValue(object? obj, string name)
    {
        if (obj == null)
            return null;

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase;
        var type = obj.GetType();
        try
        {
            var prop = type.GetProperty(name, flags);
            if (prop != null && prop.GetIndexParameters().Length == 0)
                return prop.GetValue(obj);
        }
        catch { }

        try
        {
            var field = type.GetField(name, flags);
            if (field != null)
                return field.GetValue(obj);
        }
        catch { }

        return null;
    }

    private static IEnumerable<object?> ToEnumerable(object? value)
    {
        if (value == null || value is string)
            yield break;

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                yield return item;
        }
        else
        {
            yield return value;
        }
    }

    private readonly record struct TextValueComponent(double Value, string Prefix, string Suffix);

    private sealed record RuntimeRecordProbe(
        string TypeName,
        string Group,
        string UserFriendlyName,
        int Level,
        string AffixType,
        string Domain,
        string Stats,
        string TextBlob);
}

public sealed record WellOfSoulsTierResult(
    bool Known,
    string Source,
    string RuleId,
    string Label,
    string OptionText,
    double? CurrentValue,
    WellTierRange? CurrentTier,
    WellTierRange? BestTier,
    WellTierRange? AbsoluteBestTier,
    string Summary,
    string Detail)
{
    public IReadOnlyList<WellTierRange> CurrentTierMatches { get; init; } = CurrentTier == null ? [] : [CurrentTier];

    public static WellOfSoulsTierResult Unknown(string optionText, double? value, string detail)
        => new(
            Known: false,
            Source: "unknown",
            RuleId: "",
            Label: "",
            OptionText: optionText,
            CurrentValue: value,
            CurrentTier: null,
            BestTier: null,
            AbsoluteBestTier: null,
            Summary: "tier unknown",
            Detail: detail);
}

public sealed class WellTierDatabase
{
    public int Schema { get; set; }
    public string GeneratedAt { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public List<string> Notes { get; set; } = [];
    public List<WellTierRule> Rules { get; set; } = [];
}

public sealed class WellTierRule
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public List<string> ItemClassContains { get; set; } = [];
    public List<string> TextContainsAll { get; set; } = [];
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;
    public bool DesecratedOnly { get; set; }
    public List<WellTierRange> Tiers { get; set; } = [];
}

public sealed class WellTierRange
{
    public int Tier { get; set; }
    public int ItemLevel { get; set; }
    public double Min { get; set; }
    public double Max { get; set; }
    public List<WellTierValueRange> Values { get; set; } = [];

    public bool Contains(double value)
        => value >= Min - 0.0001 && value <= Max + 0.0001;

    public double DistanceTo(double value)
        => Contains(value) ? 0 : value < Min ? Min - value : value - Max;

    public bool Contains(IReadOnlyList<double> values)
    {
        var ranges = ComponentRanges();
        if (values.Count < ranges.Count)
            return false;

        for (int i = 0; i < ranges.Count; i++)
            if (!ranges[i].Contains(values[i]))
                return false;

        return true;
    }

    public double DistanceTo(IReadOnlyList<double> values)
    {
        var ranges = ComponentRanges();
        if (values.Count == 0)
            return double.MaxValue;

        double total = 0;
        for (int i = 0; i < ranges.Count; i++)
        {
            if (i >= values.Count)
                return double.MaxValue / 2;

            total += ranges[i].DistanceTo(values[i]);
        }

        return total;
    }

    private IReadOnlyList<WellTierValueRange> ComponentRanges()
        => Values.Count > 0
            ? Values
            : [new WellTierValueRange { Min = Min, Max = Max }];
}

public sealed class WellTierValueRange
{
    public double Min { get; set; }
    public double Max { get; set; }
    public string Prefix { get; set; } = string.Empty;
    public string Suffix { get; set; } = string.Empty;

    public bool Contains(double value)
        => value >= Min - 0.0001 && value <= Max + 0.0001;

    public double DistanceTo(double value)
        => Contains(value) ? 0 : value < Min ? Min - value : value - Max;
}
