using Content.Client._DV.CartridgeLoader.Cartridges;
using Content.Shared._DV.CartridgeLoader.Cartridges;
using Content.Shared._DV.NanoChat;

namespace Content.Client._DV.NanoChat;

public sealed class NanoChatSystem : SharedNanoChatSystem
{
    // Triad: only one PDA UI can be open locally at a time, so there is never more than one live listener.
    private NanoChatUiFragment? _activeFragment;

    public override void Initialize()
    {
        base.Initialize();
        SubscribeNetworkEvent<NanoChatTypingIndicatorEvent>(OnTypingIndicator);
    }

    public void RegisterFragment(NanoChatUiFragment fragment)
    {
        _activeFragment = fragment;
    }

    public void UnregisterFragment(NanoChatUiFragment fragment)
    {
        if (_activeFragment == fragment)
            _activeFragment = null;
    }

    private void OnTypingIndicator(NanoChatTypingIndicatorEvent ev)
    {
        _activeFragment?.SetTyping(ev.SenderNumber, ev.Typing);
    }
}
