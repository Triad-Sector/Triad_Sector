using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Shipyard.Save;

[Serializable, NetSerializable]
public sealed class DeleteLocalShipFileMessage : EntityEventArgs
{
    public string FilePath { get; }

    public DeleteLocalShipFileMessage(string filePath)
    {
        FilePath = filePath;
    }
}
