using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

// ReSharper disable EntityFramework.ModelValidation.UnlimitedStringLength

namespace Content.Server.Database;

//
// Triad: market data, a durable record of every currency transaction on the server.
// Kept in its own file rather than in Model.cs so a merge from upstream conflicts on two lines
// there instead of on a hundred here. Schema decisions are argued on the Market Data Schema
// Design wiki page; read it before changing a column, because a persisted schema is the one
// artifact that cannot be cheaply changed once real rows exist.
//
// Three conventions hold across every table here and are load-bearing:
//
//   Enums persist as their NAME, never their integer value. LedgerEntryType upstream is a byte
//   enum that has already been renumbered twice by mid-list insertions; persisting ints means the
//   next insertion silently relabels history. Gameplay enums that live in Content.Shared arrive
//   here as plain strings, because this project must not take a dependency on gameplay.
//
//   Amounts are minor units in a long. The player bank is an int and store balances are
//   FixedPoint2 hundredths; one column has to hold both, so it holds hundredths.
//
//   OccurredAt is denormalized onto line rows on purpose. Every pricing and rollup query starts
//   from a line and filters by time, and without it they all join back to the largest table.
//

internal static class ModelMarket
{
    public static void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Enum columns persist as names. See the header comment.
        modelBuilder.Entity<MarketTransaction>()
            .Property(t => t.Kind)
            .HasConversion<string>();

        modelBuilder.Entity<MarketTransaction>()
            .Property(t => t.Rail)
            .HasConversion<string>();

        modelBuilder.Entity<MarketTransactionLine>()
            .Property(l => l.Direction)
            .HasConversion<string>();

        modelBuilder.Entity<MarketTransactionLine>()
            .Property(l => l.PriceSource)
            .HasConversion<string>();

        modelBuilder.Entity<MarketPriceStat>()
            .Property(s => s.Direction)
            .HasConversion<string>();

        // Telemetry outlives its subjects. A deleted round or player must never take the record of
        // what happened with it, so both sides null out rather than cascade.
        modelBuilder.Entity<MarketTransaction>()
            .HasOne(t => t.Round)
            .WithMany()
            .HasForeignKey(t => t.RoundId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MarketTransaction>()
            .HasOne(t => t.Actor)
            .WithMany()
            .HasForeignKey(t => t.ActorUserId)
            .HasPrincipalKey(p => p.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Splits and lines are parts of their transaction and have no meaning without it.
        modelBuilder.Entity<MarketTransactionSplit>()
            .HasKey(s => new { s.TransactionId, s.Account, s.EntryType });

        modelBuilder.Entity<MarketTransactionSplit>()
            .HasOne(s => s.Transaction)
            .WithMany(t => t.Splits)
            .HasForeignKey(s => s.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MarketTransactionLine>()
            .HasOne(l => l.Transaction)
            .WithMany(t => t.Lines)
            .HasForeignKey(l => l.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MarketPriceStat>()
            .HasKey(s => new { s.EntityProto, s.Currency, s.Direction, s.Day });

        modelBuilder.Entity<MarketRoundParticipant>()
            .HasKey(p => new { p.RoundId, p.UserId, p.CharacterName });

        modelBuilder.Entity<MarketRoundParticipant>()
            .HasOne(p => p.Round)
            .WithMany()
            .HasForeignKey(p => p.RoundId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SectorAccountSample>()
            .HasOne(s => s.Round)
            .WithMany()
            .HasForeignKey(s => s.RoundId)
            .OnDelete(DeleteBehavior.Cascade);

        //
        // Indexes. Postgres also gets a BRIN index on OccurredAt and a covering partial index for
        // the pricing lookup; both are provider-specific and live in ModelPostgres.cs.
        //

        // Per-round totals, and the retention purge's unit of work.
        modelBuilder.Entity<MarketTransaction>()
            .HasIndex(t => t.RoundId);

        // The Grafana breakdown-by-kind panel.
        modelBuilder.Entity<MarketTransaction>()
            .HasIndex(t => new { t.Kind, t.OccurredAt });

        // Per-player history. Postgres narrows this to a partial index on non-null actors in
        // ModelPostgres.cs, because the filter predicate has to name the physical column and the
        // two providers spell it differently.
        modelBuilder.Entity<MarketTransaction>()
            .HasIndex(t => new { t.ActorUserId, t.OccurredAt });

        modelBuilder.Entity<MarketTransactionLine>()
            .HasIndex(l => l.TransactionId);

        // The pricing question, and the rollup's driving scan. Postgres replaces this with a
        // covering, partial version in ModelPostgres.cs; this is the portable floor.
        modelBuilder.Entity<MarketTransactionLine>()
            .HasIndex(l => new { l.EntityProto, l.Direction, l.OccurredAt });

        // One line per index per transaction. Unique because the writer assigns these and a
        // duplicate would silently corrupt the container tree rather than failing.
        modelBuilder.Entity<MarketTransactionLine>()
            .HasIndex(l => new { l.TransactionId, l.LineIndex })
            .IsUnique();

        modelBuilder.Entity<SectorAccountSample>()
            .HasIndex(s => new { s.Account, s.SampledAt });
    }
}

/// <summary>
/// One money-moving act. The header: what, when, who, how much, and where.
/// </summary>
public class MarketTransaction
{
    public long Id { get; set; }

    /// <summary>
    /// Nullable because rows can be created before a round exists. Transactions raised in the
    /// pre-round lobby are held in a separate queue and stamped once the round has an id, the same
    /// way admin logs handle it; a row that somehow escapes that still has to be storable.
    /// </summary>
    public int? RoundId { get; set; }
    public Round? Round { get; set; }

    /// <summary>
    /// Stored as UTC. Always use <c>DateTime.UtcNow</c> at write sites; non-UTC values throw on Postgres.
    /// </summary>
    public DateTime OccurredAt { get; set; }

    public MarketTransactionKind Kind { get; set; }

    /// <summary>
    /// The name of the gameplay <c>LedgerEntryType</c> this transaction corresponds to, or null
    /// where none applies. A string rather than an enum because that type lives in Content.Shared
    /// and this project does not depend on gameplay, and because a name survives the mid-list
    /// insertions that byte enum has already taken twice.
    /// </summary>
    public string? LedgerEntryType { get; set; }

    /// <summary>Null for machine-driven income: ticking accounts, power sales, event rewards.</summary>
    public Guid? ActorUserId { get; set; }
    public Player? Actor { get; set; }

    /// <summary>The <c>CurrencyPrototype</c> id. A string so content can add a currency without a migration.</summary>
    public string Currency { get; set; } = null!;

    public MarketRail Rail { get; set; }

    /// <summary>Minor units. Gross is before tax, net is what the actor actually received or paid.</summary>
    public long Gross { get; set; }
    public long Tax { get; set; }
    public long Net { get; set; }

    /// <summary>
    /// What this would have cost undiscounted, where that differs from what was paid. Vouchers buy
    /// ships at zero and Ironman characters pay nothing at vendors; without this column every such
    /// transaction poisons the answer to "what is this worth".
    /// </summary>
    public long? ListPrice { get; set; }

    /// <summary>
    /// False for a refused transaction. Those are kept deliberately: someone trying to buy a ship
    /// they cannot afford is demand data, and it is the only place that signal exists. Excluded
    /// from the pricing index, included in the table.
    /// </summary>
    public bool Succeeded { get; set; }
    public string? FailReason { get; set; }

    /// <summary>Station name, which is copied from the POI prototype at spawn. Null off-station.</summary>
    public string? LocationName { get; set; }

    /// <summary>
    /// The console or machine prototype id. This carries the market tier on its own: the pallet
    /// console prototypes are authored per map and named for their tier.
    /// </summary>
    public string? ConsoleProto { get; set; }

    /// <summary>The market modifier in force, where one applied.</summary>
    public float? MarketMod { get; set; }

    /// <summary>
    /// Loose reference to a drydock hull, deliberately NOT an EF foreign key: that table does not
    /// exist on every branch, and a transaction must outlive the ship it was made against.
    /// </summary>
    public Guid? ShipGuid { get; set; }

    /// <summary>
    /// The payout calculation as a replayable trace, populated only where the arithmetic is
    /// non-trivial and null everywhere else. JSONB on Postgres, TEXT on SQLite.
    /// </summary>
    public string? Calc { get; set; }

    public List<MarketTransactionSplit> Splits { get; set; } = new();
    public List<MarketTransactionLine> Lines { get; set; } = new();
}

/// <summary>
/// Who got paid out of one transaction. A single pallet sale feeds up to four sector accounts, and
/// recording those as separate transactions would destroy the link back to the sale that produced
/// them, which is exactly what the in-memory ledger cannot answer today.
/// </summary>
public class MarketTransactionSplit
{
    public long TransactionId { get; set; }
    public MarketTransaction Transaction { get; set; } = null!;

    /// <summary>The <c>SectorBankAccount</c> name, or <c>Player</c> for the actor's own share.</summary>
    public string Account { get; set; } = null!;

    /// <summary>The gameplay <c>LedgerEntryType</c> name. See the note on the header's copy.</summary>
    public string EntryType { get; set; } = null!;

    public long Amount { get; set; }
}

/// <summary>
/// One priced item within a transaction. This is the corpus everything downstream learns from.
///
/// <para>Lines form a tree. A root line (null <see cref="ParentLineIndex"/>) is something that sat
/// on the pad; child lines are the contents of a container, captured so the breakdown reaches leaf
/// items rather than stopping at "one crate, 1200". <b>Every line carries only its own value, and
/// all lines of a transaction sum to its gross.</b> A crate line is the shell and its contents are
/// lines beside it, so the parent link says where a thing sat rather than what it contributes.
/// There is no subset to filter on and nothing to double-count.</para>
///
/// <para><b>A refused transaction writes a header and no lines.</b> Nothing changed hands, so there
/// is nothing to price, and that invariant is what lets the pricing index stay unfiltered: it
/// cannot contain a failed sale by construction. Demand analysis reads the headers instead.</para>
/// </summary>
public class MarketTransactionLine
{
    public long Id { get; set; }

    public long TransactionId { get; set; }
    public MarketTransaction Transaction { get; set; } = null!;

    /// <summary>
    /// Denormalized from the header. Every pricing and rollup query starts here and filters by
    /// time; joining back to the transaction table for it is the one join worth avoiding.
    /// </summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>
    /// This line's position within its transaction, assigned by the writer. Unique per transaction.
    /// </summary>
    public int LineIndex { get; set; }

    /// <summary>
    /// The <see cref="LineIndex"/> of the containing line, for an item priced inside a container.
    /// Null on a root line.
    ///
    /// <para>Transaction-local rather than a global line id, and deliberately not a foreign key.
    /// A global reference would force the writer to insert parents, round-trip for their generated
    /// ids, then insert children, turning one batched insert into a per-depth sequence. Indices are
    /// known before anything is written, so the whole tree inserts in one pass.</para>
    /// </summary>
    public int? ParentLineIndex { get; set; }

    public string EntityProto { get; set; } = null!;

    public MarketDirection Direction { get; set; }

    public int Quantity { get; set; }

    /// <summary>Minor units, per unit and for the line. Pre-multiplier.</summary>
    public long UnitPrice { get; set; }
    public long LineTotal { get; set; }

    /// <summary>The per-entity market multiplier that applied, where one did.</summary>
    public float? Multiplier { get; set; }

    /// <summary>
    /// Which price source produced the number. Without this you know an item sold for 400 and not
    /// which component to edit. <see cref="MarketPriceSource.Fallback"/> exists specifically so the
    /// hardcoded default in the vending path is excludable from any model.
    /// </summary>
    public MarketPriceSource PriceSource { get; set; }
}

/// <summary>
/// The daily rollup. Permanent, small, and the only table the game itself ever loads.
/// </summary>
public class MarketPriceStat
{
    public string EntityProto { get; set; } = null!;
    public string Currency { get; set; } = null!;
    public MarketDirection Direction { get; set; }
    public DateOnly Day { get; set; }

    public int TradeCount { get; set; }
    public long Units { get; set; }
    public long TotalValue { get; set; }
    public long MinUnit { get; set; }
    public long MaxUnit { get; set; }
}

/// <summary>
/// One row per character a player used in a round. A player transacts many times per round under
/// one character, so storing the name on every transaction repeats a string thousands of times to
/// say something that is true once. It is also the denominator for anything per-capita.
/// </summary>
public class MarketRoundParticipant
{
    public int RoundId { get; set; }
    public Round Round { get; set; } = null!;

    public Guid UserId { get; set; }
    public string CharacterName { get; set; } = null!;
}

/// <summary>
/// A periodic sample of a sector account balance, replacing ticking income as transaction rows.
/// Ticking income is five accounts on a ten second interval, roughly 1,800 rows an hour and about
/// forty percent of the whole corpus, and it is fully reconstructible from the account's rate. A
/// sample carries the same information at a fraction of the volume.
/// </summary>
public class SectorAccountSample
{
    public long Id { get; set; }

    public int RoundId { get; set; }
    public Round Round { get; set; } = null!;

    /// <summary>Stored as UTC.</summary>
    public DateTime SampledAt { get; set; }

    /// <summary>The <c>SectorBankAccount</c> name.</summary>
    public string Account { get; set; } = null!;

    public long Balance { get; set; }
}

/// <summary>
/// What kind of act moved the money. Persisted as a name, so this list may be reordered freely and
/// may never have a member renamed without migrating the rows that carry the old name.
/// </summary>
public enum MarketTransactionKind
{
    Unknown = 0,
    PalletSale,
    CargoOrder,
    VendorSale,
    ShipyardPurchase,
    ShipyardSale,
    ShipLoadAppraisal,
    MarketCrate,
    AtmDeposit,
    AtmWithdraw,
    StationAtmDeposit,
    StationAtmWithdraw,
    StoreBuy,
    StoreRefund,
    StoreWithdraw,
    MailDelivery,
    MedicalBounty,
    PowerSale,
    DeadDropBonus,
    BluespaceReward,
    ShuttleRecordFee,
    LoadoutSpawn,
    AdminAdjust,

    /// <summary>
    /// A sector account movement captured at the ledger chokepoint rather than at a site that knew
    /// what was happening. The <c>LedgerEntryType</c> carries the real taxonomy for these; this kind
    /// says only how the row arrived, which is what distinguishes it from a rich capture site that
    /// also knows the actor, the items, and the payout inputs.
    /// </summary>
    SectorLedger,
}

/// <summary>
/// Which substrate the value actually moved through. Credits exist on more than one of these and
/// the same physical cash stack feeds several, so without this column a sum over the table cannot
/// be reconciled against any single balance.
/// </summary>
public enum MarketRail
{
    Unknown = 0,
    Bank,
    Cash,
    CashSlot,
    StoreBalance,
    Mixed,
    Voucher,
    None,
}

public enum MarketDirection
{
    Unknown = 0,
    Sale,
    Purchase,
}

/// <summary>
/// Which price provider produced a line's unit price.
/// </summary>
public enum MarketPriceSource
{
    Unknown = 0,
    Static,
    Stack,
    Mob,
    Drifting,
    Material,
    Solution,
    VendOverride,

    /// <summary>
    /// A hardcoded default standing in for a real price, not a valuation. Any model trained on
    /// these reads a constant as a signal, so they must be excludable.
    /// </summary>
    Fallback,
}
