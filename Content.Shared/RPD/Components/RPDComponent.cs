using Content.Shared.Atmos.Components;
using Content.Shared.RCD.Components;
using Content.Shared.RPD.Systems;
using Robust.Shared.GameStates;

namespace Content.Shared.RPD.Components;

/// <summary>
/// Marker + state for the Rapid Piping Device. Coexists with <see cref="RCDComponent"/> on RPD entities; presence
/// of this component is the signal that switches construction/deconstruction behavior into the RPD-specific paths
/// handled by <see cref="RPDSystem"/>.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(RPDSystem))]
public sealed partial class RPDComponent : Component
{
    /// <summary>
    /// Selected pipe color slot from <see cref="RPDPalette.Colors"/>. The actual <see cref="Color"/> is derived
    /// server-side at spawn time via <c>RPDPalette.Colors[PipeColor]</c> — the wire only carries the key so a
    /// misbehaving client can't desync the (key, color) pair. <see cref="RPDPalette.DefaultKey"/> skips the stain.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string PipeColor { get; set; } = RPDPalette.DefaultKey;

    /// <summary>
    /// The operator's cursor-aimed pipe layer, streamed by the client on change. Read at the commit click (stamped
    /// onto the do-after via <c>RCDPlacementCommitEvent</c>) and for deconstruct targeting; a placement in flight
    /// never reads it again, so moving the cursor during the do-after cannot move the pipe. Server-only state.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public AtmosPipeLayer CurrentLayer { get; set; } = AtmosPipeLayer.Primary;
}
