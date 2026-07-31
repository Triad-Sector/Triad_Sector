namespace Content.Shared._Triad.ContrabandPermit;

public abstract partial class SharedContrabandPermitSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        InitializeConsole();
        InitializePermitChip();
    }
}
