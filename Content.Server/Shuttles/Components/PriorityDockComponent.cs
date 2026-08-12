using Content.Shared.Tag;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom.Prototype;

namespace Content.Server.Shuttles.Components;

/// <summary>
/// Given priority when considering where to dock.
/// </summary>
[RegisterComponent]
public sealed partial class PriorityDockComponent : Component
{
    /// <summary>
    /// Tag to match on the docking request, if this dock is to be prioritised.
    /// </summary>
    [ViewVariables(VVAccess.ReadWrite),
     DataField("tag", customTypeSerializer: typeof(PrototypeIdSerializer<TagPrototype>))]
    public string? Tag;

    /// <summary>
    /// When set on a station-side dock, only shuttle ports whose own PriorityDock carries this tag may
    /// form a docking config against it. Leave null to accept any port.
    /// </summary>
    // Triad: which face a shuttle presents matters at tight berths, and collision checks alone proved
    // unreliable at picking it. This lets the berth demand a side outright: the bus's port-side pair is
    // tagged DockTransitPort, its starboard pair DockTransitStarboard, and each mapped bus berth
    // requires the side that actually fits. A hard filter in GetDockingConfigs, not a sort preference.
    [ViewVariables(VVAccess.ReadWrite),
     DataField(customTypeSerializer: typeof(PrototypeIdSerializer<TagPrototype>))]
    public string? RequiredShuttleTag;
}
