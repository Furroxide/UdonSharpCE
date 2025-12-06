using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Helper class for managing VRCPlayerObject instances and player slot mapping.
    ///
    /// PlayerObjectHelper provides an abstraction layer over VRChat's PlayerObject system,
    /// making it easier to associate persistent data with individual players.
    /// </summary>
    /// <remarks>
    /// VRChat's PlayerObject system instantiates objects per-player for persistent synced state.
    /// This helper tracks which player is in which slot and provides utility methods for
    /// looking up players and their associated objects.
    ///
    /// Add this component to a GameObject in your scene and call Initialize() in Start().
    /// The helper will automatically track players as they join and leave.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class MyGameManager : UdonSharpBehaviour
    /// {
    ///     [SerializeField] private PlayerObjectHelper playerHelper;
    ///
    ///     void Start()
    ///     {
    ///         playerHelper.Initialize();
    ///     }
    ///
    ///     void Update()
    ///     {
    ///         int mySlot = playerHelper.GetLocalPlayerSlot();
    ///         // Use slot to index into synced arrays
    ///     }
    /// }
    /// </code>
    /// </example>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [PublicAPI]
    public class PlayerObjectHelper : UdonSharpBehaviour
    {
        #region Constants

        /// <summary>
        /// Maximum number of player objects supported by VRChat.
        /// </summary>
        public const int MAX_PLAYER_OBJECTS = 80;

        /// <summary>
        /// Invalid slot ID, returned when a player is not in any slot.
        /// </summary>
        public const int INVALID_SLOT = -1;

        #endregion

        #region Configuration

        /// <summary>
        /// Whether to automatically assign slots on player join.
        /// If false, call AssignPlayerSlot() manually.
        /// </summary>
        [Tooltip("Automatically assign slots when players join")]
        [SerializeField]
        private bool autoAssignSlots = true;

        /// <summary>
        /// Enable debug logging for player slot operations.
        /// </summary>
        [Tooltip("Log slot assignment/release operations")]
        [SerializeField]
        private bool debugLogging = false;

        #endregion

        #region State

        /// <summary>
        /// Players currently assigned to each slot.
        /// Null entries indicate empty slots.
        /// </summary>
        private VRCPlayerApi[] _playerSlots;

        /// <summary>
        /// Whether each slot is currently occupied.
        /// </summary>
        private bool[] _slotOccupied;

        /// <summary>
        /// Player ID to slot index mapping for fast lookup.
        /// Indexed by player.playerId, value is slot index or INVALID_SLOT.
        /// </summary>
        private int[] _playerIdToSlot;

        /// <summary>
        /// Number of slots currently in use.
        /// </summary>
        private int _activeSlotCount;

        /// <summary>
        /// The slot assigned to the local player.
        /// </summary>
        private int _localPlayerSlot = INVALID_SLOT;

        /// <summary>
        /// Whether the helper has been initialized.
        /// </summary>
        private bool _initialized;

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the player object helper.
        /// Call this in Start() before using other methods.
        /// </summary>
        public void Initialize()
        {
            if (_initialized)
            {
                if (debugLogging)
                    Debug.Log("[CE.PlayerObjectHelper] Already initialized");
                return;
            }

            _playerSlots = new VRCPlayerApi[MAX_PLAYER_OBJECTS];
            _slotOccupied = new bool[MAX_PLAYER_OBJECTS];
            _playerIdToSlot = new int[1024]; // Support player IDs up to 1024

            // Initialize player ID mapping to invalid
            for (int i = 0; i < _playerIdToSlot.Length; i++)
            {
                _playerIdToSlot[i] = INVALID_SLOT;
            }

            _activeSlotCount = 0;
            _localPlayerSlot = INVALID_SLOT;
            _initialized = true;

            if (debugLogging)
                Debug.Log("[CE.PlayerObjectHelper] Initialized with capacity " + MAX_PLAYER_OBJECTS);

            // Assign slots for any players already in the instance
            AssignExistingPlayers();
        }

        /// <summary>
        /// Assigns slots for players who were already in the instance when we initialized.
        /// </summary>
        private void AssignExistingPlayers()
        {
            if (!autoAssignSlots)
                return;

            var players = new VRCPlayerApi[MAX_PLAYER_OBJECTS];
            int count = VRCPlayerApi.GetPlayerCount();
            VRCPlayerApi.GetPlayers(players);

            for (int i = 0; i < count; i++)
            {
                var player = players[i];
                if (player != null && player.IsValid())
                {
                    AssignPlayerSlot(player);
                }
            }
        }

        #endregion

        #region VRChat Callbacks

        /// <summary>
        /// Called when a player joins the instance.
        /// Automatically assigns a slot if autoAssignSlots is enabled.
        /// </summary>
        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (!_initialized)
            {
                if (debugLogging)
                    Debug.LogWarning("[CE.PlayerObjectHelper] OnPlayerJoined called before Initialize()");
                return;
            }

            if (autoAssignSlots && player != null && player.IsValid())
            {
                AssignPlayerSlot(player);
            }
        }

        /// <summary>
        /// Called when a player leaves the instance.
        /// Releases their slot for reuse.
        /// </summary>
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (!_initialized)
                return;

            if (player != null)
            {
                ReleasePlayerSlot(player);
            }
        }

        #endregion

        #region Slot Management

        /// <summary>
        /// Assigns a slot to a player.
        /// </summary>
        /// <param name="player">The player to assign a slot to.</param>
        /// <returns>The assigned slot index, or INVALID_SLOT if no slots available.</returns>
        public int AssignPlayerSlot(VRCPlayerApi player)
        {
            if (!_initialized || player == null || !player.IsValid())
            {
                return INVALID_SLOT;
            }

            int playerId = player.playerId;

            // Check if player already has a slot
            if (playerId >= 0 && playerId < _playerIdToSlot.Length)
            {
                int existingSlot = _playerIdToSlot[playerId];
                if (existingSlot != INVALID_SLOT)
                {
                    if (debugLogging)
                        Debug.Log($"[CE.PlayerObjectHelper] Player {player.displayName} already in slot {existingSlot}");
                    return existingSlot;
                }
            }

            // Find first available slot
            for (int slot = 0; slot < MAX_PLAYER_OBJECTS; slot++)
            {
                if (!_slotOccupied[slot])
                {
                    _playerSlots[slot] = player;
                    _slotOccupied[slot] = true;
                    _activeSlotCount++;

                    if (playerId >= 0 && playerId < _playerIdToSlot.Length)
                    {
                        _playerIdToSlot[playerId] = slot;
                    }

                    // Track local player slot
                    if (player.isLocal)
                    {
                        _localPlayerSlot = slot;
                    }

                    if (debugLogging)
                        Debug.Log($"[CE.PlayerObjectHelper] Assigned player {player.displayName} to slot {slot}");

                    return slot;
                }
            }

            Debug.LogError($"[CE.PlayerObjectHelper] No slots available for player {player.displayName}");
            return INVALID_SLOT;
        }

        /// <summary>
        /// Releases a player's slot.
        /// </summary>
        /// <param name="player">The player whose slot to release.</param>
        /// <returns>True if the slot was released, false if player wasn't in a slot.</returns>
        public bool ReleasePlayerSlot(VRCPlayerApi player)
        {
            if (!_initialized || player == null)
            {
                return false;
            }

            int playerId = player.playerId;
            int slot = INVALID_SLOT;

            // Look up slot from player ID
            if (playerId >= 0 && playerId < _playerIdToSlot.Length)
            {
                slot = _playerIdToSlot[playerId];
            }

            // Fallback: search slots
            if (slot == INVALID_SLOT)
            {
                for (int i = 0; i < MAX_PLAYER_OBJECTS; i++)
                {
                    if (_slotOccupied[i] && _playerSlots[i] == player)
                    {
                        slot = i;
                        break;
                    }
                }
            }

            if (slot == INVALID_SLOT)
            {
                if (debugLogging)
                    Debug.Log($"[CE.PlayerObjectHelper] Player {player.displayName} was not in any slot");
                return false;
            }

            // Clear slot
            _playerSlots[slot] = null;
            _slotOccupied[slot] = false;
            _activeSlotCount--;

            if (playerId >= 0 && playerId < _playerIdToSlot.Length)
            {
                _playerIdToSlot[playerId] = INVALID_SLOT;
            }

            // Clear local player slot if applicable
            if (_localPlayerSlot == slot)
            {
                _localPlayerSlot = INVALID_SLOT;
            }

            if (debugLogging)
                Debug.Log($"[CE.PlayerObjectHelper] Released slot {slot} from player {player.displayName}");

            return true;
        }

        #endregion

        #region Slot Queries

        /// <summary>
        /// Gets the slot index for a player.
        /// </summary>
        /// <param name="player">The player to look up.</param>
        /// <returns>The player's slot index, or INVALID_SLOT if not assigned.</returns>
        public int GetPlayerSlot(VRCPlayerApi player)
        {
            if (!_initialized || player == null || !player.IsValid())
            {
                return INVALID_SLOT;
            }

            int playerId = player.playerId;
            if (playerId >= 0 && playerId < _playerIdToSlot.Length)
            {
                return _playerIdToSlot[playerId];
            }

            // Fallback: search slots
            for (int i = 0; i < MAX_PLAYER_OBJECTS; i++)
            {
                if (_slotOccupied[i] && _playerSlots[i] != null && _playerSlots[i].playerId == playerId)
                {
                    return i;
                }
            }

            return INVALID_SLOT;
        }

        /// <summary>
        /// Gets the player assigned to a slot.
        /// </summary>
        /// <param name="slot">The slot index to look up.</param>
        /// <returns>The player in the slot, or null if empty or invalid.</returns>
        public VRCPlayerApi GetPlayerInSlot(int slot)
        {
            if (!_initialized || slot < 0 || slot >= MAX_PLAYER_OBJECTS)
            {
                return null;
            }

            if (_slotOccupied[slot])
            {
                var player = _playerSlots[slot];
                if (player != null && player.IsValid())
                {
                    return player;
                }
            }

            return null;
        }

        /// <summary>
        /// Checks if a slot is currently occupied.
        /// </summary>
        /// <param name="slot">The slot index to check.</param>
        /// <returns>True if the slot is occupied, false otherwise.</returns>
        public bool IsSlotOccupied(int slot)
        {
            if (!_initialized || slot < 0 || slot >= MAX_PLAYER_OBJECTS)
            {
                return false;
            }

            return _slotOccupied[slot];
        }

        /// <summary>
        /// Gets the local player's slot index.
        /// </summary>
        /// <returns>The local player's slot, or INVALID_SLOT if not assigned.</returns>
        public int GetLocalPlayerSlot()
        {
            return _localPlayerSlot;
        }

        /// <summary>
        /// Gets the number of slots currently in use.
        /// </summary>
        public int ActiveSlotCount => _activeSlotCount;

        /// <summary>
        /// Gets the total capacity (maximum slots available).
        /// </summary>
        public int Capacity => MAX_PLAYER_OBJECTS;

        /// <summary>
        /// Gets whether the helper has been initialized.
        /// </summary>
        public bool IsInitialized => _initialized;

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets all currently occupied slot indices.
        /// </summary>
        /// <param name="result">Array to fill with slot indices. Must be at least ActiveSlotCount long.</param>
        /// <returns>The number of slots written to the result array.</returns>
        public int GetOccupiedSlots(int[] result)
        {
            if (!_initialized || result == null || result.Length == 0)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < MAX_PLAYER_OBJECTS && count < result.Length; i++)
            {
                if (_slotOccupied[i])
                {
                    result[count] = i;
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Gets all players currently assigned to slots.
        /// </summary>
        /// <param name="result">Array to fill with player references. Must be at least ActiveSlotCount long.</param>
        /// <returns>The number of players written to the result array.</returns>
        public int GetAllPlayers(VRCPlayerApi[] result)
        {
            if (!_initialized || result == null || result.Length == 0)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < MAX_PLAYER_OBJECTS && count < result.Length; i++)
            {
                if (_slotOccupied[i])
                {
                    var player = _playerSlots[i];
                    if (player != null && player.IsValid())
                    {
                        result[count] = player;
                        count++;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Iterates over all occupied slots and invokes a callback event.
        /// </summary>
        /// <param name="eventName">The custom event name to invoke for each slot.</param>
        /// <param name="target">The UdonBehaviour to invoke the event on.</param>
        /// <remarks>
        /// Before calling this, set the slot index via SetIterationSlot() to make it
        /// available to the callback.
        /// </remarks>
        public void ForEachSlot(string eventName, UdonSharpBehaviour target)
        {
            if (!_initialized || target == null || string.IsNullOrEmpty(eventName))
            {
                return;
            }

            for (int i = 0; i < MAX_PLAYER_OBJECTS; i++)
            {
                if (_slotOccupied[i])
                {
                    _iterationSlot = i;
                    _iterationPlayer = _playerSlots[i];
                    target.SendCustomEvent(eventName);
                }
            }

            _iterationSlot = INVALID_SLOT;
            _iterationPlayer = null;
        }

        // Iteration state for ForEachSlot
        private int _iterationSlot = INVALID_SLOT;
        private VRCPlayerApi _iterationPlayer;

        /// <summary>
        /// Gets the current iteration slot index (for use in ForEachSlot callback).
        /// </summary>
        public int GetIterationSlot() => _iterationSlot;

        /// <summary>
        /// Gets the current iteration player (for use in ForEachSlot callback).
        /// </summary>
        public VRCPlayerApi GetIterationPlayer() => _iterationPlayer;

        #endregion
    }
}
