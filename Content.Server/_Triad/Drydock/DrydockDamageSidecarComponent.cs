using Content.Shared.Damage;
using Content.Shared.FixedPoint;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// A copying sidecar carrying one entity's damage across a store and retrieve.
/// <see cref="DamageableComponent.Damage"/> is read-only to the serializer, so a damaged ship would
/// come back pristine - a free repair on every combat vessel. Applied and removed by an explicit
/// pass once the grid has materialized; the component's presence is the marker.
/// </summary>
/// <remarks>
/// Holds the raw dictionary because <see cref="DamageSpecifier.DamageDict"/> is read-only too: a
/// DamageSpecifier-typed field serializes empty whatever this declares.
/// </remarks>
[RegisterComponent]
public sealed partial class DrydockDamageSidecarComponent : Component
{
    [DataField]
    public Dictionary<string, FixedPoint2> DamageDict = new();
}
