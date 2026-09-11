using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Shipyard.Save;

[Serializable, NetSerializable]
public sealed class MigrateShipFileMessage : EntityEventArgs
{
    public string TargetPath { get; }
    public string Contents { get; }

    public MigrateShipFileMessage(string targetPath, string contents)
    {
        TargetPath = targetPath;
        Contents = contents;
    }
}
