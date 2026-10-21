using Content.Shared.NPC.Prototypes;
using Robust.Shared.GameStates;
using Robust.Shared.Prototypes;

namespace Content.Shared._Triad.NPC;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState, AutoGenerateComponentPause]
[Access(typeof(TriadNpcFactionSystem))]
public sealed partial class BlockAttackingNpcFactionsComponent : Component
{
    /// <summary>
    /// Factions that cannot be harmed by direct attacks from the entity.
    /// </summary>
    [DataField("factions"), AutoNetworkedField]
    public HashSet<ProtoId<NpcFactionPrototype>> BlockedFactions = new();

    /// <summary>
    /// Popup that shows on an attack attempt against a blocked faction.
    /// </summary>
    [DataField, AutoNetworkedField]
    public LocId? Popup = "pacified-cannot-harm-directly";

    /// <summary>
    /// The last entity attacked, used for popup purposes (avoid spam)
    /// </summary>
    [DataField]
    public EntityUid? LastAttackedEntity = null;

    /// <summary>
    /// When attempting attack against the same entity multiple times,
    /// don't spam popups every frame and instead have a cooldown.
    /// </summary>
    [DataField]
    public TimeSpan PopupCooldown = TimeSpan.FromSeconds(3.0);

    /// <summary>
    /// Time at which the next popup can be shown.
    /// </summary>
    [DataField]
    [AutoPausedField]
    public TimeSpan? NextPopupTime = null;
}
