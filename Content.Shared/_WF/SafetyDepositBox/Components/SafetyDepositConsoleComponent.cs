using Content.Shared.Containers.ItemSlots;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._WF.SafetyDepositBox.Components;

/// <summary>
/// Console for purchasing, depositing, and withdrawing safety deposit boxes.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class SafetyDepositConsoleComponent : Component
{
    /// <summary>
    /// Slot for depositing/withdrawing boxes.
    /// </summary>
    [DataField]
    public ItemSlot BoxSlot = new();

    public static string BoxSlotId = "safety-deposit-console-boxSlot";

    [DataField]
    public SoundSpecifier ErrorSound =
        new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg");

    [DataField]
    public SoundSpecifier ConfirmSound =
        new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");
}
