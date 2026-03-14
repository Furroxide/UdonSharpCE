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
            MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(instance.GetType().Name, methodName);
            }

            method.Invoke(instance, null);
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
