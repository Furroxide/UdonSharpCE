using UdonSharp;
using UdonSharp.CE.Data;
using UdonSharp.CE.DevTools;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

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

        // ========================================
        // NETWORK TRANSPORT
        // ========================================

        // CEDictionary is a managed C# object, so Udon cannot sync it. Before this, the behaviour was
        // Manual sync with *no synced fields at all*: RequestSerialization() transmitted nothing and the
        // "multiplayer leaderboard" was single-player by construction, with OnPlayerJoined merely adding
        // remote players locally at score 0.
        //
        // Parallel synced arrays are the wire format; the CEDictionary remains the in-memory index that
        // the station is actually showcasing. Arrays require Manual sync (Continuous cannot carry them),
        // and every synced array must be initialized at declaration or it does not sync at all.
        [UdonSynced] private int[] _syncedPlayerIds = new int[MAX_PLAYERS];
        [UdonSynced] private int[] _syncedScores = new int[MAX_PLAYERS];
        [UdonSynced] private string[] _syncedNames = new string[MAX_PLAYERS];
        [UdonSynced] private int _syncedCount;
        
        // Local player tracking
        private int _localPlayerId = -1;
        
        // Stats for demonstrating collection performance
        private int _lookupCount;
        private int _insertCount;
        private int _sortCount;
        private int _jsonSerializeCount;
        
        void Start()
        {
            // Rebuild rather than merely initialize: VRChat may deliver the synced arrays before Start
            // runs, and it drops the OnDeserialization event for a behaviour that has not started yet,
            // so this is the only place that delivery is ever applied. A bare InitializeCollections would
            // throw that roster away and leave the board empty until somebody happened to score again.
            RebuildFromSyncedState();

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
            // - Tombstone-based deletion (Remove still rehashes in place once tombstones pass a quarter
            //   of capacity, so never remove while walking the table)
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
        ///
        /// This is the owner's half of scoring: it changes the local index and publishes it, and the
        /// publish is inert on a client that does not hold the board. A button that called this directly
        /// moved the presser's own screen and nothing else, and the owner's next publish wiped it again.
        /// Buttons go through <see cref="RequestScore"/>.
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

            // Publish to everyone. Inert unless we hold the board; RequestScore is what carries a
            // non-owner's points to the client that does.
            PublishLeaderboard();
        }

        /// <summary>
        /// Scores for the local player, from whichever client pressed the button.
        ///
        /// The board has exactly one writer, its owner, so everybody else asks the owner to score for
        /// them. Claiming the board from the button instead looks simpler and loses points: two players
        /// pressing inside one round trip each become the owner locally, each publish a roster that is
        /// missing the other's press, and the last one to arrive wins.
        /// </summary>
        public void RequestScore(int amount)
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
                return;

            if (Networking.IsOwner(localPlayer, gameObject))
            {
                CreditPlayer(localPlayer, amount);
                return;
            }

            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(OwnerAddScore), amount);
        }

        /// <summary>
        /// Network entry point for <see cref="RequestScore"/>. Runs on the owner, which credits whoever
        /// sent the request rather than trusting a player id in the payload.
        /// </summary>
        [NetworkCallable(10)]
        public void OwnerAddScore(int amount)
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !Networking.IsOwner(localPlayer, gameObject))
                return;

            // Null for a local call. The owner scores through RequestScore's direct path, so a request
            // with nobody behind it has nobody to credit.
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller == null || !caller.IsValid())
                return;

            CreditPlayer(caller, amount);
        }

        /// <summary>
        /// The owner's credit step for either route. The player is seated first if the board has never
        /// heard of them: a request can beat the join that would have seated its sender, and an owner
        /// that inherited the board holds whatever roster the last delivery carried, which need not
        /// include itself. AddScore would otherwise drop the points with a warning.
        /// </summary>
        private void CreditPlayer(VRCPlayerApi player, int amount)
        {
            if (!_playerNames.ContainsKey(player.playerId))
                AddPlayer(player.playerId, player.displayName);

            AddScore(player.playerId, amount);
        }

        /// <summary>
        /// Copies the CEDictionary contents into the synced arrays and publishes them.
        ///
        /// Owner-only, and deliberately does NOT take ownership. This runs from paths that fire on every
        /// client (a join, for one), and grabbing the object first meant every client in turn became the
        /// owner and then overwrote the synced roster with its own local view. A late joiner has never
        /// seen anybody score, so its view has everyone on zero -- last writer wins and the whole board
        /// randomly reset. Nothing claims the board any more: new scores reach the owner as requests,
        /// see RequestScore.
        /// </summary>
        public void PublishLeaderboard()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null)
                return;

            if (!Networking.IsOwner(localPlayer, gameObject))
                return;

            EnsureSyncArrays();

            int count = 0;
            for (int i = 0; i < _playerCount && count < MAX_PLAYERS; i++)
            {
                int playerId = _sortedPlayerIds[i];
                if (!_playerNames.ContainsKey(playerId))
                    continue;

                _syncedPlayerIds[count] = playerId;
                _syncedScores[count] = _playerScores[playerId];
                _syncedNames[count] = _playerNames[playerId];
                count++;
            }

            // Blank the tail. Null entries inside a synced string[] are a serialization hazard.
            for (int i = count; i < MAX_PLAYERS; i++)
            {
                _syncedPlayerIds[i] = 0;
                _syncedScores[i] = 0;
                _syncedNames[i] = "";
            }

            _syncedCount = count;
            RequestSerialization();
        }

        /// <summary>
        /// Runs on every remote client and on late joiners, who receive the arrays automatically as part
        /// of normal synced-variable state.
        /// </summary>
        public override void OnDeserialization()
        {
            RebuildFromSyncedState();
        }

        private void OnEnable()
        {
            // Re-apply on enable. The synced arrays only reach the local index through OnDeserialization,
            // which a disabled behaviour never receives, so a leaderboard whose station is toggled on
            // later would show an empty board until somebody happened to score again. Rebuilding is
            // idempotent and never publishes, so a non-owner running it changes nothing on the wire.
            if (_playerNames == null)
            {
                // First activation: OnEnable runs before Start, which seeds the collections and applies
                // the state itself. Rebuilding here would only NullReference on the index.
                return;
            }

            RebuildFromSyncedState();

            // The rebuild replaces the index wholesale, so re-seat the local player: they are only in the
            // owner's published roster if the owner has published since they joined.
            AddLocalPlayer();
        }

        /// <summary>
        /// Rebuilds the local CEDictionary index from the synced arrays. Never calls RequestSerialization,
        /// so running it on a non-owner cannot publish that client's view of the board back over the top
        /// of the owner's.
        /// </summary>
        private void RebuildFromSyncedState()
        {
            EnsureSyncArrays();
            InitializeCollections();

            int count = Mathf.Clamp(_syncedCount, 0, MAX_PLAYERS);
            for (int i = 0; i < count; i++)
            {
                string name = _syncedNames[i];
                if (string.IsNullOrEmpty(name))
                    continue;

                AddPlayer(_syncedPlayerIds[i], name);
                _playerScores[_syncedPlayerIds[i]] = _syncedScores[i];
            }

            SortLeaderboard();
            UpdateDisplay();
        }

        private void EnsureSyncArrays()
        {
            if (_syncedPlayerIds == null || _syncedPlayerIds.Length != MAX_PLAYERS)
                _syncedPlayerIds = new int[MAX_PLAYERS];

            if (_syncedScores == null || _syncedScores.Length != MAX_PLAYERS)
                _syncedScores = new int[MAX_PLAYERS];

            if (_syncedNames == null || _syncedNames.Length != MAX_PLAYERS)
                _syncedNames = new string[MAX_PLAYERS];

            for (int i = 0; i < MAX_PLAYERS; i++)
            {
                if (_syncedNames[i] == null)
                    _syncedNames[i] = "";
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
            // The same single-writer route the ScoreButtons take. This used to claim the board and
            // publish from here, which is the last-writer-wins race RequestScore describes.
            RequestScore(amount);
            CELogger.Info("Leaderboard", $"Requested {amount} points");
        }

        // The live roster, parked while a local-only demo borrows the index fields. See BeginScratchRoster.
        private CEDictionary<int, string> _liveNames;
        private CEDictionary<int, int> _liveScores;
        private int[] _liveSortedIds;
        private int _liveCount;

        /// <summary>
        /// Parks the live roster and points the index fields at empty scratch collections, so a local
        /// demo can reuse AddPlayer / SortLeaderboard / UpdateDisplay without touching shared state.
        ///
        /// The demos used to run on the live index, which is the one PublishLeaderboard serializes: the
        /// next publish from that client handed its invented roster to the whole instance. Udon is
        /// single-threaded and nothing between here and EndScratchRoster yields, so no join, score or
        /// deserialization can observe the borrowed fields -- by the time anything can publish, the live
        /// roster is back. The board goes on showing the scratch result until the next real update
        /// redraws it.
        /// </summary>
        private void BeginScratchRoster()
        {
            _liveNames = _playerNames;
            _liveScores = _playerScores;
            _liveSortedIds = _sortedPlayerIds;
            _liveCount = _playerCount;

            InitializeCollections();
        }

        private void EndScratchRoster()
        {
            _playerNames = _liveNames;
            _playerScores = _liveScores;
            _sortedPlayerIds = _liveSortedIds;
            _playerCount = _liveCount;

            _liveNames = null;
            _liveScores = null;
            _liveSortedIds = null;
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
            
            // Benchmark on a scratch roster. Refilling the live one evicted every real player, and the 79
            // invented ids filled the table so nobody could be added back for the rest of the instance.
            BeginScratchRoster();
            
            // Seat the local player among the simulated ones
            if (localId >= 0)
            {
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
            
            int simulatedCount = _playerCount;
            EndScratchRoster();

            CELogger.Info("Leaderboard", $"Stress test complete. {simulatedCount} players loaded in {elapsed}ms.");
        }
        
        /// <summary>
        /// Demonstrates JSON deserialization - FromJson().
        /// </summary>
        public void OnLoadFromJson()
        {
            // Example JSON that could come from persistence
            string sampleJson = "{\"players\":[{\"id\":1,\"name\":\"LoadedPlayer\",\"score\":9999}]}";

            // Loaded into a scratch roster, like the stress test. The sample's id 1 is a real VRChat
            // player id -- the first player into every instance -- so loading it over the live index
            // handed that player 9999 points on the next publish.
            BeginScratchRoster();
            
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

            EndScratchRoster();
            CELogger.Info("Leaderboard", "Loaded players from JSON");
        }
        
        /// <summary>
        /// Previews the board with every score at zero. A preview, not a reset: the scores belong to
        /// whoever holds the board, and zeroing the live index here stayed invisible to everyone else
        /// right up until this client next published, at which point it wiped the instance's scores.
        /// </summary>
        public void OnResetScores()
        {
            // Copy the live roster out so the zeroing below has something of its own to work on
            int count = _playerCount;
            int[] rosterIds = new int[count];
            string[] rosterNames = new string[count];
            int[] rosterScores = new int[count];
            for (int i = 0; i < count; i++)
            {
                rosterIds[i] = _sortedPlayerIds[i];
                rosterNames[i] = GetPlayerName(rosterIds[i]);
                rosterScores[i] = GetPlayerScore(rosterIds[i]);
            }

            BeginScratchRoster();

            for (int i = 0; i < count; i++)
            {
                AddPlayer(rosterIds[i], rosterNames[i]);
                _playerScores[rosterIds[i]] = rosterScores[i];
            }

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
            
            EndScratchRoster();

            CELogger.Info("Leaderboard", "Score reset previewed");
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

            // This callback runs on EVERY client, so nothing here may take the board: an ownership claim
            // per client per join is an instance-wide scramble that ends with someone's zeroed local view
            // as the authoritative roster. PublishLeaderboard is now owner-only, so exactly the one
            // client that already holds the board adds the joiner to it and everybody else no-ops.
            PublishLeaderboard();
        }
        
        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null) return;
            
            // CEDictionary supports Remove() with O(1) average complexity. It leaves a tombstone, and
            // rehashes in place once tombstones pass a quarter of capacity -- so a Remove can still move
            // every entry, and nothing here walks the table while removing.
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
