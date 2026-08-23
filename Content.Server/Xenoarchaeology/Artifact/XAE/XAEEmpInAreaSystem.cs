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
        // Triad: EmpPulse still takes MapCoordinates on this tree
        _emp.EmpPulse(_transform.ToMapCoordinates(args.Coordinates), ent.Comp.Range, ent.Comp.EnergyConsumption, ent.Comp.DisableDuration);
    }
}
