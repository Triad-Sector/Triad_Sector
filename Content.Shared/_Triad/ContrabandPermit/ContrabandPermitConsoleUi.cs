using Robust.Shared.Serialization;

namespace Content.Shared._Triad.ContrabandPermit;

[Serializable, NetSerializable]
public enum ContrabandPermitConsoleUi : byte
{
    Key
}

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleBuiState(NetEntity? insertedChip, string? dateTime) : BoundUserInterfaceState
{
    public NetEntity? InsertedChip = insertedChip;
    public string? DateTime = dateTime;
}

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleReasonUpdatedMessage(string reason) : BoundUserInterfaceMessage
{
    public string Reason { get; } = reason;
}

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleGrantButtonPressedMessage() : BoundUserInterfaceMessage;

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsolePrintButtonPressedMessage() : BoundUserInterfaceMessage;
