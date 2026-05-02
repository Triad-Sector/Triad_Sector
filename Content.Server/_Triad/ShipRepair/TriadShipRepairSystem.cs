using Content.Server.Shuttles.Components;
using Content.Shared._Mono.ShipRepair;
using Content.Shared._Triad.Shipyard.Save;

// Suppress naming style rule for the _Triad namespace
#pragma warning disable IDE1006

namespace Content.Server._Triad.ShipRepair;

public sealed class TriadShipRepairSystem : EntitySystem
{
    [Dependency] private readonly SharedShipRepairSystem _shipRepair = default!;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<ShuttleComponent, ShipyardShuttleLoadEvent>(OnShipLoad);
    }

    private void OnShipLoad(Entity<ShuttleComponent> ent, ref ShipyardShuttleLoadEvent ev)
    {
        _shipRepair.GenerateRepairData(ent);
    }
}
