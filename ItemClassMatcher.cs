using System;
using System.Collections.Generic;
using System.Linq;

namespace WellWise;

public static class ItemClassMatcher
{
    private static readonly HashSet<string> WeaponClasses = Keys(
        "Bow", "Crossbow", "Claw", "Dagger", "Flail", "One Hand Axe", "Two Hand Axe",
        "One Hand Mace", "Two Hand Mace", "One Hand Sword", "Two Hand Sword",
        "Sceptre", "Scepter", "Spear", "Staff", "Warstaff", "Quarterstaff",
        "Talisman", "Wand");

    private static readonly Dictionary<string, HashSet<string>> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [Key("Body Armor")] = Keys("Body Armour"),
        [Key("Body Armour")] = Keys("Body Armour"),
        [Key("Mace")] = Keys("One Hand Mace", "Two Hand Mace"),
        [Key("One Handed Mace")] = Keys("One Hand Mace"),
        [Key("Two Handed Mace")] = Keys("Two Hand Mace"),
        [Key("One Handed Sword")] = Keys("One Hand Sword"),
        [Key("Two Handed Sword")] = Keys("Two Hand Sword"),
        [Key("One Handed Axe")] = Keys("One Hand Axe"),
        [Key("Two Handed Axe")] = Keys("Two Hand Axe"),
        [Key("Scepter")] = Keys("Sceptre"),
        [Key("Warstaff")] = Keys("Warstaff", "Quarterstaff"),
        [Key("Weapon")] = WeaponClasses
    };

    public static bool Matches(string itemClass, string requiredClass)
    {
        string itemKey = Key(itemClass);
        string requiredKey = Key(requiredClass);
        if (string.IsNullOrWhiteSpace(itemKey) || string.IsNullOrWhiteSpace(requiredKey))
            return false;

        if (string.Equals(itemKey, requiredKey, StringComparison.OrdinalIgnoreCase))
            return true;

        return Aliases.TryGetValue(requiredKey, out var accepted) && accepted.Contains(itemKey);
    }

    public static bool MatchesAny(string itemClass, IEnumerable<string> requiredClasses)
        => requiredClasses.Any(required => Matches(itemClass, required));

    private static HashSet<string> Keys(params string[] values)
        => values.Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string Key(string value)
    {
        value = (value ?? string.Empty)
            .Replace("Armor", "Armour", StringComparison.OrdinalIgnoreCase);

        return new string(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
    }
}
