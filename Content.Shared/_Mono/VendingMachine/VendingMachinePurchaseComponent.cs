namespace Content.Shared._Mono.VendingMachine;

/// <summary>
/// Component that tracks entities purchased from vending machines.
/// Used to apply pricing modifications when selling through cargo pallet consoles.
/// </summary>
/// <remarks>
/// Triad: server-only tracking, deliberately NOT networked. <see cref="PurchaseGrid"/> is a raw
/// <see cref="EntityUid"/>; networking it crashed PVS state serialization every tick once the
/// origin grid was deleted (the auto-generated OnGetState tried to resolve a NetEntity for a dead
/// uid and threw). Nothing on the client reads this, so the fix is to drop networking entirely.
/// </remarks>
[RegisterComponent]
public sealed partial class VendingMachinePurchaseComponent : Component
{
    /// <summary>
    /// The grid ID where this entity was purchased from a vending machine.
    /// Used to determine if the same-grid discount should apply.
    /// </summary>
    [DataField]
    public EntityUid PurchaseGrid;

    /// <summary>
    /// The original purchase price from the vending machine.
    /// Stored for reference and potential future features.
    /// </summary>
    [DataField]
    public double OriginalPurchasePrice;
}
