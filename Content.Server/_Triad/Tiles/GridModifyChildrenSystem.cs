using System.Linq;
using Content.Shared._Triad.Tiles;
using Content.Shared.Whitelist;
using Robust.Shared.Map.Components;

namespace Content.Server._Triad.Tiles;

public sealed partial class GridGodModeSystem : EntitySystem
{
    [Dependency] private IEntityManager _entityManager = default!;
    [Dependency] private EntityLookupSystem _lookup = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;

    [SubscribeLocalEvent]
    private void OnGridModifyChildrenMapInit(Entity<GridModifyChildrenComponent> ent, ref MapInitEvent args)
    {
        var grid = ent.Owner;

        // Verify this is applied to a grid
        if (!HasComp<MapGridComponent>(grid))
        {
            Log.Warning($"GridGodModeComponent applied to non-grid entity {ToPrettyString(grid)}");
            return;
        }

        var allEntitiesOnGrid = _lookup.GetEntitiesIntersecting(grid).ToHashSet();

        foreach (var entity in allEntitiesOnGrid)
        {
            // Skip the grid itself
            if (entity == grid)
                continue;

            ProcessEntityOnGrid(entity, ent.Comp);
        }
    }

    private void ProcessEntityOnGrid(EntityUid entityUid, GridModifyChildrenComponent component)
    {
        if (_whitelist.IsWhitelistPass(component.ModifyWhitelist, entityUid))
            ApplyEffects(entityUid, component);
    }

    private void ApplyEffects(EntityUid entityUid, GridModifyChildrenComponent component)
    {
        if (component.AddComponents != null)
            _entityManager.AddComponents(entityUid, component.AddComponents);
        if (component.RemoveComponents != null)
            _entityManager.RemoveComponents(entityUid, component.RemoveComponents);
    }
}
