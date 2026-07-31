using Content.Shared.Implants.Components;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Components;

namespace Content.Server.Explosion.EntitySystems;

public sealed partial class TriggerSystem : EntitySystem
{
    public void UpdateRattleTimer()
    {
        var query = EntityQueryEnumerator<ImplantedComponent, MobStateComponent>();

        while (query.MoveNext(out var implantedComponent, out var mobState))
        {
            foreach (var entityUid in implantedComponent.ImplantContainer.ContainedEntities)
            {
                if (!TryComp<RattleComponent>(entityUid, out var component))
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
                    Trigger(entityUid);
                }
            }
        }
    }
}
