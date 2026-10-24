using Robust.Shared.Configuration;

namespace Content.Shared.HL.CCVar;

/// <summary>
/// Configuration variables for HardLight-specific features
/// </summary>
[CVarDefs]
public sealed class HLCCVars
{
    // World chunk logging / behavior
    public static readonly CVarDef<bool> WorldChunkDebugLogs =
        CVarDef.Create("hardlight.world.debug_chunk_logs", false, CVar.SERVERONLY, desc: "Enable world chunk load/unload debug logs.");
}
