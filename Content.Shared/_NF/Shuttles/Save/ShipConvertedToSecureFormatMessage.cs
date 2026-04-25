using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shuttles.Save;

[Serializable, NetSerializable]
public sealed class ShipConvertedToSecureFormatMessage : EntityEventArgs
{
    public string ConvertedYamlData { get; set; } = string.Empty;
    public string ShipName { get; set; } = string.Empty;
}