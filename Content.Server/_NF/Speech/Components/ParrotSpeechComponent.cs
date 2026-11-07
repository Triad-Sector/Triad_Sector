using Content.Server.Speech.EntitySystems;
using Content.Shared.Whitelist;
using Robust.Shared.Serialization.TypeSerializers.Implementations.Custom; // Triad - for TimeOffsetSerializer

namespace Content.Server.Speech.Components;

[RegisterComponent]
[Access(typeof(ParrotSpeechSystem))]
public sealed partial class ParrotSpeechComponent : Component
{
    /// <summary>
    /// The maximum number of words the parrot can learn per phrase.
    /// Phrases are 1 to MaxPhraseLength words in length.
    /// </summary>
    [DataField]
    public int MaximumPhraseLength = 7;

    [DataField]
    public int MaximumPhraseCount = 10;

    [DataField]
    public int MinimumWait = 120; // 120 / 60 = 2 minutes

    [DataField]
    public int MaximumWait = 300; // 300 / 60 = 5 minutes

    /// <summary>
    /// The probability that a parrot will learn from something an overheard phrase.
    /// </summary>
    [DataField]
    public float LearnChance = 0.2f;

    [DataField]
    public EntityWhitelist Blacklist { get; private set; } = new();

    [DataField(customTypeSerializer: typeof(TimeOffsetSerializer))]
    public TimeSpan? NextUtterance; // Triad - TimeOffsetSerializer: absolute game time, re-based on load so a saved ship does not carry the previous server clock

    [DataField(readOnly: true)]
    public List<string> LearnedPhrases = new();
}
