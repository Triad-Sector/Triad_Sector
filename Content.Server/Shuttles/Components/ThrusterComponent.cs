using System.Numerics;
using Content.Server._Triad.Shuttles.Systems;
using Content.Server.Shuttles.Systems;
using Content.Shared.Construction.Prototypes;
using Content.Shared.Damage;
using Content.Shared.DeviceLinking; // Frontier
using Content.Shared.Whitelist; // Triad
using Robust.Shared.Audio; // Triad
using Robust.Shared.GameStates;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom;
using Robust.Shared.Prototypes;

namespace Content.Server.Shuttles.Components
{
    [RegisterComponent, NetworkedComponent, AutoGenerateComponentPause]
    [Access(typeof(ThrusterSystem))]
    public sealed partial class ThrusterComponent : Component
    {
        /// <summary>
        /// Whether the thruster has been force to be enabled / disabled (e.g. VV, interaction, etc.)
        /// </summary>
        [DataField, ViewVariables(VVAccess.ReadWrite)]
        public bool Enabled { get; set; } = true;

        /// <summary>
        /// This determines whether the thruster is actually enabled for the purposes of thrust
        /// </summary>
        public bool IsOn;

        // Need to serialize this because RefreshParts isn't called on Init and this will break post-mapinit maps!
        [ViewVariables(VVAccess.ReadWrite), DataField("thrust")]
        public float Thrust = 200f; // 100f->200f Mono

        [DataField("baseThrust"), ViewVariables(VVAccess.ReadWrite)]
        public float BaseThrust = 200f; // 100f->200f Mono

        [DataField("thrusterType")]
        public ThrusterType Type = ThrusterType.Linear;

        [DataField("burnShape")] public List<Vector2> BurnPoly = new()
        {
            new Vector2(-0.4f, 0.5f),
            new Vector2(-0.1f, 1.2f),
            new Vector2(0.1f, 1.2f),
            new Vector2(0.4f, 0.5f)
        };

        /// <summary>
        /// How much damage is done per second to anything colliding with our thrust.
        /// </summary>
        [DataField("damage")] public DamageSpecifier? Damage = new();

        [DataField("requireSpace")]
        public bool RequireSpace = true;

        // Used for burns

        // Triad start
        [ViewVariables]
        public HashSet<EntityUid> Colliding = new();

        [DataField]
        public LocId BurnPopupOther = "thruster-comp-burn-others";

        [DataField]
        public LocId BurnPopupSelf = "thruster-comp-burn-self";

        [DataField]
        public SoundSpecifier? BurnSound = new SoundPathSpecifier("/Audio/Effects/lightburn.ogg");

        [DataField]
        public float MaximumThrusterBurnRange = 5.5f;

        [DataField]
        public float MaximumMobThrusterBurnRange = 2.0f;

        [DataField]
        public float MobBurnDamageMultiplier = 0.3f;

        [DataField]
        public float DistanceBurnDamageMultiplier = 0.85f;
        // Triad end

        public bool Firing = false;

        /// <summary>
        /// How often thruster deals damage.
        /// </summary>
        [DataField]
        public TimeSpan FireCooldown = TimeSpan.FromSeconds(2);

        [DataField(customTypeSerializer: typeof(TimeOffsetSerializer)), AutoPausedField]
        public TimeSpan NextFire = TimeSpan.Zero;

        // Frontier: upgradeable parts, togglable thrust
        [DataField("machinePartThrust")]
        public ProtoId<MachinePartPrototype> MachinePartThrust = "Capacitor";

        [DataField("partRatingThrustMultiplier")]
        public float PartRatingThrustMultiplier = 1.25f; // Mono: 1.15->1.25 (frontier: 1.5->1.15)

        /// <summary>
        ///     Frontier - Amount of charge this needs from an APC per second to function.
        /// </summary>
        public float OriginalLoad { get; set; } = 0;

        /// <summary>
        ///     Frontier - Make linkable to buttons
        /// </summary>
        [DataField("onPort")] // Frontier
        public ProtoId<SinkPortPrototype> OnPort = "On"; // Frontier

        [DataField("offPort")] // Frontier
        public ProtoId<SinkPortPrototype> OffPort = "Off"; // Frontier

        [DataField("togglePort")] // Frontier
        public ProtoId<SinkPortPrototype> TogglePort = "Toggle"; // Frontier
        // End Frontier: upgradeable parts, togglable thrust

        // Mono
        /// <summary>
        ///     If we have a <see cref="ThermalSignatureComponent"/>, heat signature output per thrust while working.
        /// </summary>
        [DataField]
        public float HeatSignatureRatio = 40f;

        /// <summary>
        ///     Triad - Which type of entities block thruster paths?
        /// </summary>
        [DataField]
        public EntityWhitelist? BlockThrusterWhitelist;

        /// <summary>
        ///     Triad - Which type of entities can be burnt by thrusters?
        /// </summary>
        [DataField]
        public EntityWhitelist? BurnWhitelist;

        /// <summary>
        ///     Triad - Which type of entities cannot be burnt by thrusters?
        /// </summary>
        [DataField]
        public EntityWhitelist? BurnBlacklist;

        /// <summary>
        ///     Triad - Which type of thruster is this?
        ///     This is used for raycast checks to determine if the thruster's path is blocked.
        /// </summary>
        [DataField]
        public ThrusterBlockingProfile ThrusterProfile = ThrusterBlockingProfile.Small;
    }

    public enum ThrusterType
    {
        Linear,
        // Angular meaning rotational.
        Angular,
    }
}
