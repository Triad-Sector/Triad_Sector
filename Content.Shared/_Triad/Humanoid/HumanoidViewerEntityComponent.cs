using Robust.Shared.GameStates;
using Robust.Shared.Player;

namespace Content.Shared._Triad.Humanoid;

/// <summary>
///     Used so the server has a consistent and static reference of a player that exists past cryosleep and being deleted (such as being gibbed)
///     This is good for things like the contraband permit console, which has a reference of the original entity (not mind, since we need appearance data)--
///     so it can be modified past the owner being gibbed for example.
///     Probably a hacky way of doing this.
///     Viewer entities are stored on a paused map.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class HumanoidViewerEntityComponent : Component
{
    [ViewVariables]
    public ICommonSession? Session;
}
