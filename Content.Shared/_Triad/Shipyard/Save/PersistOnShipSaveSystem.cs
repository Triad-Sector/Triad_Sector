using Content.Shared._HL.Shipyard;
using Content.Shared.Storage;
using Content.Shared.Examine;

namespace Content.Shared._Triad.Shipyard.Save;

public sealed class PersistOnShipSaveSystem : EntitySystem
{
    private static readonly int ExaminePriority = -5;

    public override void Initialize()
    {
        SubscribeLocalEvent<HLPersistOnShipSaveComponent, ExaminedEvent>(OnExamine);
    }

    private void OnExamine(Entity<HLPersistOnShipSaveComponent> ent, ref ExaminedEvent args)
    {
        if (!args.IsInDetailsRange)
            return;

        var examineText = HasComp<StorageComponent>(ent.Owner)
            ? "persistonshipsave-component-examine-storage" : "persistonshipsave-component-examine";

        args.PushMarkup(Loc.GetString(examineText), ExaminePriority);
    }
}
