#nullable enable
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Content.Shared.Atmos;
using Content.Shared.Atmos.Components;
using Content.Shared.Construction.Conditions;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Coordinates;
using Content.Shared.Physics;
using Content.Shared.RCD;
using Content.Shared.RCD.Components;
using Content.Shared.RCD.Systems;
using Content.Shared.RPD;
using Content.Shared.RPD.Components;
using Content.Shared.RPD.Systems;
using Content.Shared.Tag;
using Content.Shared.Wall;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Map.Components;
using Robust.Shared.Maths;
using Robust.Shared.Prototypes;

namespace Content.IntegrationTests.Tests.RPD;

/// <summary>
/// Holds the RPD to the hand-placement contract. Each test is named for the rule it enforces; the rules live on the
/// wiki page "Pipe Placement Rules" (P1-P17) and the gaps they closed on "RPD Placement Parity Design".
/// </summary>
[TestFixture]
public sealed class RPDPlacementRulesTest
{
    private const string RpdProto = "RPD";

    private static IEnumerable<RCDPrototype> RpdRecipes(IPrototypeManager protoMan, IComponentFactory factory)
    {
        var rpd = protoMan.Index<EntityPrototype>(RpdProto);
        Assert.That(rpd.TryComp<RCDComponent>(out var rcd, factory), Is.True, "RPD prototype has no RCD component");
        foreach (var id in rcd!.AvailablePrototypes)
            yield return protoMan.Index(id);
    }

    private static readonly ProtoId<RCDPrototype> StraightRecipe = "PipeStraight";


    /// <summary>
    /// P9 and P10: the construction menu is the canon. Every RPD recipe names its hand twin in constructionRecipe
    /// and the twin resolves, so RCDSystem can hold the placement to the twin's conditions and canBuildInImpassable.
    /// A recipe with no twin fails rather than being skipped: without one there is nothing to hold parity against.
    /// </summary>
    [Test]
    public async Task P9_P10_EveryRecipeNamesItsHandTwin()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var recipe in RpdRecipes(protoMan, factory))
                {
                    if (recipe.Mode != RcdMode.ConstructObject)
                        continue;

                    Assert.That(recipe.ConstructionRecipe, Is.Not.Null, $"{recipe.ID}: no constructionRecipe, nothing to hold parity against");
                    if (recipe.ConstructionRecipe is not { } twinId)
                        continue;

                    Assert.That(protoMan.HasIndex(twinId), Is.True, $"{recipe.ID}: constructionRecipe {twinId} does not exist");
                    Assert.That(recipe.CollisionMask, Is.EqualTo(CollisionGroup.None),
                        $"{recipe.ID}: the wall rule comes from the twin's canBuildInImpassable, not a hand-written collisionMask");
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// P10: the manifold is Unstackable-tagged (the wrench refuses to stack it) but its menu recipe carries no
    /// NoUnstackableInTile, and the menu is the canon, so the RPD places a manifold on a tile that holds a vent.
    /// </summary>
    [Test]
    public async Task P10_ManifoldStacksLikeTheMenu()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var rcd = entMan.System<RCDSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            var user = entMan.SpawnEntity(null, grid.Owner.ToCoordinates(0, 0));

            // An East-facing Secondary vent: the manifold spans all three layers North-South, so no pipe-node overlap.
            var vent = entMan.SpawnAttachedTo("GasVentPumpAlt1", grid.Owner.ToCoordinates(0, 0), rotation: Direction.East.ToAngle());
            Assert.That(entMan.GetComponent<TransformComponent>(vent).Anchored, Is.True, "fixture vent must anchor");

            var rpdTool = entMan.SpawnEntity(RpdProto, grid.Owner.ToCoordinates(0, 0));
            var rcdComp = entMan.GetComponent<RCDComponent>(rpdTool);
            Assert.That(rcd.TryGetMapGridData(grid.Owner.ToCoordinates(0, 0), user, out var data), Is.True);

            SelectRecipe(entMan, rpdTool, "ManifoldGas");
            Assert.That(rcd.IsConstructionLocationValid(rpdTool, rcdComp, data!.Value, user, popMsgs: false),
                Is.True, "the menu allows a manifold beside a vent, so the RPD must too");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// P10: the RPD refuses a second Unstackable device on a tile even when their pipe nodes do not overlap. The
    /// pump faces East so its Longitudinal-rotated nodes miss the vent's South port; only the tag check can say no.
    /// </summary>
    [Test]
    public async Task P10_RejectsSecondUnstackableOnTile()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var rcd = entMan.System<RCDSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            var user = entMan.SpawnEntity(null, grid.Owner.ToCoordinates(0, 0));

            // A South-facing Secondary vent occupies the tile.
            var vent = entMan.SpawnEntity("GasVentPumpAlt1", grid.Owner.ToCoordinates(0, 0));
            Assert.That(entMan.GetComponent<TransformComponent>(vent).Anchored, Is.True, "fixture vent must anchor");

            var rpdTool = entMan.SpawnEntity(RpdProto, grid.Owner.ToCoordinates(0, 0));
            var rcdComp = entMan.GetComponent<RCDComponent>(rpdTool);
            Assert.That(rcd.TryGetMapGridData(grid.Owner.ToCoordinates(0, 0), user, out var data), Is.True);

            // Select the pressure pump recipe and face it East (Primary layer): no shared direction, different layer.
            SelectRecipe(entMan, rpdTool, "PressurePump");

            Assert.That(rcd.IsConstructionLocationValid(rpdTool, rcdComp, data!.Value, user, popMsgs: false, tilePlacementDirection: Direction.East),
                Is.False, "a pump must not stack on a vent even with no pipe-node overlap");

            // Control: the same pump on an empty neighbouring tile is fine.
            mapSys.SetTile(grid, new Vector2i(1, 0), new Tile(1));
            Assert.That(rcd.TryGetMapGridData(grid.Owner.ToCoordinates(1, 0), user, out var freeData), Is.True);
            Assert.That(rcd.IsConstructionLocationValid(rpdTool, rcdComp, freeData!.Value, user, popMsgs: false, tilePlacementDirection: Direction.East),
                Is.True, "control: the pump must be placeable on a free tile");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// P11: the layer that ships is the one captured at commit, not the tool's live cursor layer.
    /// </summary>
    [Test]
    public async Task P11_CommittedLayerSurvivesCursorMove()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var rpd = entMan.System<RPDSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            var rpdTool = entMan.SpawnEntity(RpdProto, grid.Owner.ToCoordinates(0, 0));
            var rpdComp = entMan.GetComponent<RPDComponent>(rpdTool);
            var recipe = protoMan.Index(StraightRecipe);

            // Commit at Secondary.
            rpd.SetLayer((rpdTool, rpdComp), AtmosPipeLayer.Secondary);
            var commit = new RCDPlacementCommitEvent();
            entMan.EventBus.RaiseLocalEvent(rpdTool, ref commit);
            Assert.That(commit.Layer, Is.EqualTo(AtmosPipeLayer.Secondary));

            // Cursor moves to Tertiary before the do-after completes.
            rpd.SetLayer((rpdTool, rpdComp), AtmosPipeLayer.Tertiary);

            // The spawn resolves against the committed layer.
            var spawn = new RCDObjectSpawnAttemptEvent(recipe, recipe.Prototype, commit.Layer);
            entMan.EventBus.RaiseLocalEvent(rpdTool, ref spawn);
            Assert.That(spawn.SpawnProto, Is.EqualTo("GasPipeStraightAlt1"), "spawn must use the layer captured at commit");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// P6: the RPD's layer pick is the hand placement mode's math. Deadzone is Primary; outside it NE/E is Secondary
    /// and SW/W is Tertiary, on a screen-relative axis.
    /// </summary>
    [Test]
    public void P6_LayerMathMatchesHandPlacement()
    {
        Assert.Multiple(() =>
        {
            // Inside the 0.25 tile deadzone, any direction.
            Assert.That(RPDLayerMath.PickLayer(new Vector2(0.2f, 0f), Angle.Zero, Angle.Zero), Is.EqualTo(AtmosPipeLayer.Primary));
            Assert.That(RPDLayerMath.PickLayer(new Vector2(-0.1f, -0.2f), Angle.Zero, Angle.Zero), Is.EqualTo(AtmosPipeLayer.Primary));

            // Outside it, unrotated: East and North of centre are Secondary, West and South are Tertiary.
            Assert.That(RPDLayerMath.PickLayer(new Vector2(0.4f, 0f), Angle.Zero, Angle.Zero), Is.EqualTo(AtmosPipeLayer.Secondary));
            Assert.That(RPDLayerMath.PickLayer(new Vector2(0f, 0.4f), Angle.Zero, Angle.Zero), Is.EqualTo(AtmosPipeLayer.Secondary));
            Assert.That(RPDLayerMath.PickLayer(new Vector2(-0.4f, 0f), Angle.Zero, Angle.Zero), Is.EqualTo(AtmosPipeLayer.Tertiary));
            Assert.That(RPDLayerMath.PickLayer(new Vector2(0f, -0.4f), Angle.Zero, Angle.Zero), Is.EqualTo(AtmosPipeLayer.Tertiary));

            // A half-turn of the eye flips the axis: what was Secondary reads Tertiary.
            Assert.That(RPDLayerMath.PickLayer(new Vector2(0.4f, 0f), Angle.FromDegrees(180), Angle.Zero), Is.EqualTo(AtmosPipeLayer.Tertiary));
            // Grid rotation is added the same way as eye rotation.
            Assert.That(RPDLayerMath.PickLayer(new Vector2(0.4f, 0f), Angle.Zero, Angle.FromDegrees(180)), Is.EqualTo(AtmosPipeLayer.Tertiary));
        });
    }

    /// <summary>
    /// P4: an empty (space) tile cannot be anchored to, and the RPD refuses before spawning rather than dropping a
    /// loose pipe the way the hand path does.
    /// </summary>
    [Test]
    public async Task P4_RefusesEmptyTile()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.ResolveDependency<IEntityManager>();
        var mapSys = entMan.System<SharedMapSystem>();
        var rcd = entMan.System<RCDSystem>();

        await server.WaitAssertion(() =>
        {
            mapSys.CreateMap(out var mapId);
            var grid = mapSys.CreateGridEntity(mapId);
            mapSys.SetTile(grid, new Vector2i(0, 0), new Tile(1));
            var user = entMan.SpawnEntity(null, grid.Owner.ToCoordinates(0, 0));
            var rpdTool = entMan.SpawnEntity(RpdProto, grid.Owner.ToCoordinates(0, 0));
            var rcdComp = entMan.GetComponent<RCDComponent>(rpdTool);

            // (1, 0) is part of the grid's chunk but holds no tile.
            Assert.That(rcd.TryGetMapGridData(grid.Owner.ToCoordinates(1, 0), user, out var data), Is.True);
            Assert.That(data!.Value.Tile.Tile.IsEmpty, Is.True, "fixture: the target tile must be empty");
            Assert.That(rcd.IsConstructionLocationValid(rpdTool, rcdComp, data.Value, user, popMsgs: false), Is.False,
                "RPD must refuse an empty tile");
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// P16: a wall-mount on the RPD is held to the menu's WallmountCondition through its twin, so every recipe whose
    /// entity carries WallMount must name a twin that declares that condition.
    /// </summary>
    [Test]
    public async Task P16_WallMountTwinsCarryWallmountCondition()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var protoMan = server.ResolveDependency<IPrototypeManager>();
        var factory = server.ResolveDependency<IComponentFactory>();

        await server.WaitAssertion(() =>
        {
            Assert.Multiple(() =>
            {
                foreach (var recipe in RpdRecipes(protoMan, factory))
                {
                    if (recipe.Mode != RcdMode.ConstructObject || recipe.Prototype == null)
                        continue;

                    var proto = protoMan.Index<EntityPrototype>(recipe.Prototype);
                    if (!proto.TryComp<WallMountComponent>(out _, factory))
                        continue;

                    Assert.That(recipe.ConstructionRecipe.HasValue && protoMan.TryIndex(recipe.ConstructionRecipe.Value, out var twin)
                                && twin.Conditions.Any(c => c is WallmountCondition), Is.True,
                        $"{recipe.ID} spawns a wall-mount ({recipe.Prototype}) and its twin must declare WallmountCondition");
                }
            });
        });

        await pair.CleanReturnAsync();
    }

    /// <summary>
    /// RCDComponent is Access-restricted to RCDSystem/RPDSystem, so drive the selection through the BUI message the
    /// client would send.
    /// </summary>
    private static void SelectRecipe(IEntityManager entMan, EntityUid tool, string recipeId)
    {
        entMan.EventBus.RaiseLocalEvent(tool, new RCDSystemMessage(recipeId));
        Assert.That(entMan.GetComponent<RCDComponent>(tool).ProtoId.Id, Is.EqualTo(recipeId), "recipe selection did not take");
    }
}
