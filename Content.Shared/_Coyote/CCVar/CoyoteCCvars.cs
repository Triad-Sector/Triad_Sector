using Robust.Shared.Configuration;

namespace Content.Shared._Coyote.CCVar;

/// <summary>
/// CVar definitions used by Coyote systems.
/// </summary>
[CVarDefs]
public sealed class CoyoteCCVars
{
    /// <summary>
    /// Base delay (in seconds) before shuttle IFF status visual updates are applied.
    /// Example: if set to 20, color changes wait at least 20 seconds.
    /// </summary>
    public static readonly CVarDef<int> ShuttleCrewUpdateBaseDelaySeconds =
        CVarDef.Create("coyote.shuttle.crew_update_base_delay", 20, CVar.SERVER);

    /// <summary>
    /// Extra randomized delay range (in seconds) added on top of base delay.
    /// </summary>
    public static readonly CVarDef<int> ShuttleCrewUpdateRandomJitterSeconds =
        CVarDef.Create("coyote.shuttle.crew_update_random_jitter", 25, CVar.SERVER);

    /// <summary>
    /// Hex color used when a player shuttle has no active crew.
    /// Default is gray (#808080).
    /// </summary>
    public static readonly CVarDef<string> ShuttleInactiveCrewColor =
        CVarDef.Create("coyote.shuttle.inactive_crew_color", "#808080", CVar.SERVER);
}
