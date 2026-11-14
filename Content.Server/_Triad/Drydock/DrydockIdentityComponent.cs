using System;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// A ship's persistent identity, on the grid itself so it rides the document and outlives a round.
/// Deliberately not on the shuttle deed: that is another fork's component, so a field on it
/// conflicts on every pull, and a deed can be dropped or reissued while identity cannot - restore
/// mints a fresh deed against an existing ship id.
/// </summary>
[RegisterComponent]
public sealed partial class DrydockIdentityComponent : Component
{
    /// <summary>
    /// Text on purpose: the engine serializer has no <see cref="Guid"/> writer and drops a
    /// Guid-typed data field silently, losing the one piece of state nothing else on the grid
    /// carries. The yaml key is pinned, because renaming the field would orphan every stored ship.
    /// </summary>
    [DataField("shipId", required: true)]
    public string RawShipId = string.Empty;

    /// <summary>
    /// Not a data field: the text above is what persists. An unparseable value reads as
    /// <see cref="Guid.Empty"/>, which the store treats as "no identity yet" and mints over.
    /// </summary>
    public Guid ShipId
    {
        get => Guid.TryParse(RawShipId, out var parsed) ? parsed : Guid.Empty;
        set => RawShipId = value.ToString("D");
    }
}
