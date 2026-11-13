using System;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// Initializes the CE Inspector system on domain reload.
    /// Manages preferences and lifecycle for the inspector components.
    /// </summary>
    [InitializeOnLoad]
    public static class CEInspectorBootstrap
    {
        // ═══════════════════════════════════════════════════════════════
        // PREFERENCE KEYS
        // ═══════════════════════════════════════════════════════════════

        private const string PREF_HIDE_UDON_BEHAVIOURS = "CE_HideUdonBehaviours";
        private const string PREF_SHOW_OPTIMIZATION_INFO = "CE_ShowOptimizationInfo";
        private const string PREF_AUTO_GROUP_PROPERTIES = "CE_AutoGroupProperties";

        // Written by the former "Use CE Inspector" preference, which nothing read. Only deleted now, on reset.
        private const string PREF_USE_CE_INSPECTOR = "CE_UseCEInspector";

        // ═══════════════════════════════════════════════════════════════
        // CONFIGURATION PROPERTIES
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Whether to hide UdonBehaviour components when proxy exists.
        /// Note: This is currently handled by UdonSharp core via UDONSHARP_DEBUG.
        /// This preference is for future user-configurable behavior.
        /// </summary>
        public static bool HideUdonBehaviours
        {
            get => EditorPrefs.GetBool(PREF_HIDE_UDON_BEHAVIOURS, true);
            set => EditorPrefs.SetBool(PREF_HIDE_UDON_BEHAVIOURS, value);
        }

        /// <summary>
        /// Whether to show optimization info in inspector.
        /// </summary>
        public static bool ShowOptimizationInfo
        {
            get => EditorPrefs.GetBool(PREF_SHOW_OPTIMIZATION_INFO, true);
            set => EditorPrefs.SetBool(PREF_SHOW_OPTIMIZATION_INFO, value);
        }

        /// <summary>
        /// Whether to auto-group properties by type/attribute.
        /// </summary>
        public static bool AutoGroupProperties
        {
            get => EditorPrefs.GetBool(PREF_AUTO_GROUP_PROPERTIES, true);
            set => EditorPrefs.SetBool(PREF_AUTO_GROUP_PROPERTIES, value);
        }

        /// <summary>
        /// Whether UdonSharp hands U# behaviours to the CE Inspector by default.
        ///
        /// That is UdonSharp's own project setting, Project Settings > Udon Sharp > Default Behaviour Editor,
        /// and this only reports it. It used to be an EditorPrefs toggle that nothing read: switching it
        /// off changed no inspector, and switching it on did not select the CE Inspector either.
        /// </summary>
        public static bool UseCEInspector
        {
            get
            {
                UdonSharpSettings settings = UdonSharpSettings.GetSettings();

                // The key UdonSharp files a default editor under is the editor type's ToString().
                return settings != null &&
                       string.Equals(settings.defaultBehaviourInterfaceType, typeof(CEBehaviourEditor).ToString(), StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Opens the project settings page where the default U# behaviour editor is chosen.
        /// </summary>
        public static void OpenDefaultEditorSetting()
        {
            SettingsService.OpenProjectSettings("Project/Udon Sharp");
        }

        // ═══════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ═══════════════════════════════════════════════════════════════

        private static bool _initialized;

        static CEInspectorBootstrap()
        {
            // Initialize on next editor update to avoid conflicts during domain reload
            EditorApplication.delayCall += Initialize;
        }

        private static void Initialize()
        {
            if (_initialized) return;

            // EditorStyles can be null during early domain reload - retry later if not ready
            if (!CEStyleCache.AreEditorStylesReady)
            {
                EditorApplication.delayCall += Initialize;
                return;
            }
            
            _initialized = true;

            // Initialize style cache
            CEStyleCache.Initialize();

            // Initialize optimization registry
            CEOptimizationRegistry.Initialize();

            // Register for editor events
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            // Log initialization (only in debug builds)
#if UDONSHARP_DEBUG
            Debug.Log("[CE Inspector] Initialized");
#endif
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            // Clear style cache on play mode changes (can help with style issues)
            if (state == PlayModeStateChange.ExitingEditMode ||
                state == PlayModeStateChange.EnteredEditMode)
            {
                CEStyleCache.ClearCache();
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // UTILITY METHODS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Reset all CE Inspector preferences to defaults.
        /// </summary>
        public static void ResetToDefaults()
        {
            EditorPrefs.DeleteKey(PREF_HIDE_UDON_BEHAVIOURS);
            EditorPrefs.DeleteKey(PREF_SHOW_OPTIMIZATION_INFO);
            EditorPrefs.DeleteKey(PREF_AUTO_GROUP_PROPERTIES);
            EditorPrefs.DeleteKey(PREF_USE_CE_INSPECTOR);

            // Clear caches
            CEStyleCache.ClearCache();
            CEOptimizationRegistry.ClearCache();

            Debug.Log("[CE Inspector] Preferences reset to defaults");
        }

        /// <summary>
        /// Force refresh of all inspectors.
        /// </summary>
        public static void RefreshAllInspectors()
        {
            // Clear caches first
            CEStyleCache.ClearCache();
            CEOptimizationRegistry.ClearCache();

            // Force repaint of all inspector windows
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window.GetType().Name == "InspectorWindow")
                {
                    window.Repaint();
                }
            }
        }

        /// <summary>
        /// Get version string for the CE Inspector system.
        /// </summary>
        public static string GetVersionString()
        {
            return "CE Inspector v1.0.0";
        }
    }
}

