using System.Collections.Generic;
using System.Linq;
using Content.Server._Triad.Market;
using Content.Server.Cargo.Components;
using Content.Server.Cargo.Systems;
using Content.Server.Database;
using Content.Shared.Cargo.Events;
using Content.Shared.Cargo;
using Content.Shared.Cargo.BUI;
using Content.Shared._Triad.CCVar;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.Containers;
using Robust.Shared.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Maths;

namespace Content.IntegrationTests.Tests._Triad.Market;

/// <summary>
/// Drives a real pallet sale end to end and reads the captured transaction back out of the database.
///
/// <para>Everything else in this folder tests a piece. This tests the wiring: that
/// <c>OnPalletSale</c> actually builds a record, that the taxes attach as splits rather than
/// standing as their own rows, that container contents reach the line tree, and that the queue
/// flushes it. Those were compiler-verified only, which for the largest payout in the game is not
/// enough.</para>
///
/// <para>The pad carries two containers on purpose: a crate holding one material, and an ore box
/// holding several. A capture that collapses containers would still produce a plausible-looking
/// total, so the test asserts on what the tree contains rather than on the money alone.</para>
/// </summary>
[TestOf(typeof(CargoSystem))]
public sealed class PalletSaleCaptureTest
{
    private const string PalletProto = "CargoPalletSell";
    private const string ConsoleProto = "ComputerPalletConsole";
    // A real entity-storage crate, and an ore box. The first draft used CrateGenericSteel, which
    // has no storage at all, and put sheets in an OreBox, which whitelists the Ore tag; both
    // insertions failed silently and the goods sat loose on the pad.
    private const string CrateProto = "CrateGeneric";
    private const string SiloProto = "OreBox";
    private const string CrateContent = "SheetSteel10";

    private static readonly string[] SiloContents =
    [
        "GoldOre1",
        "SilverOre1",
        "SteelOre1",
    ];

    private static void AnchorIfLoose(IEntityManager entMan, SharedTransformSystem xformSys, EntityUid uid)
    {
        if (!entMan.GetComponent<TransformComponent>(uid).Anchored)
            xformSys.AnchorEntity(uid);
    }

    [Test]
    public async Task PalletSaleCapturesItsTreeSplitsAndPayout()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;
        var entMan = server.EntMan;

        await pair.Server.WaitPost(() =>
            server.CfgMan.SetCVar(TriadCCVars.MarketDataEnabled, true));

        var market = server.ResolveDependency<IMarketDataManager>();
        var db = server.ResolveDependency<IServerDbManager>();
        var containers = entMan.System<SharedContainerSystem>();
        var xformSys = entMan.System<SharedTransformSystem>();

        // The pooled server's database outlives any one test, and MarketDataStoreTest writes a
        // PalletSale row of its own; whichever of the two lands second on a reused server sees both.
        // Every read below is scoped to rows this test creates.
        long baseline = 0;
        await db.RunTriadDbCommand(async (ctx, ct) =>
        {
            baseline = await ctx.MarketTransaction.MaxAsync(t => (long?)t.Id, ct) ?? 0;
        }, default);

        var testMap = await pair.CreateTestMap();
        var grid = testMap.Grid.Owner;

        EntityUid console = default;
        EntityUid crateUid = default;
        EntityUid siloUid = default;

        var mapSys = entMan.System<SharedMapSystem>();

        await server.WaitPost(() =>
        {
            // Both tiles have to exist before anything can anchor to them.
            mapSys.SetTile(grid, testMap.Grid.Comp, new Vector2i(0, 0), new Tile(1));
            mapSys.SetTile(grid, testMap.Grid.Comp, new Vector2i(1, 0), new Tile(1));

            var palletCoords = new EntityCoordinates(grid, 0.5f, 0.5f);
            var consoleCoords = new EntityCoordinates(grid, 1.5f, 0.5f);

            // The pallet has to be anchored and parented to the console's grid, or pallet discovery
            // skips it and the sale silently finds nothing to sell. Some of these prototypes anchor
            // themselves on spawn, and anchoring twice trips an engine assert.
            var pallet = entMan.SpawnAtPosition(PalletProto, palletCoords);
            AnchorIfLoose(entMan, xformSys, pallet);

            console = entMan.SpawnAtPosition(ConsoleProto, consoleCoords);
            AnchorIfLoose(entMan, xformSys, console);

            // Straight into the containers rather than through the storage systems, which apply
            // open-state and whitelist checks that have nothing to do with what is being tested.
            // Inserted into a real container or the test fails loudly, never silently.

            // A crate of one thing. Its contents must show up as child lines rather than being
            // folded into a single price for the crate. One stack only: two identical stacks
            // spawned at the same spot merge before either can be inserted.
            var crate = entMan.SpawnAtPosition(CrateProto, palletCoords);
            crateUid = crate;
            var crateContents = containers.EnsureContainer<Container>(crate, "entity_storage");
            Assert.That(containers.Insert(entMan.SpawnAtPosition(CrateContent, palletCoords), crateContents),
                Is.True, "the crate accepted the sheets");

            // And a container of several different things, so a capture that only ever sees one
            // child prototype would not pass.
            var silo = entMan.SpawnAtPosition(SiloProto, palletCoords);
            siloUid = silo;
            var siloContents = containers.EnsureContainer<Container>(silo, "storagebase");
            foreach (var proto in SiloContents)
            {
                Assert.That(containers.Insert(entMan.SpawnAtPosition(proto, palletCoords), siloContents),
                    Is.True, $"the ore box accepted {proto}");
            }
        });

        await pair.RunTicksSync(5);

        // Invoke the handler directly rather than through the event bus. Raising the BUI message
        // with RaiseComponentEvent, the shape StoreTests uses successfully, does not reach
        // CargoSystem's subscription in this harness for reasons not run to ground; the appraise
        // handler, which publishes UI state on every path, provably never fired. What this test
        // exists to prove is the capture wiring inside OnPalletSale, and that starts here.
        var cargo = entMan.System<CargoSystem>();
        await server.WaitPost(() =>
        {
            var handler = typeof(CargoSystem).GetMethod("OnPalletSale",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            Assert.That(handler, Is.Not.Null, "OnPalletSale moved or was renamed; update this test");

            var ev = new CargoPalletSellMessage { Actor = console };
            handler!.Invoke(cargo, [console, entMan.GetComponent<CargoPalletConsoleComponent>(console), ev]);

            // A sale deletes what it sold and spawns cash. Both must be true before the row is
            // even worth looking for.
            Assert.That(entMan.Deleted(crateUid), Is.True, "the crate was sold");
            Assert.That(entMan.Deleted(siloUid), Is.True, "the ore box was sold");
        });

        await pair.RunTicksSync(5);

        // Force the queue out rather than waiting on the flush timer.
        await market.Flush();

        await db.RunTriadDbCommand(async (ctx, ct) =>
        {
            var tx = await ctx.MarketTransaction
                .Include(t => t.Lines)
                .Include(t => t.Splits)
                .Where(t => t.Id > baseline && t.Kind == MarketTransactionKind.PalletSale)
                .SingleAsync(ct);

            var roots = tx.Lines.Where(l => l.ParentLineIndex == null).ToList();
            var children = tx.Lines.Where(l => l.ParentLineIndex != null).ToList();
            var protos = tx.Lines.Select(l => l.EntityProto).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(tx.Net, Is.GreaterThan(0), "the sale paid something out");

                // Cash, not bank: the seller is paid in physical Credits and no balance moved. A row
                // marked Bank here could never be reconciled against anything.
                Assert.That(tx.Rail, Is.EqualTo(MarketRail.Cash));
                Assert.That(tx.ConsoleProto, Is.EqualTo(ConsoleProto));
                Assert.That(tx.Succeeded, Is.True);
                Assert.That(tx.Calc, Is.Not.Null, "the payout trace is what makes the rounding visible");

                // Two containers went on the pad, so two roots and nothing else at the top level.
                Assert.That(roots, Has.Count.EqualTo(2), "the crate and the ore box");
                Assert.That(roots.Select(r => r.EntityProto),
                    Is.EquivalentTo(new[] { CrateProto, SiloProto }));

                // The whole point: contents are visible individually.
                Assert.That(children, Is.Not.Empty,
                    "container contents must reach the corpus, or a crate teaches nothing about steel");
                Assert.That(protos, Does.Contain(CrateContent));
                foreach (var proto in SiloContents)
                    Assert.That(protos, Does.Contain(proto));

                // Every line reconciles against what was appraised, because each carries only its
                // own value. Against Gross rather than Net: the payout is cast to an int before the
                // cash spawns, and Gross is what was captured ahead of that rounding.
                Assert.That(tx.Lines.Sum(l => l.LineTotal), Is.EqualTo(tx.Gross),
                    "every line of a transaction sums to its gross");
                Assert.That(roots.Sum(r => r.LineTotal), Is.LessThan(tx.Gross),
                    "and the roots are the two container shells, which is not the whole sale");

                // Every child resolves to a root that exists in this transaction.
                var indices = tx.Lines.Select(l => l.LineIndex).ToHashSet();
                Assert.That(children.All(c => indices.Contains(c.ParentLineIndex!.Value)), Is.True,
                    "no child points at a line that was skipped or never written");

                Assert.That(tx.Lines.All(l => l.Direction == MarketDirection.Sale), Is.True);
                Assert.That(tx.Lines.All(l => l.LineTotal > 0), Is.True,
                    "worthless nodes are dropped rather than filling the corpus");
            });

            // Taxes belong to this sale, not to rows of their own. Whether any fire depends on the
            // console's tax configuration, so assert the relationship rather than a count.
            var standalone = await ctx.MarketTransaction
                .CountAsync(t => t.Id > baseline && t.Kind == MarketTransactionKind.SectorLedger, ct);

            Assert.That(standalone, Is.Zero,
                "a pallet sale's taxes attach as splits; a standalone ledger row here means the "
                + "suppression at the call sites regressed and the link back to the sale is lost");
        }, default);

        await pair.CleanReturnAsync();
    }
}
