using Content.Shared._DV.CartridgeLoader.Cartridges; // Triad: TriTalk card scanning
using Robust.Shared.Serialization;

namespace Content.Shared.CartridgeLoader.Cartridges;

[Serializable, NetSerializable]
public sealed class LogProbeUiState : BoundUserInterfaceState
{
    /// <summary>
    /// The name of the scanned entity.
    /// </summary>
    public string EntityName;

    /// <summary>
    /// The list of probed network devices
    /// </summary>
    public List<PulledAccessLog> PulledLogs;

    /// <summary>
    /// Triad: The TriTalk card data if a card was scanned, null otherwise.
    /// </summary>
    public NanoChatData? NanoChatData { get; }

    public LogProbeUiState(string entityName, List<PulledAccessLog> pulledLogs, NanoChatData? nanoChatData = null) // Triad: TriTalk support
    {
        EntityName = entityName;
        PulledLogs = pulledLogs;
        NanoChatData = nanoChatData; // Triad
    }
}

[Serializable, NetSerializable, DataRecord]
public sealed partial class PulledAccessLog
{
    public readonly TimeSpan Time;
    public readonly string Accessor;

    public PulledAccessLog(TimeSpan time, string accessor)
    {
        Time = time;
        Accessor = accessor;
    }
}
