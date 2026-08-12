using System.Numerics;
using Content.Server.GameTicking;
using Content.Server.Salvage.Expeditions;
using Content.Server.Salvage.Expeditions.Structure;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Station.Components;
using Content.Shared.Chat;
using Content.Shared.Humanoid;
using Content.Shared.Mobs.Components;
using Content.Shared.Mobs.Systems;
using Content.Shared.Salvage.Expeditions;
using Robust.Shared.Map;
using Content.Shared.Shuttles.Components;
using Content.Shared.Localizations;
using Robust.Shared.Map.Components;
using Robust.Shared.Player;
using Content.Server.Body.Components;
using Content.Server.Buckle.Systems;
using Content.Server.Temperature.Components;
using Content.Server.Temperature.Systems;
using Content.Shared.Atmos;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Mind.Components;
using Robust.Server.Player;
using Robust.Shared.Audio;
using Robust.Shared.Enums; // Frontier
using Robust.Shared.Utility;
using Content.Shared._Triad;
using Content.Server.Warps;
using Content.Shared.Inventory;
using Content.Shared.Damage.Systems;

namespace Content.Server.Salvage;

public sealed partial class SalvageSystem
{
    /*
     * Handles actively running a salvage expedition.
     */
    [Dependency] private BuckleSystem _buckle = default!;
    [Dependency] private IPlayerManager _players = default!;
    [Dependency] private InventorySystem _inventorySystem = default!;
    [Dependency] private StaminaSystem _stamina = default!;
    [Dependency] private TemperatureSystem _temperature = default!;
    [Dependency] private MobStateSystem _mobState = default!;
    [Dependency] private GameTicker _gameTicker = default!;
    private void InitializeRunner()
    {
        SubscribeLocalEvent<FTLRequestEvent>(OnFTLRequest);
        SubscribeLocalEvent<FTLStartedEvent>(OnFTLStarted);
        SubscribeLocalEvent<FTLCompletedEvent>(OnFTLCompleted);
        SubscribeLocalEvent<ConsoleFTLAttemptEvent>(OnConsoleFTLAttempt);
    }

    private void OnConsoleFTLAttempt(ref ConsoleFTLAttemptEvent ev)
    {
        if (!TryComp(ev.Uid, out TransformComponent? xform) ||
            !TryComp<SalvageExpeditionComponent>(xform.MapUid, out var salvage))
        {
            return;
        }

        // TODO: This is terrible but need bluespace harnesses or something.
        var query = EntityQueryEnumerator<HumanoidAppearanceComponent, MobStateComponent, TransformComponent>();

        while (query.MoveNext(out var uid, out var _, out var mobState, out var mobXform))
        {
            if (mobXform.MapUid != xform.MapUid)
                continue;

            // Don't count unidentified humans (loot) or anyone you murdered so you can still maroon them once dead.
            if (_mobState.IsDead(uid, mobState))
                continue;

            // Okay they're on salvage, so are they on the shuttle.
            if (mobXform.GridUid != ev.Uid)
            {
                ev.Cancelled = true;
                ev.Reason = Loc.GetString("salvage-expedition-not-all-present");
                return;
            }
        }
    }

    /// <summary>
    /// Announces status updates to salvage crewmembers on the state of the expedition.
    /// </summary>
    private void Announce(EntityUid mapUid, string text)
    {
        var mapId = Comp<MapComponent>(mapUid).MapId;

        // I love TComms and chat!!!
        _chat.ChatMessageToManyFiltered(
            Filter.BroadcastMap(mapId),
            ChatChannel.Radio,
            text,
            text,
            _mapSystem.GetMapOrInvalid(mapId),
            false,
            true,
            null);
    }

    private void OnFTLRequest(ref FTLRequestEvent ev)
    {
        if (!HasComp<SalvageExpeditionComponent>(ev.MapUid) ||
            !TryComp<FTLDestinationComponent>(ev.MapUid, out var dest))
        {
            return;
        }

        // Only one shuttle can occupy an expedition.
        dest.Enabled = false;
        _shuttleConsoles.RefreshShuttleConsoles();
    }

    private void OnFTLCompleted(ref FTLCompletedEvent args)
    {
        if (!TryComp<SalvageExpeditionComponent>(args.MapUid, out var component))
            return;

        // Frontier
        if (TryComp<SalvageExpeditionDataComponent>(component.Station, out var data))
        {
            data.CanFinish = true;
            UpdateConsoles(component.Station, data);
        }
        // Frontier

        // Someone FTLd there so start announcement
        if (component.Stage != ExpeditionStage.Added)
            return;

        Announce(args.MapUid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", (component.EndTime - _timing.CurTime).Minutes)));

        var directionLocalization = ContentLocalizationManager.FormatDirection(component.DungeonLocation.GetDir()).ToLower();

        if (component.DungeonLocation != Vector2.Zero)
            Announce(args.MapUid, Loc.GetString("salvage-expedition-announcement-dungeon", ("direction", directionLocalization)));

        component.Stage = ExpeditionStage.Running;
        Dirty(args.MapUid, component);
    }

    private void OnFTLStarted(ref FTLStartedEvent ev)
    {
        // Started a mining mission so work out exempt entities
        if (TryComp<SalvageMiningExpeditionComponent>(
                _mapSystem.GetMapOrInvalid(_transform.ToMapCoordinates(ev.TargetCoordinates).MapId),
                out var mining))
        {
            var ents = new List<EntityUid>();
            var xformQuery = GetEntityQuery<TransformComponent>();
            MiningTax(ents, ev.Entity, mining, xformQuery);
            mining.ExemptEntities = ents;
        }

        if (!TryComp<SalvageExpeditionComponent>(ev.FromMapUid, out var expedition) ||
            !TryComp<SalvageExpeditionDataComponent>(expedition.Station, out var station))
        {
            return;
        }

        station.CanFinish = false; // Frontier

        // Check if any shuttles remain.
        var query = EntityQueryEnumerator<ShuttleComponent, TransformComponent>();

        while (query.MoveNext(out _, out var xform))
        {
            if (xform.MapUid == ev.FromMapUid)
                return;
        }

        // Last shuttle has left so finish the mission.
        QueueDel(ev.FromMapUid.Value);
    }

    // Triad: true while any player ship (ShuttleComponent grid) is still parented to the expedition
    // map. Dungeon/asteroid terrain grids aren't shuttles, so they don't block teardown.
    private bool ExpeditionMapHasShuttle(EntityUid map)
    {
        var query = AllEntityQuery<ShuttleComponent, TransformComponent>();
        while (query.MoveNext(out _, out var xform))
        {
            if (xform.MapUid == map)
                return true;
        }

        return false;
    }

    // Runs the expedition
    private void UpdateRunner()
    {
        // Generic missions
        var query = EntityQueryEnumerator<SalvageExpeditionComponent>();

        // Run the basic mission timers (e.g. announcements, auto-FTL, completion, etc)
        while (query.MoveNext(out var uid, out var comp))
        {
            var remaining = comp.EndTime - _timing.CurTime;
            var audioLength = _audio.GetAudioLength(comp.SelectedSong);

            AbortIfWiped(uid, comp); // Coyote

            if (comp.Stage < ExpeditionStage.FinalCountdown && remaining < TimeSpan.FromSeconds(45))
            {
                comp.Stage = ExpeditionStage.FinalCountdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-seconds", ("duration", TimeSpan.FromSeconds(45).Seconds)));
            }
            else if (comp.Stage < ExpeditionStage.MusicCountdown && comp.Stream == null && remaining < audioLength) // Frontier
            {
                var audio = _audio.PlayPvs(comp.Sound, uid);
                comp.Stream = audio?.Entity;
                _audio.SetMapAudio(audio);
                comp.Stage = ExpeditionStage.MusicCountdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", audioLength.Minutes)));
            }
            else if (comp.Stage < ExpeditionStage.Countdown && remaining < TimeSpan.FromMinutes(5))
            {
                comp.Stage = ExpeditionStage.Countdown;
                Dirty(uid, comp);
                Announce(uid, Loc.GetString("salvage-expedition-announcement-countdown-minutes", ("duration", TimeSpan.FromMinutes(5).Minutes)));
            }
            // Auto-FTL out any shuttles
            else if (remaining < TimeSpan.FromSeconds(_shuttle.DefaultStartupTime) + TimeSpan.FromSeconds(0.5))
            {
                var ftlTime = (float) remaining.TotalSeconds;

                if (remaining < TimeSpan.FromSeconds(_shuttle.DefaultStartupTime))
                {
                    ftlTime = MathF.Max(0, (float) remaining.TotalSeconds - 0.5f);
                }

                ftlTime = MathF.Min(ftlTime, _shuttle.DefaultStartupTime);
                var shuttleQuery = AllEntityQuery<ShuttleComponent, TransformComponent>();

                if (TryComp<StationDataComponent>(comp.Station, out var data))
                {
                    foreach (var member in data.Grids)
                    {
                        while (shuttleQuery.MoveNext(out var shuttleUid, out var shuttle, out var shuttleXform))
                        {
                            if (shuttleXform.MapUid != uid || HasComp<FTLComponent>(shuttleUid))
                                continue;

                            // Frontier: try to find a potential destination for ship that doesn't collide with other grids.
                            var mapId = _gameTicker.DefaultMap;
                            if (!_mapSystem.TryGetMap(mapId, out var mapUid))
                            {
                                Log.Error($"Could not get DefaultMap EntityUID, shuttle {shuttleUid} may be stuck on expedition.");
                                continue;
                            }

                            var shuttleGrid = shuttleXform.GridUid;
                            DestinationPriority? deadLoserDestinations = null;
                            // Need to check if the shuttle have any FTLComponent or not.
                            if (shuttleGrid != null && !HasComp<FTLComponent>(shuttleGrid))
                            {
                                var mobQuery = EntityQueryEnumerator<MindContainerComponent, TransformComponent>();
                                while (mobQuery.MoveNext(
                                       out var mobUid,
                                       out var mindC,
                                       out var mobXform))
                                {
                                    if (mobXform.MapUid != uid)
                                        continue;
                                    if (mobXform.GridUid == shuttleGrid)
                                        continue; // they're already on the shuttle
                                    if (!mindC.HasMind)
                                        continue;
                                    // move them to the shuttle
                                    deadLoserDestinations ??= GetDeadPersonDestinations(shuttleGrid.Value);
                                    RescuePlayer(
                                        mobUid,
                                        deadLoserDestinations,
                                        shuttleGrid.Value);
                                }
                            }

                            // Destination generator parameters (move to CVAR?)
                            var numRetries = 20; // Maximum number of retries
                            var minDistance = 200f; // Minimum distance from another object, in meters
                            var minRange = 750f; // Minimum distance from sector centre, in meters
                            var maxRange = 3500f; // Maximum distance from sector centre, in meters

                            // Get a list of all grid positions on the destination map
                            List<Vector2> gridCoords = new();
                            var gridQuery = AllEntityQuery<MapGridComponent, TransformComponent>();
                            while (gridQuery.MoveNext(out var _, out _, out var xform))
                            {
                                if (xform.MapID == mapId)
                                    gridCoords.Add(_transform.GetWorldPosition(xform));
                            }

                            Vector2 dropLocation = _random.NextVector2(minRange, maxRange);
                            for (int i = 0; i < numRetries; i++)
                            {
                                bool positionIsValid = true;
                                foreach (var station in gridCoords)
                                {
                                    if (Vector2.Distance(station, dropLocation) < minDistance)
                                    {
                                        positionIsValid = false;
                                        break;
                                    }
                                }

                                if (positionIsValid)
                                    break;

                                // No good position yet, pick another random position.
                                dropLocation = _random.NextVector2(minRange, maxRange);
                            }
                            _shuttle.FTLToCoordinates(shuttleUid, shuttle, new EntityCoordinates(mapUid.Value, dropLocation), 0f, 5.5f, 50f);
                            // End Frontier:  try to find a potential destination for ship that doesn't collide with other grids.
                        }

                        break;
                    }
                }
            }

            if (remaining < TimeSpan.Zero)
            {
                // Triad: don't tear down the expedition map while a player ship is still on it.
                // The auto-FTL above retries every tick, so a ship stranded by its FTL-drive
                // cooldown is evacuated once the drive is ready; deferring until the map is clear
                // turns "ship + crew deleted" into "expedition ends a few seconds late".
                if (!ExpeditionMapHasShuttle(uid))
                    QueueDel(uid);
            }
        }

        // Mining missions: NOOP since it's handled after ftling

        // Structure missions
        var structureQuery = EntityQueryEnumerator<SalvageStructureExpeditionComponent, SalvageExpeditionComponent>();

        while (structureQuery.MoveNext(out var uid, out var structure, out var comp))
        {
            if (comp.Completed)
                continue;

            var structureAnnounce = false;

            for (var i = 0; i < structure.Structures.Count; i++)
            {
                var objective = structure.Structures[i];

                if (Deleted(objective))
                {
                    structure.Structures.RemoveSwap(i);
                    structureAnnounce = true;
                }
            }

            if (structureAnnounce)
            {
                Announce(uid, Loc.GetString("salvage-expedition-structure-remaining", ("count", structure.Structures.Count)));
            }

            if (structure.Structures.Count == 0)
            {
                comp.Completed = true;
                Announce(uid, Loc.GetString("salvage-expedition-completed"));
            }
        }

        // Elimination missions
        var eliminationQuery = EntityQueryEnumerator<SalvageEliminationExpeditionComponent, SalvageExpeditionComponent>();
        while (eliminationQuery.MoveNext(out var uid, out var elimination, out var comp))
        {
            if (comp.Completed)
                continue;

            var announce = false;

            for (var i = 0; i < elimination.Megafauna.Count; i++)
            {
                var mob = elimination.Megafauna[i];

                if (Deleted(mob) || _mobState.IsDead(mob))
                {
                    elimination.Megafauna.RemoveSwap(i);
                    announce = true;
                }
            }

            if (announce)
            {
                Announce(uid, Loc.GetString("salvage-expedition-megafauna-remaining", ("count", elimination.Megafauna.Count)));
            }

            if (elimination.Megafauna.Count == 0)
            {
                comp.Completed = true;
                Announce(uid, Loc.GetString("salvage-expedition-completed"));
            }
        }
        // End Frontier: mission-specific logic
    }

    /// <summary>
    /// Takes a mob, and puts them onto this shuttle.
    /// </summary>
    private void RescuePlayer(
        EntityUid mobUid,
        DestinationPriority possibleDestinations,
        EntityUid shuttleGrid)
    {
        PrepareRescue(mobUid);
        // unbuckle them if they are buckled
        _buckle.TryUnbuckle(mobUid, null);
        // try beds first
        foreach (var bedUid in possibleDestinations.Beds)
        {
            if (TryTeleportToStrap(mobUid, bedUid))
                return;
        }
        // then chairs
        foreach (var chairUid in possibleDestinations.Chairs)
        {
            if (TryTeleportToStrap(mobUid, chairUid))
                return;
        }
        // then consoles
        foreach (var consoleUid in possibleDestinations.Consoles)
        {
            var consoleXform = Transform(consoleUid);
            var mobXform = Transform(mobUid);
            _transform.SetCoordinates(mobUid, consoleXform.Coordinates);
            _transform.AttachToGridOrMap(mobUid, mobXform);
            return;
        }
        // then fallback
        foreach (var fallbackUid in possibleDestinations.Fallback)
        {
            var fallbackXform = Transform(fallbackUid);
            var mobXform = Transform(mobUid);
            _transform.SetCoordinates(mobUid, fallbackXform.Coordinates);
            _transform.AttachToGridOrMap(mobUid, mobXform);
            return;
        }
    }

    /// <summary>
    /// Gets a list of possible destinations for dead/dying crew to be rescued to.
    /// Tries to find a location based on a list of priorities.
    /// HERES THE PRIORITIES:
    /// 2: Beds with no mobs in them.
    /// 3: Chairs with no mobs in them.
    /// 4: I dunno the console I guess
    /// </summary>
    private DestinationPriority GetDeadPersonDestinations(EntityUid shuttleGrid)
    {
        DestinationPriority destinations = new();
        // first, find the exped consoles on the grid
        var destQuery = EntityQueryEnumerator<SalvageExpeditionConsoleComponent, TransformComponent>();
        while (destQuery.MoveNext(
                   out var uid,
                   out var _,
                   out var xform))
        {
            if (xform.GridUid != shuttleGrid)
                continue;
            destinations.Add(uid, DestinationType.Console);
        }
        // then, all beds / chairs (theyre both strap components)
        var strapQuery = EntityQueryEnumerator<StrapComponent, TransformComponent>();
        while (strapQuery.MoveNext(
                   out var uid,
                   out var strap,
                   out var xform))
        {
            if (xform.GridUid != shuttleGrid)
                continue;
            destinations.Add(uid, strap.Position == StrapPosition.Stand ? DestinationType.Chair : DestinationType.Bed);
        }
        // then some fallback stuff, find the warp point
        // worst case, we just teleport them to the center of the grid. hope its not in a wall!!
        var warpQuery = EntityQueryEnumerator<WarpPointComponent, TransformComponent>();
        while (warpQuery.MoveNext(
                   out var uid,
                   out var _,
                   out var xform))
        {
            if (xform.GridUid != shuttleGrid)
                continue;
            destinations.Add(uid, DestinationType.Fallback);
        }
        return destinations;
    }

    /// <summary>
    /// Beats the heck out of the dork if they arent dead
    /// Then extinguishes them and caps their Heat to 300ish if above that.
    /// </summary>
    private void PrepareRescue(EntityUid mobUid)
    {
        if (_mobState.IsAlive(mobUid))
        {
            // Force stamcrit when rescue
            _stamina.TakeStaminaDamage(mobUid, 200);
            _inventorySystem.TryUnequip(mobUid, "suitStorage", true, true, false);
        }
        if (!_mobState.IsAlive(mobUid))
        {
            // Force strip backpack, outerClothing, and suitStorage if they're not alive
            _inventorySystem.TryUnequip(mobUid, "back", true, true, false);
            _inventorySystem.TryUnequip(mobUid, "outerClothing", true, true, false);
            _inventorySystem.TryUnequip(mobUid, "suitStorage", true, true, false);
        }

        if (!TryComp<TemperatureComponent>(mobUid, out var comp))
            return;
        if (TryComp<ThermalRegulatorComponent>(
                mobUid,
                out var regulator)) // Frontier: Look for normal body temperature and use it
        {
            _temperature.ForceChangeTemperature(
                mobUid,
                regulator.NormalBodyTemperature,
                comp);
        }
        else
        {
            _temperature.ForceChangeTemperature(
                mobUid,
                Atmospherics.T20C,
                comp);
        }
    }

    /// <summary>
    /// Tries to teleport the mob to the strap and buckle them in.
    /// Returns true on success.
    /// </summary>
    private bool TryTeleportToStrap(EntityUid mobUid, EntityUid strapUid)
    {
        if (!TryComp<BuckleComponent>(mobUid, out var buckle))
            return false;
        if (!TryComp<StrapComponent>(strapUid, out var strap))
            return false;
        if (strap.BuckledEntities.Count > 0)
            return false; // already occupied
        var strapXform = Transform(strapUid);
        var mobXform = Transform(mobUid);
        _transform.SetCoordinates(mobUid, strapXform.Coordinates);
        _transform.AttachToGridOrMap(mobUid, mobXform);
        return _buckle.TryBuckle(
            mobUid,
            null,
            strapUid);
    }

    // class that holds a set of destinations with a priority
    private sealed class DestinationPriority
    {
        public List<EntityUid> Beds = new();
        public List<EntityUid> Chairs = new();
        public List<EntityUid> Consoles = new();
        public List<EntityUid> Fallback = new();
        public void Add(EntityUid uid, DestinationType type)
        {
            switch (type)
            {
                case DestinationType.Bed:
                    Beds.Add(uid);
                    break;
                case DestinationType.Chair:
                    Chairs.Add(uid);
                    break;
                case DestinationType.Console:
                    Consoles.Add(uid);
                    break;
                default:
                case DestinationType.Fallback:
                    Fallback.Add(uid);
                    break;
            }
        }
    }

    // enum for destination types
    private enum DestinationType
    {
        Bed,
        Chair,
        Console,
        Fallback,
    }

    /// <summary>
    /// Checks if everyone on the map worth caring about is dead, and aborts the expedition if so.
    /// Honestly, as long as one person is not in crit and not SSD, we consider the expedition salvageable.
    /// </summary>
    private void AbortIfWiped(EntityUid mapUid, SalvageExpeditionComponent component)
    {
        // give it a 30 second grade after first check to avoid instant aborts
        if (component.NextAutoAbortCheck == TimeSpan.Zero)
        {
            component.NextAutoAbortCheck = _timing.CurTime + TimeSpan.FromSeconds(30);
            return;
        }
        // its an entity query and idk how expensive it is, so, cooldown
        if (_timing.CurTime < component.NextAutoAbortCheck)
            return;
        component.NextAutoAbortCheck = _timing.CurTime + TimeSpan.FromSeconds(15);

        // okay first look for aghosts, whatever
        var aghostQuery =
            EntityQueryEnumerator<AdminGhostComponent, TransformComponent>();
        while (aghostQuery.MoveNext(
                   out var _,
                   out _,
                   out var xform))
        {
            if (xform.MapUid == mapUid)
                return; // aghost found, dont abort
        }

        var query =
            EntityQueryEnumerator<
                HumanoidAppearanceComponent,
                MindContainerComponent,
                MobStateComponent,
                TransformComponent>();
        // prevent abort if:
        // - aghosts are present
        // - anyone is alive AND connected
        while (query.MoveNext(
                   out var uid,
                   out _,
                   out var mindC,
                   out var mobState,
                   out var xform))
        {
            if (xform.MapUid != mapUid)
                continue;
            // unidentified humans (loot) dont count
            if (!mindC.HasMind)
                continue;
            // if anyone is alive and not in crit, we are good
            if (_mobState.IsAlive(uid, mobState))
            {
                // okay weve got something alive, is their session?
                _players.TryGetSessionByEntity(uid, out var session);
                // if no session, check if they are SSD
                if (session == null)
                    continue;
                if (session.Status == SessionStatus.Disconnected)
                    continue;
                return; // alive and connected player found, expedition is salvageable
            }
        }
        // everyone is dead or ssd, abort the expedition
        const int departTime = 30;
        Announce(mapUid, Loc.GetString("salvage-expedition-abort-wipe", ("departTime", departTime)));
        component.NextAutoAbortCheck = _timing.CurTime + TimeSpan.FromDays(1); // prevent further checks
        var newEndTime = _timing.CurTime + TimeSpan.FromSeconds(departTime);

        if (component.EndTime <= newEndTime)
            return;

        component.Stage = ExpeditionStage.FinalCountdown;
        component.EndTime = newEndTime;

    }
}
