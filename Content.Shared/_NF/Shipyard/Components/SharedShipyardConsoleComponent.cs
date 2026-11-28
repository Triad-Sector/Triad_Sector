using Robust.Shared.GameStates;
using Robust.Shared.Audio;
using Content.Shared.Containers.ItemSlots;
using Robust.Shared.Prototypes;
using Content.Shared.Radio;
using Content.Shared.Access;
using Content.Shared._NF.Bank.Components;
using Content.Shared.Roles;
using Content.Shared.Whitelist; // Triad

namespace Content.Shared._NF.Shipyard.Components;

[RegisterComponent, NetworkedComponent, Access(typeof(SharedShipyardSystem))]
public sealed partial class ShipyardConsoleComponent : Component
{
    public static string TargetIdCardSlotId = "ShipyardConsole-targetId";

    [DataField("targetIdSlot")]
    public ItemSlot TargetIdSlot = new();

    [DataField("soundError")]
    public SoundSpecifier ErrorSound =
        new SoundPathSpecifier("/Audio/Effects/Cargo/buzz_sigh.ogg");

    [DataField("soundConfirm")]
    public SoundSpecifier ConfirmSound =
        new SoundPathSpecifier("/Audio/Effects/Cargo/ping.ogg");

    /// <summary>
    /// The comms channel that announces the ship purchase. The purchase is *always* announced
    /// on this channel.
    /// </summary>
    [DataField("shipyardChannel")]
    public ProtoId<RadioChannelPrototype> ShipyardChannel = "Traffic";

    /// <summary>
    /// A second comms channel that announces the ship purchase, with some information redacted.
    /// Currently used for black market and syndicate shipyards to alert the NFSD.
    /// </summary>
    [DataField("secretShipyardChannel")]
    public ProtoId<RadioChannelPrototype>? SecretShipyardChannel = null;

    /// <summary>
    /// If non-empty, specifies the new job title that should be given to the owner of the ship.
    /// </summary>
    [DataField]
    public string? NewJobTitle = null;

    /// <summary>
    /// Access levels to be added to the owner's ID card.
    /// </summary>
    [DataField]
    public List<ProtoId<AccessLevelPrototype>> NewAccessLevels = new();

    /// <summary>
    /// Indicates that the deeds that come from this console can be copied and transferred.
    /// </summary>
    [DataField]
    public bool CanTransferDeed = true;

    /// <summary>
    /// The accounts to receive payment, and the tax rate to apply for ship sales from this console.
    /// </summary>
    [DataField]
    public Dictionary<SectorBankAccount, float> TaxAccounts = new();

    /// <summary>
    /// If true, the base sale rate is ignored before calculating taxes.
    /// </summary>
    [DataField]
    public bool IgnoreBaseSaleRate;

    /// <summary>
    /// Triad - Who may use this console's drydock. Named for the ship saving it once gated.
    /// </summary>
    [DataField]
    public EntityWhitelist? ShipSaveWhitelist;

    /// <summary>
    /// Triad - Who is barred from this console's drydock. Named for the ship saving it once gated.
    /// </summary>
    [DataField]
    public EntityWhitelist? ShipSaveBlacklist;

    /// <summary>
    /// Triad: the drydock tab's state for whoever is currently at this console: the lists a
    /// refresh reads from the database, the access and reissue flags read from the world at the
    /// same swap, and the live store/retrieve percentage. See <see cref="BUI.DrydockTabState"/>.
    ///
    /// <para>A cache rather than a lookup because the lists come from the database and the state
    /// builder that has to publish them is synchronous. The drydock handlers fill this from an
    /// awaited read and then push the state; every other refresh path just re-sends whatever is
    /// here. Not a <c>DataField</c>: it is per-operator scratch, and persisting one player's
    /// drydock tab onto a mapped console would show it to the next person who opened it.</para>
    /// </summary>
    public BUI.DrydockTabState CachedDrydock = BUI.DrydockTabState.Empty;

    /// <summary>
    /// Triad: legacy saves this operator may import, cached for the same reason
    /// <see cref="CachedDrydock"/> is. Kept as its own field rather than folded into
    /// <see cref="CachedDrydock"/>: it is written from the import message handlers, not the
    /// drydock refresh, and answers a manifest the client sends rather than a database read.
    /// </summary>
    public List<BUI.DrydockImportShipInfo> CachedImportables = new();

    /// <summary>
    /// Triad: the candidates behind <see cref="CachedImportables"/>, keyed by the client's file id.
    ///
    /// <para>This is what makes an import message safe to act on: it may only name a file the server
    /// itself just offered, and the hash recorded here is compared with the one re-derived from the
    /// payload, so a modified client cannot send one file's id with another file's bytes. Cleared
    /// and rebuilt on every manifest.</para>
    /// </summary>
    public Dictionary<string, Events.DrydockImportCandidate> OfferedImports = new();

    /// <summary>
    /// Triad: the account the offered imports were judged for. A console is not single-occupancy, and
    /// a list built for one captain must not be spendable by the next one to walk up to it.
    /// </summary>
    public Guid? ImportOfferAccount;

    /// <summary>
    /// Triad: one import at a time on this console, the counterpart of the in-progress marker a store
    /// puts on the grid it is saving.
    ///
    /// <para>An import awaits the database several times before it files anything, and the ledger
    /// that would catch a duplicate is only consulted under enforce, so on a notifying server nothing
    /// else stands between a double press and one save becoming two ships in two berths.</para>
    /// </summary>
    public bool ImportInProgress;
}
