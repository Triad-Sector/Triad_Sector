using Content.Shared.Alert;
using Content.Shared.Inventory;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// Marks an item as a powered support harness for matching weapons.
/// Harnesses and weapons are paired by <see cref="SupportKey"/>, so new pairs can be defined in YAML.
/// Configures the equipped harness slot, magnetic retrieval slot, power drain, alerts, sounds, verbs, and drained movement.
/// The harness item's Clothing component must also allow the configured <see cref="HarnessSlot"/>.
[RegisterComponent, NetworkedComponent]
public sealed partial class WeapHarnComponent : Component
{
    [DataField]
    public string SupportKey = "Default";

    [DataField]
    public SlotFlags HarnessSlot = SlotFlags.BELT;

    [DataField]
    public SlotFlags RetrievalSlot = SlotFlags.SUITSTORAGE;

    [DataField]
    public float ActiveChargePerSecond = 5f;

    [DataField]
    public float HalfChargeThreshold = 0.5f;

    [DataField]
    public ProtoId<AlertPrototype> LowPowerAlert = "PoweredWeaponHarnessLowPower";

    [DataField]
    public ProtoId<AlertPrototype> DepletedAlert = "PoweredWeaponHarnessDepleted";

    [DataField]
    public SoundSpecifier? LinkSound = new SoundPathSpecifier("/Audio/Machines/chime.ogg");

    [DataField]
    public string LinkPopup = "WEAPON READY";

    [DataField]
    public bool MagneticRetrievalEnabled = true;

    [DataField]
    public string EnableMagneticRetrievalVerb = "Enable magnetic retrieval";

    [DataField]
    public string DisableMagneticRetrievalVerb = "Disable magnetic retrieval";

    [DataField]
    public string MagneticRetrievalEnabledPopup = "Harness magnetic retrieval enabled.";

    [DataField]
    public string MagneticRetrievalDisabledPopup = "Harness magnetic retrieval disabled.";

    [DataField]
    public float DrainedWalkModifier = 0.5f;

    [DataField]
    public float DrainedSprintModifier = 0.4f;

    [DataField]
    public SoundSpecifier? HalfChargeSound = new SoundPathSpecifier("/Audio/Machines/twobeep.ogg");

    [DataField]
    public SoundSpecifier? DepletedSound = new SoundPathSpecifier("/Audio/Machines/Nuke/angry_beep.ogg");

    [DataField]
    public TimeSpan NextActiveDrain;

    public bool HalfChargeWarned;

    public bool DepletedWarned;

    public bool LinkSoundPlayed;
}
