using Robust.Shared.Serialization;
using Robust.Shared.Map;
using Content.Shared.Shuttles.BUIStates;
using Content.Shared._Mono.ShipGuns;

namespace Content.Shared._Mono.FireControl;

[Serializable, NetSerializable]
public sealed class FireControlConsoleUpdateEvent : EntityEventArgs
{
}

[Serializable, NetSerializable]
public sealed class FireControlConsoleBoundInterfaceState : BoundUserInterfaceState
{
    public bool Connected;
    public FireControllableEntry[] FireControllables;
    public NavInterfaceState NavState;

    public FireControlConsoleBoundInterfaceState(bool connected, FireControllableEntry[] fireControllables, NavInterfaceState navState)
    {
        Connected = connected;
        FireControllables = fireControllables;
        NavState = navState;
    }
}

[Serializable, NetSerializable]
public enum FireControlConsoleUiKey : byte
{
    Key,
}

[Serializable, NetSerializable]
public sealed class FireControlConsoleRefreshServerMessage : BoundUserInterfaceMessage
{

}

[Serializable, NetSerializable]
public sealed class FireControlConsoleFireMessage : BoundUserInterfaceMessage
{
    public List<NetEntity> Selected;
    public NetCoordinates Coordinates;
    // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
    public NetEntity? LockedTarget;
    // Triad: targeting lock code end
    public FireControlConsoleFireMessage(List<NetEntity> selected, NetCoordinates coordinates, NetEntity? lockedTarget = null)
    {
        Selected = selected;
        Coordinates = coordinates;
        // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
        LockedTarget = lockedTarget;
        // Triad: targeting lock code end
    }
}

/// <summary>
/// Event raised when a fire control console wants to fire weapons at specific coordinates.
/// Used for tracking cursor position.
/// </summary>
public sealed class FireControlConsoleFireEvent : EntityEventArgs
{
    /// <summary>
    /// The coordinates of the cursor/firing position
    /// </summary>
    public NetCoordinates Coordinates;

    /// <summary>
    /// The weapons selected to fire
    /// </summary>
    public List<NetEntity> Selected;

    public FireControlConsoleFireEvent(NetCoordinates coordinates, List<NetEntity> selected)
    {
        Coordinates = coordinates;
        Selected = selected;
    }
}

[Serializable, NetSerializable]
public struct FireControllableEntry
{
    /// <summary>
    /// The entity in question
    /// </summary>
    public NetEntity NetEntity;

    /// <summary>
    /// Location of the entity
    /// </summary>
    public NetCoordinates Coordinates;

    /// <summary>
    /// Display name of the entity
    /// </summary>
    public string Name;

    /// <summary>
    /// Current ammunition count.
    /// </summary>
    public int? AmmoCount;

    /// <summary>
    /// Whether this weapon has manual reload.
    /// </summary>
    public bool HasManualReload;

    // Triad: targeting lock code start https://github.com/Triad-Sector/Triad_Sector/pull/139
    /// <summary>
    /// Projectile speed in m/s. Null for hitscan (energy) weapons.
    /// </summary>
    public float? ProjectileSpeed;

    /// <summary>
    /// Weapon type for intercept indicator shape selection.
    /// </summary>
    public ShipGunType GunType;
    // Triad: targeting lock code end

    public FireControllableEntry(NetEntity entity, NetCoordinates coordinates, string name, int? ammoCount = null, bool hasManualReload = false)
    {
        NetEntity = entity;
        Coordinates = coordinates;
        Name = name;
        AmmoCount = ammoCount;
        HasManualReload = hasManualReload;
    }
}
