using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp.CE.Persistence;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates that [PlayerData] schemas are within VRChat's 100KB limit.
    /// Uses existing SizeEstimator logic for accurate size calculation.
    /// </summary>
    public class PersistenceSizeValidator : IValidator
    {
        public string Name => "Persistence Size";
        public string Category => "Persistence";
        public string Description => "Checks [PlayerData] schemas against 100KB storage limit.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// VRChat PlayerData quota in bytes (100 KB).
        /// </summary>
        private const int QUOTA_BYTES = 100 * 1024;

        /// <summary>
        /// Warning threshold (80% of quota).
        /// </summary>
        private const float WARNING_THRESHOLD = 0.8f;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();

            foreach (var type in context.BehaviourTypes)
            {
                // Check if type has [PlayerData] attribute
                var playerDataAttr = GetPlayerDataAttribute(type);
                if (playerDataAttr == null)
                    continue;

                // Estimate schema size
                int estimatedSize = EstimateSchemaSize(type);

                if (estimatedSize > QUOTA_BYTES)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Error,
                        ValidatorName = Name,
                        Message = $"Schema '{type.Name}' exceeds 100KB limit ({FormatSize(estimatedSize)})",
                        Details = "VRChat limits PlayerData to 100KB per world per player. " +
                                 "Reduce field sizes, remove unused fields, or compress data. " +
                                 "Consider storing large data in external systems.",
                        RelatedType = type,
                        FilePath = context.GetSourceFile(type)
                    });
                }
                else if (estimatedSize > QUOTA_BYTES * WARNING_THRESHOLD)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        ValidatorName = Name,
                        Message = $"Schema '{type.Name}' approaching 100KB limit ({FormatSize(estimatedSize)})",
                        Details = "Consider optimizing to leave headroom for future additions. " +
                                 "Current usage: " + ((float)estimatedSize / QUOTA_BYTES * 100).ToString("F0") + "% of quota.",
                        RelatedType = type,
                        FilePath = context.GetSourceFile(type)
                    });
                }

                // Check for fields without size constraints
                CheckFieldConstraints(type, issues, context);
            }

            return issues;
        }

        private object GetPlayerDataAttribute(Type type)
        {
            var attrs = type.GetCustomAttributes(true);
            foreach (var attr in attrs)
            {
                string attrName = attr.GetType().Name;
                if (attrName == "PlayerDataAttribute" || attrName == "PlayerData")
                {
                    return attr;
                }
            }
            return null;
        }

        private int EstimateSchemaSize(Type type)
        {
            int totalSize = 0;

            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                // Check for [PersistKey] attribute
                if (!HasPersistKeyAttribute(field))
                    continue;

                totalSize += EstimateFieldSize(field);
            }

            // Add overhead for JSON structure
            totalSize += 50; // Base object overhead

            return totalSize;
        }

        private bool HasPersistKeyAttribute(FieldInfo field)
        {
            var attrs = field.GetCustomAttributes(true);
            foreach (var attr in attrs)
            {
                string attrName = attr.GetType().Name;
                if (attrName == "PersistKeyAttribute" || attrName == "PersistKey")
                {
                    return true;
                }
            }
            return false;
        }

        private int EstimateFieldSize(FieldInfo field)
        {
            Type fieldType = field.FieldType;

            // Get key name overhead
            string keyName = GetPersistKeyName(field) ?? field.Name;
            int keyOverhead = keyName.Length + 5; // "key": 

            // Estimate value size
            int valueSize = EstimateTypeSize(fieldType, field);

            return keyOverhead + valueSize;
        }

        private string GetPersistKeyName(FieldInfo field)
        {
            var attrs = field.GetCustomAttributes(true);
            foreach (var attr in attrs)
            {
                string attrName = attr.GetType().Name;
                if (attrName == "PersistKeyAttribute" || attrName == "PersistKey")
                {
                    // Try to get Key property
                    var keyProp = attr.GetType().GetProperty("Key");
                    if (keyProp != null)
                    {
                        return keyProp.GetValue(attr) as string;
                    }

                    // Try constructor argument
                    var keyField = attr.GetType().GetField("key", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (keyField != null)
                    {
                        return keyField.GetValue(attr) as string;
                    }
                }
            }
            return null;
        }

        private int EstimateTypeSize(Type type, FieldInfo field = null)
        {
            // Use JSON-like size estimation

            if (type == typeof(bool))
                return 5; // "false"
            if (type == typeof(int) || type == typeof(uint))
                return 11; // "-2147483648"
            if (type == typeof(long) || type == typeof(ulong))
                return 20;
            if (type == typeof(float))
                return 15;
            if (type == typeof(double))
                return 24;
            if (type == typeof(byte) || type == typeof(sbyte))
                return 4;
            if (type == typeof(short) || type == typeof(ushort))
                return 6;

            if (type == typeof(string))
            {
                // Check for MaxLength attribute
                int maxLength = 256; // Default
                if (field != null)
                {
                    var maxLengthAttr = field.GetCustomAttribute<MaxLengthAttribute>();
                    if (maxLengthAttr != null)
                    {
                        maxLength = maxLengthAttr.Length;
                    }
                }
                return maxLength + 2; // quotes
            }

            if (type.IsArray)
            {
                Type elementType = type.GetElementType();
                int elementSize = EstimateTypeSize(elementType);

                // Check for MaxLength attribute
                int maxLength = 100; // Default
                if (field != null)
                {
                    var maxLengthAttr = field.GetCustomAttribute<MaxLengthAttribute>();
                    if (maxLengthAttr != null)
                    {
                        maxLength = maxLengthAttr.Length;
                    }
                }

                return 2 + (elementSize + 1) * maxLength; // [] + elements + commas
            }

            // Unity types (stored as JSON objects)
            if (type == typeof(UnityEngine.Vector2))
                return 50;
            if (type == typeof(UnityEngine.Vector3))
                return 70;
            if (type == typeof(UnityEngine.Vector4) || type == typeof(UnityEngine.Quaternion))
                return 90;
            if (type == typeof(UnityEngine.Color))
                return 90;
            if (type == typeof(UnityEngine.Color32))
                return 40;

            // Unknown - conservative estimate
            return 100;
        }

        private void CheckFieldConstraints(Type type, List<ValidationIssue> issues, ValidationContext context)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in fields)
            {
                if (!HasPersistKeyAttribute(field))
                    continue;

                // Check string without MaxLength
                if (field.FieldType == typeof(string))
                {
                    var maxLengthAttr = field.GetCustomAttribute<MaxLengthAttribute>();
                    if (maxLengthAttr == null)
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Info,
                            ValidatorName = Name,
                            Message = $"String field '{field.Name}' has no MaxLength constraint",
                            Details = "Consider adding [MaxLength(N)] to prevent unexpectedly large strings.",
                            RelatedType = type,
                            RelatedField = field,
                            FilePath = context.GetSourceFile(type)
                        });
                    }
                }

                // Check array without MaxLength
                if (field.FieldType.IsArray)
                {
                    var maxLengthAttr = field.GetCustomAttribute<MaxLengthAttribute>();
                    if (maxLengthAttr == null)
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Info,
                            ValidatorName = Name,
                            Message = $"Array field '{field.Name}' has no MaxLength constraint",
                            Details = "Consider adding [MaxLength(N)] to limit array size and prevent quota overflow.",
                            RelatedType = type,
                            RelatedField = field,
                            FilePath = context.GetSourceFile(type)
                        });
                    }
                }
            }
        }

        private string FormatSize(int bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024f:F1} KB";
            return $"{bytes / (1024f * 1024f):F2} MB";
        }
    }
}

