using Content.Shared._Triad.ShipSize;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Drydock;

/// <summary>
/// The berth class a shipyard vessel is sold with. The id is the vessel prototype id, and the class
/// is measured from the vessel's grid file, so a purchase quotes and charges from this row before
/// the hull exists. DrydockVesselClassTableTest loads every hull and fails on a row that disagrees
/// with the grid.
/// </summary>
[Prototype]
public sealed partial class DrydockVesselClassPrototype : IPrototype
{
    [IdDataField]
    public string ID { get; private set; } = default!;

    /// <summary>The hull class the vessel's grid measures as.</summary>
    [DataField(required: true)]
    public ShipSizeClass Class;
}

/// <summary>
/// The berth half of a vessel's shipyard price, read the same way on both sides so the listing's
/// total and the purchase's charge come from one lookup.
/// </summary>
public static class DrydockVesselBerths
{
    /// <summary>The vessel's berth class from the table, false when it has no row.</summary>
    public static bool TryGetClass(IPrototypeManager prototypes, string vesselId, out ShipSizeClass sizeClass)
    {
        if (prototypes.TryIndex<DrydockVesselClassPrototype>(vesselId, out var row))
        {
            sizeClass = row.Class;
            return true;
        }

        sizeClass = default;
        return false;
    }

    /// <summary>The price of a berth of a class, from the <c>drydockBerthClass</c> ladder. Zero when the ladder has no entry.</summary>
    public static int BerthPrice(IPrototypeManager prototypes, ShipSizeClass sizeClass)
    {
        return prototypes.TryIndex<DrydockBerthClassPrototype>(sizeClass.ToString(), out var proto) ? proto.Price : 0;
    }

    /// <summary>The berth price for a vessel from its table row, false when the vessel has no row.</summary>
    public static bool TryGetBerthPrice(IPrototypeManager prototypes, string vesselId, out int price)
    {
        if (TryGetClass(prototypes, vesselId, out var sizeClass))
        {
            price = BerthPrice(prototypes, sizeClass);
            return true;
        }

        price = 0;
        return false;
    }
}
