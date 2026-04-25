using Robust.Shared.Serialization;

namespace Content.Shared._NF.Shuttles.Save
{
    [Serializable, NetSerializable]
    public sealed class DeleteLocalShipFileMessage : EntityEventArgs
    {
        public string FilePath { get; }

        public DeleteLocalShipFileMessage(string filePath)
        {
            FilePath = filePath;
        }
    }
}
