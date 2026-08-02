using Content.Shared.Implants.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;
using Content.Shared.Morgue.Components;

namespace Content.Server.Explosion.EntitySystems;

public sealed partial class TriggerSystem : EntitySystem
{
    public void UpdateRattleTimer()
    {
        var query = EntityQueryEnumerator<ImplantedComponent, MobStateComponent>();

        while (query.MoveNext(out var entityUid, out var implantedComponent, out var mobState))
        {
            foreach (var containerUid in implantedComponent.ImplantContainer.ContainedEntities)
            {
                if (!TryComp<RattleComponent>(containerUid, out var component))
                    continue;

                // This is our reset state
                if (mobState.CurrentState != MobState.Dead && (component.DeathTime != TimeSpan.Zero || component.NextTrigger != TimeSpan.Zero))
                {
                    component.DeathTime = TimeSpan.Zero;
                    component.NextTrigger = TimeSpan.Zero;
                    continue;
                }

                if (component.NextTrigger != TimeSpan.Zero && _timing.CurTime >= component.NextTrigger)
                {
                    if (!_container.TryGetContainingContainer(entityUid, out var container) || !HasComp<MorgueComponent>(container.Owner))
                    {
                        Trigger(containerUid);
                    }
                    else
                    {
                        component.NextTrigger = _timing.CurTime + component.RetriggerDelay;
                    }
                }
            }
        }
    }
}
