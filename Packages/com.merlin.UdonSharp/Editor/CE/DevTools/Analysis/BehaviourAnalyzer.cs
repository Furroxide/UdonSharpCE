using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools.Analysis
{
    /// <summary>
    /// Analyzes UdonSharpBehaviour types for network bandwidth usage.
    /// Calculates totals, checks limits, and generates optimization recommendations.
    /// </summary>
    public class BehaviourAnalyzer
    {
        private readonly FieldSizeCalculator _sizeCalculator = new FieldSizeCalculator();

        #region Constants

        /// <summary>
        /// Maximum bytes for continuous sync mode.
        /// </summary>
        public const int CONTINUOUS_SYNC_LIMIT = 200;

        /// <summary>
        /// Warning threshold as percentage of continuous limit.
        /// </summary>
        public const float WARNING_THRESHOLD = 0.8f;

        /// <summary>
        /// Sync updates per second for continuous mode.
        /// </summary>
        public const int CONTINUOUS_SYNCS_PER_SECOND = 5;

        /// <summary>
        /// Estimated sync updates per second for manual mode.
        /// </summary>
        public const int MANUAL_SYNCS_PER_SECOND = 1;

        /// <summary>
        /// Network budget in KB/s.
        /// </summary>
        public const float NETWORK_BUDGET_KBPS = 11f;

        #endregion

        #region Public Methods

        /// <summary>
        /// Analyzes a behaviour type for bandwidth usage.
        /// </summary>
        /// <param name="behaviourType">The type to analyze.</param>
        /// <returns>Analysis result with fields, totals, violations, and recommendations.</returns>
        public BehaviourAnalysisResult Analyze(Type behaviourType)
        {
            if (behaviourType == null)
                throw new ArgumentNullException(nameof(behaviourType));

            var result = new BehaviourAnalysisResult
            {
                BehaviourType = behaviourType,
                SyncMode = GetSyncMode(behaviourType)
            };

            // Find all synced fields
            var fields = behaviourType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                if (!IsSyncedField(field))
                    continue;

                var fieldResult = AnalyzeField(field);
                result.Fields.Add(fieldResult);
            }

            // Calculate totals
            result.MinTotalBytes = result.Fields.Sum(f => f.Size.MinBytes);
            result.MaxTotalBytes = result.Fields.Sum(f => f.Size.MaxBytes);

            // Check continuous sync limit
            if (result.SyncMode == "Continuous")
            {
                result.ExceedsContinuousLimit = result.MaxTotalBytes > CONTINUOUS_SYNC_LIMIT;
            }

            // Estimate bandwidth
            result.EstimatedBandwidthKBps = EstimateBandwidth(result);

            // Generate violations and recommendations
            CheckViolations(result);
            GenerateRecommendations(result);

            return result;
        }

        /// <summary>
        /// Analyzes a single field.
        /// </summary>
        public FieldAnalysisResult AnalyzeField(FieldInfo field)
        {
            var result = new FieldAnalysisResult
            {
                Field = field,
                TypeName = field.FieldType.Name,
                IsSynced = IsSyncedField(field),
                IsArray = field.FieldType.IsArray
            };

            // Calculate size
            result.Size = _sizeCalculator.CalculateSize(field);

            // Check for CE.Net [Sync] attribute properties
            var syncAttr = field.GetCustomAttribute<SyncAttribute>();
            if (syncAttr != null)
            {
                result.HasQuantization = syncAttr.Quantize > 0;
                result.QuantizeValue = syncAttr.Quantize;
                result.HasDeltaEncoding = syncAttr.DeltaEncode;

                // Adjust effective size for optimizations
                if (result.HasQuantization || result.HasDeltaEncoding)
                {
                    AdjustSizeForOptimizations(result);
                }
            }

            // Try to get array length
            if (result.IsArray)
            {
                result.ArrayLength = TryGetArrayLength(field);
            }

            return result;
        }

        #endregion

        #region Private Methods

        private bool IsSyncedField(FieldInfo field)
        {
            // Check for [UdonSynced] attribute
            var attrs = field.GetCustomAttributes(true);
            foreach (var attr in attrs)
            {
                string attrName = attr.GetType().Name;
                if (attrName == "UdonSyncedAttribute" || attrName == "UdonSynced")
                    return true;
            }
            return false;
        }

        private string GetSyncMode(Type behaviourType)
        {
            var attrs = behaviourType.GetCustomAttributes(true);
            foreach (var attr in attrs)
            {
                string attrName = attr.GetType().Name;
                if (attrName == "UdonBehaviourSyncModeAttribute" || attrName == "UdonBehaviourSyncMode")
                {
                    // Try to get the sync mode value
                    var modeProperty = attr.GetType().GetProperty("behaviourSyncMode");
                    if (modeProperty != null)
                    {
                        var modeValue = modeProperty.GetValue(attr);
                        if (modeValue != null)
                        {
                            return modeValue.ToString();
                        }
                    }

                    // Fallback to field
                    var modeField = attr.GetType().GetField("behaviourSyncMode");
                    if (modeField != null)
                    {
                        var modeValue = modeField.GetValue(attr);
                        if (modeValue != null)
                        {
                            return modeValue.ToString();
                        }
                    }
                }
            }
            return "None";
        }

        private void AdjustSizeForOptimizations(FieldAnalysisResult result)
        {
            float savingsMultiplier = 1f;

            if (result.HasQuantization)
            {
                float quantizeSavings = FieldSizeCalculator.CalculateQuantizationSavings(result.QuantizeValue);
                savingsMultiplier *= (1f - quantizeSavings);
            }

            if (result.HasDeltaEncoding)
            {
                savingsMultiplier *= (1f - FieldSizeCalculator.DEFAULT_DELTA_SAVINGS);
            }

            // Create adjusted size result
            int adjustedMin = Math.Max(1, (int)(result.Size.MinBytes * savingsMultiplier));
            int adjustedMax = Math.Max(1, (int)(result.Size.MaxBytes * savingsMultiplier));

            string notes = "";
            if (result.HasQuantization)
                notes += $"Quantized ({result.QuantizeValue}); ";
            if (result.HasDeltaEncoding)
                notes += "Delta encoded; ";

            result.Notes = notes.TrimEnd(' ', ';');

            // Update size with optimization note
            result.Size = new SizeResult
            {
                MinBytes = adjustedMin,
                MaxBytes = adjustedMax,
                IsFixed = result.Size.IsFixed,
                Description = $"{result.Size.Description} (optimized: {notes.Trim(' ', ';')})"
            };
        }

        private int TryGetArrayLength(FieldInfo field)
        {
            // Check for MaxLength attribute
            var maxLengthAttr = field.GetCustomAttribute<CE.Persistence.MaxLengthAttribute>();
            if (maxLengthAttr != null)
            {
                return maxLengthAttr.Length;
            }

            return -1;
        }

        private float EstimateBandwidth(BehaviourAnalysisResult result)
        {
            int syncsPerSecond = result.SyncMode switch
            {
                "Continuous" => CONTINUOUS_SYNCS_PER_SECOND,
                "Manual" => MANUAL_SYNCS_PER_SECOND,
                _ => 0
            };

            if (syncsPerSecond == 0)
                return 0f;

            // Use max bytes for conservative estimate
            return (result.MaxTotalBytes * syncsPerSecond) / 1000f;
        }

        private void CheckViolations(BehaviourAnalysisResult result)
        {
            // Check continuous sync limit
            if (result.SyncMode == "Continuous")
            {
                if (result.MaxTotalBytes > CONTINUOUS_SYNC_LIMIT)
                {
                    result.Violations.Add(LimitViolation.Error(
                        $"Continuous sync payload ({result.MaxTotalBytes} bytes) exceeds {CONTINUOUS_SYNC_LIMIT}-byte limit",
                        "Switch to Manual sync mode or reduce synced data"
                    ));
                }
                else if (result.MaxTotalBytes > CONTINUOUS_SYNC_LIMIT * WARNING_THRESHOLD)
                {
                    result.Violations.Add(LimitViolation.Warning(
                        $"Continuous sync payload ({result.MaxTotalBytes} bytes) approaching {CONTINUOUS_SYNC_LIMIT}-byte limit",
                        "Consider Manual sync mode if you need to add more synced fields"
                    ));
                }
            }

            // Check for bandwidth hogs
            if (result.EstimatedBandwidthKBps > NETWORK_BUDGET_KBPS * 0.5f)
            {
                result.Violations.Add(LimitViolation.Warning(
                    $"Behaviour uses {result.EstimatedBandwidthKBps:F1} KB/s ({result.EstimatedBandwidthKBps / NETWORK_BUDGET_KBPS * 100:F0}% of budget)",
                    "Consider reducing sync frequency or data size"
                ));
            }
        }

        private void GenerateRecommendations(BehaviourAnalysisResult result)
        {
            foreach (var field in result.Fields)
            {
                // Large arrays in continuous sync
                if (field.IsArray &&
                    result.SyncMode == "Continuous" &&
                    field.Size.MaxBytes > 50)
                {
                    result.Recommendations.Add(new Recommendation
                    {
                        Field = field.Field,
                        Message = "Large array in continuous sync",
                        Suggestion = "Move to Manual sync with delta encoding, or split into multiple behaviours",
                        EstimatedSavings = (int)(field.Size.MaxBytes * 0.5f)
                    });
                }

                // Float/Vector without quantization
                if (!field.HasQuantization && _sizeCalculator.IsQuantizable(field.Field.FieldType))
                {
                    result.Recommendations.Add(new Recommendation
                    {
                        Field = field.Field,
                        Message = $"{field.TypeName} without quantization",
                        Suggestion = "Use [Sync(Quantize = 0.01f)] to reduce precision and bandwidth",
                        EstimatedSavings = (int)(field.Size.MaxBytes * 0.5f)
                    });
                }

                // String fields that might be IDs
                if (field.Field.FieldType == typeof(string))
                {
                    string fieldName = field.Field.Name.ToLowerInvariant();
                    if (fieldName.Contains("id") || fieldName.Contains("key") || fieldName.Contains("name"))
                    {
                        result.Recommendations.Add(new Recommendation
                        {
                            Field = field.Field,
                            Message = "String field that might be an ID or key",
                            Suggestion = "Consider using int index instead of string (4 bytes vs ~50+ bytes)",
                            EstimatedSavings = field.Size.MaxBytes - 4
                        });
                    }
                }

                // Large arrays without delta encoding
                if (field.IsArray && !field.HasDeltaEncoding && field.Size.MaxBytes > 100)
                {
                    result.Recommendations.Add(new Recommendation
                    {
                        Field = field.Field,
                        Message = "Large array without delta encoding",
                        Suggestion = "Use [Sync(DeltaEncode = true)] to only sync changed elements",
                        EstimatedSavings = (int)(field.Size.MaxBytes * FieldSizeCalculator.DEFAULT_DELTA_SAVINGS)
                    });
                }
            }

            // Behaviour-level recommendations
            if (result.SyncMode == "Continuous" && result.Fields.Count > 5)
            {
                result.Recommendations.Add(new Recommendation
                {
                    Field = null,
                    Message = "Many fields in continuous sync",
                    Suggestion = "Consider splitting into multiple behaviours to reduce per-behaviour payload",
                    EstimatedSavings = 0
                });
            }
        }

        #endregion
    }
}

