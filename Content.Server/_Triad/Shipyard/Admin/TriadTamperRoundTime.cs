using System;

namespace Content.Server._Triad.Shipyard.Admin;

public static class TriadTamperRoundTime
{
    /// <summary>
    /// Converts round-relative time offsets into the absolute UTC timestamp window the audit query
    /// filters on. The filter boxes are "time into the round", so each bound is the round's StartDate
    /// plus the offset. A round with no recorded StartDate can't be anchored, so it yields no window
    /// (the round-time filter is simply dropped rather than producing a wrong one).
    /// </summary>
    public static (DateTime? From, DateTime? To) AnchorRoundTime(DateTime? roundStart, TimeSpan? from, TimeSpan? to)
    {
        if (roundStart is not { } start)
            return (null, null);

        var fromAbs = from.HasValue ? start + from.Value : (DateTime?)null;
        var toAbs = to.HasValue ? start + to.Value : (DateTime?)null;
        return (fromAbs, toAbs);
    }
}
