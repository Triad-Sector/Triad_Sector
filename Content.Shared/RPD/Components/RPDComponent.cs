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
    /// Pipe layer chosen at the last <see cref="Robust.Shared.GameObjects.AfterInteractEvent"/>. Per-entity so
    /// concurrent users with their own RPDs don't trample each other's selection between click and do-after
    /// completion. Server-only state.
    /// </summary>
    [ViewVariables(VVAccess.ReadOnly)]
    public AtmosPipeLayer CurrentLayer { get; set; } = AtmosPipeLayer.Primary;
}
