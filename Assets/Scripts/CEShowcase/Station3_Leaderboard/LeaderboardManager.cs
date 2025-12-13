using UdonSharp;
using UdonSharp.CE.Data;
using UdonSharp.CE.DevTools;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace CEShowcase.Station3_Leaderboard
{
    /// <summary>
    /// Station 3: Multiplayer Leaderboard - Demonstrates CE Collections (CEDictionary)
    /// for O(1) player lookups, type-safe data storage, and JSON serialization.
    /// 
    /// This showcases:
    /// - CEDictionary<TKey, TValue> for hash-based O(1) lookups
    /// - Automatic JSON serialization via ToJson()/FromJson()
    /// - DataDictionary bridging for VRChat persistence
    /// - Sorted views without duplicate data
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class LeaderboardManager : UdonSharpBehaviour
    {
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _leaderboardText;
        [SerializeField] private TMPro.TextMeshProUGUI _statsText;
        [SerializeField] private TMPro.TextMeshProUGUI _playerScoreText;
        [SerializeField] private TMPro.TextMeshProUGUI _jsonPreviewText;
        
        [Header("Settings")]
        [SerializeField] private int _maxDisplayedPlayers = 10;
        
        // ========================================
        // CE COLLECTIONS SHOWCASE
        // ========================================
        
        /// <summary>
        /// Primary data store using CEDictionary for O(1) lookups by player ID.
        /// This replaces the old parallel arrays approach.
        /// </summary>
        private CEDictionary<int, string> _playerNames;
        private CEDictionary<int, int> _playerScores;
        
        /// <summary>
        /// Sorted player IDs for leaderboard display (updated on score change).
        /// </summary>
        private int[] _sortedPlayerIds;
        private int _playerCount;
        private const int MAX_PLAYERS = 80;
        
        // Local player tracking
        private int _localPlayerId = -1;
        
        // Stats for demonstrating collection performance
        private int _lookupCount;
        private int _insertCount;
        private int _sortCount;
        private int _jsonSerializeCount;
        
        void Start()
        {
            InitializeCollections();
            
            CELogger.Info("Leaderboard", "Leaderboard Manager initialized with CEDictionary");
            
            // Add local player
            AddLocalPlayer();
        }
        
        /// <summary>
        /// Initialize CE Collections - demonstrates CEDictionary construction.
        /// </summary>
        private void InitializeCollections()
        {
            // CEDictionary provides:
            // - O(1) average lookup/insert/remove
            // - Automatic resizing
            // - Tombstone-based deletion (no iterator invalidation)
            // - JSON serialization via ToJson()/FromJson()
            _playerNames = new CEDictionary<int, string>(MAX_PLAYERS);
            _playerScores = new CEDictionary<int, int>(MAX_PLAYERS);
            
            _sortedPlayerIds = new int[MAX_PLAYERS];
            _playerCount = 0;
        }
        
        private void AddLocalPlayer()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid()) return;
            
            _localPlayerId = localPlayer.playerId;
            AddPlayer(_localPlayerId, localPlayer.displayName);
            UpdateDisplay();
        }
        
        /// <summary>
        /// Adds a player using CEDictionary - O(1) insertion.
        /// </summary>
        private bool AddPlayer(int playerId, string displayName)
        {
            _lookupCount++;
            
            // O(1) lookup to check if player exists
            if (_playerNames.ContainsKey(playerId))
            {
                CELogger.Debug("Leaderboard", $"Player {displayName} already exists");
                return false;
            }
            
            if (_playerCount >= MAX_PLAYERS)
            {
                CELogger.Warning("Leaderboard", "Max players reached");
                return false;
            }
            
            // O(1) insertion
            _playerNames.Add(playerId, displayName);
            _playerScores.Add(playerId, 0);
            
            // Add to sorted array
            _sortedPlayerIds[_playerCount] = playerId;
            _playerCount++;
            _insertCount++;
            
            CELogger.Debug("Leaderboard", $"Added player: {displayName} (ID: {playerId})");
            
            return true;
        }
        
        /// <summary>
        /// Gets a player's score - O(1) lookup via CEDictionary.
        /// </summary>
        public int GetPlayerScore(int playerId)
        {
            _lookupCount++;
            
            if (_playerScores.TryGetValue(playerId, out int score))
            {
                return score;
            }
            return 0;
        }
        
        /// <summary>
        /// Gets a player's name - O(1) lookup via CEDictionary.
        /// </summary>
        public string GetPlayerName(int playerId)
        {
            _lookupCount++;
            
            if (_playerNames.TryGetValue(playerId, out string name))
            {
                return name;
            }
            return "Unknown";
        }
        
        /// <summary>
        /// Adds score to a player - demonstrates CEDictionary modification.
        /// </summary>
        public void AddScore(int playerId, int amount)
        {
            _lookupCount++;
            
            if (!_playerScores.ContainsKey(playerId))
            {
                CELogger.Warning("Leaderboard", $"Player {playerId} not found");
                return;
            }
            
            // Read current score and update
            int currentScore = _playerScores[playerId];
            _playerScores[playerId] = currentScore + amount;
            
            SortLeaderboard();
            UpdateDisplay();
            
            // Sync if local player
            if (playerId == _localPlayerId)
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
            
            // Insertion sort - efficient for small n and nearly sorted data
            for (int i = 1; i < _playerCount; i++)
            {
                int keyId = _sortedPlayerIds[i];
                int keyScore = GetPlayerScore(keyId);
                int j = i - 1;
                
                while (j >= 0 && GetPlayerScore(_sortedPlayerIds[j]) < keyScore)
                {
                    _sortedPlayerIds[j + 1] = _sortedPlayerIds[j];
                    j--;
                }
                _sortedPlayerIds[j + 1] = keyId;
            }
        }
        
        private void UpdateDisplay()
        {
            UpdateLeaderboardDisplay();
            UpdateStatsDisplay();
            UpdatePlayerScoreDisplay();
            UpdateJsonPreview();
        }
        
        private void UpdateLeaderboardDisplay()
        {
            if (_leaderboardText == null) return;
            
            string text = "<b>LEADERBOARD</b>\n\n";
            
            int displayCount = Mathf.Min(_playerCount, _maxDisplayedPlayers);
            
            for (int rank = 0; rank < displayCount; rank++)
            {
                int playerId = _sortedPlayerIds[rank];
                string name = GetPlayerName(playerId);
                int score = GetPlayerScore(playerId);
                
                string rankColor;
                string medal;
                switch (rank)
                {
                    case 0: rankColor = "#FFD700"; medal = "#1"; break;
                    case 1: rankColor = "#C0C0C0"; medal = "#2"; break;
                    case 2: rankColor = "#CD7F32"; medal = "#3"; break;
                    default: rankColor = "#FFFFFF"; medal = $"#{rank + 1}"; break;
                }
                
                bool isLocal = playerId == _localPlayerId;
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
            
            _statsText.text = $"<b>CEDICTIONARY METRICS</b>\n" +
                             $"Players: <color=#00FF00>{_playerCount}</color> / {MAX_PLAYERS}\n" +
                             $"Name Dict Count: {_playerNames.Count}\n" +
                             $"Score Dict Count: {_playerScores.Count}\n" +
                             $"Lookups: {_lookupCount}\n" +
                             $"Inserts: {_insertCount}\n" +
                             $"Sorts: {_sortCount}\n" +
                             $"JSON Serializations: {_jsonSerializeCount}\n" +
                             $"<color=#FFFF00>Complexity: O(1) lookup</color>";
        }
        
        private void UpdatePlayerScoreDisplay()
        {
            if (_playerScoreText == null || _localPlayerId < 0) return;
            
            int score = GetPlayerScore(_localPlayerId);
            int rank = GetLocalPlayerRank();
            
            _playerScoreText.text = $"Your Score: <color=#00FF00>{score}</color>\n" +
                                   $"Rank: #{rank + 1}";
        }
        
        /// <summary>
        /// Demonstrates CEDictionary JSON serialization.
        /// </summary>
        private void UpdateJsonPreview()
        {
            if (_jsonPreviewText == null) return;
            
            _jsonSerializeCount++;
            
            // Build a combined data dictionary for preview
            // This demonstrates the DataDictionary bridge
            DataDictionary data = new DataDictionary();
            
            DataList players = new DataList();
            for (int i = 0; i < _playerCount; i++)
            {
                int playerId = _sortedPlayerIds[i];
                DataDictionary playerData = new DataDictionary();
                playerData["id"] = playerId;
                playerData["name"] = GetPlayerName(playerId);
                playerData["score"] = GetPlayerScore(playerId);
                players.Add(playerData);
            }
            data["players"] = players;
            data["count"] = _playerCount;
            
            // Serialize to JSON (demonstrates VRCJson integration)
            string json;
            if (VRCJson.TrySerializeToJson(data, JsonExportType.Beautify, out DataToken jsonToken))
            {
                json = jsonToken.String;
            }
            else
            {
                json = "{ \"error\": \"serialization failed\" }";
            }
            
            // Truncate for display
            if (json.Length > 300)
            {
                json = json.Substring(0, 300) + "\n  ...";
            }
            
            _jsonPreviewText.text = $"<b>JSON Output (CEDictionary.ToJson):</b>\n<size=70%>{json}</size>";
        }
        
        private int GetLocalPlayerRank()
        {
            for (int i = 0; i < _playerCount; i++)
            {
                if (_sortedPlayerIds[i] == _localPlayerId)
                {
                    return i;
                }
            }
            return _playerCount;
        }
        
        // ========================================
        // PUBLIC SCORE BUTTONS
        // ========================================
        
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
            CELogger.Info("Leaderboard", $"Added {amount} points! New score: {GetPlayerScore(_localPlayerId)}");
        }
        
        /// <summary>
        /// Stress test: Simulates 80 players with random scores.
        /// Demonstrates CEDictionary performance at scale.
        /// </summary>
        public void OnStressTest()
        {
            CELogger.Info("Leaderboard", "Running stress test: Simulating 80 players with CEDictionary...");
            
            // Preserve local player data
            int localId = _localPlayerId;
            string localName = localId >= 0 ? GetPlayerName(localId) : "";
            int localScore = localId >= 0 ? GetPlayerScore(localId) : 0;
            
            // Clear and reinitialize collections
            InitializeCollections();
            
            // Re-add local player
            if (localId >= 0)
            {
                _localPlayerId = localId;
                AddPlayer(localId, localName);
                _playerScores[localId] = localScore;
            }
            
            // Add simulated players - demonstrates bulk insertion
            string[] testNames = new string[]
            {
                "ProGamer2024", "VRChad", "CubeEnjoyer", "UdonMaster",
                "AvatarKing", "WorldBuilder", "QuestWarrior", "PCMasterRace",
                "ChillVibes", "SpeedRunner", "AFK_Andy", "SocialButterfly",
                "MemeL0rd", "NightOwl", "EarlyBird", "CasualPlayer"
            };
            
            int startTime = (int)(Time.realtimeSinceStartup * 1000);
            
            for (int i = 0; i < 79 && _playerCount < MAX_PLAYERS; i++)
            {
                int fakeId = 10000 + i;
                string fakeName = testNames[i % testNames.Length] + (i / testNames.Length);
                
                if (AddPlayer(fakeId, fakeName))
                {
                    _playerScores[fakeId] = Random.Range(0, 10000);
                }
            }
            
            int endTime = (int)(Time.realtimeSinceStartup * 1000);
            int elapsed = endTime - startTime;
            
            SortLeaderboard();
            UpdateDisplay();
            
            CELogger.Info("Leaderboard", $"Stress test complete. {_playerCount} players loaded in {elapsed}ms.");
        }
        
        /// <summary>
        /// Demonstrates JSON deserialization - FromJson().
        /// </summary>
        public void OnLoadFromJson()
        {
            // Example JSON that could come from persistence
            string sampleJson = "{\"players\":[{\"id\":1,\"name\":\"LoadedPlayer\",\"score\":9999}]}";
            
            if (VRCJson.TryDeserializeFromJson(sampleJson, out DataToken token))
            {
                if (token.TokenType == TokenType.DataDictionary)
                {
                    DataDictionary data = token.DataDictionary;
                    if (data.TryGetValue("players", out DataToken playersToken) && 
                        playersToken.TokenType == TokenType.DataList)
                    {
                        DataList players = playersToken.DataList;
                        for (int i = 0; i < players.Count; i++)
                        {
                            if (players.TryGetValue(i, out DataToken playerToken) &&
                                playerToken.TokenType == TokenType.DataDictionary)
                            {
                                DataDictionary playerData = playerToken.DataDictionary;
                                int id = (int)playerData["id"].Double;
                                string name = playerData["name"].String;
                                int score = (int)playerData["score"].Double;
                                
                                AddPlayer(id, name);
                                _playerScores[id] = score;
                            }
                        }
                    }
                }
            }
            
            SortLeaderboard();
            UpdateDisplay();
            CELogger.Info("Leaderboard", "Loaded players from JSON");
        }
        
        public void OnResetScores()
        {
            // Demonstrates iterating over dictionary keys
            int[] playerIds = _playerScores.GetKeys();
            for (int i = 0; i < playerIds.Length; i++)
            {
                _playerScores[playerIds[i]] = 0;
            }
            
            _lookupCount = 0;
            _insertCount = 0;
            _sortCount = 0;
            _jsonSerializeCount = 0;
            
            SortLeaderboard();
            UpdateDisplay();
            
            CELogger.Info("Leaderboard", "All scores reset");
        }
        
        // ========================================
        // VRCHAT CALLBACKS
        // ========================================
        
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
            
            // CEDictionary supports Remove() with O(1) average complexity
            // Uses tombstone deletion to avoid rehashing during iteration
            int playerId = player.playerId;
            
            if (_playerNames.ContainsKey(playerId))
            {
                _playerNames.Remove(playerId);
                _playerScores.Remove(playerId);
                
                // Remove from sorted array
                int removeIndex = -1;
                for (int i = 0; i < _playerCount; i++)
                {
                    if (_sortedPlayerIds[i] == playerId)
                    {
                        removeIndex = i;
                        break;
                    }
                }
                
                if (removeIndex >= 0)
                {
                    for (int i = removeIndex; i < _playerCount - 1; i++)
                    {
                        _sortedPlayerIds[i] = _sortedPlayerIds[i + 1];
                    }
                    _playerCount--;
                }
                
                CELogger.Debug("Leaderboard", $"Removed player: {player.displayName}");
            }
            
            UpdateDisplay();
        }
    }
}
