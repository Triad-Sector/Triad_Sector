using Content.Shared.Atmos;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// A copying sidecar carrying each of an entity's pipe nodes' share of its net's gas across a store
/// and retrieve. A pipe net's air lives on the node-group object graph rather than on any entity,
/// so the serializer never sees it and a stored ship would come back with empty pipes.
///
/// <para>Distributed by pipe volume at store, merged back and removed on the first node-group
/// rebuild the reloaded grid sees. The component's own presence is the marker.</para>
///
/// <para>Keyed by NODE name, not by entity: a pump, mixer or crystallizer has two or three nodes in
/// as many nets, and one mixture per entity leaks gas between them.</para>
/// </summary>
[RegisterComponent]
public sealed partial class DrydockPipeGasComponent : Component
{
    [DataField]
    public Dictionary<string, GasMixture> Shares = new();
}
