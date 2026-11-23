using System.Runtime.InteropServices;
using Content.Shared.Damage;
using Content.Shared.Mind;
using Content.Shared.Roles;
using Content.Shared.Roles.Jobs;
using Robust.Shared.Network;
using Robust.Shared.Prototypes;
using Robust.Shared.Timing;

namespace Content.Shared._Triad.Damage;

public sealed partial class GodmodeOnNoJobsSystem : EntitySystem
{
    [Dependency] private IPrototypeManager _prototypes = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private SharedJobSystem _jobs = default!;

    private static readonly Dictionary<ProtoId<GodmodeOnNoJobsListPrototype>, bool> CachedPrototypes = new();
    private static readonly Dictionary<ProtoId<JobPrototype>, int> ActiveJobPlayers = new();

    private static readonly TimeSpan JobCheckEvery = TimeSpan.FromMinutes(3);
    private TimeSpan? _nextJobsCheck = TimeSpan.Zero;

    public override void Initialize()
    {
        CachePrototypes();
    }

    [SubscribeLocalEvent]
    private void OnPrototypesReloaded(PrototypesReloadedEventArgs args)
    {
        if (!args.WasModified<GodmodeOnNoJobsListPrototype>())
            return;

        CachePrototypes();
    }

    [SubscribeLocalEvent]
    private void OnBeforeDamageChanged(Entity<GodmodeOnNoJobsComponent> ent, ref BeforeDamageChangedEvent args)
    {
        if (_net.IsClient)
            return;

        var enabled = CachedPrototypes[ent.Comp.JobListProto];

        if (!enabled)
            return;

        args.Cancelled = true;
    }

    private void CachePrototypes()
    {
        CachedPrototypes.Clear();

        foreach (var proto in _prototypes.EnumeratePrototypes<GodmodeOnNoJobsListPrototype>())
        {
            CachedPrototypes.Add(proto.ID, false);
        }
    }

    private void CheckForJobPlayers()
    {
        ActiveJobPlayers.Clear();

        // Cache all the jobs of everyone
        var query = AllEntityQuery<MindComponent>();
        while (query.MoveNext(out var uid, out _))
        {
            if (!_jobs.MindTryGetJobId(uid, out var jobId) || jobId == null)
                continue;

            ref var jobAmounts = ref CollectionsMarshal.GetValueRefOrAddDefault(ActiveJobPlayers, jobId.Value, out _);
            jobAmounts += 1;
        }

        // Check each prototype to see if they pass
        var passedPrototypes = new List<ProtoId<GodmodeOnNoJobsListPrototype>>();
        foreach (var (protoId, _) in CachedPrototypes)
        {
            var prototype = _prototypes.Index(protoId);
            foreach (var (jobId, minAmount) in prototype.JobList)
            {
                if (ActiveJobPlayers.TryGetValue(jobId, out var plrAmount))
                {
                    if (minAmount <= plrAmount)
                        passedPrototypes.Add(protoId);
                }
            }
        }

        foreach (var proto in passedPrototypes)
        {
            CachedPrototypes[proto] = true;
        }
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        if (_timing.CurTime < _nextJobsCheck)
            return;

        if (_net.IsClient)
            return;

        _nextJobsCheck = _timing.CurTime + JobCheckEvery;

        CheckForJobPlayers();
    }
}
