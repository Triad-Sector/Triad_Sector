// Triad: the drydock's round-end sweep holds the restart. The one call in RestartRound is marked
// in the upstream file; the dependency behind it lives here so that file gains no field.
using Content.Server._Triad.Drydock;

namespace Content.Server.GameTicking;

public sealed partial class GameTicker
{
    [Dependency] private DrydockSystem _drydock = default!;

    /// <summary>
    /// The round to stamp an audit row with, or null when there is no round yet.
    ///
    /// <para><see cref="RoundId"/> reads 0 before a round has been filed, and the round columns are
    /// real foreign keys, so passing that straight through makes the insert fail on a constraint
    /// rather than recording "no round". Nullable is what the schema means by it.</para>
    /// </summary>
    public int? RoundIdOrNull => RoundId > 0 ? RoundId : null;
}
