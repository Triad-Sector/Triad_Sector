using Content.Shared.Xenoarchaeology.Artifact.XAE.Components;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Systems;

namespace Content.Shared.Xenoarchaeology.Artifact.XAE;

/// <summary>
/// System for xeno artifact effect that make artifact pass through other objects.
/// </summary>
public sealed class XAERemoveCollisionSystem : BaseXAESystem<XAERemoveCollisionComponent>
{
    [Dependency] private readonly SharedPhysicsSystem _physics = default!;

    /// <inheritdoc />
    protected override void OnActivated(Entity<XAERemoveCollisionComponent> ent, ref XenoArtifactNodeActivatedEvent args)
    {
        // Triad: ent.Owner is the NODE, an effect-only entity with no Fixtures, so this bailed every
        // time and the phasing effect burned its one durability on nothing. The artifact is the thing
        // that phases.
        var artifact = args.Artifact.Owner;

        if (!TryComp<FixturesComponent>(artifact, out var fixtures))
            return;

        foreach (var fixture in fixtures.Fixtures.Values)
        {
            _physics.SetHard(artifact, fixture, false, fixtures);
        }
    }
}
