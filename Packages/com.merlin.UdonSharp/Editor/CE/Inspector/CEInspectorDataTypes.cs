using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.Editor.Inspector
{
    /// <summary>
    /// Badge displayed in inspector header.
    /// </summary>
    public struct CEBadge
    {
        public string Text;
        public Color Color;
        public string Tooltip;

        public CEBadge(string text, Color color, string tooltip = null)
        {
            Text = text;
            Color = color;
            Tooltip = tooltip;
        }
    }

    /// <summary>
    /// Information about sync variables on a behaviour.
    /// </summary>
    public class SyncInfo
    {
        /// <summary>
        /// The sync method used by the backing UdonBehaviour.
        /// </summary>
        public Networking.SyncType Mode;

        /// <summary>
        /// Number of fields with [UdonSynced] attribute.
        /// </summary>
        public int SyncedFieldCount;

        /// <summary>
        /// Number of fields after sync packing optimization (if applied).
        /// </summary>
        public int OptimizedFieldCount;

        /// <summary>
        /// Estimated bytes per sync before optimization.
        /// </summary>
        public int EstimatedBytesPerSync;

        /// <summary>
        /// Estimated bytes per sync after optimization.
        /// </summary>
        public int OptimizedBytesPerSync;

        /// <summary>
        /// Returns true if sync optimizations have been applied.
        /// </summary>
        public bool HasSyncOptimizations => OptimizedFieldCount > 0 && OptimizedFieldCount < SyncedFieldCount;

        /// <summary>
        /// Calculate bandwidth reduction percentage.
        /// </summary>
        public int BandwidthReductionPercent
        {
            get
            {
                if (EstimatedBytesPerSync <= 0 || OptimizedBytesPerSync <= 0)
                    return 0;
                if (OptimizedBytesPerSync >= EstimatedBytesPerSync)
                    return 0;
                return Mathf.RoundToInt((1f - (float)OptimizedBytesPerSync / EstimatedBytesPerSync) * 100f);
            }
        }
    }

    /// <summary>
    /// Group of related properties for organized inspector display.
    /// </summary>
    public class PropertyGroup
    {
        /// <summary>
        /// Display name for the group.
        /// </summary>
        public string Name;

        /// <summary>
        /// Whether this group is expanded by default.
        /// </summary>
        public bool DefaultExpanded;

        /// <summary>
        /// Whether this group should always be collapsible regardless of item count.
        /// </summary>
        public bool ForceCollapsible;

        /// <summary>
        /// Sort priority (lower = earlier in list).
        /// </summary>
        public int Priority;

        /// <summary>
        /// Properties in this group.
        /// </summary>
        public List<SerializedProperty> Properties = new List<SerializedProperty>();

        public PropertyGroup(string name, bool defaultExpanded, int priority = 100)
        {
            Name = name;
            DefaultExpanded = defaultExpanded;
            Priority = priority;
        }
    }

    /// <summary>
    /// CE optimization report for a behaviour.
    /// Contains information about what optimizations were applied during compilation.
    /// </summary>
    public class CEOptimizationReport
    {
        /// <summary>
        /// The source file path this report is for.
        /// </summary>
        public string SourceFilePath;

        /// <summary>
        /// Returns true if any optimizations were applied.
        /// </summary>
        public bool AnyOptimizationsApplied =>
            SyncPackingApplied || DeltaSyncApplied ||
            ConstantsFolded > 0 || LoopsUnrolled > 0 || 
            MethodsInlined > 0 || StringsInterned > 0 ||
            DeadCodeEliminated > 0;

        // ═══════════════════════════════════════════════════════════════
        // SYNC OPTIMIZATIONS (Stubbed - not yet implemented)
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Whether sync variable packing was applied.
        /// [STUB] This feature is not yet implemented.
        /// </summary>
        public bool SyncPackingApplied;

        /// <summary>
        /// Original number of sync variables before packing.
        /// [STUB] This feature is not yet implemented.
        /// </summary>
        public int OriginalSyncVars;

        /// <summary>
        /// Number of sync variables after packing.
        /// [STUB] This feature is not yet implemented.
        /// </summary>
        public int PackedSyncVars;

        /// <summary>
        /// Optimized bytes per sync after packing.
        /// [STUB] This feature is not yet implemented.
        /// </summary>
        public int OptimizedBytesPerSync;

        /// <summary>
        /// Whether delta sync was applied.
        /// [STUB] This feature is not yet implemented.
        /// </summary>
        public bool DeltaSyncApplied;

        /// <summary>
        /// Number of fields using delta sync.
        /// [STUB] This feature is not yet implemented.
        /// </summary>
        public int DeltaSyncFields;

        // ═══════════════════════════════════════════════════════════════
        // EXECUTION OPTIMIZATIONS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Number of constant expressions folded.
        /// </summary>
        public int ConstantsFolded;

        /// <summary>
        /// Number of small loops unrolled.
        /// </summary>
        public int LoopsUnrolled;

        /// <summary>
        /// Number of tiny methods inlined.
        /// </summary>
        public int MethodsInlined;

        /// <summary>
        /// Number of strings interned.
        /// </summary>
        public int StringsInterned;

        /// <summary>
        /// Number of dead code blocks eliminated.
        /// </summary>
        public int DeadCodeEliminated;

        // ═══════════════════════════════════════════════════════════════
        // SUMMARY
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Estimated bandwidth reduction percentage.
        /// [STUB] Returns 0 until sync optimizations are implemented.
        /// </summary>
        public int BandwidthReduction;

        /// <summary>
        /// Estimated instruction reduction percentage.
        /// </summary>
        public int InstructionReduction;

        /// <summary>
        /// Total number of optimization entries.
        /// </summary>
        public int TotalOptimizations =>
            ConstantsFolded + LoopsUnrolled + MethodsInlined + 
            StringsInterned + DeadCodeEliminated +
            (SyncPackingApplied ? 1 : 0) + (DeltaSyncApplied ? 1 : 0);
    }

    /// <summary>
    /// Color palette for CE Inspector UI.
    /// </summary>
    public static class CEColors
    {
        // ═══════════════════════════════════════════════════════════════
        // BACKGROUNDS
        // ═══════════════════════════════════════════════════════════════

        public static readonly Color HeaderBg = new Color(0.22f, 0.22f, 0.22f);
        public static readonly Color StatusBarBg = new Color(0.18f, 0.18f, 0.18f);
        public static readonly Color GroupBg = new Color(0.25f, 0.25f, 0.25f);
        public static readonly Color PanelBg = new Color(0.2f, 0.2f, 0.2f);

        // ═══════════════════════════════════════════════════════════════
        // SYNC STATUS BACKGROUNDS
        // ═══════════════════════════════════════════════════════════════

        public static readonly Color SyncNoneBg = new Color(0.3f, 0.3f, 0.3f);
        public static readonly Color SyncManualBg = new Color(0.2f, 0.3f, 0.4f);
        public static readonly Color SyncContinuousBg = new Color(0.2f, 0.35f, 0.25f);

        // ═══════════════════════════════════════════════════════════════
        // BADGE COLORS
        // ═══════════════════════════════════════════════════════════════

        public static readonly Color BadgeCE = new Color(0.5f, 0.5f, 0.5f);
        public static readonly Color BadgeOptimized = new Color(0.3f, 0.7f, 0.3f);
        public static readonly Color BadgeNetcode = new Color(0.3f, 0.5f, 0.95f);
        public static readonly Color BadgePooled = new Color(0.6f, 0.3f, 0.9f);
        public static readonly Color BadgePredicted = new Color(0.3f, 0.8f, 0.8f);

        // ═══════════════════════════════════════════════════════════════
        // TEXT COLORS
        // ═══════════════════════════════════════════════════════════════

        public static readonly Color TextPrimary = new Color(0.9f, 0.9f, 0.9f);
        public static readonly Color TextSecondary = new Color(0.6f, 0.6f, 0.6f);
        public static readonly Color TextPositive = new Color(0.4f, 0.8f, 0.4f);
        public static readonly Color TextWarning = new Color(0.9f, 0.7f, 0.2f);
        public static readonly Color TextError = new Color(0.9f, 0.3f, 0.3f);

        // ═══════════════════════════════════════════════════════════════
        // ACCENT COLORS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// The CE brand purple color (matches the U# bar color).
        /// </summary>
        public static readonly Color CEAccent = new Color32(139, 127, 198, 220);

        /// <summary>
        /// Lighter version of the accent for highlights.
        /// </summary>
        public static readonly Color CEAccentLight = new Color32(170, 160, 220, 255);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // STUB ATTRIBUTES (Not yet implemented - placeholders for future features)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// [STUB] Marks a behaviour as using CE Netcode for players.
    /// This attribute is not yet implemented.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class CENetworkedPlayerAttribute : System.Attribute { }

    /// <summary>
    /// [STUB] Marks a behaviour as using CE Netcode for projectiles.
    /// This attribute is not yet implemented.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class CENetworkedProjectileAttribute : System.Attribute { }

    /// <summary>
    /// [STUB] Marks a behaviour as pooled for object pooling.
    /// This attribute is not yet implemented.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public class CEPooledAttribute : System.Attribute { }

    /// <summary>
    /// [STUB] Marks a field as using client-side prediction.
    /// This attribute is not yet implemented.
    /// </summary>
    [System.AttributeUsage(System.AttributeTargets.Field)]
    public class CEPredictedAttribute : System.Attribute { }
}

