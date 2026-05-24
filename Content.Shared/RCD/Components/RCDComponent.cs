using Content.Shared.RCD.Systems;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Physics;
using Robust.Shared.Prototypes;

namespace Content.Shared.RCD.Components;

/// <summary>
/// Main component for the RCD
/// Optionally uses LimitedChargesComponent.
/// Charges can be refilled with RCD ammo
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(RCDSystem))]
public sealed partial class RCDComponent : Component
{
    /// <summary>
    /// List of RCD prototypes that the device comes loaded with
    /// </summary>
    [DataField, AutoNetworkedField]
    public HashSet<ProtoId<RCDPrototype>> AvailablePrototypes { get; set; } = new();

    /// <summary>
    /// Sound that plays when a RCD operation successfully completes
    /// </summary>
    [DataField]
    public SoundSpecifier SuccessSound { get; set; } = new SoundPathSpecifier("/Audio/Items/deconstruct.ogg");

    /// <summary>
    /// The ProtoId of the currently selected RCD prototype
    /// </summary>
    [DataField, AutoNetworkedField]
    public ProtoId<RCDPrototype> ProtoId { get; set; } = "Invalid";

    // Triad: RPD port from funky-station (PRs #62/#1244/#1338). IsRpd discriminates the RPD variant;
    // UseMirrorPrototype toggles spawning the flipped alternate (used for gas filter / mixer).
    /// <summary>
    /// Indicates whether this device is an RPD (pipe-construction variant) rather than a standard RCD.
    /// </summary>
    [DataField, AutoNetworkedField]
    public bool IsRpd { get; set; } = false;

    /// <summary>
    /// If true, the next construction uses <see cref="RCDPrototype.MirrorPrototype"/> instead of
    /// <see cref="RCDPrototype.Prototype"/> (where the prototype defines one).
    /// </summary>
    [AutoNetworkedField, ViewVariables(VVAccess.ReadOnly)]
    public bool UseMirrorPrototype = false;

    /// <summary>
    /// Selected pipe color for RPD-spawned pipes. Key is the palette slot identifier (e.g. "distro", "waste"),
    /// Color is the actual hex applied via <c>PipeColorVisualsComponent</c>. "default" leaves pipes unpainted.
    /// </summary>
    [DataField, AutoNetworkedField]
    public (string Key, Color? Color) PipeColor { get; set; } = ("default", null);
    // End Triad

    /// <summary>
    /// The direction constructed entities will face upon spawning
    /// </summary>
    [DataField, AutoNetworkedField]
    public Direction ConstructionDirection
    {
        get
        {
            return _constructionDirection;
        }
        set
        {
            _constructionDirection = value;
            ConstructionTransform = new Transform(new(), _constructionDirection.ToAngle());
        }
    }

    private Direction _constructionDirection = Direction.South;

    /// <summary>
    /// Returns a rotated transform based on the specified ConstructionDirection
    /// </summary>
    /// <remarks>
    /// Contains no position data
    /// </remarks>
    [ViewVariables(VVAccess.ReadOnly)]
    public Transform ConstructionTransform { get; private set; } = default!;

    /// <summary>
    /// Mono - delay multiplier for the RCD
    /// </summary>
    [DataField, AutoNetworkedField]
    public float DelayMultiplier = 1f;
}
