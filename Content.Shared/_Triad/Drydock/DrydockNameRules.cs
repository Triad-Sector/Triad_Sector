// SPDX-FileCopyrightText: 2026 Triad Sector
//
// SPDX-License-Identifier: AGPL-3.0-or-later

using Content.Shared._NF.Shipyard.Components;

namespace Content.Shared._Triad.Drydock;

/// <summary>
/// The rules the shipyard console, the drydock admin panel and the registry all apply to a stored
/// ship's display name: what shape a new name may take, and how to split a full name back into its
/// name and callsign suffix for display. One copy so the client's greyed button and card titles
/// can never drift from what the server actually stores.
/// </summary>
public static class DrydockNameRules
{
    /// <summary>
    /// The one shape a stored ship's name may take. Callers that build the name interactively (the
    /// rename prompt) validate the same trimmed text they are about to send; the store validates
    /// whatever it receives, which is why a leading or trailing space still fails here.
    /// </summary>
    public static bool IsValidStoredShipName(string name)
    {
        if (name.Length == 0 || name.Length > ShuttleDeedComponent.MaxNameLength)
            return false;

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != ' ' && c != '-')
                return false;
        }

        return name.Trim().Length == name.Length;
    }

    /// <summary>
    /// The shipyard's own rule for telling a callsign suffix from the rest of a name
    /// (<c>ShipyardSystem.TryParseShuttleName</c>): the last word is the suffix when it holds a dash
    /// and is shorter than the deed's suffix limit, otherwise there is no suffix and the whole
    /// string is the name.
    /// </summary>
    public static (string Name, string? Suffix) SplitShuttleName(string fullName)
    {
        var cut = fullName.LastIndexOf(' ');
        if (cut <= 0)
            return (fullName, null);

        var last = fullName[(cut + 1)..];
        return last.Length < ShuttleDeedComponent.MaxSuffixLength && last.Contains('-')
            ? (fullName[..cut], last)
            : (fullName, null);
    }
}
