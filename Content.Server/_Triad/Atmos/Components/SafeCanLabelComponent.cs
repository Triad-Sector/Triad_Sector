namespace Content.Server._Triad.Atmos.Components;

/// <summary>
/// Marker for gas vessels that advertise their SafeCan fire suppression on examine. Exists because the directed
/// event bus allows one ExaminedEvent subscription per component and GasTankComponent's is taken.
/// </summary>
[RegisterComponent]
public sealed partial class SafeCanLabelComponent : Component
{
    [DataField]
    public LocId Label = "gas-vessel-suppression-examine";
}
