using System.Diagnostics.CodeAnalysis;
using Content.Server._Triad.Shipyard;
using Content.Server._Triad.Shipyard.Load;
using Content.Server._Triad.Shipyard.Persistence;
using Content.Server.Shuttles.Components;
using Content.Shared._Crescent.ShipShields;
using Content.Shared._NF.Shipyard;
using Content.Shared.Shuttles.Components;
using Content.Shared.Station.Components;
using Content.Shared.Timing;
using Robust.Shared.ContentPack;
using Robust.Shared.Physics.Components;
using Robust.Shared.Utility;

namespace Content.Server._NF.Shipyard.Systems;

public sealed partial class ShipyardSystem : SharedShipyardSystem
{
    [Dependency] private ShipLoadRespawnSystem _shipLoadRespawn = default!;
    [Dependency] private UseDelaySystem _useDelay = default!;
    [Dependency] private ITriadShipyardConsumedStore _consumedStore = default!;

    /// <summary>
    /// Writes a legacy save's grid YAML to a temporary file and loads it the way a purchase loads its ship file.
    /// </summary>
    private bool TryPurchaseShuttleFromYamlData(EntityUid consoleUid, string yamlData, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;
        ResPath tempPath = default;
        try
        {
            // Triad: strip stale nodes before the loader sees them: dangling entity references from
            // pre-287 saves, legacy xenoarch entities/components from pre-rework saves, and the
            // map-init flag the old writer stripped, without which the loader map-inits every entity
            // a second time. Ship files live on the player's machine, so there is no backlog we can
            // migrate; a file written by the current build has nothing to change and comes back
            // byte-identical.
            yamlData = ShipSaveYamlSanitizer.ScrubShipLoadYaml(yamlData, out var scrubbed);
            if (scrubbed > 0)
                _sawmill.Info($"Scrubbed {scrubbed} node(s) (dangling references, legacy entities, missing map-init flags) from an older ship file on load");

            // Create a temp path under UserData/ShipyardTemp
            var fileName = $"shipyard_load_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.yml";
            var dir = new ResPath("/") / "UserData" / "ShipyardTemp";
            tempPath = dir / fileName;

            // Ensure directory exists and write file
            _resources.UserData.CreateDir(dir);
            using (var writer = _resources.UserData.OpenWriteText(tempPath))
            {
                writer.Write(yamlData);
            }

            return TryPurchaseShuttleFromFile(consoleUid, tempPath, out shuttleEntityUid);
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"Failed to purchase shuttle from YAML data: {ex.Message}");
            return false;
        }
        finally
        {
            try
            {
                if (tempPath != default && _resources.UserData.Exists(tempPath))
                    _resources.UserData.Delete(tempPath);
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    /// <summary>
    /// Loads a shuttle from a file and docks it to the grid the console is on, like ship purchases.
    /// Legacy import stages old ship saves through this.
    /// </summary>
    /// <param name="consoleUid">The entity of the shipyard console to dock to its grid</param>
    /// <param name="shuttlePath">The path to the shuttle file to load. Must be a grid file!</param>
    /// <param name="shuttleEntityUid">The EntityUid of the shuttle that was loaded</param>
    private bool TryPurchaseShuttleFromFile(EntityUid consoleUid, ResPath shuttlePath, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        if (!TryAddShuttle(shuttlePath, out var shuttleGrid)) // HardLight
        {
            shuttleEntityUid = null;
            return false;
        }

        return TryFinalizeLoadedShuttle(consoleUid, shuttleGrid.Value, out shuttleEntityUid);
    }

    /// <summary>
    /// Finalizes a loaded shuttle by removing any unneeded components and otherwise.
    /// </summary>
    private bool TryFinalizeLoadedShuttle(EntityUid consoleUid, EntityUid grid, [NotNullWhen(true)] out EntityUid? shuttleEntityUid)
    {
        shuttleEntityUid = null;

        // Get the grid the console is on
        if (!_transformQuery.TryComp(consoleUid, out var consoleXform) || consoleXform.GridUid == null)
            return false;

        if (!TryComp<ShuttleComponent>(grid, out var shuttleComponent))
            return false;

        if (TryComp<ShipShieldedComponent>(grid, out var shielded))
        {
            TryQueueDel(shielded.Shield);
            RemComp<ShipShieldedComponent>(grid);
        }

        // Ensure required components for docking and identification
        EnsureComp<PhysicsComponent>(grid);
        EnsureComp<ShuttleComponent>(grid);

        // Triad: the vessel's grant first. The bare component alone is the factory gold with no
        // flags, so an imported hull read as an unidentified contact on every mass scanner while a
        // purchased one of the same class read as white. The EnsureComp below is the import's own
        // floor for a hull that resolves to no vessel, and a no-op when the grant supplied one.
        GrantVesselComponents(grid, null);
        EnsureComp<IFFComponent>(grid);

        // Reset use delays on objects with the component so delays from previous rounds don't carry over
        TryResetUseDelays(grid);

        // Spawn the entities from entities with SpawnOnShipLoadComponent
        _shipLoadRespawn.RespawnMarkedEntities(grid);

        // Load-time sanitation: purge any deserialized joints and reset dock joint references
        // to avoid physics processing invalid joint bodies (e.g., Entity 0) from YAML.
        try
        {
            PurgeJointsAndResetDocks(grid);
        }
        catch (Exception ex)
        {
            _sawmill.Warning($"[ShipLoad] PurgeJointsAndResetDocks failed on {grid}: {ex.Message}");
        }

        // Add new grid to the same station as the console's grid (for IFF / ownership), if any
        var consoleGridUid = consoleXform.GridUid.Value;
        if (TryComp<StationMemberComponent>(consoleGridUid, out var stationMember))
            _station.AddGridToStation(stationMember.Station, grid);

        _shuttle.TryFTLDock(grid, shuttleComponent, consoleGridUid);
        shuttleEntityUid = grid;
        return true;
    }

    /// <summary>
    /// Tries to reset the delays on any entities with the UseDelayComponent.
    /// Needed to ensure items don't have prolonged delays after saving.
    /// </summary>
    private void TryResetUseDelays(EntityUid shuttleGrid)
    {
        var useDelayQuery = EntityQueryEnumerator<UseDelayComponent, TransformComponent>();

        while (useDelayQuery.MoveNext(out var uid, out var comp, out var xform))
        {
            if (xform.GridUid != shuttleGrid)
                continue;

            _useDelay.ResetAllDelays((uid, comp));
        }
    }
}
