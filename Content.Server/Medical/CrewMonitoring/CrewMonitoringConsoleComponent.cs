using Content.Shared.Medical.SuitSensor;
using Robust.Shared.Audio;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.Server.Medical.CrewMonitoring;

[RegisterComponent]
[Access(typeof(CrewMonitoringConsoleSystem))]
public sealed partial class CrewMonitoringConsoleComponent : Component
{
    /// <summary>
    ///     List of all currently connected sensors to this console.
    /// </summary>
    public Dictionary<string, SuitSensorStatus> ConnectedSensors = new(); 
    
    /// <summary>
    ///     After what time sensor consider to be lost.
    /// </summary>
    [DataField("sensorTimeout"), ViewVariables(VVAccess.ReadWrite)]
    public float SensorTimeout = 10f;

    /// <summary>
    ///     Triad - Used for the time for the next sound to play.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan? NextSound = null;

    /// <summary>
    ///     Triad - Used as the cooldown time for the next sound.
    /// </summary>
    [DataField]
    public TimeSpan Cooldown = TimeSpan.FromSeconds(15);

    /// <summary>
    ///    Triad - Used as the warning sound for the console.
    /// </summary>
    [DataField]
    public SoundSpecifier? WarningSound;

    /// <summary>
    ///    Triad - Used as the delay for multiple people to be processed for the console.
    /// </summary>
    [DataField]
    public TimeSpan ProcessDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    ///    Triad - Used as the damage threshold for the sound
    /// </summary>
    [DataField]
    public int TriggerSndDamageThreshold = 100;

    /// <summary>
    ///     Triad - Used as a saved version of the last time a sound was sent for multiple deaths.
    /// </summary>
    [DataField]
    public TimeSpan? MultipleDeathsTime = null;
}
