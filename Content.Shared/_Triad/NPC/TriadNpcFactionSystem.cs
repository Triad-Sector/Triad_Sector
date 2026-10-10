using Content.Shared.NPC.Systems;

namespace Content.Shared._Triad.NPC;

public sealed partial class TriadNpcFactionSystem : EntitySystem
{
    [Dependency] private NpcFactionSystem _npcFaction = default!;

    [SubscribeLocalEvent]
    private void OnAddFactionMapInit(Entity<AddNpcFactionOnMapInitComponent> ent, ref MapInitEvent args)
    {
        _npcFaction.AddFactions(ent.Owner, ent.Comp.AddedFactions);
    }
}
