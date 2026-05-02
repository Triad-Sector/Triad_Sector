using Content.Server.Administration;
using Content.Shared.Administration;
using Content.Shared._NF.Shipyard.Components;
using Content.Server._NF.Shipyard.Systems;
using Robust.Shared.Console;

namespace Content.Server.Shuttles.Save
{
    [AdminCommand(AdminFlags.Debug)]
    public sealed class SaveShipCommand : IConsoleCommand
    {
        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] private readonly IEntitySystemManager _entitySystemManager = default!;

        public string Command => "saveship";
        public string Description => "Save a ship from a shuttle deed";
        public string Help => "saveship <deed_entity_id>";

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (args.Length != 1)
            {
                shell.WriteLine("Usage: saveship <deed_entity_id>");
                return;
            }

            if (!int.TryParse(args[0], out var entityIdInt))
            {
                shell.WriteLine("Invalid entity ID");
                return;
            }

            var entityUid = new EntityUid(entityIdInt);
            if (!_entityManager.EntityExists(entityUid))
            {
                shell.WriteLine("Entity does not exist");
                return;
            }

            if (!_entityManager.TryGetComponent<ShuttleDeedComponent>(entityUid, out var deedComponent))
            {
                shell.WriteLine("Entity is not a shuttle deed");
                return;
            }

            var shuttleUid = deedComponent.ShuttleUid;
            if (shuttleUid == null)
            {
                shell.WriteLine("Grid not found for this deed");
                return;
            }

            if (shell.Player == null)
            {
                shell.WriteLine("No player session found");
                return;
            }

            var shipGridSaveSystem = _entitySystemManager.GetEntitySystem<ShipyardGridSaveSystem>();

            // Try to save the ship now
            if (shipGridSaveSystem.TrySaveShip(shuttleUid.Value, entityUid, shell.Player))
                shell.WriteLine($"Saved ship for deed {entityUid}");
            else
                shell.WriteLine($"Failed to save ship for deed {entityUid}");
        }
    }
}
