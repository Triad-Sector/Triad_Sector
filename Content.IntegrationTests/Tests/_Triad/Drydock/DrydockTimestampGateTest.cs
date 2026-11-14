#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Robust.Shared.Analyzers;
using Robust.Shared.GameObjects;
using Robust.Shared.Serialization.Manager.Attributes;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;

namespace Content.IntegrationTests.Tests._Triad.Drydock
{
    /// <summary>
    /// A gate on the absolute-time class, so the census behind it cannot rot on the next upstream
    /// merge.
    ///
    /// <para>The clock restarts every time the server does, so a game time written raw into a ship
    /// reads as a deadline in a round that has not happened. <c>TimeOffsetSerializer</c> writes it
    /// as an offset and re-bases it on load, which is why upstream already uses it on nearly a
    /// hundred fields. The fix for the ones it had missed was a one-off sweep, and a one-off sweep
    /// is exactly the mechanism this whole subsystem exists to stop relying on: without a gate, the
    /// next component to arrive with a raw timestamp is found by a player.</para>
    ///
    /// <para>The discriminator is the engine's own. <see cref="AutoPausedFieldAttribute"/> means
    /// "shift this when the entity is paused", which is only meaningful for a value measured
    /// against the clock. A duration never carries it. So a paused time field is a timestamp, and a
    /// timestamp that a ship can carry has to be written as an offset. That is a rule rather than a
    /// list of names, which is what keeps this from becoming another table to maintain.</para>
    /// </summary>
    /// <remarks>
    /// It is a partial gate and says so: an absolute time that carries no paused marker is invisible
    /// to it, which is the case for the use-delay times among others. Widening the rule means
    /// finding a second honest marker, not guessing from field names.
    /// </remarks>
    [TestFixture]
    public sealed class DrydockTimestampGateTest
    {
        /// <summary>
        /// Paused time fields that are deliberately left raw, each with the reason it cannot reach a
        /// stored ship. An entry without a reason is a bug somebody quietened rather than a
        /// judgement, and the next person to read this cannot tell the difference unless every line
        /// says which it is.
        /// </summary>
        private static readonly Dictionary<string, string> Exempt = new()
        {
            // Round-scoped or off-grid: these components live on the map, a game rule or a station
            // entity, none of which is ever part of a ship document.
            ["MeteorSwarmComponent.NextWaveTime"] = "a game rule entity, never aboard",
            ["GlobalTimeManagerComponent.TimeOffset"] = "the station clock, off-grid",

            // Mob-scoped. A store refuses with organics aboard, so no mob is ever serialized.
            ["SleepingComponent.CooldownEnd"] = "on a sleeping mob, and mobs refuse the store",
            ["PacifiedComponent.NextPopupTime"] = "mob status effect",
            ["GhostComponent.TimeOfDeath"] = "on a ghost",
            ["HandsComponent.NextThrowTime"] = "on a mob's hands",
            ["TypingIndicatorClothingComponent.GotEquippedTime"] = "set while worn by a mob",
            ["TemperatureSpeedComponent.NextSlowdownUpdate"] = "mob movement speed",

            // Transient: the entity carrying it is mid-flight or mid-effect and does not outlive
            // the store, which refuses while anything is still moving under its own power.
            ["ThrownItemComponent.LandTime"] = "an item in flight, and the component is removed on landing",
            ["ChasingWalkComponent.NextImpulseTime"] = "an entity mid-chase",
            ["PressurizedSolutionComponent.FizzySettleTime"] = "a drink settling, seconds long",

            // Anomaly and singularity equipment, none of which is sold on a hull and all of which
            // is round-scoped where it appears.
            ["AnomalySynchronizerComponent.NextCheckTime"] = "anomaly equipment, not sold on a ship",
            ["TechAnomalyComponent.NextTimer"] = "an anomaly, never aboard",
            ["GravityWellComponent.NextPulseTime"] = "singularity equipment, not sold on a ship",
            ["LightningArcShooterComponent.NextShootTime"] = "tesla equipment, not sold on a ship",
            ["SpookySpeakerComponent.NextSpeakTime"] = "a spooky speaker, round event dressing",
        };

        [Test]
        public async Task EveryPausedTimestampIsWrittenAsAnOffset()
        {
            await using var pair = await PoolManager.GetServerClient();
            var compFactory = pair.Server.ResolveDependency<IComponentFactory>();

            var raw = new List<string>();
            var covered = 0;
            var exemptSeen = new HashSet<string>();

            foreach (var registration in compFactory.GetAllRegistrations())
            {
                foreach (var member in TimeFields(registration.Type))
                {
                    var key = $"{registration.Type.Name}.{member.Name}";

                    if (HasOffsetSerializer(member))
                    {
                        covered++;
                        continue;
                    }

                    if (Exempt.ContainsKey(key))
                    {
                        exemptSeen.Add(key);
                        continue;
                    }

                    raw.Add(key);
                }
            }

            Assert.Multiple(() =>
            {
                // The control. A rule that matched nothing would pass this test on an empty set and
                // report a codebase in perfect health, which is the failure the serializability
                // audit next door already learned to guard against.
                Assert.That(covered, Is.GreaterThan(50),
                    "The control: almost a hundred fields already pair the paused marker with the offset "
                    + "serializer, so finding barely any means the rule stopped matching rather than the "
                    + "codebase changing.");

                Assert.That(raw, Is.Empty,
                    "These fields are marked as game timestamps but are written raw, so a ship carrying one "
                    + "comes back holding the previous server's clock. Give each the offset serializer, or "
                    + "add it to the exemption list above with the reason it cannot reach a stored ship:"
                    + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", raw));

                // A stale exemption is a quiet lie: it reads as a considered judgement about a field
                // that no longer exists or that someone has since fixed properly.
                var stale = Exempt.Keys.Where(k => !exemptSeen.Contains(k)).ToList();
                Assert.That(stale, Is.Empty,
                    "These exemptions no longer match anything and should be deleted:"
                    + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", stale));
            });

            await pair.CleanReturnAsync();
        }

        /// <summary>
        /// Every <c>TimeSpan</c> data field on the component that also carries the engine's paused
        /// marker, which is what makes it a timestamp rather than a duration.
        /// </summary>
        private static IEnumerable<MemberInfo> TimeFields(Type component)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            foreach (var member in component.GetFields(flags).Cast<MemberInfo>().Concat(component.GetProperties(flags)))
            {
                var type = member is FieldInfo f ? f.FieldType : ((PropertyInfo) member).PropertyType;
                type = Nullable.GetUnderlyingType(type) ?? type;

                if (type != typeof(TimeSpan))
                    continue;
                if (member.GetCustomAttribute<DataFieldAttribute>() == null)
                    continue;
                if (member.GetCustomAttribute<AutoPausedFieldAttribute>() == null)
                    continue;

                yield return member;
            }
        }

        private static bool HasOffsetSerializer(MemberInfo member)
        {
            return member.GetCustomAttribute<DataFieldAttribute>()?.CustomTypeSerializer == typeof(TimeOffsetSerializer);
        }
    }
}
