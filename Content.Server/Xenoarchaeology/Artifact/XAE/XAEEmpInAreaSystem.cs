using Content.Server.Emp;
using Content.Server.Xenoarchaeology.Artifact.XAE.Components;
using Content.Shared.Xenoarchaeology.Artifact;
using Content.Shared.Xenoarchaeology.Artifact.XAE;

namespace Content.Server.Xenoarchaeology.Artifact.XAE;

/// <summary>
/// System for xeno artifact effect that creates EMP on use.
/// </summary>
public sealed class XAEEmpInAreaSystem : BaseXAESystem<XAEEmpInAreaComponent>
{
    [Dependency] private readonly EmpSystem _emp = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!; // Triad

    /// <inheritdoc />
    protected override void OnActivated(Entity<XAEEmpInAreaComponent> ent, ref XenoArtifactNodeActivatedEvent args)
    {
        // Triad: EmpPulse still takes MapCoordinates on this tree. onlyGrid keeps the pulse on the
        // hull running the artifact instead of browning out whatever is docked alongside.
        var xform = Transform(ent.Owner);
        _emp.EmpPulse(
            _transform.ToMapCoordinates(args.Coordinates),
            ent.Comp.Range,
            ent.Comp.EnergyConsumption,
            ent.Comp.DisableDuration,
            onlyGrid: xform.GridUid
        );
    }
}
