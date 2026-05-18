using Content.Shared.Storage;
using Content.Shared.Whitelist;

namespace Content.Shared._RMC14.Storage;

public sealed class RMCStorageSystem : EntitySystem
{
    [Dependency] private readonly EntityWhitelistSystem _entityWhitelist = default!;

    private EntityQuery<StorageComponent> _storageQuery;

    public override void Initialize()
    {
        _storageQuery = GetEntityQuery<StorageComponent>();
    }

    private bool CanInsertStorageLimit(Entity<StorageComponent?, LimitedStorageComponent?> limited, EntityUid toInsert, out LocId popup)
    {
        popup = default;
        if (!Resolve(limited, ref limited.Comp2, false) ||
            !_storageQuery.Resolve(limited, ref limited.Comp1, false))
        {
            return true;
        }

        foreach (var limit in limited.Comp2.Limits)
        {
            if (!_entityWhitelist.IsWhitelistPassOrNull(limit.Whitelist, toInsert))
                continue;

            if (_entityWhitelist.IsBlacklistPass(limit.Blacklist, toInsert))
                continue;

            var storedCount = 0;
            foreach (var stored in limited.Comp1.StoredItems.Keys)
            {
                if (stored == toInsert)
                    continue;

                if (!_entityWhitelist.IsWhitelistPassOrNull(limit.Whitelist, stored))
                    continue;

                if (_entityWhitelist.IsBlacklistPass(limit.Blacklist, stored))
                    continue;

                storedCount++;
                if (storedCount >= limit.Count)
                    break;
            }

            if (storedCount < limit.Count)
                continue;

            popup = limit.Popup == default ? "triad-storage-limit-cant-fit" : limit.Popup;
            return false;
        }

        return true;
    }

    public bool TryGetLastItem(Entity<StorageComponent?> storage, out EntityUid item)
    {
        item = default;
        if (!Resolve(storage, ref storage.Comp, false))
            return false;

        ItemStorageLocation? lastLocation = null;
        foreach (var (stored, location) in storage.Comp.StoredItems)
        {
            if (lastLocation is not { } last ||
                last.Position.Y < location.Position.Y)
            {
                item = stored;
                lastLocation = location;
                continue;
            }

            if (last.Position.Y == location.Position.Y &&
                last.Position.X > location.Position.X)
            {
                item = stored;
                lastLocation = location;
            }
        }

        return item != default;
    }

    public bool TryGetFirstItem(Entity<StorageComponent?> storage, out EntityUid item)
    {
        item = default;
        if (!Resolve(storage, ref storage.Comp, false))
            return false;

        ItemStorageLocation? firstLocation = null;
        foreach (var (stored, location) in storage.Comp.StoredItems)
        {
            if (firstLocation is not { } first ||
                first.Position.Y > location.Position.Y)
            {
                item = stored;
                firstLocation = location;
                continue;
            }

            if (first.Position.Y == location.Position.Y &&
                first.Position.X < location.Position.X)
            {
                item = stored;
                firstLocation = location;
            }
        }

        return item != default;
    }

    public bool CanInsert(Entity<StorageComponent?> storage, EntityUid toInsert, out LocId popup)
    {
        if (!CanInsertStorageLimit((storage, storage, null), toInsert, out popup))
            return false;

        //if (!CanInsertStoreSkill((storage, storage, null), toInsert, user, out popup))
        //return false; TODO TRIAD

        return true;
    }
}
