using Content.Shared._Triad.Shipyard.Load;
using Robust.Shared.Map.Components;

namespace Content.Server._Triad.Shipyard.Load;

/// <summary>
/// Triad: replaces the entities aboard a returning ship that carry
/// <see cref="SpawnOnShipLoadComponent"/>, on both drydock retrieve and legacy import.
/// </summary>
/// <remarks>
/// Lifted out of the legacy ship-save system when that was retired: this was the only part of it
/// still wanted, and leaving it there would have tied AI-core respawn to the import path, which is
/// meant to be switched off once the legacy pool drains.
/// </remarks>
public sealed class ShipLoadRespawnSystem : EntitySystem
{
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private SharedTransformSystem _transform = default!;

    private readonly HashSet<Entity<SpawnOnShipLoadComponent>> _found = new();

    private EntityQuery<MapGridComponent> _gridQuery;
    private EntityQuery<TransformComponent> _transformQuery;

    public override void Initialize()
    {
        base.Initialize();

        _gridQuery = GetEntityQuery<MapGridComponent>();
        _transformQuery = GetEntityQuery<TransformComponent>();
    }

    /// <summary>
    /// Spawns the replacement for every marked entity on the grid, and deletes the original where
    /// the component asks for it.
    /// </summary>
    public void RespawnMarkedEntities(EntityUid gridUid)
    {
        if (!_gridQuery.HasComp(gridUid))
            return;

        _found.Clear();

        var gridTransform = _transformQuery.GetComponent(gridUid);
        var worldAABB = _lookup.GetWorldAABB(gridUid, gridTransform);
        _lookup.GetEntitiesIntersecting(gridTransform.MapID, worldAABB, _found);

        var toDelete = new HashSet<EntityUid>();

        foreach (var (ent, comp) in _found)
        {
            if (ent == gridUid)
                continue;

            var position = _transform.GetMoverCoordinates(ent);
            var newEntity = Spawn(comp.Spawn, position);
            _transform.AttachToGridOrMap(newEntity);

            if (comp.DeleteSelfAfterSpawn)
                toDelete.Add(ent);
        }

        foreach (var uid in toDelete)
        {
            QueueDel(uid);
        }
    }
}
