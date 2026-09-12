using Content.Shared.Storage;
using Robust.Shared.Containers;
using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Item.Location;

/// <summary>
/// Attached to an entity when it is stored in a storage container to track its location within that container.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class ItemStorageLocationComponent : Component
{
    /// <summary>
    /// The location of this item within the storage container's grid.
    /// </summary>
    [DataField]
    public ItemStorageLocation ItemLocation;
}
