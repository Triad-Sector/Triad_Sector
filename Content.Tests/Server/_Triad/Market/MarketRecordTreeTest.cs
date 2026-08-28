using Content.Server._Triad.Market;
using Content.Server.Database;
using NUnit.Framework;

namespace Content.Tests.Server._Triad.Market;

// The line tree carries one invariant that nothing else enforces: every line carries only its own
// value, so all the lines of a transaction sum to its gross. Read it as roots-only and every total
// over the line table under-counts anything sold inside a container, which on a cargo pad is most
// of what gets sold.
//
// It matters because the mistake is silent. A crate of forty steel sheets records as a line for the
// crate shell plus forty lines for the sheets; taking the crate alone gives a fraction of the money
// that changed hands, and nothing about the resulting number looks wrong. This branch documented it
// backwards for four commits before an end-to-end sale measured it.
[TestFixture]
[TestOf(typeof(MarketRecord))]
public sealed class MarketRecordTreeTest
{
    private static MarketRecord CrateOfSteel()
    {
        var record = new MarketRecord { Kind = MarketTransactionKind.PalletSale };

        // A crate whose own shell is worth 200, holding two sheets worth 50 each. The appraisal
        // totals 300, and each of the three lines carries its own share of that.
        var crate = record.AddLine("CrateGeneric", MarketDirection.Sale, 1, 20000, 20000, MarketPriceSource.Static);
        record.AddChildLine(crate, "SheetSteel", MarketDirection.Sale, 1, 5000, 5000, MarketPriceSource.Stack);
        record.AddChildLine(crate, "SheetSteel", MarketDirection.Sale, 1, 5000, 5000, MarketPriceSource.Stack);

        return record;
    }

    [Test]
    public void EveryLineCarriesItsOwnValueAndAllOfThemSumToTheGross()
    {
        var record = CrateOfSteel();

        Assert.That(record.Lines, Has.Count.EqualTo(3), "crate plus its two sheets");
        Assert.That(record.LineTotal(), Is.EqualTo(30000), "the shell plus both sheets");

        var roots = 0L;
        foreach (var line in record.Lines)
        {
            if (line.ParentLineIndex == null)
                roots += line.LineTotal;
        }

        Assert.That(roots, Is.EqualTo(20000),
            "the crate line is the shell on its own, so roots alone understate the sale");
    }

    [Test]
    public void IndicesAreTransactionLocalAndParentsResolve()
    {
        var record = CrateOfSteel();

        // The tree is expressed in indices assigned before anything is written, which is what lets
        // the whole thing insert in one pass with no round trip for generated keys.
        for (var i = 0; i < record.Lines.Count; i++)
            Assert.That(record.Lines[i].LineIndex, Is.EqualTo(i), "index is the position in the list");

        Assert.That(record.Lines[0].ParentLineIndex, Is.Null, "the crate is a root");
        Assert.That(record.Lines[1].ParentLineIndex, Is.EqualTo(0));
        Assert.That(record.Lines[2].ParentLineIndex, Is.EqualTo(0));
    }

    [Test]
    public void LooseItemsAreAllRoots()
    {
        var record = new MarketRecord { Kind = MarketTransactionKind.PalletSale };
        record.AddLine("SheetSteel", MarketDirection.Sale, 30, 100, 3000, MarketPriceSource.Stack);
        record.AddLine("SheetGlass", MarketDirection.Sale, 10, 50, 500, MarketPriceSource.Stack);

        Assert.That(record.LineTotal(), Is.EqualTo(3500),
            "nothing containerized, so every line is a root and the two readings agree");
    }

    [Test]
    public void SplitsCarryTheirAccountAndSign()
    {
        var record = new MarketRecord { Kind = MarketTransactionKind.PalletSale };

        // A sale that fed one account and was penalised by another. Both are splits of the same
        // transaction rather than two unrelated rows, which is the whole reason the table exists.
        record.AddSplit("Frontier", "ColonialOutpostSales", 1000);
        record.AddSplit("BlackMarket", "BlackMarketPenalties", -250);

        Assert.That(record.Splits, Has.Count.EqualTo(2));

        var net = 0L;
        foreach (var split in record.Splits)
            net += split.Amount;

        Assert.That(net, Is.EqualTo(750), "a penalty nets against income rather than adding to it");
    }
}
