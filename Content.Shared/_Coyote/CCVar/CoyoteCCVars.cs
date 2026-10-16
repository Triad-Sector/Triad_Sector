using Content.Shared.Administration;
using Content.Shared.CCVar.CVarAccess;
using Robust.Shared.Configuration;
using Robust.Shared.Reflection;

namespace Content.Shared._Coyote.CCVar;

/// <summary>
/// CVar definitions used by Coyote systems.
/// </summary>

[Reflect(true)]
[CVarDefs]
public sealed class CoyoteCCVars
{
    /// <summary>
    /// Base delay (in seconds) before shuttle IFF status visual updates are applied.
    /// Example: if set to 20, color changes wait at least 20 seconds.
    /// </summary>
    [CVarControl(AdminFlags.Admin)]
    public static readonly CVarDef<int> ShuttleCrewUpdateBaseDelaySeconds =
        CVarDef.Create("coyote.shuttle.crew_update_base_delay", 20, CVar.SERVERONLY);

    /// <summary>
    /// Extra randomized delay range (in seconds) added on top of base delay.
    /// </summary>
    [CVarControl(AdminFlags.Admin)]
    public static readonly CVarDef<int> ShuttleCrewUpdateRandomJitterSeconds =
        CVarDef.Create("coyote.shuttle.crew_update_random_jitter", 25, CVar.SERVERONLY);

    /// <summary>
    /// Hex color used when a player shuttle has no active crew.
    /// Default is gray (#808080).
    /// </summary>
    [CVarControl(AdminFlags.Admin)]
    public static readonly CVarDef<string> ShuttleInactiveCrewColor =
        CVarDef.Create("coyote.shuttle.inactive_crew_color", "#808080", CVar.SERVERONLY);

    /// <summary>
    /// Enables verbose logging for shuttle crew status logic.
    /// </summary>
    [CVarControl(AdminFlags.Admin)]
    public static readonly CVarDef<bool> ShuttleCrewStatusDebug =
        CVarDef.Create("coyote.shuttle.crew_status_debug", false, CVar.SERVERONLY);
}
