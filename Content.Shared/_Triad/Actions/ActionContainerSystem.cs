using Content.Shared.Actions.Components;

namespace Content.Shared.Actions;

/// <summary>
/// Triad system for action containers.
/// </summary>
public sealed partial class ActionContainerSystem : EntitySystem
{
    [SubscribeLocalEvent]
    private void OnActionContainerStartup(Entity<ActionsContainerComponent> ent, ref ComponentStartup args)
    {
        // This exists to migrate old contained actions into the new format
        if (_netMan.IsClient)
            return;

        if (ent.Comp.Container == null)
            return;

        foreach (var uid in ent.Comp.Container.ContainedEntities)
        {
            if (!_query.TryComp(uid, out var action) || action.Container != null)
                continue;

            action.Container = ent.Owner;
            Dirty(uid, action);
        }
    }
}
