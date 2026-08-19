using Content.Client.UserInterface.Fragments;
using Content.Shared.CartridgeLoader;
using Content.Shared._DV.CartridgeLoader.Cartridges;
using Robust.Client.UserInterface;

namespace Content.Client._DV.CartridgeLoader.Cartridges;

public sealed partial class NanoChatUi : UIFragment
{
    private NanoChatUiFragment? _fragment;

    public override Control GetUIFragmentRoot()
    {
        return _fragment!;
    }

    public override void Setup(BoundUserInterface userInterface, EntityUid? fragmentOwner)
    {
        // Triad: this UIFragment lives on the cartridge's client component, so it outlives the BoundUserInterface
        // and Setup runs again on every reopen, binding to the new BUI. Tear the previous fragment down first so it
        // cannot linger holding its typing-indicator registration or its popup windows.
        _fragment?.Dispose();

        _fragment = new NanoChatUiFragment();

        _fragment.OnMessageSent += (type, number, content, job) =>
        {
            SendNanoChatUiMessage(type, number, content, job, userInterface);
        };
    }

    public override void UpdateState(BoundUserInterfaceState state)
    {
        if (state is NanoChatUiState cast)
            _fragment?.UpdateState(cast);
    }

    private static void SendNanoChatUiMessage(NanoChatUiMessageType type,
        uint? number,
        string? content,
        string? job,
        BoundUserInterface userInterface)
    {
        var nanoChatMessage = new NanoChatUiMessageEvent(type, number, content, job);
        var message = new CartridgeUiMessage(nanoChatMessage);
        userInterface.SendMessage(message);
    }
}
