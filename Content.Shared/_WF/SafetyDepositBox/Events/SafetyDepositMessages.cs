using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._WF.SafetyDepositBox.Events;

/// <summary>
/// Message to purchase a new safety deposit box.
/// </summary>
[Serializable, NetSerializable]
public sealed class SafetyDepositPurchaseMessage : BoundUserInterfaceMessage
{
    public string BoxProtoId;

    public SafetyDepositPurchaseMessage(string boxProtoId)
    {
        BoxProtoId = boxProtoId;
    }
}

/// <summary>
/// Message to deposit a box into the console.
/// </summary>
[Serializable, NetSerializable]
public sealed class SafetyDepositDepositMessage : BoundUserInterfaceMessage
{
}

/// <summary>
/// Message to withdraw a specific box from storage.
/// </summary>
[Serializable, NetSerializable]
public sealed class SafetyDepositWithdrawMessage : BoundUserInterfaceMessage
{
    public Guid BoxId;

    public SafetyDepositWithdrawMessage(Guid boxId)
    {
        BoxId = boxId;
    }
}

/// <summary>
/// Message to reclaim a lost box (delete old record and spawn new empty box).
/// </summary>
[Serializable, NetSerializable]
public sealed class SafetyDepositReclaimMessage : BoundUserInterfaceMessage
{
    public Guid BoxId;

    public SafetyDepositReclaimMessage(Guid boxId)
    {
        BoxId = boxId;
    }
}

/// <summary>
/// Message to remove/delete a safety deposit box permanently.
/// Allowed only if box is deposited (in database) or lost (missing).
/// </summary>
[Serializable, NetSerializable]
public sealed class SafetyDepositRemoveMessage : BoundUserInterfaceMessage
{
    public Guid BoxId;

    public SafetyDepositRemoveMessage(Guid boxId)
    {
        BoxId = boxId;
    }
}
