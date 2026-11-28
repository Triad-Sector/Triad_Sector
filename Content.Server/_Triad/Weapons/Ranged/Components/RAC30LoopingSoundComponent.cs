
using Robust.Shared.Audio;
using Robust.Shared.GameObjects;

namespace Content.Server.Weapons.Ranged.Components;

[RegisterComponent]
public sealed partial class RAC30LoopingSoundComponent : Component
{
    [DataField]
    public SoundSpecifier LoopSound = new SoundPathSpecifier(
        "/Audio/_Mono/Weapons/Guns/Gunshots/slammer.ogg");

    public EntityUid? AudioStream;

    public TimeSpan StopTime;

    public TimeSpan? PendingStartTime;
}
