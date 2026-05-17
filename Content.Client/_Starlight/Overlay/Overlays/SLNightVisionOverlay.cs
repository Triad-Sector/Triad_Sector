using Robust.Client.Graphics;

namespace Content.Client._Starlight.Overlay;

public sealed class SLNightVisionOverlay : BaseVisionOverlay
{
    public SLNightVisionOverlay(ShaderPrototype shader) : base(shader)
        => ZIndex = (int?)OverlayZIndexes.SLNightVision;
}