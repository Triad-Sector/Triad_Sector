using Robust.Shared.Serialization;

namespace Content.Shared.Power;

[Serializable, NetSerializable]
public enum TeslaCoilVisuals : byte
{
    Enabled,
    Lightning,
    Charged // Triad: coil battery too full to accept a full strike; shows the persistent arcing indicator
}
