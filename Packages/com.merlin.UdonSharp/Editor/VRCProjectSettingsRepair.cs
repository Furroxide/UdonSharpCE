using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UdonSharp;
using UnityEditor;
using UnityEngine;

namespace UdonSharpEditor
{
    [InitializeOnLoad]
    public static class VRCProjectSettingsRepair
    {
        private const string ProjectSettingsPrefabPath = "Packages/com.vrchat.base/Runtime/VRCSDK/Dependencies/VRChat/Resources/VRCProjectSettings.prefab";
        private const int CollisionMatrixWidth = 32;
        private const int CollisionMatrixHeight = 32;
        private const int ExpectedCollisionEntryCount = CollisionMatrixWidth * CollisionMatrixHeight;

        static VRCProjectSettingsRepair()
        {
            EditorApplication.delayCall += TryRepairOnLoad;
        }

        private static void TryRepairOnLoad()
        {
            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorApplication.delayCall += TryRepairOnLoad;
                return;
            }

            try
            {
                TryRepairSettingsAsset();
            }
            catch (Exception ex)
            {
                UdonSharpCEUtils.LogWarning($"Failed to repair VRChat project settings asset: {ex.Message}");
            }
        }

        public static void VerifyRepair()
        {
            if (!TryRepairSettingsAsset())
            {
                ValidateSettingsAsset();
                return;
            }

            ValidateSettingsAsset();
        }

        private static bool TryRepairSettingsAsset()
        {
            if (!File.Exists(ProjectSettingsPrefabPath))
                return false;

            Type settingsType = FindType("VRCProjectSettings");
            if (settingsType == null)
                return false;

            GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectSettingsPrefabPath);
            if (prefabRoot == null)
                return false;

            Component settingsComponent = prefabRoot.GetComponent(settingsType);
            if (settingsComponent == null)
                return false;

            if (HasValidCollisionArray(settingsType, settingsComponent))
                return false;

            if (!TryReadSerializedSettings(ProjectSettingsPrefabPath, out SerializedProjectSettingsData serializedData))
                throw new InvalidOperationException($"Could not decode '{ProjectSettingsPrefabPath}'.");

            bool changed = false;
            changed |= SetFieldIfDifferent(settingsType, settingsComponent, "layers", serializedData.Layers);
            changed |= SetFieldIfDifferent(settingsType, settingsComponent, "numLayers", serializedData.NumLayers);
            changed |= SetFieldIfDifferent(settingsType, settingsComponent, "layerCollisionArr", serializedData.LayerCollisionArray);
            changed |= SetFieldIfDifferent(settingsType, settingsComponent, "queriesHitTriggers", serializedData.QueriesHitTriggers);

            if (!changed)
            {
                ClearUpdateLayersCache();
                return true;
            }

            EditorUtility.SetDirty(settingsComponent);
            EditorUtility.SetDirty(prefabRoot);
            PrefabUtility.SavePrefabAsset(prefabRoot);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(ProjectSettingsPrefabPath, ImportAssetOptions.ForceUpdate);
            ClearUpdateLayersCache();

            UdonSharpCEUtils.Log("Repaired VRChat project settings collision matrix serialization.");
            return true;
        }

        private static void ValidateSettingsAsset()
        {
            Type settingsType = FindType("VRCProjectSettings");
            Type updateLayersType = FindType("UpdateLayers");
            if (settingsType == null || updateLayersType == null)
                throw new InvalidOperationException("Could not locate VRChat editor types for validation.");

            GameObject prefabRoot = AssetDatabase.LoadAssetAtPath<GameObject>(ProjectSettingsPrefabPath);
            if (prefabRoot == null)
                throw new InvalidOperationException($"Could not load '{ProjectSettingsPrefabPath}'.");

            Component settingsComponent = prefabRoot.GetComponent(settingsType);
            if (settingsComponent == null)
                throw new InvalidOperationException("Could not locate VRCProjectSettings component on the prefab.");

            FieldInfo collisionField = settingsType.GetField("layerCollisionArr", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            bool[] collisionArray = collisionField?.GetValue(settingsComponent) as bool[];
            if (collisionArray == null || collisionArray.Length != ExpectedCollisionEntryCount)
                throw new InvalidOperationException($"VRCProjectSettings.layerCollisionArr has invalid length '{collisionArray?.Length ?? 0}'.");

            ClearUpdateLayersCache();
            MethodInfo validationMethod = updateLayersType.GetMethod("IsCollisionLayerMatrixSetup", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            _ = validationMethod?.Invoke(null, null);
        }

        private static bool HasValidCollisionArray(Type settingsType, Component settingsComponent)
        {
            FieldInfo collisionField = settingsType.GetField("layerCollisionArr", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            bool[] collisionArray = collisionField?.GetValue(settingsComponent) as bool[];
            return collisionArray != null && collisionArray.Length == ExpectedCollisionEntryCount;
        }

        private static bool SetFieldIfDifferent(Type settingsType, Component settingsComponent, string fieldName, object value)
        {
            FieldInfo field = settingsType.GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                return false;

            object currentValue = field.GetValue(settingsComponent);
            if (AreEqual(currentValue, value))
                return false;

            field.SetValue(settingsComponent, value);
            return true;
        }

        private static bool AreEqual(object currentValue, object newValue)
        {
            if (ReferenceEquals(currentValue, newValue))
                return true;

            if (currentValue is bool[] currentBools && newValue is bool[] newBools)
                return HaveSameContents(currentBools, newBools);

            if (currentValue is string[] currentStrings && newValue is string[] newStrings)
                return HaveSameContents(currentStrings, newStrings);

            return Equals(currentValue, newValue);
        }

        private static bool HaveSameContents<T>(T[] current, T[] updated)
        {
            if (current == null || updated == null)
                return current == updated;

            if (current.Length != updated.Length)
                return false;

            for (int i = 0; i < current.Length; ++i)
            {
                if (!Equals(current[i], updated[i]))
                    return false;
            }

            return true;
        }

        private static bool TryReadSerializedSettings(string assetPath, out SerializedProjectSettingsData settingsData)
        {
            settingsData = default;

            string[] lines = File.ReadAllLines(assetPath);
            List<string> layers = new List<string>();
            bool readingLayers = false;
            int numLayers = 0;
            bool numLayersFound = false;
            string packedCollisionArray = null;
            bool queriesHitTriggers = false;
            bool queriesHitTriggersFound = false;

            foreach (string rawLine in lines)
            {
                string trimmedLine = rawLine.Trim();

                if (trimmedLine == "layers:")
                {
                    readingLayers = true;
                    continue;
                }

                if (readingLayers)
                {
                    if (trimmedLine.StartsWith("- ", StringComparison.Ordinal))
                    {
                        layers.Add(trimmedLine.Substring(2));
                        continue;
                    }

                    readingLayers = false;
                }

                if (trimmedLine.StartsWith("numLayers: ", StringComparison.Ordinal))
                {
                    numLayersFound = int.TryParse(trimmedLine.Substring("numLayers: ".Length), out numLayers);
                    continue;
                }

                if (trimmedLine.StartsWith("layerCollisionArr: ", StringComparison.Ordinal))
                {
                    packedCollisionArray = trimmedLine.Substring("layerCollisionArr: ".Length).Trim();
                    continue;
                }

                if (trimmedLine.StartsWith("queriesHitTriggers: ", StringComparison.Ordinal))
                {
                    string value = trimmedLine.Substring("queriesHitTriggers: ".Length).Trim();
                    queriesHitTriggersFound = TryParseBool(value, out queriesHitTriggers);
                }
            }

            bool[] decodedCollisionArray = DecodePackedBoolArray(packedCollisionArray);
            if (!numLayersFound || decodedCollisionArray == null || decodedCollisionArray.Length != ExpectedCollisionEntryCount)
                return false;

            settingsData = new SerializedProjectSettingsData(
                layers.ToArray(),
                numLayers,
                decodedCollisionArray,
                queriesHitTriggersFound && queriesHitTriggers);
            return true;
        }

        private static bool[] DecodePackedBoolArray(string packedValues)
        {
            if (string.IsNullOrEmpty(packedValues) || (packedValues.Length & 1) != 0)
                return null;

            bool[] decodedValues = new bool[packedValues.Length / 2];

            // Unity serialized this bool[] as raw bytes encoded in hex, using 00/01 pairs.
            for (int i = 0; i < decodedValues.Length; ++i)
            {
                int highNibble = ParseHexDigit(packedValues[i * 2]);
                int lowNibble = ParseHexDigit(packedValues[i * 2 + 1]);
                if (highNibble < 0 || lowNibble < 0)
                    return null;

                decodedValues[i] = ((highNibble << 4) | lowNibble) != 0;
            }

            return decodedValues;
        }

        private static int ParseHexDigit(char value)
        {
            if (value >= '0' && value <= '9')
                return value - '0';

            if (value >= 'a' && value <= 'f')
                return value - 'a' + 10;

            if (value >= 'A' && value <= 'F')
                return value - 'A' + 10;

            return -1;
        }

        private static bool TryParseBool(string rawValue, out bool parsedValue)
        {
            if (bool.TryParse(rawValue, out parsedValue))
                return true;

            if (rawValue == "0")
            {
                parsedValue = false;
                return true;
            }

            if (rawValue == "1")
            {
                parsedValue = true;
                return true;
            }

            parsedValue = false;
            return false;
        }

        private static void ClearUpdateLayersCache()
        {
            Type updateLayersType = FindType("UpdateLayers");
            FieldInfo cachedSettingsField = updateLayersType?.GetField("_projectSettings", BindingFlags.Static | BindingFlags.NonPublic);
            cachedSettingsField?.SetValue(null, null);
        }

        private static Type FindType(string typeName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; ++i)
            {
                Type type = assemblies[i].GetType(typeName);
                if (type != null)
                    return type;
            }

            return null;
        }

        private readonly struct SerializedProjectSettingsData
        {
            public string[] Layers { get; }
            public int NumLayers { get; }
            public bool[] LayerCollisionArray { get; }
            public bool QueriesHitTriggers { get; }

            public SerializedProjectSettingsData(string[] layers, int numLayers, bool[] layerCollisionArray, bool queriesHitTriggers)
            {
                Layers = layers;
                NumLayers = numLayers;
                LayerCollisionArray = layerCollisionArray;
                QueriesHitTriggers = queriesHitTriggers;
            }
        }
    }
}
