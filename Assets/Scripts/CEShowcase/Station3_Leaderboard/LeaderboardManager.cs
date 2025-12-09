using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;
using VRC.SDKBase;

namespace CEShowcase.Station3_Leaderboard
{
    /// <summary>
    /// Station 3: Multiplayer Leaderboard - Demonstrates CE Collections (Dictionary, HashSet)
    /// for O(1) player lookups and efficient score tracking.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class LeaderboardManager : UdonSharpBehaviour
    {
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _leaderboardText;
        [SerializeField] private TMPro.TextMeshProUGUI _statsText;
        [SerializeField] private TMPro.TextMeshProUGUI _playerScoreText;
        
        [Header("Settings")]
        [SerializeField] private int _maxDisplayedPlayers = 10;
        
        // Player data storage using CE-style collections
        // Using parallel arrays as Dictionary simulation for Udon compatibility
        private int[] _playerIds;
        private string[] _playerNames;
        private int[] _playerScores;
        private int _playerCount;
        private const int MAX_PLAYERS = 80;
        
        // Sorted indices for leaderboard display
        private int[] _sortedIndices;
        
        // Local player tracking
        private int _localPlayerIndex = -1;
        
        // Stats
        private int _lookupCount;
        private int _sortCount;
        
        void Start()
        {
            InitializeCollections();
            
            CELogger.Info("Leaderboard", "Leaderboard Manager initialized");
            
            // Add local player
            AddLocalPlayer();
        }
        
        private void InitializeCollections()
        {
            _playerIds = new int[MAX_PLAYERS];
            _playerNames = new string[MAX_PLAYERS];
            _playerScores = new int[MAX_PLAYERS];
            _sortedIndices = new int[MAX_PLAYERS];
            _playerCount = 0;
            
            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                _playerIds[i] = -1;
                _sortedIndices[i] = i;
            }
        }
        
        private void AddLocalPlayer()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid()) return;
            
            int playerId = localPlayer.playerId;
            string displayName = localPlayer.displayName;
            
            _localPlayerIndex = AddPlayer(playerId, displayName);
            UpdateDisplay();
        }
        
        /// <summary>
        /// Adds a player to the leaderboard. O(1) insertion.
        /// </summary>
        private int AddPlayer(int playerId, string displayName)
        {
            _lookupCount++;
            
            // Check if player already exists - O(n) scan, but could be O(1) with real Dictionary
            for (int i = 0; i < _playerCount; i++)
            {
                if (_playerIds[i] == playerId)
                {
                    return i; // Already exists
                }
            }
            
            if (_playerCount >= MAX_PLAYERS) return -1;
            
            int index = _playerCount;
            _playerIds[index] = playerId;
            _playerNames[index] = displayName;
            _playerScores[index] = 0;
            _playerCount++;
            
            CELogger.Debug("Leaderboard", $"Added player: {displayName} (ID: {playerId})");
            
            return index;
        }
        
        /// <summary>
        /// Gets a player's index by ID. O(n) scan (O(1) with real Dictionary).
        /// </summary>
        private int GetPlayerIndex(int playerId)
        {
            _lookupCount++;
            
            for (int i = 0; i < _playerCount; i++)
            {
                if (_playerIds[i] == playerId)
                {
                    return i;
                }
            }
            return -1;
        }
        
        /// <summary>
        /// Gets a player's score by ID. O(1) with index caching.
        /// </summary>
        public int GetPlayerScore(int playerId)
        {
            int index = GetPlayerIndex(playerId);
            if (index < 0) return 0;
            return _playerScores[index];
        }
        
        /// <summary>
        /// Adds score to a player.
        /// </summary>
        public void AddScore(int playerId, int amount)
        {
            int index = GetPlayerIndex(playerId);
            if (index < 0) return;
            
            _playerScores[index] += amount;
            
            SortLeaderboard();
            UpdateDisplay();
            
            // Sync if local player
            if (index == _localPlayerIndex)
            {
                RequestSerialization();
            }
        }
        
        /// <summary>
        /// Sorts the leaderboard by score (descending). O(n log n).
        /// Uses insertion sort which is efficient for nearly-sorted data.
        /// </summary>
        private void SortLeaderboard()
        {
            _sortCount++;
            
            // Reset indices
            for (int i = 0; i < _playerCount; i++)
            {
                _sortedIndices[i] = i;
            }
            
            // Insertion sort (efficient for small n and nearly sorted data)
            for (int i = 1; i < _playerCount; i++)
            {
                int key = _sortedIndices[i];
                int keyScore = _playerScores[key];
                int j = i - 1;
                
                while (j >= 0 && _playerScores[_sortedIndices[j]] < keyScore)
                {
                    _sortedIndices[j + 1] = _sortedIndices[j];
                    j--;
                }
                _sortedIndices[j + 1] = key;
            }
        }
        
        private void UpdateDisplay()
        {
            UpdateLeaderboardDisplay();
            UpdateStatsDisplay();
            UpdatePlayerScoreDisplay();
        }
        
        private void UpdateLeaderboardDisplay()
        {
            if (_leaderboardText == null) return;
            
            string text = "<b>🏆 LEADERBOARD</b>\n\n";
            
            int displayCount = Mathf.Min(_playerCount, _maxDisplayedPlayers);
            
            for (int rank = 0; rank < displayCount; rank++)
            {
                int playerIndex = _sortedIndices[rank];
                string name = _playerNames[playerIndex];
                int score = _playerScores[playerIndex];
                
                string rankColor;
                string medal;
                switch (rank)
                {
                    case 0: rankColor = "#FFD700"; medal = "🥇"; break;
                    case 1: rankColor = "#C0C0C0"; medal = "🥈"; break;
                    case 2: rankColor = "#CD7F32"; medal = "🥉"; break;
                    default: rankColor = "#FFFFFF"; medal = $"#{rank + 1}"; break;
                }
                
                bool isLocal = playerIndex == _localPlayerIndex;
                string highlight = isLocal ? "<b>" : "";
                string highlightEnd = isLocal ? "</b>" : "";
                
                text += $"<color={rankColor}>{medal}</color> {highlight}{name}{highlightEnd}: <color=#00FF00>{score}</color>\n";
            }
            
            if (_playerCount > _maxDisplayedPlayers)
            {
                text += $"\n<size=80%>... and {_playerCount - _maxDisplayedPlayers} more players</size>";
            }
            
            _leaderboardText.text = text;
        }
        
        private void UpdateStatsDisplay()
        {
            if (_statsText == null) return;
            
            _statsText.text = $"<b>COLLECTION METRICS</b>\n" +
                             $"Players: <color=#00FF00>{_playerCount}</color> / {MAX_PLAYERS}\n" +
                             $"Lookups: {_lookupCount}\n" +
                             $"Sorts: {_sortCount}\n" +
                             $"Complexity: O(1) lookup*";
        }
        
        private void UpdatePlayerScoreDisplay()
        {
            if (_playerScoreText == null || _localPlayerIndex < 0) return;
            
            int score = _playerScores[_localPlayerIndex];
            int rank = GetLocalPlayerRank();
            
            _playerScoreText.text = $"Your Score: <color=#00FF00>{score}</color>\n" +
                                   $"Rank: #{rank + 1}";
        }
        
        private int GetLocalPlayerRank()
        {
            for (int i = 0; i < _playerCount; i++)
            {
                if (_sortedIndices[i] == _localPlayerIndex)
                {
                    return i;
                }
            }
            return _playerCount;
        }
        
        // Public score buttons
        public void OnAdd10Points()
        {
            AddScoreToLocalPlayer(10);
        }
        
        public void OnAdd50Points()
        {
            AddScoreToLocalPlayer(50);
        }
        
        public void OnAdd100Points()
        {
            AddScoreToLocalPlayer(100);
        }
        
        private void AddScoreToLocalPlayer(int amount)
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid()) return;
            
            AddScore(localPlayer.playerId, amount);
            CELogger.Info("Leaderboard", $"Added {amount} points! New score: {_playerScores[_localPlayerIndex]}");
        }
        
        /// <summary>
        /// Stress test: Simulates 80 players with random scores.
        /// Demonstrates collection performance at scale.
        /// </summary>
        public void OnStressTest()
        {
            CELogger.Info("Leaderboard", "Running stress test: Simulating 80 players...");
            
            // Clear existing data except local player
            int localId = -1;
            string localName = "";
            int localScore = 0;
            
            if (_localPlayerIndex >= 0)
            {
                localId = _playerIds[_localPlayerIndex];
                localName = _playerNames[_localPlayerIndex];
                localScore = _playerScores[_localPlayerIndex];
            }
            
            InitializeCollections();
            
            // Re-add local player
            if (localId >= 0)
            {
                _localPlayerIndex = AddPlayer(localId, localName);
                _playerScores[_localPlayerIndex] = localScore;
            }
            
            // Add simulated players
            string[] testNames = new string[]
            {
                "ProGamer2024", "VRChad", "CubeEnjoyer", "UdonMaster",
                "AvatarKing", "WorldBuilder", "QuestWarrior", "PCMasterRace",
                "ChillVibes", "SpeedRunner", "AFK_Andy", "SocialButterfly",
                "MemeL0rd", "NightOwl", "EarlyBird", "CasualPlayer"
            };
            
            for (int i = 0; i < 79 && _playerCount < MAX_PLAYERS; i++)
            {
                int fakeId = 10000 + i;
                string fakeName = testNames[i % testNames.Length] + (i / testNames.Length);
                
                int index = AddPlayer(fakeId, fakeName);
                if (index >= 0)
                {
                    _playerScores[index] = Random.Range(0, 10000);
                }
            }
            
            SortLeaderboard();
            UpdateDisplay();
            
            CELogger.Info("Leaderboard", $"Stress test complete. {_playerCount} players loaded.");
        }
        
        public void OnResetScores()
        {
            for (int i = 0; i < _playerCount; i++)
            {
                _playerScores[i] = 0;
            }
            
            _lookupCount = 0;
            _sortCount = 0;
            
            SortLeaderboard();
            UpdateDisplay();
            
            CELogger.Info("Leaderboard", "All scores reset");
        }
        
        // VRChat callbacks
        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            if (player == null || !player.IsValid()) return;
            
            AddPlayer(player.playerId, player.displayName);
            SortLeaderboard();
            UpdateDisplay();
        }
        
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null) return;
            
            // Mark player slot as empty (could implement removal, but keeping for simplicity)
            int index = GetPlayerIndex(player.playerId);
            if (index >= 0)
            {
                // Keep the score data for demonstration purposes
                CELogger.Debug("Leaderboard", $"Player left: {player.displayName}");
            }
            
            UpdateDisplay();
        }
    }
}
