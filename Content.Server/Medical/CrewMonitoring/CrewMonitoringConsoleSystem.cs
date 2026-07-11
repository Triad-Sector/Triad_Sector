using System.Linq;
using Content.Server._NF.CryoSleep;
using Content.Server.Chat.Systems;
using Content.Server.Medical.SuitSensors;
using Content.Server.PowerCell;
using Content.Shared.Damage;
using Content.Shared.DeviceNetwork;
using Content.Shared.DeviceNetwork.Events;
using Content.Shared.Inventory;
using Content.Shared.Medical.CrewMonitoring;
using Content.Shared.Medical.SuitSensor;
using Content.Shared.Mobs.Components;
using Content.Shared.Pinpointer;
using Robust.Server.GameObjects;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers;
using Robust.Shared.Map;
using Robust.Shared.Timing;

namespace Content.Server.Medical.CrewMonitoring;

public sealed class CrewMonitoringConsoleSystem : EntitySystem
{
    [Dependency] private readonly PowerCellSystem _cell = default!;
    [Dependency] private readonly UserInterfaceSystem _uiSystem = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!; // Triad
    [Dependency] private readonly InventorySystem _inventory = default!; // Triad
    [Dependency] private readonly IGameTiming _timing = default!; // Triad
    [Dependency] private readonly ChatSystem _chat = default!; // Triad

    private readonly List<EntityCoordinates> _coords = new();

    public override void Initialize()
    {
        base.Initialize();
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, ComponentRemove>(OnRemove);
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, DeviceNetworkPacketEvent>(OnPacketReceived);
        SubscribeLocalEvent<CrewMonitoringConsoleComponent, BoundUIOpenedEvent>(OnUIOpened);
        SubscribeLocalEvent<MobStateComponent, DamageChangedEvent>(OnDamageChanged); // Triad
    }

    private void OnRemove(EntityUid uid, CrewMonitoringConsoleComponent component, ComponentRemove args)
    {
        component.ConnectedSensors.Clear();
    }

    private void OnPacketReceived(EntityUid uid, CrewMonitoringConsoleComponent component, DeviceNetworkPacketEvent args)
    {
        var payload = args.Data;

        // Check command
        if (!payload.TryGetValue(DeviceNetworkConstants.Command, out string? command))
            return;

        if (command != DeviceNetworkConstants.CmdUpdatedState)
            return;

        if (!payload.TryGetValue(SuitSensorConstants.NET_STATUS_COLLECTION, out Dictionary<string, SuitSensorStatus>? sensorStatus))
            return;

        component.ConnectedSensors = sensorStatus;
        UpdateUserInterface(uid, component);
    }

    private void OnUIOpened(EntityUid uid, CrewMonitoringConsoleComponent component, BoundUIOpenedEvent args)
    {
        if (!_cell.TryUseActivatableCharge(uid))
            return;

        UpdateUserInterface(uid, component);
    }
    // Triad
    //Over TriggerSndDamageThreshold damage? Play a sound to add to overall dread. (explained in PR #192)
    //Over 128 crew consoles? Start skipping everything to preservere resourses.
    //Dont play that sound if it is in a sound radius of another crew monitor to stop really loud sounds.
    //Dont play that sound if there is a cooldown to stop really loud sounds.
    //No sounds if we dont have a sensor; how would the crew console get it IC?
    //Send a IC message saying what crew got hurt so they can know.

    /// <summary>
    /// Triad - Main loop where we do most of the work for processing each Crew Console entity UID to play a sound at that point.
    /// </summary>
    private void EnumarateCrewConsoles(EntityUid eu, DamageSpecifier damageDelta)
    {
        _coords.Clear();

        var max = 0;
        var query = EntityQueryEnumerator<CrewMonitoringConsoleComponent, TransformComponent>();
        while (query.MoveNext(out var uid, out var monitorComp, out var xform))
        {
            if (max > 128) //if there are more than 128 crew monitoring consoles just ignore it to save processing power.
                continue;

            if (damageDelta.GetTotal().Value < monitorComp.TriggerSndDamageThreshold * 100) //if the change in damage was not over TriggerSndDamageThreshold, skip it (It is a FixedPoint2 so the value is shifted left twice then rounded)
                continue;

            if (monitorComp.WarningSound == null)
                continue;

            var transf = xform;
            var coord1 = transf.Coordinates;
            var skip = false;

            // If dist between 2 crew monitors is less than the sound radius, skip playing it since you can already hear it from the other comp.
            foreach (var coordinate in _coords)
            {
                if (coord1.TryDistance(EntityManager, coordinate, out var dist) && dist < monitorComp.WarningSound.Params.MaxDistance)
                {
                    skip = true;
                    break;
                }
            }
            _coords.Add(coord1);

            if (skip)
                continue;

            //check if we have the string first.
            if (!Loc.TryGetString("message-crit-damage-crew-monitor", out string? message, ("user", MetaData(eu).EntityName)))
                continue;

            if (message == null)
                continue;

            //if next sound is new, play it like normal and set the next sound interval. If it is old, do the same.
            if (monitorComp.NextSound == null || _timing.CurTime >= monitorComp.NextSound)
            {
                monitorComp.MultipleDeathsTime = _timing.CurTime; //create new curtime
                monitorComp.NextSound = _timing.CurTime + monitorComp.Cooldown;
                _audio.PlayPvs(monitorComp.WarningSound, uid);
                _chat.TrySendInGameICMessage(uid, message, Shared.Chat.InGameICChatType.Speak, hideChat: true);
            }

            else if (monitorComp.MultipleDeathsTime != null && _timing.CurTime < monitorComp.MultipleDeathsTime + monitorComp.ProcessDelay) //if there are multiple deaths per time, log them all.
                _chat.TrySendInGameICMessage(uid, message, Shared.Chat.InGameICChatType.Speak, hideChat: true);

            else
                monitorComp.MultipleDeathsTime = null; //get rid of curtime for loop

            max++; //if there are more than 128 crew monitoring consoles just ignore it
        }
    }

    /// <summary>
    /// Triad - Checks if a child of the damaged person has a suit with sensors on them, if so we skip them because it doesnt really make sense for it to go off when you have no sensors.
    /// <summary>
    private bool FindSuit(InventorySystem.InventorySlotEnumerator enu)
    {
        while (enu.MoveNext(out ContainerSlot? mightBeSuit))
        {
            if (mightBeSuit != null && mightBeSuit.ContainedEntity != null && TryComp<SuitSensorComponent>(mightBeSuit.ContainedEntity, out SuitSensorComponent? suit))
            {
                if (suit.Mode != SuitSensorMode.SensorOff && !suit.Jammed)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Triad - When an entity takes over TriggerSndDamageThreshold damage, play a sound to warn med if it meets the critera:
    /// Over 128 crew consoles? Start skipping everything to preservere resourses.
    /// Dont play that sound if it is in the sound radius of another crew monitor to stop really loud sounds.
    /// Dont play that sound if there is a cooldown to stop really loud sounds.
    /// No sounds if we dont have a sensor; how would the crew console get it IC?
    /// Also Send a IC message saying what crew got hurt so they can know.
    /// <summary>
    private void OnDamageChanged(EntityUid eu, MobStateComponent mobState, DamageChangedEvent args)
    {
        if (!HasComp<PlayerJobComponent>(eu)) //only player jobs count! Guard clause to skip non-crew.
            return;

        if (args.DamageDelta == null) //if its null there is no damage.
            return;

        var suitFinder = _inventory.GetSlotEnumerator(eu, SlotFlags.INNERCLOTHING);
        if (!FindSuit(suitFinder))// if the mob doesnt have a suit sensor, how would it play IC?
            return;

        if (args.DamageDelta != null)
        {
            DamageSpecifier damageDelta = args.DamageDelta;
            EnumarateCrewConsoles(eu, damageDelta); //do main loop for most proccesing
        }
    }
    //end Triad
    private void UpdateUserInterface(EntityUid uid, CrewMonitoringConsoleComponent? component = null)
    {
        if (!Resolve(uid, ref component))
            return;

        if (!_uiSystem.IsUiOpen(uid, CrewMonitoringUIKey.Key))
            return;

        // The grid must have a NavMapComponent to visualize the map in the UI
        var xform = Transform(uid);

        if (xform.GridUid != null)
            EnsureComp<NavMapComponent>(xform.GridUid.Value);

        // Update all sensors info
        var allSensors = component.ConnectedSensors.Values.ToList();
        _uiSystem.SetUiState(uid, CrewMonitoringUIKey.Key, new CrewMonitoringState(allSensors));
    }
}
