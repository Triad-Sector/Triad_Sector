using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad - for TimeOffsetSerializer

namespace Content.Shared._Mono;

/// <summary>
/// Component that applies Pacified status to all organic entities on a grid.
/// Entities with company affiliations matching the exempt companies will not be pacified.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentPause]
public sealed partial class GridPacifiedComponent : Component
{
    /// <summary>
    /// A check for if an entity is pre-pacified, such as by having the pacified trait.
    /// </summary>
    [DataField]
    public bool PrePacified = false;

    /// <summary>
    /// Until what time an entity will be pacified for. The component is removed when this is exceeded.
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan PacifiedTime; // Triad - TimeOffsetSerializer: absolute game time, re-based on load so a saved ship does not carry the previous server clock

    /// <summary>
    /// The time when the next periodic update should occur
    /// </summary>
    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
    public TimeSpan NextUpdate; // Triad - TimeOffsetSerializer: absolute game time, re-based on load so a saved ship does not carry the previous server clock

    /// <summary>
    /// How frequently to check the entity for changes
    /// </summary>
    [DataField]
    public TimeSpan UpdateInterval = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The radius from a GridPacifier entity that a GridPacified entity is pacified.
    /// </summary>
    [DataField]
    public float PacifyRadius = 256f;
}
