using Robust.Shared.Configuration;

namespace Content.Shared.CCVar;

public sealed partial class CCVars
{
    // Triad: removed - all three of these were inert. None has a C# read site and none is set by
    // any config preset, so the verbose/progress instrumentation they were meant to gate does not
    // exist. `use_legacy_serializer` documented itself as "future use" and that future never
    // arrived. Superseded by the ship-storage replacement. Kept commented rather than deleted so
    // an upstream merge surfaces a conflict here.
    // /// <summary>
    // /// Enable verbose shipyard serialization logging (per-entity / progress). Default false for performance.
    // /// </summary>
    // public static readonly CVarDef<bool> ShipyardSaveVerbose =
    //     CVarDef.Create("shipyard.save_verbose", false, CVar.SERVERONLY);
    //
    // /// <summary>
    // /// Use legacy (hand-written) ship serialization path instead of refactored / optimized path (future use).
    // /// Currently only gates verbose instrumentation hooks.
    // /// </summary>
    // public static readonly CVarDef<bool> ShipyardUseLegacySerializer =
    //     CVarDef.Create("shipyard.use_legacy_serializer", false, CVar.SERVERONLY);
    //
    // /// <summary>
    // /// Progress interval (entity count) for emitting progress logs when verbose serialization enabled.
    // /// 0 disables progress logs.
    // /// </summary>
    // public static readonly CVarDef<int> ShipyardSaveProgressInterval =
    //     CVarDef.Create("shipyard.save_progress_interval", 0, CVar.SERVERONLY);
}
