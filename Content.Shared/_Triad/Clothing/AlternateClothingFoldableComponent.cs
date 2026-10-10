using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Clothing;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
[Access(typeof(TriadClothingSystem))]
public sealed partial class AlternateClothingFoldableComponent : Component
{
    [DataField, AutoNetworkedField]
    public string? ActivatedPrefix;

    [DataField, AutoNetworkedField]
    public List<ClothingAlternateFoldableType> Types = new();
}

/// <summary>
/// Prefix is the clothing prefix when this foldable type is activated. BlacklistedPrefix will prevent this foldable type
/// from activating while the blacklisted one is activated.
/// </summary>
[DataRecord]
[Serializable, NetSerializable]
public partial record struct ClothingAlternateFoldableType(string Prefix, LocId Name, int Priority, string? BlacklistedPrefix, LocId? BlacklistPopup);
