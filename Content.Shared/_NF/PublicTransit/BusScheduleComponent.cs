using Robust.Shared.GameStates;

namespace Content.Shared._NF.PublicTransit;

/// <summary>
/// A wall sign that shows where the public transit bus is heading next.
/// </summary>
/// <remarks>
/// Triad: restores the half of the Frontier sign that never got ported. The sprite only ever had a
/// body and a greyscale top panel, so the "schedule" is a colour: the panel is tinted to the IFF
/// colour of the stop the bus is bound for, and every sign in the sector shows the same thing
/// because there is only one bus. The <see cref="PublicTransitVisuals"/> enum survived the drop, so
/// this is the component and the visualizer that were missing around it.
/// </remarks>
[RegisterComponent, NetworkedComponent]
public sealed partial class BusScheduleComponent : Component
{
    /// <summary>
    /// Colour shown when the bus has no destination, for instance before the route is set up.
    /// </summary>
    [DataField]
    public Color IdleColor = Color.FromHex("#aaaaaa");
}
