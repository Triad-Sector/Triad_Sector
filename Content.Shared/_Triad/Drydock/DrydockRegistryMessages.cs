// Triad: the drydock registry, the shuttle records console's read-only view of the drydock.
using System;
using System.Collections.Generic;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Drydock;

/// <summary>
/// Where a hull is, as a dock clerk would know it. Pending transfers and deregistered hulls are
/// folded in: a transfer is a Stored hull with a flag, and a sold, destroyed or abandoned hull is
/// Deregistered without saying which.
/// </summary>
[Serializable, NetSerializable]
public enum DrydockRegistryStatus : byte
{
    Stored,
    Underway,
    Impounded,
    Deregistered,
}

/// <summary>One hull on the registry: nothing an account, an admin or the timeline would add.</summary>
[Serializable, NetSerializable]
public sealed record DrydockRegistryShipInfo(
    Guid ShipGuid,
    string Name,
    string? SizeClass,
    // The character recorded as the hull's captain, or null for a hull not stored since the column shipped.
    string? CaptainName,
    DrydockRegistryStatus Status,
    // Only while Stored.
    int? BerthId,
    bool TransferPending,
    // Only while Impounded.
    int ImpoundFee,
    string? ImpoundReason);

/// <summary>
/// Asks for a page of the registry. Search matches the captain's name and the ship's name, callsign
/// included; never an account. A null status is every hull.
/// </summary>
[Serializable, NetSerializable]
public sealed class DrydockRegistryRequestPageMessage : BoundUserInterfaceMessage
{
    public readonly string? Search;
    public readonly DrydockRegistryStatus? Status;
    public readonly int Page;

    public DrydockRegistryRequestPageMessage(string? search, DrydockRegistryStatus? status, int page)
    {
        Search = search;
        Status = status;
        Page = page;
    }
}

/// <summary>The page, sent to the viewer who asked for it rather than published as console state.</summary>
[Serializable, NetSerializable]
public sealed class DrydockRegistryPageMessage : BoundUserInterfaceMessage
{
    public readonly List<DrydockRegistryShipInfo> Ships;
    public readonly int Total;
    public readonly int Page;
    public readonly int PageSize;

    public DrydockRegistryPageMessage(List<DrydockRegistryShipInfo> ships, int total, int page, int pageSize)
    {
        Ships = ships;
        Total = total;
        Page = page;
        PageSize = pageSize;
    }
}
