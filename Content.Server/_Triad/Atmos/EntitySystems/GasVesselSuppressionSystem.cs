using Content.Server.Administration.Logs;
using Content.Server.NodeContainer.EntitySystems;
using Content.Server.NodeContainer.NodeGroups;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Popups;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Atmos.Piping.Unary.Components;
using Content.Shared.Containers.ItemSlots;
using Content.Shared.Database;
using Content.Shared._Triad.Atmos.Components;
using Content.Shared._Triad.Atmos.EntitySystems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Prototypes;
using Content.Shared.NodeContainer;

namespace Content.Server._Triad.Atmos.EntitySystems;

/// <summary>
/// Neuters gas-vessel bombs. A tank or canister whose contents approach plasma ignition temperature while holding
/// flammables has those contents converted to room-temperature water vapour, its valves seized, and any docked tank
/// fused into the canister. Vessels that fail anyway (fragmentation, destruction while charged) foam over into a
/// solid metal-foam block via <see cref="FoamOver"/> instead of exploding or venting, consuming their contents.
/// </summary>
public sealed partial class GasVesselSuppressionSystem : SharedGasVesselSuppressionSystem
{
    [Dependency] private IAdminLogManager _adminLogger = default!;
    [Dependency] private ItemSlotsSystem _itemSlots = default!;
    [Dependency] private NodeContainerSystem _nodeContainer = default!;
    [Dependency] private PopupSystem _popup = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private SharedContainerSystem _containers = default!;
    [Dependency] private SharedMapSystem _map = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    // Trips just below Atmospherics.PlasmaMinimumBurnTemperature so a burn can never start inside a vessel. The
    // classic bomb recipes hold their fuel mix above ignition temperature, so this fires during assembly.
    public const float SuppressionTemperatureFraction = 0.99f;

    // Below this many moles of plasma + tritium the contents cannot meaningfully burn; hot inert cargo (pure O2,
    // N2, ...) must survive external fires untouched.
    public const float FlammableMoleThreshold = 0.1f;

    private static readonly EntProtoId FoamPrototype = "FoamedIronMetal";

    private static readonly SoundPathSpecifier SuppressionSound = new("/Audio/Effects/extinguish.ogg");

    private const float TankCheckDelay = 0.5f;
    private float _timer;

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        _timer += frameTime;
        if (_timer < TankCheckDelay)
            return;
        _timer -= TankCheckDelay;

        var tankQuery = EntityQueryEnumerator<GasTankComponent, SafeGasCanComponent>();
        while (tankQuery.MoveNext(out var uid, out var tank, out var safety))
        {
            if (!safety.Enabled)
                continue;

            // A tank docked in a canister is handled by the canister pass, which also fuses the slot.
            if (_containers.TryGetContainingContainer((uid, null, null), out var container)
                && HasComp<GasCanisterComponent>(container.Owner))
                continue;

            if (tank.Air == null || !NeedsSuppression(tank.Air))
                continue;

            SuppressMixture(tank.Air);
            tank.IsValveOpen = false;
            AnnounceSuppression(uid);
        }

        var canisterQuery = EntityQueryEnumerator<GasCanisterComponent, SafeGasCanComponent>();
        while (canisterQuery.MoveNext(out var uid, out var canister, out var safety))
        {
            if (!safety.Enabled)
                continue;

            CheckCanister(uid, canister);
        }
    }

    private void CheckCanister(EntityUid uid, GasCanisterComponent canister)
    {
        GasMixture? tankAir = null;
        GasTankComponent? dockedTank = null;
        if (canister.GasTankSlot.Item is { } tankUid && TryComp(tankUid, out dockedTank))
            tankAir = dockedTank.Air;

        if (!NeedsSuppression(canister.Air) && (tankAir == null || !NeedsSuppression(tankAir)))
            return;

        var disconnected = EmergencyDisconnect(uid, canister);
        SuppressMixture(canister.Air);
        canister.ReleaseValve = false;

        if (tankAir != null)
        {
            SuppressMixture(tankAir);
            dockedTank?.IsValveOpen = false;
            _itemSlots.SetLock(uid, canister.GasTankSlot, true);
        }

        AnnounceSuppression(uid, disconnected);
    }

    /// <summary>
    /// Kicks a canister off its connector port. A ported canister equalizes with its pipe net every atmos tick, so
    /// suppressing it in place would scrub the whole net one canister-volume at a time; unanchoring drops the node
    /// out of the net so only the canister's own slug is neutralized.
    /// </summary>
    private bool EmergencyDisconnect(EntityUid uid, GasCanisterComponent canister)
    {
        if (!TryComp<NodeContainerComponent>(uid, out var nodeContainer)
            || !_nodeContainer.TryGetNode(nodeContainer, canister.PortName, out PortablePipeNode? portNode)
            || portNode.NodeGroup is not PipeNet { NodeCount: > 1 })
            return false;

        _transform.Unanchor(uid, Transform(uid));
        return true;
    }

    /// <summary>
    /// Whether this mixture is flammable and close enough to plasma ignition that suppression should fire.
    /// </summary>
    public static bool NeedsSuppression(GasMixture air)
    {
        if (air.Temperature < Atmospherics.PlasmaMinimumBurnTemperature * SuppressionTemperatureFraction)
            return false;

        return air.GetMoles(Gas.Plasma) + air.GetMoles(Gas.Tritium) > FlammableMoleThreshold;
    }

    /// <summary>
    /// Replaces the mixture with the same mole count of room-temperature water vapour. Mole-conserving, and since
    /// suppression only ever fires above room temperature this always lowers the mixture's pressure.
    /// </summary>
    public static void SuppressMixture(GasMixture air)
    {
        var moles = air.TotalMoles;
        air.Clear();
        air.AdjustMoles(Gas.WaterVapor, moles);
        air.Temperature = Atmospherics.T20C;
    }

    /// <summary>
    /// Turns a failing vessel's tile into a metal-foam block. The caller is responsible for deleting the vessel
    /// (and thereby its contents); nothing is vented. Skipped off-grid or when the tile already holds an occluding
    /// structure.
    /// </summary>
    public void FoamOver(EntityUid vessel)
    {
        var mapCoords = _transform.GetMapCoordinates(vessel);
        _audio.PlayPvs(SuppressionSound, vessel);
        _adminLogger.Add(LogType.Explosion, LogImpact.High,
            $"Gas vessel {ToPrettyString(vessel):entity} failed at {mapCoords:coordinates} and was foamed over instead of exploding/venting");

        if (!_map.TryFindGridAt(mapCoords, out var gridUid, out var grid))
            return;

        var tile = _map.WorldToTile(gridUid, grid, mapCoords.Position);
        foreach (var anchored in _map.GetAnchoredEntities(gridUid, grid, tile))
        {
            if (HasComp<OccluderComponent>(anchored))
                return;
        }

        Spawn(FoamPrototype, _map.GridTileToLocal(gridUid, grid, tile));
    }

    private void AnnounceSuppression(EntityUid vessel, bool disconnected = false)
    {
        _audio.PlayPvs(SuppressionSound, vessel);
        var message = disconnected ? "gas-vessel-suppression-disconnected" : "gas-vessel-suppression-triggered";
        _popup.PopupEntity(Loc.GetString(message, ("vessel", vessel)), vessel);
        _adminLogger.Add(LogType.Explosion, LogImpact.High,
            $"Internal fire suppression neutralized flammable contents of {ToPrettyString(vessel):entity} at {_transform.GetMapCoordinates(vessel):coordinates}{(disconnected ? " after an emergency disconnect from its port" : "")}");
    }
}
