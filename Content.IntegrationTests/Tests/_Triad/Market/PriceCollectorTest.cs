using System.Collections.Generic;
using System.Linq;
using Content.Server.Cargo.Systems;
using Content.Server.Storage.EntitySystems;
using Content.Shared.Storage.Components;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests._Triad.Market;

/// <summary>
/// The collecting price path against real entities in a real container.
///
/// <para>The whole reason it exists is that the plain path folds a container's contents into one
/// number, so a crate of steel appraises as "one crate" and a pricing model learns nothing about
/// steel. Two things have to hold for the capture built on it to be trustworthy: it must report the
/// same total as the method the sale actually pays out from, and the tree it builds must have the
/// crate as a root with its contents hanging off it.</para>
///
/// <para>If the totals ever diverge, the money recorded stops matching the money paid, which is the
/// failure that would be hardest to notice from the data alone.</para>
/// </summary>
[TestOf(typeof(PricingSystem))]
public sealed class PriceCollectorTest
{
    private const string CrateProto = "CrateGenericSteel";
    private const string SheetProto = "SheetSteel10";

    [Test]
    public async Task CollectingTotalMatchesThePlainPathAndBuildsTheTree()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;

        var pricing = entMan.System<PricingSystem>();
        var storage = entMan.System<EntityStorageSystem>();

        var testMap = await pair.CreateTestMap();

        double plain = 0;
        double collecting = 0;
        var nodes = new List<PricedNode>();
        EntityUid crate = default;

        await server.WaitPost(() =>
        {
            crate = entMan.SpawnAtPosition(CrateProto, testMap.GridCoords);

            // Two stacks inside, so the crate is worth strictly more than itself and a collapse to
            // one line would be visible in the totals.
            for (var i = 0; i < 2; i++)
            {
                var sheets = entMan.SpawnAtPosition(SheetProto, testMap.GridCoords);
                storage.Insert(sheets, crate);
            }
        });

        await server.WaitRunTicks(1);

        await server.WaitPost(() =>
        {
            var grid = testMap.Grid.Owner;
            plain = pricing.GetPriceWithVendingDiscount(crate, grid);
            collecting = pricing.GetPriceWithVendingDiscountCollecting(crate, grid, nodes);
        });

        Assert.Multiple(() =>
        {
            Assert.That(collecting, Is.EqualTo(plain),
                "the collector must not change what the sale pays out");

            Assert.That(nodes, Has.Count.GreaterThanOrEqualTo(3),
                "the crate plus at least the two stacks put inside it");

            Assert.That(nodes[0].Uid, Is.EqualTo(crate), "the seed entity is the first node");
            Assert.That(nodes[0].ParentIndex, Is.Null, "and it is the root");

            Assert.That(nodes.Skip(1).All(n => n.ParentIndex != null), Is.True,
                "everything the traversal reached is contained by something");

            // Own prices sum to the appraisal. This is the property the line tree relies on: roots
            // carry the payout, and leaves are a breakdown of it rather than extra money.
            Assert.That(nodes.Sum(n => n.OwnPrice), Is.EqualTo(collecting).Within(0.001),
                "own prices across the tree reconstruct the total exactly once");

            Assert.That(nodes[0].OwnPrice, Is.LessThan(collecting),
                "the crate alone is worth less than the crate with its contents");
        });

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task LooseItemIsASingleRootNode()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;

        var pricing = entMan.System<PricingSystem>();
        var testMap = await pair.CreateTestMap();

        var nodes = new List<PricedNode>();
        double total = 0;

        await server.WaitPost(() =>
        {
            var sheets = entMan.SpawnAtPosition(SheetProto, testMap.GridCoords);
            total = pricing.GetPriceWithVendingDiscountCollecting(sheets, testMap.Grid.Owner, nodes);
        });

        Assert.Multiple(() =>
        {
            // Not one node. A steel sheet carries a SolutionContainerManager, and solutions are
            // entities held in containers, so the traversal reaches the sheet's own steel solution.
            // Both price paths recurse identically, so the totals still agree; what it means is that
            // the sale capture has to drop worthless nodes or the corpus fills with solutions.
            Assert.That(nodes, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(nodes[0].ParentIndex, Is.Null, "the seed entity is the root");
            Assert.That(nodes.Skip(1).All(n => n.OwnPrice == 0), Is.True,
                "everything below a loose sheet is internal and worth nothing");
            Assert.That(nodes.Sum(n => n.OwnPrice), Is.EqualTo(total).Within(0.001));
            Assert.That(total, Is.GreaterThan(0), "steel is worth something, or the test proves nothing");
        });

        await pair.CleanReturnAsync();
    }
}
