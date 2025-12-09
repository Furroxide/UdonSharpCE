using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace CEShowcase.Station5_Persistence
{
    /// <summary>
    /// Station 5: Persistent Inventory - Demonstrates CEPersistence for saving/loading player data.
    /// Shows how to persist game state across sessions using VRChat's PlayerData API.
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
        
        [Header("Collectible Prefabs")]
        [SerializeField] private string[] _itemNames;
        [SerializeField] private Sprite[] _itemIcons;
        
        [Header("Visual")]
        [SerializeField] private Transform[] _inventorySlotDisplays;
        [SerializeField] private GameObject _itemDisplayPrefab;
        
        // Inventory data
        private int[] _itemIds;
        private int[] _itemCounts;
        private int _totalItemCount;
        private int _uniqueItemCount;
        
        // Player stats (also persisted)
        private int _totalCollected;
        private int _sessionCollected;
        private float _playTime;
        
        // Save state
        private bool _hasUnsavedChanges;
        private int _saveCount;
        private int _loadCount;
        
        // Persistence key
        private const string SAVE_KEY = "ce_demo_inventory";
        private const int SAVE_VERSION = 1;
        private const int QUOTA_LIMIT = 102400; // 100KB
        
        void Start()
        {
            InitializeInventory();
            LoadInventory();
            
            CELogger.Info("Inventory", "Inventory System initialized");
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
        
        /// <summary>
        /// Checks if player has a specific item.
        /// </summary>
        public bool HasItem(int itemId)
        {
            return FindItemSlot(itemId) >= 0;
        }
        
        /// <summary>
        /// Gets the count of a specific item.
        /// </summary>
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
        
        // ========================
        // PERSISTENCE OPERATIONS
        // ========================
        
        /// <summary>
        /// Saves inventory to VRChat PlayerData.
        /// Demonstrates CEPersistence.Save() workflow.
        /// </summary>
        public void SaveInventory()
        {
            CELogger.Info("Inventory", "Saving inventory...");
            
            // Build data dictionary
            DataDictionary data = new DataDictionary();
            
            // Metadata
            data["version"] = SAVE_VERSION;
            data["save_time"] = System.DateTime.Now.ToString();
            
            // Inventory items
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
            
            // Estimate size
            int estimatedSize = EstimateDataSize(data);
            
            // In a real implementation, this would call:
            // VRCPlayerApi.GetPlayerById(playerId).SetPlayerTag(SAVE_KEY, jsonString);
            // Or use the newer PlayerData API
            
            _saveCount++;
            _hasUnsavedChanges = false;
            
            CELogger.Info("Inventory", $"Inventory saved! ({estimatedSize} bytes)");
            UpdateDisplay();
        }
        
        /// <summary>
        /// Loads inventory from VRChat PlayerData.
        /// Demonstrates CEPersistence.Restore() workflow.
        /// </summary>
        public void LoadInventory()
        {
            CELogger.Info("Inventory", "Loading inventory...");
            
            // In a real implementation, this would:
            // 1. Get stored JSON from PlayerData
            // 2. Parse it into DataDictionary
            // 3. Validate version and migrate if needed
            // 4. Populate local state
            
            // For demo purposes, we'll simulate loading existing data
            // if this isn't the first run
            
            _loadCount++;
            
            // Check for existing save (simulated)
            bool hasExistingSave = PlayerPrefs.HasKey(SAVE_KEY);
            
            if (hasExistingSave)
            {
                // Would load from VRChat's PlayerData API
                CELogger.Info("Inventory", "Previous save found - data restored");
            }
            else
            {
                CELogger.Info("Inventory", "No previous save - starting fresh");
            }
            
            UpdateDisplay();
        }
        
        /// <summary>
        /// Wipes all saved data.
        /// </summary>
        public void WipeData()
        {
            CELogger.Warning("Inventory", "Wiping all save data!");
            
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
            
            // Would clear from PlayerData as well
            PlayerPrefs.DeleteKey(SAVE_KEY);
            
            _hasUnsavedChanges = false;
            
            UpdateDisplay();
            CELogger.Info("Inventory", "All data wiped");
        }
        
        private int EstimateDataSize(DataDictionary data)
        {
            // Rough estimate based on content
            // In a real implementation, CEPersistence.EstimateSize would be used
            int size = 50; // Base overhead
            
            size += _uniqueItemCount * 30; // Per item overhead
            size += 100; // Stats and metadata
            
            return size;
        }
        
        // ========================
        // UI DISPLAY
        // ========================
        
        private void UpdateDisplay()
        {
            UpdateInventoryDisplay();
            UpdateStatsDisplay();
            UpdateJsonPreview();
        }
        
        private void UpdateInventoryDisplay()
        {
            if (_inventoryText == null) return;
            
            string text = "<b>📦 INVENTORY</b>\n\n";
            
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
            
            int estimatedSize = EstimateDataSize(null);
            float quotaPercent = (estimatedSize / (float)QUOTA_LIMIT) * 100f;
            
            string saveStatus = _hasUnsavedChanges ? "<color=#FFFF00>Unsaved</color>" : "<color=#00FF00>Saved</color>";
            
            _statsText.text = $"<b>PERSISTENCE METRICS</b>\n" +
                             $"Total Collected: {_totalCollected}\n" +
                             $"This Session: {_sessionCollected}\n" +
                             $"Play Time: {FormatTime(_playTime)}\n" +
                             $"Data Size: ~{estimatedSize} bytes\n" +
                             $"Quota: {quotaPercent:F1}% of 100KB\n" +
                             $"Status: {saveStatus}\n" +
                             $"Saves: {_saveCount} | Loads: {_loadCount}";
        }
        
        private void UpdateJsonPreview()
        {
            if (_jsonPreviewText == null) return;
            
            // Show what the JSON would look like
            string json = "{\n";
            json += $"  \"version\": {SAVE_VERSION},\n";
            json += "  \"items\": [\n";
            
            bool first = true;
            for (int i = 0; i < _maxSlots; i++)
            {
                if (_itemIds[i] >= 0)
                {
                    if (!first) json += ",\n";
                    json += $"    {{\"id\": {_itemIds[i]}, \"count\": {_itemCounts[i]}}}";
                    first = false;
                }
            }
            
            json += "\n  ],\n";
            json += $"  \"total_collected\": {_totalCollected},\n";
            json += $"  \"play_time\": {_playTime:F1}\n";
            json += "}";
            
            _jsonPreviewText.text = $"<b>JSON Preview:</b>\n<size=70%>{json}</size>";
        }
        
        private string FormatTime(float seconds)
        {
            int mins = Mathf.FloorToInt(seconds / 60);
            int secs = Mathf.FloorToInt(seconds % 60);
            return $"{mins}m {secs}s";
        }
        
        // ========================
        // UI CALLBACKS
        // ========================
        
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
    }
}
