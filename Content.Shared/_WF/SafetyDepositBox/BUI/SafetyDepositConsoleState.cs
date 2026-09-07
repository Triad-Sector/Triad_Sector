using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._WF.SafetyDepositBox.BUI;

/// <summary>
/// State of the safety deposit console UI
/// </summary>
[Serializable, NetSerializable]
public sealed class SafetyDepositConsoleState : BoundUserInterfaceState
{
    /// <summary>
    /// List of boxes owned by the current user.
    /// </summary>
    public List<SafetyDepositBoxInfo> OwnedBoxes = new();

    /// <summary>
    /// Amount of cash currently inserted in the console.
    /// </summary>
    public int InsertedCash;

    /// <summary>
    /// Is there a box currently in the box slot?
    /// </summary>
    public bool HasBoxInSlot;

    /// <summary>
    /// Info about the box in the slot, if any.
    /// </summary>
    public SafetyDepositBoxInfo? BoxInSlot;

    /// <summary>
    /// Available box types for purchase with their costs and prototype IDs.
    /// </summary>
    public List<BoxTypeInfo> AvailableBoxTypes = new();

    /// <summary>
    /// The current round ID, used to determine if boxes are lost.
    /// </summary>
    public int CurrentRoundId;

    // Triad Start - Add bank balance for inspect-before-buy affordability comparison on right detail panel
    /// <summary>
    /// Player bank balance for affordability display.
    /// </summary>
    public int BankBalance;

    public SafetyDepositConsoleState(
        List<SafetyDepositBoxInfo> ownedBoxes,
        int insertedCash,
        bool hasBoxInSlot,
        SafetyDepositBoxInfo? boxInSlot,
        List<BoxTypeInfo> availableBoxTypes,
        int currentRoundId,
        int bankBalance = 0)
    {
        OwnedBoxes = ownedBoxes;
        InsertedCash = insertedCash;
        HasBoxInSlot = hasBoxInSlot;
        BoxInSlot = boxInSlot;
        AvailableBoxTypes = availableBoxTypes;
        CurrentRoundId = currentRoundId;
        BankBalance = bankBalance;
    }
    // Triad End
}

/// <summary>
/// Information about a box type available for purchase.
/// </summary>
[Serializable, NetSerializable]
public record struct BoxTypeInfo(
    string ProtoId,
    string Name,
    string Description,
    int Cost
);

/// <summary>
/// Information about a safety deposit box.
/// </summary>
[Serializable, NetSerializable]
public record struct SafetyDepositBoxInfo(
    Guid BoxId,
    string OwnerName,
    bool IsDeposited,
    string? Nickname,
    string ProtoId,
    DateTime? LastWithdrawn,
    int? LastWithdrawnRoundId
);
