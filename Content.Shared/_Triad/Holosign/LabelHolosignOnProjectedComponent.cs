using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared._Triad.Holosign;

[RegisterComponent, NetworkedComponent, AutoGenerateComponentState]
public sealed partial class LabelHolosignOnProjectedComponent : Component
{
    [DataField, AutoNetworkedField]
    public string Description = string.Empty;

    [DataField, AutoNetworkedField]
    public int MaxDescriptionCharacters = 512;

    [DataField, AutoNetworkedField]
    public bool Explicit = false;
}

[Serializable, NetSerializable]
public enum LabelHolosignProjectorUIKey
{
    Key,
}

[Serializable, NetSerializable]
public sealed class LabelHolosignProjectorDescriptionMessage(string description, bool isExplicit) : BoundUserInterfaceMessage
{
    public string Description { get; } = description;
    public bool Explicit { get; } = isExplicit;
}
