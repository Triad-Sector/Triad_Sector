using Content.Shared.Xenoarchaeology.Artifact.Components;
using Content.Shared.Xenoarchaeology.Artifact.XAT.Components;

namespace Content.Shared.Xenoarchaeology.Artifact.XAT;

/// <summary>
/// System for xeno artifact trigger that requires some entity/entities with certain component on them nearby.
/// </summary>
public sealed class XATCompNearbySystem : BaseQueryUpdateXATSystem<XATCompNearbyComponent>
{
    [Dependency] private readonly EntityLookupSystem _entityLookup = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;

    /// <summary> Pre-allocated and re-used collection.</summary>
    private readonly HashSet<Entity<IComponent>> _entities = new();

    /// <inheritdoc />
    protected override void UpdateXAT(
        Entity<XenoArtifactComponent> artifact,
        Entity<XATCompNearbyComponent, XenoArtifactNodeComponent> node,
        float frameTime
    )
    {
        var compNearbyComponent = node.Comp1;

        var pos = _transform.GetMapCoordinates(artifact);
        var comp = EntityManager.ComponentFactory.GetRegistration(compNearbyComponent.RequireComponentWithName);

        _entities.Clear();
        _entityLookup.GetEntitiesInRange(comp.Type, pos, compNearbyComponent.Radius, _entities);

        // Triad: the lookup is a circle on the map, so a docked neighbour's cargo counts toward the
        // required tally. Only what is aboard the artifact's own hull should.
        var xform = Transform(artifact.Owner);
        var count = 0;
        foreach (var candidate in _entities)
        {
            if (XenoArtifact.IsGridLocal(xform, candidate))
                count++;
        }

        if (count >= compNearbyComponent.Count)
            Trigger(artifact, node);
    }
}
