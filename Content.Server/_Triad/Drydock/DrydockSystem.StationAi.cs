using Content.Shared.Mind.Components;
using Content.Shared.Silicons.StationAi;
using Robust.Shared.Containers;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The one content-specific hook in the store path, in its own file so it lifts out cleanly in a
/// fork with no station AI. Everything else here adapts to whatever content exists; this knows about
/// one thing, because that thing deliberately lives off the grid.
/// </summary>
public sealed partial class DrydockSystem
{
    [Dependency] private SharedContainerSystem _containers = default!;

    /// <summary>
    /// Empties any vacant station AI core aboard, before the grid is serialized.
    ///
    /// <para>The core points at an eye entity in null space and the brain references the same eye,
    /// so a ship carrying an AI serializes with dangling references and the round-trip check rejects
    /// it. The vacant brain and eye are deleted and the reference cleared: the physical core survives
    /// empty and takes a fresh intellicard. Minds do not ride through storage, the same rule the
    /// organics gate enforces.</para>
    ///
    /// <para>An occupied core is left alone - deleting a live AI's brain would ghost the player, and
    /// refusing that ship is the organics gate's call. Not undoable, which is why it runs after
    /// everything that is; an empty core is the intended end state anyway.</para>
    /// </summary>
    private void SanitizeStationAiCores(EntityUid gridUid)
    {
        var query = AllEntityQuery<StationAiCoreComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var core, out var xform))
        {
            if (xform.GridUid != gridUid)
                continue;

            EntityUid? brain = null;
            if (_containers.TryGetContainer(uid, StationAiHolderComponent.Container, out var container)
                && container.ContainedEntities.Count > 0)
            {
                brain = container.ContainedEntities[0];

                if (TryComp<MindContainerComponent>(brain, out var mind) && mind.HasMind)
                    continue;
            }

            // Immediate deletes, not queued. The serializer's tree walk is now many ticks away, so a
            // queued deletion would in fact be honoured long before it - but a merely queued brain is
            // a real grid child until the queue is drained, and every walk between here and the save
            // would see it. Deleting now is what makes "the AI core is empty" true from this line
            // onwards rather than at some point after it. Clear the eye reference first so the
            // still-live core never points at a deleted entity.
            if (core.RemoteEntity is { } eye)
            {
                core.RemoteEntity = null;
                Dirty(uid, core);
                Del(eye);
            }

            if (brain is { } b)
                Del(b);
        }
    }
}
