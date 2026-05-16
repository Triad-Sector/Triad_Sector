using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Shipyard.Save;

/// <summary>
/// Defines the limits of how many entities with the same limit ID can be saved when saving a ship.
/// </summary>
[Prototype]
public sealed partial class ShipSaveLimitsPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    [DataField]
    public Dictionary<string, int> Limits { get; private set; } = new();
}
