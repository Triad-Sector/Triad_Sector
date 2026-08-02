using Robust.Shared.Serialization;

namespace Content.Shared._Triad.ContrabandPermit;

[Serializable, NetSerializable]
public enum ContrabandPermitConsoleUi : byte
{
    Key
}

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleBuiState(NetEntity? insertedChip, string? dateTime, ContrabandPermitConsoleEntry[] entries, PermitEntryFocusData? focusData) : BoundUserInterfaceState
{
    public NetEntity? InsertedChip = insertedChip;
    public string? DateTime = dateTime;
    public ContrabandPermitConsoleEntry[] Entries = entries;
    public PermitEntryFocusData? FocusData = focusData;
}

[Serializable, NetSerializable]
public struct ContrabandPermitConsoleEntry(NetEntity owner, List<NetEntity> items)
{
    /// <summary>
    /// Owner of the permit(s) that the UI will read from
    /// </summary>
    public NetEntity Owner = owner;

    /// <summary>
    /// Items that the owner has valid permits for.
    /// </summary>
    public List<NetEntity> Items = items;
}

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleReasonUpdatedMessage(string reason) : BoundUserInterfaceMessage
{
    public string Reason { get; } = reason;
}

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleGrantButtonPressedMessage() : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleRevokeButtonPressedMessage(string reason) : BoundUserInterfaceMessage
{
    public string Reason = reason;
}

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsolePrintButtonPressedMessage() : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleFocusChangeMessage(NetEntity? focusedOwner, NetEntity? focusedItem) : BoundUserInterfaceMessage
{
    public NetEntity? FocusedOwner = focusedOwner;
    public NetEntity? FocusedItem = focusedItem;
}
