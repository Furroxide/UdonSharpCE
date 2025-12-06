using UdonSharp;
using UdonSharp.CE.Data;
using UdonSharp.CE.Persistence;
using UdonSharp.CE.DevTools;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDKBase;
using VRC.Udon;

namespace UdonSharp.CE.Samples.Persistence
{
    /// <summary>
    /// Example data model for RPG-style player saves.
    ///
    /// This demonstrates how to use CE.Persistence attributes to define
    /// a persistent data structure that can be saved to VRChat's PlayerData.
    /// </summary>
    [PlayerData("rpg_save_v1")]
    public class PlayerSaveData
    {
        /// <summary>
        /// Player experience points.
        /// </summary>
        [PersistKey("xp")]
        public int experience;

        /// <summary>
        /// Player level (constrained to 1-100).
        /// </summary>
        [PersistKey("lvl"), Range(1, 100)]
        public int level = 1;

        /// <summary>
        /// Player display name (max 32 characters).
        /// </summary>
        [PersistKey("name"), MaxLength(32)]
        public string displayName;

        /// <summary>
        /// Last known position in the world.
        /// </summary>
        [PersistKey("pos")]
        public Vector3 lastPosition;

        /// <summary>
        /// Inventory item IDs (max 100 slots).
        /// </summary>
        [PersistKey("inv"), MaxLength(100)]
        public int[] inventory = new int[100];

        /// <summary>
        /// Inventory item quantities.
        /// </summary>
        [PersistKey("qty"), MaxLength(100)]
        public int[] quantities = new int[100];

        /// <summary>
        /// Number of items in inventory.
        /// </summary>
        [PersistKey("inv_count")]
        public int inventoryCount;

        /// <summary>
        /// Quest completion flags (bit flags).
        /// </summary>
        [PersistKey("quests")]
        public int questFlags;

        /// <summary>
        /// Current gold amount.
        /// </summary>
        [PersistKey("gold"), Range(0, 999999)]
        public int gold;

        /// <summary>
        /// Total playtime in seconds.
        /// </summary>
        [PersistKey("time")]
        public float playtime;
    }

    /// <summary>
    /// Example UdonSharpBehaviour demonstrating CE.Persistence usage.
    ///
    /// This shows the complete flow of:
    /// 1. Registering a persistent data model
    /// 2. Loading saved data when a player joins
    /// 3. Saving data periodically and on demand
    /// 4. Handling various restore results
    /// </summary>
    public class PersistenceExample : UdonSharpBehaviour
    {
        [Header("UI References")]
        [SerializeField] private UnityEngine.UI.Text statusText;
        [SerializeField] private UnityEngine.UI.Text statsText;

        [Header("Settings")]
        [SerializeField] private float autoSaveInterval = 60f;

        // The player's save data
        private PlayerSaveData _saveData;

        // Tracking
        private float _lastSaveTime;
        private float _sessionStartTime;
        private bool _isInitialized;

        #region Lifecycle

        private void Start()
        {
            // Register the converter for PlayerSaveData
            RegisterConverter();

            _sessionStartTime = Time.time;
            CELogger.Info("PersistenceExample initialized");
        }

        public override void OnPlayerJoined(VRCPlayerApi player)
        {
            // Only load data for the local player
            if (!player.isLocal)
                return;

            LoadPlayerData();
        }

        private void Update()
        {
            if (!_isInitialized)
                return;

            // Update playtime
            _saveData.playtime += Time.deltaTime;

            // Auto-save periodically
            if (Time.time - _lastSaveTime > autoSaveInterval)
            {
                AutoSave();
            }
        }

        #endregion

        #region Registration

        /// <summary>
        /// Registers the converter functions for PlayerSaveData.
        ///
        /// In Phase 2, converters must be manually defined.
        /// Future phases may support automatic generation from attributes.
        /// </summary>
        private void RegisterConverter()
        {
            CEPersistence.Register<PlayerSaveData>(
                toData: ToDataDictionary,
                fromData: FromDataDictionary,
                key: "rpg_save_v1",
                version: 1,
                validate: ValidateSaveData
            );

            CELogger.Debug("Persistence", "Registered PlayerSaveData converter");
        }

        /// <summary>
        /// Converts PlayerSaveData to DataDictionary for storage.
        /// </summary>
        private DataDictionary ToDataDictionary(PlayerSaveData data)
        {
            var dict = new DataDictionary();

            dict["xp"] = data.experience;
            dict["lvl"] = data.level;
            dict["name"] = data.displayName ?? "";
            dict["gold"] = data.gold;
            dict["time"] = data.playtime;
            dict["quests"] = data.questFlags;
            dict["inv_count"] = data.inventoryCount;

            // Serialize position as nested dictionary
            var posDict = new DataDictionary();
            posDict["x"] = data.lastPosition.x;
            posDict["y"] = data.lastPosition.y;
            posDict["z"] = data.lastPosition.z;
            dict["pos"] = posDict;

            // Serialize inventory arrays
            var invList = new DataList();
            var qtyList = new DataList();
            for (int i = 0; i < data.inventoryCount && i < data.inventory.Length; i++)
            {
                invList.Add(data.inventory[i]);
                qtyList.Add(data.quantities[i]);
            }
            dict["inv"] = invList;
            dict["qty"] = qtyList;

            return dict;
        }

        /// <summary>
        /// Converts DataDictionary back to PlayerSaveData.
        /// </summary>
        private PlayerSaveData FromDataDictionary(DataDictionary dict)
        {
            var data = new PlayerSaveData();

            // Note: JSON numbers are doubles, need to cast
            if (dict.TryGetValue("xp", out DataToken xp))
                data.experience = (int)xp.Double;

            if (dict.TryGetValue("lvl", out DataToken lvl))
                data.level = Mathf.Clamp((int)lvl.Double, 1, 100);
            else
                data.level = 1;

            if (dict.TryGetValue("name", out DataToken name))
                data.displayName = name.String;

            if (dict.TryGetValue("gold", out DataToken gold))
                data.gold = (int)gold.Double;

            if (dict.TryGetValue("time", out DataToken time))
                data.playtime = (float)time.Double;

            if (dict.TryGetValue("quests", out DataToken quests))
                data.questFlags = (int)quests.Double;

            // Deserialize position
            if (dict.TryGetValue("pos", out DataToken posToken) &&
                posToken.TokenType == TokenType.DataDictionary)
            {
                var posDict = posToken.DataDictionary;
                float x = 0, y = 0, z = 0;

                if (posDict.TryGetValue("x", out DataToken px)) x = (float)px.Double;
                if (posDict.TryGetValue("y", out DataToken py)) y = (float)py.Double;
                if (posDict.TryGetValue("z", out DataToken pz)) z = (float)pz.Double;

                data.lastPosition = new Vector3(x, y, z);
            }

            // Deserialize inventory
            if (dict.TryGetValue("inv", out DataToken invToken) &&
                invToken.TokenType == TokenType.DataList &&
                dict.TryGetValue("qty", out DataToken qtyToken) &&
                qtyToken.TokenType == TokenType.DataList)
            {
                var invList = invToken.DataList;
                var qtyList = qtyToken.DataList;

                if (dict.TryGetValue("inv_count", out DataToken count))
                    data.inventoryCount = Mathf.Min((int)count.Double, data.inventory.Length);

                for (int i = 0; i < data.inventoryCount && i < invList.Count; i++)
                {
                    if (invList.TryGetValue(i, out DataToken inv))
                        data.inventory[i] = (int)inv.Double;
                    if (qtyList.TryGetValue(i, out DataToken qty))
                        data.quantities[i] = (int)qty.Double;
                }
            }

            return data;
        }

        /// <summary>
        /// Validates save data before storing.
        /// </summary>
        private System.Collections.Generic.List<ValidationError> ValidateSaveData(PlayerSaveData data)
        {
            var errors = new System.Collections.Generic.List<ValidationError>();

            // Validate level range
            if (data.level < 1 || data.level > 100)
            {
                errors.Add(new ValidationError("level", "lvl",
                    $"Level must be between 1 and 100, was {data.level}",
                    data.level));
            }

            // Validate gold range
            if (data.gold < 0 || data.gold > 999999)
            {
                errors.Add(new ValidationError("gold", "gold",
                    $"Gold must be between 0 and 999999, was {data.gold}",
                    data.gold));
            }

            // Validate display name length
            if (data.displayName != null && data.displayName.Length > 32)
            {
                errors.Add(new ValidationError("displayName", "name",
                    $"Display name exceeds 32 characters (was {data.displayName.Length})",
                    data.displayName));
            }

            // Validate inventory count
            if (data.inventoryCount < 0 || data.inventoryCount > data.inventory.Length)
            {
                errors.Add(new ValidationError("inventoryCount", "inv_count",
                    $"Invalid inventory count: {data.inventoryCount}",
                    data.inventoryCount));
            }

            return errors;
        }

        #endregion

        #region Load/Save Operations

        /// <summary>
        /// Loads the player's saved data.
        /// </summary>
        public void LoadPlayerData()
        {
            CELogger.Info("Loading player data...");
            UpdateStatus("Loading...");

            RestoreResult result = CEPersistence.Restore(out _saveData);

            switch (result)
            {
                case RestoreResult.Success:
                    CELogger.Info($"Player data loaded successfully. Level: {_saveData.level}, XP: {_saveData.experience}");
                    UpdateStatus("Data loaded!");
                    break;

                case RestoreResult.NoData:
                    CELogger.Info("No existing save data. Creating new save...");
                    _saveData = CreateNewSaveData();
                    UpdateStatus("New save created");
                    break;

                case RestoreResult.VersionMismatch:
                    CELogger.Warning("Persistence", "Save data version mismatch. Creating new save...");
                    _saveData = CreateNewSaveData();
                    UpdateStatus("Version mismatch - reset");
                    break;

                case RestoreResult.ParseError:
                    CELogger.Error("Failed to parse save data. Creating new save...");
                    _saveData = CreateNewSaveData();
                    UpdateStatus("Parse error - reset");
                    break;

                case RestoreResult.NotReady:
                    CELogger.Warning("Persistence", "Player data not ready. Will retry...");
                    UpdateStatus("Not ready...");
                    SendCustomEventDelayedSeconds(nameof(LoadPlayerData), 1f);
                    return;

                default:
                    CELogger.Error($"Unknown restore result: {result}");
                    _saveData = CreateNewSaveData();
                    UpdateStatus("Error - reset");
                    break;
            }

            _isInitialized = true;
            _lastSaveTime = Time.time;
            UpdateStatsDisplay();
        }

        /// <summary>
        /// Saves the current player data.
        /// </summary>
        public void SavePlayerData()
        {
            if (_saveData == null)
            {
                CELogger.Error("Cannot save - no save data loaded");
                return;
            }

            // Update position before saving
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer != null && localPlayer.IsValid())
            {
                _saveData.lastPosition = localPlayer.GetPosition();
            }

            // Estimate size before saving
            int estimatedSize = CEPersistence.EstimateSize(_saveData);
            CELogger.Debug("Persistence", $"Estimated save size: {SizeEstimator.FormatSize(estimatedSize)} ({SizeEstimator.GetQuotaPercentage(estimatedSize):F1}% of quota)");

            SaveResult result = CEPersistence.Save(_saveData);

            switch (result)
            {
                case SaveResult.Success:
                    CELogger.Info("Player data saved successfully");
                    UpdateStatus("Saved!");
                    _lastSaveTime = Time.time;
                    break;

                case SaveResult.ValidationFailed:
                    CELogger.Error("Save failed: validation error");
                    UpdateStatus("Save failed - validation");
                    break;

                case SaveResult.QuotaExceeded:
                    CELogger.Error("Save failed: quota exceeded");
                    UpdateStatus("Save failed - quota");
                    break;

                default:
                    CELogger.Error($"Save failed: {result}");
                    UpdateStatus($"Save failed: {result}");
                    break;
            }
        }

        /// <summary>
        /// Auto-save handler.
        /// </summary>
        private void AutoSave()
        {
            CELogger.Debug("Persistence", "Auto-saving...");
            SavePlayerData();
        }

        /// <summary>
        /// Creates a new save data with default values.
        /// </summary>
        private PlayerSaveData CreateNewSaveData()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;

            var data = new PlayerSaveData
            {
                experience = 0,
                level = 1,
                displayName = localPlayer != null ? localPlayer.displayName : "Player",
                lastPosition = localPlayer != null ? localPlayer.GetPosition() : Vector3.zero,
                inventory = new int[100],
                quantities = new int[100],
                inventoryCount = 0,
                questFlags = 0,
                gold = 100, // Starting gold
                playtime = 0
            };

            return data;
        }

        #endregion

        #region Game Logic Examples

        /// <summary>
        /// Adds experience points and handles level-ups.
        /// </summary>
        public void AddExperience(int amount)
        {
            if (_saveData == null) return;

            _saveData.experience += amount;

            // Simple level-up formula
            int xpForNextLevel = _saveData.level * 100;
            while (_saveData.experience >= xpForNextLevel && _saveData.level < 100)
            {
                _saveData.experience -= xpForNextLevel;
                _saveData.level++;
                CELogger.Info($"Level up! Now level {_saveData.level}");
                xpForNextLevel = _saveData.level * 100;
            }

            UpdateStatsDisplay();
        }

        /// <summary>
        /// Adds an item to the inventory.
        /// </summary>
        public bool AddItem(int itemId, int quantity)
        {
            if (_saveData == null) return false;

            // Check for existing stack
            for (int i = 0; i < _saveData.inventoryCount; i++)
            {
                if (_saveData.inventory[i] == itemId)
                {
                    _saveData.quantities[i] += quantity;
                    CELogger.Debug("Persistence", $"Added {quantity}x item {itemId} to existing stack");
                    return true;
                }
            }

            // Add new stack
            if (_saveData.inventoryCount < _saveData.inventory.Length)
            {
                _saveData.inventory[_saveData.inventoryCount] = itemId;
                _saveData.quantities[_saveData.inventoryCount] = quantity;
                _saveData.inventoryCount++;
                CELogger.Debug("Persistence", $"Added new item {itemId} x{quantity}");
                return true;
            }

            CELogger.Warning("Persistence", "Inventory full!");
            return false;
        }

        /// <summary>
        /// Adds gold to the player.
        /// </summary>
        public void AddGold(int amount)
        {
            if (_saveData == null) return;

            _saveData.gold = Mathf.Clamp(_saveData.gold + amount, 0, 999999);
            UpdateStatsDisplay();
        }

        /// <summary>
        /// Sets a quest flag.
        /// </summary>
        public void SetQuestFlag(int questIndex)
        {
            if (_saveData == null || questIndex < 0 || questIndex >= 32) return;

            _saveData.questFlags |= (1 << questIndex);
            CELogger.Debug("Persistence", $"Quest {questIndex} completed");
        }

        /// <summary>
        /// Checks if a quest flag is set.
        /// </summary>
        public bool HasQuestFlag(int questIndex)
        {
            if (_saveData == null || questIndex < 0 || questIndex >= 32) return false;
            return (_saveData.questFlags & (1 << questIndex)) != 0;
        }

        #endregion

        #region UI

        private void UpdateStatus(string message)
        {
            if (statusText != null)
            {
                statusText.text = message;
            }
        }

        private void UpdateStatsDisplay()
        {
            if (statsText == null || _saveData == null) return;

            string stats = $"Level: {_saveData.level}\n" +
                          $"XP: {_saveData.experience}/{_saveData.level * 100}\n" +
                          $"Gold: {_saveData.gold}\n" +
                          $"Items: {_saveData.inventoryCount}\n" +
                          $"Playtime: {FormatPlaytime(_saveData.playtime)}";

            statsText.text = stats;
        }

        private string FormatPlaytime(float seconds)
        {
            int hours = (int)(seconds / 3600);
            int minutes = (int)((seconds % 3600) / 60);
            return $"{hours}h {minutes}m";
        }

        #endregion

        #region Debug UI Buttons

        /// <summary>
        /// Button handler: Add 50 XP
        /// </summary>
        public void OnAddXPClicked()
        {
            AddExperience(50);
        }

        /// <summary>
        /// Button handler: Add 100 Gold
        /// </summary>
        public void OnAddGoldClicked()
        {
            AddGold(100);
        }

        /// <summary>
        /// Button handler: Add Random Item
        /// </summary>
        public void OnAddItemClicked()
        {
            int randomItemId = UnityEngine.Random.Range(1, 100);
            AddItem(randomItemId, 1);
            UpdateStatsDisplay();
        }

        /// <summary>
        /// Button handler: Manual Save
        /// </summary>
        public void OnSaveClicked()
        {
            SavePlayerData();
        }

        /// <summary>
        /// Button handler: Manual Load
        /// </summary>
        public void OnLoadClicked()
        {
            LoadPlayerData();
        }

        /// <summary>
        /// Button handler: Show Size Estimate
        /// </summary>
        public void OnShowSizeClicked()
        {
            if (_saveData == null) return;

            int size = CEPersistence.EstimateSize(_saveData);
            float percent = SizeEstimator.GetQuotaPercentage(size);

            CELogger.Info($"Current save size: {SizeEstimator.FormatSize(size)} ({percent:F1}% of quota)");
            UpdateStatus($"Size: {SizeEstimator.FormatSize(size)}");
        }

        #endregion
    }
}
