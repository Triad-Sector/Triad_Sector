using Robust.Shared.GameStates;

namespace Content.Shared._HL.Shipyard;

/// <summary>
/// Entities with this component will persist themselves and their contents when the ship is saved.
/// </summary>
[RegisterComponent, NetworkedComponent]
public sealed partial class HLPersistOnShipSaveComponent : Component;
