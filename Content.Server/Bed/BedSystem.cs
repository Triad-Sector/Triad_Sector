using Content.Shared.Bed;
using Content.Shared.Bed.Components;
using Content.Shared.Bed.Sleep;
using Content.Shared.Buckle.Components;
using Content.Shared.Damage;
using Content.Shared.Emag.Systems;
using Content.Shared.Mobs.Systems;
using Content.Shared.Power;
using Content.Server.Construction;
using Content.Shared._EinsteinEngines.Silicon.Components; // Triad

namespace Content.Server.Bed
{
    public sealed partial class BedSystem : SharedBedSystem
    {
        [Dependency] private DamageableSystem _damageableSystem = default!;
        [Dependency] private EmagSystem _emag = default!;
        [Dependency] private SharedAppearanceSystem _appearance = default!;
        [Dependency] private MobStateSystem _mobStateSystem = default!;

        private EntityQuery<SleepingComponent> _sleepingQuery;

        public override void Initialize()
        {
            base.Initialize();

            _sleepingQuery = GetEntityQuery<SleepingComponent>();
        }

        public override void Update(float frameTime)
        {
            base.Update(frameTime);

            var query = EntityQueryEnumerator<HealOnBuckleHealingComponent, HealOnBuckleComponent, StrapComponent>();
            while (query.MoveNext(out var uid, out _, out var bedComponent, out var strapComponent))
            {
                if (Timing.CurTime < bedComponent.NextHealTime)
                    continue;

                bedComponent.NextHealTime += TimeSpan.FromSeconds(bedComponent.HealTime);

                if (strapComponent.BuckledEntities.Count == 0)
                    continue;

                foreach (var healedEntity in strapComponent.BuckledEntities)
                {
                    if (_mobStateSystem.IsDead(healedEntity)
                        || HasComp<SiliconComponent>(healedEntity)) // Goobstation
                        continue;

                    var damage = bedComponent.Damage;

                    if (_sleepingQuery.HasComp(healedEntity))
                        damage *= bedComponent.SleepMultiplier;

                    _damageableSystem.TryChangeDamage(healedEntity, damage, true, origin: uid);
                }
            }
        }

        // Frontier: upgradeable parts
        [SubscribeLocalEvent]
        private void OnRefreshParts(Entity<StasisBedComponent> ent, ref RefreshPartsEvent args)
        {
            var metabolismRating = args.PartRatings[ent.Comp.MachinePartMetabolismModifier];
            ent.Comp.Multiplier = ent.Comp.BaseMultiplier * metabolismRating; //linear scaling so it's not OP
            if (_emag.CheckFlag(ent, EmagType.Interaction))
                ent.Comp.Multiplier = 1f / ent.Comp.Multiplier;

            Dirty(ent);
        }

        [SubscribeLocalEvent]
        private void OnUpgradeExamine(Entity<StasisBedComponent> ent, ref UpgradeExamineEvent args)
        {
            args.AddPercentageUpgrade("stasis-bed-component-upgrade-stasis", ent.Comp.Multiplier / ent.Comp.BaseMultiplier);
        }
    }
}
