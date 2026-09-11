using Content.Shared.Damage;
using Content.Shared.DoAfter;
using Content.Shared.Whitelist;
using Robust.Shared.Audio;
using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.ContrabandPermit;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ContrabandPermitChipComponent : Component
{
    /// <summary>
    /// The net ent of the scanned permit chip.
    /// </summary>
    [DataField, AutoNetworkedField]
    public NetEntity? ScannedItem;

    /// <summary>
    /// The net ent of the permit carrier/owner.
    /// </summary>
    [DataField, AutoNetworkedField]
    public NetEntity? ScannedPermitCarrier;

    [DataField, AutoNetworkedField]
    public EntityWhitelist? PermitCarrierWhitelist;

    [DataField, AutoNetworkedField]
    public EntityWhitelist? PermitCarrierBlacklist;

    [DataField, AutoNetworkedField]
    public TimeSpan ScanIdDelay = TimeSpan.FromSeconds(5);

    [DataField, AutoNetworkedField]
    public SoundSpecifier? ScanSound =
        new SoundPathSpecifier("/Audio/Machines/high_tech_confirm.ogg")
        {
            Params = AudioParams.Default.WithVolume(-2f)
        };

    [DataField, AutoNetworkedField]
    public SoundSpecifier? ClearSound = new SoundPathSpecifier("/Audio/Machines/custom_deny.ogg");

    [DataField, AutoNetworkedField]
    public DamageSpecifier PrickDamage = new();
}

[Serializable, NetSerializable]
public sealed partial class ContrabandPermitChipScanIdentityDoAfterEvent : SimpleDoAfterEvent;
