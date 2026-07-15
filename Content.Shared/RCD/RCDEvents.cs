using Content.Shared.RCD.Systems;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared.RCD;

[Serializable, NetSerializable]
public sealed class RCDSystemMessage : BoundUserInterfaceMessage
{
    public ProtoId<RCDPrototype> ProtoId;

    public RCDSystemMessage(ProtoId<RCDPrototype> protoId)
    {
        ProtoId = protoId;
    }
}

[Serializable, NetSerializable]
public sealed class RCDConstructionGhostRotationEvent : EntityEventArgs
{
    public readonly NetEntity NetEntity;
    public readonly Direction Direction;

    public RCDConstructionGhostRotationEvent(NetEntity netEntity, Direction direction)
    {
        NetEntity = netEntity;
        Direction = direction;
    }
}

[Serializable, NetSerializable]
public enum RcdUiKey : byte
{
    Key
}

// Triad: mirror-prototype flip toggle (R key). Generic RCD feature — recipes can define a MirrorPrototype
// for asymmetric variants (gas filter/mixer, possibly future asymmetric airlocks).
/// <summary>
/// Sent from the client when the operator presses the flip key (default R) while holding an RCD on a recipe with
/// a defined <c>MirrorPrototype</c>. The server toggles <c>RCDComponent.UseMirrorPrototype</c> so the next
/// ConstructObject spawns the flipped variant.
/// </summary>
[Serializable, NetSerializable]
public sealed class RCDConstructionGhostFlipEvent : EntityEventArgs
{
    public readonly NetEntity NetEntity;
    public readonly bool UseMirrorPrototype;

    public RCDConstructionGhostFlipEvent(NetEntity netEntity, bool useMirrorPrototype)
    {
        NetEntity = netEntity;
        UseMirrorPrototype = useMirrorPrototype;
    }
}
// End Triad

// Triad: extensibility hooks for sibling systems (e.g. RPDSystem) to participate in RCD deconstruct validation
// and construct-spawn without RCDSystem knowing about them. By-ref so handlers can short-circuit (decon attempt)
// or rewrite the spawn proto (e.g. swap to an AtmosPipeLayer alternative).
/// <summary>
/// Raised before <c>RCDSystem</c> applies its own deconstruct validation. Setting <c>Cancelled = true</c> blocks
/// the operation; the handler is responsible for any user-facing popup. <c>Target</c> is null when the operator is
/// attempting to deconstruct a tile (rather than a structure).
/// </summary>
[ByRefEvent]
public struct RCDDeconstructAttemptEvent
{
    public readonly EntityUid? Target;
    public readonly EntityUid User;
    public readonly bool ShowPopups;
    public bool Cancelled;
    /// <summary>
    /// Set by a sibling handler (RPD) to admit a target RCD would otherwise reject (e.g. an <c>rpd: true</c>,
    /// <c>deconstructable: false</c> pipe), so RCDSystem doesn't need to know what an RPD is.
    /// </summary>
    public bool Admitted;

    public RCDDeconstructAttemptEvent(EntityUid? target, EntityUid user, bool showPopups)
    {
        Target = target;
        User = user;
        ShowPopups = showPopups;
        Cancelled = false;
        Admitted = false;
    }
}

/// <summary>
/// Raised by <c>RCDSystem</c> immediately before spawning a <c>ConstructObject</c> recipe. Handlers may mutate
/// <c>SpawnProto</c> to redirect to an alternate entity (e.g. layer-specific pipe variant). The starting value is
/// already adjusted for mirror-flip; layer-aware handlers should respect that.
/// </summary>
[ByRefEvent]
public struct RCDObjectSpawnAttemptEvent
{
    public readonly RCDPrototype Recipe;
    public string? SpawnProto;

    public RCDObjectSpawnAttemptEvent(RCDPrototype recipe, string? spawnProto)
    {
        Recipe = recipe;
        SpawnProto = spawnProto;
    }
}

/// <summary>
/// Raised after a <c>ConstructObject</c> spawn completes. Lets sibling systems decorate the spawned entity (e.g.
/// apply pipe color stain).
/// </summary>
[ByRefEvent]
public struct RCDObjectSpawnedEvent
{
    public readonly EntityUid Spawned;
    public readonly RCDPrototype Recipe;

    public RCDObjectSpawnedEvent(EntityUid spawned, RCDPrototype recipe)
    {
        Spawned = spawned;
        Recipe = recipe;
    }
}

/// <summary>
/// Raised in RCD Deconstruct mode when the direct click didn't land on an RPD-deconstructable target — typically
/// the pipe is hidden under a floor tile (<c>SubFloorHideComponent</c>), so the click resolved to bare tile or a
/// tile-sharing structure. A handler (<c>RPDSystem</c>) may set <c>Target</c> to an RPD-deconstructable entity
/// anchored on the tile, choosing the one on the operator's aimed pipe layer. Left null = no override, the original
/// click target stands. No handler (plain RCD) leaves <c>Target</c> untouched. <c>MapGridData</c> is the clicked
/// tile, for searching its anchored set.
/// </summary>
[ByRefEvent]
public struct RCDDeconstructTargetResolveEvent
{
    public readonly MapGridData MapGridData;
    public EntityUid? Target;

    public RCDDeconstructTargetResolveEvent(MapGridData mapGridData, EntityUid? target)
    {
        MapGridData = mapGridData;
        Target = target;
    }
}

/// <summary>
/// Raised during <c>ConstructObject</c> validation so a sibling system can veto the placement with logic RCD
/// does not own (e.g. RPD pipe-layer conflict). Setting <c>Cancelled = true</c> blocks the placement; the handler
/// owns any user-facing popup. Plain RCD has no handler, so the placement proceeds untouched.
/// </summary>
[ByRefEvent]
public struct RCDConstructionAttemptEvent
{
    public readonly MapGridData MapGridData;
    public readonly RCDPrototype Recipe;
    public readonly Direction Direction;
    public readonly EntityUid User;
    public readonly bool ShowPopups;
    public bool Cancelled;

    public RCDConstructionAttemptEvent(MapGridData mapGridData, RCDPrototype recipe, Direction direction, EntityUid user, bool showPopups)
    {
        MapGridData = mapGridData;
        Recipe = recipe;
        Direction = direction;
        User = user;
        ShowPopups = showPopups;
        Cancelled = false;
    }
}
// End Triad
