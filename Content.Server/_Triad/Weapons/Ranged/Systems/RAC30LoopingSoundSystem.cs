
using System;
using Content.Server.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Components;
using Content.Shared.Weapons.Ranged.Events;
using Content.Shared.Weapons.Ranged.Systems;
using Robust.Shared.Audio;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Random;
using Robust.Shared.Timing;

namespace Content.Server.Weapons.Ranged.Systems;

public sealed class RAC30LoopingSoundSystem : EntitySystem
{
    [Dependency] private readonly SharedAudioSystem _audio = default!;
    [Dependency] private readonly IGameTiming _timing = default!;
    [Dependency] private readonly IRobustRandom _random = default!;

    private static readonly TimeSpan SoundTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly int MaxStartDelayMs = 60;

    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<RAC30LoopingSoundComponent, GunShotEvent>(OnGunShot);
        SubscribeLocalEvent<RAC30LoopingSoundComponent, ComponentShutdown>(OnShutdown);
    }

    private void OnGunShot(Entity<RAC30LoopingSoundComponent> ent, ref GunShotEvent args)
    {
        ent.Comp.StopTime = _timing.CurTime + SoundTimeout;

        if (ent.Comp.AudioStream is { } stream && Exists(stream))
            return;

        if (ent.Comp.PendingStartTime != null)
            return;

        var delay = TimeSpan.FromMilliseconds(_random.Next(0, MaxStartDelayMs + 1));
        ent.Comp.PendingStartTime = _timing.CurTime + delay;
    }

    public override void Update(float frameTime)
    {
        base.Update(frameTime);

        var query = EntityQueryEnumerator<RAC30LoopingSoundComponent, AutoShootGunComponent>();

        while (query.MoveNext(out var uid, out var sound, out var autoShoot))
        {
            var canContinue = autoShoot.CanFire &&
                              (autoShoot.Enabled || autoShoot.RemainingTime > TimeSpan.Zero);

            if (!canContinue || _timing.CurTime >= sound.StopTime)
            {
                sound.PendingStartTime = null;

                if (sound.AudioStream != null)
                    StopLoop(sound);

                continue;
            }

            if (sound.AudioStream is { } stream && Exists(stream))
                continue;

            if (sound.PendingStartTime is not { } startTime ||
                _timing.CurTime < startTime)
                continue;

            sound.PendingStartTime = null;

            var audioParams = AudioParams.Default.WithLoop(true);

            sound.AudioStream = _audio.PlayPvs(
                sound.LoopSound,
                uid,
                audioParams)?.Entity;
        }
    }

    private void StopLoop(RAC30LoopingSoundComponent component)
    {
        component.AudioStream = _audio.Stop(component.AudioStream);
    }

    private void OnShutdown(Entity<RAC30LoopingSoundComponent> ent, ref ComponentShutdown args)
    {
        ent.Comp.PendingStartTime = null;
        StopLoop(ent.Comp);
    }
}
