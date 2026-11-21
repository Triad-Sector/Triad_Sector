using Robust.Shared.Map;
using Robust.Shared.Utility;

namespace Content.Client._Triad.ShipPreview;

public sealed class ShipPreviewScene
{
    public MapId MapId { get; internal set; } = MapId.Nullspace;
    public EntityUid MapEntity { get; internal set; } = EntityUid.Invalid;

    public EntityUid? Ship { get; internal set; }
    public ResPath? ShipFile { get; internal set; }
    public Box2 ShipBounds { get; internal set; }
    public Vector2i ShipSizeInTiles { get; internal set; }
    public int ShipTileCount { get; internal set; }

    public bool HasShip => Ship != null;
}
