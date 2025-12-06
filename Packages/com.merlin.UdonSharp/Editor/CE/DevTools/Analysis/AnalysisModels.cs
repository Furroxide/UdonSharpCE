using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools.Analysis
{
    /// <summary>
    /// Severity level for analysis findings.
    /// </summary>
    public enum AnalysisSeverity
    {
        /// <summary>Will cause runtime failure or data loss.</summary>
        Error,
        /// <summary>Likely bug or performance issue.</summary>
        Warning,
        /// <summary>Suggestion or optimization opportunity.</summary>
        Info
    }

    /// <summary>
    /// Result of calculating serialized byte size for a type or field.
    /// </summary>
    public class SizeResult
    {
        /// <summary>
        /// Minimum possible byte size.
        /// </summary>
        public int MinBytes { get; set; }

        /// <summary>
        /// Maximum possible byte size.
        /// </summary>
        public int MaxBytes { get; set; }

        /// <summary>
        /// Whether the size is fixed (min == max).
        /// </summary>
        public bool IsFixed { get; set; }

        /// <summary>
        /// Human-readable description of the size calculation.
        /// </summary>
        public string Description { get; set; }

        /// <summary>
        /// Whether this type is unknown/unsupported.
        /// </summary>
        public bool IsUnknown { get; set; }

        /// <summary>
        /// Creates a fixed-size result.
        /// </summary>
        public static SizeResult Fixed(int bytes, string description)
        {
            return new SizeResult
            {
                MinBytes = bytes,
                MaxBytes = bytes,
                IsFixed = true,
                Description = description
            };
        }

        /// <summary>
        /// Creates a variable-size result.
        /// </summary>
        public static SizeResult Variable(int minBytes, int maxBytes, string description)
        {
            return new SizeResult
            {
                MinBytes = minBytes,
                MaxBytes = maxBytes,
                IsFixed = false,
                Description = description
            };
        }

        /// <summary>
        /// Creates an unknown type result.
        /// </summary>
        public static SizeResult Unknown(string typeName)
        {
            return new SizeResult
            {
                MinBytes = 0,
                MaxBytes = 100,
                IsFixed = false,
                IsUnknown = true,
                Description = $"Unknown type: {typeName}"
            };
        }

        public override string ToString()
        {
            if (IsFixed)
                return $"{MaxBytes}B";
            return $"{MinBytes}-{MaxBytes}B";
        }
    }

    /// <summary>
    /// Analysis result for a single synced field.
    /// </summary>
    public class FieldAnalysisResult
    {
        /// <summary>
        /// The analyzed field.
        /// </summary>
        public FieldInfo Field { get; set; }

        /// <summary>
        /// The field name.
        /// </summary>
        public string FieldName => Field?.Name ?? "unknown";

        /// <summary>
        /// The field type name.
        /// </summary>
        public string TypeName { get; set; }

        /// <summary>
        /// Size estimation result.
        /// </summary>
        public SizeResult Size { get; set; }

        /// <summary>
        /// Whether this field is an array.
        /// </summary>
        public bool IsArray { get; set; }

        /// <summary>
        /// Array length if known, -1 otherwise.
        /// </summary>
        public int ArrayLength { get; set; } = -1;

        /// <summary>
        /// Whether the field has the [UdonSynced] attribute.
        /// </summary>
        public bool IsSynced { get; set; }

        /// <summary>
        /// Whether quantization is enabled via [Sync] attribute.
        /// </summary>
        public bool HasQuantization { get; set; }

        /// <summary>
        /// Quantization value if specified.
        /// </summary>
        public float QuantizeValue { get; set; }

        /// <summary>
        /// Whether delta encoding is enabled via [Sync] attribute.
        /// </summary>
        public bool HasDeltaEncoding { get; set; }

        /// <summary>
        /// Additional notes about the field.
        /// </summary>
        public string Notes { get; set; }

        public override string ToString()
        {
            return $"{FieldName}: {Size}";
        }
    }

    /// <summary>
    /// A limit violation detected during analysis.
    /// </summary>
    public class LimitViolation
    {
        /// <summary>
        /// Severity of the violation.
        /// </summary>
        public AnalysisSeverity Severity { get; set; }

        /// <summary>
        /// Description of the violation.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Suggested fix for the violation.
        /// </summary>
        public string Recommendation { get; set; }

        /// <summary>
        /// Creates an error violation.
        /// </summary>
        public static LimitViolation Error(string message, string recommendation = null)
        {
            return new LimitViolation
            {
                Severity = AnalysisSeverity.Error,
                Message = message,
                Recommendation = recommendation
            };
        }

        /// <summary>
        /// Creates a warning violation.
        /// </summary>
        public static LimitViolation Warning(string message, string recommendation = null)
        {
            return new LimitViolation
            {
                Severity = AnalysisSeverity.Warning,
                Message = message,
                Recommendation = recommendation
            };
        }
    }

    /// <summary>
    /// An optimization recommendation for a field.
    /// </summary>
    public class Recommendation
    {
        /// <summary>
        /// The field this recommendation applies to (may be null for behaviour-level).
        /// </summary>
        public FieldInfo Field { get; set; }

        /// <summary>
        /// Brief description of the issue.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Suggested improvement.
        /// </summary>
        public string Suggestion { get; set; }

        /// <summary>
        /// Estimated bytes saved if the suggestion is applied.
        /// </summary>
        public int EstimatedSavings { get; set; }
    }

    /// <summary>
    /// Analysis result for a single UdonSharpBehaviour type.
    /// </summary>
    public class BehaviourAnalysisResult
    {
        /// <summary>
        /// The analyzed behaviour type.
        /// </summary>
        public Type BehaviourType { get; set; }

        /// <summary>
        /// The sync mode (None, Continuous, Manual).
        /// </summary>
        public string SyncMode { get; set; }

        /// <summary>
        /// Number of instances in the scene.
        /// </summary>
        public int InstanceCount { get; set; } = 1;

        /// <summary>
        /// Analysis results for each synced field.
        /// </summary>
        public List<FieldAnalysisResult> Fields { get; } = new List<FieldAnalysisResult>();

        /// <summary>
        /// Minimum total bytes for all synced fields.
        /// </summary>
        public int MinTotalBytes { get; set; }

        /// <summary>
        /// Maximum total bytes for all synced fields.
        /// </summary>
        public int MaxTotalBytes { get; set; }

        /// <summary>
        /// Estimated bandwidth usage in KB/s.
        /// </summary>
        public float EstimatedBandwidthKBps { get; set; }

        /// <summary>
        /// Whether this behaviour exceeds the continuous sync limit.
        /// </summary>
        public bool ExceedsContinuousLimit { get; set; }

        /// <summary>
        /// Limit violations detected.
        /// </summary>
        public List<LimitViolation> Violations { get; } = new List<LimitViolation>();

        /// <summary>
        /// Optimization recommendations.
        /// </summary>
        public List<Recommendation> Recommendations { get; } = new List<Recommendation>();

        /// <summary>
        /// Gets total bandwidth considering instance count.
        /// </summary>
        public float TotalBandwidthKBps => EstimatedBandwidthKBps * InstanceCount;
    }

    /// <summary>
    /// Analysis result for an entire scene/world.
    /// </summary>
    public class WorldAnalysisResult
    {
        /// <summary>
        /// Analysis results for each behaviour type.
        /// </summary>
        public List<BehaviourAnalysisResult> BehaviourResults { get; } = new List<BehaviourAnalysisResult>();

        /// <summary>
        /// Minimum total bytes across all behaviours.
        /// </summary>
        public int TotalMinBytes { get; set; }

        /// <summary>
        /// Maximum total bytes across all behaviours.
        /// </summary>
        public int TotalMaxBytes { get; set; }

        /// <summary>
        /// Total estimated bandwidth in KB/s.
        /// </summary>
        public float TotalEstimatedBandwidthKBps { get; set; }

        /// <summary>
        /// World-level limit violations.
        /// </summary>
        public List<LimitViolation> Violations { get; } = new List<LimitViolation>();

        /// <summary>
        /// Total number of synced behaviours in the scene.
        /// </summary>
        public int TotalSyncedBehaviours { get; set; }

        /// <summary>
        /// Total number of synced fields across all behaviours.
        /// </summary>
        public int TotalSyncedFields { get; set; }

        /// <summary>
        /// Percentage of the 11 KB/s network budget used.
        /// </summary>
        public float BudgetPercentage => (TotalEstimatedBandwidthKBps / 11f) * 100f;
    }
}

