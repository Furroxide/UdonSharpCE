using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace RocketLeague
{
    /// <summary>
    /// The arena's roster: who is playing, on which team, in which car.
    ///
    /// A player takes a seat when they walk into the arena (<see cref="RequestJoinArena"/>, from
    /// ArenaTeleporter) and gives it up when they leave it (<see cref="RequestLeaveArena"/>, from
    /// HubTeleporter and a respawn) or the instance. A seated player drives a car from the pool while one
    /// is free and waits for the next one otherwise; teams are balanced over the players who drive.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class PlayerSpawner : UdonSharpBehaviour
    {
        [Header("Spawn Points")]
        public Transform[] BlueSpawnPoints;
        public Transform[] OrangeSpawnPoints;

        [Tooltip("Sideways step, in metres, between cars that share a spawn point because a team has more " +
                 "drivers than spawn points.")]
        public float SpawnLaneSpacing = 4f;

        [Header("Vehicles")]
        [Tooltip("Pool of pre-instantiated vehicles")]
        public NetVehicle[] VehiclePool;

        [Header("Settings")]
        public bool AutoAssignTeams = true;

        [Header("Team Colors")]
        [Tooltip("Tints each driven car with its team's colour, so the teams can be told apart on the field.")]
        public bool TintVehiclesByTeam = true;
        public Color BlueTeamColor = new Color(0.2f, 0.4f, 1f);
        public Color OrangeTeamColor = new Color(1f, 0.5f, 0.1f);

        // playerId is a monotonically increasing per-instance counter, NOT a slot index. Using it to
        // index these arrays meant that after 15 cumulative joins every subsequent player fell through
        // the bounds check and silently got no team and no vehicle. This table maps slots to players.
        [UdonSynced] private int[] _slotPlayerIds = new int[MAX_PLAYERS];
        [UdonSynced] private int[] _playerTeams = new int[MAX_PLAYERS];
        [UdonSynced] private int[] _playerVehicleIndices = new int[MAX_PLAYERS];
        [UdonSynced] private int _bluePlayerCount;
        [UdonSynced] private int _orangePlayerCount;

        private const int MAX_PLAYERS = 16;

        // A seat request travels to the roster's owner as a network event, and an event can be lost: the
        // owner may be on the way out, or ownership may be moving to a master who never heard it. Until
        // the roster agrees with where the local player is, the request is sent again this many times.
        private const int SeatRequestRetries = 5;
        private const float SeatRequestRetrySeconds = 3f;

        // Whether the local player is standing in the arena. Local, not synced: it gates what this
        // client's own input may do to the car it drives, and it is what the seat requests ask for.
        private bool _localPlayerInArena;
        private int _seatRetriesLeft;
        private bool _seatCheckPending;

        private MaterialPropertyBlock _tintBlock;

        /// <summary>Players seated in the arena, driving or waiting for a car.</summary>
        public int SeatedPlayerCount
        {
            get
            {
                int count = 0;
                for (int i = 0; i < MAX_PLAYERS; i++)
                {
                    if (IsSlotOccupied(i))
                        count++;
                }

                return count;
            }
        }

        public int BluePlayerCount => _bluePlayerCount;
        public int OrangePlayerCount => _orangePlayerCount;

        private void Start()
        {
            // Only the owner may seed the slot table. Running this on every client wrote lobby defaults
            // over synced state VRChat may already have delivered -- the values can land before Start,
            // and the OnDeserialization that would have re-applied them is dropped for a behaviour that
            // has not started -- which showed up as a joining player losing their team and their vehicle.
            // The apply pass below is what picks such a delivery up.
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
        /// when the arena is already at capacity.</summary>
        private int AllocateSlot(int playerId)
        {
            int existing = FindSlot(playerId);
            if (existing >= 0)
                return existing;

            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (!IsSlotOccupied(i))
                {
                    // The other columns are cleared with the claim. A client that inherits the roster
                    // before it ever received it holds the compiled defaults, and a stale 0 there reads
                    // as "already on blue, already in car 0".
                    _slotPlayerIds[i] = playerId;
                    _playerTeams[i] = -1;
                    _playerVehicleIndices[i] = -1;
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

            // Joining the instance is not joining the match: the seat and the car wait until the player
            // walks into the arena. Handing them out here gave the pool away to whoever arrived first,
            // wherever they were, and a visitor who never came near the arena kept a car from everybody
            // who did. What a join does change is who this client can resolve: a slot table delivered
            // before VRChat announced its player left that player's car without a driver here.
            ApplyVehicleAssignments();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null)
                return;

            if (!EnsureOwnership())
                return;

            LeaveArena(player.playerId);
        }

        // -----------------------------------------------------------------------------------------
        // Seats: the local player's requests and the owner's handling of them
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The local player has walked into the arena: seat them, on a team and in a car if one is free.
        /// From here the car they drive may listen to them.
        ///
        /// The roster is the owner's to write, so anyone else asks the owner, who seats whoever sent the
        /// request rather than a player id from the payload.
        /// </summary>
        public void RequestJoinArena()
        {
            SetLocalPlayerInArena(true);
            _seatRetriesLeft = SeatRequestRetries;
            SendSeatRequest();
        }

        /// <summary>
        /// The local player has left the arena: give the seat and the car back, so a waiting player can
        /// have it. Their hands come off the car at once; the release follows the request.
        /// </summary>
        public void RequestLeaveArena()
        {
            SetLocalPlayerInArena(false);
            _seatRetriesLeft = SeatRequestRetries;
            SendSeatRequest();
        }

        /// <summary>
        /// Sends whichever request makes the roster agree with where the local player is, or applies it
        /// directly when this client owns the roster. Sends nothing when they already agree, which is the
        /// usual case for a respawn in the hub.
        /// </summary>
        private void SendSeatRequest()
        {
            VRCPlayerApi local = Networking.LocalPlayer;
            if (local == null || !local.IsValid())
                return;

            bool seated = FindSlot(local.playerId) >= 0;
            if (seated == _localPlayerInArena)
                return;

            if (Networking.IsOwner(local, gameObject))
            {
                if (_localPlayerInArena)
                    JoinArena(local);
                else
                    LeaveArena(local.playerId);

                return;
            }

            if (_localPlayerInArena)
                SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(OwnerJoinArena));
            else
                SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(OwnerLeaveArena));

            if (!_seatCheckPending)
            {
                _seatCheckPending = true;
                SendCustomEventDelayedSeconds(nameof(_CheckSeatRequest), SeatRequestRetrySeconds);
            }
        }

        /// <summary>
        /// Sends the seat request again if the roster still disagrees with where the local player is.
        /// Local only: the leading underscore keeps it out of reach of network events.
        /// </summary>
        public void _CheckSeatRequest()
        {
            _seatCheckPending = false;
            RetrySeatRequest();
        }

        /// <summary>Spends one retry on a seat request the roster has not caught up with.</summary>
        private void RetrySeatRequest()
        {
            if (_seatRetriesLeft <= 0)
                return;

            VRCPlayerApi local = Networking.LocalPlayer;
            if (local == null || !local.IsValid())
                return;

            if ((FindSlot(local.playerId) >= 0) == _localPlayerInArena)
                return;

            _seatRetriesLeft--;
            SendSeatRequest();
        }

        /// <summary>Network entry point for <see cref="RequestJoinArena"/>. Runs on the roster's owner.</summary>
        [NetworkCallable]
        public void OwnerJoinArena()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !Networking.IsOwner(localPlayer, gameObject))
                return;

            // Null for a local call. The owner seats itself through RequestJoinArena's direct path, so a
            // request with nobody behind it has nobody to seat.
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller == null || !caller.IsValid())
                return;

            JoinArena(caller);
        }

        /// <summary>Network entry point for <see cref="RequestLeaveArena"/>. Runs on the roster's owner.</summary>
        [NetworkCallable]
        public void OwnerLeaveArena()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !Networking.IsOwner(localPlayer, gameObject))
                return;

            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller == null || !caller.IsValid())
                return;

            LeaveArena(caller.playerId);
        }

        /// <summary>
        /// Seats a player, or tops up the seat they already have with a car if one has come free. Owner
        /// only. Asking twice is harmless, which is what lets a lost request simply be sent again.
        /// </summary>
        private void JoinArena(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid())
                return;

            int slot = AllocateSlot(player.playerId);
            if (slot < 0)
                return; // The arena is full; the player watches.

            if (_playerVehicleIndices[slot] < 0)
            {
                int vehicleIndex = FindAvailableVehicle();
                if (vehicleIndex >= 0)
                    GiveVehicleToSlot(slot, vehicleIndex, player);
            }

            RequestSerialization();
        }

        /// <summary>
        /// Takes a player's seat back and hands their car to whoever has waited longest. Owner only. A
        /// player who holds no seat is ignored, so a duplicate or late request does nothing.
        /// </summary>
        private void LeaveArena(int playerId)
        {
            int slot = FindSlot(playerId);
            if (slot < 0)
                return;

            int vehicleIndex = _playerVehicleIndices[slot];

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
            HandVehicleToWaitingPlayer(vehicleIndex);

            RequestSerialization();
        }

        /// <summary>
        /// Puts a seated player in a car: picks their team, then hands them the car at a clear spot of
        /// that team's end. Teams are picked here rather than at the seat because they are balanced over
        /// the players who drive -- a player waiting for a car is not on the field.
        /// </summary>
        private void GiveVehicleToSlot(int slot, int vehicleIndex, VRCPlayerApi player)
        {
            if (_playerTeams[slot] < 0)
                AssignTeamToSlot(slot);

            _playerVehicleIndices[slot] = vehicleIndex;
            AssignVehicleToPlayer(vehicleIndex, player, _playerTeams[slot]);
        }

        /// <summary>
        /// Puts the slot's player on a team and counts them, returning the team.
        ///
        /// The count has to move on both branches. With AutoAssignTeams off the player was put on blue
        /// without being counted, so a later team balance or spawn lookup started from the wrong number.
        /// </summary>
        private int AssignTeamToSlot(int slot)
        {
            // Balance teams, or default everyone to blue
            int team = AutoAssignTeams && _bluePlayerCount > _orangePlayerCount ? 1 : 0;

            _playerTeams[slot] = team;
            if (team == 0)
                _bluePlayerCount++;
            else
                _orangePlayerCount++;

            return team;
        }

        /// <summary>
        /// Gives a vehicle that has just come free to a seated player who is still waiting for one.
        ///
        /// Vehicles were only ever handed out at the moment a player joined, so a player who arrived to
        /// an empty pool stayed on foot for good: the car a departing driver left behind sat hidden until
        /// somebody new joined and was given it instead.
        /// </summary>
        private void HandVehicleToWaitingPlayer(int vehicleIndex)
        {
            if (VehiclePool == null || vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                return;

            if (VehiclePool[vehicleIndex] == null)
                return;

            int slot = FindSlotWaitingForVehicle(0);
            while (slot >= 0)
            {
                VRCPlayerApi waiting = VRCPlayerApi.GetPlayerById(_slotPlayerIds[slot]);
                if (waiting != null && waiting.IsValid())
                {
                    GiveVehicleToSlot(slot, vehicleIndex, waiting);
                    return;
                }

                // This slot's own player is on the way out too; their leave will free it.
                slot = FindSlotWaitingForVehicle(slot + 1);
            }
        }

        /// <summary>First occupied slot at or after <paramref name="startSlot"/> with no vehicle, or -1.</summary>
        private int FindSlotWaitingForVehicle(int startSlot)
        {
            for (int i = startSlot < 0 ? 0 : startSlot; i < MAX_PLAYERS; i++)
            {
                if (IsSlotOccupied(i) && _playerVehicleIndices[i] < 0)
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// Whether this player holds a seat in the arena, driving or waiting.
        /// </summary>
        public bool IsSeated(VRCPlayerApi player)
        {
            return player != null && player.IsValid() && FindSlot(player.playerId) >= 0;
        }

        /// <summary>
        /// Get the team for a player (0 = Blue, 1 = Orange, -1 = not driving).
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
            if (player == null || !player.IsValid() || VehiclePool == null)
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
                int spawnIndex;

                if (team == 0)
                {
                    spawnIndex = blueSpawnIndex;
                    blueSpawnIndex++;
                }
                else if (team == 1)
                {
                    spawnIndex = orangeSpawnIndex;
                    orangeSpawnIndex++;
                }
                else
                {
                    continue;
                }

                Transform spawnPoint = GetSpawnPointForTeam(team, spawnIndex);
                if (spawnPoint != null)
                    RespawnVehicle(vehicle, spawnPoint, GetSpawnLaneOffset(team, spawnIndex));
            }
        }

        /// <summary>
        /// Respawns a specific player's vehicle.
        /// </summary>
        public void RespawnPlayer(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid() || VehiclePool == null)
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

            SpawnAtClearSpot(vehicle, _playerTeams[slot]);
        }

        /// <summary>
        /// Spawn point <paramref name="index"/> of a team, cycling when the team has more drivers than
        /// spawn points. Never negative: C#'s % keeps the sign of its left operand, so -1 against two or
        /// more spawn points is an out-of-range read, and an out-of-range read halts this behaviour --
        /// taking every later join and leave with it.
        /// </summary>
        private Transform GetSpawnPointForTeam(int team, int index)
        {
            if (index < 0)
                index = 0;

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

        /// <summary>
        /// Where on its spawn point the <paramref name="index"/>th car of a team stands: on it for the
        /// first lap through the team's spawn points, then a lane to the side per lap. Without this a team
        /// with more drivers than spawn points stacked two cars in one place, and two bodies spawned inside
        /// each other are thrown apart by the physics engine.
        /// </summary>
        private Vector3 GetSpawnLaneOffset(int team, int index)
        {
            int count = team == 0 && BlueSpawnPoints != null ? BlueSpawnPoints.Length
                : team == 1 && OrangeSpawnPoints != null ? OrangeSpawnPoints.Length
                : 0;

            if (count <= 0 || index < count)
                return Vector3.zero;

            Transform point = GetSpawnPointForTeam(team, index);
            if (point == null)
                return Vector3.zero;

            int lap = index / count;
            return point.right * (lap * SpawnLaneSpacing);
        }

        /// <summary>
        /// Puts a car down on its team's end where no other car stands, for a car that joins a match in
        /// progress. Spawning at the newest team member's spawn point put a car that joined after a
        /// teammate left straight on top of a car still standing there.
        /// </summary>
        private void SpawnAtClearSpot(NetVehicle vehicle, int team)
        {
            int index = FindClearSpawnIndex(team, vehicle);
            if (index < 0)
                return; // The team has no spawn points; leave the car where it is.

            RespawnVehicle(vehicle, GetSpawnPointForTeam(team, index), GetSpawnLaneOffset(team, index));
        }

        /// <summary>
        /// The first of a team's spawn spots with room for a car, in spawn-point order, or the roomiest
        /// one when none has room; -1 when the team has no spawn points. There are as many spots as cars,
        /// counting the lanes beside each spawn point.
        /// </summary>
        private int FindClearSpawnIndex(int team, NetVehicle self)
        {
            int candidates = VehiclePool != null ? VehiclePool.Length : 0;
            int bestIndex = -1;
            float bestClearance = -1f;

            for (int index = 0; index < candidates; index++)
            {
                Transform point = GetSpawnPointForTeam(team, index);
                if (point == null)
                    return -1;

                float clearance = ClearanceAround(point.position + GetSpawnLaneOffset(team, index), self);

                // A car needs a lane's width of room.
                if (clearance >= SpawnLaneSpacing)
                    return index;

                if (clearance > bestClearance)
                {
                    bestClearance = clearance;
                    bestIndex = index;
                }
            }

            return bestIndex;
        }

        /// <summary>Distance from a spot to the nearest driven car other than <paramref name="self"/>.</summary>
        private float ClearanceAround(Vector3 spot, NetVehicle self)
        {
            float clearance = float.MaxValue;

            for (int i = 0; i < VehiclePool.Length; i++)
            {
                NetVehicle other = VehiclePool[i];
                if (other == null || other == self || !IsVehicleAssigned(i))
                    continue;

                float distance = Vector3.Distance(other.transform.position, spot);
                if (distance < clearance)
                    clearance = distance;
            }

            return clearance;
        }

        private void RespawnVehicle(NetVehicle vehicle, Transform spawnPoint, Vector3 offset)
        {
            vehicle.Position = spawnPoint.position + offset;
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
            if (VehiclePool == null || vehicleIndex < 0 || vehicleIndex >= VehiclePool.Length)
                return;

            NetVehicle vehicle = VehiclePool[vehicleIndex];
            if (vehicle == null)
                return;

            // Hand the network object to the driver. Naming the driver on the vehicle is deliberately NOT
            // done here: that write only ever ran on the master, so every other client -- including the
            // driver -- was left with a visible car that had no driver id and therefore never counted as
            // the local vehicle. ApplyVehicleAssignments derives it from the synced slot table instead.
            Networking.SetOwner(player, vehicle.gameObject);

            SpawnAtClearSpot(vehicle, team);

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

                int slot = GetVehicleSlot(i);
                int driverId = slot >= 0 ? _slotPlayerIds[slot] : -1;
                ApplyVehicleOwner(vehicle, driverId);
                SetVehicleVisible(vehicle, driverId > 0);
                ApplyInputGate(vehicle);

                if (slot >= 0)
                    TintVehicle(vehicle, _playerTeams[slot]);
            }
        }

        /// <summary>
        /// Tells the roster whether the local player is in the arena, and with that whether the car they
        /// drive may listen to them.
        ///
        /// The recorder on a car reads VRChat's ordinary movement callbacks, which fire wherever the player
        /// happens to be. A driver who could drive from the hub steered the car by walking to a station,
        /// and every click on a button spent its boost. RequestJoinArena switches this on;
        /// RequestLeaveArena switches it off.
        ///
        /// Deliberately only a gate on input. The player is not immobilised -- the same sticks still walk
        /// their avatar while they drive -- because freezing them would strand anyone the way out could
        /// not reach.
        /// </summary>
        public void SetLocalPlayerInArena(bool inArena)
        {
            _localPlayerInArena = inArena;

            if (VehiclePool == null)
                return;

            for (int i = 0; i < VehiclePool.Length; i++)
            {
                if (VehiclePool[i] != null)
                    ApplyInputGate(VehiclePool[i]);
            }
        }

        public override void OnPlayerRespawn(VRCPlayerApi player)
        {
            // Respawning drops the player at the world spawn in the hub without going near HubTeleporter,
            // and they should not keep a car from the players still in the arena.
            if (player != null && player.isLocal && _localPlayerInArena)
                RequestLeaveArena();
        }

        /// <summary>
        /// Applies the arena gate to one car's recorder. It is set on every pool vehicle rather than only
        /// on the local driver's, so a car handed to this player later starts out gated as well; on a car
        /// somebody else drives the flag has nothing to switch off.
        /// </summary>
        private void ApplyInputGate(NetVehicle vehicle)
        {
            InputRecorder recorder = vehicle.Recorder;

            // NetVehicle resolves its recorder in its own Start, which may not have run yet.
            if (recorder == null)
                recorder = vehicle.GetComponent<InputRecorder>();

            if (recorder != null)
                recorder.InputEnabled = _localPlayerInArena;
        }

        /// <summary>
        /// The slot driving this pool index, or -1. VRChat player ids start at 1, so a slot whose player
        /// id is not positive is free whatever its vehicle column says.
        /// </summary>
        private int GetVehicleSlot(int vehicleIndex)
        {
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (IsSlotOccupied(i) && _playerVehicleIndices[i] == vehicleIndex)
                    return i;
            }

            return -1;
        }

        /// <summary>Is this pool index claimed by an occupied slot?</summary>
        private bool IsVehicleAssigned(int vehicleIndex)
        {
            return GetVehicleSlot(vehicleIndex) >= 0;
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
        /// Paints a driven car in its team's colour. A property block rather than the renderer's own
        /// material, so the pool's shared material asset is never touched and every car keeps batching.
        /// </summary>
        private void TintVehicle(NetVehicle vehicle, int team)
        {
            if (!TintVehiclesByTeam || (team != 0 && team != 1))
                return;

            if (_tintBlock == null)
                _tintBlock = new MaterialPropertyBlock();

            Color color = team == 0 ? BlueTeamColor : OrangeTeamColor;

            Renderer[] renderers = vehicle.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer tinted = renderers[i];
                if (tinted == null)
                    continue;

                tinted.GetPropertyBlock(_tintBlock);
                _tintBlock.SetColor("_Color", color);
                tinted.SetPropertyBlock(_tintBlock);
            }
        }

        /// <summary>
        /// Every client re-derives the pool from the slot table it just received. This is what makes the
        /// assignment networked: no SendCustomNetworkEvent is needed because the state is already synced.
        ///
        /// It is also where this client finds out whether its own seat request landed. A request the
        /// roster has not caught up with is sent again here as well as on its timer: a reply slower than
        /// the timer would otherwise find the timer already spent and leave a player who walked back to
        /// the hub still holding a car.
        /// </summary>
        public override void OnDeserialization()
        {
            ApplyVehicleAssignments();
            RetrySeatRequest();
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

            ApplyVehicleAssignments();
            RequestSerialization();
        }
    }
}
