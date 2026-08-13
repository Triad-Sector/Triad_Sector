using Robust.Shared.Serialization;

namespace Content.Shared._DV.CartridgeLoader.Cartridges;

/// <summary>
///     Triad: pushed from server to a single recipient's client when a contact starts or stops composing
///     a message to them. Kept separate from <see cref="NanoChatUiState"/> since that carries the full
///     recipients/messages payload and would be far too heavy to push on every keystroke.
/// </summary>
[Serializable, NetSerializable]
public sealed class NanoChatTypingIndicatorEvent : EntityEventArgs
{
    /// <summary>
    ///     The NanoChat number of the contact who started or stopped typing.
    /// </summary>
    public readonly uint SenderNumber;

    /// <summary>
    ///     Whether the contact is now typing, or has stopped.
    /// </summary>
    public readonly bool Typing;

    public NanoChatTypingIndicatorEvent(uint senderNumber, bool typing)
    {
        SenderNumber = senderNumber;
        Typing = typing;
    }
}
