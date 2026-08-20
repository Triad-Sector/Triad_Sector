using Content.Shared.Radio;
using Robust.Shared.Audio; // Triad: send tones
using Robust.Shared.Prototypes;

namespace Content.Server._DV.CartridgeLoader.Cartridges;

[RegisterComponent, Access(typeof(NanoChatCartridgeSystem))]
public sealed partial class NanoChatCartridgeComponent : Component
{
    /// <summary>
    ///     The NanoChat card to keep track of.
    /// </summary>
    [DataField]
    public EntityUid? Card;

    /// <summary>
    ///     The <see cref="RadioChannelPrototype" /> required to send or receive messages.
    /// </summary>
    [DataField]
    public ProtoId<RadioChannelPrototype> RadioChannel = "Common";

    // Triad begin: send tones.
    // Both play to the SENDER ONLY, on purpose. Receiving a message already rings the PDA out loud for the whole
    // room, so making sends audible to bystanders would put two public noises on every exchange, and it would
    // leak "this person just texted someone" as a stealth signal. That is a balance decision, not polish, so it
    // stays local until someone decides otherwise: widen the filter in HandleSendMessage if that day comes.

    /// <summary>
    ///     Played to the sender when a message goes out.
    /// </summary>
    /// <remarks>
    ///     Pulled well below the volume everything else uses for this file (the fax plays it at 0, the hand
    ///     teleporter and the contraband permit chip at -2). Those are room sounds you hear across a distance for
    ///     a one-off event; this one plays point blank into the sender's own ears, several times a conversation.
    /// </remarks>
    [DataField]
    public SoundSpecifier? SendSound = new SoundPathSpecifier("/Audio/Machines/high_tech_confirm.ogg")
    {
        Params = AudioParams.Default.WithVolume(-8f),
    };

    /// <summary>
    ///     Played to the sender when a message could not be delivered, so the failure is audible without having
    ///     to go back and read the bubble for the "Failed to deliver" line. Kept a couple of dB above
    ///     <see cref="SendSound"/>, since a failure is the one worth looking up for.
    /// </summary>
    [DataField]
    public SoundSpecifier? SendFailedSound = new SoundPathSpecifier("/Audio/Machines/buzz-two.ogg")
    {
        Params = AudioParams.Default.WithVolume(-6f),
    };
    // Triad end
}
