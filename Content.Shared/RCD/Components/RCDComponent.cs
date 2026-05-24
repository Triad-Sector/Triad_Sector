using Content.Shared.RCD.Systems;
using Content.Shared.RPD.Systems;
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
[Access(typeof(RCDSystem), typeof(RPDSystem))]
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

    // Triad: MirrorPrototype is a general RCD feature (asymmetric recipes); the RPD-specific state lives on
    // RPDComponent in Content.Shared.RPD. See Resources/Prototypes/_Triad/RPD/ for the RPD subsystem.
    /// <summary>
    /// If true, the next construction uses <see cref="RCDPrototype.MirrorPrototype"/> instead of
    /// <see cref="RCDPrototype.Prototype"/> (where the prototype defines one).
    /// </summary>
    [AutoNetworkedField, ViewVariables(VVAccess.ReadOnly)]
    public bool UseMirrorPrototype = false;
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
