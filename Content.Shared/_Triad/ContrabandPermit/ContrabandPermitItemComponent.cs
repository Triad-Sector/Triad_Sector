using Robust.Shared.GameStates;
using Robust.Shared.Network;

namespace Content.Shared._Triad.ContrabandPermit;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class ContrabandPermitItemComponent : Component
{
    /// <summary>
    /// The name of the permit owner: the character the permit was issued to. Saved, and with
    /// <see cref="PermitOwnerAccount"/> it is how a permit recognises its holder after a store, a
    /// restart or a ship file, since the uid fields below are not.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string PermitOwnerName = string.Empty;

    /// <summary>
    /// The account of the player the permit was issued to: the half of the holder's identity a
    /// character name cannot give, since two players can name a character the same. Saved and
    /// deliberately not networked, because no client needs another player's account id. Null on
    /// every permit issued before it existed, and those are recognised by name alone.
    /// </summary>
    [DataField]
    public NetUserId? PermitOwnerAccount;

    [DataField, AutoNetworkedField]
    public string PermitReason = string.Empty;

    /// <summary>
    /// The UID of the permit owner. Session-local, re-stamped when a ship is retrieved or imported
    /// for the character the permit was issued to; never serialized.
    /// </summary>
    [AutoNetworkedField, ViewVariables]
    public EntityUid? PermitOwner;

    /// <summary>
    /// The sector service record key of the permit item. Will almost always be the humanoid view of the permit owner, unless the permit owner doesn't have a humanoid view.
    /// Not serialized.
    /// </summary>
    [ViewVariables]
    public NetEntity? PermitRecordKey;

    /// <summary>
    /// The mind of the permit owner. Used for checking if a permitted item should stay or be seized on a saved ship.
    /// Session-local, re-stamped when a ship is retrieved or imported for the character the permit
    /// was issued to; never serialized.
    /// </summary>
    [ViewVariables]
    public EntityUid? PermitOwnerMind;

    /// <summary>
    /// Flavor RP date of whenever the contraband permit was granted.
    /// </summary>
    [DataField, AutoNetworkedField]
    public string DateGranted = string.Empty;
}
