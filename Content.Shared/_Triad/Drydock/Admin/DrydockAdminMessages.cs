using System;
using System.Collections.Generic;
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Drydock.Admin;

/// <summary>
/// What the drydock admin panel shows: a filtered page of hulls, the one that is selected with its
/// history and timeline, and the selected owner's berths. Everything an admin decides about a
/// ship is decided from here, so this state carries every fact those decisions need, resolved to
/// names where a person will read them.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockAdminEuiState : EuiStateBase
{
    public List<DrydockAdminShipDto> Ships { get; set; } = new();
    public int TotalShips { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public int CurrentRoundId { get; set; }

    public DrydockAdminShipDetailDto? Selected { get; set; }

    /// <summary>The selected ship's owner's berths, occupants included, for restore and move targets.</summary>
    public List<DrydockAdminBerthDto> OwnerBerths { get; set; } = new();

    /// <summary>The outcome of the last action, for the footer. Null when nothing has happened yet.</summary>
    public string? Notice { get; set; }

    /// <summary>
    /// The fee share the impound dialog opens at: the round-end sweep's rate, so an admin who
    /// impounds by hand charges what the sweep would have unless they move the slider. The cvar
    /// behind it is server-only, so the client has to be told.
    /// </summary>
    public int ImpoundDefaultPercent { get; set; } = 50;
}

[Serializable, NetSerializable]
public sealed record DrydockAdminShipDto(
    Guid ShipGuid,
    string Name,
    Guid OwnerUserId,
    string? OwnerName,
    string State,
    string? SizeClass,
    string? VesselProto,
    int? BerthId,
    int? LastBerthId,
    int? CheckedOutRoundId,
    DateTime StateChangedAt,
    int CurrentRevision,
    // A grid carrying this hull's id exists in the current round. Restore is refused while true.
    bool LiveThisRound,
    // While in escrow: when the standing offer runs out, so the row can carry a clock.
    DateTime? EscrowExpiresAt,
    // The impound terms. Meaningful only while the state is Impounded; on any other state they are
    // the last impound's residue, which the row deliberately keeps.
    int ImpoundFee = 0,
    string? ImpoundReason = null,
    bool ImpoundRedeemable = false,
    // What the last sale paid, so a Sold row can carry the figure the canvas draws on it. Null when
    // the ship has never been sold; kept on any state, like the impound residue above.
    int? LastSalePrice = null);

[Serializable, NetSerializable]
public sealed record DrydockAdminRevisionDto(
    int Revision,
    string Kind,
    DateTime CreatedAt,
    int? RoundId,
    Guid? ActorUserId,
    string? ActorName,
    int SizeBytes,
    // Pruning takes blobs and never history; a revision without one is history only.
    bool HasBlob,
    int? DerivedFromRevision,
    int? AppraisedValue);

[Serializable, NetSerializable]
public sealed record DrydockAdminAuditDto(
    long Id,
    DateTime At,
    string Action,
    Guid? ActorUserId,
    string? ActorName,
    Guid? SubjectUserId,
    string? SubjectName,
    int? Revision,
    int? BerthId,
    int? RoundId,
    string? Reason,
    // The name the ship had when the row was written, so a rename shows its old name in place.
    string? ShipName);

[Serializable, NetSerializable]
public sealed record DrydockAdminBerthDto(
    int BerthId,
    string MaxSizeClass,
    string Kind,
    int PricePaid,
    Guid? OccupantShipGuid,
    string? OccupantName,
    string? OccupantSizeClass,
    string? OccupantState);

/// <summary>The standing offer on a ship in escrow, as the escrow card draws it.</summary>
[Serializable, NetSerializable]
public sealed record DrydockAdminEscrowDto(
    long TransferId,
    Guid FromUserId,
    string? FromName,
    Guid ToUserId,
    string? ToName,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    // The recipient's berth it would land in if accepted now, or null when nothing of theirs fits.
    int? LandsInBerthId);

/// <summary>The most recent sale of a sold ship, for the restore-from-sale dialog.</summary>
[Serializable, NetSerializable]
public sealed record DrydockAdminSaleDto(
    int Price,
    DateTime At,
    // The owner's current balance, from their session or their saved character, or null when
    // neither could be read. The dialog unticks "take the money back" when it cannot cover.
    int? OwnerBalance);

[Serializable, NetSerializable]
public sealed record DrydockAdminShipDetailDto(
    DrydockAdminShipDto Ship,
    string? AdminNotes,
    List<DrydockAdminRevisionDto> Revisions,
    List<DrydockAdminAuditDto> Timeline,
    DrydockAdminEscrowDto? Escrow,
    DrydockAdminSaleDto? LastSale);

[Serializable, NetSerializable]
public sealed class DrydockAdminRequestPageMessage : EuiMessageBase
{
    public int Page { get; set; }
    public int PageSize { get; set; }

    /// <summary>
    /// One box: a character name from any of the owner's slots, an account name, a ship name
    /// including any name it used to have (the callsign is part of it), a ship id, or an account
    /// id. The server decides which it was.
    /// </summary>
    public string? Search { get; set; }

    /// <summary>
    /// A <c>DrydockShipState</c> name, or the flag "Stranded" (checked out in a round that is not
    /// the current one), or null for every state.
    /// </summary>
    public string? Chip { get; set; }
}

[Serializable, NetSerializable]
public sealed class DrydockAdminSelectShipMessage : EuiMessageBase
{
    public Guid? ShipGuid { get; set; }
}

/// <summary>
/// Take a hull into the impound lot, from its berth or from the world. The dialog behind it is the
/// only place the terms are chosen; the server decides which of the two takings applies.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockAdminImpoundMessage : EuiMessageBase
{
    public Guid ShipGuid { get; set; }

    /// <summary>Shown to the owner, and written on the timeline row.</summary>
    public string? Reason { get; set; }

    /// <summary>
    /// Share of the appraisal owed, 0 to 100. A percent rather than credits, so a fee above what the
    /// hull is worth cannot be sent; the server still clamps, because the client is not trusted.
    /// </summary>
    public int FeePercent { get; set; }

    /// <summary>
    /// Whether the owner may reclaim or abandon it themselves. Defaults true, which is the courtesy
    /// case: an admin who means "frozen until I say otherwise" has to say so.
    /// </summary>
    public bool Redeemable { get; set; } = true;
}

/// <summary>
/// Lift an impound into one of the owner's berths without naming which: the hull's last berth if it
/// is free and fits, else the smallest free berth that fits. Naming a berth is the restore message.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockAdminReleaseImpoundMessage : EuiMessageBase
{
    public Guid ShipGuid { get; set; }
    public string? Reason { get; set; }
}

[Serializable, NetSerializable]
public sealed class DrydockAdminNotesMessage : EuiMessageBase
{
    public Guid ShipGuid { get; set; }
    public string? Notes { get; set; }
}

[Serializable, NetSerializable]
public sealed class DrydockAdminRestoreMessage : EuiMessageBase
{
    public Guid ShipGuid { get; set; }
    public int BerthId { get; set; }
    public string? Reason { get; set; }
}

/// <summary>Undo a sale: the ship returns to a berth, and by default the price is taken back.</summary>
[Serializable, NetSerializable]
public sealed class DrydockAdminRestoreFromSaleMessage : EuiMessageBase
{
    public Guid ShipGuid { get; set; }
    public int BerthId { get; set; }
    public bool TakeMoneyBack { get; set; }
    public string? Reason { get; set; }
}

/// <summary>An admin withdraws someone else's standing offer. The ship leaves escrow.</summary>
[Serializable, NetSerializable]
public sealed class DrydockAdminCancelOfferMessage : EuiMessageBase
{
    public long TransferId { get; set; }
    public string? Reason { get; set; }
}

[Serializable, NetSerializable]
public sealed class DrydockAdminMoveMessage : EuiMessageBase
{
    public Guid ShipGuid { get; set; }

    /// <summary>Null vacates the berth a ship that is out is still shown in.</summary>
    public int? BerthId { get; set; }

    public string? Reason { get; set; }
}

[Serializable, NetSerializable]
public sealed class DrydockAdminGrantBerthMessage : EuiMessageBase
{
    public Guid OwnerUserId { get; set; }

    /// <summary>A <c>ShipSizeClass</c> name.</summary>
    public string MaxSizeClass { get; set; } = string.Empty;
}

[Serializable, NetSerializable]
public sealed class DrydockAdminDeleteBerthMessage : EuiMessageBase
{
    public int BerthId { get; set; }
}

[Serializable, NetSerializable]
public sealed class DrydockAdminPromoteRevisionMessage : EuiMessageBase
{
    public Guid ShipGuid { get; set; }
    public int Revision { get; set; }
    public string? Reason { get; set; }
}

[Serializable, NetSerializable]
public sealed class DrydockAdminDeleteShipMessage : EuiMessageBase
{
    public Guid ShipGuid { get; set; }
    public string? Reason { get; set; }
}
