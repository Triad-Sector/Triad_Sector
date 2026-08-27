using Content.Server._Triad.Market;
using Content.Server.Database;
using NUnit.Framework;

namespace Content.Tests.Server._Triad.Market;

// The line tree carries one invariant that nothing else enforces: root lines sum to the payout, and
// child lines are breakdown only. Get it wrong and every total over the line table double-counts
// anything that was sold inside a container, which on a cargo pad is most of what gets sold.
//
// It matters because the mistake is silent. A crate of forty steel sheets records as a root line for
// the crate plus forty children; summing all forty one gives twice the money that changed hands, and
// nothing about the resulting number looks wrong.
[TestFixture]
[TestOf(typeof(MarketRecord))]
public sealed class MarketRecordTreeTest
{
    private static MarketRecord CrateOfSteel()
    {
        var record = new MarketRecord { Kind = MarketTransactionKind.PalletSale };

        // One crate worth 200, holding two sheets worth 50 each. The crate's own price is 100, so
        // the appraisal totals 200 and the crate line carries the whole 200 as a root.
        var crate = record.AddLine("CrateGeneric", MarketDirection.Sale, 1, 20000, 20000, MarketPriceSource.Static);
        record.AddChildLine(crate, "SheetSteel", MarketDirection.Sale, 1, 5000, 5000, MarketPriceSource.Stack);
        record.AddChildLine(crate, "SheetSteel", MarketDirection.Sale, 1, 5000, 5000, MarketPriceSource.Stack);

        return record;
    }

    [Test]
    public void RootLinesSumToThePayoutAndChildrenDoNot()
    {
        var record = CrateOfSteel();

        Assert.That(record.Lines, Has.Count.EqualTo(3), "crate plus its two sheets");
        Assert.That(record.RootLineTotal(), Is.EqualTo(20000), "only the crate is a root");

        var everyLine = 0L;
        foreach (var line in record.Lines)
            everyLine += line.LineTotal;

        Assert.That(everyLine, Is.EqualTo(30000),
            "summing every line double-counts the contents, which is exactly why callers must not");
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

        Assert.That(record.RootLineTotal(), Is.EqualTo(3500),
            "nothing containerized, so roots and the full payout agree");
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
