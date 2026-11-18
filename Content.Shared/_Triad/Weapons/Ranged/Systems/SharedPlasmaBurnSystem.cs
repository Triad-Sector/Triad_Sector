using Content.Shared.Damage;
using Content.Shared.Projectiles;
using Content.Shared._Triad.Weapons.Ranged.Components;
using Robust.Shared.Timing;
using Content.Shared.Whitelist;
using Robust.Shared.Prototypes;
using Content.Shared.Alert;
using Content.Shared.Atmos.Components;
using Content.Shared.Stunnable;
using Content.Shared.ActionBlocker;
using Content.Shared.Popups;
using Robust.Shared.Random;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Network;

namespace Content.Shared._Triad.Weapons.Ranged.Systems;

public abstract partial class SharedPlasmaBurnSystem : EntitySystem
{
    [Dependency] private IGameTiming _timing = default!;
    [Dependency] private INetManager _net = default!;
    [Dependency] private IRobustRandom _random = default!;
    [Dependency] private ActionBlockerSystem _actionBlocker = default!;
    [Dependency] private AlertsSystem _alerts = default!;
    [Dependency] private SharedAudioSystem _audio = default!;
    [Dependency] private EntityWhitelistSystem _whitelist = default!;
    [Dependency] private SharedAppearanceSystem _appearance = default!;
    [Dependency] private SharedStunSystem _stun = default!;
    [Dependency] private SharedPopupSystem _popup = default!;
    [Dependency] private DamageableSystem _damage = default!;

    [Dependency] private EntityQuery<UserPlasmaBurnComponent> _userBurnQuery = default!;

    private static readonly ProtoId<AlertPrototype> FireAlert = "Fire";
    private const int ResistReduction = 5;

    [SubscribeLocalEvent]
    private void OnPlasmaBurnOnHit(Entity<AddPlasmaBurningOnHitComponent> ent, ref ProjectileHitEvent args)
    {
        var target = args.Target;

        if (!_whitelist.CheckBoth(target, ent.Comp.Blacklist, ent.Comp.Whitelist))
            return;

        var selfMessage = Loc.GetString(ent.Comp.SelfPopup, ("projectile", ent.Owner));

        if (_net.IsServer)
            _popup.PopupEntity(selfMessage, target, target, PopupType.MediumCaution);

        var expiration = _timing.CurTime + ent.Comp.Duration;

        if (_userBurnQuery.TryComp(target, out var burnComp))
        {
            burnComp.Stacks += ent.Comp.AddStacks;
            burnComp.ExpiresAt = expiration;
            burnComp.Color = ent.Comp.Color;
            UpdateBurn((target, burnComp));
            return;
        }

        var newBurnComp = EnsureComp<UserPlasmaBurnComponent>(target);
        newBurnComp.Stacks = ent.Comp.AddStacks;
        newBurnComp.ExpiresAt = expiration;
        newBurnComp.Damage = ent.Comp.Damage;
        newBurnComp.ArmorPenetration = ent.Comp.ArmorPenetration;
        newBurnComp.Color = ent.Comp.Color;
        UpdateBurn((target, newBurnComp));
    }

    [SubscribeLocalEvent]
    private void OnPlasmaBurnAdd(Entity<UserPlasmaBurnComponent> ent, ref MapInitEvent args)
    {
        _appearance.SetData(ent, PlasmaBurnVisuals.Burning, PlasmaBurningState.Burning);
        _alerts.ShowAlert(ent, FireAlert);

        ent.Comp.ExpiresAt = _timing.CurTime + ent.Comp.Duration;
        ent.Comp.NextSoundAt = _timing.CurTime;

        UpdateBurn(ent);
    }

    [SubscribeLocalEvent]
    private void OnPlasmaBurnRemove(Entity<UserPlasmaBurnComponent> ent, ref ComponentRemove args)
    {
        _appearance.SetData(ent, PlasmaBurnVisuals.Burning, PlasmaBurningState.None);

        if (TryComp<FlammableComponent>(ent, out var flammable) && flammable.OnFire)
            return;

        _alerts.ClearAlert(ent, FireAlert);
    }

    public void ResistPlasmaBurn(EntityUid uid)
    {
        if (!_userBurnQuery.TryComp(uid, out var userBurn))
            return;

        if (!_actionBlocker.CanInteract(uid, null))
            return;

        userBurn.Stacks -= ResistReduction;
        _stun.TryParalyze(uid, userBurn.ParalyzeOnResistDuration, true, doJitter: false);
        UpdateBurn((uid, userBurn));
    }

    private void UpdateBurn(Entity<UserPlasmaBurnComponent> ent)
    {
        if (ent.Comp.Stacks > ent.Comp.MaxStacks)
            ent.Comp.Stacks = ent.Comp.MaxStacks;

        Dirty(ent);
        UpdateAppearance(ent);
    }

    protected virtual void UpdateAppearance(Entity<UserPlasmaBurnComponent> ent)
    {
    }

    public override void Update(float frameTime)
    {
        var time = _timing.CurTime;

        var burnQuery = EntityQueryEnumerator<UserPlasmaBurnComponent>();
        while (burnQuery.MoveNext(out var uid, out var comp))
        {
            if (time > comp.ExpiresAt || comp.Stacks <= 0)
            {
                RemCompDeferred<UserPlasmaBurnComponent>(uid);
                continue;
            }

            if (time < comp.NextDamageAt)
                continue;

            comp.NextDamageAt = time + comp.DamageInterval;
            DirtyField(uid, comp, nameof(UserPlasmaBurnComponent.NextDamageAt));

            if (_net.IsServer && time > comp.NextSoundAt)
            {
                var randomJitter = _random.Next(0, 3);
                comp.NextSoundAt = time + (comp.SoundCooldown + TimeSpan.FromSeconds(randomJitter));
                _audio.PlayPvs(comp.BurnSound, uid);
            }

            var curDuration = comp.ExpiresAt.TotalSeconds - time.TotalSeconds;
            var maxDuration = comp.Duration.TotalSeconds;
            var durationMod = Math.Min(curDuration / maxDuration * 1.5, 1);
            var damageMod = Math.Clamp(durationMod * 0.12, 0.5, 2);

            // Damage calculations
            // Lessens overtime and scales based on stacks
            var damage = comp.Damage * comp.Stacks * damageMod * 0.14;
            _damage.TryChangeDamage(uid, damage, armorPenetration: comp.ArmorPenetration, canSever: false, interruptsDoAfters: false);
        }
    }
}
