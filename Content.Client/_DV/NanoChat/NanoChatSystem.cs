using Content.Client._DV.CartridgeLoader.Cartridges;
using Content.Shared._DV.CartridgeLoader.Cartridges;
using Content.Shared._DV.NanoChat;

namespace Content.Client._DV.NanoChat;

public sealed class NanoChatSystem : SharedNanoChatSystem
{
    /// <summary>
    ///     The fragment currently attached to the UI tree, if any. Only one PDA UI can be open locally at a time,
    ///     so there is never more than one on-screen listener.
    /// </summary>
    /// <remarks>
    ///     Triad: registration is driven by the fragment entering and leaving the UI tree, NOT by its constructor.
    ///     A fragment that was built but never attached must not capture this slot, or typing pushes get delivered
    ///     to a control nobody can see.
    /// </remarks>
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
        // Identity-checked: during a swap the incoming fragment may register before the outgoing one detaches,
        // and the straggler must not clear the live registration.
        if (_activeFragment == fragment)
            _activeFragment = null;
    }

    private void OnTypingIndicator(NanoChatTypingIndicatorEvent ev)
    {
        _activeFragment?.SetTyping(ev.SenderNumber, ev.Typing);
    }
}
