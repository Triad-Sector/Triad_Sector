using Robust.Shared.Player;
using Content.Shared._NF.Shuttles.Save;

namespace Content.Server.Shuttles.Save
{
    public sealed class ShipSaveSystem : EntitySystem
    {

        // Static caches for admin ship save interactions
        private static readonly Dictionary<string, Action<string>> PendingAdminRequests = new();
        private static readonly Dictionary<string, List<(string filename, string shipName, DateTime timestamp)>> PlayerShipCache = new();

        public override void Initialize()
        {
            base.Initialize();

            SubscribeNetworkEvent<RequestLoadShipMessage>(OnRequestLoadShip);
            SubscribeNetworkEvent<RequestAvailableShipsMessage>(OnRequestAvailableShips);
            SubscribeNetworkEvent<AdminSendPlayerShipsMessage>(OnAdminSendPlayerShips);
            SubscribeNetworkEvent<AdminSendShipDataMessage>(OnAdminSendShipData);
        }

        private void OnRequestLoadShip(RequestLoadShipMessage msg, EntitySessionEventArgs args)
        {
            var playerSession = args.SenderSession;
            if (playerSession == null)
                return;

            Logger.Info($"Player {playerSession.Name} requested to load ship from YAML data");

            // TODO: Implement ship loading from saved files
            // This would involve deserializing the ship data and spawning it in the game world
            // For now, we just log the request
        }

        private void OnRequestAvailableShips(RequestAvailableShipsMessage msg, EntitySessionEventArgs args)
        {
            var playerSession = args.SenderSession;
            if (playerSession == null)
                return;

            // Client handles available ships from local user data
            Logger.Info($"Player {playerSession.Name} requested available ships - client handles this locally");
        }

        private void OnAdminSendPlayerShips(AdminSendPlayerShipsMessage msg, EntitySessionEventArgs args)
        {
            var key = $"player_ships_{msg.AdminName}";
            if (PendingAdminRequests.TryGetValue(key, out var callback))
            {
                // Cache the ship data for later commands
                PlayerShipCache[key] = msg.Ships;

                var result = $"=== Ships for player ===\n\n";
                for (int i = 0; i < msg.Ships.Count; i++)
                {
                    var (filename, shipName, timestamp) = msg.Ships[i];
                    result += $"[{i + 1}] {shipName} ({filename})\n";
                    result += $"    Saved: {timestamp:yyyy-MM-dd HH:mm:ss}\n";
                    result += "\n";
                }
                callback(result);
                PendingAdminRequests.Remove(key);
            }
        }

        private void OnAdminSendShipData(AdminSendShipDataMessage msg, EntitySessionEventArgs args)
        {
            var key = $"ship_data_{msg.AdminName}_{msg.ShipFilename}";
            if (PendingAdminRequests.TryGetValue(key, out var callback))
            {
                callback(msg.ShipData);
                PendingAdminRequests.Remove(key);
            }
        }

        public static void RegisterAdminRequest(string key, Action<string> callback)
        {
            PendingAdminRequests[key] = callback;
        }

        public void SendAdminRequestPlayerShips(Guid playerId, string adminName, ICommonSession targetSession)
        {
            RaiseNetworkEvent(new AdminRequestPlayerShipsMessage(playerId, adminName), targetSession);
        }

        public void SendAdminRequestShipData(string filename, string adminName, ICommonSession targetSession)
        {
            RaiseNetworkEvent(new AdminRequestShipDataMessage(filename, adminName), targetSession);
        }
    }
}
