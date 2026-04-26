using UdonSharp;
using UdonSharp.CE.Data;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Persistence;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace CEShowcase.Station5_Persistence
{
    /// <summary>
    /// Station 5: Treasure Hunt - An interactive demo of CEPersistence.
    /// 
    /// This showcases:
    /// - AUTOMATIC persistence (no save button needed!)
    /// - Visual feedback with rarity tiers and achievements
    /// - Collection progress and goals
    /// - Real-time quota monitoring
    /// 
    /// The system saves automatically whenever you collect an item.
    /// Come back later and your progress is still there!
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class InventorySystem : UdonSharpBehaviour
    {
        [Header("Inventory Settings")]
        [SerializeField] private int _maxSlots = 20;
        
        [Header("Main UI")]
        [SerializeField] private TMPro.TMP_Text _titleText;
        [SerializeField] private TMPro.TMP_Text _progressText;
        [SerializeField] private TMPro.TMP_Text _statsText;
        [SerializeField] private TMPro.TMP_Text _recentActivityText;
        [SerializeField] private Slider _progressBar;
        
        [Header("Visual Inventory Grid")]
        [SerializeField] private Image[] _inventorySlotImages;
        [SerializeField] private TMPro.TMP_Text[] _inventorySlotCounts;
        [SerializeField] private Sprite[] _itemSprites;  // Icons for each item type
        [SerializeField] private Sprite _emptySlotSprite;
        
        [Header("Achievement Display")]
        [SerializeField] private TMPro.TMP_Text _achievementText;
        [SerializeField] private GameObject _achievementPopup;
        [SerializeField] private TMPro.TMP_Text _achievementPopupText;
        [SerializeField] private AudioSource _achievementSound;
        
        [Header("Collection Feedback")]
        [SerializeField] private AudioSource _collectSound;
        [SerializeField] private AudioSource _rareCollectSound;
        [SerializeField] private AudioSource _legendaryCollectSound;
        [SerializeField] private ParticleSystem _collectParticles;
        [SerializeField] private ParticleSystem _rareParticles;
        [SerializeField] private ParticleSystem _legendaryParticles;
        
        [Header("Treasure Configuration")]
        [SerializeField] private string[] _itemNames = new string[]
        {
            "Gold Coin", "Silver Coin", "Ruby", "Emerald", "Sapphire",
            "Diamond", "Ancient Key", "Magic Scroll", "Crystal Shard", "Dragon Scale"
        };
        [SerializeField] private int[] _itemRarities = new int[]  // 0=common, 1=rare, 2=legendary
        {
            0, 0, 1, 1, 1,
            2, 2, 1, 0, 2
        };
        [SerializeField] private int[] _itemPoints = new int[]
        {
            10, 5, 50, 50, 50,
            200, 150, 75, 15, 250
        };
        
        // Inventory data (persisted)
        private int[] _itemIds;
        private int[] _itemCounts;
        private int _totalItemCount;
        private int _uniqueItemCount;
        
        // Player stats (persisted)
        private int _totalScore;
        private int _totalCollected;
        private int _sessionCollected;
        private float _playTime;
        private int _commonCount;
        private int _rareCount;
        private int _legendaryCount;
        
        // Achievements (persisted)
        private bool _achieveFirstItem;
        private bool _achieveTenItems;
        private bool _achieveHundredItems;
        private bool _achieveFirstRare;
        private bool _achieveFirstLegendary;
        private bool _achieveAllTypes;
        private bool _achieve1000Points;
        private bool _achieve5000Points;
        
        // UI state
        private string[] _recentActivity;
        private int _recentActivityIndex;
        private const int RECENT_ACTIVITY_SIZE = 5;
        private float _achievementPopupTimer;
        private const float ACHIEVEMENT_POPUP_DURATION = 3f;
        
        // Persistence state
        private int _estimatedSize;
        private int _autoSaveCount;
        private bool _playerDataReady;
        
        // Rarity colors
        private readonly Color COMMON_COLOR = new Color(0.8f, 0.8f, 0.8f);
        private readonly Color RARE_COLOR = new Color(0.2f, 0.6f, 1f);
        private readonly Color LEGENDARY_COLOR = new Color(1f, 0.8f, 0.2f);
        
        // Constants
        private const string SAVE_KEY = "ce_demo_inventory";
        private const int SAVE_VERSION = 2;
        private const int GOAL_ITEMS = 50;
        
        void Start()
        {
            if (_maxSlots < 1)
            {
                _maxSlots = 1;
                CELogger.Warning("Inventory", "Max slots was less than 1. Clamped to 1.");
            }

            EnsurePersistenceRegistration();
            InitializeInventory();
            InitializeUI();
            UpdateDisplay();
            
            CELogger.Info("Inventory", "Treasure Hunt initialized - persistence is AUTOMATIC!");
        }

        public override void OnPlayerRestored(VRCPlayerApi player)
        {
            if (!Utilities.IsValid(player) || !player.isLocal)
            {
                return;
            }

            _playerDataReady = true;
            LoadInventory();
        }
        
        void Update()
        {
            _playTime += Time.deltaTime;
            
            // Update achievement popup timer
            if (_achievementPopupTimer > 0)
            {
                _achievementPopupTimer -= Time.deltaTime;
                if (_achievementPopupTimer <= 0 && _achievementPopup != null)
                {
                    _achievementPopup.SetActive(false);
                }
            }
        }
        
        private void InitializeInventory()
        {
            _itemIds = new int[_maxSlots];
            _itemCounts = new int[_maxSlots];
            _recentActivity = new string[RECENT_ACTIVITY_SIZE];
            
            for (int i = 0; i < _maxSlots; i++)
            {
                _itemIds[i] = -1;
                _itemCounts[i] = 0;
            }
            
            for (int i = 0; i < RECENT_ACTIVITY_SIZE; i++)
            {
                _recentActivity[i] = "";
            }
        }
        
        private void InitializeUI()
        {
            if (_titleText != null)
            {
                _titleText.text = "<size=150%>TREASURE HUNT</size>\n<size=80%>Collect treasures - progress saves automatically!</size>";
            }
            
            if (_achievementPopup != null)
            {
                _achievementPopup.SetActive(false);
            }
        }
        
        // ========================================
        // INVENTORY OPERATIONS
        // ========================================
        
        /// <summary>
        /// Adds an item to the inventory. Automatically saves!
        /// </summary>
        public bool AddItem(int itemId)
        {
            if (_itemNames == null || _itemNames.Length == 0) return false;
            if (itemId < 0 || itemId >= _itemNames.Length) return false;
            
            int rarity = GetItemRarity(itemId);
            int points = GetItemPoints(itemId);
            string itemName = GetItemName(itemId);
            
            // Check if we already have this item
            int existingSlot = FindItemSlot(itemId);
            if (existingSlot >= 0)
            {
                _itemCounts[existingSlot]++;
            }
            else
            {
                // Find empty slot
                int emptySlot = FindEmptySlot();
                if (emptySlot < 0)
                {
                    AddRecentActivity($"<color=#FF6666>Inventory full!</color>");
                    UpdateDisplay();
                    return false;
                }
                
                _itemIds[emptySlot] = itemId;
                _itemCounts[emptySlot] = 1;
                _uniqueItemCount++;
            }
            
            _totalItemCount++;
            _totalCollected++;
            _sessionCollected++;
            _totalScore += points;

            // Track by rarity only for successful collections
            switch (rarity)
            {
                case 0: _commonCount++; break;
                case 1: _rareCount++; break;
                case 2: _legendaryCount++; break;
            }
            
            // Visual and audio feedback based on rarity
            PlayCollectFeedback(rarity);
            
            // Add to recent activity
            string rarityTag = GetRarityTag(rarity);
            AddRecentActivity($"{rarityTag} {itemName} <color=#88FF88>+{points} pts</color>");
            
            // Check achievements
            CheckAchievements();
            
            // *** AUTOMATIC PERSISTENCE! ***
            // This is the key feature - no save button needed
            AutoSave();
            
            UpdateDisplay();
            return true;
        }
        
        private void PlayCollectFeedback(int rarity)
        {
            switch (rarity)
            {
                case 0: // Common
                    if (_collectSound != null) _collectSound.Play();
                    if (_collectParticles != null) _collectParticles.Play();
                    break;
                case 1: // Rare
                    if (_rareCollectSound != null)
                    {
                        _rareCollectSound.Play();
                    }
                    else if (_collectSound != null)
                    {
                        PlayCollectSoundWithPitch(1.2f);
                    }
                    if (_rareParticles != null) _rareParticles.Play();
                    else if (_collectParticles != null) _collectParticles.Play();
                    break;
                case 2: // Legendary
                    if (_legendaryCollectSound != null)
                    {
                        _legendaryCollectSound.Play();
                    }
                    else if (_collectSound != null)
                    {
                        PlayCollectSoundWithPitch(1.5f);
                    }
                    if (_legendaryParticles != null) _legendaryParticles.Play();
                    else if (_collectParticles != null) _collectParticles.Play();
                    break;
            }
        }

        private void PlayCollectSoundWithPitch(float pitch)
        {
            if (_collectSound == null) return;

            float originalPitch = _collectSound.pitch;
            _collectSound.pitch = pitch;
            _collectSound.Play();
            _collectSound.pitch = originalPitch;
        }
        
        private string GetRarityTag(int rarity)
        {
            switch (rarity)
            {
                case 1: return "<color=#3399FF>[RARE]</color>";
                case 2: return "<color=#FFD700>[LEGENDARY]</color>";
                default: return "";
            }
        }
        
        private int GetItemRarity(int itemId)
        {
            if (itemId < 0 || _itemRarities == null || itemId >= _itemRarities.Length) return 0;
            return _itemRarities[itemId];
        }
        
        private int GetItemPoints(int itemId)
        {
            if (itemId < 0 || _itemPoints == null || itemId >= _itemPoints.Length) return 10;
            return _itemPoints[itemId];
        }
        
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
            
            AutoSave();
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
                if (_itemIds[i] == itemId) return i;
            }
            return -1;
        }
        
        private int FindEmptySlot()
        {
            for (int i = 0; i < _maxSlots; i++)
            {
                if (_itemIds[i] < 0) return i;
            }
            return -1;
        }
        
        private string GetItemName(int itemId)
        {
            if (itemId < 0 || _itemNames == null || itemId >= _itemNames.Length)
            {
                return $"Treasure #{itemId}";
            }
            return _itemNames[itemId];
        }
        
        // ========================================
        // ACHIEVEMENTS
        // ========================================
        
        private void CheckAchievements()
        {
            bool newAchievement = false;
            string achievementName = "";
            
            if (!_achieveFirstItem && _totalCollected >= 1)
            {
                _achieveFirstItem = true;
                achievementName = "🏆 First Discovery!";
                newAchievement = true;
            }
            else if (!_achieveTenItems && _totalCollected >= 10)
            {
                _achieveTenItems = true;
                achievementName = "🏆 Novice Collector (10 items)";
                newAchievement = true;
            }
            else if (!_achieveHundredItems && _totalCollected >= 100)
            {
                _achieveHundredItems = true;
                achievementName = "🏆 Master Collector (100 items)";
                newAchievement = true;
            }
            else if (!_achieveFirstRare && _rareCount >= 1)
            {
                _achieveFirstRare = true;
                achievementName = "💎 First Rare Find!";
                newAchievement = true;
            }
            else if (!_achieveFirstLegendary && _legendaryCount >= 1)
            {
                _achieveFirstLegendary = true;
                achievementName = "⭐ Legendary Discovery!";
                newAchievement = true;
            }
            else if (!_achieve1000Points && _totalScore >= 1000)
            {
                _achieve1000Points = true;
                achievementName = "💰 Treasure Hunter (1000 pts)";
                newAchievement = true;
            }
            else if (!_achieve5000Points && _totalScore >= 5000)
            {
                _achieve5000Points = true;
                achievementName = "👑 Treasure Master (5000 pts)";
                newAchievement = true;
            }
            else if (!_achieveAllTypes && _itemNames != null && _uniqueItemCount >= _itemNames.Length)
            {
                _achieveAllTypes = true;
                achievementName = "🌟 Complete Collection!";
                newAchievement = true;
            }
            
            if (newAchievement)
            {
                ShowAchievementPopup(achievementName);
            }
        }
        
        private void ShowAchievementPopup(string achievementName)
        {
            if (_achievementPopup != null)
            {
                _achievementPopup.SetActive(true);
                _achievementPopupTimer = ACHIEVEMENT_POPUP_DURATION;
                
                if (_achievementPopupText != null)
                {
                    _achievementPopupText.text = achievementName;
                }
            }
            
            if (_achievementSound != null)
            {
                _achievementSound.Play();
            }
            
            AddRecentActivity($"<color=#FFD700>{achievementName}</color>");
            CELogger.Info("Inventory", $"Achievement unlocked: {achievementName}");
        }
        
        private int CountAchievements()
        {
            int count = 0;
            if (_achieveFirstItem) count++;
            if (_achieveTenItems) count++;
            if (_achieveHundredItems) count++;
            if (_achieveFirstRare) count++;
            if (_achieveFirstLegendary) count++;
            if (_achieve1000Points) count++;
            if (_achieve5000Points) count++;
            if (_achieveAllTypes) count++;
            return count;
        }
        
        // ========================================
        // AUTOMATIC PERSISTENCE
        // ========================================
        
        /// <summary>
        /// Automatically saves inventory - called after every change.
        /// This is the key feature of CEPersistence: NO manual save needed!
        /// </summary>
        private void AutoSave()
        {
            if (!Utilities.IsValid(Networking.LocalPlayer))
            {
                return;
            }

            if (!_playerDataReady)
            {
                CELogger.Debug("Inventory", "Skipping auto-save until local PlayerData is restored.");
                return;
            }

            EnsurePersistenceRegistration();
            InventorySaveData saveData = CreateSaveData();
            DataDictionary data = saveData.data;
            _estimatedSize = EstimateDataSize(data);
            
            // Check quota
            if (_estimatedSize > CEPersistence.PLAYER_DATA_QUOTA)
            {
                CELogger.Error("Inventory", $"Data size ({_estimatedSize} bytes) exceeds quota!");
                return;
            }
            
            SaveResult result = CEPersistence.Save(saveData);
            if (result == SaveResult.Success)
            {
                _autoSaveCount++;
                
                // Only log occasionally to avoid spam
                if (_autoSaveCount % 10 == 0)
                {
                    CELogger.Debug("Inventory", $"Auto-saved ({_autoSaveCount} saves, {_estimatedSize} bytes)");
                }
            }
            else if (result != SaveResult.NotAllowed)
            {
                CELogger.Warning("Inventory", $"Auto-save failed: {result}");
            }
        }
        
        private DataDictionary ToDataDictionary()
        {
            DataDictionary data = new DataDictionary();
            
            data["__ce_version"] = SAVE_VERSION;
            data["__ce_model"] = SAVE_KEY;
            
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
            
            data["score"] = _totalScore;
            data["total_collected"] = _totalCollected;
            data["play_time"] = _playTime;
            data["common"] = _commonCount;
            data["rare"] = _rareCount;
            data["legendary"] = _legendaryCount;
            
            // Achievement flags
            data["ach_first"] = _achieveFirstItem;
            data["ach_10"] = _achieveTenItems;
            data["ach_100"] = _achieveHundredItems;
            data["ach_rare"] = _achieveFirstRare;
            data["ach_leg"] = _achieveFirstLegendary;
            data["ach_all"] = _achieveAllTypes;
            data["ach_1k"] = _achieve1000Points;
            data["ach_5k"] = _achieve5000Points;
            
            return data;
        }
        
        private bool FromDataDictionary(DataDictionary data)
        {
            if (data == null) return false;

            ResetLoadedState();
            int configuredItemTypeCount = _itemNames != null ? _itemNames.Length : 0;
            
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
                        if (!TryReadInt(itemData, "id", out int id) ||
                            !TryReadInt(itemData, "count", out int count))
                        {
                            continue;
                        }

                        if (id < 0 || id >= configuredItemTypeCount || count <= 0)
                        {
                            continue;
                        }
                        
                        _itemIds[_uniqueItemCount] = id;
                        _itemCounts[_uniqueItemCount] = count;
                        _totalItemCount += count;
                        _uniqueItemCount++;
                    }
                }
            }
            
            // Load stats
            TryReadInt(data, "score", out _totalScore);
            TryReadInt(data, "total_collected", out _totalCollected);
            TryReadFloat(data, "play_time", out _playTime);
            TryReadInt(data, "common", out _commonCount);
            TryReadInt(data, "rare", out _rareCount);
            TryReadInt(data, "legendary", out _legendaryCount);
            
            // Load achievements
            TryReadBool(data, "ach_first", out _achieveFirstItem);
            TryReadBool(data, "ach_10", out _achieveTenItems);
            TryReadBool(data, "ach_100", out _achieveHundredItems);
            TryReadBool(data, "ach_rare", out _achieveFirstRare);
            TryReadBool(data, "ach_leg", out _achieveFirstLegendary);
            TryReadBool(data, "ach_all", out _achieveAllTypes);
            TryReadBool(data, "ach_1k", out _achieve1000Points);
            TryReadBool(data, "ach_5k", out _achieve5000Points);
            
            return true;
        }

        private void ResetLoadedState()
        {
            for (int i = 0; i < _maxSlots; i++)
            {
                _itemIds[i] = -1;
                _itemCounts[i] = 0;
            }

            _uniqueItemCount = 0;
            _totalItemCount = 0;
            _totalScore = 0;
            _totalCollected = 0;
            _sessionCollected = 0;
            _playTime = 0f;
            _commonCount = 0;
            _rareCount = 0;
            _legendaryCount = 0;

            _achieveFirstItem = false;
            _achieveTenItems = false;
            _achieveHundredItems = false;
            _achieveFirstRare = false;
            _achieveFirstLegendary = false;
            _achieveAllTypes = false;
            _achieve1000Points = false;
            _achieve5000Points = false;
        }

        private bool TryReadInt(DataDictionary source, string key, out int value)
        {
            value = 0;
            if (source == null) return false;
            if (!source.TryGetValue(key, out DataToken token)) return false;
            if (!IsNumericToken(token)) return false;

            value = (int)token.Double;
            return true;
        }

        private bool TryReadFloat(DataDictionary source, string key, out float value)
        {
            value = 0f;
            if (source == null) return false;
            if (!source.TryGetValue(key, out DataToken token)) return false;
            if (!IsNumericToken(token)) return false;

            value = (float)token.Double;
            return true;
        }

        private bool TryReadBool(DataDictionary source, string key, out bool value)
        {
            value = false;
            if (source == null) return false;
            if (!source.TryGetValue(key, out DataToken token)) return false;
            if (token.TokenType != TokenType.Boolean) return false;

            value = token.Boolean;
            return true;
        }

        private bool IsNumericToken(DataToken token)
        {
            switch (token.TokenType)
            {
                case TokenType.SByte:
                case TokenType.Byte:
                case TokenType.Short:
                case TokenType.UShort:
                case TokenType.Int:
                case TokenType.UInt:
                case TokenType.Long:
                case TokenType.ULong:
                case TokenType.Float:
                case TokenType.Double:
                    return true;
                default:
                    return false;
            }
        }
        
        public void LoadInventory()
        {
            CELogger.Info("Inventory", "Loading inventory via CEPersistence...");

            if (!Utilities.IsValid(Networking.LocalPlayer))
            {
                CELogger.Debug("Inventory", "Skipping restore - local player not available.");
                UpdateDisplay();
                return;
            }

            EnsurePersistenceRegistration();
            
            RestoreResult result = CEPersistence.Restore(out InventorySaveData savedData);
            if (result == RestoreResult.Success && savedData != null)
            {
                ApplySaveData(savedData);
                CELogger.Info("Inventory", "Inventory loaded - welcome back!");
            }
            else if (result == RestoreResult.NoData)
            {
                CELogger.Info("Inventory", "No saved data found - starting fresh");
            }
            else
            {
                CELogger.Warning("Inventory", $"Restore failed: {result}");
            }
            
            UpdateDisplay();
        }

        private void EnsurePersistenceRegistration()
        {
#if COMPILER_UDONSHARP
            // CE persistence models are auto-registered during Udon compile.
            // Runtime registration with delegates is editor-only.
            return;
#else
            if (CEPersistence.IsRegistered<InventorySaveData>())
            {
                return;
            }

            CEPersistence.Register<InventorySaveData>(
                toData: model => model != null && model.data != null ? model.data : new DataDictionary(),
                fromData: data => new InventorySaveData { data = data },
                key: SAVE_KEY,
                version: SAVE_VERSION);
#endif
        }

        private InventorySaveData CreateSaveData()
        {
            return new InventorySaveData { data = ToDataDictionary() };
        }
        
        private void ApplySaveData(InventorySaveData saveData)
        {
            if (saveData == null || saveData.data == null) return;
            FromDataDictionary(saveData.data);
        }
        
        private int EstimateDataSize(DataDictionary data)
        {
            int size = 100; // Base overhead
            size += _uniqueItemCount * 30; // Per-item
            size += 200; // Stats and achievements
            return size;
        }
        
        // ========================================
        // UI DISPLAY
        // ========================================
        
        private void AddRecentActivity(string activity)
        {
            // Shift existing entries down
            for (int i = RECENT_ACTIVITY_SIZE - 1; i > 0; i--)
            {
                _recentActivity[i] = _recentActivity[i - 1];
            }
            _recentActivity[0] = activity;
        }
        
        private void UpdateDisplay()
        {
            UpdateProgressDisplay();
            UpdateInventoryGrid();
            UpdateStatsDisplay();
            UpdateRecentActivityDisplay();
            UpdateAchievementDisplay();
        }
        
        private void UpdateProgressDisplay()
        {
            int itemTypeCount = _itemNames != null ? _itemNames.Length : 0;

            if (_progressText != null)
            {
                float progress = Mathf.Min((float)_totalCollected / GOAL_ITEMS, 1f);
                string status = _totalCollected >= GOAL_ITEMS ? "<color=#FFD700>GOAL REACHED!</color>" : $"{_totalCollected}/{GOAL_ITEMS}";
                
                _progressText.text = $"<b>SCORE: <color=#FFD700>{_totalScore:N0}</color></b>   |   " +
                                    $"Collected: {status}   |   " +
                                    $"Types: {_uniqueItemCount}/{itemTypeCount}";
            }
            
            if (_progressBar != null)
            {
                _progressBar.value = Mathf.Min((float)_totalCollected / GOAL_ITEMS, 1f);
            }
        }
        
        private void UpdateInventoryGrid()
        {
            if (_inventorySlotImages == null) return;
            
            for (int i = 0; i < _inventorySlotImages.Length; i++)
            {
                if (_inventorySlotImages[i] == null) continue;
                
                if (i < _maxSlots && _itemIds[i] >= 0)
                {
                    int itemId = _itemIds[i];
                    int rarity = GetItemRarity(itemId);
                    
                    // Set sprite
                    if (_itemSprites != null && itemId < _itemSprites.Length && _itemSprites[itemId] != null)
                    {
                        _inventorySlotImages[i].sprite = _itemSprites[itemId];
                    }
                    
                    // Color by rarity
                    switch (rarity)
                    {
                        case 0: _inventorySlotImages[i].color = COMMON_COLOR; break;
                        case 1: _inventorySlotImages[i].color = RARE_COLOR; break;
                        case 2: _inventorySlotImages[i].color = LEGENDARY_COLOR; break;
                    }
                    
                    // Count display
                    if (_inventorySlotCounts != null && i < _inventorySlotCounts.Length && _inventorySlotCounts[i] != null)
                    {
                        _inventorySlotCounts[i].text = _itemCounts[i] > 1 ? $"x{_itemCounts[i]}" : "";
                    }
                }
                else
                {
                    // Empty slot
                    if (_emptySlotSprite != null)
                    {
                        _inventorySlotImages[i].sprite = _emptySlotSprite;
                    }
                    _inventorySlotImages[i].color = new Color(0.3f, 0.3f, 0.3f, 0.5f);
                    
                    if (_inventorySlotCounts != null && i < _inventorySlotCounts.Length && _inventorySlotCounts[i] != null)
                    {
                        _inventorySlotCounts[i].text = "";
                    }
                }
            }
        }
        
        private void UpdateStatsDisplay()
        {
            if (_statsText == null) return;
            
            float quotaPercent = (_estimatedSize / (float)CEPersistence.PLAYER_DATA_QUOTA) * 100f;
            
            _statsText.text = $"<b>TREASURE STATS</b>\n" +
                             $"<color=#AAAAAA>Common:</color> {_commonCount}   " +
                             $"<color=#3399FF>Rare:</color> {_rareCount}   " +
                             $"<color=#FFD700>Legendary:</color> {_legendaryCount}\n" +
                             $"Play Time: {FormatTime(_playTime)}   |   " +
                             $"This Session: {_sessionCollected}\n" +
                             $"<size=80%><color=#88FF88>✓ Auto-saved ({_autoSaveCount}x) • {quotaPercent:F1}% quota used</color></size>";
        }
        
        private void UpdateRecentActivityDisplay()
        {
            if (_recentActivityText == null) return;
            
            string text = "<b>RECENT ACTIVITY</b>\n";
            for (int i = 0; i < RECENT_ACTIVITY_SIZE; i++)
            {
                if (!string.IsNullOrEmpty(_recentActivity[i]))
                {
                    text += $"• {_recentActivity[i]}\n";
                }
            }
            
            if (string.IsNullOrEmpty(_recentActivity[0]))
            {
                text += "<color=#888888>Explore the area to find treasures!</color>";
            }
            
            _recentActivityText.text = text;
        }
        
        private void UpdateAchievementDisplay()
        {
            if (_achievementText == null) return;
            
            int totalAchievements = 8;
            int earned = CountAchievements();
            
            string text = $"<b>ACHIEVEMENTS ({earned}/{totalAchievements})</b>\n";
            
            text += _achieveFirstItem ? "<color=#88FF88>✓</color> " : "<color=#666666>○</color> ";
            text += "First Discovery\n";
            
            text += _achieveTenItems ? "<color=#88FF88>✓</color> " : "<color=#666666>○</color> ";
            text += "Novice Collector\n";
            
            text += _achieveFirstRare ? "<color=#88FF88>✓</color> " : "<color=#666666>○</color> ";
            text += "First Rare Find\n";
            
            text += _achieveFirstLegendary ? "<color=#88FF88>✓</color> " : "<color=#666666>○</color> ";
            text += "Legendary Discovery\n";
            
            text += _achieve1000Points ? "<color=#88FF88>✓</color> " : "<color=#666666>○</color> ";
            text += "Treasure Hunter (1000 pts)\n";
            
            _achievementText.text = text;
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
        
        /// <summary>
        /// Wipes all saved data (for demo/testing purposes only).
        /// </summary>
        public void OnWipeButton()
        {
            WipeData();
        }
        
        public void WipeData()
        {
            CELogger.Warning("Inventory", "Wiping all persistence data!");
            
            for (int i = 0; i < _maxSlots; i++)
            {
                _itemIds[i] = -1;
                _itemCounts[i] = 0;
            }
            
            _totalItemCount = 0;
            _uniqueItemCount = 0;
            _totalCollected = 0;
            _totalScore = 0;
            _playTime = 0f;
            _sessionCollected = 0;
            _commonCount = 0;
            _rareCount = 0;
            _legendaryCount = 0;
            
            _achieveFirstItem = false;
            _achieveTenItems = false;
            _achieveHundredItems = false;
            _achieveFirstRare = false;
            _achieveFirstLegendary = false;
            _achieveAllTypes = false;
            _achieve1000Points = false;
            _achieve5000Points = false;
            
            _estimatedSize = 0;
            _autoSaveCount = 0;
            
            for (int i = 0; i < RECENT_ACTIVITY_SIZE; i++)
            {
                _recentActivity[i] = "";
            }
            
            AddRecentActivity("<color=#FF8888>All data wiped - fresh start!</color>");
            AutoSave();
            UpdateDisplay();
            CELogger.Info("Inventory", "All persistence data wiped");
        }
        
        /// <summary>
        /// Called by CollectibleItem when player collects something.
        /// </summary>
        public void OnItemCollected(int itemId)
        {
            AddItem(itemId);
        }
        
        // Manual save button (optional - auto-save is the primary path)
        public void OnSaveButton()
        {
            AutoSave();
            AddRecentActivity("<color=#88FF88>Auto-save refreshed</color>");
            UpdateDisplay();
        }
        
        // Quick collect buttons for demo
        public void CollectRandomItem()
        {
            if (_itemRarities == null || _itemRarities.Length == 0)
            {
                AddRecentActivity("<color=#FF6666>No treasures configured</color>");
                UpdateDisplay();
                return;
            }

            // Weighted random by rarity (common more likely)
            int roll = Random.Range(0, 100);
            int itemId;
            
            if (roll < 60) // 60% common
            {
                int[] commons = GetItemsByRarity(0);
                itemId = commons.Length > 0 ? commons[Random.Range(0, commons.Length)] : Random.Range(0, _itemRarities.Length);
            }
            else if (roll < 90) // 30% rare
            {
                int[] rares = GetItemsByRarity(1);
                itemId = rares.Length > 0 ? rares[Random.Range(0, rares.Length)] : Random.Range(0, _itemRarities.Length);
            }
            else // 10% legendary
            {
                int[] legendaries = GetItemsByRarity(2);
                itemId = legendaries.Length > 0 ? legendaries[Random.Range(0, legendaries.Length)] : Random.Range(0, _itemRarities.Length);
            }
            
            AddItem(itemId);
        }
        
        private int[] GetItemsByRarity(int rarity)
        {
            if (_itemRarities == null || _itemRarities.Length == 0)
            {
                return new int[0];
            }

            int count = 0;
            for (int i = 0; i < _itemRarities.Length; i++)
            {
                if (_itemRarities[i] == rarity) count++;
            }
            
            int[] result = new int[count];
            int idx = 0;
            for (int i = 0; i < _itemRarities.Length; i++)
            {
                if (_itemRarities[i] == rarity)
                {
                    result[idx++] = i;
                }
            }
            return result;
        }
        
        // Specific item buttons
        public void CollectGoldCoin() => AddItem(0);
        public void CollectSilverCoin() => AddItem(1);
        public void CollectRuby() => AddItem(2);
        public void CollectEmerald() => AddItem(3);
        public void CollectSapphire() => AddItem(4);
        public void CollectDiamond() => AddItem(5);
        public void CollectAncientKey() => AddItem(6);
        public void CollectMagicScroll() => AddItem(7);
        public void CollectCrystalShard() => AddItem(8);
        public void CollectDragonScale() => AddItem(9);
    }
}
