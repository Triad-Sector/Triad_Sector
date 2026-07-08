using System.Numerics;
using Content.Shared.Atmos.Components;

namespace Content.Shared.RPD;

/// <summary>
/// Shared math for picking an <see cref="AtmosPipeLayer"/> from where the operator's cursor sits inside the
/// clicked tile. Both the client placement preview and the server-side spawn commit need to agree on the result —
/// keep this as the single source of truth so the ghost can't lie about which layer the pipe will land on.
/// </summary>
public static class RPDLayerMath
{
    /// <summary>
    /// Mouse must be at least this far (in tile units) from the tile center for the layer to leave Primary.
    /// </summary>
    public const float MouseDeadzoneRadius = 0.25f;

    /// <summary>
    /// Map a cursor offset (from the clicked tile's center) plus the operator's view orientation to a pipe layer.
    /// Within the deadzone radius returns <see cref="AtmosPipeLayer.Primary"/>; outside, NE/E quadrant returns
    /// <see cref="AtmosPipeLayer.Secondary"/> and SW/W quadrant returns <see cref="AtmosPipeLayer.Tertiary"/>,
    /// adjusted for grid rotation so the result is always relative to the player's screen.
    /// </summary>
    public static AtmosPipeLayer PickLayer(Vector2 mouseDiffFromTileCenter, Angle eyeRotation, Angle gridRotation)
    {
        if (mouseDiffFromTileCenter.Length() <= MouseDeadzoneRadius)
            return AtmosPipeLayer.Primary;

        var angle = new Angle(mouseDiffFromTileCenter);
        var direction = (angle + eyeRotation + gridRotation + Math.PI / 2).GetCardinalDir();
        return (direction == Direction.North || direction == Direction.East)
            ? AtmosPipeLayer.Secondary
            : AtmosPipeLayer.Tertiary;
    }
}
