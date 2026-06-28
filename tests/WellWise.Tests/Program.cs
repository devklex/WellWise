using System;
using System.Collections.Generic;
using System.IO;
using WellWise;

var tests = new (string Name, Action Body)[]
{
    ("missing item context returns unknown", MissingItemContextReturnsUnknown),
    ("body armour flat evasion does not match percent evasion", BodyArmourFlatEvasion),
    ("body armour percent evasion resolves percent tier", BodyArmourPercentEvasion),
    ("body armour hybrid evasion and life resolves hybrid rule", BodyArmourHybridEvasionLife),
    ("belt charm mana-as-armour desecrated mod resolves", BeltManaAsArmour),
    ("jewel skill effect duration resolves", JewelSkillEffectDuration),
    ("jewel cold penetration resolves", JewelColdPenetration),
    ("staff block chance uses current self-extracted range", StaffBlockChanceUsesCurrentRange),
    ("staff block chance legacy range remains recognized", StaffBlockChanceLegacyRange),
    ("staff Puppet Master stacks resolves", StaffPuppetMasterStacksResolves),
    ("staff Puppet Master stacks stays class-specific", StaffPuppetMasterStacksStaysClassSpecific),
    ("otherworldly ring mana before life resolves", OtherworldlyRingDamageTakenFromManaBeforeLife),
    ("otherworldly amulet scaled critical chance uses display range", OtherworldlyAmuletFireSpellCriticalChance),
    ("otherworldly belt no-number stat resolves without fake range", OtherworldlyBeltMeleeSplash),
    ("otherworldly accessory rules stay class-specific", OtherworldlyAccessoryRulesStayClassSpecific)
};

int failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Body();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine("  " + ex.Message);
    }
}

if (failures > 0)
    throw new InvalidOperationException($"{failures} WellWise regression test(s) failed.");

Console.WriteLine($"{tests.Length} WellWise regression tests passed.");

static WellOfSoulsTierResolver CreateResolver()
{
    string root = FindWellWiseRoot();
    var resolver = new WellOfSoulsTierResolver();
    resolver.Load(root);
    AssertContains(resolver.LoadStatus, "loaded", "resolver data should load");
    return resolver;
}

static string FindWellWiseRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory != null)
    {
        string dataPath = Path.Combine(directory.FullName, "data", "well_of_souls_tiers.json");
        if (File.Exists(dataPath))
            return directory.FullName;

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not find data/well_of_souls_tiers.json from test output path.");
}

static ItemSnapshot Item(string className, int itemLevel = 80, string baseName = "Test Item")
    => new()
    {
        Source = "test",
        BaseName = baseName,
        ClassName = className,
        ItemLevel = itemLevel
    };

static void MissingItemContextReturnsUnknown()
{
    var result = CreateResolver().Resolve(null, "+46 to Evasion Rating");
    AssertFalse(result.Known, "missing item context should not guess between item classes");
    AssertContains(result.Detail, "missing Well item class context", "unknown reason");
}

static void BodyArmourFlatEvasion()
{
    var result = CreateResolver().Resolve(Item("Body Armour", 80), "+46 to Evasion Rating");
    AssertKnown(result);
    AssertEqual(10, result.CurrentTier?.Tier, "flat evasion tier");
    AssertEqualIgnoreCase("prefix", result.AffixType, "flat evasion affix type");
    AssertContains(result.Label, "to Evasion Rating", "flat evasion label");
    AssertFalse(result.Label.Contains("increased Evasion Rating", StringComparison.OrdinalIgnoreCase), "flat evasion should not resolve to percent evasion");
}

static void BodyArmourPercentEvasion()
{
    var result = CreateResolver().Resolve(Item("Body Armour", 80), "90% increased Evasion Rating");
    AssertKnown(result);
    AssertEqual(3, result.CurrentTier?.Tier, "percent evasion tier");
    AssertEqualIgnoreCase("prefix", result.AffixType, "percent evasion affix type");
    AssertContains(result.Label, "increased Evasion Rating", "percent evasion label");
}

static void BodyArmourHybridEvasionLife()
{
    var result = CreateResolver().Resolve(Item("Body Armour", 80), "41% increased Evasion Rating +46 to maximum Life");
    AssertKnown(result);
    AssertEqual(1, result.CurrentTier?.Tier, "hybrid evasion/life tier");
    AssertEqualIgnoreCase("prefix", result.AffixType, "hybrid evasion/life affix type");
    AssertContains(result.Label, "maximum Life", "hybrid evasion/life label");
}

static void BeltManaAsArmour()
{
    var result = CreateResolver().Resolve(Item("Belt", 78), "Gain 10% of Maximum Mana as Armour");
    AssertKnown(result);
    AssertEqual(1, result.CurrentTier?.Tier, "belt mana-as-armour tier");
    AssertEqualIgnoreCase("prefix", result.AffixType, "belt mana-as-armour affix type");
    AssertContains(result.Label, "maximum Mana as Armour", "belt mana-as-armour label");
}

static void JewelSkillEffectDuration()
{
    var result = CreateResolver().Resolve(Item("Jewel", 80), "15% increased Skill Effect Duration");
    AssertKnown(result);
    AssertEqual(1, result.CurrentTier?.Tier, "jewel skill effect duration tier");
    AssertEqualIgnoreCase("prefix", result.AffixType, "jewel skill effect duration affix type");
    AssertContains(result.Label, "Skill Effect Duration", "jewel skill effect duration label");
}

static void JewelColdPenetration()
{
    var result = CreateResolver().Resolve(Item("Jewel", 80), "Damage Penetrates 6% Cold Resistance");
    AssertKnown(result);
    AssertEqualIgnoreCase("prefix", result.AffixType, "jewel cold penetration affix type");
    AssertContains(result.Label, "Cold Resistance", "jewel cold penetration label");
}

static void StaffBlockChanceUsesCurrentRange()
{
    var result = CreateResolver().Resolve(Item("Staff", 80), "+24% to Block chance");
    AssertKnown(result);
    AssertEqual(1, result.CurrentTier?.Tier, "staff block chance tier");
    AssertFalse(result.CurrentTier?.Legacy == true, "current staff block chance should not be marked legacy");
    AssertContains(result.Summary, "+20-25%", "current staff block chance range");
}

static void StaffBlockChanceLegacyRange()
{
    var result = CreateResolver().Resolve(Item("Staff", 80), "+14% to Block chance");
    AssertKnown(result);
    AssertEqual(1, result.CurrentTier?.Tier, "legacy staff block chance tier");
    AssertEqual(true, result.CurrentTier?.Legacy, "legacy staff block chance marker");
    AssertContains(result.Summary, "Legacy T1 +12-16%", "legacy staff block chance range");
    AssertContains(result.Summary, "item max T1 +20-25%", "legacy staff block chance current item max");
}

static void StaffPuppetMasterStacksResolves()
{
    var result = CreateResolver().Resolve(Item("Staff", 80), "+3 maximum stacks of Puppet Master");
    AssertKnown(result);
    AssertContains(result.RuleId, "AbyssModStaffKurgalSuffixPuppetMasterStacks", "staff Puppet Master stacks rule id");
    AssertEqual(1, result.CurrentTier?.Tier, "staff Puppet Master stacks tier");
    AssertEqualIgnoreCase("suffix", result.AffixType, "staff Puppet Master stacks affix type");
    AssertEqual(3d, result.CurrentTier?.Min, "staff Puppet Master stacks minimum");
    AssertEqual(4d, result.CurrentTier?.Max, "staff Puppet Master stacks maximum");
}

static void StaffPuppetMasterStacksStaysClassSpecific()
{
    var result = CreateResolver().Resolve(Item("Ring", 80), "+3 maximum stacks of Puppet Master");
    AssertFalse(result.Known, "Staff-only Puppet Master stacks rule should not resolve for a ring");
}

static void OtherworldlyRingDamageTakenFromManaBeforeLife()
{
    var result = CreateResolver().Resolve(Item("Ring", 80), "8% of Damage is taken from Mana before Life");
    AssertKnown(result);
    AssertEqual(1, result.CurrentTier?.Tier, "otherworldly ring damage taken from mana before life tier");
    AssertEqualIgnoreCase("prefix", result.AffixType, "otherworldly ring damage taken from mana before life affix type");
    AssertContains(result.RuleId, "GenesisTreeRingDamageTakenFromManaBeforeLife", "otherworldly ring rule id");
    AssertContains(result.Summary, "8-12%", "otherworldly ring display range");
}

static void OtherworldlyAmuletFireSpellCriticalChance()
{
    var result = CreateResolver().Resolve(Item("Amulet", 80), "+4% to Fire Spell Critical Hit Chance");
    AssertKnown(result);
    AssertEqual(1, result.CurrentTier?.Tier, "otherworldly amulet fire spell crit tier");
    AssertEqualIgnoreCase("suffix", result.AffixType, "otherworldly amulet fire spell crit affix type");
    AssertContains(result.RuleId, "GenesisTreeFireSpellBaseCriticalChance", "otherworldly amulet rule id");
    AssertContains(result.Summary, "+4-5%", "otherworldly amulet crit display range should not use raw 400-500 values");
}

static void OtherworldlyBeltMeleeSplash()
{
    var result = CreateResolver().Resolve(Item("Belt", 80), "Minions' Strikes have Melee Splash");
    AssertKnownWithoutTier(result);
    AssertEqualIgnoreCase("suffix", result.AffixType, "otherworldly belt melee splash affix type");
    AssertContains(result.RuleId, "GenesisTreeBeltMinionMeleeSplash", "otherworldly belt melee splash rule id");
    AssertEqual("known stat", result.Summary, "otherworldly no-number stat summary");
}

static void OtherworldlyAccessoryRulesStayClassSpecific()
{
    var result = CreateResolver().Resolve(Item("Belt", 80), "8% of Damage is taken from Mana before Life");
    AssertFalse(result.Known, "ring-only Otherworldly rule should not resolve for a belt");
}

static void AssertKnown(WellOfSoulsTierResult result)
{
    if (!result.Known)
        throw new InvalidOperationException($"Expected known tier, got unknown: {result.Detail}");

    if (result.CurrentTier == null)
        throw new InvalidOperationException($"Expected current tier. Summary: {result.Summary}");
}

static void AssertKnownWithoutTier(WellOfSoulsTierResult result)
{
    if (!result.Known)
        throw new InvalidOperationException($"Expected known stat, got unknown: {result.Detail}");
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
}

static void AssertEqualIgnoreCase(string expected, string actual, string label)
{
    if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'.");
}

static void AssertContains(string value, string expectedPart, string label)
{
    if (!value.Contains(expectedPart, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{label}: expected '{value}' to contain '{expectedPart}'.");
}

static void AssertFalse(bool condition, string message)
{
    if (condition)
        throw new InvalidOperationException(message);
}
