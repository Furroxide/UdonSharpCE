using System;
using System.Collections.Generic;
using System.Reflection;
using CEShowcase.Station6_Procgen;
using NUnit.Framework;
using UnityEngine;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// The four Station 6 dig sites shipped with every serialized field left blank -- empty loot table,
    /// empty weights, no renderer, no light, no feedback text, and the same _digSiteId on all four. The
    /// result was an interaction that looked live and did nothing: the prompt said "Dig for Treasure!",
    /// the click rolled "Nothing", and the world was bit-for-bit identical afterwards except that the site
    /// had quietly switched its own interaction off.
    ///
    /// Nothing failed loudly, which is exactly why these tests exist. They pin the contract that a dig site
    /// with no inspector wiring at all still produces loot, still changes what the player sees, and still
    /// agrees with Station 5 about how rare a treasure is.
    /// </summary>
    public class Station6DigSiteRegressionTests
    {
        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private readonly List<UnityEngine.Object> _createdAssets = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
                }
            }

            for (int i = _createdAssets.Count - 1; i >= 0; i--)
            {
                if (_createdAssets[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_createdAssets[i]);
                }
            }

            _createdObjects.Clear();
            _createdAssets.Clear();
        }

        // -----------------------------------------------------------------------------------------
        // The original defect: a completely unwired site must still work
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Start_WhenSceneLeavesLootTableEmpty_StillRollsRealLoot()
        {
            DigSite digSite = CreateUnwiredDigSite();

            InvokePrivate(digSite, "Start");

            string[] lootTable = GetFieldValue<string[]>(digSite, "_lootTable");
            Assert.That(lootTable, Is.Not.Null.And.Not.Empty,
                "An unwired dig site must fall back to a default loot table the way ProcgenDemo does. " +
                "With an empty table RollLoot returns \"Nothing\" and every dig is a no-op.");

            for (int i = 0; i < 50; i++)
            {
                Assert.That(InvokePrivate<string>(digSite, "RollLoot"), Is.Not.EqualTo("Nothing"),
                    "RollLoot must never return the empty-table sentinel after Start.");
            }
        }

        [Test]
        public void Start_WhenLootWeightsUnset_ProducesWeightsThatMatchTheTable()
        {
            DigSite digSite = CreateUnwiredDigSite();

            InvokePrivate(digSite, "Start");

            string[] lootTable = GetFieldValue<string[]>(digSite, "_lootTable");
            float[] lootWeights = GetFieldValue<float[]>(digSite, "_lootWeights");

            Assert.That(lootWeights, Is.Not.Null.And.Not.Empty);
            Assert.That(lootWeights.Length, Is.EqualTo(lootTable.Length),
                "Mismatched lengths silently drop RollLoot to its equal-weights path, which throws away " +
                "the entire rarity curve without reporting anything.");
        }

        [Test]
        public void Start_WhenGlowRendererUnassigned_FallsBackToOwnRenderer()
        {
            DigSite digSite = CreateUnwiredDigSite();

            InvokePrivate(digSite, "Start");

            Assert.That(GetFieldValue<Renderer>(digSite, "_glowRenderer"),
                Is.SameAs(digSite.GetComponent<MeshRenderer>()),
                "A dig site is its own glow source; without this fallback a dug site never changes colour.");
        }

        [Test]
        public void Start_WhenPointLightUnassigned_FallsBackToChildLight()
        {
            DigSite digSite = CreateUnwiredDigSite();
            Light childLight = CreateChildLight(digSite);

            InvokePrivate(digSite, "Start");

            Assert.That(GetFieldValue<Light>(digSite, "_pointLight"), Is.SameAs(childLight));
        }

        [Test]
        public void Start_WhenInspectorWiringExists_DoesNotOverrideIt()
        {
            DigSite digSite = CreateUnwiredDigSite();
            string[] authored = { "Cursed Idol" };
            SetField(digSite, "_lootTable", authored);

            InvokePrivate(digSite, "Start");

            Assert.That(GetFieldValue<string[]>(digSite, "_lootTable"), Is.SameAs(authored),
                "The fallback fills blanks only. Clobbering an authored table would make the scene's own " +
                "configuration unreachable.");
        }

        // -----------------------------------------------------------------------------------------
        // A dig has to change something the player can see
        // -----------------------------------------------------------------------------------------

        [Test]
        public void Dig_DimsGlowAndLight_SoASpentSiteLooksSpent()
        {
            DigSite digSite = CreateUnwiredDigSite();
            Light childLight = CreateChildLight(digSite);
            InvokePrivate(digSite, "Start");

            MeshRenderer renderer = digSite.GetComponent<MeshRenderer>();
            Color freshEmission = AppliedEmission(renderer);
            Assert.That(childLight.intensity, Is.GreaterThan(0f), "Guard: the site should start lit.");

            SetField(digSite, "_isDug", true);
            InvokePrivate(digSite, "UpdateVisualState");

            Assert.That(Luminance(AppliedEmission(renderer)), Is.LessThan(Luminance(freshEmission)),
                "Albedo alone is not the visible state on an emissive accent material -- a spent site whose " +
                "emission stays at full HDR orange glows exactly as brightly as a fresh one.");
            Assert.That(childLight.intensity, Is.EqualTo(0f),
                "The pulse light must go dark when the site is dug.");
        }

        [Test]
        public void Dig_WritesFeedbackNamingTheTreasure()
        {
            DigSite digSite = CreateUnwiredDigSite();
            TMPro.TextMeshProUGUI feedback = CreateFeedbackText();
            SetField(digSite, "_feedbackText", feedback);
            InvokePrivate(digSite, "Start");

            InvokePrivate(digSite, "Dig");

            Assert.That(feedback.text, Is.Not.Null.And.Not.Empty,
                "A dig with nowhere to report its result is indistinguishable from a dig that did nothing.");

            string[] lootTable = GetFieldValue<string[]>(digSite, "_lootTable");
            bool namesATreasure = false;
            for (int i = 0; i < lootTable.Length; i++)
            {
                if (feedback.text.Contains(lootTable[i]))
                {
                    namesATreasure = true;
                    break;
                }
            }

            Assert.That(namesATreasure, Is.True, "Feedback must name the treasure that was actually rolled.");
        }

        [Test]
        public void DugSite_DisablesInteraction_RatherThanBlankingTheInteractionText()
        {
            DigSite digSite = CreateUnwiredDigSite();
            InvokePrivate(digSite, "Start");

            SetField(digSite, "_isDug", true);
            InvokePrivate(digSite, "UpdateVisualState");

            Assert.That(digSite.DisableInteractive, Is.True,
                "An empty InteractionText does not remove the prompt -- VRChat falls back to a bare \"Use\", " +
                "so a spent site kept inviting clicks that Interact() drops on the floor.");
            Assert.That(digSite.InteractionText, Is.Not.Empty);
        }

        [Test]
        public void RespawnedSite_ReturnsToItsPristineGlow_EvenWhenOnEnableRanFirst()
        {
            DigSite digSite = CreateUnwiredDigSite();
            MeshRenderer renderer = digSite.GetComponent<MeshRenderer>();
            Material material = renderer.sharedMaterial;
            Color pristineAlbedo = material.color;
            Color pristineEmission = material.GetColor("_EmissionColor");

            // OnEnable runs before Start on the first activation. If the defaults pass ran only from Start,
            // OnEnable would cache the glow off a null renderer and latch the "already cached" flag, leaving
            // the site permanently dull once it respawned.
            InvokePrivate(digSite, "OnEnable");
            InvokePrivate(digSite, "Start");

            SetField(digSite, "_isDug", true);
            InvokePrivate(digSite, "UpdateVisualState");
            SetField(digSite, "_isDug", false);
            InvokePrivate(digSite, "UpdateVisualState");

            Assert.That(AppliedAlbedo(renderer), Is.EqualTo(pristineAlbedo));
            Assert.That(AppliedEmission(renderer), Is.EqualTo(pristineEmission));
            Assert.That(material.color, Is.EqualTo(pristineAlbedo),
                "The shared material asset must never be written to -- all four dig sites use it.");
        }

        // -----------------------------------------------------------------------------------------
        // Cross-station agreement: the same treasure cannot be common here and legendary in Station 5
        // -----------------------------------------------------------------------------------------

        [Test]
        public void DefaultLootTable_RarityMatchesStation5InventoryTable()
        {
            // Mirrors InventorySystem's _itemNames/_itemRarities wiring in CEShowcase.unity.
            var station5Rarity = new Dictionary<string, int>
            {
                { "Gold Coin", 0 },
                { "Silver Coin", 0 },
                { "Crystal Shard", 0 },
                { "Ruby", 1 },
                { "Emerald", 1 },
                { "Sapphire", 1 },
                { "Magic Scroll", 1 },
                { "Ancient Key", 2 },
                { "Diamond", 2 },
                { "Dragon Scale", 2 },
            };

            DigSite digSite = CreateUnwiredDigSite();
            InvokePrivate(digSite, "Start");

            string[] lootTable = GetFieldValue<string[]>(digSite, "_lootTable");
            MethodInfo determineRarity = typeof(DigSite).GetMethod(
                "DetermineRarity", BindingFlags.Instance | BindingFlags.NonPublic);

            for (int i = 0; i < lootTable.Length; i++)
            {
                string treasure = lootTable[i];
                Assert.That(station5Rarity.ContainsKey(treasure), Is.True,
                    $"'{treasure}' is not one of Station 5's items, so a dig would hand the player a " +
                    "treasure the inventory panel has never heard of.");

                Assert.That((int)determineRarity.Invoke(digSite, new object[] { treasure }),
                    Is.EqualTo(station5Rarity[treasure]),
                    $"DigSite and InventorySystem disagree about how rare '{treasure}' is.");
            }
        }

        [Test]
        public void DetermineRarity_ForEmptyOrSentinelTreasure_ReportsNoFind()
        {
            DigSite digSite = CreateUnwiredDigSite();
            MethodInfo determineRarity = typeof(DigSite).GetMethod(
                "DetermineRarity", BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That((int)determineRarity.Invoke(digSite, new object[] { "Nothing" }), Is.EqualTo(-1));
            Assert.That((int)determineRarity.Invoke(digSite, new object[] { "" }), Is.EqualTo(-1));
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A dig site exactly as the scene shipped it: a mesh, a collider and the behaviour, with every
        /// serialized field left at its default.
        /// </summary>
        private DigSite CreateUnwiredDigSite()
        {
            GameObject go = new GameObject("DigSiteUnderTest");
            _createdObjects.Add(go);

            // A throwaway material, so nothing here writes through to a shared project asset -- in edit mode
            // Renderer.material returns the shared material rather than instantiating a copy.
            Material material = new Material(Shader.Find("Standard"));
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", new Color(1.5f, 0.8f, 0f, 1.5f));
            material.color = new Color(1f, 0.533f, 0f, 1f);
            _createdAssets.Add(material);

            go.AddComponent<MeshFilter>();
            go.AddComponent<MeshRenderer>().sharedMaterial = material;

            return go.AddComponent<DigSite>();
        }

        private Light CreateChildLight(DigSite digSite)
        {
            GameObject go = new GameObject("Glow");
            go.transform.SetParent(digSite.transform, false);

            Light light = go.AddComponent<Light>();
            light.type = LightType.Point;
            light.intensity = 1.8f;
            light.range = 3f;
            return light;
        }

        private TMPro.TextMeshProUGUI CreateFeedbackText()
        {
            GameObject go = new GameObject("Feedback");
            _createdObjects.Add(go);
            return go.AddComponent<TMPro.TextMeshProUGUI>();
        }

        /// <summary>The albedo the behaviour actually pushed to this renderer's property block.</summary>
        private static Color AppliedAlbedo(Renderer renderer)
        {
            return AppliedColor(renderer, "_Color");
        }

        private static Color AppliedEmission(Renderer renderer)
        {
            return AppliedColor(renderer, "_EmissionColor");
        }

        private static Color AppliedColor(Renderer renderer, string property)
        {
            MaterialPropertyBlock properties = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(properties);
            return properties.GetColor(property);
        }

        private static float Luminance(Color color)
        {
            return color.r * 0.2126f + color.g * 0.7152f + color.b * 0.0722f;
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
