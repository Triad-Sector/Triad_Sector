using Content.Shared.Popups;
using Content.Shared.Xenoarchaeology.Artifact.XAE.Components;
using Robust.Shared.Containers;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Shared.Xenoarchaeology.Artifact.XAE;

public sealed class XAERandomTeleportInvokerSystem : BaseXAESystem<XAERandomTeleportInvokerComponent>
{
    [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly SharedTransformSystem _xform = default!;
    [Dependency] private readonly SharedContainerSystem _container = default!;
    [Dependency] private readonly IGameTiming _timing = default!;

    /// <summary>
    /// Triad: how many destinations to sample before giving up on finding one still on the artifact's
    /// own grid. A blink that lands the artifact in space is a lost artifact, not a hazard.
    /// </summary>
    private const int GridSampleAttempts = 8;

    /// <inheritdoc />
    protected override void OnActivated(Entity<XAERandomTeleportInvokerComponent> ent, ref XenoArtifactNodeActivatedEvent args)
    {
        if (!_timing.IsFirstTimePredicted)
            return;

        var component = ent.Comp;

        // Triad: ent.Owner is the NODE, which lives inside the artifact's node-container. Offsetting
        // its coordinates only nudged it around inside that container, so the blink moved nothing and
        // the effect was a popup with a danger rating attached. Move the artifact.
        var artifact = args.Artifact.Owner;

        // A held or bagged artifact has to leave the container before it can go anywhere, otherwise
        // SetCoordinates rewrites the parent out from under the container that is holding it.
        if (_container.IsEntityInContainer(artifact))
            _container.TryRemoveFromContainer(artifact, force: true);

        var xform = Transform(artifact);
        _popup.PopupCoordinates(Loc.GetString("blink-artifact-popup"), xform.Coordinates, PopupType.Medium);

        var origin = xform.Coordinates;
        var grid = xform.GridUid;

        for (var i = 0; i < GridSampleAttempts; i++)
        {
            var candidate = origin.Offset(_random.NextVector2(component.MinRange, component.MaxRange));

            // Off-grid artifacts have nothing to stay on, so any destination will do. On a ship, the
            // artifact has to still be aboard when it lands.
            if (grid != null && _xform.GetGrid(candidate) != grid)
                continue;

            _xform.SetCoordinates(artifact, candidate);
            return;
        }
    }
}
