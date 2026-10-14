using Robust.Shared.GameStates;

namespace Content.Shared._Triad.Shuttles;

[RegisterComponent, NetworkedComponent]
public sealed partial class ThrusterProfileComponent : Component
{
    /// <summary>
    ///     Triad - Which type of thruster is this?
    ///     This is used for raycast checks to determine if the thruster's path is blocked.
    /// </summary>
    [DataField]
    public ThrusterBlockingProfile Profile = ThrusterBlockingProfile.Small;
}
