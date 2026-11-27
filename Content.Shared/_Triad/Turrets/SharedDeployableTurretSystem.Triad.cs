namespace Content.Shared.Turrets;

public abstract partial class SharedDeployableTurretSystem : EntitySystem
{
    [SubscribeLocalEvent]
    private void OnMapInit(Entity<DeployableTurretComponent> ent, ref MapInitEvent args)
    {
        if (!ent.Comp.StartEnabled)
            return;

        SetState(ent, true);
    }
}
