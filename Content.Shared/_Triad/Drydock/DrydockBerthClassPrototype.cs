using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Drydock;

/// <summary>
/// The price of a drydock berth for one hull size class. The id is the <c>ShipSizeClass</c> name,
/// so the ladder in YAML and the enum in code cannot drift apart without a lookup failing loudly.
/// </summary>
[Prototype]
public sealed partial class DrydockBerthClassPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>What a berth of this class costs to buy. Upgrades charge the difference.</summary>
    [DataField(required: true)]
    public int Price;
}
