using Content.Client.UserInterface.Fragments;
using Content.Shared.CartridgeLoader;
using Robust.Client.GameObjects;
using Robust.Client.UserInterface;

namespace Content.Client.CartridgeLoader;


public abstract class CartridgeLoaderBoundUserInterface : BoundUserInterface
{
    [ViewVariables]
    private EntityUid? _activeProgram;

    [ViewVariables]
    private UIFragment? _activeCartridgeUI;

    [ViewVariables]
    private Control? _activeUiFragment;

    private IEntityManager _entManager;

    protected CartridgeLoaderBoundUserInterface(EntityUid owner, Enum uiKey) : base(owner, uiKey)
    {
        _entManager = IoCManager.Resolve<IEntityManager>();
    }

    protected override void UpdateState(BoundUserInterfaceState state)
    {
        base.UpdateState(state);

        if (state is not CartridgeLoaderUiState loaderUiState)
        {
            _activeCartridgeUI?.UpdateState(state);
            return;
        }

        // TODO move this to a component state and ensure the net ids.
        var programs = GetCartridgeComponents(_entManager.GetEntityList(loaderUiState.Programs));
        UpdateAvailablePrograms(programs);

        var activeUI = _entManager.GetEntity(loaderUiState.ActiveUI);

        // Triad begin: rebuild the cartridge UI only when the active program actually changes.
        // Upstream ran RetrieveCartridgeUI (and therefore UIFragment.Setup) on every loader state, which built a
        // fresh fragment, while the type-equality guard below then kept the OLD fragment attached. Every later
        // cartridge state landed on the detached fragment, so the visible one froze mid-session, or came up blank
        // on reopen. Any loader state resends this: the PDA light, inserting an ID or pen, a station rename.
        if (_activeProgram == activeUI && _activeUiFragment is not null)
            return;

        _activeProgram = activeUI;

        var ui = RetrieveCartridgeUI(activeUI);
        var comp = RetrieveCartridgeComponent(activeUI);
        var control = ui?.GetUIFragmentRoot();

        // Triad: upstream guarded on fragment TYPE here, which is what orphaned the fragment. Program identity
        // above is the real question, and it also lets two programs sharing a fragment type swap correctly.
        ////Prevent the same UI fragment from getting disposed and attached multiple times
        //if (_activeUiFragment?.GetType() == control?.GetType())
        //    return;
        // Triad end

        var previousFragment = _activeUiFragment;
        if (previousFragment is not null)
            DetachCartridgeUI(previousFragment);

        // Triad: publish the new fragment BEFORE announcing readiness, so the state the server sends back in
        // answer to CartridgeUiReadyEvent has somewhere to land. Upstream assigned these afterwards.
        _activeCartridgeUI = ui;
        _activeUiFragment = control;

        if (control is not null && _activeProgram.HasValue)
        {
            AttachCartridgeUI(control, Loc.GetString(comp?.ProgramName ?? "default-program-name"));
            SendCartridgeUiReadyEvent(_activeProgram.Value);
        }

        // Triad: disposed last, so the outgoing fragment tears down after the incoming one has claimed any
        // registration they share.
        previousFragment?.Dispose();
    }

    protected void ActivateCartridge(EntityUid cartridgeUid)
    {
        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(cartridgeUid), CartridgeUiMessageAction.Activate);
        SendMessage(message);
    }

    protected void DeactivateActiveCartridge()
    {
        if (!_activeProgram.HasValue)
            return;

        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(_activeProgram.Value), CartridgeUiMessageAction.Deactivate);
        SendMessage(message);
    }

    protected void InstallCartridge(EntityUid cartridgeUid)
    {
        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(cartridgeUid), CartridgeUiMessageAction.Install);
        SendMessage(message);
    }

    protected void UninstallCartridge(EntityUid cartridgeUid)
    {
        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(cartridgeUid), CartridgeUiMessageAction.Uninstall);
        SendMessage(message);
    }

    private List<(EntityUid, CartridgeComponent)> GetCartridgeComponents(List<EntityUid> programs)
    {
        var components = new List<(EntityUid, CartridgeComponent)>();

        foreach (var program in programs)
        {
            var component = RetrieveCartridgeComponent(program);
            if (component is not null)
                components.Add((program, component));
        }

        return components;
    }

    /// <summary>
    /// The implementing ui needs to add the passed ui fragment as a child to itself
    /// </summary>
    protected abstract void AttachCartridgeUI(Control cartridgeUIFragment, string? title);

    /// <summary>
    /// The implementing ui needs to remove the passed ui from itself
    /// </summary>
    protected abstract void DetachCartridgeUI(Control cartridgeUIFragment);

    protected abstract void UpdateAvailablePrograms(List<(EntityUid, CartridgeComponent)> programs);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
            _activeUiFragment?.Dispose();
    }

    protected CartridgeComponent? RetrieveCartridgeComponent(EntityUid? cartridgeUid)
    {
        return EntMan.GetComponentOrNull<CartridgeComponent>(cartridgeUid);
    }

    private void SendCartridgeUiReadyEvent(EntityUid cartridgeUid)
    {
        var message = new CartridgeLoaderUiMessage(_entManager.GetNetEntity(cartridgeUid), CartridgeUiMessageAction.UIReady);
        SendMessage(message);
    }

    private UIFragment? RetrieveCartridgeUI(EntityUid? cartridgeUid)
    {
        var component = EntMan.GetComponentOrNull<UIFragmentComponent>(cartridgeUid);
        component?.Ui?.Setup(this, cartridgeUid);
        return component?.Ui;
    }
}
