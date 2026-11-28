namespace Content.Server._Triad.Drydock;

/// <summary>
/// Present on a grid for the span of a store attempt, as the re-entrancy sentinel and nothing else.
/// A store yields - on the database, and now on the tick budget for as long as the pipeline lasts -
/// so a second request for the same grid would otherwise run the whole preparation again over a ship
/// that is already halfway into a berth and file a second revision of it.
///
/// <para>It used to do a second job, refusing container insertion anywhere on the grid, through the
/// only unfiltered subscription to the insertion attempt in the solution. That is gone: from the
/// freeze onwards the ship sits on a private paused map with nobody aboard, so there is nothing left
/// that could insert into it, and every container insertion on the server was paying two component
/// lookups for the length of somebody else's store.</para>
///
/// <para><c>UnsavedComponent</c> is load-bearing, and more so than before. The marker is stamped
/// before the grid is serialized, so without it the marker rides the document into storage, comes
/// back on the retrieved ship, and the sentinel gate at the top of the store answers "already in
/// progress" to every future store of that hull, permanently. That is a worse failure than the
/// blocked containers the attribute used to be justified by.</para>
/// </summary>
[RegisterComponent, UnsavedComponent]
public sealed partial class DrydockInProgressComponent : Component;
