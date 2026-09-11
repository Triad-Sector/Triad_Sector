using Content.Server.NPC.Systems;

namespace Content.Server.NPC.Components;

/// <summary>
/// Entities with this component will retaliate against those who physically attack them.
/// It has an optional "memory" specification wherein it will only attack those entities for a specified length of time.
/// </summary>
[RegisterComponent, Access(typeof(NPCRetaliationSystem))]
public sealed partial class NPCRetaliationComponent : Component
{
    /// <summary>
    /// How long after being attacked will an NPC continue to be aggressive to the attacker for.
    /// </summary>
    [DataField("attackMemoryLength"), ViewVariables(VVAccess.ReadWrite)]
    public TimeSpan? AttackMemoryLength;

    /// <summary>
    /// A dictionary that stores an entity and the time at which they will no longer be considered hostile.
    /// </summary>
    /// todo: this needs to support timeoffsetserializer at some point
    // Triad: keys are arbitrary attackers anywhere in the world, not entities co-located with this
    // NPC, so on a ship-grid save any attacker outside the save set serializes as the literal string
    // "invalid". Two of them collide as a duplicate dictionary key and kill the whole save. Combat
    // memory is runtime state that expires on AttackMemoryLength anyway, so it is no longer persisted.
    /*
    [DataField("attackMemories")]
    */
    [ViewVariables]
    // End Triad
    public Dictionary<EntityUid, TimeSpan> AttackMemories = new();
}
