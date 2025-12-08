
using System.Collections.Generic;
using UnityEngine;
using System.IO;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace UdonSharp.Updater
{
    internal class UdonSharpDataLocator : ScriptableObject
    {
        private const string DEFAULT_DATA_PATH = "Assets/UdonSharp/UdonSharpDataLocator.asset";

        private static string _cachedDataLocation;

        public static string DataPath
        {
            get
            {
            #if UNITY_EDITOR
                if (_cachedDataLocation != null)
                    return _cachedDataLocation;

                string[] foundLocatorGuids = AssetDatabase.FindAssets($"t:{nameof(UdonSharpDataLocator)}");
                List<UdonSharpDataLocator> foundLocators = new List<UdonSharpDataLocator>();

                foreach (string locatorGuid in foundLocatorGuids)
                {
                    UdonSharpDataLocator locator =
                        AssetDatabase.LoadAssetAtPath<UdonSharpDataLocator>(AssetDatabase.GUIDToAssetPath(locatorGuid));

                    if (locator)
                        foundLocators.Add(locator);
                }

                if (foundLocators.Count > 1)
                    throw new System.Exception(
                        "Multiple UdonSharp data locators found, make sure you do not have multiple installations of UdonSharp and have not duplicated any UdonSharp directories");

                if (foundLocators.Count == 0)
                    foundLocators.Add(InitializeUdonSharpData());

                _cachedDataLocation = Path.GetDirectoryName(AssetDatabase.GetAssetPath(foundLocators[0]));
                return _cachedDataLocation;
            #else
                throw new System.PlatformNotSupportedException("Cannot get UdonSharp data path outside of the Editor runtime");
            #endif
            }
        }

#if UNITY_EDITOR
        private static UdonSharpDataLocator InitializeUdonSharpData()
        {
            if (!AssetDatabase.IsValidFolder(Path.GetDirectoryName(DEFAULT_DATA_PATH)))
                AssetDatabase.CreateFolder("Assets", "UdonSharp");
            
            UdonSharpDataLocator locator = CreateInstance<UdonSharpDataLocator>();
            AssetDatabase.CreateAsset(locator, DEFAULT_DATA_PATH);

            Debug.Log("Created UdonSharp data directory", locator);

            AssetDatabase.Refresh();

            return locator;
        }
    #endif
    }

#if UNITY_EDITOR
    [CustomEditor(typeof(UdonSharpDataLocator))]
    internal class UdonSharpDataLocatorEditor : Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox("Do not delete this file! This is used by UdonSharp to locate its data directory.", MessageType.Error);
        }
    }
 
    internal class InitUSharpDataOnImport : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            if (importedAssets.Length <= 0) return;
            
            try
            {
                foreach (string importedAsset in importedAssets)
                {
                    if (Path.GetFileName(importedAsset) == "UdonSharpLocator.asset" && 
                        AssetDatabase.LoadAssetAtPath<UdonSharpLocator>(importedAsset))
                    {
                        string _ = UdonSharpDataLocator.DataPath; // Implicitly initializes the data asset if it doesn't exist
                    }
                }
            }
            catch (System.Exception e)
            {
                Debug.LogError(e);
            }
        }
    }
#endif
}
