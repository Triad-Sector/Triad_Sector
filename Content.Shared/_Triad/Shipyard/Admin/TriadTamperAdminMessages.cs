using System;
using System.Collections.Generic;
using Content.Shared.Eui;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Shipyard.Admin;

[Serializable, NetSerializable]
public sealed class TriadTamperAdminEuiState : EuiStateBase
{
    public List<AuditRowDto> AuditPage { get; set; } = new();
    public int AuditTotalCount { get; set; }
    public int AuditPage_Index { get; set; }
    public int AuditPageSize { get; set; }

    // Round the feed is scoped to, and the live round, so the client can show "Round N" and enable /
    // disable the prev / next arrows (prev at >1, next at < current).
    public int SelectedRoundId { get; set; }
    public int CurrentRoundId { get; set; }

    // Distinct players with audit activity in the selected round, for the player filter dropdown.
    public List<PlayerOptionDto> RoundPlayers { get; set; } = new();

    // Active per-player permits (the legacy-onboarding bypass), shown in the audit feed strip.
    public List<PermitDto> Permits { get; set; } = new();
}

[Serializable, NetSerializable]
public sealed record PlayerOptionDto(Guid UserId, string? Name);

[Serializable, NetSerializable]
public sealed record AuditRowDto(
    long Id,
    DateTime At,
    string EventType,
    Guid PlayerUserId,
    string? PlayerName,
    string? ShipName,
    string ShipHashHex,
    string? PubkeyFingerprint,
    int? SaveTimeAppraisal,
    int? LoadTimeAppraisal,
    string? VesselId,
    string? MapId,
    // Set on admin-action rows (PermitGranted / PermitRevoked): the acting admin's resolved name.
    // Null on player save/load rows.
    string? AdminName);

[Serializable, NetSerializable]
public sealed record PermitDto(
    Guid PlayerUserId,
    string? PlayerName,
    Guid GrantedByAdmin,
    // Resolved display name of the granting admin (online session, else last-seen from the DB). Null
    // only when the id resolves to neither; the render layer falls back to a short guid.
    string? AdminName,
    DateTime GrantedAt,
    string? Notes);

[Serializable, NetSerializable]
public sealed class TriadTamperAdminRequestAuditPageMessage : EuiMessageBase
{
    public int Page { get; set; }
    public int PageSize { get; set; }

    // Round to view. The prev / next arrows send the adjacent number; the server clamps to
    // [1, current round]. Player dropdown + feed both re-scope to this round.
    public int RoundId { get; set; }

    // Round-time bounds as seconds into the round (the client parses the HH:MM:SS boxes). Either side
    // null = unbounded; the server anchors these against the round's StartDate.
    public int? RoundTimeFromSeconds { get; set; }
    public int? RoundTimeToSeconds { get; set; }

    public Guid? PlayerUserId { get; set; }
    public List<string>? EventTypes { get; set; }
    public string? ShipNameContains { get; set; }
}

[Serializable, NetSerializable]
public sealed class TriadTamperAdminGrantPermitMessage : EuiMessageBase
{
    public Guid Target { get; set; }
    public string? Notes { get; set; }
}

[Serializable, NetSerializable]
public sealed class TriadTamperAdminRevokePermitMessage : EuiMessageBase
{
    public Guid Target { get; set; }
}
