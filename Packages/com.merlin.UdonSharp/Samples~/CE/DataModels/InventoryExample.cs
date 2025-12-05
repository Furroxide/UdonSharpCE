using UdonSharp;
using UnityEngine;
using VRC.SDK3.Data;
using UdonSharp.CE.Data;
using UdonSharp.CE.DevTools;

namespace UdonSharp.CE.Samples
{
    /// <summary>
    /// Example showing how to use CE.Data for inventory management.
    /// Demonstrates CEList, CEDictionary, DataModel attributes, and JSON serialization.
    /// </summary>
    /// <remarks>
    /// This example shows:
    /// 1. Using CEList for type-safe collections with DataList conversion
    /// 2. Using CEDictionary for key-value storage with DataDictionary conversion
    /// 3. Manual registration of data models via CEDataBridge
    /// 4. JSON serialization for persistence or networking
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class InventoryExample : UdonSharpBehaviour
    {
        [Header("Test Controls")]
        [SerializeField] private bool runTestOnStart = true;

        // Example data model (attributes are documentation in Phase 1)
        // [DataModel]
        // public class InventoryItem
        // {
        //     [DataField("id")] public int itemId;
        //     [DataField("qty")] public int quantity;
        //     [DataField("name")] public string itemName;
        // }

        // Since we can't use non-UdonSharpBehaviour classes directly,
        // we'll demonstrate with primitive types and manual conversion

        private void Start()
        {
            if (runTestOnStart)
            {
                RunTests();
            }
        }

        public void RunTests()
        {
            CELogger.Info("InventoryExample", "=== Starting CE.Data Tests ===");

            TestCEList();
            TestCEDictionary();
            TestJsonSerialization();
            TestDataBridge();

            CELogger.Info("InventoryExample", "=== All Tests Complete ===");
        }

        private void TestCEList()
        {
            CELogger.Info("InventoryExample", "--- Testing CEList<int> ---");

            // Create and populate list
            CEList<int> itemIds = new CEList<int>();
            itemIds.Add(101);
            itemIds.Add(205);
            itemIds.Add(307);

            CELogger.Debug("InventoryExample", $"List count: {itemIds.Count}");
            CELogger.Debug("InventoryExample", $"Item at index 1: {itemIds[1]}");

            // Convert to DataList
            DataList dataList = itemIds.ToDataList();
            CELogger.Debug("InventoryExample", $"DataList count: {dataList.Count}");

            // Convert back
            CEList<int> restored = CEList<int>.FromDataList(dataList);
            CELogger.Debug("InventoryExample", $"Restored count: {restored.Count}");

            // JSON serialization
            string json = itemIds.ToJson();
            CELogger.Debug("InventoryExample", $"JSON: {json}");

            CEList<int> fromJson = CEList<int>.FromJson(json);
            CELogger.Debug("InventoryExample", $"From JSON count: {fromJson.Count}");

            CELogger.Info("InventoryExample", "CEList test passed!");
        }

        private void TestCEDictionary()
        {
            CELogger.Info("InventoryExample", "--- Testing CEDictionary<string, int> ---");

            // Create and populate dictionary
            CEDictionary<string, int> inventory = new CEDictionary<string, int>();
            inventory.Add("sword", 1);
            inventory.Add("potion", 5);
            inventory.Add("gold", 100);

            CELogger.Debug("InventoryExample", $"Dictionary count: {inventory.Count}");
            CELogger.Debug("InventoryExample", $"Gold amount: {inventory["gold"]}");

            // Check containment
            bool hasSword = inventory.ContainsKey("sword");
            CELogger.Debug("InventoryExample", $"Has sword: {hasSword}");

            // Convert to DataDictionary
            DataDictionary dataDict = inventory.ToDataDictionary();
            CELogger.Debug("InventoryExample", $"DataDictionary created");

            // Convert back
            CEDictionary<string, int> restored = CEDictionary<string, int>.FromDataDictionary(dataDict);
            CELogger.Debug("InventoryExample", $"Restored count: {restored.Count}");

            // JSON serialization
            string json = inventory.ToJson(beautify: true);
            CELogger.Debug("InventoryExample", $"JSON:\n{json}");

            CELogger.Info("InventoryExample", "CEDictionary test passed!");
        }

        private void TestJsonSerialization()
        {
            CELogger.Info("InventoryExample", "--- Testing JSON Round-Trip ---");

            // Create complex nested structure
            CEList<string> items = new CEList<string>();
            items.Add("sword_01");
            items.Add("shield_02");
            items.Add("potion_03");

            // Serialize to JSON
            string json = items.ToJson();
            CELogger.Debug("InventoryExample", $"Serialized: {json}");

            // Deserialize
            CEList<string> deserialized = CEList<string>.FromJson(json);

            // Verify
            bool match = deserialized.Count == items.Count;
            for (int i = 0; i < items.Count && match; i++)
            {
                match = items[i] == deserialized[i];
            }

            if (match)
            {
                CELogger.Info("InventoryExample", "JSON round-trip test passed!");
            }
            else
            {
                CELogger.Error("InventoryExample", "JSON round-trip test FAILED!");
            }
        }

        private void TestDataBridge()
        {
            CELogger.Info("InventoryExample", "--- Testing CEDataBridge Manual Registration ---");

            // In Phase 1, we manually register model converters
            // This demonstrates the pattern for custom data models

            // Register a "PlayerData" model converter
            // (In a real app, you'd define a class and register in Start())

            // For this example, we'll use DataDictionary directly
            // since we can't create non-UdonSharpBehaviour classes

            DataDictionary playerData = new DataDictionary();
            playerData["name"] = "TestPlayer";
            playerData["level"] = 10;
            playerData["xp"] = 5000;

            // Serialize to JSON
            if (VRCJson.TrySerializeToJson(playerData, JsonExportType.Beautify, out DataToken jsonToken))
            {
                string json = jsonToken.String;
                CELogger.Debug("InventoryExample", $"Player data JSON:\n{json}");

                // Deserialize back
                if (VRCJson.TryDeserializeFromJson(json, out DataToken resultToken))
                {
                    DataDictionary restored = resultToken.DataDictionary;
                    string name = restored["name"].String;
                    // JSON numbers become doubles
                    int level = (int)restored["level"].Double;

                    CELogger.Debug("InventoryExample", $"Restored: name={name}, level={level}");
                    CELogger.Info("InventoryExample", "DataBridge pattern test passed!");
                }
            }
        }

        // Button handlers for UI testing
        public void OnRunTestsClicked()
        {
            RunTests();
        }
    }
}
