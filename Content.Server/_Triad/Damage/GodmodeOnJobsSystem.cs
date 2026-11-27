using System.Runtime.InteropServices;
using Content.Shared.Damage;
using Content.Shared.Mind;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Content.Shared._Triad.Damage;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;
using Content.Shared.Ghost;

namespace Content.Server._Triad.Damage;

public sealed partial class GodmodeOnJobsSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedJobSystem _jobs = default!;

    private readonly Dictionary<ProtoId<GodmodeOnJobsListPrototype>, bool> CachedPrototypes = new();
    
    private static readonly Dictionary<ProtoId<JobPrototype>, int> ActiveJobPlayers = new();
    private static readonly TimeSpan JobCheckEvery = TimeSpan.FromMinutes(4);
    
    private TimeSpan? _nextJobsCheck = TimeSpan.Zero;

    public override void Initialize()
    {
        CachePrototypes();
    }

    [SubscribeLocalEvent]
    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<GodmodeOnJobsListPrototype>())
            return;

        CachePrototypes();
    }

    [SubscribeLocalEvent]
    private void OnBeforeDamageChanged(Entity<GodmodeOnJobsComponent> ent, ref BeforeDamageChangedEvent args)
    {
        var enabled = CachedPrototypes[ent.Comp.JobListProto];

        if (!enabled)
            return;

        args.Cancelled = true;
    }

    private void CachePrototypes()
    {
        CachedPrototypes.Clear();

        foreach (var proto in _prototypes.EnumeratePrototypes<GodmodeOnJobsListPrototype>())
        {
            if (!CachedPrototypes.ContainsKey(proto.ID))
                CachedPrototypes[proto.ID] = false;
        }
    }

    private void CheckForJobPlayers()
    {
        ActiveJobPlayers.Clear();

        // Cache all the jobs of everyone
        var query = AllEntityQuery<MindComponent>();
        while (query.MoveNext(out var uid, out var comp))
        {
            if (comp.CurrentEntity is null)
                continue;

            if (!_jobs.MindTryGetJobId(uid, out var jobId) || jobId == null)
                continue;

            ref var jobAmounts = ref CollectionsMarshal.GetValueRefOrAddDefault(ActiveJobPlayers, jobId.Value, out _);
            jobAmounts += 1;
        }

        // Check each prototype to see if they pass
        foreach (var (protoId, _) in CachedPrototypes)
        {
            var prototype = _prototypes.Index(protoId);
            var failed = false; // Used for the All and None modes
            var any = false;
            foreach (var (jobId, minAmount) in prototype.JobList)
            {
                if (ActiveJobPlayers.TryGetValue(jobId, out var plrAmount))
                {
                    switch (prototype.Mode)
                    {
                        case GodmodeOnJobsMode.All:
                            if (minAmount > plrAmount)
                                failed = true;
                            break;
                        case GodmodeOnJobsMode.Any:
                            if (minAmount <= plrAmount)
                            {
                                any = true;
                                failed = false;
                            }
                            break;
                        case GodmodeOnJobsMode.None:
                            if (minAmount <= plrAmount)
                                failed = true;
                            break;
                    }
                }

                if (failed || any)
                    break;
            }

            CachedPrototypes[protoId] = !failed;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextJobsCheck)
            return;

        _nextJobsCheck = _timing.CurTime + JobCheckEvery;

        CheckForJobPlayers();
    }
}
