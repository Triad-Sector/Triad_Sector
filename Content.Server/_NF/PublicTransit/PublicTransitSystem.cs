using System.Linq;
using System.Numerics;
using Content.Server._NF.PublicTransit.Components;
using Content.Server.Chat.Systems;
using Content.Server.GameTicking;
using Content.Server.Power.Components;
using Content.Server.Shuttles.Components;
using Content.Server.Shuttles.Events;
using Content.Server.Shuttles.Systems;
using Content.Shared.Chat; // Einstein Engines - Languages
using Content.Shared.GameTicking;
using Content.Shared._NF.CCVar;
using Content.Shared._NF.PublicTransit;
using Content.Shared.Shuttles.Components;
using Content.Shared.Tiles;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Configuration;
using Robust.Shared.Map;
using Robust.Shared.Timing;
using TimedDespawnComponent = Robust.Shared.Spawners.TimedDespawnComponent;
using Content.Server._NF.Station.Systems;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Utility;

namespace Content.Server._NF.PublicTransit;

/// <summary>
/// If enabled, spawns a public trasnport grid as definied by cvar, to act as an automatic transit shuttle between designated grids
/// </summary>
public sealed partial class PublicTransitSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfgManager = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private GameTicker _ticker = default!;
    [Dependency] private MapSystem _map = default!;
    [Dependency] private MapLoaderSystem _loader = default!;
    [Dependency] private ShuttleSystem _shuttles = default!;
    [Dependency] private ChatSystem _chat = default!;
    [Dependency] private MetaDataSystem _meta = default!;
    [Dependency] private StationRenameWarpsSystems _renameWarps = default!;
    [Dependency] private DockingSystem _dockSystem = default!;
    [Dependency] private TransformSystem _transform = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedAudioSystem _audio = default!;

    /// <summary>
    /// If enabled then spawns the bus and sets up the bus line.
    /// </summary>
    public bool Enabled { get; private set; }
    public float FlyTime = 50f;

    /// <summary>
    /// How long before departure the boarding call goes out.
    /// </summary>
    // Triad: wait_time defaults to 40s, so this leaves a reasonable window to actually get aboard.
    private static readonly TimeSpan DepartureWarning = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The PriorityDock tag mappers put on a berth reserved for the bus.
    /// </summary>
    // Triad: this string was written out at four separate call sites, and the arrival path had already
    // been caught missing it once.
    public const string BerthTag = "DockTransit";

    public int Counter = 0;
    public List<EntityUid> StationList = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<StationTransitComponent, MapInitEvent>(OnStationStartup);
        SubscribeLocalEvent<StationTransitComponent, ComponentShutdown>(OnStationShutdown);
        SubscribeLocalEvent<TransitShuttleComponent, ComponentStartup>(OnShuttleStartup);
        SubscribeLocalEvent<TransitShuttleComponent, EntityUnpausedEvent>(OnShuttleUnpaused);
        SubscribeLocalEvent<TransitShuttleComponent, FTLCompletedEvent>(OnShuttleArrival);
        SubscribeLocalEvent<TransitShuttleComponent, FTLTagEvent>(OnShuttleTag);
        SubscribeLocalEvent<RoundStartedEvent>(OnRoundStart);

        Enabled = _cfgManager.GetCVar(NFCCVars.PublicTransit);
        FlyTime = _cfgManager.GetCVar(NFCCVars.PublicTransitFlyTime);
        Counter = 0;
        StationList.Clear();
        _cfgManager.OnValueChanged(NFCCVars.PublicTransit, SetTransit);
        _cfgManager.OnValueChanged(NFCCVars.PublicTransitFlyTime, SetFly);
    }

    public void OnRoundStart(RoundStartedEvent args)
    {
        Counter = 0;
        if (Enabled)
            SetupPublicTransit();
    }

    public override void Shutdown()
    {
        base.Shutdown();
        _cfgManager.UnsubValueChanged(NFCCVars.PublicTransitFlyTime, SetFly);
        _cfgManager.UnsubValueChanged(NFCCVars.PublicTransit, SetTransit);
    }


    /// <summary>
    /// Hardcoded snippit to intercept FTL events. It catches the transit shuttle and ensures its looking for the "DockTransit" priority dock.
    /// </summary>
    private void OnShuttleTag(EntityUid uid, TransitShuttleComponent component, ref FTLTagEvent args)
    {
        if (args.Handled)
            return;

        // Just saves mappers forgetting, or ensuring that a non-standard grid forced to be a bus will prioritize the "DockTransit" tagged docks
        args.Handled = true;
        args.Tag = BerthTag;
    }

    /// <summary>
    /// Checks to make sure the grid is on the appropriate playfield, i.e., not in mapping space being worked on.
    /// If so, adds the grid to the list of bus stops, but only if its not already there
    /// </summary>
    private void OnStationStartup(EntityUid uid, StationTransitComponent component, MapInitEvent args)
    {
        if (Transform(uid).MapID == _ticker.DefaultMap) //best solution i could find because of componentinit/mapinit race conditions
        {
            if (!StationList.Contains(uid)) //if the grid isnt already in
            {
                StationList.Add(uid); //add it to the list
                RebuildRoute(); // Triad: keep hubs spaced as stops register
            }
        }
    }

    /// <summary>
    /// Reorders the stop list into a spatial loop: start at the home station, then visit stops in an
    /// order that keeps each hop short instead of criss-crossing the sector.
    /// </summary>
    // Triad: raw registration order is whatever happened to MapInit first, which sent the bus zig-
    // zagging across the map. FTL time is a flat timer so this changes nothing about the schedule; it
    // makes the line legible, the radar hop go to a neighbour, and the landing marker show up where
    // you'd expect the bus to head next. Nearest-neighbour builds the tour, one 2-opt pass untangles
    // the crossings it leaves; stop counts are single digits, so this is microseconds once per round.
    // POIs are ForceAnchored and never move, so the tour only needs recomputing when the stop list
    // changes. A mid-round rebuild only nudges Counter's view of the loop by a stop.
    private void RebuildRoute()
    {
        if (StationList.Count <= 3)
            return; // any order of three or fewer stops is already a shortest loop

        // Nearest-neighbour tour anchored on the first registered stop, which is the home station:
        // stations set up before the POI spawn rule runs, so it is always at the front of the list.
        var remaining = new List<EntityUid>(StationList);
        var route = new List<EntityUid> { remaining[0] };
        remaining.RemoveAt(0);

        while (remaining.Count > 0)
        {
            var curPos = _transform.GetWorldPosition(route[^1]);
            var best = 0;
            var bestDist = float.MaxValue;

            for (var i = 0; i < remaining.Count; i++)
            {
                var dist = (_transform.GetWorldPosition(remaining[i]) - curPos).LengthSquared();
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }

            route.Add(remaining[best]);
            remaining.RemoveAt(best);
        }

        // 2-opt: while any pair of loop edges would be shorter swapped, reverse the segment between
        // them. Treats the route as closed (last stop wraps to the anchor).
        var pos = new Vector2[route.Count];
        for (var i = 0; i < route.Count; i++)
            pos[i] = _transform.GetWorldPosition(route[i]);

        float Dist(int a, int b) => (pos[a] - pos[b]).Length();

        var improved = true;
        while (improved)
        {
            improved = false;

            for (var i = 0; i < route.Count - 1; i++)
            {
                for (var j = i + 1; j < route.Count; j++)
                {
                    var after = (j + 1) % route.Count;
                    if (after == i)
                        continue; // adjacent through the wrap; reversal is a no-op

                    if (Dist(i, j) + Dist(i + 1, after) >= Dist(i, i + 1) + Dist(j, after))
                        continue;

                    route.Reverse(i + 1, j - i);
                    Array.Reverse(pos, i + 1, j - i);
                    improved = true;
                }
            }
        }

        StationList.Clear();
        StationList.AddRange(route);
    }

    /// <summary>
    /// When a bus stop gets deleted in-game, we need to remove it from the list of bus stops, or else we get FTL problems
    /// </summary>
    private void OnStationShutdown(EntityUid uid, StationTransitComponent component, ComponentShutdown args)
    {
        if (StationList.Contains(uid))
            StationList.Remove(uid);
    }

    /// <summary>
    /// Again, this can and likely should be instructed to mappers to do, but just in case it was either forgotten or we are doing admemes,
    /// we make sure that the bus is (mostly) griefer protected and that it cant be hijacked
    /// </summary>
    private void OnShuttleStartup(EntityUid uid, TransitShuttleComponent component, ComponentStartup args)
    {
        EnsureComp<PreventPilotComponent>(uid);
        // Triad: the bus leaves on its clock, not on anyone's say-so, so nothing docked to it at that
        // moment can have agreed to come along. It travels alone.
        EnsureComp<FTLSoloComponent>(uid);
        var prot = EnsureComp<ProtectedGridComponent>(uid);
        prot.PreventArtifactTriggers = true;
        prot.PreventEmpEvents = true;
        prot.PreventExplosions = true;
        prot.PreventFloorPlacement = true;
        prot.PreventFloorRemoval = true;
        prot.PreventRCDUse = true;

        var stationName = Loc.GetString(component.Name);

        var meta = EnsureComp<MetaDataComponent>(uid);
        _meta.SetEntityName(uid, stationName, meta);

        _renameWarps.SyncWarpPointsToGrid(uid);
    }

    /// <summary>
    /// ensuring that pausing the shuttle for any reason doesnt mess up our timing
    /// </summary>
    private void OnShuttleUnpaused(EntityUid uid, TransitShuttleComponent component, ref EntityUnpausedEvent args)
    {
        component.NextTransfer += args.PausedTime;
    }

    private void OnShuttleArrival(EntityUid uid, TransitShuttleComponent comp, ref FTLCompletedEvent args)
    {
        // Triad: the dwell clock starts here, on arrival, not back when we left. It used to be
        // departure + FlyTime + WaitTime, so every second the trip ran over its estimate came out of
        // the stop instead, and once the overrun exceeded the wait the bus left the moment it docked.
        // Measuring from arrival means a stop is a stop regardless of what the trip did.
        var waitTime = _cfgManager.GetCVar(NFCCVars.PublicTransitWaitTime);
        comp.NextTransfer = _timing.CurTime + TimeSpan.FromSeconds(waitTime);
        comp.DepartureAnnounced = false;

        if (!TryComp(comp.NextStation, out MetaDataComponent? metadata))
            return;

        // Triad: the passenger-facing lines are console speech, which leaves nothing on the server for
        // anyone asking why the bus stopped running. A route this system drives on its own all round
        // should be traceable without attaching a debugger.
        Log.Debug($"Public transit arrived, holding {waitTime}s before departing for {metadata.EntityName}.");

        AnnounceToBus(uid, Loc.GetString("public-transit-arrival",
            ("destination", metadata.EntityName), ("waittime", waitTime)));
    }

    /// <summary>
    /// Here is our bus stop list handler. Theres probably a better way...
    /// First, sets our output to null just in case
    /// then, makes sure that our counter/index isnt out of range (reaching the end of the list will force you back to the beginning, like a loop)
    /// Then, it checks to make sure that there even is anything in the list
    /// and if so, we return the next station, and then increment our counter for the next time its ran
    /// </summary>
    private bool TryGetNextStation(out EntityUid? station)
    {
        station = null;

        if (Counter >= StationList.Count)
            Counter = 0;

        if (!(StationList.Count > 0))
            return false;

        station = StationList[Counter];
        Counter++;
        return true;
    }

    /// <summary>
    /// We check the current time every tick, and if its not yet time, we just ignore.
    /// If the timer is ready, we send the shuttle on an FTL journey to the destination it has saved
    /// then we check our bus list, and if it returns true with the next station, we cache it on the component and reset the timer
    /// if it returns false or gives a bad grid, we are just going to FTL back to where we are and try again until theres a proper destination
    /// This could cause unintended behavior, if a destination is deleted while it's next in the cache, the shuttle is going to be stuck in FTL space
    /// However the timer is going to force it to FTL to the next bus stop
    /// If it happens that all bus stops are deleted and we never get a valid stop again, we are going to be stuck FTL'ing forever in ftl space
    /// but at that point, theres nowhere to return to anyway
    /// </summary>
    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<TransitShuttleComponent, ShuttleComponent>();
        var curTime = _timing.CurTime;

        var waitTime = _cfgManager.GetCVar(NFCCVars.PublicTransitWaitTime);

        while (query.MoveNext(out var uid, out var comp, out var shuttle))
        {
            // Triad: never stack a departure on top of a trip that is still running. This used to be
            // impossible to hit because the bus never actually left, so nothing noticed.
            if (HasComp<FTLComponent>(uid))
                continue;

            if (comp.NextTransfer > curTime)
            {
                // Triad: give passengers a boarding call before the doors go. One per stop.
                if (!comp.DepartureAnnounced
                    && comp.NextTransfer - curTime <= DepartureWarning
                    && TryComp(comp.NextStation, out MetaDataComponent? upcoming))
                {
                    comp.DepartureAnnounced = true;
                    AnnounceToBus(uid, Loc.GetString("public-transit-departure-warning",
                        ("destination", upcoming.EntityName), ("time", (int) DepartureWarning.TotalSeconds)),
                        chime: true);
                }

                continue;
            }

            comp.DepartureAnnounced = false;

            // Triad: the cached destination can be deleted while it waits its turn, and Valid only
            // says the uid was once well formed, not that the stop is still there. Re-roll instead of
            // jumping at nothing, and if the whole route is gone just wait rather than burning a trip.
            if (!TryComp(comp.NextStation, out MetaDataComponent? destination))
            {
                if (!TryGetNextStation(out var replacement) || replacement is not { } newStop || !TryComp(newStop, out destination))
                {
                    comp.NextTransfer = curTime + TimeSpan.FromSeconds(waitTime);
                    continue;
                }

                comp.NextStation = newStop;
            }

            // Triad: check there is somewhere to berth before announcing a trip we might not take.
            // Stations do not despawn, but Edison in particular gets wrecked often enough that its docks
            // can be dark or gone while the stop itself still exists, so skip it and say why rather than
            // flying there to find out.
            // Triad: undock before asking, not after. CanDock rejects a port that is already docked, so
            // a config query run while the bus is still berthed is answered with half its airlocks
            // missing. Undocking first costs nothing if we then skip: the bus is parked and under no
            // thrust, it just spends the interval unwelded.
            _dockSystem.UndockDocks(uid);

            if (!BerthIsServiceable(uid, comp.NextStation))
            {
                var skipped = destination.EntityName;

                if (TryGetNextStation(out var afterSkip) && afterSkip is { Valid: true } replacement)
                    comp.NextStation = replacement;

                var nextName = TryComp(comp.NextStation, out MetaDataComponent? nextMeta) ? nextMeta.EntityName : skipped;

                // Passengers hear this over the console; nobody waiting on a platform does, so leave a
                // line on the server console too. A skip that repeats every stop is a mapping fault.
                Log.Info($"Public transit skipping {skipped}: no serviceable berth. Continuing to {nextName}.");

                AnnounceToBus(uid, Loc.GetString("public-transit-skipped",
                    ("destination", skipped), ("next", nextName)),
                    chime: true);

                // Retry on the normal cadence instead of hammering the dock check every tick.
                comp.NextTransfer = curTime + TimeSpan.FromSeconds(waitTime);
                continue;
            }

            // Triad: the note that used to sit here said FTLToDock called TryFTLDock, which bypassed the
            // FTL delay and broke OnShuttleArrival, and worked around it by announcing the arrival
            // string on departure. That bypass is fixed at the source now: FTLToDock reads its docking
            // config without teleporting the shuttle, so the trip actually takes FlyTime and the
            // departure line can say so honestly.
            AnnounceToBus(uid, Loc.GetString("public-transit-departure",
                ("destination", destination.EntityName), ("flytime", (int) FlyTime)));

            Log.Debug($"Public transit departing for {destination.EntityName}, {FlyTime}s in transit.");

            _shuttles.FTLToDock(uid, shuttle, comp.NextStation, hyperspaceTime: FlyTime, priorityTag: BerthTag);

            if (TryGetNextStation(out var nextStation) && nextStation is { Valid: true } next)
                comp.NextStation = next;

            // Triad: the signs advertise where the bus is going, so they update once it is under way.
            UpdateScheduleSigns(comp.NextStation);

            comp.NextTransfer = curTime + TimeSpan.FromSeconds(FlyTime + waitTime);
        }
    }

    /// <summary>
    /// Whether the given stop is in a fit state to be served right now.
    /// </summary>
    // Triad: this asks about the stop, not about the trip. It used to run GetDockingConfig between the
    // bus and the station, which sounds equivalent and is not: CanDock rejects any port that is already
    // docked, and this gate runs while the bus is still berthed at the previous stop. The bus docks with
    // both ports on one side at once, so half its airlocks were excluded from a check that then decided
    // the destination was dead. Skipped stops announce over the console only, so from a platform it just
    // looked like the bus never came. Asking the station directly is both the correct question and
    // immune to wherever the bus happens to be parked.
    //
    // A berth counts as live if it still exists, is anchored, and either draws no power or has it.
    // Plenty of dock ports are not powered devices, so a missing receiver is not a fault. Occupancy is
    // deliberately not disqualifying: another ship sitting in the bay is normal, and arrival falls back
    // to any other berth on the station.
    private bool BerthIsServiceable(EntityUid busUid, EntityUid station)
    {
        // A stop we cannot physically berth at is the dangerous case, not merely a wasted trip:
        // UpdateFTLArriving answers a null docking config with TryFTLProximity, which is the one
        // placement path in the FTL sequence that no docking validation stands behind, and which drops
        // the grid at an arbitrary angle. Refusing the trip here is what keeps the bus out of it.
        var config = _dockSystem.GetDockingConfig(busUid, station, BerthTag);

        if (config is null)
        {
            Log.Info($"Berth check failed for {ToPrettyString(station)}: no docking config forms anywhere on the station.");
            return false;
        }

        var tagged = 0;
        var taggedLive = 0;
        var anyLive = 0;

        foreach (var (dockUid, dock) in _dockSystem.GetDocks(station))
        {
            if ((dock.DockType & DockType.Airlock) == DockType.None)
                continue;

            var live = Transform(dockUid).Anchored
                       && (!TryComp<ApcPowerReceiverComponent>(dockUid, out var power) || power.Powered);

            if (live)
                anyLive++;

            if (TryComp<PriorityDockComponent>(dockUid, out var priority) && priority.Tag == BerthTag)
            {
                tagged++;
                if (live)
                    taggedLive++;
            }
        }

        // Judge a mapped stop on its designated berths and everywhere else on whatever it has.
        if (tagged > 0 ? taggedLive <= 0 : anyLive <= 0)
        {
            Log.Info($"Berth check failed for {ToPrettyString(station)}: no live berth (tagged={tagged}, taggedLive={taggedLive}, anyLive={anyLive}).");
            return false;
        }

        // A stop that bothered to mark a berth gets used at that berth or not at all. GetDockingConfig
        // returns the best config it can find, tagged or otherwise, so a station with dozens of ordinary
        // shuttle airlocks answers "yes, somewhere" even when the bus bay itself does not fit the bus.
        // Priority sorts first, so a non-priority winner proves no config reaches the marked berth.
        if (tagged > 0 && !_dockSystem.IsConfigPriority(config, BerthTag))
        {
            Log.Info($"Berth check failed for {ToPrettyString(station)}: {tagged} tagged berth(s) present but no config reaches one (best config docks at {string.Join(", ", config.Docks.Select(d => d.DockBUid))}).");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Repaints every bus schedule sign in the sector to the colour of the given destination.
    /// </summary>
    // Triad: there is one bus, so every sign shows the same next stop. The colour comes off the
    // destination's IFF component, which is what gives each POI its identity on the console already.
    private void UpdateScheduleSigns(EntityUid destination)
    {
        var color = TryComp<IFFComponent>(destination, out var iff) && iff.Color != default
            ? iff.Color
            : (Color?) null;

        var signs = EntityQueryEnumerator<BusScheduleComponent, AppearanceComponent>();

        while (signs.MoveNext(out var signUid, out var sign, out var appearance))
        {
            _appearance.SetData(signUid, PublicTransitVisuals.Livery, color ?? sign.IdleColor, appearance);
        }
    }

    /// <summary>
    /// Speaks a line over every shuttle console aboard the given bus, optionally with a chime.
    /// </summary>
    // Triad: both callers walked every shuttle console in the sector and filtered by grid, once per
    // bus per announcement. Same walk, but in one place and only when there is something to say.
    private void AnnounceToBus(EntityUid busUid, string message, bool chime = false)
    {
        var consoleQuery = EntityQueryEnumerator<ShuttleConsoleComponent>();
        var chimed = false;

        while (consoleQuery.MoveNext(out var consoleUid, out _))
        {
            if (Transform(consoleUid).GridUid != busUid)
                continue;

            // Triad: the station announcement chime, but PlayPvs off the console rather than the
            // PlayGlobal that ChatSystem uses, so it is heard aboard the bus instead of sector-wide.
            // Once per call: a bus with two consoles should not double up the sound on itself.
            if (chime && !chimed)
            {
                chimed = true;
                _audio.PlayPvs(ChatSystem.DefaultAnnouncementSound, consoleUid,
                    AudioParams.Default.WithVolume(-2f));
            }

            // Triad: HideChat keeps these out of the chat window. They still speak over the console, so
            // the bubble is there for anyone stood on the shuttle, but a bus running a loop all round
            // would otherwise push a line into everyone's chat at every stop. Ambient, not conversation.
            _chat.TrySendInGameICMessage(consoleUid, message,
                InGameICChatType.Speak, ChatTransmitRange.HideChat, hideLog: true, checkRadioPrefix: false,
                ignoreActionBlocker: true);
        }
    }

    /// <summary>
    /// Here is handling a simple CVAR change to enable/disable the system
    /// if the cvar is changed to enabled, we setup the transit system
    /// if its changed to disabled, we delete any bus grids that exist
    /// along with anyone/thing riding the bus
    /// you've been warned
    /// </summary>
    private void SetTransit(bool obj)
    {
        Enabled = obj;

        if (Enabled)
        {
            SetupPublicTransit();
        }
        else
        {
            var shuttleQuery = AllEntityQuery<TransitShuttleComponent>();

            while (shuttleQuery.MoveNext(out var uid, out _))
            {
                QueueDel(uid);
            }
        }
    }

    /// <summary>
    /// Simple cache reflection
    /// </summary>
    private void SetFly(float obj)
    {
        FlyTime = obj;
    }

    /// <summary>
    /// Here is where we handle setting up the transit system, including sanity checks.
    /// This is called multiple times, from a few different sources, to ensure that if the system is activated dynamically
    /// it will still function as intended
    /// </summary>
    private void SetupPublicTransit()
    {
        // If a public bus alraedy exists, we simply return. No need to set up the system again.
        var query = EntityQueryEnumerator<TransitShuttleComponent>();
        while (query.MoveNext(out var euid, out _))
        {
            if (!Deleted(euid))
                return;
        }

        // Triad: which stops exist varies per round, since POIs roll for their spawn. Without this the
        // only way to know whether somewhere is actually on the line is to stand there and wait.
        Log.Info($"Public transit route ({StationList.Count} stops): " +
                 string.Join(" -> ", StationList.Select(x => ToPrettyString(x))));

        // Spawn the bus onto a dummy map
        var dummyMapUid = _map.CreateMap(out var dummyMap);
        var busMap = new ResPath(_cfgManager.GetCVar(NFCCVars.PublicTransitBusMap));
        if (_loader.TryLoadGrid(dummyMap, busMap, out var shuttleEnt))
        {
            var shuttle = shuttleEnt.Value;
            var shuttleComp = Comp<ShuttleComponent>(shuttle);
            // Here we are making sure that the shuttle has the TransitShuttle comp onto it, in case of dynamically changing the bus grid
            var transitComp = EnsureComp<TransitShuttleComponent>(shuttle);

            //We run our bus station function to try to get a valid station to FTL to. If for some reason, there are no bus stops, we will instead just delete the shuttle
            if (TryGetNextStation(out var station) && station is { Valid: true } destination)
            {
                //we set up a default in case the second time we call it fails for some reason
                transitComp.NextStation = destination;

                // Ensure the shuttle is undocked before initiating FTL travel
                _dockSystem.UndockDocks(shuttle);
                // Triad: the Update loop asks for the DockTransit berth and this path did not, so the
                // bus's opening trip of the round always berthed wherever sorted first regardless of
                // how the stop was mapped. Same tag both places.
                _shuttles.FTLToDock(shuttle, shuttleComp, destination, hyperspaceTime: 5f, priorityTag: BerthTag);

                // Triad: FTLToDock returns silently if the destination map has no FTLDestination or is
                // disabled. Left unchecked, the bus stays on the dummy map and the TimedDespawn below
                // deletes it along with the map: no bus all round and not one log line saying why.
                if (!HasComp<FTLComponent>(shuttle))
                {
                    Log.Error($"Public transit bus could not FTL to {ToPrettyString(destination)}; is the map missing FTLDestination? Deleting the bus.");
                    QueueDel(shuttle);
                    return;
                }

                transitComp.NextTransfer = _timing.CurTime + TimeSpan.FromSeconds(_cfgManager.GetCVar(NFCCVars.PublicTransitWaitTime));

                //since the initial cached value of the next station is actually the one we are 'starting' from, we need to run the
                //bus stop list code one more time so that our first trip isnt just Frontier - Frontier
                if (TryGetNextStation(out var firstStop) && firstStop is { Valid: true } firstDestination)
                    transitComp.NextStation = firstDestination;
            }
            else
                QueueDel(shuttle);
        }

        // the FTL sequence takes a few seconds to warm up and send the grid, so we give the temp dummy map
        // some buffer time before calling a self-delete
        var timer = AddComp<TimedDespawnComponent>(dummyMapUid);
        timer.Lifetime = 15f;
    }
}
