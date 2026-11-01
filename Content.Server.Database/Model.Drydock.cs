using System;
using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;

// ReSharper disable EntityFramework.ModelValidation.UnlimitedStringLength

namespace Content.Server.Database;

//
// Triad: the drydock, a database-backed replacement for client-held ship save files.
// Kept in its own file rather than in Model.cs so a merge from upstream conflicts on two lines
// there instead of on a hundred here. Schema is locked; see the Drydock System Design wiki page
// before changing a column, because a persisted schema is the one artifact that cannot be cheaply
// changed once real ships exist.
//

internal static class ModelDrydock
{
    public static void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Explicit: the key is named for what it is rather than "Id", so convention will not find it.
        modelBuilder.Entity<DrydockShip>()
            .HasKey(s => s.ShipGuid);

        modelBuilder.Entity<DrydockShip>()
            .HasOne(s => s.Owner)
            .WithMany()
            .HasForeignKey(s => s.OwnerUserId)
            .HasPrincipalKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // The stored-ship list and every admin query start from an owner.
        modelBuilder.Entity<DrydockShip>()
            .HasIndex(s => s.OwnerUserId);

        // "Which ships are checked out and never came back" is the adjudication question, and it
        // wants to be an index scan rather than a table scan once the fleet is large.
        modelBuilder.Entity<DrydockShip>()
            .HasIndex(s => new { s.State, s.StateChangedAt });

        modelBuilder.Entity<DrydockRevision>()
            .HasKey(r => new { r.ShipGuid, r.Revision });

        modelBuilder.Entity<DrydockRevision>()
            .HasOne(r => r.Ship)
            .WithMany(s => s.Revisions)
            .HasForeignKey(r => r.ShipGuid)
            .OnDelete(DeleteBehavior.Cascade);

        // Null on system re-bakes, and set null rather than cascade if the player row ever goes:
        // losing who stored a revision must never take the revision with it.
        modelBuilder.Entity<DrydockRevision>()
            .HasOne(r => r.Actor)
            .WithMany()
            .HasForeignKey(r => r.ActorUserId)
            .HasPrincipalKey(p => p.UserId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<DrydockBlob>()
            .HasKey(b => new { b.ShipGuid, b.Revision });

        modelBuilder.Entity<DrydockBlob>()
            .HasOne(b => b.RevisionRow)
            .WithOne(r => r.Blob)
            .HasForeignKey<DrydockBlob>(b => new { b.ShipGuid, b.Revision })
            .OnDelete(DeleteBehavior.Cascade);

        // The timeline read: one ship, oldest to newest.
        modelBuilder.Entity<DrydockAudit>()
            .HasIndex(a => new { a.ShipGuid, a.CreatedAt });

        modelBuilder.Entity<DrydockAudit>()
            .HasIndex(a => a.ActorUserId);

        // "Every ship this player has ever owned" walks transfer rows by recipient.
        modelBuilder.Entity<DrydockAudit>()
            .HasIndex(a => a.SubjectUserId);

        // Berths. Explicit key for the same reason as the ship.
        modelBuilder.Entity<DrydockBerth>()
            .HasKey(b => b.BerthId);

        modelBuilder.Entity<DrydockBerth>()
            .HasOne(b => b.Owner)
            .WithMany()
            .HasForeignKey(b => b.OwnerUserId)
            .HasPrincipalKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        // Every player-facing berth query starts from the owner.
        modelBuilder.Entity<DrydockBerth>()
            .HasIndex(b => b.OwnerUserId);

        // The principal for the ship's composite foreign key below. Redundant with the primary
        // key as a uniqueness fact, but a foreign key needs a key to point at, and pointing at
        // (berth, owner) rather than (berth) is what makes ownership a database invariant.
        modelBuilder.Entity<DrydockBerth>()
            .HasAlternateKey(b => new { b.BerthId, b.OwnerUserId });

        // A ship can sit only in a berth its own owner owns. A transfer that moves the owner but
        // not the berth, or a move into another player's berth, fails here rather than surfacing a
        // round trip later. Restrict on delete: an occupied berth cannot be deleted, move first.
        modelBuilder.Entity<DrydockShip>()
            .HasOne(s => s.Berth)
            .WithMany()
            .HasForeignKey(s => new { s.BerthId, s.OwnerUserId })
            .HasPrincipalKey(b => new { b.BerthId, b.OwnerUserId })
            .OnDelete(DeleteBehavior.Restrict);

        // One hull per berth. Nulls are distinct on both providers, so every ship that is out in
        // the world coexists with every other. This is also the index the free-berth anti-join
        // reads, and the arbiter when two stores race for the last free berth.
        modelBuilder.Entity<DrydockShip>()
            .HasIndex(s => s.BerthId)
            .IsUnique();

        // A hint, never a claim, so it may point at a berth that has since been deleted.
        modelBuilder.Entity<DrydockShip>()
            .HasOne(s => s.LastBerth)
            .WithMany()
            .HasForeignKey(s => s.LastBerthId)
            .OnDelete(DeleteBehavior.SetNull);

        // Transfers. A ship in escrow has exactly one pending offer, and the database says so:
        // the filtered unique index refuses a second while the first stands, whatever the code
        // thinks. Resolved offers stay as history beside the audit row that resolved them.
        modelBuilder.Entity<DrydockTransfer>()
            .HasOne(t => t.Ship)
            .WithMany()
            .HasForeignKey(t => t.ShipGuid)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DrydockTransfer>()
            .HasIndex(t => t.ShipGuid)
            .IsUnique()
            .HasFilter("resolution = 0");

        // The recipient's alert: every pending offer addressed to an account.
        modelBuilder.Entity<DrydockTransfer>()
            .HasIndex(t => new { t.ToUserId, t.Resolution });

        // The expiry sweep walks pending offers by their deadline.
        modelBuilder.Entity<DrydockTransfer>()
            .HasIndex(t => new { t.Resolution, t.ExpiresAt });
    }
}

/// <summary>
/// One transfer offer, addressed to one account, persisted so that a restart cannot leave a ship
/// in escrow with nobody able to resolve it. The ship's state says "in escrow"; this row says to
/// whom and until when, and its resolution says how it ended.
/// </summary>
public sealed class DrydockTransfer
{
    public long Id { get; set; }

    public Guid ShipGuid { get; set; }

    public DrydockShip Ship { get; set; } = default!;

    /// <summary>The owner who made the offer. Not a foreign key, for the same reason the audit's actor is not.</summary>
    public Guid FromUserId { get; set; }

    /// <summary>The account the offer is addressed to. Online when the offer was made; the clock runs regardless after that.</summary>
    public Guid ToUserId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DrydockTransferResolution Resolution { get; set; }

    public DateTime? ResolvedAt { get; set; }

    public int? RoundId { get; set; }
}

public enum DrydockTransferResolution
{
    /// <summary>Standing. The ship is in escrow for it. Zero on purpose: the filtered unique index names it.</summary>
    Pending = 0,

    Accepted = 1,
    Declined = 2,

    /// <summary>Withdrawn by the owner.</summary>
    Cancelled = 3,

    /// <summary>The clock ran out, by the sweep or by the boot check.</summary>
    Expired = 4,
}

/// <summary>
/// One row per hull, never pruned. Identity, ownership and authority live here; everything that
/// varies between stores lives on <see cref="DrydockRevision"/>.
/// </summary>
public sealed class DrydockShip
{
    /// <summary>
    /// The ship id, also stamped on the grid-side deed. A <see cref="Guid"/> rather than a row id
    /// because it has to outlive the grid and survive store and retrieve cycles unchanged.
    /// </summary>
    public Guid ShipGuid { get; set; }

    /// <summary>Authoritative owner. A transfer is one update here plus an audit row.</summary>
    public Guid OwnerUserId { get; set; }

    public Player Owner { get; set; } = default!;

    /// <summary>
    /// Display cache for the stored-ship list, refreshed on every store so the list can be drawn
    /// without materializing a blob.
    /// </summary>
    public string ShipName { get; set; } = default!;

    /// <summary>
    /// The character recorded as the hull's captain, for the drydock registry. The owner is an
    /// account and nothing else here names a character; this is written by the console when a
    /// character of the owning account stores, retrieves, imports, or accepts the hull, and is
    /// display only. Null on a hull no console has written it for yet.
    /// </summary>
    public string? CaptainName { get; set; }

    /// <summary>
    /// Informational only, and deliberately not load-bearing: a vessel prototype can be renamed or
    /// removed upstream, and a stored ship must not stop being retrievable because of it.
    /// </summary>
    public string? VesselProto { get; set; }

    /// <summary>
    /// Display cache, stored as text rather than an enum so a content-side size taxonomy change
    /// cannot invalidate rows.
    /// </summary>
    public string? SizeClass { get; set; }

    public DrydockShipState State { get; set; }

    public DateTime StateChangedAt { get; set; }

    /// <summary>
    /// The round the ship was checked out in. Together with <see cref="State"/> this answers
    /// "checked out in round N and never came back" from a single row.
    /// </summary>
    public int? CheckedOutRoundId { get; set; }

    public Round? CheckedOutRound { get; set; }

    public string? AdminNotes { get; set; }

    //
    // The impound fields. None of the three is cleared when the ship leaves the lot, and
    // that is a rule rather than an oversight: the next impound overwrites them, so a reversal has
    // something to restore to and the timeline can still say what the hull was taken for. Read them
    // only against <see cref="DrydockShipState.Impounded"/>; on any other state they are the last
    // impound's residue.
    //

    /// <summary>
    /// What reclaiming it costs, in credits, frozen at impound rather than recomputed. Appraisal lives
    /// per revision on <see cref="DrydockRevision.AppraisedValue"/> and moves with every store, and a
    /// debt already quoted to a player must not move under them. Zero is legal and means free.
    /// </summary>
    public int ImpoundFee { get; set; }

    /// <summary>What the hull was taken for, shown to its owner. Null when nothing was given.</summary>
    public string? ImpoundReason { get; set; }

    /// <summary>
    /// Whether the owner may act on the impound at all. Not inferable from the fee, which is why it
    /// is a column: a courtesy impound is free and redeemable, an adjudication is frozen at any
    /// price, and both are the same state at the same number of credits.
    /// </summary>
    public bool ImpoundRedeemable { get; set; }

    /// <summary>Pointer into <see cref="DrydockRevision"/>. Zero means nothing filed yet.</summary>
    public int CurrentRevision { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>
    /// The berth the hull sits in while stored, null while it is out in the world. A parking
    /// spot, not a home: retrieve vacates it as its last step, and store finds one again.
    /// </summary>
    public int? BerthId { get; set; }

    public DrydockBerth? Berth { get; set; }

    /// <summary>
    /// A hint, never a claim: the berth this hull last sat in, so a store can put it back where it
    /// was when that spot is still free, and an admin restore has a sensible default.
    /// </summary>
    public int? LastBerthId { get; set; }

    public DrydockBerth? LastBerth { get; set; }

    public List<DrydockRevision> Revisions { get; set; } = default!;
}

/// <summary>
/// One slot in a player's drydock, holding at most one hull. A player's drydock is the set of
/// berths they own; there is no garage row because it would only ever be a count.
/// </summary>
public sealed class DrydockBerth
{
    /// <summary>An identity integer: nothing outside the database ever names a berth.</summary>
    public int BerthId { get; set; }

    public Guid OwnerUserId { get; set; }

    public Player Owner { get; set; } = default!;

    /// <summary>
    /// The largest size class this berth accepts, stored as text for the same reason the ship's
    /// class is: a content-side taxonomy change cannot invalidate rows. Compared in code after
    /// parsing, never in SQL; an owner has a handful of berths.
    /// </summary>
    public string MaxSizeClass { get; set; } = default!;

    public DrydockBerthKind Kind { get; set; }

    /// <summary>What makes sell-back computable. Zero for a grant, so a grant refunds nothing.</summary>
    public int PricePaid { get; set; }

    public DateTime PurchasedAt { get; set; }

    public int? PurchasedRoundId { get; set; }

    public Round? PurchasedRound { get; set; }
}

public enum DrydockBerthKind
{
    /// <summary>Bought by the player at a terminal.</summary>
    Purchased = 0,

    /// <summary>
    /// Handed out by the import bridge, a starter grant, or an admin. Refunds nothing on sale,
    /// so a grant can never be turned into credits.
    /// </summary>
    Granted = 1,
}

public enum DrydockShipState
{
    /// <summary>In the drydock and retrievable.</summary>
    Stored = 0,

    /// <summary>Materialized into a round. A second retrieve must refuse rather than hand out a copy.</summary>
    CheckedOut = 1,

    /// <summary>
    /// An administrative freeze pending adjudication. Refuses retrieve without any machine having
    /// decided anything, which is the point: the system records and a person adjudicates. The
    /// impound fields on <see cref="DrydockShip"/> carry what it was taken for and what it costs
    /// to get back.
    /// </summary>
    Impounded = 2,

    /// <summary>
    /// Offered to another player and waiting on their answer. The ship keeps its berth and
    /// refuses retrieve, sale, rename and move until the offer resolves; the pending
    /// <see cref="DrydockTransfer"/> row says to whom and until when.
    /// </summary>
    InEscrow = 3,

    /// <summary>
    /// Scrapped by its owner for credits, and the berth is freed. Terminal, and pruning only runs
    /// inside a store, a promote or a re-bake (which refuses anything not stored), so a terminal
    /// ship's remaining blobs are frozen rather than
    /// decaying: an admin can undo a sale made in anger for as long as the row exists.
    /// </summary>
    Sold = 4,

    /// <summary>
    /// The hull could not have brought itself home: no piloting console, or nothing to dock with.
    /// Holds no berth, and terminal in the same sense <see cref="Sold"/> is. Says the hull is gone
    /// and never that the data is, which is what makes an admin
    /// <see cref="DrydockAuditAction.Restore"/> the way back.
    /// </summary>
    Destroyed = 5,

    /// <summary>
    /// The owner gave up an impounded hull rather than reclaim it, and was paid nothing for it.
    /// Holds no berth, and terminal in the same sense <see cref="Sold"/> is. Separate from
    /// <see cref="Destroyed"/> so that an admin reads the cause instead of inferring it: this one
    /// was somebody's choice.
    /// </summary>
    Abandoned = 6,
}

/// <summary>
/// One row per revision, kept indefinitely. This is the hull's history, and it deliberately does
/// not require a blob to exist: pruning takes blobs, never history.
/// </summary>
public sealed class DrydockRevision
{
    public Guid ShipGuid { get; set; }

    public DrydockShip Ship { get; set; } = default!;

    /// <summary>Monotonic per ship, starting at 1.</summary>
    public int Revision { get; set; }

    public DrydockRevisionKind Kind { get; set; }

    /// <summary>
    /// Which revision a re-bake or an admin promote was derived from. Null for a player store and an
    /// import.
    /// </summary>
    public int? DerivedFromRevision { get; set; }

    /// <summary>
    /// Which generation of the re-bake ladder produced this. Zero for a player store; a promote
    /// carries its source's.
    /// </summary>
    public int RebakeVersion { get; set; }

    /// <summary>
    /// Excluded from blob pruning while set: keep-N and the two-blob floor both step around a pinned
    /// revision's document, however far behind the current revision it falls. Set and cleared only
    /// through <c>DrydockStore.TryPinRevision</c> and <c>TryUnpinRevision</c>, each of which writes a
    /// timeline row. A pin protects the document, never a promise that it still loads: content can
    /// have moved since.
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>
    /// Who stored it, null for the system. Owners change hands, so this is what makes a year-old
    /// history still read correctly after a transfer.
    /// </summary>
    public Guid? ActorUserId { get; set; }

    public Player? Actor { get; set; }

    /// <summary>
    /// Nullable because a re-bake is filed without a round: the ladder is designed to run between
    /// rounds, when there is no round to point at.
    /// </summary>
    public int? CreatedRoundId { get; set; }

    public Round? CreatedRound { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The engine's map document format version, so a re-bake knows what it is holding.</summary>
    public int EngineFormatVer { get; set; }

    /// <summary>Our own sidecar and manifest encoding version, for a re-bake to migrate the same way.</summary>
    public int DrydockFormatVer { get; set; }

    /// <summary>
    /// Hash over the set of prototype ids the blob references. One of the two drift classes, and
    /// the one the re-bake sweep heals (<c>DrydockSystem.RunRebakeSweep</c>).
    /// </summary>
    public byte[] ProtoFingerprint { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Hash over the captured-state keys the fidelity layer wrote. The other drift class: a C#
    /// rename silently orphans a key, and comparing this is how that stops being silent.
    /// </summary>
    public byte[] CapturedKeyHash { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Over the uncompressed document, so a mismatch on retrieve distinguishes a storage fault
    /// from a serializer fault.
    /// </summary>
    public byte[] Checksum { get; set; } = Array.Empty<byte>();

    /// <summary>Uncompressed document size. The only source of a real blob-size distribution.</summary>
    public int SizeBytes { get; set; }

    /// <summary>
    /// What the shipyard appraised the hull at when this revision was filed, captured while the
    /// grid was still live because a stored ship has nothing left to appraise. A re-bake and a
    /// promote copy their source revision's, since neither has a live grid either. Null when nothing
    /// appraised it, and on rows filed before the column existed; a sale quotes from the current
    /// revision.
    /// </summary>
    public int? AppraisedValue { get; set; }

    /// <summary>
    /// What was aboard, one entry per entity in walk order. Minified JSON held as plain text on
    /// both providers, written on every store and read by nothing yet: <c>DrydockManifest
    /// .Deserialize</c> has no production caller. No index until a query exists, because
    /// <c>jsonb_ops</c> indexes every key and every scalar, and each entry carries a
    /// <c>CapturedKeys</c> list, so a capital hull would pay tens of thousands of index
    /// insertions inside the store transaction for nobody. A GIN over <c>((manifest)::jsonb)</c>
    /// is one statement whenever a reader turns up.
    /// </summary>
    public string Manifest { get; set; } = default!;

    public DrydockBlob? Blob { get; set; }
}

public enum DrydockRevisionKind
{
    /// <summary>A player put their ship away.</summary>
    PlayerStore = 0,

    /// <summary>The re-bake ladder rewrote an older revision to current content.</summary>
    SystemRebake = 1,

    /// <summary>
    /// Read out of a legacy ship save file by the import bridge. Present from day one because enum
    /// values persist, and adding it later would mean a migration to reinterpret existing rows.
    /// </summary>
    LegacyImport = 2,

    /// <summary>
    /// An admin promoted an older revision as the current one. Filed as a new revision derived
    /// from the old, so history stays append-only and nothing ever rewinds the pointer.
    /// </summary>
    AdminRestore = 3,
}

/// <summary>
/// The document itself, split from the revision so that pruning a blob leaves the history intact.
/// Nothing else belongs in this table.
/// </summary>
public sealed class DrydockBlob
{
    public Guid ShipGuid { get; set; }

    public int Revision { get; set; }

    public DrydockRevision RevisionRow { get; set; } = default!;

    /// <summary>
    /// Compressed by the game server, not by the database. The Postgres column is set to
    /// <c>STORAGE EXTERNAL</c> by migration so TOAST stores it out of line without trying to
    /// recompress data that arrives already compressed.
    /// </summary>
    public byte[] Blob { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// The timeline. Stores and retrieves are logged here alongside the exceptional events, which is
/// what makes it a timeline rather than an exceptions log, and what lets a diff that crosses a
/// change of hands say so.
/// </summary>
public sealed class DrydockAudit
{
    public long Id { get; set; }

    /// <summary>
    /// Deliberately NOT a foreign key: audit outlives the ship record, so a deletion through admin
    /// tooling must not cascade away the evidence of it. Null on berth events that involve no
    /// ship, which is why there is one timeline rather than two.
    /// </summary>
    public Guid? ShipGuid { get; set; }

    /// <summary>
    /// The berth taken, vacated, bought, sold, or moved. No foreign key, for the same reason as
    /// the ship. Store and retrieve rows carry it too, so the timeline says where as well as when.
    /// </summary>
    public int? BerthId { get; set; }

    /// <summary>Name snapshot, because the ship row it came from may no longer exist.</summary>
    public string? ShipName { get; set; }

    public DrydockAuditAction Action { get; set; }

    /// <summary>Who acted. Null for the system. Not a foreign key, for the same reason as above.</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Transfer recipient or restore beneficiary.</summary>
    public Guid? SubjectUserId { get; set; }

    public int? Revision { get; set; }

    public int? RoundId { get; set; }

    /// <summary>Free text. On an adjudication this is the reasoning, which is the whole point.</summary>
    public string? Reason { get; set; }

    /// <summary>Stored as UTC. Always write <c>DateTime.UtcNow</c>; a non-UTC value throws on Postgres.</summary>
    public DateTime CreatedAt { get; set; }
}

public enum DrydockAuditAction
{
    Store = 0,
    Retrieve = 1,

    /// <summary>An admin rebuilt a ship judged lost. Always a human decision, never a rule.</summary>
    Restore = 2,

    Transfer = 3,
    Delete = 4,

    /// <summary>
    /// A system re-bake was filed as the ship's new current revision. The actor is null, the
    /// revision is the one filed, and the reason names the revision it was derived from.
    /// </summary>
    Rebake = 5,

    /// <summary>
    /// Took the hull into the impound lot. The actor is the admin, or null for the round-end sweep;
    /// the subject is the owner; the berth is the one vacated, if any; the reason carries the fee
    /// with the share and appraisal it was taken against, whether the owner may reclaim it, and how
    /// many occupants were moved off.
    /// </summary>
    Impound = 6,

    /// <summary>An admin lifted an impound into one of the owner's berths, for nothing; the berth is the one it landed in.</summary>
    ImpoundReleased = 7,

    BerthPurchase = 8,
    BerthSale = 9,
    BerthGrant = 10,
    BerthUpgrade = 11,

    /// <summary>A ship moved between two berths of the same owner, by an admin.</summary>
    BerthMove = 12,

    BerthDelete = 13,

    /// <summary>
    /// A retrieve could not use the current revision and materialized an older one. The newer
    /// document still exists until pruning reaches it, which is why this is on the timeline.
    /// </summary>
    Fallback = 14,

    /// <summary>
    /// A session sent a drydock message for a ship or berth its account does not own. The console
    /// never offers such a click, so a row here is a modified client or a forged message, and the
    /// actor and subject together are the stolen-card signal an admin looks for.
    /// </summary>
    AccessRefused = 17,

    /// <summary>The owner offered the ship to another account; subject is the recipient.</summary>
    TransferOffered = 18,

    /// <summary>The recipient turned the offer down.</summary>
    TransferDeclined = 19,

    /// <summary>The owner withdrew the offer.</summary>
    TransferCancelled = 20,

    /// <summary>The offer's clock ran out. Written by the sweep, so the actor is null.</summary>
    TransferExpired = 21,

    /// <summary>The owner scrapped the ship; the reason carries the price and the appraisal.</summary>
    ShipSold = 22,

    /// <summary>The owner renamed the ship; the reason carries old and new.</summary>
    Renamed = 23,

    /// <summary>An admin undid a sale; the reason says whether the money came back.</summary>
    SaleReversed = 24,

    /// <summary>
    /// A ship came in from the old save system. Written whatever the tamper mode, because the tamper
    /// audit stops writing entirely when the check is off and an import must still leave a trace; the
    /// reason says so when the save was not actually spent.
    /// </summary>
    Imported = 25,

    /// <summary>The owner paid the fee and the hull went back to one of their berths.</summary>
    ImpoundRedeemed = 26,

    /// <summary>
    /// The owner gave up an impounded hull rather than reclaim it. No money moved in either
    /// direction, which is the whole difference from <see cref="ShipSold"/>.
    /// </summary>
    ShipAbandoned = 27,

    /// <summary>An admin undid an abandon. The counterpart of <see cref="SaleReversed"/>, with no arithmetic.</summary>
    AbandonReversed = 28,

    /// <summary>
    /// The round-end sweep judged a hull unable to have brought itself home. A verdict, not an
    /// action taken against anyone, which is why it reads differently from <see cref="Impound"/>.
    /// </summary>
    ShipDestroyed = 29,

    /// <summary>
    /// The round-end sweep reached the hull and could not file it. The absence of this row for a
    /// ship still checked out is the other system failure, where the sweep never ran at all.
    /// </summary>
    ShipStranded = 30,

    /// <summary>
    /// A retrieve claimed the ship, failed, and put it back. Nobody decided anything and no hull
    /// ever materialized, which is why it does not share <see cref="ImpoundReleased"/>: a timeline
    /// that reads "an admin lifted an impound" for a database hiccup is worse than no row.
    /// </summary>
    ClaimReleased = 31,

    /// <summary>
    /// An admin promoted an older revision to current, filed forward as a copy. Its own row rather
    /// than <see cref="Restore"/>, which is the word for a hull judged lost: a promote changes which
    /// document a retrieve reads, never where the hull is.
    /// </summary>
    RevisionPromoted = 32,

    /// <summary>
    /// A revision's document was excluded from pruning. The revision is the one pinned, the actor
    /// is whoever pinned it (null for the system), and the reason says why.
    /// </summary>
    RevisionPinned = 33,

    /// <summary>
    /// A pin was cleared, typically once the document has been re-baked or judged unrecoverable. The
    /// next store, promote or re-bake prunes the document if keep-N and the floor no longer cover it.
    /// </summary>
    RevisionUnpinned = 34,

    /// <summary>
    /// Written by the retrieve's drift gate (<c>DrydockSystem.RefuseForDrift</c>): a retrieve refused
    /// because the document references content that no longer resolves after the migration mappings
    /// are applied, or a format outside its reader's window. The actor is the retrieving owner, the
    /// revision is the one refused, and the reason lists up to ten unresolved ids and any format out
    /// of window.
    /// </summary>
    DriftRefused = 35,

    /// <summary>
    /// Written by the retrieve wrapper once the ship is presented (<c>DrydockSystem.WriteSkippedState</c>):
    /// the hull came back with captured-state or appearance keys skipped, because nothing on the live
    /// entity answers to them any more. One row per sidecar; the reason opens with <c>captured:</c>
    /// or <c>appearance:</c> and lists up to twenty keys with why each was skipped.
    /// </summary>
    StateSkipped = 36,
}
