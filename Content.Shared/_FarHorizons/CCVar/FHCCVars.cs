using Robust.Shared.Configuration;

namespace Content.Shared._FarHorizons.CCVar;

/// <summary>
/// Host for the Far Horizons CVars this fork carries. Upstream declares many more on this class;
/// only the partials we have actually ported live here, so the reactor tuning knobs keep their
/// upstream names and a future rollup applies without a rename.
/// </summary>
[CVarDefs]
public sealed partial class FHCCVars
{
}
