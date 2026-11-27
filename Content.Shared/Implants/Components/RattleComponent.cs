using Content.Shared._EinsteinEngines.Language;
using System.Threading;
using Content.Shared.Radio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Implants.Components;

[RegisterComponent, NetworkedComponent]
public sealed partial class RattleComponent : Component
{
    // The radio channel the message will be sent to
    [DataField]
    public ProtoId<RadioChannelPrototype> RadioChannel = "Syndicate";

    // Mono - Language of message on rattle
    [DataField]
    public ProtoId<LanguagePrototype> Language = "TauCetiBasic";

    // The message that the implant will send when crit
    [DataField]
    public LocId CritMessage = "deathrattle-implant-critical-message";

    // The message that the implant will send when dead
    [DataField("deathMessage")]
    public LocId DeathMessage = "deathrattle-implant-dead-message";

    // The message that the implant will send when revived from death // Mono
    [DataField("reviveMessage")]
    public LocId ReviveMessage = "deathrattle-implant-revive-message";

    // Triad: Add recurring rattle and backup rattle
    // The message that the implant will send IF they are still dead
    [DataField]
    public LocId StillDeadMessage = "deathrattle-implant-still-dead-message";

    // The time of death of the person.
    [DataField]
    public TimeSpan DeathTime = TimeSpan.Zero;

    // The time of next trigger.
    [DataField]
    public TimeSpan NextTrigger = TimeSpan.Zero;

    // The duration of the trigger's delay.
    [DataField]
    public TimeSpan RetriggerDelay = TimeSpan.FromMinutes(1); // Set to 1 for testing, change to 5 before merge

    // Whether the backup trigger mechanic is enabled
    [DataField]
    public bool BackupTrigger = false;

    // The channel the backup trigger will be broadcast on
    [DataField]
    public ProtoId<RadioChannelPrototype> BackupRadioChannel = "Common";

    // The time to start backup triggers.
    [DataField]
    public TimeSpan StartBackupTrigger = TimeSpan.Zero;

    // The delay from death upon which the backup trigger activates
    [DataField]
    public TimeSpan BackupTriggerDelay = TimeSpan.FromMinutes(1); // Set to 1 for testing, change to 10 before merge

    // End Triad
}
