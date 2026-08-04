using Robust.Shared.Audio;
using Robust.Shared.Map;

namespace Content.Server._Triad.Supermatter;

/// <summary>
/// Gives every mob on one map paracusia for a fixed duration, then takes it back off them.
/// Started by the supermatter crystal when it delaminates.
/// </summary>
/// <remarks>
/// This is deliberately not a <c>StationEvent</c>. It is only ever started by the crystal, so it
/// has no business sitting in the random event pool, and it needs a map to scope itself to, which
/// the scheduler has no way to supply.
/// </remarks>
[RegisterComponent]
public sealed partial class SupermatterHallucinationRuleComponent : Component
{
    /// <summary>
    /// How long the hallucinations last before the rule ends and cleans up after itself.
    /// </summary>
    [DataField]
    public TimeSpan Duration = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Minimum time in seconds between paracusia incidents.
    /// </summary>
    [DataField]
    public float MinTimeBetweenIncidents = 0.1f;

    /// <summary>
    /// Maximum time in seconds between paracusia incidents.
    /// </summary>
    [DataField]
    public float MaxTimeBetweenIncidents = 300f;

    /// <summary>
    /// How far away a paracusia sound can be placed from the listener.
    /// </summary>
    [DataField]
    public float MaxSoundDistance = 7f;

    [DataField]
    public SoundSpecifier Sounds = new SoundCollectionSpecifier("Paracusia");

    /// <summary>
    /// Only mobs on this map are affected. The starting code is expected to set this before the
    /// rule starts; a null map affects every mob in the round, which is almost never wanted.
    /// </summary>
    [ViewVariables]
    public MapId? TargetMap;

    /// <summary>
    /// When <see cref="Duration"/> runs out. Set on start.
    /// </summary>
    [ViewVariables]
    public TimeSpan EndTime;

    /// <summary>
    /// Mobs this rule added paracusia to, and only those. Mobs that already had paracusia when the
    /// rule started are excluded so ending the rule can't strip someone's paracusia trait.
    /// </summary>
    [ViewVariables]
    public List<EntityUid> AffectedEntities = new();
}
