using Robust.Shared.Prototypes;
using Robust.Shared.Audio.Systems;
using Robust.Shared.Containers; // Frontier

namespace Content.Shared.Audio.Jukebox;

public abstract partial class SharedJukeboxSystem : EntitySystem
{
    [Dependency] protected SharedAudioSystem Audio = default!;
    [Dependency] protected IPrototypeManager _protoManager = default!; // wizden#42210

    // wizden#42210 + Triad: list full jukebox catalog (wizden-style). Frontier jukeboxes may also expose
    // extra tracks only via inserted music discs (JukeboxContainerComponent); union those when present.
    public IEnumerable<JukeboxPrototype> GetAvailableTracks(Entity<JukeboxComponent> entity)
    {
        var availableMusic = new HashSet<JukeboxPrototype>();

        foreach (var proto in _protoManager.EnumeratePrototypes<JukeboxPrototype>())
            availableMusic.Add(proto);

        // Frontier: Music Discs (optional — many jukebox entities have no containers)
        if (!TryComp<ContainerManagerComponent>(entity.Owner, out var containers))
            return availableMusic;

        foreach (var container in containers.Containers.Values)
        {
            foreach (var ent in container.ContainedEntities)
            {
                if (!TryComp(ent, out JukeboxContainerComponent? tracklist))
                    continue;

                foreach (var trackID in tracklist.Tracks)
                {
                    if (_protoManager.TryIndex(trackID, out var track))
                        availableMusic.Add(track);
                }
            }
        }

        return availableMusic;
    }
}
