using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Content.Server._Mono.Shuttles.Components;
using Content.Server._NF.Shipyard.Systems;
using Content.Server._Triad.ContrabandPermit;
using Content.Server._NF.Station.Components;
using Content.Server.NodeContainer;
using Content.Server.NodeContainer.Nodes;
using Content.Server.Nuke;
using Content.Server.Database;
using Content.Server.Shuttles.Systems;
using Content.Server.Station.Components;
using Content.Server.Station.Systems;
using Content.Shared._Mono.ShipRepair.Components;
using Content.Shared._Mono.Ships.Components;
using Content.Shared._Mono.Shipyard; // Triad
using Content.Shared._NF.Shipyard.Prototypes;
using Content.Shared._Triad.CCVar;
using Content.Shared._Triad.ContrabandPermit;
using Content.Shared._Triad.Shipyard.Save.Contraband;
using Content.Shared._Triad.ShipSize;
using Content.Shared.Anomaly.Components;
using Content.Shared.Damage;
using Content.Shared.Explosion.Components;
using Content.Shared.FixedPoint;
using Content.Shared.Mobs.Components;
using Content.Shared.NodeContainer;
using Content.Shared.Nuke;
using Content.Shared.Shuttles.Components;
using Content.Shared.Singularity.Components;
using Content.Shared.Station.Components;
using Content.Shared.Store.Components;
using Robust.Shared.Configuration;
using Robust.Shared.CPUJob.JobQueues;
using Robust.Shared.EntitySerialization;
using Robust.Shared.EntitySerialization.Systems;
using Robust.Shared.Map.Components;
using Robust.Shared.Serialization.Manager;
using Robust.Shared.Serialization.Markdown;
using Robust.Shared.Serialization.Markdown.Mapping;
using Robust.Shared.Utility;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace Content.Server._Triad.Drydock;

/// <summary>
/// Stores a deeded grid as an engine-serialized document in the database and takes it off the map.
/// The pipeline is entirely in memory: serialize, validate, checksum, compress, file, despawn. No
/// file ever touches disk, which is the whole point of replacing the ship save system.
///
/// <para>None of those phases happens in one tick any more. Everything from the freeze onwards runs
/// a few milliseconds at a time, on a private map that was paused before the ship was moved onto it:
/// it is fine to make the one captain who pressed the button wait as long as it takes, and it is not
/// fine to stall sixty other players for a reason they cannot perceive. The ship is frozen and
/// invisible for that whole span, which is what makes an elastic duration safe rather than merely
/// slower - a sliced walk over a ship that was still flying would tear its own snapshot.</para>
/// </summary>
public sealed partial class DrydockSystem : EntitySystem
{
    [Dependency] private IConfigurationManager _cfg = default!;
    [Dependency] private IDependencyCollection _dependency = default!;
    [Dependency] private ISerializationManager _serialization = default!;
    [Dependency] private DrydockStore _store = default!;
    [Dependency] private DrydockFidelitySystem _fidelity = default!;
    [Dependency] private MapLoaderSystem _mapLoader = default!;
    [Dependency] private ShipSizeSystem _shipSize = default!;
    [Dependency] private ShipyardSystem _shipyard = default!;
    [Dependency] private DockingSystem _docking = default!;
    [Dependency] private StationSystem _station = default!;
    [Dependency] private ContrabandPermitSystem _contrabandPermit = default!;

    /// <summary>
    /// Components cut from the live grid before it is written, because they are derived state or
    /// hold references that rot across a reload.
    ///
    /// <para>The repair data is session-scoped: entity references and raw tile ids, regenerated
    /// against the loaded grid. Station membership must not ride the document at all, because the
    /// station is round-scoped and rebuilt on retrieve, so a serialized reference reloads as invalid
    /// and the deserializer logs an error on every single load, the validation scratch load
    /// included.</para>
    /// </summary>
    private static readonly Type[] StoreStripList =
    {
        typeof(ShipRepairDataComponent),
        typeof(StationMemberComponent),
        // A powered-down helm parks its job slots here with the station they belonged to, and the
        // station is round-scoped like the membership above: serialized, it reloads as an invalid
        // reference and logs on every load. The recreated station gets its vessel's slots anyway.
        typeof(ShuttleConsoleJobSlotsComponent),
        // Guest access granted at the helm, as raw uids of cards the guests carry away with them:
        // off-grid references that reload invalid, and a permission that should not outlast the
        // voyage. Retrieve starts with none.
        typeof(ShipGuestAccessComponent),
    };

    /// <summary>
    /// Whether the drydock is on and not read-only, the gate every write path checks before it
    /// touches a row or a grid.
    /// </summary>
    private bool DrydockWritable => _cfg.GetCVar(TriadCCVars.DrydockEnabled) && !_cfg.GetCVar(TriadCCVars.DrydockReadOnly);

    /// <summary>
    /// Stores <paramref name="gridUid"/> for <paramref name="ownerUserId"/>. The order is gate,
    /// depart, freeze, prepare, serialize, validate, commit, despawn, and the grid is only removed
    /// once the document is filed.
    ///
    /// <para>This method is only the wrapper. It owns three things: the two refusals that must bypass
    /// every undo, the re-entrancy sentinel, and the choice between a sliced job and running the
    /// pipeline inline. <see cref="RunStorePipeline"/> is the store.</para>
    ///
    /// <para>Identity is resolved before it is minted. A grid that already carries a
    /// <see cref="DrydockIdentityComponent"/>, whether from an earlier retrieve this round or from a
    /// previous round entirely, files a new revision against the same hull. Only a grid that has
    /// never been stored mints a fresh id. Getting this backwards forks a new independently
    /// retrievable row on every store while the old one stays retrievable too, which is unbounded
    /// duplication on the happy path.</para>
    /// </summary>
    /// <param name="berthId">
    /// The berth to land in, or null to let the store pick: the ship's own last berth if free and
    /// fitting, else the smallest free berth that fits. A named berth still has to be the owner's,
    /// free, and large enough.
    /// </param>
    /// <param name="stationUid">
    /// The station the console resolved, and where the unwind puts the ship back if anything refuses
    /// after the freeze. Null falls back to the grid's own owning station, which is the right answer
    /// for every caller that is not a console; by the time the unwind runs the grid is on a private
    /// map and has no owning station left to ask.
    /// </param>
    /// <param name="onProgress">
    /// Fired on the main thread whenever the whole integer percent changes or a phase opens. A store
    /// is elastic on purpose, so any caller with a player waiting on it is expected to pass one: the
    /// percentage is what makes an unbounded wait acceptable rather than alarming.
    /// </param>
    /// <param name="impound">
    /// Set to take the hull into the impound lot rather than put it away in a berth. See
    /// <see cref="DrydockImpound"/> and <see cref="TryImpoundShip"/>; ordinary callers leave it null
    /// and get every gate the way it was written.
    /// </param>
    /// <param name="inline">
    /// Run the whole pipeline on this caller's async path with no job and the engine's own
    /// serializer, whatever the slicing cvars say. The round-end sweep sets it: nobody is left to
    /// protect from a hitch and the restart is waiting. See <see cref="DrydockStoreContext.Inline"/>.
    /// </param>
    /// <param name="permitHolderMind">
    /// The mind of whoever is putting the ship away, when somebody is: a console store and an import
    /// pass it, and then only permits issued to that mind travel. Null judges permits by the owning
    /// account instead, which is all an impound or the round-end sweep can vouch for. See
    /// <see cref="DrydockStoreContext.PermitHolderMind"/>.
    /// </param>
    public async Task<(DrydockStoreResult Result, Guid? ShipId)> TryStoreShip(
        EntityUid gridUid,
        Guid ownerUserId,
        int? roundId,
        int? berthId = null,
        EntityUid? stationUid = null,
        DrydockProgressCallback? onProgress = null,
        DrydockImpound? impound = null,
        bool inline = false,
        EntityUid? permitHolderMind = null)
    {
        if (!DrydockWritable)
            return (DrydockStoreResult.Disabled, null);

        // The sentinel, before anything yields. A second store request for the same grid while this
        // one is awaiting the database would otherwise run the whole preparation again on a grid
        // mid-store and file a second revision of it. Both this refusal and the disabled one above
        // deliberately sit outside the try below: the finally removes the marker unconditionally, so
        // a second request that fell through it would strip the first store's sentinel.
        //
        // Re-entrancy is all the marker does now: a ship frozen on a private map with nobody aboard
        // has nothing that can insert into it. The window that leaves open is the one between this
        // stamp and the freeze, which is a single awaited capacity check: for that tick plus a
        // database round trip the ship is live,
        // docked and unguarded. That is the exposure the pre-slicing code already had after its own
        // await, and it is accepted.
        if (HasComp<DrydockInProgressComponent>(gridUid))
            return (DrydockStoreResult.InProgress, null);

        // A grid on a drydock staging map is inside a pipeline already: a retrieve still loading it,
        // or an unwind that could not put the ship back. The sentinel above cannot see either, and a
        // second pipeline would freeze a frozen hull, so this refuses as in progress and sits with
        // the other refusals outside the try. Only the admin ripcord can reach it: no console can
        // see a grid on a private map.
        var homeXform = Transform(gridUid);
        if (homeXform.MapUid is { } currentMap && HasComp<DrydockStagingMapComponent>(currentMap))
            return (DrydockStoreResult.InProgress, null);

        EnsureComp<DrydockInProgressComponent>(gridUid);

        var ctx = new DrydockStoreContext
        {
            GridUid = gridUid,
            OwnerUserId = ownerUserId,
            RoundId = roundId,
            BerthId = berthId,
            StationUid = stationUid ?? _station.GetOwningStation(gridUid) ?? EntityUid.Invalid,
            Impound = impound,
            Inline = inline,
            PermitHolderMind = permitHolderMind,
            HomeMap = homeXform.MapUid,
            HomePosition = _xform.GetWorldPosition(gridUid),
        };

        // Hoisted so the finally can retire the job and record its meter on every path, the rollback
        // lever included.
        DrydockPipelineJob<DrydockStoreContext, DrydockStoreOutcome>? job = null;

        try
        {
            // A budget of zero or less is the rollback lever on a pipeline whose deploy has no other
            // one: no job, no queue, no queue latency, and the whole store on this caller's own async
            // path, in the order it ran before slicing. It is also what the integration fixtures set,
            // because a sliced store outruns their tick pumps, and what the round-end sweep asks for
            // by name, because nobody is left to protect from a hitch.
            var budget = TickBudgetSeconds;
            if (budget <= 0 || inline)
            {
                var direct = await RunStorePipeline(ctx, new DrydockSyncSlice(DrydockPhases.Store, onProgress));
                return (direct.Result, direct.ShipId);
            }

            job = StartPipelineJob<DrydockStoreContext, DrydockStoreOutcome>(
                ctx, budget, onProgress, DrydockPhases.Store, RunStorePipeline);

            DrydockStoreOutcome? outcome;
            try
            {
                outcome = await job.AsTask;
            }
            catch (OperationCanceledException)
            {
                // A round restart, a shutdown, or the slice watchdog. The pipeline's own finally has
                // already run the unwind, so the ship is back where it was; nothing was filed, because
                // the pipeline stops being cancellable the moment the filing transaction commits. The
                // job wrapper cancels its task without recording an exception, so a cancelled job is
                // only ever visible here and never through its exception property.
                return (DrydockStoreResult.Cancelled, null);
            }

            // A pipeline that threw has already thrown out of the await above: the job wrapper
            // records the exception and faults the task with it in the same catch.
            return outcome is null
                ? (DrydockStoreResult.SerializeFailed, null)
                : (outcome.Result, outcome.ShipId);
        }
        finally
        {
            EndPipelineJob(job);

            // Covers a job cancelled before its body ever ran, as well as every ordinary exit. On
            // success the grid is already queued for deletion and this is a no-op; on any refusal it
            // re-opens the ship to a second store attempt.
            if (!TerminatingOrDeleted(gridUid))
                RemCompDeferred<DrydockInProgressComponent>(gridUid);
        }
    }

    /// <summary>
    /// The store itself, from the identity stamp through the despawn, written against a tick budget.
    /// Driven either by <see cref="DrydockPipelineJob{TContext,TOutcome}"/> or, when the budget cvar
    /// is off, by <see cref="DrydockSyncSlice"/> on the caller's own async path.
    /// </summary>
    /// <remarks>
    /// <para>The only legal awaits in here are on the slice. A bare await leaves the job with no
    /// resume handle: it asserts in debug and hangs forever in release, and a hung store is a frozen
    /// ship parked on a private map for the rest of the round.</para>
    /// <para>Every suspension is a hole in the world's continuity, so every one of them is followed
    /// by <see cref="GuardStoreResume"/>. The two post-await re-checks the old shape needed are still
    /// here; they simply have siblings now.</para>
    /// </remarks>
    internal async Task<DrydockStoreOutcome> RunStorePipeline(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        var gridUid = ctx.GridUid;
        var timer = ctx.Timer;
        var jobId = JobIdOf(slice);

        var mobQuery = GetEntityQuery<MobStateComponent>();
        var xformQuery = GetEntityQuery<TransformComponent>();

        void MarkPhase(DrydockPhase phase) => timer.Mark(DrydockPhases.Mark(phase));

        try
        {
            // Opening a phase suspends, so even the very first line of the store is already a tick
            // away from the caller that asked for it.
            await slice.Begin(DrydockPhase.Gate, 0);
            GuardStoreResume(ctx);

            // Capacity first, because it is the one gate that needs the database. The await sits
            // before any other gate has been passed and before any mutation, so nothing has to be
            // re-checked after it. A full garage refuses cheaply here; the filing transaction
            // checks again, and the unique index on the berth column makes that answer the final
            // one. Both reads are of the live grid: the cached class text on the row is never
            // load-bearing.
            var shipId = ResolveOrMintShipId(gridUid);
            ctx.ShipId = shipId;
            var sizeClass = _shipSize.GetSizeClass((gridUid, Comp<MapGridComponent>(gridUid))).ToString();

            // An impound needs no berth: the lot is not one, and requiring a free berth
            // would make the round-end sweep fail for exactly the owners whose garage is full.
            if (ctx.Impound == null)
            {
                var capacity = await slice.Await(
                    _store.CheckBerthForStore(shipId, ctx.OwnerUserId, sizeClass, ctx.BerthId));

                if (capacity != DrydockBerthResult.Success)
                    return new DrydockStoreOutcome(BerthRefusal(capacity), null);
            }

            if (TerminatingOrDeleted(gridUid))
                return new DrydockStoreOutcome(DrydockStoreResult.SerializeFailed, null);

            // Hazards next, because the check mutates nothing and refusing here means nobody has
            // been moved for a store that was never going to happen. Runtime countdowns are
            // ordinary data fields that would resume on thaw, so an armed ship must be refused
            // rather than frozen. An impound has nowhere to refuse to, so it clears them first and
            // then passes this same gate, which stays put as the backstop.
            if (ctx.Impound != null)
                PurgeHazardsAboard(gridUid);

            if (HasHazardAboard(gridUid))
                return new DrydockStoreOutcome(DrydockStoreResult.HazardAboard, null);

            // A mind must never be serialized, and a living mob does not round-trip cleanly. A
            // store refuses, which is the safe direction to be stricter in; an impound moves them
            // off and passes the same gate, for the same reason it purges rather than refuses.
            if (GateOrganics(ctx, gridUid, mobQuery, xformQuery))
                return new DrydockStoreOutcome(DrydockStoreResult.OrganicsAboard, null);

            EnsureComp<DrydockIdentityComponent>(gridUid).ShipId = shipId;
            MarkPhase(DrydockPhase.Gate);

            var shipName = Comp<MetaDataComponent>(gridUid).EntityName;

            // Two sources, both written by a purchase, and the station is asked first because a
            // vessel's own station config is what fills it in. The grid's VesselComponent is the
            // fallback, and it is the only one a legacy import has: that path stages the hull into
            // the CONSOLE's station, which carries no vessel information at all, so a station-only
            // read filed every imported hull with no vessel and retrieve handed it a plain station
            // forever after. Read at the top of the freeze block for two reasons: the strip further
            // down cuts station membership off the grid, and the unwind needs the vessel's priority
            // dock tag to hand a refused ship back at the same kind of berth a purchase of it would
            // have picked. Reparenting does not touch station membership - the station system
            // subscribes to no parent change - so this reads the same either side of the freeze.
            var vesselProto = TryComp<StationMemberComponent>(gridUid, out var stationMember)
                              && TryComp<ExtraShuttleInformationComponent>(stationMember.Station, out var vesselInfo)
                              && vesselInfo.Vessel is { } vessel
                ? vessel.Id
                : VesselIdOnGrid(gridUid);

            ctx.ReturnDockTag = PriorityDockTagFor(vesselProto);

            // The departure. An observer sees what a real jump shows them, because a real jump's
            // vanish is itself a map reparent: the startup sound, then gone. Deliberately not the
            // thruster half of a real jump setup, which latches the thrusters north and would be
            // serialized that way, and deliberately not real FTL at all - that is a multi-tick state
            // machine whose updates run on the paused-skipping enumerator, so freezing mid-jump
            // stalls it forever and leaves behind exactly the component the strip below removes.
            PlayDepartureEffect(gridUid);

            // Read while the docks still exist, which is only true for one more statement. The
            // unwind's first choice of where to put the ship back is whatever it was docked to.
            ctx.ReturnTarget = FindDockedPartnerGrid(gridUid);

            // The last free refusal. Everything past this point either moves the ship or takes
            // something off it, and right here the ship is still whole and still docked, so a
            // straggler who walked aboard while the capacity check was in the database costs nothing
            // to refuse for. The old post-database counterpart of this check is gone with it: nobody
            // can walk aboard a ship on a private map, and the reparent that puts it there stays on
            // this side of the next suspension (see FreezeOntoStagingMap).
            if (GateOrganics(ctx, gridUid, mobQuery, xformQuery))
                return new DrydockStoreOutcome(DrydockStoreResult.OrganicsAboard, null);

            // Hoisted above the reparent, which is where upstream's own jump setup puts it. A stored
            // ship has to be fully detached regardless - its docking partner is not in the document,
            // so a serialized dock reloads as an invalid reference and crashes the docking system's
            // startup on every load, the validation scratch load included - but the reason it has to
            // happen HERE is that a grid still weld-jointed to a station cannot be reparented
            // cleanly.
            ctx.Undocked = true;
            _docking.UndockDocks(gridUid);

            // From here the ship is private, frozen and invisible, and every phase below runs on the
            // tick budget. The map is paused while it is still empty, so the engine's own recursive
            // pause walks one entity instead of nine hundred; the reparent onto it is then what
            // actually freezes the tree, and the walk inside the freeze drives the bar and back-stops.
            ctx.StagingMap = CreateStagingMap(jobId, DrydockStagingKind.Store, shipId, mapInit: true);
            await FreezeOntoStagingMap(gridUid, ctx.StagingMap.Value, slice);
            ctx.Frozen = true;
            GuardStoreResume(ctx);

            // Backstop to the gate above, so a future reorder cannot reopen the window silently.
            // Anyone found here boarded before the reparent. Refusing is still free for a store: the
            // purge and every strip are below, and the unwind thaws the hull and docks it back with
            // them on it, which is the answer the gate would have given. An impound has already
            // deleted hazards and moved occupants by here and puts neither back on any later
            // refusal; that trade is stated on DrydockSystem.Impound.cs.
            if (GateOrganics(ctx, gridUid, mobQuery, xformQuery))
                return new DrydockStoreOutcome(DrydockStoreResult.OrganicsAboard, null);

            MarkPhase(DrydockPhase.Freeze);

            // The saving-contraband purge, by the ship-save path's two rules: marked entities go
            // unless they carry a permit, and a permit that does not belong to whoever the ship is
            // going away for takes its item with it. After every refusal above, so a refused store
            // deletes nothing, and before the appraisal, so the quote is for what is filed.
            // Not undoable.
            await PurgeSavingContrabandSliced(ctx, slice);
            MarkPhase(DrydockPhase.Purge);

            // The sale quote, taken while the hull is whole and before any sidecar or strip
            // touches it, so what a scrap pays is what the shipyard would have paid at this moment.
            await slice.Begin(DrydockPhase.Appraise, 0);
            GuardStoreResume(ctx);
            var appraisal = _shipyard.AppraiseHull(gridUid);
            MarkPhase(DrydockPhase.Appraise);

            // Three walks under one phase name, each opening it with its own item count. The
            // percentage is clamped monotonic, so re-opening a phase reads as a stall inside its band
            // rather than as a bar running backwards.
            //
            // A pipe net's air lives on the node-group graph, which the serializer cannot reach.
            // Distribute each net's gas across its members by volume. The live net is left alone -
            // pointlessly now, since the ship is frozen on a private map and nothing will ever read
            // it again, but writing to it would be a mutation with no undo entry for no gain.
            await InjectPipeGasSidecarsSliced(ctx, slice);

            // Damage is read-only to the serializer, so a damaged ship would come back pristine.
            await InjectDamageSidecarsSliced(ctx, slice);

            // Appearance data is not a data field at all, so no save has ever carried it and the
            // probe below cannot see it either. Without this a retrieved ship's visuals come back
            // at prototype defaults wherever the owning system does not re-derive them on startup.
            await _fidelity.CaptureAppearanceSliced(gridUid, ctx.InjectedAppearance, slice);
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Sidecars);

            await StripListedComponentsSliced(ctx, slice);

            // The grid's own deed names the card holding it, which is outside the document. Written
            // as-is it reloads as an invalid reference and the deserializer logs an error on every
            // scratch load and every retrieve; retrieve sets the holder afresh anyway. The flag goes
            // up before the call, not after: an abort between the two restores a null holder, which
            // is what the reattach would have been handed anyway.
            ctx.DeedDetached = true;
            ctx.DeedHolder = _shipyard.DetachGridDeedHolder(gridUid);

            // A PDA's store remembers the map it was set up on, which is likewise outside the
            // document and reloads as an invalid reference that logs on every load. Purchased
            // ships already leave that map behind when they dock, so a retrieved store is no
            // worse off for coming back with the field blank.
            await DetachStoreMapsSliced(ctx, slice);
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Strip);

            // The general net, after the two specific sidecars and the strip list so it sees the
            // final live component set. For every unserializable populated field it either captures
            // the value or strips it, and clears the live field either way. The ledger is created and
            // handed to the context BEFORE the walk starts: it is the only record of what was blanked
            // and on which entity, and a walk that can now be abandoned half-way through must not be
            // the thing that owns it.
            ctx.Fidelity = new DrydockFidelityCapture();
            await _fidelity.CaptureAndStripSliced(gridUid, ctx.Fidelity, slice);
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Capture);

            await slice.Begin(DrydockPhase.Prepare, 0);
            GuardStoreResume(ctx);

            // The rest of preparation is deliberately not undoable, and runs last for that reason.
            // An empty AI core is the intended end state, and a ship at rest has no business carrying
            // FTL state.
            SanitizeStationAiCores(gridUid);

            // A ship stored during its FTL cooldown still carries the component the jump added. A
            // reborn ship carrying it comes back mid-jump and the shuttle system errors on it every
            // tick, which leaves it stuck.
            RemComp<FTLComponent>(gridUid);

            // A ship document is self-contained. The engine default drags any referenced null-space
            // entity into the save, and a ship that is its own station references that station,
            // which pulls the whole station in along with state the serializer cannot write. Ignore
            // turns those references into invalid ones, which retrieve rebinds. Transform parenting
            // is exempt, so grid children are unaffected.
            var saveOptions = new SerializationOptions { MissingEntityBehaviour = MissingEntityBehaviour.Ignore };
            MarkPhase(DrydockPhase.Prepare);

            // Two ways to write the document. With DrydockSlicedSerialize on, SerializeGridSliced
            // drives the engine's public per-entity serializer one entity at a time against the
            // budget, working around the two private members of the engine's own wrapper (see
            // DrydockSystem.Serialize.cs). Off, or on an inline store, it is TrySaveGrid: one atomic
            // call that is then one of the calls setting the real per-tick ceiling - the honest
            // claim there is "the budget plus the longest bulk call", not "the budget" - with the
            // whole phase landing inside one tick.
            string yaml;

            if (_cfg.GetCVar(TriadCCVars.DrydockSlicedSerialize) && !ctx.Inline)
            {
                // Opens its own phase, because it knows the entity count and the bar wants it.
                var sliced = await SerializeGridSliced(ctx, slice, saveOptions);
                GuardStoreResume(ctx);

                if (sliced == null)
                    return new DrydockStoreOutcome(DrydockStoreResult.SerializeFailed, null);

                yaml = sliced;
            }
            else
            {
                await slice.Begin(DrydockPhase.Serialize, 0);
                GuardStoreResume(ctx);

                using var writer = new StringWriter();
                if (!_mapLoader.TrySaveGrid(gridUid, writer, saveOptions))
                    return new DrydockStoreOutcome(DrydockStoreResult.SerializeFailed, null);

                yaml = writer.ToString();
            }

            MarkPhase(DrydockPhase.Serialize);

            // The parse half of the reload can leave the main thread; the build half cannot, so this
            // is now two marks instead of one. See DetectRoundTripMismatch for why.
            await slice.Begin(DrydockPhase.Validate, 0);
            GuardStoreResume(ctx);

            var (mismatch, liveEntities) = await DetectRoundTripMismatch(ctx, slice, yaml);
            if (mismatch)
            {
                DrydockMetrics.ValidationMismatches.Inc();
                return new DrydockStoreOutcome(DrydockStoreResult.ValidationFailed, null);
            }

            MarkPhase(DrydockPhase.Validate);

            // Everything from here to the commit is pure byte work: no entity, no component, no map,
            // so it is the one part of a store that can leave the main thread at all. Encoding and
            // checksumming a multi-megabyte document, and compressing it, are real milliseconds that
            // the server no longer has to spend. Each hop back costs a tick of latency, which an
            // elastic store does not care about.
            //
            // Checksum the uncompressed document, so stored hashes survive a future change of
            // compression.
            await slice.Begin(DrydockPhase.Hash, 0);
            var hashed = await slice.Await(Task.Run(() =>
            {
                var bytes = Encoding.UTF8.GetBytes(yaml);
                return (Bytes: bytes, Checksum: SHA256.HashData(bytes));
            }));

            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Hash);

            await slice.Begin(DrydockPhase.Drift, 0);
            var (fingerprint, engineFormat) = await slice.Await(Task.Run(() => ReadDriftMetadata(yaml)));
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Drift);

            // Its last read: the async state machine would otherwise pin the document to the commit.
            yaml = null!;

            // The last walk of the live tree, and it has to finish before the despawn below.
            var manifest = new DrydockManifest();
            await BuildManifestSliced(ctx, slice, manifest);
            GuardStoreResume(ctx);

            var request = new DrydockRevisionRequest
            {
                ShipGuid = shipId,
                OwnerUserId = ctx.OwnerUserId,
                ShipName = shipName,
                VesselProto = vesselProto,
                SizeClass = sizeClass,
                BerthId = ctx.BerthId,
                Kind = DrydockRevisionKind.PlayerStore,
                // An impound is filed by whoever took the hull, an admin or the sweep, never by the
                // owner: the revision and its audit row both name this, and a timeline that says the
                // owner impounded their own ship is the wrong record of an adjudication.
                ActorUserId = ctx.Impound != null ? ctx.Impound.ActorUserId : ctx.OwnerUserId,
                CreatedRoundId = ctx.RoundId,
                EngineFormatVer = engineFormat,
                ProtoFingerprint = fingerprint,
                CapturedKeyHash = ctx.Fidelity.ComputeCapturedKeyHash(),
                Checksum = hashed.Checksum,
                SizeBytes = hashed.Bytes.Length,
                AppraisedValue = appraisal,
                Manifest = manifest.Serialize(),

                Impound = ctx.Impound,
                Evicted = ctx.Evicted,
            };

            MarkPhase(DrydockPhase.Manifest);

            await slice.Begin(DrydockPhase.Compress, 0);
            var payload = await slice.Await(Task.Run(() => CompressZstd(hashed.Bytes)));
            GuardStoreResume(ctx);
            MarkPhase(DrydockPhase.Compress);

            // Likewise: checksum and size are already on the request.
            hashed = default;

            await slice.Begin(DrydockPhase.Commit, 0);
            GuardStoreResume(ctx);

            // Held in a local because the suspension inside slice.Await lands AFTER the transaction
            // commits: Job.WaitAsyncTask awaits the task and only then parks on a resume handle
            // (RobustToolbox Job.cs:92-107), which Job.Run cancels when the job is cancelled.
            // Reading the result off the task is what stops a round restart landing in that gap from
            // throwing out of a store whose revision is already durable.
            var fileTask = _store.FileRevision(request, payload, _cfg.GetCVar(TriadCCVars.DrydockKeepBlobs));

            DrydockFileResult filed;
            try
            {
                filed = await slice.Await(fileTask);
            }
            catch (OperationCanceledException) when (fileTask.IsCompletedSuccessfully)
            {
                // Not through the slice, and it does not need to be: the filter above has already
                // established the task is complete, so this await returns synchronously and cannot
                // strand the job the way an unwrapped suspending await would.
                filed = await fileTask;
            }

            MarkPhase(DrydockPhase.Commit);

            // The garage filled up between the capacity check and the commit, or this store lost
            // the last berth to another committing in the same instant. The transaction rolled back,
            // so nothing was filed, the ordinary staleness rules still apply, and the unwind below
            // thaws the ship and hands it back to the station.
            if (filed.Outcome != DrydockBerthResult.Success)
            {
                GuardStoreResume(ctx);
                return new DrydockStoreOutcome(BerthRefusal(filed.Outcome), null);
            }

            // Past a successful filing the store is durable and stops being abortable. No suspension
            // and no resume guard between here and ctx.Committed, deliberately: an abort in this
            // window unwinds a ship whose revision is already filed and whose berth is already
            // seated, and if the grid died while the write was in the database it leaves the row
            // checked out with no hull behind it, which is the state MarkStored exists to prevent.
            // A grid that died in that window is the end state the despawn was about to produce.
            //
            // No organics re-check here: the write above yields, but there is no boarding a ship that
            // has been on a paused map of its own since long before the write started. The re-check in
            // the freeze block is what covers the one window that is still real.
            slice.Progress.BeginPhase(DrydockPhase.Despawn, 0);

            // A no-op when the grid died while the write was in flight, which is the point.
            QueueDel(gridUid);
            ctx.Committed = true;

            // The staging map goes with the ship. Not through ScrapStagingMap, which refuses a map
            // that still carries a grid: that refusal is right everywhere except here, where the grid
            // on it is the ship we just filed and are deliberately despawning. Both are queued in the
            // same tick, so the order they run in does not matter.
            if (ctx.StagingMap is { } stagingMap && Exists(stagingMap))
                QueueDel(stagingMap);

            // The grid is queued for deletion and nothing below can bring it back, so the row may
            // now say stored. If this write fails the revision is filed and the ship is gone from
            // the world with its row still checked out, which is exactly the "wait for a human"
            // state an admin restore exists for, and not a duplicate.
            try
            {
                var marked = ctx.Impound != null
                    ? _store.MarkImpounded(shipId)
                    : _store.MarkStored(shipId);

                if (!await slice.Await(marked))
                    Log.Warning($"Drydock: {shipId} filed revision {filed.Revision} but its row did not move to {(ctx.Impound != null ? "impounded" : "stored")}; it may already be impounded.");
            }
            catch (OperationCanceledException)
            {
                // Caught apart from the general failure below, and deliberately not rethrown. This is
                // the one await that happens after the commit, so a cancellation landing on it is not
                // a cancelled store: the revision is filed and the ship is already gone. Letting it
                // travel would report a store that really happened as cancelled, and logging it at
                // Error would fail every pooled integration pair that cancels a pipeline.
                Log.Warning($"Drydock: {shipId} filed revision {filed.Revision} and despawned, but the pipeline "
                            + "was cancelled before its row moved to stored. An admin restore recovers it.");
            }
            catch (Exception e)
            {
                Log.Error($"Drydock: {shipId} filed revision {filed.Revision} but marking it stored failed: {e.Message}. An admin restore recovers it.");
            }

            MarkPhase(DrydockPhase.Despawn);

            // The per-phase figures are wall clock and always were, but under slicing that stops
            // being a footnote: a phase spanning fifty ticks reads as fifty ticks. The worst slice
            // beside them is the number that says whether anyone else felt it.
            var (worstSliceMs, slices) = slice is IDrydockPipelineJob job
                ? (job.WorstSliceMs, job.Slices)
                : (0d, 0);

            Log.Info(timer.Format("store", shipId, liveEntities, worstSliceMs, slices));

            return new DrydockStoreOutcome(DrydockStoreResult.Success, shipId);
        }
        catch (DrydockAbortedException e)
        {
            // The world moved under a parked pipeline. Turned into an ordinary outcome here and
            // never allowed to escape: the job's process wrapper logs any exception that reaches it
            // at Error, and a pooled integration pair fails its return on an unexpected error log.
            Log.Warning($"Drydock: store of {ctx.ShipId} aborted: {e.Reason}.");

            return new DrydockStoreOutcome(
                e.GridGone ? DrydockStoreResult.SerializeFailed : DrydockStoreResult.Cancelled,
                null);
        }
        finally
        {
            if (!ctx.Committed)
                UnwindStore(ctx);

            // Last, so the bar only reads finished once the ship is really back. Fired on every exit,
            // refusals included, because a client indicator that is never told the story ended sits
            // at whatever percent the refusal happened at.
            slice.Progress.Finish();
        }
    }

    /// <summary>Reason text for the two store aborts, for the log line. The outcome branches on <see cref="DrydockAbortedException.GridGone"/>.</summary>
    private const string AbortGridGone = "the grid was deleted while the pipeline was parked";

    /// <inheritdoc cref="AbortGridGone"/>
    private const string AbortMapGone = "the staging map was deleted while the pipeline was parked";

    /// <summary>
    /// The staleness check after every suspension. Slicing replaced three await-driven re-checks with
    /// a hole at every yield point: an administrator can delete the grid or its private map between
    /// any two slices, and a walk that carried on past that would blank a ship that no longer exists
    /// or file a document of half a hull.
    /// </summary>
    /// <exception cref="DrydockAbortedException">
    /// Always the way this fails. The pipeline catches it and turns it into an outcome.
    /// </exception>
    private void GuardStoreResume(DrydockStoreContext ctx)
    {
        if (TerminatingOrDeleted(ctx.GridUid))
            throw new DrydockAbortedException(AbortGridGone, gridGone: true);

        if (ctx.StagingMap is { } map && TerminatingOrDeleted(map))
            throw new DrydockAbortedException(AbortMapGone);
    }

    /// <summary>Step and guard, the pair every sliced store loop calls in place of a bare step.</summary>
    private async Task StoreStep(DrydockStoreContext ctx, IDrydockSlice slice, int index)
    {
        await slice.Step(index);
        GuardStoreResume(ctx);
    }

    /// <summary>
    /// The skeleton every sliced store walk shares: open the phase against a pre-built item list,
    /// run <paramref name="body"/> per item, then step. <paramref name="body"/> does its own
    /// existence and component checks, since which ones apply differs per walk.
    /// </summary>
    private async Task StoreSweep<T>(
        DrydockStoreContext ctx, IDrydockSlice slice, DrydockPhase phase, IReadOnlyList<T> items, Action<T> body)
    {
        await slice.Begin(phase, items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            body(items[i]);
            await StoreStep(ctx, slice, i);
        }
    }

    /// <summary>
    /// Which registered job is driving this slice, or zero for the synchronous path. The id is
    /// stamped on every private map the pipeline creates, so a map left behind names an owner the
    /// sweep can ask whether it is still alive. <see cref="StartPipelineJob{TContext,TOutcome}"/>
    /// stamps it onto the job right after <see cref="RegisterJob"/>, before the job is enqueued.
    ///
    /// <para>Zero makes a synchronous store's staging maps look ownerless to that sweep. That is the
    /// honest answer - there is no job to ask - and it costs nothing in practice, because the sweep
    /// only runs at a round boundary and a store still in flight across a round boundary was already
    /// the pre-slicing code's problem.</para>
    /// </summary>
    private static int JobIdOf(IDrydockSlice slice)
    {
        return slice is IDrydockPipelineJob job ? job.JobId : 0;
    }

    /// <summary>
    /// Puts back everything a refused store took off the ship, then puts the ship itself back at the
    /// station. Runs from the pipeline's finally on every path that did not commit, cancellation
    /// included.
    ///
    /// <para>Fully synchronous and cancellation-blind, and it has to be both. An async unwind inside a
    /// job would need to suspend, which is the one thing a cancelled job can no longer do; an unwind
    /// that honoured cancellation could never finish. It is also why the whole thing is written to
    /// tolerate anything already being gone rather than to assume a coherent world.</para>
    ///
    /// <para>The restores run in PREPARATION order rather than in reverse, which works because each
    /// one is independent of the others. The return leg is appended at the end deliberately: by the
    /// time the ship reappears at the station it is already whole.</para>
    /// </summary>
    private void UnwindStore(DrydockStoreContext ctx)
    {
        var gridUid = ctx.GridUid;

        if (!TerminatingOrDeleted(gridUid))
        {
            RemoveInjected<DrydockPipeGasComponent>(ctx.InjectedGas);
            RemoveInjected<DrydockDamageSidecarComponent>(ctx.InjectedDamage);
            RemoveInjected<DrydockAppearanceComponent>(ctx.InjectedAppearance);

            RestoreStrippedComponents(gridUid, ctx.Stripped);

            if (ctx.DeedDetached)
                _shipyard.ReattachGridDeedHolder(gridUid, ctx.DeedHolder);

            ReattachStoreMaps(ctx.StoreMaps);

            // Stripping station membership fired the station system's shutdown handler, which
            // removed this grid from its station's set. Restoring the component brings the
            // reference back but not the set entry, and that set is access-locked to the station
            // system, so the re-add has to go through it. Only after a strip actually happened:
            // the gates above the strip refuse through this same path, and re-booking a grid
            // that never left its station is not a no-op for the station's listeners.
            if (ctx.Stripped.Count > 0
                && TryComp<StationMemberComponent>(gridUid, out var restoredMember)
                && HasComp<StationDataComponent>(restoredMember.Station))
            {
                _station.AddGridToStation(restoredMember.Station, gridUid);
            }

            if (ctx.Fidelity != null)
                _fidelity.RestoreSnapshot(ctx.Fidelity);
        }

        if (ctx.StagingMap is not { } staging)
        {
            // Never frozen, so the ship is still where it was. It may still have been undocked, in
            // the one statement between the undock and the map: re-dock it rather than leaving a
            // ship the player thought was moored drifting alongside the station.
            if (ctx.Undocked && !TerminatingOrDeleted(gridUid))
                ReturnGridToStation(gridUid, ctx.ReturnTarget, ctx.StationUid, ctx.ReturnDockTag);

            return;
        }

        if (TerminatingOrDeleted(gridUid))
        {
            ScrapStagingMap(staging);
            return;
        }

        // The return leg. The dock places the ship on the station's own unpaused map, and that map
        // change thaws the whole subtree inside the engine's own recursive walk, so a successful
        // return needs no per-entity thaw of ours.
        if (ReturnGridToStation(gridUid, ctx.ReturnTarget, ctx.StationUid, ctx.ReturnDockTag))
        {
            ScrapStagingMap(staging);
            return;
        }

        // Nowhere to put it: the remembered docking partner and the station are both gone. The ship
        // stays frozen on its private map, re-kinded so the sweep leaves it alone and an admin can
        // still hand it back. Deleting it is the one thing this whole path exists to prevent, and a
        // budgeted thaw is not available to a synchronous unwind.
        if (TryComp<DrydockStagingMapComponent>(staging, out var stagingMap))
            stagingMap.Kind = DrydockStagingKind.Stranded;

        Log.Error($"Drydock: the unwind for ship {ctx.ShipId} found nowhere to return {ToPrettyString(gridUid)} to. "
                  + $"It is frozen on staging map {ToPrettyString(staging)}, restored and intact, awaiting an admin.");
    }

    /// <summary>Takes a sidecar ledger's component back off every entity on it that still exists.</summary>
    private void RemoveInjected<T>(List<EntityUid> injected) where T : IComponent
    {
        foreach (var uid in injected)
        {
            if (!TerminatingOrDeleted(uid))
                RemComp<T>(uid);
        }
    }

    /// <summary>
    /// Resolves before minting. The grid-side identity component survives both a store and retrieve
    /// cycle and a round boundary, so a second store lands on the same hull.
    /// </summary>
    private Guid ResolveOrMintShipId(EntityUid gridUid)
    {
        if (TryComp<DrydockIdentityComponent>(gridUid, out var identity) && identity.ShipId != Guid.Empty)
            return identity.ShipId;

        return Guid.NewGuid();
    }

    /// <summary>
    /// What the console tells the player when the drydock has nowhere to put the hull. Only the
    /// too-small case gets its own message, because its fix is different; every other berth
    /// outcome a store can produce means "no free berth", including losing a race for the last one.
    /// </summary>
    private static DrydockStoreResult BerthRefusal(DrydockBerthResult outcome)
    {
        return outcome switch
        {
            DrydockBerthResult.BerthTooSmall => DrydockStoreResult.BerthTooSmall,
            DrydockBerthResult.BerthOccupied => DrydockStoreResult.BerthOccupied,
            _ => DrydockStoreResult.NoBerth,
        };
    }

    /// <summary>
    /// Deserializes the document that was just written onto an inert scratch map and compares it
    /// with the live grid, in two tiers: a whole-grid entity count, then a per-prototype tally of
    /// each side's direct grid children, so a bug that swaps one kind of entity for another while
    /// preserving the count is caught too. The scratch map never initializes or ticks, so it cannot
    /// touch the live simulation, and it is deleted on every path out of this call.
    ///
    /// <para>The reload is two calls, not one. Parsing the YAML text into a document is pure CPU
    /// with no entity access, so it runs on a threadpool thread behind <see cref="ParseDocument"/>
    /// and the pipeline is free to suspend around it; that hop is what the <c>validate_parse</c>
    /// timer key measures. Building the parsed document into entities and diffing them against the
    /// live grid stays one atomic main-thread call, because the entities it creates have to be built
    /// and started within a single tick: a yield in the middle would hand every other system's
    /// Update a half-built scratch entity for however long the slice budget left it there. That call
    /// is what the caller's own <c>validate</c> mark now measures alone, so the two keys together
    /// show how the old single number split.</para>
    ///
    /// <para>That last clause used to be a promise about the whole store and is now only a promise
    /// about this method: the store around it spans ticks. Nothing inside here yields, so the map
    /// still lives and dies within one call and the tag it is given is not load-bearing today. It is
    /// tagged anyway, because it is now a private map created from inside a pipeline that can be
    /// cancelled, and an untagged private map is invisible to the sweep the moment anything ever does
    /// leave one behind. Deliberately loaded pre-init: map-initialising the scratch copy would fire
    /// map init across it and make the composition tally disagree on every store.</para>
    ///
    /// <para>A byte-for-byte double-serialize comparison would be the deeper check and was tried
    /// first in the implementation this comes from. It is not usable as a production gate on real
    /// ship content: a reload rebuilds fixtures and the broadphase, which legitimately resets
    /// physics state that is itself a data field, so the comparison differs with no content drift
    /// at all. That is one instance of an open-ended class rather than a single normalizable noise
    /// source.</para>
    ///
    /// <para>Both tiers count only what the serializer will actually write. The engine's
    /// <c>EntitySerializer.IsSerializable</c> skips any entity whose prototype declares
    /// <c>save: false</c>, a class of ninety-odd prototypes that includes every live sound effect:
    /// a sound played at grid coordinates is a real grid child until its despawn timer fires.
    /// Before this filter, a ship that happened to have a sound in the air at the moment of the
    /// store counted it on the live side, never saw it on the scratch side, and was refused - which
    /// vessel that hit depended on the instant the store ran. The roster sweep caught it
    /// refusing different vessels on identical back-to-back runs.</para>
    /// </summary>
    /// <returns>
    /// A mismatch flag, true meaning the store must abort, paired with how many serializable direct
    /// children the live grid holds: the sum of the per-prototype tally above, which skips
    /// <c>save: false</c> prototypes and does not descend into containers or grandchildren. It is
    /// what the store's timing line prints as its entity count. Zero on the two reload-failure
    /// paths, since neither reaches the tally.
    /// </returns>
    private async Task<(bool Mismatch, int LiveEntities)> DetectRoundTripMismatch(
        DrydockStoreContext ctx, IDrydockSlice slice, string yaml)
    {
        var gridUid = ctx.GridUid;

        // The parse alone, off-thread: no entity is touched until the data node comes back.
        var data = await slice.Await(Task.Run(() => ParseDocument(yaml)));
        GuardStoreResume(ctx);
        ctx.Timer.Mark("validate_parse");

        if (data == null
            || TryLoadOntoNewPausedMap(data, "drydock/validation", initializeMap: false) is not { } load)
        {
            Log.Warning($"Drydock store validation failed for {ToPrettyString(gridUid)}: the document just written would not reload.");
            return (true, 0);
        }

        var (mapUid, scratchGrid) = load;

        TagStagingMap(mapUid, JobIdOf(slice), DrydockStagingKind.Validation, ctx.ShipId);

        try
        {
            var live = CountChildPrototypes(gridUid);
            var scratch = CountChildPrototypes(scratchGrid);

            var liveCount = live.Values.Sum();
            var scratchCount = scratch.Values.Sum();
            if (liveCount != scratchCount)
            {
                Log.Warning($"Drydock store validation failed for {ToPrettyString(gridUid)}: entity count mismatch (live={liveCount}, scratch={scratchCount}).");
                return (true, liveCount);
            }

            if (!PrototypeCountsMatch(live, scratch, out var detail))
            {
                Log.Warning($"Drydock store validation failed for {ToPrettyString(gridUid)}: composition mismatch ({detail}).");
                return (true, liveCount);
            }

            return (false, liveCount);
        }
        finally
        {
            Del(mapUid);
        }
    }

    /// <summary>
    /// Loads an already-parsed grid document onto a new map of its own, paused while it is still
    /// empty so the engine's recursive pause walks one entity instead of a whole hull.
    ///
    /// <para>Replicates the map-creating <c>TryLoadGrid</c> wrapper by hand, because that wrapper
    /// parses and builds in one call and there is no overload that takes an already-parsed document
    /// and still owns creating the target map. Untagged: each caller tags the map for its own
    /// pipeline once the load has held.</para>
    /// </summary>
    /// <param name="initializeMap">
    /// Whether the map, and so the load, is map-initialised. The validation scratch load is
    /// deliberately not; a retrieve is.
    /// </param>
    /// <returns>
    /// The map and its one grid, or null when the load failed or produced anything but exactly one
    /// grid. On null, everything the load created and the map itself are already deleted.
    /// </returns>
    private (EntityUid Map, EntityUid Grid)? TryLoadOntoNewPausedMap(MappingDataNode data, string source, bool initializeMap)
    {
        var mapUid = _maps.CreateMap(out var mapId, runMapInit: initializeMap);
        _maps.SetPaused(mapUid, true);

        var loadOptions = new MapLoadOptions
        {
            MergeMap = mapId,
            DeserializationOptions = new DeserializationOptions
            {
                InitializeMaps = initializeMap,
                PauseMaps = true,
            },
            ExpectedCategory = FileCategory.Grid,
        };

        var loaded = _mapLoader.TryLoadGeneric(data, source, out var result, loadOptions);

        if (loaded && result!.Grids.Count == 1)
            return (mapUid, result.Grids.Single().Owner);

        if (result != null)
        {
            foreach (var uid in result.Entities)
            {
                if (Exists(uid))
                    Del(uid);
            }
        }

        Del(mapUid);
        return null;
    }

    /// <summary>
    /// The parse half of the reload, split out so it can run off the main thread: pure text-to-node
    /// work with no entity access. No logging in here, since a Task.Run body runs off-thread and the
    /// caller is the one positioned to attribute a failure to a grid and a ship.
    /// </summary>
    /// <returns>The parsed document, or null if the stream held anything but exactly one.</returns>
    private static MappingDataNode? ParseDocument(string yaml)
    {
        using var reader = new StringReader(yaml);
        var documents = DataNodeParser.ParseYamlStream(reader).ToArray();
        return documents.Length == 1 ? (MappingDataNode) documents[0].Root : null;
    }

    private Dictionary<string, int> CountChildPrototypes(EntityUid gridUid)
    {
        var counts = new Dictionary<string, int>();
        var enumerator = Transform(gridUid).ChildEnumerator;
        while (enumerator.MoveNext(out var child))
        {
            var meta = MetaData(child);

            // The serializer's own gate, mirrored: EntitySerializer.IsSerializable refuses any
            // entity whose prototype declares save: false, so such a child is live but will never
            // be in the document. Counting it refuses the store for content the store was never
            // going to write. A prototype-less entity is serializable and stays counted.
            if (meta.EntityPrototype?.MapSavable == false)
                continue;

            var protoId = meta.EntityPrototype?.ID ?? "<no-prototype>";
            counts.TryGetValue(protoId, out var count);
            counts[protoId] = count + 1;
        }

        return counts;
    }

    private static bool PrototypeCountsMatch(Dictionary<string, int> live, Dictionary<string, int> scratch, out string detail)
    {
        foreach (var (proto, liveCount) in live)
        {
            if (!scratch.TryGetValue(proto, out var scratchCount) || scratchCount != liveCount)
            {
                detail = $"prototype '{proto}': live={liveCount}, scratch={(scratch.TryGetValue(proto, out var sc) ? sc : 0)}";
                return false;
            }
        }

        foreach (var (proto, scratchCount) in scratch)
        {
            if (!live.ContainsKey(proto))
            {
                detail = $"prototype '{proto}': live=0, scratch={scratchCount}";
                return false;
            }
        }

        detail = string.Empty;
        return true;
    }

    /// <summary>
    /// The forensic record of what was aboard, built from what the store already has in hand and
    /// filled in place into <paramref name="manifest"/>. Parents are recorded as indices into the
    /// entry list rather than as entity references, since entity ids do not survive a round trip and
    /// a manifest has to still mean something a year later.
    /// </summary>
    /// <remarks>
    /// The walk itself is materialised up front, in one un-yielded pass, and only the per-entity
    /// component reads are sliced. That is what keeps the parent indices honest: they are positions
    /// in a list that was fixed before the first suspension, so an entity that vanished mid-walk
    /// still occupies its slot and every child below it still points at the right parent. The order
    /// is the depth-first one the unsliced version produced, kept so two manifests of the same hull
    /// remain comparable across the change.
    /// </remarks>
    private async Task BuildManifestSliced(DrydockStoreContext ctx, IDrydockSlice slice, DrydockManifest manifest)
    {
        var capturedByEntity = ctx.Fidelity == null
            ? new Dictionary<EntityUid, List<string>>()
            : ctx.Fidelity.Snapshot
                .GroupBy(s => s.Uid)
                .ToDictionary(g => g.Key, g => g.Select(s => $"{s.Comp.GetType().Name}|{s.Member.Name}").ToList());

        var order = new List<(EntityUid Uid, int? Parent)>();
        var stack = new Stack<(EntityUid Uid, int? Parent)>();
        stack.Push((ctx.GridUid, null));

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            order.Add(node);
            var myIndex = order.Count - 1;

            var children = Transform(node.Uid).ChildEnumerator;
            while (children.MoveNext(out var child))
                stack.Push((child, myIndex));
        }

        await StoreSweep(ctx, slice, DrydockPhase.Manifest, order, node =>
        {
            var (uid, parent) = node;
            var entry = new DrydockManifestEntry { Parent = parent };

            // An entry is added for every position in the walk whether or not the entity survived to
            // be read, because the indices above are what the parent links mean.
            if (!TerminatingOrDeleted(uid))
            {
                entry.Proto = MetaData(uid).EntityPrototype?.ID ?? string.Empty;

                if (TryComp<DamageableComponent>(uid, out var damageable))
                    entry.Damage = (float) damageable.TotalDamage;

                if (TryComp<Content.Shared.Stacks.StackComponent>(uid, out var stackComp))
                    entry.Stack = stackComp.Count;

                if (capturedByEntity.TryGetValue(uid, out var keys))
                    entry.CapturedKeys = keys;
            }

            manifest.Entries.Add(entry);
        });
    }

    /// <summary>
    /// Files each pipe net's gas onto its member pipes as a per-node share, so a document that cannot
    /// carry the node-group graph still carries the air.
    /// </summary>
    /// <remarks>
    /// The read half is one un-yielded pass and cannot be anything else. The dictionary is keyed by
    /// live node-group object identity and the second pass reads a member's air off that same live
    /// group; node group updates are not gated by pause, so a group rebuilt between two slices would
    /// leave the second pass reading a dead one. Snapshotting every share first and slicing only the
    /// write-out keeps the whole graph read inside one tick, which is where it has to be.
    /// </remarks>
    private async Task InjectPipeGasSidecarsSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        // Nets are de-duplicated by node-group identity: a net has many member pipes and its gas
        // must be distributed exactly once. Members are (entity, node name, node), because a
        // two-port device sits in two nets and each node's share has to be filed under its own
        // name, or the last net written wins and the restore leaks it into the other.
        var nets = new Dictionary<object, List<(EntityUid Owner, string Name, PipeNode Pipe)>>();

        foreach (var uid in _fidelity.GridTreeList(ctx.GridUid))
        {
            if (!TryComp<NodeContainerComponent>(uid, out var nodeContainer))
                continue;

            foreach (var (name, node) in nodeContainer.Nodes)
            {
                if (node is not PipeNode { NodeGroup: { } group } pipe)
                    continue;

                if (!nets.TryGetValue(group, out var members))
                    nets[group] = members = new List<(EntityUid, string, PipeNode)>();

                members.Add((uid, name, pipe));
            }
        }

        var shares = new List<(EntityUid Owner, string Name, Content.Shared.Atmos.GasMixture Share)>();

        foreach (var members in nets.Values)
        {
            var totalVolume = 0f;
            foreach (var (_, _, pipe) in members)
                totalVolume += pipe.Volume;

            if (totalVolume <= 0f)
                continue;

            var netAir = members[0].Pipe.Air;

            foreach (var (owner, name, pipe) in members)
            {
                var share = new Content.Shared.Atmos.GasMixture(netAir) { Volume = pipe.Volume };
                share.Multiply(pipe.Volume / totalVolume);
                shares.Add((owner, name, share));
            }
        }

        await StoreSweep(ctx, slice, DrydockPhase.Sidecars, shares, item =>
        {
            var (owner, name, share) = item;
            if (TerminatingOrDeleted(owner))
                return;

            // One ledger entry per owner, and it goes in before the component does. Asking
            // whether the sidecar was already there beats counting the shares afterwards: an
            // owner with two nodes gets two shares and must still be removed exactly once, and an
            // abort between the two writes must still find it on the ledger.
            if (!HasComp<DrydockPipeGasComponent>(owner))
                ctx.InjectedGas.Add(owner);

            EnsureComp<DrydockPipeGasComponent>(owner).Shares[name] = share;
        });
    }

    private async Task InjectDamageSidecarsSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        var damaged = new List<(EntityUid Uid, Dictionary<string, FixedPoint2> Damage)>();

        foreach (var uid in _fidelity.GridTreeList(ctx.GridUid))
        {
            if (!TryComp<DamageableComponent>(uid, out var damageable) || damageable.TotalDamage <= FixedPoint2.Zero)
                continue;

            damaged.Add((uid, new Dictionary<string, FixedPoint2>(damageable.Damage.DamageDict)));
        }

        await StoreSweep(ctx, slice, DrydockPhase.Sidecars, damaged, item =>
        {
            var (uid, damage) = item;
            if (TerminatingOrDeleted(uid))
                return;

            if (!HasComp<DrydockDamageSidecarComponent>(uid))
                ctx.InjectedDamage.Add(uid);

            EnsureComp<DrydockDamageSidecarComponent>(uid).DamageDict = damage;
        });
    }

    /// <summary>
    /// Removes each listed component, keeping a deep copy rather than the live instance so an
    /// aborted store can put the field data back. A bare re-add of a fresh instance would come back
    /// empty.
    /// </summary>
    private Task StripListedComponentsSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        return StoreSweep(ctx, slice, DrydockPhase.Strip, StoreStripList, type =>
        {
            if (TerminatingOrDeleted(ctx.GridUid) || !TryComp(ctx.GridUid, type, out var comp))
                return;

            // The copy lands on the ledger before the live component is taken off, so an abort
            // between the two finds the component still on the grid and re-adds a duplicate of
            // it, which is a no-op, rather than finding it gone with no copy to put back.
            ctx.Stripped.Add(_serialization.CreateCopy(comp, notNullableOverride: true));
            RemComp(ctx.GridUid, comp);
        });
    }

    private void RestoreStrippedComponents(EntityUid gridUid, List<IComponent> stripped)
    {
        foreach (var comp in stripped)
        {
#pragma warning disable CS0618 // Owner is obsolete for external callers; this is the component-restore seam.
            comp.Owner = gridUid;
#pragma warning restore CS0618
            AddComp(gridUid, comp, true);
        }
    }

    /// <summary>
    /// Deletes every entity aboard that may not go away with the ship, containers and contents
    /// included. Two rules, both the ship-save path's. Anything marked as saving contraband goes
    /// unless it carries a permit (<c>IsInvalidEntity</c>), applied by component rather than by a
    /// list: the component is what the content marks. And any permitted item goes whose permit does
    /// not travel with whoever the ship is being put away for (<c>ClearPermitItemsOnGrid</c>, judged
    /// by <see cref="ContrabandPermitSystem.PermitTravelsWith"/>), whether or not it is marked, since
    /// a permit follows its person and is not the ship's to carry. Immediate deletes, not queued: the
    /// serializer walks the tree many ticks later now, and a queued deletion would be honoured well
    /// before then, but a merely queued entity is still a real grid child in the meantime and every
    /// walk between here and the save - the sidecars, the capture, the manifest - would count and
    /// touch it.
    ///
    /// <para>Deliberately absolute, with no exemption for anchored entities. Anchoring is a state a
    /// player can create with a wrench, so exempting it would let anyone bolt restricted kit to a
    /// deck and carry it between rounds. A hull fixture that must survive a store is one that should
    /// not have carried the contraband marker in the first place, which is where that gets fixed.</para>
    /// </summary>
    private async Task PurgeSavingContrabandSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        var doomed = new List<EntityUid>();

        // One walk for both rules. A permitted item is judged by its permit alone, whichever way
        // that goes; anything else that is marked goes.
        foreach (var uid in _fidelity.GridTreeList(ctx.GridUid))
        {
            if (TryComp<ContrabandPermitItemComponent>(uid, out var permit))
            {
                if (!_contrabandPermit.PermitTravelsWith((uid, permit), ctx.PermitHolderMind, ctx.OwnerUserId))
                    doomed.Add(uid);
            }
            else if (HasComp<SavingContrabandComponent>(uid))
            {
                doomed.Add(uid);
            }
        }

        var count = 0;
        await StoreSweep(ctx, slice, DrydockPhase.Purge, doomed, uid =>
        {
            // A container purged earlier in the list takes its contents with it.
            if (TerminatingOrDeleted(uid))
                return;

            Del(uid);
            count++;
        });

        if (count > 0)
            Log.Info($"Drydock: {ctx.ShipId} store purged {count} entities: saving contraband without a permit, or a permit that is not the holder's.");
    }

    /// <summary>
    /// Blanks the starting-map reference on every store aboard (a PDA's uplink store, mostly) and
    /// returns what was there, so an aborted store can put it back.
    /// </summary>
    private async Task DetachStoreMapsSliced(DrydockStoreContext ctx, IDrydockSlice slice)
    {
        var found = new List<(EntityUid Store, EntityUid? Map)>();
        foreach (var uid in _fidelity.GridTreeList(ctx.GridUid))
        {
            if (!TryComp<StoreComponent>(uid, out var store) || store.StartingMap == null)
                continue;

            found.Add((uid, store.StartingMap));
        }

        await StoreSweep(ctx, slice, DrydockPhase.Strip, found, item =>
        {
            var (uid, map) = item;

            // Ledger first, blank second, so an abort between them re-writes a value that is still
            // there rather than losing one that is already gone.
            if (TryComp<StoreComponent>(uid, out var store) && store.StartingMap != null)
            {
                ctx.StoreMaps.Add((uid, map));
                store.StartingMap = null;
            }
        });
    }

    private void ReattachStoreMaps(List<(EntityUid Store, EntityUid? Map)> detached)
    {
        foreach (var (uid, map) in detached)
        {
            if (TryComp<StoreComponent>(uid, out var store))
                store.StartingMap = map;
        }
    }

    /// <summary>
    /// The organics gate, run three times across the freeze pipeline because occupancy can change
    /// between database awaits. Every store first lifts any ghost off the hull; an impound then evicts
    /// and folds the count into <see cref="DrydockStoreContext.Evicted"/>; either path then refuses if
    /// anyone board-able is still found. Each call site's own comment says why that particular point
    /// still needs asking.
    /// </summary>
    private bool GateOrganics(
        DrydockStoreContext ctx,
        EntityUid gridUid,
        EntityQuery<MobStateComponent> mobQuery,
        EntityQuery<TransformComponent> xformQuery)
    {
        // Every store, not only an impound: a ghost never blocks, but one left aboard is deleted with
        // the grid. Asked at each gate for the same reason the organics are, since one can drift aboard.
        EvictGhostsAboard(ctx);

        if (ctx.Impound != null)
            ctx.Evicted += EvictOrganicsAboard(ctx);

        return _shipyard.FoundOrganics(gridUid, mobQuery, xformQuery) is not null;
    }

    /// <summary>
    /// Whether an armed nuke, an active countdown, a singularity, or an anomaly is aboard.
    /// </summary>
    private bool HasHazardAboard(EntityUid gridUid) => CollectHazards(gridUid, new List<EntityUid>(), new List<EntityUid>());

    /// <summary>
    /// Walks the four rare-component world queries that define a hazard aboard <paramref
    /// name="gridUid"/>: an armed nuke, an active countdown, a singularity, or an anomaly. Each is a
    /// world query filtered by grid rather than a tree walk, the trade
    /// <see cref="DrydockFidelitySystem.GridTreeList"/> warns against taken deliberately: these four
    /// components are rare, so each query visits a handful of entities where a walk would visit the
    /// whole hull, and its cost grows with how many of them the sector holds rather than with the
    /// round's entity count. The transform's grid resolves through container nesting, so a nuke
    /// stashed in a crate still reports the ship.
    ///
    /// <para>An anomaly counts for the same reason as a live countdown: its pulse and supercritical
    /// timers are ordinary data fields that resume on thaw, and it does not come back from a
    /// document the way it went in.</para>
    ///
    /// <para>Countdown carriers go to <paramref name="disarm"/>; the rest go to <paramref
    /// name="doomed"/>. Returns whether anything was found.</para>
    /// </summary>
    private bool CollectHazards(EntityUid gridUid, List<EntityUid> doomed, List<EntityUid> disarm)
    {
        var nukes = AllEntityQuery<NukeComponent, TransformComponent>();
        while (nukes.MoveNext(out var uid, out var nuke, out var xform))
        {
            if (xform.GridUid == gridUid && nuke.Status == NukeStatus.ARMED)
                doomed.Add(uid);
        }

        var timers = AllEntityQuery<ActiveTimerTriggerComponent, TransformComponent>();
        while (timers.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                disarm.Add(uid);
        }

        var singularities = AllEntityQuery<SingularityComponent, TransformComponent>();
        while (singularities.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                doomed.Add(uid);
        }

        var anomalies = AllEntityQuery<AnomalyComponent, TransformComponent>();
        while (anomalies.MoveNext(out var uid, out _, out var xform))
        {
            if (xform.GridUid == gridUid)
                doomed.Add(uid);
        }

        return doomed.Count > 0 || disarm.Count > 0;
    }

    /// <summary>
    /// The document's format version, and a hash over the sorted set of prototype ids it references.
    /// That id set is the drift key: <see cref="DetectDrift"/> reads it at retrieve and in the re-bake
    /// sweep, after the migration mappings.
    /// </summary>
    /// <remarks>
    /// <para>Streams the document rather than loading it into a node tree. The two facts wanted here
    /// live in the top-level <c>meta</c> mapping and in the <c>proto</c> key of each entry of the
    /// top-level <c>entities</c> sequence, and entities are grouped by prototype, so those keys number
    /// in the hundreds while the tree under them holds every instance, component and field on the
    /// ship. Building that tree to read the headers cost 237 ms of a 2.1 s store on a large hull,
    /// measured across 112 stores 2026-09-09, which was more than a tenth of the whole pipeline.</para>
    /// <para>The output is contractually identical to the node-tree read it replaces, because the
    /// fingerprint is persisted on every revision and compared across stores: a different value here
    /// would read as content drift on ships that had not changed.
    /// <c>DrydockDriftMetadataTest</c> holds that equality down against a tree-reading oracle.</para>
    /// </remarks>
    internal static (byte[] Fingerprint, int FormatVersion) ReadDriftMetadata(string yaml)
    {
        var (ids, formatVer) = ReadDriftIds(yaml);
        return (DriftFingerprint(ids), formatVer);
    }

    /// <summary>
    /// The fingerprint's input without the hash: the ordinal-sorted set of non-empty <c>proto</c>
    /// ids and the <c>meta.format</c> value, 0 where either is absent. The drift detector and the
    /// re-bake read this rather than the hash, because they need to know which ids moved.
    /// </summary>
    internal static (SortedSet<string> Ids, int FormatVersion) ReadDriftIds(string yaml)
    {
        var formatVer = 0;
        var protos = new SortedSet<string>(StringComparer.Ordinal);

        var parser = new Parser(new StringReader(yaml));
        parser.Consume<StreamStart>();
        parser.Consume<DocumentStart>();

        if (!parser.TryConsume<MappingStart>(out _))
            return (protos, formatVer);

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            var key = parser.Consume<Scalar>().Value;

            switch (key)
            {
                case "meta":
                    formatVer = ReadFormat(parser);
                    break;
                case "entities":
                    ReadProtoGroups(parser, protos);
                    break;
                default:
                    parser.SkipThisAndNestedEvents();
                    break;
            }
        }

        return (protos, formatVer);
    }

    /// <summary>
    /// SHA-256 over the ids joined with '\n'. The set must be ordinal-sorted, as
    /// <see cref="ReadDriftIds"/> returns it: the value is persisted and compared across stores.
    /// </summary>
    internal static byte[] DriftFingerprint(SortedSet<string> ids) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', ids)));

    /// <summary>Reads <c>format</c> out of the meta mapping, skipping everything else in it.</summary>
    private static int ReadFormat(IParser parser)
    {
        if (!parser.TryConsume<MappingStart>(out _))
        {
            // Not a mapping, so there is no format to find. The value still has to be consumed or
            // the caller reads this node's contents as its own keys.
            parser.SkipThisAndNestedEvents();
            return 0;
        }

        var formatVer = 0;

        while (!parser.TryConsume<MappingEnd>(out _))
        {
            if (parser.Consume<Scalar>().Value == "format" && parser.TryConsume<Scalar>(out var value))
                int.TryParse(value.Value, out formatVer);
            else
                parser.SkipThisAndNestedEvents();
        }

        return formatVer;
    }

    /// <summary>
    /// Collects the <c>proto</c> of each prototype group, skipping the instance list under it, which
    /// is where the document's bulk lives.
    /// </summary>
    private static void ReadProtoGroups(IParser parser, SortedSet<string> protos)
    {
        if (!parser.TryConsume<SequenceStart>(out _))
        {
            parser.SkipThisAndNestedEvents();
            return;
        }

        while (!parser.TryConsume<SequenceEnd>(out _))
        {
            // A non-mapping entry is skipped rather than refused, matching the node-tree read, which
            // filtered the sequence to mappings.
            if (!parser.TryConsume<MappingStart>(out _))
            {
                parser.SkipThisAndNestedEvents();
                continue;
            }

            while (!parser.TryConsume<MappingEnd>(out _))
            {
                if (parser.Consume<Scalar>().Value == "proto"
                    && parser.TryConsume<Scalar>(out var proto))
                {
                    if (proto.Value.Length > 0)
                        protos.Add(proto.Value);
                }
                else
                {
                    parser.SkipThisAndNestedEvents();
                }
            }
        }
    }

    /// <summary>The <c>drydock_blob.blob</c> codec. Tests that doctor a stored document use this pair, never a copy.</summary>
    internal static byte[] CompressZstd(byte[] input)
    {
        using var output = new MemoryStream();
        using (var compress = new ZStdCompressStream(output, ownStream: false))
        {
            compress.Write(input);
        }

        return output.ToArray();
    }
}
