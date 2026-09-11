using System.Collections.Generic;
using Content.Server.Database;

namespace Content.Server._Triad.Market;

/// <summary>
/// One captured transaction, as the game thread hands it over.
///
/// <para>Deliberately not the EF entity. A capture site builds one of these and drops it in a
/// queue; the writer converts it to entities on its own thread. Enqueuing tracked EF objects from
/// the game thread would put change-tracking state somewhere two threads can reach it, which is the
/// kind of bug that shows up as a corrupted batch once a month and never reproduces.</para>
///
/// <para>Use <see cref="AddLine"/> and <see cref="AddChildLine"/> rather than filling
/// <see cref="Lines"/> by hand; they assign the transaction-local indices the container tree is
/// built from.</para>
/// </summary>
public sealed class MarketRecord
{
    public MarketTransactionKind Kind;

    /// <summary>The gameplay <c>LedgerEntryType</c> name, where one applies.</summary>
    public string? LedgerEntryType;

    /// <summary>Null for machine-driven income with no player behind it.</summary>
    public Guid? ActorUserId;

    /// <summary>The character the actor was playing. Recorded once per round, not per transaction.</summary>
    public string? ActorCharacterName;

    /// <summary>The <c>CurrencyPrototype</c> id.</summary>
    public string Currency = "Speso";

    public MarketRail Rail;

    /// <summary>Minor units. See the note in Model.Market.cs on why these are hundredths.</summary>
    public long Gross;
    public long Tax;
    public long Net;

    /// <summary>Undiscounted price, where a voucher or an exemption meant it was not what was paid.</summary>
    public long? ListPrice;

    public bool Succeeded = true;
    public string? FailReason;

    public string? LocationName;
    public string? ConsoleProto;
    public float? MarketMod;
    public Guid? ShipGuid;

    /// <summary>The payout trace, for the handful of sites whose arithmetic is worth replaying.</summary>
    public string? Calc;

    public readonly List<MarketSplitRecord> Splits = new();
    public readonly List<MarketLineRecord> Lines = new();

    /// <summary>
    /// Records that <paramref name="amount"/> of this transaction went to one account. Splits are
    /// what keep a sale and the four taxes it produced as one fact rather than five unrelated ones.
    /// </summary>
    public void AddSplit(string account, string entryType, long amount)
    {
        Splits.Add(new MarketSplitRecord
        {
            Account = account,
            EntryType = entryType,
            Amount = amount,
        });
    }

    /// <summary>
    /// Adds a root line, something that sat on the pad in its own right. Returns its index, which
    /// is what <see cref="AddChildLine"/> takes to hang contents off it.
    /// </summary>
    public int AddLine(string entityProto, MarketDirection direction, int quantity, long unitPrice, long lineTotal,
        MarketPriceSource priceSource, float? multiplier = null)
    {
        return AddLineInternal(null, entityProto, direction, quantity, unitPrice, lineTotal, priceSource, multiplier);
    }

    /// <summary>
    /// Adds a line for something priced inside a container. A child carries its own value like any
    /// other line; the parent link records where it sat, not what it contributes. See the invariant
    /// on <see cref="MarketTransactionLine"/>.
    /// </summary>
    public int AddChildLine(int parentIndex, string entityProto, MarketDirection direction, int quantity,
        long unitPrice, long lineTotal, MarketPriceSource priceSource, float? multiplier = null)
    {
        return AddLineInternal(parentIndex, entityProto, direction, quantity, unitPrice, lineTotal, priceSource, multiplier);
    }

    private int AddLineInternal(int? parentIndex, string entityProto, MarketDirection direction, int quantity,
        long unitPrice, long lineTotal, MarketPriceSource priceSource, float? multiplier)
    {
        var index = Lines.Count;
        Lines.Add(new MarketLineRecord
        {
            LineIndex = index,
            ParentLineIndex = parentIndex,
            EntityProto = entityProto,
            Direction = direction,
            Quantity = quantity,
            UnitPrice = unitPrice,
            LineTotal = lineTotal,
            PriceSource = priceSource,
            Multiplier = multiplier,
        });
        return index;
    }

    /// <summary>
    /// The sum of every line, which by the tree invariant is the transaction's gross. Exists so
    /// tests and the sell path can assert that rather than hope for it.
    ///
    /// <para>Every line, not the roots alone. The collector reports each entity its own value, so a
    /// crate line is the shell and its contents are lines beside it rather than underneath it.
    /// There is no subset to filter on and nothing to double-count.</para>
    /// </summary>
    public long LineTotal()
    {
        long total = 0;
        foreach (var line in Lines)
            total += line.LineTotal;
        return total;
    }
}

public sealed class MarketSplitRecord
{
    public string Account = null!;
    public string EntryType = null!;
    public long Amount;
}

public sealed class MarketLineRecord
{
    public int LineIndex;
    public int? ParentLineIndex;
    public string EntityProto = null!;
    public MarketDirection Direction;
    public int Quantity;
    public long UnitPrice;
    public long LineTotal;
    public float? Multiplier;
    public MarketPriceSource PriceSource;
}
