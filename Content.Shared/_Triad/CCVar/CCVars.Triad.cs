
using Robust.Shared.Configuration;

namespace Content.Shared._Triad.CCVar;

/// <summary>
/// Configuration variables for Triad features
/// </summary>
[CVarDefs]
public sealed class TriadCCVars
{
    /// <summary>
    ///     How much the ship cost will be. 0.3f = 30% of full appraisal
    /// </summary>
    public static readonly CVarDef<float> LoadShipPrice =
        CVarDef.Create("triad.load_ship_price", 0.3f, CVar.SERVERONLY);
}
