using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;
using VRC.SDKBase;

namespace RocketLeague
{
    /// <summary>
    /// Handles spawning and respawning of player vehicles.
    /// Assigns players to teams and manages spawn positions.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class PlayerSpawner : UdonSharpBehaviour
    {
        [Header("Spawn Points")]
        public Transform[] BlueSpawnPoints;
        public Transform[] OrangeSpawnPoints;

        [Header("Vehicles")]
        [Tooltip("Pool of pre-instantiated vehicles")]
        public NetVehicle[] VehiclePool;

        [Header("Settings")]
        public bool AutoAssignTeams = true;

        [UdonSynced] private int[] _playerTeams = new int[MAX_PLAYERS];
        [UdonSynced] private int[] _playerVehicleIndices = new int[MAX_PLAYERS];
        [UdonSynced] private int _bluePlayerCount;
        [UdonSynced] private int _orangePlayerCount;

        private const int MAX_PLAYERS = 16;
        private bool _initialized;

        private void Start()
        {
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                _playerTeams[i] = -1;
                _playerVehicleIndices[i] = -1;
            }

            _initialized = true;
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (!Networking.IsMaster || player == null || !player.IsValid())
                return;

            // Assign team
            int playerId = player.playerId;
            if (playerId >= 0 && playerId < MAX_PLAYERS)
            {
                if (AutoAssignTeams)
                {
                    // Balance teams
                    _playerTeams[playerId] = _bluePlayerCount <= _orangePlayerCount ? 0 : 1;
                    if (_playerTeams[playerId] == 0)
                        _bluePlayerCount++;
                    else
                        _orangePlayerCount++;
                }
                else
                {
                    _playerTeams[playerId] = 0; // Default to blue
                }

                // Assign vehicle from pool
                int vehicleIndex = FindAvailableVehicle();
                if (vehicleIndex >= 0)
                {
                    _playerVehicleIndices[playerId] = vehicleIndex;
                    AssignVehicleToPlayer(vehicleIndex, player, _playerTeams[playerId]);
                }

                RequestSerialization();
            }
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!Networking.IsMaster || player == null)
                return;

            int playerId = player.playerId;
            if (playerId >= 0 && playerId < MAX_PLAYERS)
            {
                // Release vehicle
                int vehicleIndex = _playerVehicleIndices[playerId];
                if (vehicleIndex >= 0 && vehicleIndex < VehiclePool.Length)
                {
                    ReleaseVehicle(vehicleIndex);
                }

                // Update team counts
                if (_playerTeams[playerId] == 0)
                    _bluePlayerCount = Mathf.Max(0, _bluePlayerCount - 1);
                else if (_playerTeams[playerId] == 1)
                    _orangePlayerCount = Mathf.Max(0, _orangePlayerCount - 1);

                _playerTeams[playerId] = -1;
                _playerVehicleIndices[playerId] = -1;

                RequestSerialization();
            }
        }

        /// <summary>
        /// Get the team for a player (0 = Blue, 1 = Orange, -1 = unassigned).
        /// </summary>
        public int GetPlayerTeam(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return -1;

            int playerId = player.playerId;
            if (playerId < 0 || playerId >= MAX_PLAYERS)
                return -1;

            return _playerTeams[playerId];
        }

        /// <summary>
        /// Get the vehicle assigned to a player.
        /// </summary>
        public NetVehicle GetPlayerVehicle(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return null;

            int playerId = player.playerId;
            if (playerId < 0 || playerId >= MAX_PLAYERS)
                return null;

            int vehicleIndex = _playerVehicleIndices[playerId];
            if (vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                return null;

            return VehiclePool[vehicleIndex];
        }

        /// <summary>
        /// Respawns all players to their team spawn positions.
        /// </summary>
        public void RespawnAllPlayers()
        {
            int blueSpawnIndex = 0;
            int orangeSpawnIndex = 0;

            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                int vehicleIndex = _playerVehicleIndices[i];
                if (vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                    continue;

                NetVehicle vehicle = VehiclePool[vehicleIndex];
                if (vehicle == null)
                    continue;

                int team = _playerTeams[i];
                Transform spawnPoint = null;

                if (team == 0 && BlueSpawnPoints != null && BlueSpawnPoints.Length > 0)
                {
                    spawnPoint = BlueSpawnPoints[blueSpawnIndex % BlueSpawnPoints.Length];
                    blueSpawnIndex++;
                }
                else if (team == 1 && OrangeSpawnPoints != null && OrangeSpawnPoints.Length > 0)
                {
                    spawnPoint = OrangeSpawnPoints[orangeSpawnIndex % OrangeSpawnPoints.Length];
                    orangeSpawnIndex++;
                }

                if (spawnPoint != null)
                {
                    RespawnVehicle(vehicle, spawnPoint);
                }
            }
        }

        /// <summary>
        /// Respawns a specific player's vehicle.
        /// </summary>
        public void RespawnPlayer(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return;

            int playerId = player.playerId;
            if (playerId < 0 || playerId >= MAX_PLAYERS)
                return;

            int vehicleIndex = _playerVehicleIndices[playerId];
            if (vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                return;

            NetVehicle vehicle = VehiclePool[vehicleIndex];
            if (vehicle == null)
                return;

            int team = _playerTeams[playerId];
            Transform spawnPoint = GetSpawnPointForTeam(team, 0);

            if (spawnPoint != null)
            {
                RespawnVehicle(vehicle, spawnPoint);
            }
        }

        private Transform GetSpawnPointForTeam(int team, int index)
        {
            if (team == 0 && BlueSpawnPoints != null && BlueSpawnPoints.Length > 0)
            {
                return BlueSpawnPoints[index % BlueSpawnPoints.Length];
            }
            else if (team == 1 && OrangeSpawnPoints != null && OrangeSpawnPoints.Length > 0)
            {
                return OrangeSpawnPoints[index % OrangeSpawnPoints.Length];
            }
            return null;
        }

        private void RespawnVehicle(NetVehicle vehicle, Transform spawnPoint)
        {
            vehicle.Position = spawnPoint.position;
            vehicle.Rotation = spawnPoint.rotation;
            vehicle.Velocity = Vector3.zero;
            vehicle.AngularVelocity = Vector3.zero;
            vehicle.ResetVehicle();
        }

        private int FindAvailableVehicle()
        {
            if (VehiclePool == null)
                return -1;

            for (int i = 0; i < VehiclePool.Length; i++)
            {
                if (VehiclePool[i] == null)
                    continue;

                // Check if already assigned
                bool assigned = false;
                for (int j = 0; j < MAX_PLAYERS; j++)
                {
                    if (_playerVehicleIndices[j] == i)
                    {
                        assigned = true;
                        break;
                    }
                }

                if (!assigned)
                    return i;
            }

            return -1;
        }

        private void AssignVehicleToPlayer(int vehicleIndex, VRCPlayerApi player, int team)
        {
            if (vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                return;

            NetVehicle vehicle = VehiclePool[vehicleIndex];
            if (vehicle == null)
                return;

            // Set ownership
            Networking.SetOwner(player, vehicle.gameObject);
            vehicle.SetOwner(player);

            // Get spawn point
            int spawnIndex = team == 0 ? _bluePlayerCount - 1 : _orangePlayerCount - 1;
            Transform spawnPoint = GetSpawnPointForTeam(team, spawnIndex);

            if (spawnPoint != null)
            {
                RespawnVehicle(vehicle, spawnPoint);
            }

            // Enable the vehicle
            vehicle.gameObject.SetActive(true);
        }

        private void ReleaseVehicle(int vehicleIndex)
        {
            if (vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                return;

            NetVehicle vehicle = VehiclePool[vehicleIndex];
            if (vehicle == null)
                return;

            // Disable and reset
            vehicle.gameObject.SetActive(false);
            vehicle.SetOwner(null);
        }

        /// <summary>
        /// Manually set a player's team.
        /// </summary>
        public void SetPlayerTeam(VRCPlayerApi player, int team)
        {
            if (!Networking.IsMaster || player == null || !player.IsValid())
                return;

            int playerId = player.playerId;
            if (playerId < 0 || playerId >= MAX_PLAYERS)
                return;

            int oldTeam = _playerTeams[playerId];
            if (oldTeam == team)
                return;

            // Update counts
            if (oldTeam == 0)
                _bluePlayerCount = Mathf.Max(0, _bluePlayerCount - 1);
            else if (oldTeam == 1)
                _orangePlayerCount = Mathf.Max(0, _orangePlayerCount - 1);

            _playerTeams[playerId] = team;

            if (team == 0)
                _bluePlayerCount++;
            else if (team == 1)
                _orangePlayerCount++;

            RequestSerialization();
        }
    }
}

