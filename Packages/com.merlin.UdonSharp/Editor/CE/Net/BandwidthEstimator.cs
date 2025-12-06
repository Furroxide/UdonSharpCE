using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Net
{
    /// <summary>
    /// Estimation result for a single synced field.
    /// </summary>
    public class FieldBandwidthEstimate
    {
        /// <summary>
        /// The field name.
        /// </summary>
        public string FieldName { get; set; }

        /// <summary>
        /// Raw byte size without optimizations.
        /// </summary>
        public int RawBytes { get; set; }

        /// <summary>
        /// Effective byte size after applying optimizations.
        /// </summary>
        public int EffectiveBytes { get; set; }

        /// <summary>
        /// Percentage saved by quantization (0.0 to 1.0).
        /// </summary>
        public float QuantizationSavings { get; set; }

        /// <summary>
        /// Percentage saved by delta encoding (0.0 to 1.0).
        /// </summary>
        public float DeltaSavings { get; set; }

        /// <summary>
        /// The field type name.
        /// </summary>
        public string TypeName { get; set; }

        /// <summary>
        /// Whether this is an array field.
        /// </summary>
        public bool IsArray { get; set; }

        /// <summary>
        /// Array length if known, -1 otherwise.
        /// </summary>
        public int ArrayLength { get; set; } = -1;

        /// <summary>
        /// Notes about the estimation.
        /// </summary>
        public string Notes { get; set; }

        public override string ToString()
        {
            if (RawBytes == EffectiveBytes)
            {
                return $"{FieldName}: {RawBytes}B";
            }
            return $"{FieldName}: {RawBytes}B -> ~{EffectiveBytes}B (optimized)";
        }
    }

    /// <summary>
    /// Estimation result for an entire behaviour's sync payload.
    /// </summary>
    public class BehaviourBandwidthEstimate
    {
        /// <summary>
        /// The behaviour type name.
        /// </summary>
        public string TypeName { get; set; }

        /// <summary>
        /// Total raw bytes without optimizations.
        /// </summary>
        public int TotalRawBytes { get; set; }

        /// <summary>
        /// Total effective bytes after optimizations.
        /// </summary>
        public int TotalEffectiveBytes { get; set; }

        /// <summary>
        /// Individual field estimates.
        /// </summary>
        public List<FieldBandwidthEstimate> Fields { get; set; } = new List<FieldBandwidthEstimate>();

        /// <summary>
        /// Whether the payload exceeds the continuous sync limit.
        /// </summary>
        public bool ExceedsContinuousLimit { get; set; }

        /// <summary>
        /// Percentage of the 11KB/s network budget this behaviour uses.
        /// </summary>
        public float BudgetPercentage { get; set; }

        /// <summary>
        /// The behaviour's sync mode.
        /// </summary>
        public string SyncMode { get; set; }
    }

    /// <summary>
    /// Estimates network bandwidth usage for synced fields.
    ///
    /// Supports quantization and delta encoding hints from CE.Net attributes
    /// to provide more accurate estimates.
    /// </summary>
    public static class BandwidthEstimator
    {
        #region Constants

        /// <summary>
        /// Maximum bytes for continuous sync mode.
        /// </summary>
        public const int CONTINUOUS_SYNC_LIMIT = 200;

        /// <summary>
        /// Network budget per second in bytes.
        /// </summary>
        public const int NETWORK_BUDGET_BYTES_PER_SECOND = 11264;

        /// <summary>
        /// Default quantization savings (50% for floats).
        /// </summary>
        public const float DEFAULT_QUANTIZATION_SAVINGS = 0.5f;

        /// <summary>
        /// Default delta encoding savings (50% for sparse updates).
        /// </summary>
        public const float DEFAULT_DELTA_SAVINGS = 0.5f;

        #endregion

        #region Type Sizes

        /// <summary>
        /// Raw byte sizes for common types.
        /// </summary>
        private static readonly Dictionary<string, int> TypeSizes = new Dictionary<string, int>
        {
            // Primitives
            { "System.Boolean", 1 },
            { "System.Byte", 1 },
            { "System.SByte", 1 },
            { "System.Int16", 2 },
            { "System.UInt16", 2 },
            { "System.Int32", 4 },
            { "System.UInt32", 4 },
            { "System.Int64", 8 },
            { "System.UInt64", 8 },
            { "System.Single", 4 },
            { "System.Double", 8 },
            { "System.Char", 2 },

            // Unity types
            { "UnityEngine.Vector2", 8 },
            { "UnityEngine.Vector3", 12 },
            { "UnityEngine.Vector4", 16 },
            { "UnityEngine.Quaternion", 16 },
            { "UnityEngine.Color", 16 },
            { "UnityEngine.Color32", 4 },
            { "UnityEngine.Vector2Int", 8 },
            { "UnityEngine.Vector3Int", 12 },
            { "UnityEngine.Rect", 16 },
            { "UnityEngine.Bounds", 24 },

            // VRChat types
            { "VRC.SDKBase.VRCPlayerApi", 4 }, // Player ID
        };

        /// <summary>
        /// Types that support quantization optimization.
        /// </summary>
        private static readonly HashSet<string> QuantizableTypes = new HashSet<string>
        {
            "System.Single",
            "System.Double",
            "UnityEngine.Vector2",
            "UnityEngine.Vector3",
            "UnityEngine.Vector4",
            "UnityEngine.Quaternion",
            "UnityEngine.Color"
        };

        #endregion

        #region Field Estimation

        /// <summary>
        /// Estimates bandwidth for a synced field.
        /// </summary>
        /// <param name="field">The field symbol to estimate.</param>
        /// <param name="quantize">Quantization value from [Sync] attribute (0 = disabled).</param>
        /// <param name="deltaEncode">Delta encoding flag from [Sync] attribute.</param>
        /// <returns>The field bandwidth estimate.</returns>
        public static FieldBandwidthEstimate EstimateField(
            FieldSymbol field,
            float quantize = 0f,
            bool deltaEncode = false)
        {
            var estimate = new FieldBandwidthEstimate
            {
                FieldName = field.Name
            };

            TypeSymbol fieldType = field.Type;
            string typeName = fieldType.RoslynSymbol?.ToString() ?? "unknown";
            estimate.TypeName = typeName;

            // Handle arrays
            if (fieldType.IsArray)
            {
                estimate.IsArray = true;
                TypeSymbol elementType = fieldType.ElementType;
                int elementSize = GetTypeSize(elementType);

                // Try to get array size from initializer
                int arrayLength = TryGetArrayLength(field);
                estimate.ArrayLength = arrayLength;

                if (arrayLength > 0)
                {
                    estimate.RawBytes = elementSize * arrayLength;
                }
                else
                {
                    // Unknown size - estimate 16 elements
                    estimate.RawBytes = elementSize * 16;
                    estimate.Notes = "Array size unknown, estimated 16 elements";
                }

                // Apply delta encoding savings to arrays
                if (deltaEncode)
                {
                    estimate.DeltaSavings = DEFAULT_DELTA_SAVINGS;
                }
            }
            else
            {
                // Non-array field
                estimate.RawBytes = GetTypeSize(fieldType);
            }

            // Handle strings
            if (typeName == "System.String" || typeName == "string")
            {
                estimate.RawBytes = 50; // Max synced string length
                estimate.Notes = "String capped at 50 characters";
            }

            // Apply quantization savings to float types
            if (quantize > 0 && IsQuantizable(typeName))
            {
                estimate.QuantizationSavings = CalculateQuantizationSavings(quantize);
            }

            // Calculate effective bytes
            float savingsMultiplier = (1f - estimate.QuantizationSavings) * (1f - estimate.DeltaSavings);
            estimate.EffectiveBytes = Math.Max(1, (int)(estimate.RawBytes * savingsMultiplier));

            return estimate;
        }

        /// <summary>
        /// Estimates bandwidth for an entire behaviour.
        /// </summary>
        /// <param name="type">The type symbol to analyze.</param>
        /// <param name="syncMode">The sync mode (Continuous, Manual, None).</param>
        /// <returns>The behaviour bandwidth estimate.</returns>
        public static BehaviourBandwidthEstimate EstimateBehaviour(
            TypeSymbol type,
            string syncMode = "Manual")
        {
            var estimate = new BehaviourBandwidthEstimate
            {
                TypeName = type.Name,
                SyncMode = syncMode
            };

            if (type.FieldSymbols == null)
                return estimate;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                if (!field.IsSynced)
                    continue;

                // Extract sync attribute properties
                float quantize = GetQuantizeValue(field);
                bool deltaEncode = GetDeltaEncodeValue(field);

                var fieldEstimate = EstimateField(field, quantize, deltaEncode);
                estimate.Fields.Add(fieldEstimate);

                estimate.TotalRawBytes += fieldEstimate.RawBytes;
                estimate.TotalEffectiveBytes += fieldEstimate.EffectiveBytes;
            }

            // Check against continuous sync limit
            if (syncMode == "Continuous")
            {
                estimate.ExceedsContinuousLimit = estimate.TotalEffectiveBytes > CONTINUOUS_SYNC_LIMIT;
            }

            // Calculate budget percentage (assuming 10 syncs per second for continuous)
            int syncsPerSecond = syncMode == "Continuous" ? 10 : 1;
            int bytesPerSecond = estimate.TotalEffectiveBytes * syncsPerSecond;
            estimate.BudgetPercentage = (float)bytesPerSecond / NETWORK_BUDGET_BYTES_PER_SECOND;

            return estimate;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets the byte size of a type.
        /// </summary>
        private static int GetTypeSize(TypeSymbol type)
        {
            if (type?.RoslynSymbol == null)
                return 4;

            string typeName = type.RoslynSymbol.ToString();

            if (TypeSizes.TryGetValue(typeName, out int size))
            {
                return size;
            }

            // Enums are typically int
            if (type.RoslynSymbol.TypeKind == TypeKind.Enum)
            {
                return 4;
            }

            // Reference types (UdonBehaviour references, etc.)
            if (type.RoslynSymbol.IsReferenceType)
            {
                return 4; // Object reference ID
            }

            // Unknown - conservative estimate
            return 4;
        }

        /// <summary>
        /// Checks if a type supports quantization.
        /// </summary>
        private static bool IsQuantizable(string typeName)
        {
            return QuantizableTypes.Contains(typeName) ||
                   typeName.EndsWith("[]") && QuantizableTypes.Contains(typeName.TrimEnd('[', ']'));
        }

        /// <summary>
        /// Calculates quantization savings based on quantize value.
        /// </summary>
        private static float CalculateQuantizationSavings(float quantize)
        {
            // Higher precision = more bytes needed
            // Lower precision = fewer bytes needed
            // quantize=0.01 (1cm precision) -> ~50% savings
            // quantize=0.1 (10cm precision) -> ~60% savings
            // quantize=1.0 (1m precision) -> ~75% savings

            if (quantize >= 1f)
                return 0.75f;
            if (quantize >= 0.1f)
                return 0.6f;
            if (quantize >= 0.01f)
                return 0.5f;
            if (quantize >= 0.001f)
                return 0.3f;

            return 0f;
        }

        /// <summary>
        /// Tries to get array length from field initializer.
        /// </summary>
        private static int TryGetArrayLength(FieldSymbol field)
        {
            if (field.InitializerSyntax == null)
                return -1;

            string initializer = field.InitializerSyntax.ToString().Trim();

            // Pattern: new Type[N]
            int bracketStart = initializer.IndexOf('[');
            int bracketEnd = initializer.IndexOf(']');

            if (bracketStart >= 0 && bracketEnd > bracketStart)
            {
                string sizeStr = initializer.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim();

                if (int.TryParse(sizeStr, out int size))
                {
                    return size;
                }
            }

            // Pattern: new Type[] { elem, elem, ... }
            int braceStart = initializer.IndexOf('{');
            int braceEnd = initializer.LastIndexOf('}');

            if (braceStart >= 0 && braceEnd > braceStart)
            {
                string elementsStr = initializer.Substring(braceStart + 1, braceEnd - braceStart - 1);
                if (string.IsNullOrWhiteSpace(elementsStr))
                    return 0;

                return elementsStr.Split(',').Length;
            }

            return -1;
        }

        /// <summary>
        /// Gets the quantize value from a field's [Sync] attribute.
        /// </summary>
        private static float GetQuantizeValue(FieldSymbol field)
        {
            if (field.RoslynSymbol == null)
                return 0f;

            foreach (var attr in field.RoslynSymbol.GetAttributes())
            {
                string attrName = attr.AttributeClass?.Name ?? "";
                if (attrName == "SyncAttribute" || attrName == "Sync")
                {
                    // Check named arguments
                    foreach (var namedArg in attr.NamedArguments)
                    {
                        if (namedArg.Key == "Quantize" && namedArg.Value.Value is float value)
                        {
                            return value;
                        }
                    }
                }
            }

            return 0f;
        }

        /// <summary>
        /// Gets the delta encode value from a field's [Sync] attribute.
        /// </summary>
        private static bool GetDeltaEncodeValue(FieldSymbol field)
        {
            if (field.RoslynSymbol == null)
                return false;

            foreach (var attr in field.RoslynSymbol.GetAttributes())
            {
                string attrName = attr.AttributeClass?.Name ?? "";
                if (attrName == "SyncAttribute" || attrName == "Sync")
                {
                    // Check named arguments
                    foreach (var namedArg in attr.NamedArguments)
                    {
                        if (namedArg.Key == "DeltaEncode" && namedArg.Value.Value is bool value)
                        {
                            return value;
                        }
                    }
                }
            }

            return false;
        }

        #endregion

        #region Formatting

        /// <summary>
        /// Formats a byte count for display.
        /// </summary>
        public static string FormatBytes(int bytes)
        {
            if (bytes < 1024)
                return $"{bytes}B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024.0:F1}KB";
            return $"{bytes / (1024.0 * 1024.0):F2}MB";
        }

        /// <summary>
        /// Creates a summary string for a behaviour estimate.
        /// </summary>
        public static string FormatBehaviourSummary(BehaviourBandwidthEstimate estimate)
        {
            var summary = $"{estimate.TypeName}: {FormatBytes(estimate.TotalEffectiveBytes)}";

            if (estimate.TotalRawBytes != estimate.TotalEffectiveBytes)
            {
                summary += $" (raw: {FormatBytes(estimate.TotalRawBytes)})";
            }

            if (estimate.ExceedsContinuousLimit)
            {
                summary += $" [EXCEEDS {CONTINUOUS_SYNC_LIMIT}B LIMIT]";
            }

            return summary;
        }

        #endregion
    }
}
