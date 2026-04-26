using System;
using System.Collections.Generic;
using System.Reflection;
using CEShowcase.Station5_Persistence;
using NUnit.Framework;
using UnityEngine;

namespace CEShowcase.Tests.Editor
{
    public class Station5PersistenceRegressionTests
    {
        private readonly List<GameObject> _createdObjects = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                var obj = _createdObjects[i];
                if (obj != null)
                {
                    UnityEngine.Object.DestroyImmediate(obj);
                }
            }

            _createdObjects.Clear();
        }

        [Test]
        public void AddItem_WhenInventoryIsFull_DoesNotIncrementFailedRarityCounts()
        {
            InventorySystem inventory = CreateComponent<InventorySystem>("InventoryRegression");

            SetField(inventory, "_maxSlots", 1);
            InvokePrivate(inventory, "InitializeInventory");

            bool firstAdd = inventory.AddItem(0);
            bool secondAdd = inventory.AddItem(2);

            Assert.IsTrue(firstAdd, "First add should succeed.");
            Assert.IsFalse(secondAdd, "Second add should fail because only one unique slot is allowed.");
            Assert.AreEqual(1, GetFieldValue<int>(inventory, "_commonCount"), "Common count should reflect only successful adds.");
            Assert.AreEqual(0, GetFieldValue<int>(inventory, "_rareCount"), "Rare count must not increment for a rejected add.");
            Assert.AreEqual(1, GetFieldValue<int>(inventory, "_totalItemCount"), "Total item count should remain unchanged after failed add.");
        }

        [Test]
        public void CollectRandomItem_WhenRequestedRarityPoolIsEmpty_DoesNotThrow()
        {
            InventorySystem inventory = CreateComponent<InventorySystem>("InventoryRandomRegression");

            SetField(inventory, "_itemNames", new[] { "Only Common A", "Only Common B" });
            SetField(inventory, "_itemRarities", new[] { 0, 0 });
            SetField(inventory, "_itemPoints", new[] { 1, 1 });
            InvokePrivate(inventory, "InitializeInventory");

            Assert.DoesNotThrow(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    inventory.CollectRandomItem();
                }
            });
        }

        [Test]
        public void Collectible_Start_WhenVisualObjectMissing_UsesOwningGameObject()
        {
            CollectibleItem collectible = CreateComponent<CollectibleItem>("CollectibleVisualFallback");

            InvokePrivate(collectible, "Start");

            GameObject visualObject = GetFieldValue<GameObject>(collectible, "_visualObject");
            Assert.AreSame(collectible.gameObject, visualObject, "Collectible should fall back to its own GameObject when visual is not configured.");
        }

        [Test]
        public void Collectible_Start_WhenSyncedCollected_HidesVisualAndStartsRespawnTimer()
        {
            CollectibleItem collectible = CreateComponent<CollectibleItem>("CollectibleSyncedState");
            GameObject visual = CreateObject("CollectibleVisual");
            visual.transform.SetParent(collectible.transform, false);
            visual.transform.position = new Vector3(3f, 2f, 1f);

            SetField(collectible, "_visualObject", visual);
            SetField(collectible, "_isCollected", true);
            SetField(collectible, "_canRespawn", true);
            SetField(collectible, "_respawnTime", 7f);

            InvokePrivate(collectible, "Start");

            Assert.IsFalse(visual.activeSelf, "Collected items should start hidden.");
            Assert.IsTrue(GetFieldValue<bool>(collectible, "_localCollected"), "Local collected flag should mirror synced collected state.");
            Assert.AreEqual(7f, GetFieldValue<float>(collectible, "_respawnTimer"), 0.0001f, "Respawn timer should start from configured respawn time.");
            Assert.AreEqual(visual.transform.position, GetFieldValue<Vector3>(collectible, "_startPosition"), "Start position should be taken from the visual object transform.");
        }

        [Test]
        public void InventorySaveData_RoundTripsThroughDataDictionaryModel()
        {
            InventorySystem source = CreateComponent<InventorySystem>("InventoryRoundTripSource");
            SetField(source, "_maxSlots", 4);
            InvokePrivate(source, "InitializeInventory");

            Assert.IsTrue(source.AddItem(0));
            Assert.IsTrue(source.AddItem(2));

            InventorySaveData saveData = InvokePrivate<InventorySaveData>(source, "CreateSaveData");

            InventorySystem restored = CreateComponent<InventorySystem>("InventoryRoundTripRestored");
            SetField(restored, "_maxSlots", 4);
            InvokePrivate(restored, "InitializeInventory");
            InvokePrivate(restored, "ApplySaveData", saveData);

            Assert.AreEqual(2, GetFieldValue<int>(restored, "_totalItemCount"));
            Assert.AreEqual(2, GetFieldValue<int>(restored, "_uniqueItemCount"));
            Assert.AreEqual(60, GetFieldValue<int>(restored, "_totalScore"));
            Assert.IsTrue(restored.HasItem(0));
            Assert.IsTrue(restored.HasItem(2));
        }

        [Test]
        public void InventorySystem_ExposesOnPlayerRestoredReloadHook()
        {
            MethodInfo method = typeof(InventorySystem).GetMethod("OnPlayerRestored", BindingFlags.Instance | BindingFlags.Public);

            Assert.IsNotNull(method, "Inventory should wait for VRChat PlayerData restoration before loading saved data.");
        }

        [Test]
        public void Collectible_OnDeserialization_WhenRemoteCollected_DoesNotReportInventory()
        {
            InventorySystem inventory = CreateComponent<InventorySystem>("RemoteCollectInventory");
            SetField(inventory, "_maxSlots", 4);
            InvokePrivate(inventory, "InitializeInventory");

            CollectibleItem collectible = CreateComponent<CollectibleItem>("RemoteCollectible");
            GameObject visual = CreateObject("RemoteCollectibleVisual");
            visual.transform.SetParent(collectible.transform, false);

            SetField(collectible, "_inventorySystem", inventory);
            SetField(collectible, "_visualObject", visual);
            SetField(collectible, "_itemId", 2);
            SetField(collectible, "_isCollected", true);

            InvokePrivate(collectible, "Start");
            collectible.OnDeserialization();

            Assert.IsFalse(visual.activeSelf, "Remote synced collection should hide the collectible.");
            Assert.AreEqual(0, GetFieldValue<int>(inventory, "_totalItemCount"), "Remote deserialization must not credit local inventory.");
            Assert.AreEqual(0, GetFieldValue<int>(inventory, "_totalScore"), "Remote deserialization must not save local progress.");
        }

        [Test]
        public void Collectible_Update_WhenNotOwner_DoesNotRespawnLocally()
        {
            CollectibleItem collectible = CreateComponent<CollectibleItem>("NonOwnerRespawn");
            GameObject visual = CreateObject("NonOwnerRespawnVisual");
            visual.transform.SetParent(collectible.transform, false);

            SetField(collectible, "_visualObject", visual);
            SetField(collectible, "_canRespawn", true);
            SetField(collectible, "_respawnTime", 0.01f);
            SetField(collectible, "_isCollected", true);
            SetField(collectible, "_respawnTimer", 0f);

            InvokePrivate(collectible, "Start");
            InvokePrivate(collectible, "Update");

            Assert.IsTrue(GetFieldValue<bool>(collectible, "_isCollected"), "Non-owners should not mutate synced respawn state.");
            Assert.IsTrue(GetFieldValue<bool>(collectible, "_localCollected"), "Non-owners should keep mirroring collected state.");
            Assert.IsFalse(visual.activeSelf);
        }

        private T CreateComponent<T>(string name) where T : Component
        {
            GameObject go = CreateObject(name);
            return go.AddComponent<T>();
        }

        private GameObject CreateObject(string name)
        {
            GameObject go = new GameObject(name);
            _createdObjects.Add(go);
            return go;
        }

        private static void InvokePrivate(object instance, string methodName)
        {
            InvokePrivate(instance, methodName, null);
        }

        private static void InvokePrivate(object instance, string methodName, params object[] args)
        {
            MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(instance.GetType().Name, methodName);
            }

            method.Invoke(instance, args);
        }

        private static T InvokePrivate<T>(object instance, string methodName)
        {
            MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(instance.GetType().Name, methodName);
            }

            return (T)method.Invoke(instance, null);
        }

        private static T GetFieldValue<T>(object instance, string fieldName)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new MissingFieldException(instance.GetType().Name, fieldName);
            }

            return (T)field.GetValue(instance);
        }

        private static void SetField(object instance, string fieldName, object value)
        {
            FieldInfo field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
            {
                throw new MissingFieldException(instance.GetType().Name, fieldName);
            }

            field.SetValue(instance, value);
        }
    }
}
