using Content.Shared.NPC.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;


namespace Content.Shared._Triad.NPC;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(TriadNpcFactionSystem))]
public sealed partial class AddNpcFactionOnMapInitComponent : Component
{
    /// <summary>
    /// Factions to add to the entity
    /// </summary>
    [DataField("factions"), AutoNetworkedField]
    public HashSet<ProtoId<NpcFactionPrototype>> AddedFactions = new();
}
