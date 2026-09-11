using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Materials.OreSilo;

/// <summary>
/// Provides additional materials to linked clients across long distances.
/// </summary>
[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(SharedOreSiloSystem))]
public sealed partial class OreSiloComponent : Component
{
    /// <summary>
    /// The <see cref="OreSiloClientComponent"/> that are connected to this silo.
    /// Runtime-only: rebuilt from the clients on startup. <see cref="OreSiloClientComponent.Silo"/> is
    /// the authoritative half of the link.
    /// </summary>
    // Triad: persisting BOTH halves of a two-way link is what produced the dangling ore-silo
    // references in production. A ship save carries whichever half sits on the grid, so a silo saved
    // without its clients (or a client saved without its silo) deserialized a uid that resolves to
    // entity 0, and every downstream lookup then logged a resolve error with a full stack trace.
    // Only the client half is persisted now and this set is rebuilt from it, which is the same shape
    // the engine already uses for DeviceLinkSinkComponent.LinkedSources: one side saved, the other
    // reconstructed by the source's ComponentStartup.
    /*
    [DataField, AutoNetworkedField]
    */
    [AutoNetworkedField]
    // End Triad
    public HashSet<EntityUid> Clients = new();

    /// <summary>
    /// The maximum distance you can be to the silo and still receive transmission.
    /// </summary>
    /// <remarks>
    /// Default value should be big enough to span a single large department.
    /// </remarks>
    [DataField, AutoNetworkedField]
    public float Range = 20f;
}

[Serializable, NetSerializable]
public sealed class OreSiloBuiState : BoundUserInterfaceState
{
    public readonly HashSet<(NetEntity, string, string)> Clients;

    public OreSiloBuiState(HashSet<(NetEntity, string, string)> clients)
    {
        Clients = clients;
    }
}

[Serializable, NetSerializable]
public sealed class ToggleOreSiloClientMessage : BoundUserInterfaceMessage
{
    public readonly NetEntity Client;

    public ToggleOreSiloClientMessage(NetEntity client)
    {
        Client = client;
    }
}

[Serializable, NetSerializable]
public enum OreSiloUiKey : byte
{
    Key
}
