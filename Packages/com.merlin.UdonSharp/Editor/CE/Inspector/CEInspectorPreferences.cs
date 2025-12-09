using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// CE Inspector preferences in Unity Preferences window.
    /// Accessible via Edit > Preferences > UdonSharp CE > Inspector
    /// </summary>
    public class CEInspectorPreferences : SettingsProvider
    {
        private static class Styles
        {
            public static readonly GUIContent HideUdonBehavioursLabel = new GUIContent(
                "Hide UdonBehaviour Components",
                "When enabled, the raw UdonBehaviour component is hidden when a UdonSharp proxy behaviour exists. " +
                "Note: Currently controlled by UDONSHARP_DEBUG define. This setting is for future use."
            );

            public static readonly GUIContent ShowOptimizationInfoLabel = new GUIContent(
                "Show Optimization Info",
                "Display CE optimization details in the inspector, showing what optimizations were applied during compilation."
            );

            public static readonly GUIContent AutoGroupPropertiesLabel = new GUIContent(
                "Auto-Group Properties",
                "Automatically group related properties together based on type and attributes (e.g., References, Configuration, Synced Variables)."
            );

            public static readonly GUIContent UseCEInspectorLabel = new GUIContent(
                "Use CE Inspector",
                "Use the CE-enhanced inspector as the default for UdonSharpBehaviours. " +
                "Disable to use the standard UdonSharp inspector."
            );
        }

        public CEInspectorPreferences()
            : base("Preferences/UdonSharp CE/Inspector", SettingsScope.User)
        {
            keywords = new HashSet<string>(new[]
            {
                "UdonSharp", "CE", "Inspector", "Optimization", "Properties", "Udon"
            });
        }

        [SettingsProvider]
        public static SettingsProvider CreateSettingsProvider()
        {
            return new CEInspectorPreferences();
        }

        public override void OnGUI(string searchContext)
        {
            EditorGUILayout.Space(10);

            // Header
            EditorGUILayout.LabelField("CE Inspector Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            using (new EditorGUI.IndentLevelScope())
            {
                // Use CE Inspector toggle
                EditorGUI.BeginChangeCheck();
                bool useCE = EditorGUILayout.Toggle(Styles.UseCEInspectorLabel, CEInspectorBootstrap.UseCEInspector);
                if (EditorGUI.EndChangeCheck())
                {
                    CEInspectorBootstrap.UseCEInspector = useCE;
                    CEInspectorBootstrap.RefreshAllInspectors();
                }

                EditorGUILayout.Space(5);

                // Show optimization info toggle
                EditorGUI.BeginDisabledGroup(!CEInspectorBootstrap.UseCEInspector);
                {
                    CEInspectorBootstrap.ShowOptimizationInfo = EditorGUILayout.Toggle(
                        Styles.ShowOptimizationInfoLabel,
                        CEInspectorBootstrap.ShowOptimizationInfo
                    );

                    // Auto-group properties toggle
                    CEInspectorBootstrap.AutoGroupProperties = EditorGUILayout.Toggle(
                        Styles.AutoGroupPropertiesLabel,
                        CEInspectorBootstrap.AutoGroupProperties
                    );
                }
                EditorGUI.EndDisabledGroup();
            }

            EditorGUILayout.Space(15);

            // Component Visibility section
            EditorGUILayout.LabelField("Component Visibility", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            using (new EditorGUI.IndentLevelScope())
            {
                // Hide UdonBehaviour toggle (informational - controlled by UDONSHARP_DEBUG)
                EditorGUI.BeginDisabledGroup(true);
                {
                    bool hideUdon = CEInspectorBootstrap.HideUdonBehaviours;
                    EditorGUILayout.Toggle(Styles.HideUdonBehavioursLabel, hideUdon);
                }
                EditorGUI.EndDisabledGroup();

                EditorGUILayout.HelpBox(
                    "UdonBehaviour visibility is controlled by the UDONSHARP_DEBUG scripting define. " +
                    "Define UDONSHARP_DEBUG in Player Settings to show backing UdonBehaviours.",
                    MessageType.Info
                );
            }

            EditorGUILayout.Space(15);

            // Actions section
            EditorGUILayout.LabelField("Actions", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("Refresh Inspectors", GUILayout.Width(150)))
                    {
                        CEInspectorBootstrap.RefreshAllInspectors();
                    }

                    if (GUILayout.Button("Reset to Defaults", GUILayout.Width(150)))
                    {
                        if (EditorUtility.DisplayDialog(
                            "Reset CE Inspector Preferences",
                            "Are you sure you want to reset all CE Inspector preferences to their default values?",
                            "Reset", "Cancel"))
                        {
                            CEInspectorBootstrap.ResetToDefaults();
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(5);

                if (GUILayout.Button("Open Optimization Report Window", GUILayout.Width(250)))
                {
                    CEInspectorMenu.OpenOptimizationReport();
                }
            }

            EditorGUILayout.Space(15);

            // About section
            EditorGUILayout.LabelField("About", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            using (new EditorGUI.IndentLevelScope())
            {
                var aboutStyle = new GUIStyle(EditorStyles.label)
                {
                    wordWrap = true
                };
                aboutStyle.normal.textColor = new Color(0.6f, 0.6f, 0.6f);

                EditorGUILayout.LabelField(CEInspectorBootstrap.GetVersionString(), aboutStyle);
                EditorGUILayout.LabelField(
                    "The CE Inspector provides a clean, branded editor experience for UdonSharpCE " +
                    "behaviours with optimization visibility and progressive disclosure.",
                    aboutStyle
                );
            }
        }
    }
}

