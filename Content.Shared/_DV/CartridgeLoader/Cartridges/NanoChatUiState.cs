using System.Linq;
using Robust.Shared.Serialization;

namespace Content.Shared._DV.CartridgeLoader.Cartridges;

[Serializable, NetSerializable]
public sealed class NanoChatUiState : BoundUserInterfaceState
{
    public readonly Dictionary<uint, NanoChatRecipient> Recipients = [];
    public readonly Dictionary<uint, List<NanoChatMessage>> Messages = [];
    public readonly HashSet<uint> MutedChats = [];
    public readonly List<NanoChatRecipient>? Contacts;
    public readonly uint? CurrentChat;
    public readonly uint OwnNumber;
    public readonly int MaxRecipients;
    public readonly bool NotificationsMuted;
    public readonly bool ListNumber;

    /// <summary>
    /// Triad: whether this card is a legally issued ID and may therefore change its directory listing.
    /// </summary>
    public readonly bool CanList;

    public NanoChatUiState(
        Dictionary<uint, NanoChatRecipient> recipients,
        Dictionary<uint, List<NanoChatMessage>> messages,
        HashSet<uint> mutedChats,
        List<NanoChatRecipient>? contacts,
        uint? currentChat,
        uint ownNumber,
        int maxRecipients,
        bool notificationsMuted,
        bool listNumber,
        bool canList = false) // Triad
    {
        // Triad: snapshot the collections instead of aliasing them. Callers hand us the card component's live
        // dictionaries, and this state object outlives the call: the UI system retains it, replays it into the
        // next BoundUserInterface, and compares it against the following state to decide whether anything
        // changed. Sharing the references let later card mutations rewrite a state that had already been sent.
        Recipients = new Dictionary<uint, NanoChatRecipient>(recipients);
        Messages = messages.ToDictionary(chat => chat.Key, chat => new List<NanoChatMessage>(chat.Value));
        MutedChats = new HashSet<uint>(mutedChats);
        Contacts = contacts;
        CurrentChat = currentChat;
        OwnNumber = ownNumber;
        MaxRecipients = maxRecipients;
        NotificationsMuted = notificationsMuted;
        ListNumber = listNumber;
        CanList = canList; // Triad
    }
}
