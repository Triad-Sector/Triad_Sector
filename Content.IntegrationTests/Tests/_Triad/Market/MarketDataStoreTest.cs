using System.Linq;
using Content.Server._Triad.Market;
using Content.Server.Database;
using Microsoft.EntityFrameworkCore;
using Robust.Shared.IoC;

namespace Content.IntegrationTests.Tests._Triad.Market;

/// <summary>
/// Round-trips captured transactions through the real database layer.
///
/// <para>The unit tests over <see cref="MarketRecord"/> prove the tree is built correctly in memory.
/// They cannot prove it survives being written, which is where the interesting failures live: the
/// container tree is expressed in transaction-local indices that no foreign key enforces, amounts
/// are minor units that a careless conversion silently scales by a hundred, and enums persist as
/// names rather than the integers EF would default to.</para>
///
/// <para>Every query here filters on a per-test marker. The pooled pair shares one database across
/// tests, so anything asserting on "the only row" passes alone and fails in a suite, which is the
/// worst way for a test to be wrong.</para>
/// </summary>
[TestOf(typeof(MarketDataStore))]
public sealed class MarketDataStoreTest
{
    private const string RoundTripMarker = "test-round-trip";
    private const string RefusedMarker = "test-refused";
    private const string PurgeOldMarker = "test-purge-old";
    private const string PurgeNewMarker = "test-purge-new";

    private static MarketRecord CrateOfSteel()
    {
        var record = new MarketRecord
        {
            Kind = MarketTransactionKind.PalletSale,
            Currency = "Speso",
            Rail = MarketRail.Cash,
            Gross = 27000,
            Tax = 2000,
            Net = 25000,
            LocationName = RoundTripMarker,
            ConsoleProto = "ComputerPalletConsoleNFHighMarket",
            MarketMod = 1.25f,
        };

        var crate = record.AddLine("CrateGeneric", MarketDirection.Sale, 1, 20000, 20000, MarketPriceSource.Static, 1.25f);
        record.AddChildLine(crate, "SheetSteel", MarketDirection.Sale, 30, 200, 6000, MarketPriceSource.Stack, 1.25f);
        record.AddChildLine(crate, "SheetGlass", MarketDirection.Sale, 10, 100, 1000, MarketPriceSource.Stack, 1.25f);

        record.AddSplit("Frontier", "ColonialOutpostSales", 1500);
        record.AddSplit("BlackMarket", "BlackMarketPenalties", -500);

        return record;
    }

    [Test]
    public async Task TransactionRoundTripsWithItsTreeAndSplits()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var store = server.ResolveDependency<MarketDataStore>();
        var db = server.ResolveDependency<IServerDbManager>();

        var written = await store.WriteBatch(new[]
        {
            new PendingMarketRecord
            {
                Record = CrateOfSteel(),
                OccurredAt = new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc),
                RoundId = null,
            },
        });

        Assert.That(written, Is.EqualTo(1));

        await db.RunTriadDbCommand(async (ctx, ct) =>
        {
            var tx = await ctx.MarketTransaction
                .Include(t => t.Lines)
                .Include(t => t.Splits)
                .SingleAsync(t => t.LocationName == RoundTripMarker, ct);

            Assert.Multiple(() =>
            {
                // Enums must come back as names. If these ever read as "1" the column type changed
                // and every historical row is now mislabelled by the next mid-list enum insertion.
                Assert.That(tx.Kind, Is.EqualTo(MarketTransactionKind.PalletSale));
                Assert.That(tx.Rail, Is.EqualTo(MarketRail.Cash));

                Assert.That(tx.Gross, Is.EqualTo(27000), "minor units survive the write unscaled");
                Assert.That(tx.Net, Is.EqualTo(25000));
                Assert.That(tx.LocationName, Is.EqualTo(RoundTripMarker));
                Assert.That(tx.ConsoleProto, Is.EqualTo("ComputerPalletConsoleNFHighMarket"));
                Assert.That(tx.Succeeded, Is.True);

                Assert.That(tx.Lines, Has.Count.EqualTo(3));
                Assert.That(tx.Splits, Has.Count.EqualTo(2));
            });

            var root = tx.Lines.Single(l => l.ParentLineIndex == null);
            var children = tx.Lines.Where(l => l.ParentLineIndex != null).ToList();

            Assert.Multiple(() =>
            {
                Assert.That(root.EntityProto, Is.EqualTo("CrateGeneric"));
                Assert.That(root.LineTotal, Is.EqualTo(20000), "the crate line is the shell alone");

                // The invariant the whole design rests on. The roots alone give 20000 here.
                Assert.That(tx.Lines.Sum(l => l.LineTotal), Is.EqualTo(tx.Gross),
                    "every line of a transaction sums to its gross");

                Assert.That(children, Has.Count.EqualTo(2));
                Assert.That(children.All(c => c.ParentLineIndex == root.LineIndex), Is.True,
                    "children point at the crate by its transaction-local index");
                Assert.That(children.Select(c => c.EntityProto),
                    Is.EquivalentTo(new[] { "SheetSteel", "SheetGlass" }));

                // Denormalized onto lines on purpose; the pricing queries never join back for it.
                Assert.That(tx.Lines.All(l => l.OccurredAt == tx.OccurredAt), Is.True);

                Assert.That(tx.Splits.Sum(s => s.Amount), Is.EqualTo(1000),
                    "a penalty nets against income rather than adding to it");
            });
        }, default);

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task RefusedTransactionWritesAHeaderAndNoLines()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var store = server.ResolveDependency<MarketDataStore>();
        var db = server.ResolveDependency<IServerDbManager>();

        var refused = new MarketRecord
        {
            Kind = MarketTransactionKind.ShipyardPurchase,
            Currency = "Speso",
            Rail = MarketRail.Bank,
            Succeeded = false,
            FailReason = "InsufficientFunds",
            LocationName = RefusedMarker,
            ListPrice = 5_000_000,
        };

        await store.WriteBatch(new[]
        {
            new PendingMarketRecord { Record = refused, OccurredAt = DateTime.UtcNow, RoundId = null },
        });

        await db.RunTriadDbCommand(async (ctx, ct) =>
        {
            var tx = await ctx.MarketTransaction.Include(t => t.Lines)
                .SingleAsync(t => t.LocationName == RefusedMarker, ct);

            Assert.Multiple(() =>
            {
                Assert.That(tx.Succeeded, Is.False);
                Assert.That(tx.FailReason, Is.EqualTo("InsufficientFunds"));

                // A refused purchase is demand data and is kept, but it must never reach the price
                // corpus. That is what lets the pricing index stay unfiltered.
                Assert.That(tx.Lines, Is.Empty);

                // What it would have cost is the point of the row.
                Assert.That(tx.ListPrice, Is.EqualTo(5_000_000));
            });
        }, default);

        await pair.CleanReturnAsync();
    }

    [Test]
    public async Task PurgeRemovesOldRowsAndLeavesRecentOnes()
    {
        await using var pair = await PoolManager.GetServerClient();
        var server = pair.Server;

        var store = server.ResolveDependency<MarketDataStore>();
        var db = server.ResolveDependency<IServerDbManager>();

        var now = DateTime.UtcNow;

        await store.WriteBatch(new[]
        {
            new PendingMarketRecord
            {
                Record = new MarketRecord
                {
                    Kind = MarketTransactionKind.AtmDeposit, Currency = "Speso", LocationName = PurgeOldMarker,
                },
                OccurredAt = now.AddDays(-120),
            },
            new PendingMarketRecord
            {
                Record = new MarketRecord
                {
                    Kind = MarketTransactionKind.AtmWithdraw, Currency = "Speso", LocationName = PurgeNewMarker,
                },
                OccurredAt = now.AddDays(-1),
            },
        });

        var removed = await store.PurgeOlderThan(now.AddDays(-90));
        Assert.That(removed, Is.GreaterThanOrEqualTo(1));

        await db.RunTriadDbCommand(async (ctx, ct) =>
        {
            Assert.Multiple(async () =>
            {
                Assert.That(await ctx.MarketTransaction.AnyAsync(t => t.LocationName == PurgeOldMarker, ct),
                    Is.False, "the expired row is gone");
                Assert.That(await ctx.MarketTransaction.AnyAsync(t => t.LocationName == PurgeNewMarker, ct),
                    Is.True, "the recent row survives");
            });
        }, default);

        await pair.CleanReturnAsync();
    }
}
