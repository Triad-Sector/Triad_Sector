// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;

namespace Content.Shared._Triad.ShipSize;

/// <summary>
/// The pure size-class rules the drydock berth pickers apply on both ends of the wire: whether a
/// hull's class parses, whether it fits a berth's class, the next class up for an upgrade quote,
/// and the "smallest free berth that fits, then by id" ordering every free-berth picker walks. One
/// copy so the client's preview and the server's actual pick can never drift.
/// </summary>
public static class ShipSizeRules
{
    /// <summary>
    /// Classes are stored and sent as text so a taxonomy change cannot invalidate a row or a
    /// message; parsing happens here, once. A string that isn't a defined member (including a bare
    /// number, which <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/> would otherwise accept)
    /// fails rather than aliasing onto whichever class happens to share that ordinal.
    /// </summary>
    public static bool TryParseClass(string? text, out ShipSizeClass sizeClass)
    {
        return Enum.TryParse(text, ignoreCase: false, out sizeClass) && Enum.IsDefined(sizeClass);
    }

    /// <summary>
    /// A berth takes any hull of its own class or smaller. Anything that doesn't parse on either
    /// side fits nothing, never a crash.
    /// </summary>
    public static bool Fits(string? hullClass, string? berthClass)
    {
        return TryParseClass(hullClass, out var hull)
            && TryParseClass(berthClass, out var max)
            && hull <= max;
    }

    /// <summary>The next class up, or null from the top of the ladder. Drives an upgrade's price quote.</summary>
    public static ShipSizeClass? NextSizeClass(ShipSizeClass sizeClass)
    {
        return sizeClass == ShipSizeClass.SuperCapital ? null : sizeClass + 1;
    }

    /// <summary>
    /// The order every free-berth picker applies once a set of berths has been filtered to "free and
    /// fits": smallest class first, then by id, so the big berths stay available for the hulls that
    /// actually need them.
    /// </summary>
    public static IOrderedEnumerable<T> OrderByFitPreference<T>(
        IEnumerable<T> berths,
        Func<T, string?> maxSizeClass,
        Func<T, int> berthId)
    {
        return berths
            .OrderBy(b => TryParseClass(maxSizeClass(b), out var max) ? (int)max : int.MaxValue)
            .ThenBy(berthId);
    }

    /// <summary>
    /// The berth id a plain store or a reclaim lands in, from a fitting list already in
    /// <see cref="OrderByFitPreference{T}"/> order: the ship's own last berth when it is among them,
    /// else the first, which is the smallest. Null when nothing fits.
    /// </summary>
    public static int? PreferredBerth(IReadOnlyList<int> fittingOrdered, int? lastBerthId)
    {
        if (fittingOrdered.Count == 0)
            return null;

        return lastBerthId is { } last && fittingOrdered.Contains(last) ? last : fittingOrdered[0];
    }
}
