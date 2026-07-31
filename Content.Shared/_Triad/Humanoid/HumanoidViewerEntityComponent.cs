using Robust.Shared.GameStates;
using Robust.Shared.Player;

namespace Content.Shared._Triad.Humanoid;

/// <summary>
///     Used so clients can see humanoid appearance data from players across the map, such as for photographs.
///     Probably a hacky way of doing this.
///     Viewer entities are stored on an empty map.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class HumanoidViewerEntityComponent : Component
{
    [ViewVariables]
    public ICommonSession? Session;
}
