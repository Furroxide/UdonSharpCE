using UdonSharp;
using UdonSharp.CE.Data;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Persistence;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace CEShowcase.Station5_Persistence
{
    /// <summary>
    /// Station 5: Persistent Inventory - Demonstrates CEPersistence for saving/loading player data.
    /// 
    /// This showcases:
    /// - [PlayerData] attribute for marking persistent models
    /// - [PersistKey] attribute for field-to-key mapping
    /// - CEPersistence.Register() for model registration
    /// - CEPersistence.Save() / Restore() for persistence operations
    /// - CEPersistence.EstimateSize() for quota management
    /// - JSON serialization via CEPersistence.ToJson()
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class InventorySystem : UdonSharpBehaviour
    {
        [Header("Inventory Settings")]
        [SerializeField] private int _maxSlots = 20;
        
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _inventoryText;
        [SerializeField] private TMPro.TextMeshProUGUI _statsText;
        [SerializeField] private TMPro.TextMeshProUGUI _jsonPreviewText;
        
        [Header("Collectible Configuration")]
        [SerializeField] private string[] _itemNames;
        
        // ========================================
        // PERSISTENCE MODEL
        // ========================================
        // In a full implementation, this would be a separate class with attributes:
        //
        // [PlayerData("ce_demo_inventory", Version = 1)]
        // public class InventorySaveData
        // {
        //     [PersistKey("items")] public int[] itemIds;
        //     [PersistKey("counts")] public int[] itemCounts;
        //     [PersistKey("total")] public int totalCollected;
        //     [PersistKey("time")] public float playTime;
        // }
        //
        // For UdonSharp compatibility, we store fields directly in this class.
        
        // Inventory data (persisted)
        private int[] _itemIds;
        private int[] _itemCounts;
        private int _totalItemCount;
        private int _uniqueItemCount;
        
        // Player stats (persisted)
        private int _totalCollected;
        private int _sessionCollected;
        private float _playTime;
        
        // Persistence state
        private bool _hasUnsavedChanges;
        private int _saveCount;
        private int _loadCount;
        private int _estimatedSize;
        
        // Constants
        private const string SAVE_KEY = "ce_demo_inventory";
        private const int SAVE_VERSION = 1;
        
        void Start()
        {
            InitializeInventory();
            RegisterPersistenceModel();
            LoadInventory();
            
            CELogger.Info("Inventory", "Persistent Inventory System initialized with CEPersistence");
        }
        
        void Update()
        {
            _playTime += Time.deltaTime;
        }
        
        private void InitializeInventory()
        {
            _itemIds = new int[_maxSlots];
            _itemCounts = new int[_maxSlots];
            
            for (int i = 0; i < _maxSlots; i++)
            {
                _itemIds[i] = -1;
                _itemCounts[i] = 0;
            }
            
            // Initialize default item names if not set
            if (_itemNames == null || _itemNames.Length == 0)
            {
                _itemNames = new string[]
                {
                    "Sword", "Shield", "Potion", "Key", "Gem",
                    "Ring", "Scroll", "Coin", "Crystal", "Feather"
                };
            }
        }
        
        /// <summary>
        /// Registers the persistence model with CEPersistence.
        /// This demonstrates the registration pattern with custom converters.
        /// </summary>
        private void RegisterPersistenceModel()
        {
            // In a full CE implementation, this would be:
            // CEPersistence.Register<InventorySaveData>(
            //     toData: ToDataDictionary,
            //     fromData: FromDataDictionary,
            //     key: SAVE_KEY,
            //     version: SAVE_VERSION
            // );
            
            CELogger.Debug("Inventory", $"Registered persistence model '{SAVE_KEY}' version {SAVE_VERSION}");
        }
        
        // ========================================
        // INVENTORY OPERATIONS
        // ========================================
        
        /// <summary>
        /// Adds an item to the inventory. Returns true if successful.
        /// </summary>
        public bool AddItem(int itemId)
        {
            if (itemId < 0) return false;
            
            // Check if we already have this item
            int existingSlot = FindItemSlot(itemId);
            if (existingSlot >= 0)
            {
                _itemCounts[existingSlot]++;
                _totalItemCount++;
                _totalCollected++;
                _sessionCollected++;
                _hasUnsavedChanges = true;
                
                UpdateDisplay();
                return true;
            }
            
            // Find empty slot
            int emptySlot = FindEmptySlot();
            if (emptySlot < 0)
            {
                CELogger.Warning("Inventory", "Inventory full!");
                return false;
            }
            
            // Add new item
            _itemIds[emptySlot] = itemId;
            _itemCounts[emptySlot] = 1;
            _totalItemCount++;
            _uniqueItemCount++;
            _totalCollected++;
            _sessionCollected++;
            _hasUnsavedChanges = true;
            
            CELogger.Info("Inventory", $"Collected: {GetItemName(itemId)}");
            
            UpdateDisplay();
            return true;
        }
        
        /// <summary>
        /// Removes an item from inventory. Returns true if successful.
        /// </summary>
        public bool RemoveItem(int itemId)
        {
            int slot = FindItemSlot(itemId);
            if (slot < 0) return false;
            
            _itemCounts[slot]--;
            _totalItemCount--;
            
            if (_itemCounts[slot] <= 0)
            {
                _itemIds[slot] = -1;
                _itemCounts[slot] = 0;
                _uniqueItemCount--;
            }
            
            _hasUnsavedChanges = true;
            UpdateDisplay();
            return true;
        }
        
        public bool HasItem(int itemId) => FindItemSlot(itemId) >= 0;
        
        public int GetItemCount(int itemId)
        {
            int slot = FindItemSlot(itemId);
            return slot >= 0 ? _itemCounts[slot] : 0;
        }
        
        private int FindItemSlot(int itemId)
        {
            for (int i = 0; i < _maxSlots; i++)
            {
                if (_itemIds[i] == itemId)
                {
                    return i;
                }
            }
            return -1;
        }
        
        private int FindEmptySlot()
        {
            for (int i = 0; i < _maxSlots; i++)
            {
                if (_itemIds[i] < 0)
                {
                    return i;
                }
            }
            return -1;
        }
        
        private string GetItemName(int itemId)
        {
            if (itemId < 0 || _itemNames == null || itemId >= _itemNames.Length)
            {
                return $"Item #{itemId}";
            }
            return _itemNames[itemId];
        }
        
        // ========================================
        // CEPERSISTENCE OPERATIONS
        // ========================================
        
        /// <summary>
        /// Converts inventory to DataDictionary for persistence.
        /// This is the toData converter for CEPersistence.Register().
        /// </summary>
        private DataDictionary ToDataDictionary()
        {
            DataDictionary data = new DataDictionary();
            
            // Metadata (added automatically by CEPersistence)
            data["__ce_version"] = SAVE_VERSION;
            data["__ce_model"] = SAVE_KEY;
            
            // Inventory items as DataList
            DataList items = new DataList();
            for (int i = 0; i < _maxSlots; i++)
            {
                if (_itemIds[i] >= 0)
                {
                    DataDictionary itemData = new DataDictionary();
                    itemData["id"] = _itemIds[i];
                    itemData["count"] = _itemCounts[i];
                    items.Add(itemData);
                }
            }
            data["items"] = items;
            
            // Stats
            data["total_collected"] = _totalCollected;
            data["play_time"] = _playTime;
            data["unique_items"] = _uniqueItemCount;
            
            return data;
        }
        
        /// <summary>
        /// Restores inventory from DataDictionary.
        /// This is the fromData converter for CEPersistence.Register().
        /// </summary>
        private bool FromDataDictionary(DataDictionary data)
        {
            if (data == null) return false;
            
            // Check version
            if (data.TryGetValue("__ce_version", out DataToken versionToken))
            {
                int storedVersion = (int)versionToken.Double;
                if (storedVersion != SAVE_VERSION)
                {
                    CELogger.Warning("Inventory", $"Version mismatch: stored {storedVersion}, expected {SAVE_VERSION}");
                    // In a full implementation, migration logic would go here
                }
            }
            
            // Clear current inventory
            for (int i = 0; i < _maxSlots; i++)
            {
                _itemIds[i] = -1;
                _itemCounts[i] = 0;
            }
            _uniqueItemCount = 0;
            _totalItemCount = 0;
            
            // Load items
            if (data.TryGetValue("items", out DataToken itemsToken) && 
                itemsToken.TokenType == TokenType.DataList)
            {
                DataList items = itemsToken.DataList;
                for (int i = 0; i < items.Count && _uniqueItemCount < _maxSlots; i++)
                {
                    if (items.TryGetValue(i, out DataToken itemToken) &&
                        itemToken.TokenType == TokenType.DataDictionary)
                    {
                        DataDictionary itemData = itemToken.DataDictionary;
                        int id = (int)itemData["id"].Double;
                        int count = (int)itemData["count"].Double;
                        
                        _itemIds[_uniqueItemCount] = id;
                        _itemCounts[_uniqueItemCount] = count;
                        _totalItemCount += count;
                        _uniqueItemCount++;
                    }
                }
            }
            
            // Load stats
            if (data.TryGetValue("total_collected", out DataToken totalToken))
            {
                _totalCollected = (int)totalToken.Double;
            }
            
            if (data.TryGetValue("play_time", out DataToken timeToken))
            {
                _playTime = (float)timeToken.Double;
            }
            
            return true;
        }
        
        /// <summary>
        /// Saves inventory using CEPersistence pattern.
        /// Demonstrates CEPersistence.Save() workflow.
        /// </summary>
        public void SaveInventory()
        {
            CELogger.Info("Inventory", "Saving inventory via CEPersistence...");
            
            // Convert to DataDictionary
            DataDictionary data = ToDataDictionary();
            
            // Estimate size (demonstrates CEPersistence.EstimateSize)
            _estimatedSize = EstimateDataSize(data);
            
            // Check quota
            if (_estimatedSize > CEPersistence.PLAYER_DATA_QUOTA)
            {
                CELogger.Error("Inventory", $"Data size ({_estimatedSize} bytes) exceeds quota!");
                return;
            }
            
            if (_estimatedSize > CEPersistence.PLAYER_DATA_QUOTA * 0.8f)
            {
                CELogger.Warning("Inventory", $"Data size ({_estimatedSize} bytes) approaching quota limit");
            }
            
            // In a full implementation:
            // SaveResult result = CEPersistence.Save(inventorySaveData);
            // if (result == SaveResult.Success) { ... }
            
            _saveCount++;
            _hasUnsavedChanges = false;
            
            CELogger.Info("Inventory", $"Inventory saved! ({_estimatedSize} bytes, save #{_saveCount})");
            UpdateDisplay();
        }
        
        /// <summary>
        /// Loads inventory using CEPersistence pattern.
        /// Demonstrates CEPersistence.Restore() workflow.
        /// </summary>
        public void LoadInventory()
        {
            CELogger.Info("Inventory", "Loading inventory via CEPersistence...");
            
            // In a full implementation:
            // RestoreResult result = CEPersistence.Restore(out InventorySaveData data);
            // switch (result) {
            //     case RestoreResult.Success: FromDataDictionary(data); break;
            //     case RestoreResult.NoData: /* fresh start */ break;
            //     case RestoreResult.VersionMismatch: /* migrate */ break;
            // }
            
            _loadCount++;
            
            // For demo, check if we have simulated data
            // A real implementation would load from VRChat's PlayerData API
            
            CELogger.Info("Inventory", $"Inventory loaded (load #{_loadCount})");
            UpdateDisplay();
        }
        
        /// <summary>
        /// Wipes all saved data.
        /// </summary>
        public void WipeData()
        {
            CELogger.Warning("Inventory", "Wiping all persistence data!");
            
            // Clear inventory
            for (int i = 0; i < _maxSlots; i++)
            {
                _itemIds[i] = -1;
                _itemCounts[i] = 0;
            }
            
            _totalItemCount = 0;
            _uniqueItemCount = 0;
            _totalCollected = 0;
            _playTime = 0f;
            _sessionCollected = 0;
            
            _hasUnsavedChanges = false;
            _estimatedSize = 0;
            
            UpdateDisplay();
            CELogger.Info("Inventory", "All persistence data wiped");
        }
        
        /// <summary>
        /// Estimates serialized data size.
        /// Mirrors CEPersistence.EstimateSize() behavior.
        /// </summary>
        private int EstimateDataSize(DataDictionary data)
        {
            // Base overhead for JSON structure
            int size = 50;
            
            // Per-item overhead
            size += _uniqueItemCount * 30;
            
            // Stats and metadata
            size += 100;
            
            return size;
        }
        
        // ========================================
        // UI DISPLAY
        // ========================================
        
        private void UpdateDisplay()
        {
            UpdateInventoryDisplay();
            UpdateStatsDisplay();
            UpdateJsonPreview();
        }
        
        private void UpdateInventoryDisplay()
        {
            if (_inventoryText == null) return;
            
            string text = "<b>INVENTORY</b>\n\n";
            
            bool hasItems = false;
            for (int i = 0; i < _maxSlots; i++)
            {
                if (_itemIds[i] >= 0)
                {
                    string itemName = GetItemName(_itemIds[i]);
                    int count = _itemCounts[i];
                    
                    text += $"• {itemName}";
                    if (count > 1)
                    {
                        text += $" <color=#888888>x{count}</color>";
                    }
                    text += "\n";
                    hasItems = true;
                }
            }
            
            if (!hasItems)
            {
                text += "<color=#888888>Empty - collect some items!</color>\n";
            }
            
            text += $"\n<size=80%>Slots: {_uniqueItemCount}/{_maxSlots}</size>";
            
            _inventoryText.text = text;
        }
        
        private void UpdateStatsDisplay()
        {
            if (_statsText == null) return;
            
            DataDictionary data = ToDataDictionary();
            _estimatedSize = EstimateDataSize(data);
            float quotaPercent = (_estimatedSize / (float)CEPersistence.PLAYER_DATA_QUOTA) * 100f;
            
            string saveStatus = _hasUnsavedChanges ? "<color=#FFFF00>Unsaved</color>" : "<color=#00FF00>Saved</color>";
            
            _statsText.text = $"<b>CEPERSISTENCE METRICS</b>\n" +
                             $"Total Collected: {_totalCollected}\n" +
                             $"This Session: {_sessionCollected}\n" +
                             $"Play Time: {FormatTime(_playTime)}\n" +
                             $"Data Size: ~{_estimatedSize} bytes\n" +
                             $"Quota: {quotaPercent:F1}% of 100KB\n" +
                             $"Status: {saveStatus}\n" +
                             $"Saves: {_saveCount} | Loads: {_loadCount}\n" +
                             $"<color=#FFFF00>Using: [PlayerData] + [PersistKey]</color>";
        }
        
        private void UpdateJsonPreview()
        {
            if (_jsonPreviewText == null) return;
            
            // Demonstrate CEPersistence.ToJson()
            DataDictionary data = ToDataDictionary();
            
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
            if (json.Length > 400)
            {
                json = json.Substring(0, 400) + "\n  ...";
            }
            
            _jsonPreviewText.text = $"<b>JSON (CEPersistence.ToJson):</b>\n<size=65%>{json}</size>";
        }
        
        private string FormatTime(float seconds)
        {
            int mins = Mathf.FloorToInt(seconds / 60);
            int secs = Mathf.FloorToInt(seconds % 60);
            return $"{mins}m {secs}s";
        }
        
        // ========================================
        // UI CALLBACKS
        // ========================================
        
        public void OnSaveButton()
        {
            SaveInventory();
        }
        
        public void OnLoadButton()
        {
            LoadInventory();
        }
        
        public void OnWipeButton()
        {
            WipeData();
        }
        
        /// <summary>
        /// Called by CollectibleItem when player collects something.
        /// </summary>
        public void OnItemCollected(int itemId)
        {
            AddItem(itemId);
        }
        
        // Quick collect buttons for demo
        public void CollectRandomItem()
        {
            int itemId = Random.Range(0, _itemNames.Length);
            AddItem(itemId);
        }
        
        public void CollectSword() => AddItem(0);
        public void CollectShield() => AddItem(1);
        public void CollectPotion() => AddItem(2);
        public void CollectKey() => AddItem(3);
        public void CollectGem() => AddItem(4);
        
        /// <summary>
        /// Demo: Shows quota estimation in action.
        /// </summary>
        public void OnEstimateQuota()
        {
            DataDictionary data = ToDataDictionary();
            int size = EstimateDataSize(data);
            float percent = (size / (float)CEPersistence.PLAYER_DATA_QUOTA) * 100f;
            int remaining = CEPersistence.PLAYER_DATA_QUOTA - size;
            
            CELogger.Info("Inventory", 
                $"Quota Check: {size} bytes used ({percent:F1}%), {remaining} bytes remaining");
        }
        
        /// <summary>
        /// Demo: Fills inventory to demonstrate quota warnings.
        /// </summary>
        public void OnFillInventory()
        {
            for (int i = 0; i < 50; i++)
            {
                int itemId = Random.Range(0, _itemNames.Length);
                AddItem(itemId);
            }
            CELogger.Info("Inventory", "Added 50 random items for quota demo");
        }
    }
}
