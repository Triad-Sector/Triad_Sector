using Content.Shared._Triad.Holosign;
using JetBrains.Annotations;
using Robust.Client.UserInterface;

namespace Content.Client._Triad.Holosign.UI;

[UsedImplicitly]
public sealed partial class HolosignProjectorLabelBUI : BoundUserInterface
{
    [Dependency] private IEntityManager _entManager = default!;

    [ViewVariables]
    private HolosignProjectorLabelWindow? _window;

    public HolosignProjectorLabelBUI(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        IoCManager.InjectDependencies(this);
    }

    protected override void Open()
    {
        base.Open();
        _window = this.CreateWindow<HolosignProjectorLabelWindow>();
        _window.OnDescriptionChanged += OnDescriptionChanged;
        _window.OnExplicitChanged += OnExplicitStatusChanged;
        _window.SetOwner(Owner);
        _window.Reload();
    }

    private void OnDescriptionChanged(string description)
    {
        if (!_entManager.TryGetComponent(Owner, out LabelHolosignOnProjectedComponent? projector) ||
            projector.Description.Equals(description))
            return;

        SendPredictedMessage(new LabelHolosignProjectorDescriptionMessage(description, projector.Explicit));
    }

    private void OnExplicitStatusChanged(bool isExplicit)
    {
        if (!_entManager.TryGetComponent(Owner, out LabelHolosignOnProjectedComponent? projector))
            return;

        SendPredictedMessage(new LabelHolosignProjectorDescriptionMessage(projector.Description, isExplicit));
    }
}
