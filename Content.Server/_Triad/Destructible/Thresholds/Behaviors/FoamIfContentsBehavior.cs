using Content.Server._Triad.Atmos.EntitySystems;
using Content.Server.Destructible;
using Content.Server.Destructible.Thresholds.Behaviors;
using GasCanisterComponent = Content.Shared.Atmos.Piping.Unary.Components.GasCanisterComponent;

namespace Content.Server._Triad.Destructible.Thresholds.Behaviors;

/// <summary>
/// Foams a destroyed gas canister over instead of venting it, but only when it actually holds gas - an empty
/// canister just breaks into wreckage. The contents are consumed with the entity; nothing reaches the room.
/// </summary>
[Serializable]
[DataDefinition]
public sealed partial class FoamIfContentsBehavior : IThresholdBehavior
{
    [DataField]
    public float MinMoles = 1f;

    public void Execute(EntityUid owner, DestructibleSystem system, EntityUid? cause = null)
    {
        if (!system.EntityManager.TryGetComponent<GasCanisterComponent>(owner, out var canister))
            return;

        if (canister.Air.TotalMoles < MinMoles)
            return;

        system.EntityManager.System<GasVesselSuppressionSystem>().FoamOver(owner);
    }
}
