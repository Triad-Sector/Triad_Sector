using Content.Shared.Access.Components;
using Content.Shared.Administration.Logs;
using Content.Shared.Charges.Components;
using Content.Shared.Charges.Systems;
using Content.Shared.Construction;
using Content.Shared.Database;
using Content.Shared.DoAfter;
using Content.Shared.Examine;
using Content.Shared.Hands.Components;
using Content.Shared.Interaction;
using Content.Shared.Maps;
using Content.Shared.Physics;
using Content.Shared.Popups;
using Content.Shared.RCD.Components;
using Content.Shared._NF.Shipyard.Components; // Frontier
using Content.Shared.Tag;
using Content.Shared.Tiles;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Network;
using Robust.Shared.Physics;
using Robust.Shared.Physics.Collision.Shapes;
using Robust.Shared.Physics.Dynamics;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;
using Robust.Shared.Timing;
using System.Diagnostics.CodeAnalysis;
using Robust.Shared.Audio;

// Mono
using System.Numerics;

namespace Content.Shared.RCD.Systems;

/// <summary>
/// Shared RCD rules, do-after, and placement. Triad fork adds <see cref="GetConstructTileTypeId"/> (direction-mapped
/// tiles), <see cref="MapGridData"/> / off-grid hull targeting, and the duplicate-entity check aligned with
/// space-wizards#42556 (<see cref="RCDPrototype"/> <c>AllowMultiDirection</c>). See Resources/Prototypes/_Mono/RCD/README.md.
/// </summary>
[Virtual]
public class RCDSystem : EntitySystem
{
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly INetManager _net = default!;
    [Dependency] private readonly ISharedAdminLogManager _adminLogger = default!;
    [Dependency] private readonly ITileDefinitionManager _tileDefMan = default!;
    [Dependency] private readonly FloorTileSystem _floors = default!;
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly SharedChargesSystem _sharedCharges = default!;
    [Dependency] private readonly SharedDoAfterSystem _doAfter = default!;
    [Dependency] private readonly SharedInteractionSystem _interaction = default!;
    [Dependency] private readonly SharedPopupSystem _popup = default!;
    [Dependency] private readonly TurfSystem _turf = default!;
    [Dependency] private readonly EntityLookupSystem _lookup = default!;
    [Dependency] private readonly IPrototypeManager _protoManager = default!;
    [Dependency] private readonly SharedMapSystem _mapSystem = default!;
    [Dependency] private readonly SharedTransformSystem _transform = default!;
    [Dependency] private readonly TagSystem _tags = default!;

    private readonly int _instantConstructionDelay = 0;
    private readonly EntProtoId _instantConstructionFx = "EffectRCDConstruct0";
    private readonly ProtoId<RCDPrototype> _deconstructTileProto = "DeconstructTile";
    private readonly ProtoId<RCDPrototype> _deconstructLatticeProto = "DeconstructLattice";

    private HashSet<EntityUid> _intersectingEntities = new();

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RCDComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<RCDComponent, ExaminedEvent>(OnExamine);
        SubscribeLocalEvent<RCDComponent, AfterInteractEvent>(OnAfterInteract);
        SubscribeLocalEvent<RCDComponent, RCDDoAfterEvent>(OnDoAfter);
        SubscribeLocalEvent<RCDComponent, DoAfterAttemptEvent<RCDDoAfterEvent>>(OnDoAfterAttempt);
        SubscribeLocalEvent<RCDComponent, RCDSystemMessage>(OnRCDSystemMessage);
        SubscribeNetworkEvent<RCDConstructionGhostRotationEvent>(OnRCDconstructionGhostRotationEvent);
    }

    #region Event handling

    private void OnMapInit(EntityUid uid, RCDComponent component, MapInitEvent args)
    {
        // On init, set the RCD to the first available recipe that actually exists (same enumeration order as before
        // when the first id is valid). Skip missing ids so a bad entry cannot leave ProtoId invalid (Index would throw
        // on examine/use).
        foreach (var protoId in component.AvailablePrototypes)
        {
            if (!_protoManager.HasIndex(protoId))
                continue;

            component.ProtoId = protoId;
            Dirty(uid, component);
            return;
        }

        // No valid recipes (empty set or every id missing)? Remove the item.
        QueueDel(uid);
    }

    private void OnRCDSystemMessage(EntityUid uid, RCDComponent component, RCDSystemMessage args)
    {
        // Exit if the RCD doesn't actually know the supplied prototype
        if (!component.AvailablePrototypes.Contains(args.ProtoId))
            return;

        if (!_protoManager.HasIndex(args.ProtoId))
            return;

        // Set the current RCD prototype to the one supplied
        component.ProtoId = args.ProtoId;
        Dirty(uid, component);
    }

    private void OnExamine(EntityUid uid, RCDComponent component, ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var prototype = _protoManager.Index(component.ProtoId);

        var msg = Loc.GetString("rcd-component-examine-mode-details", ("mode", Loc.GetString(prototype.SetName)));

        if (prototype.Mode == RcdMode.ConstructTile || prototype.Mode == RcdMode.ConstructObject)
        {
            var name = Loc.GetString(prototype.SetName);

            if (prototype.Mode == RcdMode.ConstructTile)
            {
                var tileId = GetConstructTileTypeId(prototype, component.ConstructionDirection);
                if (_tileDefMan.TryGetDefinition(tileId, out var tileDef))
                    name = Loc.GetString(tileDef.Name);
            }
            else if (prototype.Prototype != null &&
                     _protoManager.TryIndex(prototype.Prototype, out var proto))
            {
                name = proto.Name;
            }

            msg = Loc.GetString("rcd-component-examine-build-details", ("name", name));
        }

        args.PushMarkup(msg);
    }

    private void OnAfterInteract(EntityUid uid, RCDComponent component, AfterInteractEvent args)
    {
        if (args.Handled || !args.CanReach)
            return;

        var user = args.User;
        var location = args.ClickLocation;
        var prototype = _protoManager.Index(component.ProtoId);

        // Initial validity checks
        if (!location.IsValid(EntityManager))
            return;

        var gridUid = _transform.GetGrid(location);

        if (!TryGetMapGridData(location, user, out var mapGridData))
        {
            _popup.PopupClient(Loc.GetString("rcd-component-no-valid-grid"), uid, user);
            return;
        }

        if (!IsRCDOperationStillValid(uid, component, mapGridData.Value, args.Target, args.User,
                tilePlacementDirection: component.ConstructionDirection))
            return;

        if (!_net.IsServer)
            return;

        // Get the starting cost, delay, and effect from the prototype
        var cost = prototype.Cost;
        var delay = prototype.Delay;
        var effectPrototype = prototype.Effect;

        #region: Operation modifiers

        // Deconstruction modifiers
        switch (prototype.Mode)
        {
            case RcdMode.Deconstruct:

                // Deconstructing an object
                if (args.Target != null)
                {
                    if (TryComp<RCDDeconstructableComponent>(args.Target, out var destructible))
                    {
                        cost = destructible.Cost;
                        delay = destructible.Delay;
                        effectPrototype = destructible.Effect;
                    }
                }

                // Deconstructing a tile
                else
                {
                    var deconstructedTile = _mapSystem.GetTileRef(mapGridData.Value.GridUid, mapGridData.Value.Component, mapGridData.Value.Location);
                    var protoName = !deconstructedTile.IsSpace() ? _deconstructTileProto : _deconstructLatticeProto;

                    if (_protoManager.TryIndex(protoName, out var deconProto))
                    {
                        cost = deconProto.Cost;
                        delay = deconProto.Delay;
                        effectPrototype = deconProto.Effect;
                    }
                }

                break;

            case RcdMode.ConstructTile:

                // If replacing a tile, make the construction instant
                var contructedTile = _mapSystem.GetTileRef(mapGridData.Value.GridUid, mapGridData.Value.Component, mapGridData.Value.Location);

                if (!contructedTile.Tile.IsEmpty)
                {
                    delay = _instantConstructionDelay;
                    effectPrototype = _instantConstructionFx;
                }

                break;
        }

        #endregion

        // Try to start the do after
        // <Mono>
        var gridData = mapGridData.Value;
        var effect = Spawn(effectPrototype, new EntityCoordinates(gridData.GridUid, Vector2.Zero));
        _transform.SetParent(effect, gridData.GridUid);
        _transform.SetLocalPositionNoLerp(effect, gridData.Position + new Vector2(0.5f, 0.5f));
        // </Mono>
        var ev = new RCDDoAfterEvent(
            GetNetCoordinates(mapGridData.Value.Location),
            GetNetEntity(mapGridData.Value.GridUid),
            component.ConstructionDirection,
            component.ProtoId,
            cost,
            EntityManager.GetNetEntity(effect));

        var doAfterArgs = new DoAfterArgs(EntityManager, user, delay*component.DelayMultiplier, ev, uid, target: args.Target, used: uid) // Mono - add delay multiplier.
        {
            BreakOnDamage = true,
            BreakOnHandChange = true,
            BreakOnMove = true,
            AttemptFrequency = AttemptFrequency.EveryTick,
            CancelDuplicate = false,
            BlockDuplicate = false,
            MultiplyDelay = false, // Goobstation
        };

        args.Handled = true;

        if (!_doAfter.TryStartDoAfter(doAfterArgs))
            QueueDel(effect);
    }

    private void OnDoAfterAttempt(EntityUid uid, RCDComponent component, DoAfterAttemptEvent<RCDDoAfterEvent> args)
    {
        if (args.Event?.DoAfter?.Args == null)
            return;

        // Exit if the RCD prototype has changed
        if (component.ProtoId != args.Event.StartingProtoId)
        {
            args.Cancel();
            return;
        }

        var gridUid = GetEntity(args.Event.TargetGridId);

        if (!TryComp<MapGridComponent>(gridUid, out var mapGrid))
        {
            args.Cancel();
            return;
        }

        var location = GetCoordinates(args.Event.Location);
        var tile = _mapSystem.GetTileRef(gridUid, mapGrid, location);
        var position = _mapSystem.TileIndicesFor(gridUid, mapGrid, location);
        var mapGridData = new MapGridData(gridUid, mapGrid, location, tile, position);

        if (!IsRCDOperationStillValid(uid, component, mapGridData, args.Event.Target, args.Event.User,
                tilePlacementDirection: args.Event.Direction))
            args.Cancel();
    }

    private void OnDoAfter(EntityUid uid, RCDComponent component, RCDDoAfterEvent args)
    {
        if (args.Cancelled && _net.IsServer)
            QueueDel(EntityManager.GetEntity(args.Effect));

        if (args.Handled || args.Cancelled || !_timing.IsFirstTimePredicted)
            return;

        args.Handled = true;

        var gridUid = GetEntity(args.TargetGridId);

        if (!TryComp<MapGridComponent>(gridUid, out var mapGrid))
            return;

        var location = GetCoordinates(args.Location);
        var tile = _mapSystem.GetTileRef(gridUid, mapGrid, location);
        var position = _mapSystem.TileIndicesFor(gridUid, mapGrid, location);
        var mapGridData = new MapGridData(gridUid, mapGrid, location, tile, position);

        // Ensure the RCD operation is still valid
        if (!IsRCDOperationStillValid(uid, component, mapGridData, args.Target, args.User,
                tilePlacementDirection: args.Direction))
            return;

        // Finalize the operation
        FinalizeRCDOperation(uid, component, mapGridData, args.Direction, args.Target, args.User);

        // Play audio and consume charges
        _audio.PlayPredicted(component.SuccessSound, uid, args.User);
        _sharedCharges.AddCharges(uid, -args.Cost);
    }

    private void OnRCDconstructionGhostRotationEvent(RCDConstructionGhostRotationEvent ev, EntitySessionEventArgs session)
    {
        var uid = GetEntity(ev.NetEntity);

        // Determine if player that send the message is carrying the specified RCD in their active hand
        if (session.SenderSession.AttachedEntity == null)
            return;

        if (!TryComp<HandsComponent>(session.SenderSession.AttachedEntity, out var hands) ||
            uid != hands.ActiveHand?.HeldEntity)
            return;

        if (!TryComp<RCDComponent>(uid, out var rcd))
            return;

        // Update the construction direction
        rcd.ConstructionDirection = ev.Direction;
        Dirty(uid, rcd);
    }

    #endregion

    #region Entity construction/deconstruction rule checks

    public bool IsRCDOperationStillValid(EntityUid uid, RCDComponent component, MapGridData mapGridData, EntityUid? target, EntityUid user, bool popMsgs = true,
        Direction? tilePlacementDirection = null)
    {
        var prototype = _protoManager.Index(component.ProtoId);
        var tileDir = tilePlacementDirection ?? component.ConstructionDirection;

        // Check that the RCD has enough ammo to get the job done
        var charges = _sharedCharges.GetCurrentCharges(uid);

        // Both of these were messages were suppose to be predicted, but HasInsufficientCharges wasn't being checked on the client for some reason?
        if (charges == 0)
        {
            if (popMsgs)
                _popup.PopupClient(Loc.GetString("rcd-component-no-ammo-message"), uid, user);

            return false;
        }

        if (prototype.Cost > charges)
        {
            if (popMsgs)
                _popup.PopupClient(Loc.GetString("rcd-component-insufficient-ammo-message"), uid, user);

            return false;
        }

        // Exit if the target / target location is obstructed
        var unobstructed = (target == null)
            ? _interaction.InRangeUnobstructed(user, _mapSystem.GridTileToWorld(mapGridData.GridUid, mapGridData.Component, mapGridData.Position), popup: popMsgs)
            : _interaction.InRangeUnobstructed(user, target.Value, popup: popMsgs);

        if (!unobstructed)
            return false;

        // Return whether the operation location is valid
        switch (prototype.Mode)
        {
            case RcdMode.ConstructTile: return IsConstructionLocationValid(uid, component, mapGridData, user, popMsgs, tileDir);
            case RcdMode.ConstructObject: return IsConstructionLocationValid(uid, component, mapGridData, user, popMsgs);
            case RcdMode.Deconstruct: return IsDeconstructionStillValid(uid, component, mapGridData, target, user, popMsgs);
        }

        return false;
    }

    private bool IsConstructionLocationValid(EntityUid uid, RCDComponent component, MapGridData mapGridData, EntityUid user, bool popMsgs = true,
        Direction? tilePlacementDirection = null)
    {
        var prototype = _protoManager.Index(component.ProtoId);

        // Check rule: Must build on empty tile
        if (prototype.ConstructionRules.Contains(RcdConstructionRule.MustBuildOnEmptyTile) && !mapGridData.Tile.Tile.IsEmpty)
        {
            if (popMsgs)
                _popup.PopupClient(Loc.GetString("rcd-component-must-build-on-empty-tile-message"), uid, user);

            return false;
        }

        // Check rule: Must build on non-empty tile
        if (!prototype.ConstructionRules.Contains(RcdConstructionRule.CanBuildOnEmptyTile) && mapGridData.Tile.Tile.IsEmpty)
        {
            if (popMsgs)
                _popup.PopupClient(Loc.GetString("rcd-component-cannot-build-on-empty-tile-message"), uid, user);

            return false;
        }

        // Check rule: Must place on subfloor
        if (prototype.ConstructionRules.Contains(RcdConstructionRule.MustBuildOnSubfloor) && !mapGridData.Tile.Tile.GetContentTileDefinition().IsSubFloor)
        {
            if (popMsgs)
                _popup.PopupClient(Loc.GetString("rcd-component-must-build-on-subfloor-message"), uid, user);

            return false;
        }

        // Tile specific rules
        if (prototype.Mode == RcdMode.ConstructTile)
        {
            // Check rule: Tile placement is valid
            if (!_floors.CanPlaceTile(mapGridData.GridUid, mapGridData.Component, null, out var reason))
            {
                if (popMsgs)
                    _popup.PopupClient(reason, uid, user);

                return false;
            }

            // Check rule: Tiles can't be identical
            var placeTileId = GetConstructTileTypeId(prototype, tilePlacementDirection ?? component.ConstructionDirection);
            if (mapGridData.Tile.Tile.GetContentTileDefinition().ID == placeTileId)
            {
                if (popMsgs)
                    _popup.PopupClient(Loc.GetString("rcd-component-cannot-build-identical-tile"), uid, user);

                return false;
            }

            // Ensure that all construction rules shared between tiles and object are checked before exiting here
            return true;
        }

        // Entity specific rules

        // Check rule: The tile is unoccupied
        var isWindow = prototype.ConstructionRules.Contains(RcdConstructionRule.IsWindow);
        var isCatwalk = prototype.ConstructionRules.Contains(RcdConstructionRule.IsCatwalk);

        _intersectingEntities.Clear();
        _lookup.GetLocalEntitiesIntersecting(mapGridData.GridUid, mapGridData.Position, _intersectingEntities, -0.05f, LookupFlags.Uncontained);

        foreach (var ent in _intersectingEntities)
        {
            // space-wizards/space-station-14#42556 — block spamming the same entity on one tile (e.g. lights);
            // AllowMultiDirection permits one per cardinal direction (directional windows, diagonals, etc.).
            if (prototype.Prototype != null && MetaData(ent).EntityPrototype?.ID == prototype.Prototype)
            {
                var isIdentical = true;
                if (prototype.AllowMultiDirection)
                {
                    var entDirection = Transform(ent).LocalRotation.GetCardinalDir();
                    if (entDirection != component.ConstructionDirection)
                        isIdentical = false;
                }

                if (isIdentical)
                {
                    if (popMsgs)
                        _popup.PopupClient(Loc.GetString("rcd-component-cannot-build-identical-entity"), uid, user);

                    return false;
                }
            }

            if (isWindow && HasComp<SharedCanBuildWindowOnTopComponent>(ent))
                continue;

            if (isCatwalk && _tags.HasTag(ent, "Catwalk"))
            {
                if (popMsgs)
                    _popup.PopupClient(Loc.GetString("rcd-component-cannot-build-on-occupied-tile-message"), uid, user);

                return false;
            }

            if (prototype.CollisionMask != CollisionGroup.None && TryComp<FixturesComponent>(ent, out var fixtures))
            {
                foreach (var fixture in fixtures.Fixtures.Values)
                {
                    // Continue if no collision is possible
                    if (!fixture.Hard || fixture.CollisionLayer <= 0 || (fixture.CollisionLayer & (int)prototype.CollisionMask) == 0)
                        continue;

                    // Continue if our custom collision bounds are not intersected
                    if (prototype.CollisionPolygon != null &&
                        !DoesCustomBoundsIntersectWithFixture(prototype.CollisionPolygon, component.ConstructionTransform, ent, fixture))
                        continue;

                    // Collision was detected
                    if (popMsgs)
                        _popup.PopupClient(Loc.GetString("rcd-component-cannot-build-on-occupied-tile-message"), uid, user);

                    return false;
                }
            }
        }

        return true;
    }

    private bool IsDeconstructionStillValid(EntityUid uid, RCDComponent component, MapGridData mapGridData, EntityUid? target, EntityUid user, bool popMsgs = true)
    {
        // Attempt to deconstruct a floor tile
        if (target == null)
        {
            // The tile is empty
            if (mapGridData.Tile.Tile.IsEmpty)
            {
                if (popMsgs)
                    _popup.PopupClient(Loc.GetString("rcd-component-nothing-to-deconstruct-message"), uid, user);

                return false;
            }

            // The tile has a structure sitting on it
            if (_turf.IsTileBlocked(mapGridData.Tile, CollisionGroup.MobMask))
            {
                if (popMsgs)
                    _popup.PopupClient(Loc.GetString("rcd-component-tile-obstructed-message"), uid, user);

                return false;
            }

            // The tile cannot be destroyed
            var tileDef = (ContentTileDefinition)_tileDefMan[mapGridData.Tile.Tile.TypeId];

            if (tileDef.Indestructible)
            {
                if (popMsgs)
                    _popup.PopupClient(Loc.GetString("rcd-component-tile-indestructible-message"), uid, user);

                return false;
            }
        }

        // Attempt to deconstruct an object
        else
        {
            // The object is not in the whitelist
            if (!TryComp<RCDDeconstructableComponent>(target, out var deconstructible) || !deconstructible.Deconstructable)
            {
                if (popMsgs)
                    _popup.PopupClient(Loc.GetString("rcd-component-deconstruct-target-not-on-whitelist-message"), uid, user);

                return false;
            }
        }

        return true;
    }

    #endregion

    #region Entity construction/deconstruction

    private void FinalizeRCDOperation(EntityUid uid, RCDComponent component, MapGridData mapGridData, Direction direction, EntityUid? target, EntityUid user)
    {
        if (!_net.IsServer)
            return;

        var prototype = _protoManager.Index(component.ProtoId);

        if (prototype.Prototype == null)
            return;

        switch (prototype.Mode)
        {
            case RcdMode.ConstructTile:
            {
                var tileTypeId = GetConstructTileTypeId(prototype, direction);
                if (string.IsNullOrEmpty(tileTypeId) || !_tileDefMan.TryGetDefinition(tileTypeId, out var tileDef))
                    return;

                _mapSystem.SetTile(mapGridData.GridUid, mapGridData.Component, mapGridData.Position, new Tile(tileDef.TileId));
                _adminLogger.Add(LogType.RCD, LogImpact.High, $"{ToPrettyString(user):user} used RCD to set grid: {mapGridData.GridUid} {mapGridData.Position} to {tileTypeId}");
                break;
            }

            case RcdMode.ConstructObject:
                var ent = Spawn(prototype.Prototype, _mapSystem.GridTileToLocal(mapGridData.GridUid, mapGridData.Component, mapGridData.Position));

                switch (prototype.Rotation)
                {
                    case RcdRotation.Fixed:
                        Transform(ent).LocalRotation = Angle.Zero;
                        break;
                    case RcdRotation.Camera:
                        Transform(ent).LocalRotation = Transform(uid).LocalRotation;
                        break;
                    case RcdRotation.User:
                        Transform(ent).LocalRotation = direction.ToAngle();
                        break;
                }

                _adminLogger.Add(LogType.RCD, LogImpact.High, $"{ToPrettyString(user):user} used RCD to spawn {ToPrettyString(ent)} at {mapGridData.Position} on grid {mapGridData.GridUid}");
                break;

            case RcdMode.Deconstruct:

                if (target == null)
                {
                    // Deconstruct tile (either converts the tile to lattice, or removes lattice)
                    var tile = (mapGridData.Tile.Tile.GetContentTileDefinition().ID != "Lattice") ? new Tile(_tileDefMan["Lattice"].TileId) : Tile.Empty;
                    _mapSystem.SetTile(mapGridData.GridUid, mapGridData.Component, mapGridData.Position, tile);
                    _adminLogger.Add(LogType.RCD, LogImpact.High, $"{ToPrettyString(user):user} used RCD to set grid: {mapGridData.GridUid} tile: {mapGridData.Position} open to space");
                }
                else
                {
                    // Deconstruct object
                    _adminLogger.Add(LogType.RCD, LogImpact.High, $"{ToPrettyString(user):user} used RCD to delete {ToPrettyString(target):target}");
                    QueueDel(target);
                }

                break;
        }
    }

    #endregion

    #region Utility functions

    public bool TryGetMapGridData(EntityCoordinates location, [NotNullWhen(true)] out MapGridData? mapGridData)
    {
        return TryGetMapGridData(location, null, out mapGridData);
    }

    /// <summary>
    /// Resolves which floor tile id an RCD <see cref="RcdMode.ConstructTile"/> recipe will place for the given direction.
    /// Fork: used with <see cref="RCDPrototype.ConstructTileByDirection"/>; keep in sync when merging upstream RCD tile
    /// validation (e.g. baseWhitelist / tile history from space-wizards#42556 family).
    /// </summary>
    public string GetConstructTileTypeId(RCDPrototype prototype, Direction direction)
    {
        if (prototype.ConstructTileByDirection is { Count: > 0 } map &&
            map.TryGetValue(direction, out var mapped) &&
            !string.IsNullOrEmpty(mapped))
        {
            return mapped;
        }

        return prototype.Prototype ?? string.Empty;
    }

    /// <summary>
    /// Resolves grid and tile for an RCD click. If <paramref name="user"/> is set and the click is not on a grid
    /// (e.g. open space off the edge of a shuttle), falls back to the grid the user is standing on so hull plating
    /// can target vacuum tiles from a valid grid.
    /// </summary>
    public bool TryGetMapGridData(EntityCoordinates location, EntityUid? user, [NotNullWhen(true)] out MapGridData? mapGridData)
    {
        mapGridData = null;
        var resolvedLocation = location;
        var gridUid = _transform.GetGrid(resolvedLocation);

        if (!TryComp<MapGridComponent>(gridUid, out var mapGrid))
        {
            resolvedLocation = location.AlignWithClosestGridTile(1.75f, EntityManager);
            gridUid = _transform.GetGrid(resolvedLocation);

            if (!TryComp(gridUid, out mapGrid))
            {
                if (user == null)
                    return false;

                gridUid = _transform.GetGrid(user.Value);
                if (!TryComp(gridUid, out mapGrid))
                    return false;

                resolvedLocation = location;
            }
        }

        var tile = _mapSystem.GetTileRef(gridUid.Value, mapGrid, resolvedLocation);
        var position = _mapSystem.TileIndicesFor(gridUid.Value, mapGrid, resolvedLocation);
        mapGridData = new MapGridData(gridUid.Value, mapGrid, resolvedLocation, tile, position);

        return true;
    }

    private bool DoesCustomBoundsIntersectWithFixture(PolygonShape boundingPolygon, Transform boundingTransform, EntityUid fixtureOwner, Fixture fixture)
    {
        var entXformComp = Transform(fixtureOwner);
        var entXform = new Transform(new(), entXformComp.LocalRotation);

        return boundingPolygon.ComputeAABB(boundingTransform, 0).Intersects(fixture.Shape.ComputeAABB(entXform, 0));
    }

    #endregion
}

public struct MapGridData
{
    public EntityUid GridUid;
    public MapGridComponent Component;
    public EntityCoordinates Location;
    public TileRef Tile;
    public Vector2i Position;

    public MapGridData(EntityUid gridUid, MapGridComponent component, EntityCoordinates location, TileRef tile, Vector2i position)
    {
        GridUid = gridUid;
        Component = component;
        Location = location;
        Tile = tile;
        Position = position;
    }
}

[Serializable, NetSerializable]
public sealed partial class RCDDoAfterEvent : DoAfterEvent
{
    [DataField(required: true)]
    public NetCoordinates Location { get; private set; } = default!;

    [DataField(required: true)]
    public NetEntity TargetGridId { get; private set; } = default!;

    [DataField]
    public Direction Direction { get; private set; } = default!;

    [DataField]
    public ProtoId<RCDPrototype> StartingProtoId { get; private set; } = default!;

    [DataField]
    public int Cost { get; private set; } = 1;

    [DataField("fx")]
    public NetEntity? Effect { get; private set; } = null;

    private RCDDoAfterEvent() { }

    public RCDDoAfterEvent(
        NetCoordinates location,
        NetEntity targetGridId,
        Direction direction,
        ProtoId<RCDPrototype> startingProtoId,
        int cost,
        NetEntity? effect = null)
    {
        Location = location;
        TargetGridId = targetGridId;
        Direction = direction;
        StartingProtoId = startingProtoId;
        Cost = cost;
        Effect = effect;
    }

    public override DoAfterEvent Clone() => this;
}
