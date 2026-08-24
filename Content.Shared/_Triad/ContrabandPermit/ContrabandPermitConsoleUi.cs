using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Robust.Shared.Enums;
using Robust.Shared.Prototypes;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.ContrabandPermit;

[Serializable, NetSerializable]
public enum ContrabandPermitConsoleUi : byte
{
    Key
}

[Serializable, NetSerializable]
public sealed class ContrabandPermitConsoleBuiState(NetEntity? insertedChip, string? dateTime, EntProtoId? scannedItemProtoId, ContrabandPermitConsoleBuiStateOwnerInfo? ownerInfo, ContrabandPermitConsoleEntry[] entries, PermitEntryFocusData? focusData) : BoundUserInterfaceState
{
    public NetEntity? InsertedChip = insertedChip;
    public string? DateTime = dateTime;
    public EntProtoId? ScannedItemProtoId = scannedItemProtoId;
    public ContrabandPermitConsoleBuiStateOwnerInfo? OwnerInfo = ownerInfo;
    public ContrabandPermitConsoleEntry[] Entries = entries;
    public PermitEntryFocusData? FocusData = focusData;
}

[Serializable, NetSerializable]
public struct ContrabandPermitConsoleBuiStateOwnerInfo(string scannedOwnerName, string scannedOwnerSpecies, int scannedOwnerAge, Gender scannedOwnerGender, string scannedOwnerEyeColor)
{
    public string ScannedOwnerName = scannedOwnerName;
    public string ScannedOwnerSpecies = scannedOwnerSpecies;
    public int ScannedOwnerAge = scannedOwnerAge;
    public Gender ScannedOwnerGender = scannedOwnerGender;
    public string ScannedOwnerEyeColor = scannedOwnerEyeColor;
}

[Serializable, NetSerializable]
public struct ContrabandPermitConsoleEntry(ContrabandPermitConsoleOwner owner, List<ContrabandPermitConsoleItem> items)
{
    /// <summary>
    /// Owner of the permit(s) that the UI will read from
    /// </summary>
    public ContrabandPermitConsoleOwner Owner = owner;

    /// <summary>
    /// Items that the owner has valid permits for.
    /// </summary>
    public List<ContrabandPermitConsoleItem> Items = items;
}

[Serializable, NetSerializable]
public struct ContrabandPermitConsoleOwner(NetEntity owner, string name, HumanoidCharacterAppearance appearance, int age, Gender gender, Sex sex, ProtoId<SpeciesPrototype> species, List<EntProtoId> loadout)
{
    /// <summary>
    /// NetEntity of the permit owner.
    /// </summary>
    public NetEntity Owner = owner;

    /// <summary>
    /// Name of the permit owner
    /// </summary>
    public string Name = name;

    /// <summary>
    /// Character appearance of the permit owner
    /// </summary>
    public HumanoidCharacterAppearance Appearance = appearance;

    /// <summary>
    /// Age of the permit owner
    /// </summary>
    public int Age = age;

    /// <summary>
    /// Gender of the permit owner
    /// </summary>
    public Gender Gender = gender;

    /// <summary>
    /// Sex of the permit owner
    /// </summary>
    public Sex Sex = sex;

    /// <summary>
    /// species of the permit owner
    /// </summary>
    public ProtoId<SpeciesPrototype> Species = species;

    /// <summary>
    /// Items to equip to the dummy entity in the console UI.
    /// </summary>
    public List<EntProtoId> ItemsToEquip = loadout;
}

[Serializable, NetSerializable]
public struct ContrabandPermitConsoleItem(NetEntity item, string permitReason, string dateGranted, EntProtoId itemPrototype)
{
    /// <summary>
    /// NetEntity of the permit item.
    /// </summary>
    public NetEntity Item = item;

    /// <summary>
    /// Reason of the permit item
    /// </summary>
    public string PermitReason = permitReason;

    /// <summary>
    /// Date granted RP flavor string that the permit has
    /// </summary>
    public string DateGranted = dateGranted;

    /// <summary>
    /// EntProtoId of the permit item
    /// </summary>
    public EntProtoId Prototype = itemPrototype;
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
public sealed class ContrabandPermitConsoleFocusChangeMessage(ContrabandPermitConsoleOwner? focusedOwner, ContrabandPermitConsoleItem? focusedItem) : BoundUserInterfaceMessage
{
    public ContrabandPermitConsoleOwner? FocusedOwner = focusedOwner;
    public ContrabandPermitConsoleItem? FocusedItem = focusedItem;
}
