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

        // playerId is a monotonically increasing per-instance counter, NOT a slot index. Using it to
        // index these arrays meant that after 15 cumulative joins every subsequent player fell through
        // the bounds check and silently got no team and no vehicle. This table maps slots to players.
        [UdonSynced] private int[] _slotPlayerIds = new int[MAX_PLAYERS];
        [UdonSynced] private int[] _playerTeams = new int[MAX_PLAYERS];
        [UdonSynced] private int[] _playerVehicleIndices = new int[MAX_PLAYERS];
        [UdonSynced] private int _bluePlayerCount;
        [UdonSynced] private int _orangePlayerCount;

        private const int MAX_PLAYERS = 16;

        private void Start()
        {
            // Only the owner may seed the slot table. Running this on every client wrote lobby defaults
            // over synced state VRChat may already have delivered -- OnDeserialization is allowed to land
            // before Start -- which showed up as a joining player losing their team and their vehicle.
            if (EnsureOwnership())
            {
                for (int i = 0; i < MAX_PLAYERS; i++)
                {
                    _slotPlayerIds[i] = -1;
                    _playerTeams[i] = -1;
                    _playerVehicleIndices[i] = -1;
                }

                RequestSerialization();
            }

            ApplyVehicleAssignments();
        }

        /// <summary>
        /// The roster is instance-wide, so the master claims it. Ownership -- not master status -- is
        /// what grants the right to write synced variables: a master that does not own this object had
        /// its slot-table writes silently discarded, and no other client was writing them either.
        /// </summary>
        private bool EnsureOwnership()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return false;

            if (Networking.IsOwner(localPlayer, gameObject))
                return true;

            if (!Networking.IsMaster)
                return false;

            Networking.SetOwner(localPlayer, gameObject);
            return Networking.IsOwner(localPlayer, gameObject);
        }

        public override void OnMasterTransferred(VRCPlayerApi newMaster)
        {
            // Without this, a master handover leaves the roster owned by a player who has left, and no
            // further join or leave is ever recorded.
            if (newMaster != null && newMaster.isLocal)
                EnsureOwnership();
        }

        /// <summary>Returns the slot already held by this player, or -1.</summary>
        private int FindSlot(int playerId)
        {
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (_slotPlayerIds[i] == playerId)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Slot occupancy. VRChat player ids start at 1, so the compiled default of 0 reads as "free" on
        /// a client that has not received the slot table yet. Without this a fresh joiner would briefly
        /// believe vehicle 0 was assigned to all sixteen slots and show it as taken.
        /// </summary>
        private bool IsSlotOccupied(int slot)
        {
            return slot >= 0 && slot < MAX_PLAYERS && _slotPlayerIds[slot] > 0;
        }

        /// <summary>Returns this player's slot, claiming a free one if they do not have it yet, or -1
        /// when the instance is already at capacity.</summary>
        private int AllocateSlot(int playerId)
        {
            int existing = FindSlot(playerId);
            if (existing >= 0)
                return existing;

            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (!IsSlotOccupied(i))
                {
                    _slotPlayerIds[i] = playerId;
                    return i;
                }
            }

            return -1;
        }

        private void ReleaseSlot(int slot)
        {
            if (slot >= 0 && slot < MAX_PLAYERS)
                _slotPlayerIds[slot] = -1;
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return;

            // Gate on ownership, not on IsMaster: the writes below go into the void on a master that does
            // not own this object.
            if (!EnsureOwnership())
            {
                // A non-owner cannot touch the roster, but it can now resolve this player's id, which it
                // could not do when the assignment arrived. Re-derive so a slot table that was delivered
                // before VRChat announced the player does not leave that player's car ownerless here.
                ApplyVehicleAssignments();
                return;
            }

            // Assign team
            int slot = AllocateSlot(player.playerId);
            if (slot >= 0)
            {
                if (AutoAssignTeams)
                {
                    // Balance teams
                    _playerTeams[slot] = _bluePlayerCount <= _orangePlayerCount ? 0 : 1;
                    if (_playerTeams[slot] == 0)
                        _bluePlayerCount++;
                    else
                        _orangePlayerCount++;
                }
                else
                {
                    _playerTeams[slot] = 0; // Default to blue
                }

                // Assign vehicle from pool
                int vehicleIndex = FindAvailableVehicle();
                if (vehicleIndex >= 0)
                {
                    _playerVehicleIndices[slot] = vehicleIndex;
                    AssignVehicleToPlayer(vehicleIndex, player, _playerTeams[slot]);
                }

                RequestSerialization();
            }
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null)
                return;

            if (!EnsureOwnership())
                return;

            int slot = FindSlot(player.playerId);
            if (slot >= 0)
            {
                int vehicleIndex = _playerVehicleIndices[slot];

                // Update team counts
                if (_playerTeams[slot] == 0)
                    _bluePlayerCount = Mathf.Max(0, _bluePlayerCount - 1);
                else if (_playerTeams[slot] == 1)
                    _orangePlayerCount = Mathf.Max(0, _orangePlayerCount - 1);

                _playerTeams[slot] = -1;
                _playerVehicleIndices[slot] = -1;
                ReleaseSlot(slot);

                // Free the slot before releasing the vehicle: visibility is derived from the slot table,
                // so a release that ran first would still find the vehicle "assigned" and leave it visible.
                ReleaseVehicle(vehicleIndex);

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

            int slot = FindSlot(player.playerId);
            if (slot < 0)
                return -1;

            return _playerTeams[slot];
        }

        /// <summary>
        /// Get the vehicle assigned to a player.
        /// </summary>
        public NetVehicle GetPlayerVehicle(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return null;

            int slot = FindSlot(player.playerId);
            if (slot < 0)
                return null;

            int vehicleIndex = _playerVehicleIndices[slot];
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
                if (!IsSlotOccupied(i))
                    continue;

                int vehicleIndex = _playerVehicleIndices[i];
                if (vehicleIndex < 0 || VehiclePool == null || vehicleIndex >= VehiclePool.Length)
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

            int slot = FindSlot(player.playerId);
            if (slot < 0)
                return;

            int vehicleIndex = _playerVehicleIndices[slot];
            if (vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                return;

            NetVehicle vehicle = VehiclePool[vehicleIndex];
            if (vehicle == null)
                return;

            int team = _playerTeams[slot];
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
                bool assigned = IsVehicleAssigned(i);

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

            // Hand the network object to the driver. Naming the driver on the vehicle is deliberately NOT
            // done here: that write only ever ran on the master, so every other client -- including the
            // driver -- was left with a visible car that had no driver id and therefore never counted as
            // the local vehicle. ApplyVehicleAssignments derives it from the synced slot table instead.
            Networking.SetOwner(player, vehicle.gameObject);

            // Get spawn point
            int spawnIndex = team == 0 ? _bluePlayerCount - 1 : _orangePlayerCount - 1;
            Transform spawnPoint = GetSpawnPointForTeam(team, spawnIndex);

            if (spawnPoint != null)
            {
                RespawnVehicle(vehicle, spawnPoint);
            }

            // Show the vehicle from the slot table rather than switching the GameObject on here -- see
            // ApplyVehicleAssignments for why SetActive is not an option on a synced UdonBehaviour.
            ApplyVehicleAssignments();
        }

        private void ReleaseVehicle(int vehicleIndex)
        {
            if (VehiclePool == null || vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                return;

            NetVehicle vehicle = VehiclePool[vehicleIndex];
            if (vehicle == null)
                return;

            // The slot was already cleared by the caller, so the derive pass below sees this index as
            // unassigned and clears the driver id itself. See AssignVehicleToPlayer.
            ApplyVehicleAssignments();
        }

        /// <summary>
        /// Drives vehicle visibility AND vehicle ownership on every client from the already-synced slot
        /// table, so the pool looks the same everywhere without a single extra byte on the wire.
        ///
        /// Ownership has to be derived here rather than assigned once on the master: NetVehicle decides
        /// whether a car is the local vehicle by comparing the local player id against its driver id, so
        /// a driver id that only ever existed on the master left every driver watching a car they could
        /// not control.
        ///
        /// Deliberately does NOT call SetActive on a vehicle root. NetVehicle derives from
        /// NetPhysicsEntity, which is a Manual-sync UdonBehaviour, and a disabled UdonBehaviour in VRChat
        /// stops running Update and stops receiving OnDeserialization and network events. The previous
        /// code called SetActive on the master only, so remotes kept every pool vehicle visible forever
        /// while any vehicle the master disabled dropped out of sync permanently -- divergence that never
        /// healed because the disabled behaviour could no longer hear the correction.
        ///
        /// Toggling renderers and colliders is the same approach DemoManager and CollectibleItem use.
        /// </summary>
        private void ApplyVehicleAssignments()
        {
            if (VehiclePool == null)
                return;

            for (int i = 0; i < VehiclePool.Length; i++)
            {
                NetVehicle vehicle = VehiclePool[i];
                if (vehicle == null)
                    continue;

                int driverId = GetVehicleDriverId(i);
                ApplyVehicleOwner(vehicle, driverId);
                SetVehicleVisible(vehicle, driverId > 0);
            }
        }

        /// <summary>
        /// Player id occupying this pool index, or -1. VRChat player ids start at 1, so anything not
        /// positive means unassigned.
        /// </summary>
        private int GetVehicleDriverId(int vehicleIndex)
        {
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (IsSlotOccupied(i) && _playerVehicleIndices[i] == vehicleIndex)
                    return _slotPlayerIds[i];
            }

            return -1;
        }

        /// <summary>Is this pool index claimed by an occupied slot?</summary>
        private bool IsVehicleAssigned(int vehicleIndex)
        {
            return GetVehicleDriverId(vehicleIndex) > 0;
        }

        /// <summary>
        /// Points the vehicle at the driver the slot table names.
        ///
        /// Two guards matter here. The first is the equality check: NetVehicle.SetOwner publishes -- it
        /// serializes when we own the car and otherwise sends a network event to whoever does -- and this
        /// method runs on every client on every roster deserialization, so calling it unconditionally
        /// would put one packet per vehicle per client on the wire every time anybody joined or left.
        ///
        /// The second is the writer check. Only the client that owns the vehicle object, or the driver
        /// the table names, may act. Letting every client "clear" a released vehicle had all of them run
        /// Networking.SetOwner on the same object at once, which is an ownership tug-of-war, and every
        /// non-driver correcting an assigned vehicle just spammed its owner with publish requests.
        /// Clients that do neither pick the driver id up from NetVehicle's own synced copy.
        /// </summary>
        private void ApplyVehicleOwner(NetVehicle vehicle, int driverId)
        {
            if (vehicle.OwnerPlayerId == driverId)
                return;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            bool localIsDriver = driverId > 0 && localPlayer.playerId == driverId;
            if (!localIsDriver && !Networking.IsOwner(localPlayer, vehicle.gameObject))
                return;

            if (driverId > 0)
            {
                VRCPlayerApi driver = VRCPlayerApi.GetPlayerById(driverId);

                // The roster can name a player this client has not been told about yet. Leaving the old
                // id in place and retrying on the next apply beats writing -1 over a live driver.
                if (driver == null || !driver.IsValid())
                    return;

                vehicle.SetOwner(driver);
                return;
            }

            vehicle.SetOwner(null);
        }

        private void SetVehicleVisible(NetVehicle vehicle, bool visible)
        {
            GameObject root = vehicle.gameObject;

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                    renderers[i].enabled = visible;
            }

            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                    colliders[i].enabled = visible;
            }

            // Park the body while it is unassigned. With its colliders off an unclaimed vehicle would
            // otherwise fall out of the arena under gravity and be somewhere absurd by the time the next
            // player claims it. NetPhysics never touches isKinematic itself, so this is ours to own.
            Rigidbody rb = vehicle.GetComponent<Rigidbody>();
            if (rb != null)
            {
                if (!visible)
                {
                    rb.velocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                rb.isKinematic = !visible;
            }
        }

        /// <summary>
        /// Every client re-derives the pool from the slot table it just received. This is what makes the
        /// assignment networked: no SendCustomNetworkEvent is needed because the state is already synced.
        /// </summary>
        public override void OnDeserialization()
        {
            ApplyVehicleAssignments();
        }

        /// <summary>
        /// Manually set a player's team.
        /// </summary>
        public void SetPlayerTeam(VRCPlayerApi player, int team)
        {
            if (player == null || !player.IsValid())
                return;

            if (!EnsureOwnership())
                return;

            int slot = FindSlot(player.playerId);
            if (slot < 0)
                return;

            int oldTeam = _playerTeams[slot];
            if (oldTeam == team)
                return;

            // Update counts
            if (oldTeam == 0)
                _bluePlayerCount = Mathf.Max(0, _bluePlayerCount - 1);
            else if (oldTeam == 1)
                _orangePlayerCount = Mathf.Max(0, _orangePlayerCount - 1);

            _playerTeams[slot] = team;

            if (team == 0)
                _bluePlayerCount++;
            else if (team == 1)
                _orangePlayerCount++;

            RequestSerialization();
        }
    }
}

