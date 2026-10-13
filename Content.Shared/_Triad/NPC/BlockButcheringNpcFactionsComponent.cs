using Content.Shared.NPC.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.NPC;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(TriadNpcFactionSystem))]
public sealed partial class BlockButcheringNpcFactionsComponent : Component
{
    /// <summary>
    /// Factions that cannot be butchered by the entity.
    /// </summary>
    [DataField("factions"), AutoNetworkedField]
    public HashSet<ProtoId<NpcFactionPrototype>> BlockedFactions = new();

    /// <summary>
    /// Popup that shows on butcher attempt.
    /// </summary>
    [DataField, AutoNetworkedField]
    public LocId? Popup = "butcher-attempt-faction-fail";
}
