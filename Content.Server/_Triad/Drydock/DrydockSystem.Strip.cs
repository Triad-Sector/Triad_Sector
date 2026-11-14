using System.Linq;
using System.Threading.Tasks;
using Content.Shared._Triad.Drydock;
using Content.Shared.Item;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// The retrieve's strip step: deletes every entity a <see cref="DrydockStripPrototype"/> names.
/// </summary>
/// <remarks>
/// On retrieve rather than on store, for the reason research resets there: a store that refuses after
/// its preparation cannot undo a deletion, so stripping at store would cost a player the things for
/// a store that never happened, and a retrieve-side strip also cleans every ship filed before the rule.
/// </remarks>
public sealed partial class DrydockSystem
{
    /// <summary>Every entity prototype id a strip rule matches, built from the rules on first use.</summary>
    private HashSet<string>? _stripProtos;

    private void OnStripPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (args.WasModified<DrydockStripPrototype>() || args.WasModified<EntityPrototype>())
            _stripProtos = null;
    }

    /// <summary>
    /// Resolves the strip rules into a set of entity prototype ids. Components are read off the composed
    /// prototype, so a component a parent grants counts. A rule naming a component, parent or prototype
    /// that does not exist is an error, since a typo would otherwise strip nothing in silence.
    /// </summary>
    internal HashSet<string> StripPrototypeIds()
    {
        if (_stripProtos != null)
            return _stripProtos;

        var set = new HashSet<string>();
        var factory = EntityManager.ComponentFactory;

        foreach (var rule in _protoMan.EnumeratePrototypes<DrydockStripPrototype>())
        {
            foreach (var comp in rule.Components)
            {
                if (!factory.TryGetRegistration(comp, out _))
                    Log.Error($"Drydock strip rule '{rule.ID}' names a component that does not exist: {comp}");
            }

            var parentsMatched = new HashSet<string>();

            foreach (var proto in _protoMan.EnumeratePrototypes<EntityPrototype>())
            {
                if (rule.Components.Any(proto.Components.ContainsKey))
                {
                    set.Add(proto.ID);
                    continue;
                }

                if (rule.Parents.Count == 0)
                    continue;

                foreach (var (parentId, _) in _protoMan.EnumerateAllParents<EntityPrototype>(proto.ID))
                {
                    if (!rule.Parents.Contains(parentId))
                        continue;

                    set.Add(proto.ID);
                    parentsMatched.Add(parentId);
                    break;
                }
            }

            foreach (var parent in rule.Parents)
            {
                if (!parentsMatched.Contains(parent))
                    Log.Error($"Drydock strip rule '{rule.ID}' names a parent with no concrete descendants: {parent}");
            }

            foreach (var id in rule.Prototypes)
            {
                set.Add(id);
            }
        }

        _stripProtos = set;
        return set;
    }

    /// <summary>
    /// Deletes every stripped entity aboard. What one holds that is not stripped itself and is an item
    /// (a grabber's load) is dropped where the holder stood rather than deleted with it; anything else
    /// it holds goes with it.
    /// </summary>
    /// <remarks>
    /// Mechs themselves never reach this: <c>BaseMech</c> is <c>save: false</c>, so a mech and everything
    /// inside it, pilot's cargo and power cell included, is left out of the document at store. The rule
    /// still names them, so the day that changes nothing rides through.
    /// </remarks>
    private Task StripSliced(EntityUid grid, IDrydockSlice slice)
    {
        var protos = StripPrototypeIds();

        var targets = new List<EntityUid>();
        foreach (var uid in _fidelity.GridTreeList(grid))
        {
            if (MetaData(uid).EntityPrototype is { } proto && protos.Contains(proto.ID))
                targets.Add(uid);
        }

        return SweepRaw(grid, slice, DrydockPhase.Sweeps, targets, uid =>
        {
            // Already gone with a stripped holder earlier in the list.
            if (TerminatingOrDeleted(uid))
                return;

            if (TryComp<ContainerManagerComponent>(uid, out var manager))
            {
                var drop = Transform(uid).Coordinates;
                foreach (var container in _containers.GetAllContainers(uid, manager).ToList())
                {
                    foreach (var held in container.ContainedEntities.ToList())
                    {
                        // Only things a player could carry: a mech's actions, its pilot slot machinery
                        // and the like go with it.
                        if (!HasComp<ItemComponent>(held)
                            || MetaData(held).EntityPrototype is { } heldProto && protos.Contains(heldProto.ID))
                        {
                            continue;
                        }

                        _containers.Remove(held, container, force: true, destination: drop);
                    }
                }
            }

            Del(uid);
        });
    }
}
