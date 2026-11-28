using Content.Shared._DEN.TimedDespawn;
using Content.Shared.Examine;
using Robust.Server.Audio;
using Robust.Server.GameObjects;
using Robust.Shared.Timing;

namespace Content.Server._DEN.TimedDespawn;

/// <summary>
/// This handles the <see cref="TimedDespawnDetailedComponent"/>
/// </summary>
public sealed partial class TimedDespawnDetailedSystem : EntitySystem
{
    [Dependency] private AudioSystem _audioSystem = default!;
    [Dependency] private IGameTiming _gameTiming = default!;
    [Dependency] private TransformSystem _transformSystem = default!;

    private readonly HashSet<EntityUid> _timedDespawns = new();

    // Triad: reused each tick so Update can walk the set without enumerating it while TryDelete
    // removes from it.
    private readonly List<EntityUid> _despawnScratch = new();

    /// <inheritdoc/>
    public override void Initialize()
    {
        SubscribeLocalEvent<TimedDespawnDetailedComponent, MapInitEvent>(OnMapInit);
        SubscribeLocalEvent<TimedDespawnDetailedComponent, ComponentStartup>(OnStartup); // Triad
        SubscribeLocalEvent<TimedDespawnDetailedComponent, ExaminedEvent>(OnExamine);
    }

    public override void Update(float frameTime)
    {
        if (_timedDespawns.Count == 0)
            return;

        // Triad: walk a copy. TryDelete calls StopTimer, which removes from this set, and mutating
        // it inside the foreach throws on the very tick anything expires.
        _despawnScratch.Clear();
        _despawnScratch.AddRange(_timedDespawns);

        foreach (var entity in _despawnScratch)
        {
            if (!Exists(entity) || !TryComp<TimedDespawnDetailedComponent>(entity, out var timedDespawn))
            {
                _timedDespawns.Remove(entity);
                continue;
            }

            TryDelete((entity, timedDespawn));
        }
    }

    private void OnExamine(Entity<TimedDespawnDetailedComponent> ent, ref ExaminedEvent args)
    {
        var timeLeft = GetTimeRemaining(ent);

        if (timeLeft == null || ent.Comp.ExamineLocId == null)
            return;

        var stringTime = double.Round(timeLeft.Value.TotalSeconds, 1);
        var examineText = Loc.GetString(ent.Comp.ExamineLocId, ("remaining", stringTime));
        args.PushMarkup(examineText, 1);
    }

    public void StartTimer(Entity<TimedDespawnDetailedComponent> ent)
    {
        ent.Comp.StartTime = _gameTiming.CurTime;
        _timedDespawns.Add(ent);

        if (ent.Comp.StartSound != null)
        {
            var entCoords = _transformSystem.GetMoverCoordinates(ent);
            _audioSystem.PlayPredicted(ent.Comp.StartSound, entCoords, null, ent.Comp.StartSoundParams);
        }
    }

    public void StopTimer(Entity<TimedDespawnDetailedComponent> ent)
    {
        ent.Comp.StartTime = TimeSpan.Zero;
        _timedDespawns.Remove(ent);
    }

    public TimeSpan? GetTimeRemaining(Entity<TimedDespawnDetailedComponent> ent)
    {
        if (!_timedDespawns.Contains(ent))
            return null;

        var despawnAfterAsSpan = TimeSpan.FromSeconds(ent.Comp.Lifetime);
        var timeLeft = (ent.Comp.StartTime + despawnAfterAsSpan) - _gameTiming.CurTime;

        return timeLeft;
    }

    public void TryDelete(Entity<TimedDespawnDetailedComponent> ent)
    {
        var remaining = GetTimeRemaining(ent);

        if (remaining == null || remaining.Value.TotalSeconds > 0)
            return;

        if (ent.Comp.EndSound != null)
        {
            var entCoords = _transformSystem.GetMoverCoordinates(ent);
            _audioSystem.PlayPredicted(ent.Comp.EndSound, entCoords, null, ent.Comp.EndSoundParams);
        }

        StopTimer(ent);
        EntityManager.QueueDeleteEntity(ent);
    }

    /// <summary>
    /// Triad: re-registers a timer that arrived from a save. Only OnMapInit filled the despawn set,
    /// and MapInitEvent does not re-fire for an already-map-initialised entity, so a loaded holofan
    /// was never in it and never expired. A zero StartTime is a fresh spawn, which reaches startup
    /// before map init and is left for OnMapInit to start; a non-zero one keeps its own deadline.
    /// </summary>
    private void OnStartup(Entity<TimedDespawnDetailedComponent> ent, ref ComponentStartup args)
    {
        if (ent.Comp.StartTime == TimeSpan.Zero)
            return;

        _timedDespawns.Add(ent);
    }

    private void OnMapInit(Entity<TimedDespawnDetailedComponent> ent, ref MapInitEvent args)
    {
        var entCoords = _transformSystem.GetMoverCoordinates(ent);

        if (ent.Comp.StartSound != null)
            _audioSystem.PlayPredicted(ent.Comp.StartSound, entCoords, null, ent.Comp.StartSoundParams);

        StartTimer(ent);
    }
}
