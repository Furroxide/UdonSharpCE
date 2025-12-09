using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// CE-enhanced custom inspector for UdonSharpBehaviour subclasses.
    /// Provides a clean, branded editor experience with optimization visibility.
    /// 
    /// Register this as the default inspector using:
    /// [assembly: DefaultUdonSharpBehaviourEditor(typeof(CEBehaviourEditor), "CE Inspector")]
    /// </summary>
    public class CEBehaviourEditor : UnityEditor.Editor
    {
        // ═══════════════════════════════════════════════════════════════
        // CACHED DATA
        // ═══════════════════════════════════════════════════════════════

        private UdonBehaviour _backingBehaviour;
        private CEOptimizationReport _optimizationReport;
        private PropertyGroup[] _propertyGroups;
        private SyncInfo _syncInfo;
        private CEBadge[] _badges;
        private bool _isSetup;

        // Static foldout state storage
        private static readonly Dictionary<string, bool> _foldoutStates = new Dictionary<string, bool>();

        // ═══════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ═══════════════════════════════════════════════════════════════

        protected virtual void OnEnable()
        {
            if (target == null) return;

            try
            {
                SetupInspector();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CE Inspector] Failed to setup inspector for {target}: {ex.Message}");
                _isSetup = false;
            }
        }

        private void SetupInspector()
        {
            _isSetup = false;

            // Get backing UdonBehaviour
            if (target is UdonSharpBehaviour usb)
            {
                _backingBehaviour = UdonSharpEditorUtility.GetBackingUdonBehaviour(usb);
            }

            // Get optimization report
            _optimizationReport = CEOptimizationRegistry.GetReport(target);

            // Analyze sync info
            _syncInfo = AnalyzeSyncInfo();

            // Group properties if enabled
            if (CEInspectorBootstrap.AutoGroupProperties)
            {
                _propertyGroups = GroupProperties(serializedObject);
            }

            // Determine badges
            _badges = DetermineBadges();

            _isSetup = true;
        }

        // ═══════════════════════════════════════════════════════════════
        // MAIN DRAWING
        // ═══════════════════════════════════════════════════════════════

        public override void OnInspectorGUI()
        {
            // Ensure setup
            if (!_isSetup || target == null)
            {
                DrawFallbackInspector();
                return;
            }

            serializedObject.Update();

            try
            {
                // Draw default U# header (sync settings, interact, etc.) 
                if (UdonSharpGUI.DrawDefaultUdonSharpBehaviourHeader(target, true, false))
                {
                    return;
                }

                // Draw CE header with badges
                CEInspectorRenderer.DrawHeader(target.GetType().Name, _badges);

                // Draw status bar
                CEInspectorRenderer.DrawStatusBar(_syncInfo, _optimizationReport);

                EditorGUILayout.Space(4);

                // Draw properties (grouped or default)
                if (CEInspectorBootstrap.AutoGroupProperties && _propertyGroups != null && _propertyGroups.Length > 0)
                {
                    DrawGroupedProperties();
                }
                else
                {
                    DrawDefaultProperties();
                }

                EditorGUILayout.Space(4);

                // Draw optimization panel
                if (CEInspectorBootstrap.ShowOptimizationInfo && _optimizationReport != null)
                {
                    CEInspectorRenderer.DrawOptimizationPanel(_optimizationReport);
                }

                // Draw advanced section
                DrawAdvancedSection();
            }
            catch (Exception ex)
            {
                EditorGUILayout.HelpBox($"CE Inspector error: {ex.Message}", MessageType.Error);
                DrawFallbackInspector();
            }

            serializedObject.ApplyModifiedProperties();
        }

        private void DrawFallbackInspector()
        {
            EditorGUILayout.HelpBox("CE Inspector could not be initialized. Showing default inspector.", MessageType.Warning);
            base.OnInspectorGUI();
        }

        // ═══════════════════════════════════════════════════════════════
        // PROPERTY DRAWING
        // ═══════════════════════════════════════════════════════════════

        private void DrawGroupedProperties()
        {
            foreach (var group in _propertyGroups)
            {
                if (group.Properties.Count == 0) continue;

                bool isCollapsible = group.Properties.Count > 2 || group.ForceCollapsible;

                if (isCollapsible)
                {
                    string key = $"{target.GetType().Name}_{group.Name}";
                    if (!_foldoutStates.TryGetValue(key, out bool expanded))
                    {
                        expanded = group.DefaultExpanded;
                        _foldoutStates[key] = expanded;
                    }

                    expanded = CEInspectorRenderer.DrawGroupHeader(
                        group.Name,
                        expanded,
                        group.Properties.Count
                    );
                    _foldoutStates[key] = expanded;

                    if (expanded)
                    {
                        EditorGUI.indentLevel++;
                        CEInspectorRenderer.BeginGroupBox();
                        DrawPropertiesInGroup(group);
                        CEInspectorRenderer.EndGroupBox();
                        EditorGUI.indentLevel--;
                    }
                }
                else
                {
                    // Draw without collapsing for small groups
                    if (group.Name != "General")
                    {
                        EditorGUILayout.LabelField(group.Name, EditorStyles.boldLabel);
                    }
                    DrawPropertiesInGroup(group);
                }

                EditorGUILayout.Space(2);
            }
        }

        private void DrawPropertiesInGroup(PropertyGroup group)
        {
            foreach (var prop in group.Properties)
            {
                EditorGUILayout.PropertyField(prop, true);
            }
        }

        private void DrawDefaultProperties()
        {
            var iterator = serializedObject.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;

                // Skip script reference
                if (iterator.name == "m_Script") continue;

                // Skip backing field
                if (iterator.name == UdonSharpEditorUtility.BackingFieldName) continue;

                EditorGUILayout.PropertyField(iterator, true);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ADVANCED SECTION
        // ═══════════════════════════════════════════════════════════════

        private void DrawAdvancedSection()
        {
            string key = $"{target.GetType().Name}_CEAdvanced";
            if (!_foldoutStates.TryGetValue(key, out bool expanded))
            {
                expanded = false;
                _foldoutStates[key] = expanded;
            }

            expanded = CEInspectorRenderer.DrawGroupHeader("Advanced", expanded, -1);
            _foldoutStates[key] = expanded;

            if (!expanded) return;

            EditorGUI.indentLevel++;
            CEInspectorRenderer.BeginGroupBox();

            // Debug buttons
            EditorGUILayout.BeginHorizontal();
            {
                if (GUILayout.Button("View Optimization Report"))
                {
                    ShowOptimizationReportWindow();
                }

                EditorGUI.BeginDisabledGroup(_backingBehaviour == null);
                if (GUILayout.Button("Select Backing Udon"))
                {
                    if (_backingBehaviour != null)
                    {
                        Selection.activeObject = _backingBehaviour;
                    }
                }
                EditorGUI.EndDisabledGroup();
            }
            EditorGUILayout.EndHorizontal();

            // Show some debug info
            EditorGUI.BeginDisabledGroup(true);
            {
                if (_backingBehaviour != null)
                {
                    EditorGUILayout.ObjectField("Backing Behaviour", _backingBehaviour, typeof(UdonBehaviour), true);
                    
                    var programAsset = _backingBehaviour.programSource as UdonSharpProgramAsset;
                    if (programAsset != null)
                    {
                        EditorGUILayout.ObjectField("Program Asset", programAsset, typeof(UdonSharpProgramAsset), false);
                        EditorGUILayout.ObjectField("Script", programAsset.sourceCsScript, typeof(MonoScript), false);
                    }
                }
            }
            EditorGUI.EndDisabledGroup();

            CEInspectorRenderer.EndGroupBox();
            EditorGUI.indentLevel--;
        }

        private void ShowOptimizationReportWindow()
        {
            // Try to open the optimization report window if it exists
            var windowType = Type.GetType("UdonSharp.CE.Editor.Optimizers.OptimizationReportWindow, UdonSharp.CE.Editor");
            if (windowType != null)
            {
                EditorWindow.GetWindow(windowType, false, "CE Optimization Report");
            }
            else
            {
                // Fallback: log report to console
                if (_optimizationReport != null && _optimizationReport.AnyOptimizationsApplied)
                {
                    Debug.Log($"[CE] Optimization Report for {target.GetType().Name}:\n" +
                              $"  Constants Folded: {_optimizationReport.ConstantsFolded}\n" +
                              $"  Loops Unrolled: {_optimizationReport.LoopsUnrolled}\n" +
                              $"  Methods Inlined: {_optimizationReport.MethodsInlined}\n" +
                              $"  Strings Interned: {_optimizationReport.StringsInterned}\n" +
                              $"  Dead Code Eliminated: {_optimizationReport.DeadCodeEliminated}");
                }
                else
                {
                    Debug.Log($"[CE] No optimizations applied to {target.GetType().Name}");
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // ANALYSIS METHODS
        // ═══════════════════════════════════════════════════════════════

        private SyncInfo AnalyzeSyncInfo()
        {
            var info = new SyncInfo();

            if (_backingBehaviour != null)
            {
                info.Mode = _backingBehaviour.SyncMethod;
            }

            // Count synced fields
            var fields = target.GetType().GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var field in fields)
            {
                if (field.GetCustomAttribute<UdonSyncedAttribute>() != null)
                {
                    info.SyncedFieldCount++;
                    info.EstimatedBytesPerSync += EstimateFieldSize(field.FieldType);
                }
            }

            // Apply optimization adjustments (stubbed for now)
            if (_optimizationReport != null && _optimizationReport.SyncPackingApplied)
            {
                info.OptimizedFieldCount = _optimizationReport.PackedSyncVars;
                info.OptimizedBytesPerSync = _optimizationReport.OptimizedBytesPerSync;
            }

            return info;
        }

        private int EstimateFieldSize(Type fieldType)
        {
            // Rough byte size estimates for common types
            if (fieldType == typeof(bool)) return 1;
            if (fieldType == typeof(byte) || fieldType == typeof(sbyte)) return 1;
            if (fieldType == typeof(short) || fieldType == typeof(ushort)) return 2;
            if (fieldType == typeof(int) || fieldType == typeof(uint)) return 4;
            if (fieldType == typeof(long) || fieldType == typeof(ulong)) return 8;
            if (fieldType == typeof(float)) return 4;
            if (fieldType == typeof(double)) return 8;
            if (fieldType == typeof(Vector2)) return 8;
            if (fieldType == typeof(Vector3)) return 12;
            if (fieldType == typeof(Vector4) || fieldType == typeof(Quaternion)) return 16;
            if (fieldType == typeof(Color) || fieldType == typeof(Color32)) return 4;
            if (fieldType == typeof(string)) return 32; // Estimate
            if (fieldType.IsArray) return 64; // Estimate
            return 8; // Default estimate
        }

        private PropertyGroup[] GroupProperties(SerializedObject obj)
        {
            var groups = new Dictionary<string, PropertyGroup>
            {
                ["References"] = new PropertyGroup("References", true, 0),
                ["Configuration"] = new PropertyGroup("Configuration", true, 10),
                ["Synced Variables"] = new PropertyGroup("Synced Variables", false, 20) { ForceCollapsible = true },
                ["Debug Options"] = new PropertyGroup("Debug Options", false, 80) { ForceCollapsible = true },
                ["Internal"] = new PropertyGroup("Internal", false, 90) { ForceCollapsible = true },
                ["General"] = new PropertyGroup("General", true, 50)
            };

            var type = target.GetType();
            var fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .ToDictionary(f => f.Name, f => f);

            var iterator = obj.GetIterator();
            bool enterChildren = true;

            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;
                if (iterator.name == UdonSharpEditorUtility.BackingFieldName) continue;

                fields.TryGetValue(iterator.name, out var field);
                string groupName = DetermineGroup(iterator, field);

                // Handle custom Header attributes
                if (field != null)
                {
                    var header = field.GetCustomAttribute<HeaderAttribute>();
                    if (header != null)
                    {
                        groupName = header.header;
                        if (!groups.ContainsKey(groupName))
                        {
                            groups[groupName] = new PropertyGroup(groupName, true, 30);
                        }
                    }
                }

                if (groups.TryGetValue(groupName, out var group))
                {
                    group.Properties.Add(iterator.Copy());
                }
                else
                {
                    // Create a new group for unknown categories
                    var newGroup = new PropertyGroup(groupName, true, 40);
                    newGroup.Properties.Add(iterator.Copy());
                    groups[groupName] = newGroup;
                }
            }

            return groups.Values
                .Where(g => g.Properties.Count > 0)
                .OrderBy(g => g.Priority)
                .ToArray();
        }

        private string DetermineGroup(SerializedProperty prop, FieldInfo field)
        {
            if (field == null) return "General";

            // Check for UdonSynced
            if (field.GetCustomAttribute<UdonSyncedAttribute>() != null)
                return "Synced Variables";

            // Check name patterns
            if (prop.name.StartsWith("_")) return "Internal";
            if (prop.name.ToLowerInvariant().Contains("debug")) return "Debug Options";

            // Check type - references first
            if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                return "References";

            // Primitive types go to Configuration
            if (field.FieldType.IsPrimitive || 
                field.FieldType == typeof(string) ||
                field.FieldType == typeof(Vector2) ||
                field.FieldType == typeof(Vector3) ||
                field.FieldType == typeof(Vector4) ||
                field.FieldType == typeof(Color) ||
                field.FieldType == typeof(Color32))
                return "Configuration";

            return "General";
        }

        private CEBadge[] DetermineBadges()
        {
            var badges = new List<CEBadge>();
            var type = target.GetType();

            // CE base badge - show optimized if any optimizations were applied
            if (_optimizationReport != null && _optimizationReport.AnyOptimizationsApplied)
            {
                badges.Add(new CEBadge("CE ✓", CEColors.BadgeOptimized, "CE optimizations applied"));
            }
            else
            {
                badges.Add(new CEBadge("CE", CEColors.BadgeCE, "UdonSharpCE managed"));
            }

            // Netcode badge (stubbed - check for attributes that don't exist yet)
            if (HasAttribute<CENetworkedPlayerAttribute>(type) ||
                HasAttribute<CENetworkedProjectileAttribute>(type))
            {
                badges.Add(new CEBadge("Netcode", CEColors.BadgeNetcode, "CE Netcode enabled"));
            }

            // Pooled badge (stubbed)
            if (HasAttribute<CEPooledAttribute>(type))
            {
                badges.Add(new CEBadge("Pooled", CEColors.BadgePooled, "Object pooling enabled"));
            }

            // Predicted badge (stubbed - check fields)
            if (HasPredictedFields(type))
            {
                badges.Add(new CEBadge("Predicted", CEColors.BadgePredicted, "Client prediction enabled"));
            }

            return badges.ToArray();
        }

        private bool HasAttribute<T>(Type type) where T : Attribute
        {
            try
            {
                return type.GetCustomAttribute<T>() != null;
            }
            catch
            {
                return false;
            }
        }

        private bool HasPredictedFields(Type type)
        {
            try
            {
                var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return fields.Any(f => f.GetCustomAttribute<CEPredictedAttribute>() != null);
            }
            catch
            {
                return false;
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // EDITOR OVERRIDES
        // ═══════════════════════════════════════════════════════════════

        public override bool RequiresConstantRepaint()
        {
            return EditorApplication.isPlaying;
        }

        public override bool UseDefaultMargins()
        {
            return true;
        }
    }
}

