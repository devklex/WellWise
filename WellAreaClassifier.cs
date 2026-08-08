using System;
using System.Collections.Generic;

namespace WellWise;

/// <summary>Pure PoE2 area classification for the Well of Souls overlay.</summary>
public static class WellAreaClassifier
{
    private static readonly HashSet<string> KnownRawNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Abyss_Hub",
        "Abyss_Pinnacle"
    };

    public static bool IsWellOfSoulsArea(string? instanceName, string? areaName, string? areaId, string? rawName)
        => KnownRawNames.Contains(areaId ?? string.Empty)
           || KnownRawNames.Contains(rawName ?? string.Empty)
           || string.Equals(instanceName, "The Well of Souls", StringComparison.OrdinalIgnoreCase)
           || string.Equals(areaName, "The Well of Souls", StringComparison.OrdinalIgnoreCase);
}
