# CE Inspector System — Specification Proposal

*A Clean, Informative, CE-Branded Editor Experience*

**Version:** 1.0  
**Date:** December 2025  
**Status:** Specification  

---

## Executive Summary

UdonSharpCE's runtime improvements deserve an editor experience that matches. Currently, the Unity Inspector shows confusing dual-component layouts, scary "Missing Script" warnings, and exposes low-level UdonBehaviour internals that users never need to see.

This specification proposes a **unified CE Inspector system** that:
- Hides implementation complexity
- Presents a single, clean component view
- Shows CE optimization status at a glance
- Provides actionable insights without overwhelming users

---

## Part 1: Problem Analysis

### 1.1 Current Inspector Issues

| Issue | User Impact | Severity |
|-------|-------------|----------|
| Two components visible (proxy + UdonBehaviour) | Confusion about which to edit | High |
| "Missing (Mono Script)" warning | Fear that something is broken | High |
| "Program Source: None" field | Meaningless to users | Medium |
| No CE branding | No indication CE is active | Medium |
| No optimization visibility | Users don't know CE is helping | Low |
| Cluttered layout | Hard to find important fields | Medium |

### 1.2 Current Component Structure

```
GameObject
├── DemoManager : UdonSharpBehaviour (Proxy)
│   ├── Serialized fields (what users care about)
│   ├── "Missing Script" warning (scary, incorrect)
│   └── "Backing Udon Behaviour Dump" (confusing)
│
└── UdonBehaviour (Hidden implementation)
    ├── Synchronization dropdown
    ├── Program Source field
    └── Internal state (users never need this)
```

### 1.3 User Personas

| Persona | Needs | Pain Points |
|---------|-------|-------------|
| **Beginner** | Simple, clear interface | Scared by warnings, confused by two components |
| **Intermediate** | Quick access to sync settings | Has to dig through UdonBehaviour component |
| **Advanced** | Optimization insights, debugging | No visibility into what CE is doing |
| **Team Lead** | Confidence that setup is correct | Can't quickly verify CE is working |

---

## Part 2: Goals & Requirements

### 2.1 Primary Goals

1. **Single Component Appearance** — Users see ONE component per UdonSharp behaviour
2. **Zero Warnings** — No scary messages when everything is working correctly
3. **CE Visibility** — Clear indication that CE enhancements are active
4. **Progressive Disclosure** — Simple by default, details available on demand

### 2.2 Requirements

#### Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-01 | Hide UdonBehaviour component when proxy exists | Must |
| FR-02 | Display all serialized fields from proxy behaviour | Must |
| FR-03 | Show sync mode without exposing UdonBehaviour | Must |
| FR-04 | Display CE optimization status | Should |
| FR-05 | Group related fields automatically | Should |
| FR-06 | Provide access to advanced settings via foldout | Should |
| FR-07 | Support multi-object editing | Must |
| FR-08 | Maintain undo/redo functionality | Must |
| FR-09 | Show netcode status when CE Netcode is used | Could |
| FR-10 | Integrate with CE build reports | Could |

#### Non-Functional Requirements

| ID | Requirement | Priority |
|----|-------------|----------|
| NFR-01 | Inspector renders in <1ms (no frame drops) | Must |
| NFR-02 | Consistent with Unity's visual style | Should |
| NFR-03 | Works with Unity 2019.4 through 2022.3 | Must |
| NFR-04 | No runtime performance impact | Must |
| NFR-05 | Graceful fallback if reflection fails | Must |

---

## Part 3: Solution Architecture

### 3.1 System Overview

```
┌─────────────────────────────────────────────────────────────────────────┐
│                      CE Inspector System                                │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                  CEInspectorBootstrap                           │   │
│  │  • Registers custom editors on domain reload                    │   │
│  │  • Manages component visibility flags                           │   │
│  │  • Initializes style cache                                      │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                              │                                          │
│          ┌───────────────────┼───────────────────┐                     │
│          ▼                   ▼                   ▼                      │
│  ┌──────────────┐   ┌──────────────┐   ┌──────────────┐                │
│  │ CEBehaviour  │   │ CEUdon       │   │ CEComponent  │                │
│  │ Editor       │   │ Editor       │   │ Hider        │                │
│  │              │   │              │   │              │                │
│  │ Main proxy   │   │ Fallback for │   │ Hides raw    │                │
│  │ inspector    │   │ raw Udon     │   │ UdonBehaviour│                │
│  └──────────────┘   └──────────────┘   └──────────────┘                │
│          │                   │                   │                      │
│          └───────────────────┼───────────────────┘                     │
│                              ▼                                          │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                  CEInspectorRenderer                            │   │
│  │  • Header rendering (icon, name, badges)                        │   │
│  │  • Sync status bar                                              │   │
│  │  • Property grouping and display                                │   │
│  │  • Optimization info panel                                      │   │
│  │  • Advanced settings foldout                                    │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                              │                                          │
│                              ▼                                          │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │                  CEStyleCache                                   │   │
│  │  • GUIStyles for consistent appearance                          │   │
│  │  • Icons and textures                                           │   │
│  │  • Color palette                                                │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### 3.2 Component Responsibilities

| Component | Responsibility |
|-----------|----------------|
| **CEInspectorBootstrap** | Initialization, registration, lifecycle |
| **CEBehaviourEditor** | Custom editor for UdonSharpBehaviour subclasses |
| **CEUdonEditor** | Custom editor for raw UdonBehaviour (fallback) |
| **CEComponentHider** | Hides UdonBehaviour when proxy exists |
| **CEInspectorRenderer** | Shared rendering logic for consistent UI |
| **CEStyleCache** | Cached styles, icons, colors |
| **CEOptimizationInfo** | Retrieves and displays optimization data |

### 3.3 Data Flow

```
User Selects GameObject
         │
         ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ Unity calls OnEnable() for each component's custom editor              │
└─────────────────────────────────────────────────────────────────────────┘
         │
         ├─── UdonBehaviour ───► CEUdonEditor.OnEnable()
         │                              │
         │                              ▼
         │                       Has proxy behaviour?
         │                        /            \
         │                      Yes             No
         │                       │               │
         │                       ▼               ▼
         │               Hide component    Show fallback UI
         │
         └─── UdonSharpBehaviour ───► CEBehaviourEditor.OnEnable()
                                              │
                                              ▼
                                    Cache serialized properties
                                    Detect CE optimizations
                                    Find linked UdonBehaviour
                                              │
                                              ▼
┌─────────────────────────────────────────────────────────────────────────┐
│ OnInspectorGUI() called each frame                                      │
└─────────────────────────────────────────────────────────────────────────┘
                                              │
                                              ▼
                                    CEInspectorRenderer.Draw()
                                              │
                    ┌─────────────────────────┼─────────────────────────┐
                    ▼                         ▼                         ▼
             DrawHeader()              DrawProperties()          DrawFooter()
                    │                         │                         │
                    ▼                         ▼                         ▼
            Icon + Name + Badges      Grouped SerializedProperties    CE Info
```

---

## Part 4: Visual Design Specification

### 4.1 Inspector Layout

```
┌─────────────────────────────────────────────────────────────────────────┐
│ ▼ ◆ ComponentName                                          [CE ✓]      │
├─────────────────────────────────────────────────────────────────────────┤
│  Sync: Continuous  │  6 synced vars  │  Optimized: -45% bandwidth      │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  ┌─ References ────────────────────────────────────────────────────┐   │
│  │  Target Player         [None (VRCPlayerApi)              ◉]     │   │
│  │  Spawn Point           [SpawnPoint (Transform)           ◉]     │   │
│  │  Audio Source          [None (Audio Source)              ◉]     │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  ┌─ Configuration ─────────────────────────────────────────────────┐   │
│  │  Move Speed            [====●=====]  5.0                        │   │
│  │  Jump Force            [========●=]  8.5                        │   │
│  │  Max Health            [100      ]                              │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  ▶ Synced Variables (6)                                                │
│                                                                         │
│  ▶ Debug Options                                                       │
│                                                                         │
├─────────────────────────────────────────────────────────────────────────┤
│  ▶ CE Optimizations                                                    │
│     ✓ Sync Packing: 6 → 2 variables                                    │
│     ✓ Delta Sync: position, rotation                                   │
│     ✓ Constants Folded: 4 expressions                                  │
├─────────────────────────────────────────────────────────────────────────┤
│  ▶ Advanced                                                            │
│     Synchronization    [Continuous           ▼]                        │
│     Reliability        [Reliable Ordered     ▼]                        │
└─────────────────────────────────────────────────────────────────────────┘
```

### 4.2 Header Design

```
┌─────────────────────────────────────────────────────────────────────────┐
│                              HEADER                                     │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  [◆] ComponentName                                    [Badge] [Badge]   │
│   │       │                                              │       │      │
│   │       │                                              │       │      │
│   │       └─── Class name, bold, prominent               │       │      │
│   │                                                      │       │      │
│   └─── CE diamond icon (indicates CE-managed)            │       │      │
│                                                          │       │      │
│              ┌───────────────────────────────────────────┘       │      │
│              │                                                   │      │
│              ▼                                                   │      │
│        [CE ✓] = CE optimizations applied                         │      │
│        [CE] = CE managed, no optimizations                       │      │
│                                                                  │      │
│                     ┌────────────────────────────────────────────┘      │
│                     │                                                   │
│                     ▼                                                   │
│              [Netcode] = CE Netcode active (blue badge)                 │
│              [Pooled] = Object is pooled (purple badge)                 │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### 4.3 Status Bar Design

```
┌─────────────────────────────────────────────────────────────────────────┐
│                            STATUS BAR                                   │
├─────────────────────────────────────────────────────────────────────────┤
│                                                                         │
│  Compact single-line summary of networking status:                      │
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  Sync: None                                                     │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  Sync: Manual  │  4 synced vars  │  Bandwidth: ~120 bytes/sync  │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  ┌─────────────────────────────────────────────────────────────────┐   │
│  │  Sync: Continuous  │  12 → 4 vars (packed)  │  -68% bandwidth   │   │
│  └─────────────────────────────────────────────────────────────────┘   │
│                                                                         │
│  Colors:                                                                │
│  • None = Gray background                                               │
│  • Manual = Light blue background                                       │
│  • Continuous = Light green background                                  │
│  • Optimized = Green text for savings                                   │
│                                                                         │
└─────────────────────────────────────────────────────────────────────────┘
```

### 4.4 Property Grouping Rules

| Rule | Condition | Group Name |
|------|-----------|------------|
| 1 | Field is `UnityEngine.Object` subtype | "References" |
| 2 | Field has `[Header("X")]` attribute | Use "X" as group |
| 3 | Field has `[UdonSynced]` attribute | "Synced Variables" |
| 4 | Field name contains "debug" (case-insensitive) | "Debug Options" |
| 5 | Field name matches pattern `_.*` (underscore prefix) | "Internal" (collapsed) |
| 6 | Field is numeric primitive | "Configuration" |
| 7 | No match | "General" |

### 4.5 Badge Specifications

| Badge | Condition | Color | Icon |
|-------|-----------|-------|------|
| **CE** | Always (UdonSharpCE behaviour) | Gray (#888) | ◆ |
| **CE ✓** | Optimizations applied | Green (#4a4) | ◆✓ |
| **Netcode** | Has `[CENetworkedPlayer]` etc. | Blue (#48f) | ⚡ |
| **Pooled** | Has `[CEPooled]` | Purple (#a4f) | ♻ |
| **Predicted** | Has `[CEPredicted]` fields | Cyan (#4dd) | ↻ |

### 4.6 Color Palette

```csharp
public static class CEColors
{
    // Backgrounds
    public static readonly Color HeaderBg = new Color(0.22f, 0.22f, 0.22f);
    public static readonly Color StatusBarBg = new Color(0.18f, 0.18f, 0.18f);
    public static readonly Color GroupBg = new Color(0.25f, 0.25f, 0.25f);
    
    // Sync status backgrounds
    public static readonly Color SyncNoneBg = new Color(0.3f, 0.3f, 0.3f);
    public static readonly Color SyncManualBg = new Color(0.2f, 0.3f, 0.4f);
    public static readonly Color SyncContinuousBg = new Color(0.2f, 0.35f, 0.25f);
    
    // Badge colors
    public static readonly Color BadgeCE = new Color(0.5f, 0.5f, 0.5f);
    public static readonly Color BadgeOptimized = new Color(0.3f, 0.7f, 0.3f);
    public static readonly Color BadgeNetcode = new Color(0.3f, 0.5f, 0.95f);
    public static readonly Color BadgePooled = new Color(0.6f, 0.3f, 0.9f);
    
    // Text colors
    public static readonly Color TextPrimary = new Color(0.9f, 0.9f, 0.9f);
    public static readonly Color TextSecondary = new Color(0.6f, 0.6f, 0.6f);
    public static readonly Color TextPositive = new Color(0.4f, 0.8f, 0.4f);
    public static readonly Color TextWarning = new Color(0.9f, 0.7f, 0.2f);
}
```

---

## Part 5: Component Specifications

### 5.1 CEInspectorBootstrap

```csharp
namespace UdonSharpCE.Editor.Inspector
{
    /// <summary>
    /// Initializes the CE Inspector system on domain reload.
    /// </summary>
    [InitializeOnLoad]
    public static class CEInspectorBootstrap
    {
        // ═══════════════════════════════════════════════════════════════
        // CONFIGURATION
        // ═══════════════════════════════════════════════════════════════
        
        /// <summary>
        /// Whether to hide UdonBehaviour components when proxy exists.
        /// </summary>
        public static bool HideUdonBehaviours
        {
            get => EditorPrefs.GetBool("CE_HideUdonBehaviours", true);
            set => EditorPrefs.SetBool("CE_HideUdonBehaviours", value);
        }
        
        /// <summary>
        /// Whether to show optimization info in inspector.
        /// </summary>
        public static bool ShowOptimizationInfo
        {
            get => EditorPrefs.GetBool("CE_ShowOptimizationInfo", true);
            set => EditorPrefs.SetBool("CE_ShowOptimizationInfo", value);
        }
        
        /// <summary>
        /// Whether to auto-group properties.
        /// </summary>
        public static bool AutoGroupProperties
        {
            get => EditorPrefs.GetBool("CE_AutoGroupProperties", true);
            set => EditorPrefs.SetBool("CE_AutoGroupProperties", value);
        }
        
        // ═══════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ═══════════════════════════════════════════════════════════════
        
        static CEInspectorBootstrap()
        {
            // Initialize on next editor update to avoid conflicts
            EditorApplication.delayCall += Initialize;
        }
        
        private static void Initialize()
        {
            // Initialize style cache
            CEStyleCache.Initialize();
            
            // Initialize optimization registry
            CEOptimizationRegistry.Initialize();
            
            // Register for hierarchy changes
            EditorApplication.hierarchyChanged += OnHierarchyChanged;
            
            // Register for selection changes
            Selection.selectionChanged += OnSelectionChanged;
            
            Debug.Log("[CE Inspector] Initialized");
        }
        
        private static void OnHierarchyChanged()
        {
            // Update hidden component flags
            if (HideUdonBehaviours)
            {
                CEComponentHider.RefreshAll();
            }
        }
        
        private static void OnSelectionChanged()
        {
            // Force repaint when selection changes
            if (Selection.activeGameObject != null)
            {
                CEComponentHider.UpdateForGameObject(Selection.activeGameObject);
            }
        }
    }
}
```

### 5.2 CEComponentHider

```csharp
namespace UdonSharpCE.Editor.Inspector
{
    /// <summary>
    /// Manages hiding of UdonBehaviour components when proxy exists.
    /// </summary>
    public static class CEComponentHider
    {
        // Cache of hidden components
        private static readonly HashSet<int> _hiddenInstanceIds = new HashSet<int>();
        
        /// <summary>
        /// Update visibility for all components on a GameObject.
        /// </summary>
        public static void UpdateForGameObject(GameObject go)
        {
            if (go == null) return;
            
            var udonBehaviours = go.GetComponents<UdonBehaviour>();
            var proxies = go.GetComponents<UdonSharpBehaviour>();
            
            foreach (var udon in udonBehaviours)
            {
                bool hasProxy = false;
                
                foreach (var proxy in proxies)
                {
                    if (UdonSharpEditorUtility.GetBackingUdonBehaviour(proxy) == udon)
                    {
                        hasProxy = true;
                        break;
                    }
                }
                
                SetComponentHidden(udon, hasProxy && CEInspectorBootstrap.HideUdonBehaviours);
            }
        }
        
        /// <summary>
        /// Refresh visibility for all loaded GameObjects.
        /// </summary>
        public static void RefreshAll()
        {
            var allUdon = Object.FindObjectsOfType<UdonBehaviour>();
            var processed = new HashSet<GameObject>();
            
            foreach (var udon in allUdon)
            {
                if (processed.Add(udon.gameObject))
                {
                    UpdateForGameObject(udon.gameObject);
                }
            }
        }
        
        private static void SetComponentHidden(Component component, bool hidden)
        {
            int id = component.GetInstanceID();
            
            if (hidden)
            {
                if (_hiddenInstanceIds.Add(id))
                {
                    component.hideFlags |= HideFlags.HideInInspector;
                }
            }
            else
            {
                if (_hiddenInstanceIds.Remove(id))
                {
                    component.hideFlags &= ~HideFlags.HideInInspector;
                }
            }
        }
        
        /// <summary>
        /// Temporarily show all hidden components (for debugging).
        /// </summary>
        public static void ShowAllTemporarily()
        {
            foreach (var udon in Object.FindObjectsOfType<UdonBehaviour>())
            {
                udon.hideFlags &= ~HideFlags.HideInInspector;
            }
            _hiddenInstanceIds.Clear();
        }
    }
}
```

### 5.3 CEBehaviourEditor

```csharp
namespace UdonSharpCE.Editor.Inspector
{
    /// <summary>
    /// Custom inspector for all UdonSharpBehaviour subclasses.
    /// </summary>
    [CustomEditor(typeof(UdonSharpBehaviour), true)]
    [CanEditMultipleObjects]
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
        
        // Foldout states
        private static readonly Dictionary<string, bool> _foldoutStates = 
            new Dictionary<string, bool>();
        
        // ═══════════════════════════════════════════════════════════════
        // LIFECYCLE
        // ═══════════════════════════════════════════════════════════════
        
        protected virtual void OnEnable()
        {
            if (target == null) return;
            
            // Find backing UdonBehaviour
            _backingBehaviour = UdonSharpEditorUtility
                .GetBackingUdonBehaviour((UdonSharpBehaviour)target);
            
            // Analyze optimizations
            _optimizationReport = CEOptimizationRegistry.GetReport(target);
            
            // Analyze sync info
            _syncInfo = AnalyzeSyncInfo();
            
            // Group properties
            if (CEInspectorBootstrap.AutoGroupProperties)
            {
                _propertyGroups = GroupProperties(serializedObject);
            }
            
            // Determine badges
            _badges = DetermineBadges();
            
            // Ensure UdonBehaviour is hidden
            CEComponentHider.UpdateForGameObject(((Component)target).gameObject);
        }
        
        // ═══════════════════════════════════════════════════════════════
        // MAIN DRAWING
        // ═══════════════════════════════════════════════════════════════
        
        public override void OnInspectorGUI()
        {
            serializedObject.Update();
            
            CEInspectorRenderer.DrawHeader(target.GetType().Name, _badges);
            CEInspectorRenderer.DrawStatusBar(_syncInfo, _optimizationReport);
            
            EditorGUILayout.Space(4);
            
            if (CEInspectorBootstrap.AutoGroupProperties && _propertyGroups != null)
            {
                DrawGroupedProperties();
            }
            else
            {
                DrawDefaultProperties();
            }
            
            EditorGUILayout.Space(4);
            
            if (CEInspectorBootstrap.ShowOptimizationInfo && _optimizationReport != null)
            {
                CEInspectorRenderer.DrawOptimizationPanel(_optimizationReport);
            }
            
            DrawAdvancedSection();
            
            serializedObject.ApplyModifiedProperties();
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
                        DrawPropertiesInGroup(group);
                        EditorGUI.indentLevel--;
                    }
                }
                else
                {
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
                
                EditorGUILayout.PropertyField(iterator, true);
            }
        }
        
        // ═══════════════════════════════════════════════════════════════
        // ADVANCED SECTION
        // ═══════════════════════════════════════════════════════════════
        
        private void DrawAdvancedSection()
        {
            string key = $"{target.GetType().Name}_Advanced";
            if (!_foldoutStates.TryGetValue(key, out bool expanded))
            {
                expanded = false;
                _foldoutStates[key] = expanded;
            }
            
            expanded = CEInspectorRenderer.DrawGroupHeader("Advanced", expanded, -1);
            _foldoutStates[key] = expanded;
            
            if (expanded && _backingBehaviour != null)
            {
                EditorGUI.indentLevel++;
                
                // Sync mode dropdown
                var syncMode = _backingBehaviour.SyncMethod;
                var newSyncMode = (Networking.SyncType)EditorGUILayout.EnumPopup(
                    "Synchronization", syncMode);
                
                if (newSyncMode != syncMode)
                {
                    Undo.RecordObject(_backingBehaviour, "Change Sync Mode");
                    _backingBehaviour.SyncMethod = newSyncMode;
                    EditorUtility.SetDirty(_backingBehaviour);
                    _syncInfo = AnalyzeSyncInfo();
                }
                
                EditorGUILayout.Space(4);
                
                // Debug buttons
                EditorGUILayout.BeginHorizontal();
                {
                    if (GUILayout.Button("View Udon Assembly"))
                    {
                        CEDebugWindows.ShowUdonAssembly(_backingBehaviour);
                    }
                    
                    if (GUILayout.Button("View Optimization Report"))
                    {
                        CEDebugWindows.ShowOptimizationReport(target);
                    }
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUI.indentLevel--;
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
                    info.EstimatedBytesPerSync += EstimateFieldSize(field);
                }
            }
            
            // Apply optimization adjustments
            if (_optimizationReport != null && _optimizationReport.SyncPackingApplied)
            {
                info.OptimizedFieldCount = _optimizationReport.PackedSyncVars;
                info.OptimizedBytesPerSync = _optimizationReport.OptimizedBytesPerSync;
            }
            
            return info;
        }
        
        private PropertyGroup[] GroupProperties(SerializedObject obj)
        {
            var groups = new Dictionary<string, PropertyGroup>
            {
                ["References"] = new PropertyGroup("References", true),
                ["Configuration"] = new PropertyGroup("Configuration", true),
                ["Synced Variables"] = new PropertyGroup("Synced Variables", false),
                ["Debug Options"] = new PropertyGroup("Debug Options", false),
                ["Internal"] = new PropertyGroup("Internal", false) { ForceCollapsible = true },
                ["General"] = new PropertyGroup("General", true)
            };
            
            var type = target.GetType();
            var fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            
            var iterator = obj.GetIterator();
            bool enterChildren = true;
            
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.name == "m_Script") continue;
                
                var field = FindField(fields, iterator.name);
                string groupName = DetermineGroup(iterator, field);
                
                if (groups.TryGetValue(groupName, out var group))
                {
                    group.Properties.Add(iterator.Copy());
                }
            }
            
            return groups.Values
                .Where(g => g.Properties.Count > 0)
                .ToArray();
        }
        
        private string DetermineGroup(SerializedProperty prop, FieldInfo field)
        {
            if (field == null) return "General";
            
            // Check for Header attribute
            var header = field.GetCustomAttribute<HeaderAttribute>();
            if (header != null) return header.header;
            
            // Check for UdonSynced
            if (field.GetCustomAttribute<UdonSyncedAttribute>() != null)
                return "Synced Variables";
            
            // Check name patterns
            if (prop.name.StartsWith("_")) return "Internal";
            if (prop.name.ToLower().Contains("debug")) return "Debug Options";
            
            // Check type
            if (typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType))
                return "References";
            
            if (field.FieldType.IsPrimitive || field.FieldType == typeof(string))
                return "Configuration";
            
            return "General";
        }
        
        private CEBadge[] DetermineBadges()
        {
            var badges = new List<CEBadge>();
            var type = target.GetType();
            
            // CE base badge
            if (_optimizationReport != null && _optimizationReport.AnyOptimizationsApplied)
            {
                badges.Add(new CEBadge("CE ✓", CEColors.BadgeOptimized));
            }
            else
            {
                badges.Add(new CEBadge("CE", CEColors.BadgeCE));
            }
            
            // Netcode badge
            if (type.GetCustomAttribute<CENetworkedPlayerAttribute>() != null ||
                type.GetCustomAttribute<CENetworkedProjectileAttribute>() != null)
            {
                badges.Add(new CEBadge("Netcode", CEColors.BadgeNetcode));
            }
            
            // Pooled badge
            if (type.GetCustomAttribute<CEPooledAttribute>() != null)
            {
                badges.Add(new CEBadge("Pooled", CEColors.BadgePooled));
            }
            
            return badges.ToArray();
        }
    }
}
```

### 5.4 CEInspectorRenderer

```csharp
namespace UdonSharpCE.Editor.Inspector
{
    /// <summary>
    /// Shared rendering utilities for CE inspectors.
    /// </summary>
    public static class CEInspectorRenderer
    {
        // ═══════════════════════════════════════════════════════════════
        // HEADER
        // ═══════════════════════════════════════════════════════════════
        
        public static void DrawHeader(string className, CEBadge[] badges)
        {
            EditorGUILayout.BeginHorizontal(CEStyleCache.HeaderStyle);
            {
                // CE diamond icon
                GUILayout.Label(CEStyleCache.DiamondIcon, GUILayout.Width(18), GUILayout.Height(18));
                GUILayout.Space(4);
                
                // Class name
                GUILayout.Label(className, CEStyleCache.HeaderTextStyle);
                
                GUILayout.FlexibleSpace();
                
                // Badges
                foreach (var badge in badges)
                {
                    DrawBadge(badge);
                    GUILayout.Space(4);
                }
            }
            EditorGUILayout.EndHorizontal();
        }
        
        private static void DrawBadge(CEBadge badge)
        {
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = badge.Color;
            
            GUILayout.Label(badge.Text, CEStyleCache.BadgeStyle);
            
            GUI.backgroundColor = prevBg;
        }
        
        // ═══════════════════════════════════════════════════════════════
        // STATUS BAR
        // ═══════════════════════════════════════════════════════════════
        
        public static void DrawStatusBar(SyncInfo syncInfo, CEOptimizationReport report)
        {
            Color bgColor = syncInfo.Mode switch
            {
                Networking.SyncType.None => CEColors.SyncNoneBg,
                Networking.SyncType.Manual => CEColors.SyncManualBg,
                Networking.SyncType.Continuous => CEColors.SyncContinuousBg,
                _ => CEColors.SyncNoneBg
            };
            
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = bgColor;
            
            EditorGUILayout.BeginHorizontal(CEStyleCache.StatusBarStyle);
            {
                // Sync mode
                GUILayout.Label($"Sync: {syncInfo.Mode}", CEStyleCache.StatusTextStyle);
                
                if (syncInfo.SyncedFieldCount > 0)
                {
                    GUILayout.Label("│", CEStyleCache.StatusSeparatorStyle);
                    
                    // Synced var count
                    if (syncInfo.OptimizedFieldCount > 0 && 
                        syncInfo.OptimizedFieldCount < syncInfo.SyncedFieldCount)
                    {
                        // Show optimization
                        GUILayout.Label(
                            $"{syncInfo.SyncedFieldCount} → {syncInfo.OptimizedFieldCount} vars (packed)",
                            CEStyleCache.StatusPositiveStyle
                        );
                    }
                    else
                    {
                        GUILayout.Label(
                            $"{syncInfo.SyncedFieldCount} synced vars",
                            CEStyleCache.StatusTextStyle
                        );
                    }
                    
                    // Bandwidth savings
                    if (report != null && report.BandwidthReduction > 0)
                    {
                        GUILayout.Label("│", CEStyleCache.StatusSeparatorStyle);
                        GUILayout.Label(
                            $"-{report.BandwidthReduction}% bandwidth",
                            CEStyleCache.StatusPositiveStyle
                        );
                    }
                }
                
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndHorizontal();
            
            GUI.backgroundColor = prevBg;
        }
        
        // ═══════════════════════════════════════════════════════════════
        // GROUP HEADER
        // ═══════════════════════════════════════════════════════════════
        
        public static bool DrawGroupHeader(string name, bool expanded, int count)
        {
            EditorGUILayout.BeginHorizontal(CEStyleCache.GroupHeaderStyle);
            {
                expanded = EditorGUILayout.Foldout(expanded, name, true, CEStyleCache.FoldoutStyle);
                
                if (count >= 0)
                {
                    GUILayout.FlexibleSpace();
                    GUILayout.Label($"({count})", CEStyleCache.GroupCountStyle);
                }
            }
            EditorGUILayout.EndHorizontal();
            
            return expanded;
        }
        
        // ═══════════════════════════════════════════════════════════════
        // OPTIMIZATION PANEL
        // ═══════════════════════════════════════════════════════════════
        
        public static void DrawOptimizationPanel(CEOptimizationReport report)
        {
            if (!report.AnyOptimizationsApplied) return;
            
            EditorGUILayout.BeginVertical(CEStyleCache.OptimizationPanelStyle);
            {
                EditorGUILayout.LabelField("CE Optimizations", CEStyleCache.OptimizationHeaderStyle);
                
                if (report.SyncPackingApplied)
                {
                    DrawOptimizationItem(
                        "Sync Packing",
                        $"{report.OriginalSyncVars} → {report.PackedSyncVars} variables"
                    );
                }
                
                if (report.DeltaSyncApplied)
                {
                    DrawOptimizationItem(
                        "Delta Sync",
                        $"{report.DeltaSyncFields} fields"
                    );
                }
                
                if (report.ConstantsFolded > 0)
                {
                    DrawOptimizationItem(
                        "Constants Folded",
                        $"{report.ConstantsFolded} expressions"
                    );
                }
                
                if (report.LoopsUnrolled > 0)
                {
                    DrawOptimizationItem(
                        "Loops Unrolled",
                        $"{report.LoopsUnrolled} loops"
                    );
                }
                
                if (report.StringsInterned > 0)
                {
                    DrawOptimizationItem(
                        "Strings Interned",
                        $"{report.StringsInterned} strings"
                    );
                }
            }
            EditorGUILayout.EndVertical();
        }
        
        private static void DrawOptimizationItem(string name, string value)
        {
            EditorGUILayout.BeginHorizontal();
            {
                GUILayout.Label("✓", CEStyleCache.OptimizationCheckStyle, GUILayout.Width(16));
                GUILayout.Label(name, CEStyleCache.OptimizationNameStyle, GUILayout.Width(120));
                GUILayout.Label(value, CEStyleCache.OptimizationValueStyle);
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
```

### 5.5 CEStyleCache

```csharp
namespace UdonSharpCE.Editor.Inspector
{
    /// <summary>
    /// Cached GUIStyles for consistent inspector appearance.
    /// </summary>
    public static class CEStyleCache
    {
        // ═══════════════════════════════════════════════════════════════
        // ICONS
        // ═══════════════════════════════════════════════════════════════
        
        public static Texture2D DiamondIcon { get; private set; }
        public static Texture2D CheckIcon { get; private set; }
        
        // ═══════════════════════════════════════════════════════════════
        // STYLES
        // ═══════════════════════════════════════════════════════════════
        
        public static GUIStyle HeaderStyle { get; private set; }
        public static GUIStyle HeaderTextStyle { get; private set; }
        public static GUIStyle BadgeStyle { get; private set; }
        public static GUIStyle StatusBarStyle { get; private set; }
        public static GUIStyle StatusTextStyle { get; private set; }
        public static GUIStyle StatusPositiveStyle { get; private set; }
        public static GUIStyle StatusSeparatorStyle { get; private set; }
        public static GUIStyle GroupHeaderStyle { get; private set; }
        public static GUIStyle GroupCountStyle { get; private set; }
        public static GUIStyle FoldoutStyle { get; private set; }
        public static GUIStyle OptimizationPanelStyle { get; private set; }
        public static GUIStyle OptimizationHeaderStyle { get; private set; }
        public static GUIStyle OptimizationCheckStyle { get; private set; }
        public static GUIStyle OptimizationNameStyle { get; private set; }
        public static GUIStyle OptimizationValueStyle { get; private set; }
        
        // ═══════════════════════════════════════════════════════════════
        // INITIALIZATION
        // ═══════════════════════════════════════════════════════════════
        
        private static bool _initialized;
        
        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            
            CreateIcons();
            CreateStyles();
        }
        
        private static void CreateIcons()
        {
            // Diamond icon (CE logo)
            DiamondIcon = CreateIcon(16, 16, (x, y, w, h) =>
            {
                float cx = w / 2f, cy = h / 2f;
                float dx = Mathf.Abs(x - cx) / cx;
                float dy = Mathf.Abs(y - cy) / cy;
                return (dx + dy <= 1f) ? CEColors.BadgeOptimized : Color.clear;
            });
            
            // Checkmark icon
            CheckIcon = CreateIcon(12, 12, (x, y, w, h) =>
            {
                // Simple checkmark shape
                return Color.clear; // Placeholder
            });
        }
        
        private static Texture2D CreateIcon(int w, int h, Func<int, int, int, int, Color> colorFunc)
        {
            var tex = new Texture2D(w, h);
            var pixels = new Color[w * h];
            
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    pixels[y * w + x] = colorFunc(x, y, w, h);
                }
            }
            
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
        
        private static void CreateStyles()
        {
            // Header
            HeaderStyle = new GUIStyle(EditorStyles.toolbar)
            {
                fixedHeight = 24,
                padding = new RectOffset(8, 8, 4, 4)
            };
            HeaderStyle.normal.background = MakeTex(1, 1, CEColors.HeaderBg);
            
            HeaderTextStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13,
                alignment = TextAnchor.MiddleLeft
            };
            HeaderTextStyle.normal.textColor = CEColors.TextPrimary;
            
            // Badge
            BadgeStyle = new GUIStyle(EditorStyles.miniButton)
            {
                fontSize = 10,
                fontStyle = FontStyle.Bold,
                padding = new RectOffset(6, 6, 2, 2),
                margin = new RectOffset(0, 0, 3, 3)
            };
            
            // Status bar
            StatusBarStyle = new GUIStyle(EditorStyles.helpBox)
            {
                fixedHeight = 20,
                padding = new RectOffset(8, 8, 2, 2),
                margin = new RectOffset(0, 0, 2, 2)
            };
            
            StatusTextStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };
            StatusTextStyle.normal.textColor = CEColors.TextSecondary;
            
            StatusPositiveStyle = new GUIStyle(StatusTextStyle);
            StatusPositiveStyle.normal.textColor = CEColors.TextPositive;
            
            StatusSeparatorStyle = new GUIStyle(StatusTextStyle)
            {
                alignment = TextAnchor.MiddleCenter,
                fixedWidth = 16
            };
            
            // Group header
            GroupHeaderStyle = new GUIStyle()
            {
                padding = new RectOffset(0, 0, 2, 2)
            };
            
            GroupCountStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                alignment = TextAnchor.MiddleRight
            };
            GroupCountStyle.normal.textColor = CEColors.TextSecondary;
            
            FoldoutStyle = new GUIStyle(EditorStyles.foldout)
            {
                fontStyle = FontStyle.Bold
            };
            
            // Optimization panel
            OptimizationPanelStyle = new GUIStyle(EditorStyles.helpBox)
            {
                padding = new RectOffset(8, 8, 6, 6)
            };
            
            OptimizationHeaderStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 11
            };
            
            OptimizationCheckStyle = new GUIStyle(EditorStyles.label)
            {
                alignment = TextAnchor.MiddleCenter
            };
            OptimizationCheckStyle.normal.textColor = CEColors.TextPositive;
            
            OptimizationNameStyle = new GUIStyle(EditorStyles.label);
            
            OptimizationValueStyle = new GUIStyle(EditorStyles.label);
            OptimizationValueStyle.normal.textColor = CEColors.TextSecondary;
        }
        
        private static Texture2D MakeTex(int w, int h, Color color)
        {
            var pixels = new Color[w * h];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            
            var tex = new Texture2D(w, h);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }
    }
}
```

---

## Part 6: Data Structures

```csharp
namespace UdonSharpCE.Editor.Inspector
{
    /// <summary>
    /// Badge displayed in inspector header.
    /// </summary>
    public struct CEBadge
    {
        public string Text;
        public Color Color;
        
        public CEBadge(string text, Color color)
        {
            Text = text;
            Color = color;
        }
    }
    
    /// <summary>
    /// Information about sync variables.
    /// </summary>
    public class SyncInfo
    {
        public Networking.SyncType Mode;
        public int SyncedFieldCount;
        public int OptimizedFieldCount;
        public int EstimatedBytesPerSync;
        public int OptimizedBytesPerSync;
    }
    
    /// <summary>
    /// Group of related properties.
    /// </summary>
    public class PropertyGroup
    {
        public string Name;
        public bool DefaultExpanded;
        public bool ForceCollapsible;
        public List<SerializedProperty> Properties = new List<SerializedProperty>();
        
        public PropertyGroup(string name, bool defaultExpanded)
        {
            Name = name;
            DefaultExpanded = defaultExpanded;
        }
    }
    
    /// <summary>
    /// CE optimization report for a behaviour.
    /// </summary>
    public class CEOptimizationReport
    {
        public bool AnyOptimizationsApplied =>
            SyncPackingApplied || DeltaSyncApplied || 
            ConstantsFolded > 0 || LoopsUnrolled > 0 || StringsInterned > 0;
        
        // Sync optimizations
        public bool SyncPackingApplied;
        public int OriginalSyncVars;
        public int PackedSyncVars;
        public int OptimizedBytesPerSync;
        
        public bool DeltaSyncApplied;
        public int DeltaSyncFields;
        
        // Execution optimizations
        public int ConstantsFolded;
        public int LoopsUnrolled;
        public int MethodsInlined;
        public int StringsInterned;
        
        // Summary
        public int BandwidthReduction;    // Percentage
        public int InstructionReduction;  // Percentage
    }
}
```

---

## Part 7: Settings Integration

### 7.1 Preferences Window

```csharp
namespace UdonSharpCE.Editor.Inspector
{
    /// <summary>
    /// CE Inspector preferences in Unity Preferences window.
    /// </summary>
    public class CEInspectorPreferences : SettingsProvider
    {
        public CEInspectorPreferences() 
            : base("Preferences/UdonSharp CE/Inspector", SettingsScope.User)
        {
        }
        
        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new CEInspectorPreferences();
        }
        
        public override void OnGUI(string searchContext)
        {
            EditorGUILayout.LabelField("Inspector Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            // Hide UdonBehaviour toggle
            EditorGUI.BeginChangeCheck();
            bool hideUdon = EditorGUILayout.Toggle(
                new GUIContent(
                    "Hide UdonBehaviour Components",
                    "When enabled, the raw UdonBehaviour component is hidden when a " +
                    "UdonSharp proxy behaviour exists."
                ),
                CEInspectorBootstrap.HideUdonBehaviours
            );
            if (EditorGUI.EndChangeCheck())
            {
                CEInspectorBootstrap.HideUdonBehaviours = hideUdon;
                CEComponentHider.RefreshAll();
            }
            
            // Show optimization info toggle
            CEInspectorBootstrap.ShowOptimizationInfo = EditorGUILayout.Toggle(
                new GUIContent(
                    "Show Optimization Info",
                    "Display CE optimization details in inspector."
                ),
                CEInspectorBootstrap.ShowOptimizationInfo
            );
            
            // Auto-group properties toggle
            CEInspectorBootstrap.AutoGroupProperties = EditorGUILayout.Toggle(
                new GUIContent(
                    "Auto-Group Properties",
                    "Automatically group related properties together."
                ),
                CEInspectorBootstrap.AutoGroupProperties
            );
            
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Debugging", EditorStyles.boldLabel);
            EditorGUILayout.Space();
            
            if (GUILayout.Button("Show All Hidden Components"))
            {
                CEComponentHider.ShowAllTemporarily();
            }
            
            if (GUILayout.Button("Refresh Component Visibility"))
            {
                CEComponentHider.RefreshAll();
            }
        }
    }
}
```

### 7.2 Menu Items

```csharp
namespace UdonSharpCE.Editor.Inspector
{
    public static class CEInspectorMenu
    {
        [MenuItem("Tools/UdonSharp CE/Inspector/Toggle UdonBehaviour Visibility")]
        public static void ToggleUdonBehaviourVisibility()
        {
            CEInspectorBootstrap.HideUdonBehaviours = !CEInspectorBootstrap.HideUdonBehaviours;
            CEComponentHider.RefreshAll();
            
            Debug.Log($"[CE] UdonBehaviour visibility: " +
                      $"{(CEInspectorBootstrap.HideUdonBehaviours ? "Hidden" : "Visible")}");
        }
        
        [MenuItem("Tools/UdonSharp CE/Inspector/Refresh All Inspectors")]
        public static void RefreshAllInspectors()
        {
            CEComponentHider.RefreshAll();
            
            // Force repaint
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (window.GetType().Name == "InspectorWindow")
                {
                    window.Repaint();
                }
            }
        }
        
        [MenuItem("Tools/UdonSharp CE/Inspector/Open Preferences")]
        public static void OpenPreferences()
        {
            SettingsService.OpenUserPreferences("Preferences/UdonSharp CE/Inspector");
        }
    }
}
```

---

## Part 8: Testing Plan

### 8.1 Test Cases

| ID | Test | Expected Result |
|----|------|-----------------|
| T-01 | Select GameObject with UdonSharp behaviour | Single CE-styled component visible |
| T-02 | UdonBehaviour hidden when proxy exists | UdonBehaviour not visible in inspector |
| T-03 | Serialized fields displayed correctly | All fields editable, values persist |
| T-04 | Sync mode displayed in status bar | Correct sync type shown |
| T-05 | Optimization badges appear correctly | Badges match applied optimizations |
| T-06 | Multi-select editing works | Shared fields editable together |
| T-07 | Undo/redo works | Changes revertible |
| T-08 | Foldout states persist | Collapse states saved between selections |
| T-09 | Advanced section shows sync dropdown | Sync mode changeable |
| T-10 | Preferences respected | Settings changes take immediate effect |
| T-11 | Performance acceptable | No frame drops when inspector visible |
| T-12 | Works with prefabs | Prefab overrides display correctly |
| T-13 | Works without CE optimizations | Basic display even without optimizations |
| T-14 | Toggle visibility setting | Components show/hide as expected |
| T-15 | Property grouping works | Fields grouped by type/attribute |

### 8.2 Edge Cases

| Case | Handling |
|------|----------|
| Null backing UdonBehaviour | Fall back to default property display |
| Missing proxy reference | Show UdonBehaviour normally |
| Multiple UdonSharp behaviours | Each gets own inspector |
| Nested prefab variants | Respect prefab override display |
| Play mode changes | Continue working, no errors |
| Domain reload | Reinitialize cleanly |

---

## Part 9: Implementation Plan

### Phase 1: Foundation (Week 1)

| Task | Effort | Deliverable |
|------|--------|-------------|
| CEInspectorBootstrap | 0.5 days | Initialization system |
| CEComponentHider | 0.5 days | Component visibility control |
| CEStyleCache | 1 day | Styles and icons |
| Basic CEBehaviourEditor | 1 day | Simple custom editor |
| Unit tests | 1 day | Core functionality tests |

### Phase 2: Rendering (Week 2)

| Task | Effort | Deliverable |
|------|--------|-------------|
| CEInspectorRenderer header | 0.5 days | Header with badges |
| CEInspectorRenderer status bar | 0.5 days | Sync status display |
| Property grouping | 1 day | Auto-grouped properties |
| Foldout persistence | 0.5 days | Saved collapse states |
| Multi-object editing | 1 day | Multi-select support |
| Integration tests | 0.5 days | Full flow tests |

### Phase 3: Polish (Week 3)

| Task | Effort | Deliverable |
|------|--------|-------------|
| Optimization panel | 1 day | CE optimization display |
| Advanced section | 0.5 days | Sync mode + debug buttons |
| Preferences window | 0.5 days | Settings UI |
| Menu items | 0.25 days | Quick access commands |
| Documentation | 1 day | Usage guide |
| Edge case handling | 0.75 days | Robustness improvements |
| Final testing | 1 day | Full test pass |

---

## Part 10: Success Criteria

| Criterion | Measurement |
|-----------|-------------|
| **Clean appearance** | Single component visible, no warnings |
| **CE branding** | Badge visible on all CE behaviours |
| **Functionality preserved** | All fields editable, undo works |
| **Performance** | <1ms inspector render time |
| **Compatibility** | Works Unity 2019.4 - 2022.3 |
| **Discoverability** | Optimization info visible without digging |
| **Configurability** | Settings available in Preferences |
| **Robustness** | No errors on edge cases |

---

## Appendix A: File Structure

```
Packages/com.merlin.UdonSharp/
└── Editor/
    └── Inspector/
        ├── CEInspectorBootstrap.cs
        ├── CEComponentHider.cs
        ├── CEBehaviourEditor.cs
        ├── CEUdonEditor.cs
        ├── CEInspectorRenderer.cs
        ├── CEStyleCache.cs
        ├── CEOptimizationRegistry.cs
        ├── CEInspectorPreferences.cs
        ├── CEInspectorMenu.cs
        ├── Data/
        │   ├── CEBadge.cs
        │   ├── SyncInfo.cs
        │   ├── PropertyGroup.cs
        │   └── CEOptimizationReport.cs
        ├── Icons/
        │   ├── ce_diamond.png
        │   └── ce_check.png
        └── Tests/
            ├── CEInspectorTests.cs
            └── CEComponentHiderTests.cs
```

---

## Appendix B: Dependencies

| Dependency | Required Version | Purpose |
|------------|------------------|---------|
| Unity | 2019.4+ | Editor API |
| UdonSharp | 1.x | Base system |
| VRChat SDK | 3.x | UdonBehaviour |

---

*Specification End*
