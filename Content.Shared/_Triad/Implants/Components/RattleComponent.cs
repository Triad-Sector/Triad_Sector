using Content.Shared._EinsteinEngines.Language;
using System.Threading;
using Content.Shared.Radio;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared.Implants.Components;

[NetworkedComponent] // Register component is in Content.Shared/Implants/Components
public sealed partial class RattleComponent : Component
{
    [DataField]
    public ProtoId<LanguagePrototype> Language = "TauCetiBasic";

    /// <summary>
    ///     The message that the implant will send when revived from death
    /// </summary>
    [DataField]
    public LocId ReviveMessage = "deathrattle-implant-revive-message";

    /// <summary>
    ///     The message that the implant will send IF they are still dead after a timed delay
    /// </summary>
    [DataField]
    public LocId StillDeadMessage = "deathrattle-implant-still-dead-message";

    /// <summary>
    ///     The cached time of death of the person.
    /// </summary>
    [DataField]
    public TimeSpan DeathTime = TimeSpan.Zero;

    /// <summary>
    ///     The time of next trigger.
    /// </summary>
    [DataField]
    public TimeSpan NextTrigger = TimeSpan.Zero;

    /// <summary>
    ///     The duration of the trigger's delay.
    /// </summary>
    [DataField]
    public TimeSpan RetriggerDelay = TimeSpan.FromMinutes(5);

    /// <summary>
    ///     Whether the backup trigger mechanic is enabled
    /// </summary>
    [DataField]
    public bool BackupTrigger = false;

    [DataField]
    public ProtoId<RadioChannelPrototype> BackupRadioChannel = "Common";

    [DataField]
    public TimeSpan BackupTriggerThreshold = TimeSpan.Zero;

    [DataField]
    public TimeSpan BackupTriggerDelay = TimeSpan.FromMinutes(12);
}
