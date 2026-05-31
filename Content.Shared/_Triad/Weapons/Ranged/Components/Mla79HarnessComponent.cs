using Content.Shared.Alert;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.Weapons.Ranged.Components;

/// <summary>
/// Marks a belt item as a powered support harness for the MLA-79 smartgun.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class Mla79HarnessComponent : Component
{
    [DataField]
    public float ActiveChargePerSecond = 5f;

    [DataField]
    public float HalfChargeThreshold = 0.5f;

    [DataField]
    public ProtoId<AlertPrototype> LowPowerAlert = "Mla79HarnessLowPower";

    [DataField]
    public ProtoId<AlertPrototype> DepletedAlert = "Mla79HarnessDepleted";

    [DataField]
    public SoundSpecifier? LinkSound = new SoundPathSpecifier("/Audio/Machines/chime.ogg");

    [DataField]
    public string LinkPopup = "GUN READY";

    [DataField]
    public bool MagneticRetrievalEnabled = true;

    [DataField]
    public string EnableMagneticRetrievalVerb = "Enable magnetic retrieval";

    [DataField]
    public string DisableMagneticRetrievalVerb = "Disable magnetic retrieval";

    [DataField]
    public string MagneticRetrievalEnabledPopup = "MLA-79 magnetic retrieval enabled.";

    [DataField]
    public string MagneticRetrievalDisabledPopup = "MLA-79 magnetic retrieval disabled.";

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
