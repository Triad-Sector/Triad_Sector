using Content.Shared.DoAfter;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;

namespace Content.Shared.MagicMirror;

/// <summary>
/// Allows humanoids to change their appearance mid-round.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class MagicMirrorComponent : Component
{
    // Triad: DoAfterId is a runtime handle (user + index into their live do-after list) with no type
    // serializer, so persisting it throws "No type serializer or data definition found" and kills the
    // whole ship-grid save once it goes non-null. Runtime-only state now.
    /*
    [DataField]
    */
    [ViewVariables]
    public DoAfterId? DoAfter;
    // End Triad

    /// <summary>
    /// Magic mirror target, used for validating UI messages.
    /// </summary>
    [DataField, AutoNetworkedField]
    public EntityUid? Target;

    /// <summary>
    /// Do after time to add a new slot, adding hair to a person
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan AddSlotTime = TimeSpan.FromSeconds(7);

    /// <summary>
    /// Do after time to remove a slot, removing hair from a person
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan RemoveSlotTime = TimeSpan.FromSeconds(7);

    /// <summary>
    /// Do after time to change a person's hairstyle
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan SelectSlotTime = TimeSpan.FromSeconds(7);

    /// <summary>
    /// Do after time to change a person's hair color
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan ChangeSlotTime = TimeSpan.FromSeconds(7);

    /// <summary>
    /// Sound emitted when slots are changed
    /// </summary>
    [DataField, ViewVariables(VVAccess.ReadWrite)]
    public SoundSpecifier? ChangeHairSound = new SoundPathSpecifier("/Audio/Items/scissors.ogg");
}
