using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp.CE.Persistence;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools.Analysis
{
    /// <summary>
    /// Calculates serialized byte size for synced fields.
    /// Supports all VRChat-compatible types including primitives, Unity types, arrays, and strings.
    /// </summary>
    public class FieldSizeCalculator
    {
        #region Constants

        /// <summary>
        /// Maximum bytes for continuous sync mode.
        /// </summary>
        public const int CONTINUOUS_SYNC_LIMIT = 200;

        /// <summary>
        /// Network budget per second in bytes (11 KB/s).
        /// </summary>
        public const int NETWORK_BUDGET_BYTES_PER_SECOND = 11264;

        /// <summary>
        /// Default max string length for sync.
        /// </summary>
        public const int DEFAULT_MAX_STRING_LENGTH = 50;

        /// <summary>
        /// Default array size when unknown.
        /// </summary>
        public const int DEFAULT_ARRAY_SIZE = 16;

        #endregion

        #region Type Size Mappings

        /// <summary>
        /// Raw byte sizes for common types.
        /// </summary>
        private static readonly Dictionary<Type, int> TypeSizes = new Dictionary<Type, int>
        {
            // Primitives
            { typeof(bool), 1 },
            { typeof(byte), 1 },
            { typeof(sbyte), 1 },
            { typeof(short), 2 },
            { typeof(ushort), 2 },
            { typeof(int), 4 },
            { typeof(uint), 4 },
            { typeof(long), 8 },
            { typeof(ulong), 8 },
            { typeof(float), 4 },
            { typeof(double), 8 },
            { typeof(char), 2 },

            // Unity types
            { typeof(Vector2), 8 },
            { typeof(Vector3), 12 },
            { typeof(Vector4), 16 },
            { typeof(Quaternion), 16 },
            { typeof(Color), 16 },
            { typeof(Color32), 4 },
            { typeof(Vector2Int), 8 },
            { typeof(Vector3Int), 12 },
            { typeof(Rect), 16 },
            { typeof(Bounds), 24 },
        };

        /// <summary>
        /// Type name mappings for string-based lookups.
        /// </summary>
        private static readonly Dictionary<string, int> TypeNameSizes = new Dictionary<string, int>
        {
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
            { "VRC.SDKBase.VRCPlayerApi", 4 },
        };

        /// <summary>
        /// Types that support quantization optimization.
        /// </summary>
        private static readonly HashSet<Type> QuantizableTypes = new HashSet<Type>
        {
            typeof(float),
            typeof(double),
            typeof(Vector2),
            typeof(Vector3),
            typeof(Vector4),
            typeof(Quaternion),
            typeof(Color)
        };

        #endregion

        #region Public Methods

        /// <summary>
        /// Calculates the serialized byte size for a field.
        /// </summary>
        /// <param name="field">The field to calculate size for.</param>
        /// <param name="defaultValue">Optional default value for arrays.</param>
        /// <returns>Size calculation result.</returns>
        public SizeResult CalculateSize(FieldInfo field, object defaultValue = null)
        {
            if (field == null)
                return SizeResult.Unknown("null");

            return CalculateSize(field.FieldType, field, defaultValue);
        }

        /// <summary>
        /// Calculates the serialized byte size for a type.
        /// </summary>
        /// <param name="type">The type to calculate size for.</param>
        /// <param name="field">Optional field for array length detection.</param>
        /// <param name="defaultValue">Optional default value for arrays.</param>
        /// <returns>Size calculation result.</returns>
        public SizeResult CalculateSize(Type type, FieldInfo field = null, object defaultValue = null)
        {
            if (type == null)
                return SizeResult.Unknown("null");

            // Check primitives
            if (TypeSizes.TryGetValue(type, out int primitiveSize))
            {
                return SizeResult.Fixed(primitiveSize, GetTypeDescription(type));
            }

            // Handle strings
            if (type == typeof(string))
            {
                return CalculateStringSize(field);
            }

            // Handle arrays
            if (type.IsArray)
            {
                return CalculateArraySize(type, field, defaultValue);
            }

            // Handle enums
            if (type.IsEnum)
            {
                return SizeResult.Fixed(4, $"enum {type.Name} (int)");
            }

            // Check type name mapping
            string typeName = type.FullName ?? type.Name;
            if (TypeNameSizes.TryGetValue(typeName, out int namedSize))
            {
                return SizeResult.Fixed(namedSize, GetTypeDescription(type));
            }

            // Reference types (UdonBehaviour references, etc.)
            if (type.IsClass)
            {
                return SizeResult.Fixed(4, $"{type.Name} (reference ID)");
            }

            // Unknown type
            return SizeResult.Unknown(type.Name);
        }

        /// <summary>
        /// Checks if a type supports quantization.
        /// </summary>
        public bool IsQuantizable(Type type)
        {
            if (type == null)
                return false;

            if (QuantizableTypes.Contains(type))
                return true;

            if (type.IsArray)
            {
                Type elementType = type.GetElementType();
                return elementType != null && QuantizableTypes.Contains(elementType);
            }

            return false;
        }

        /// <summary>
        /// Gets the base byte size for a type.
        /// </summary>
        public int GetTypeSize(Type type)
        {
            if (type == null)
                return 4;

            if (TypeSizes.TryGetValue(type, out int size))
                return size;

            string typeName = type.FullName ?? type.Name;
            if (TypeNameSizes.TryGetValue(typeName, out int namedSize))
                return namedSize;

            if (type.IsEnum)
                return 4;

            if (type.IsClass)
                return 4;

            return 4;
        }

        #endregion

        #region Private Methods

        private SizeResult CalculateStringSize(FieldInfo field)
        {
            int maxLength = DEFAULT_MAX_STRING_LENGTH;

            // Check for MaxLength attribute
            if (field != null)
            {
                var maxLengthAttr = field.GetCustomAttribute<MaxLengthAttribute>();
                if (maxLengthAttr != null)
                {
                    maxLength = maxLengthAttr.Length;
                }
            }

            // String size: 4 byte header + 2 bytes per character (UTF-16)
            int minSize = 4; // Empty string
            int maxSize = 4 + (maxLength * 2);

            return SizeResult.Variable(minSize, maxSize, $"string (max {maxLength} chars, 2 bytes/char + 4 byte header)");
        }

        private SizeResult CalculateArraySize(Type arrayType, FieldInfo field, object defaultValue)
        {
            Type elementType = arrayType.GetElementType();
            if (elementType == null)
                return SizeResult.Unknown($"{arrayType.Name} (no element type)");

            int elementSize = GetTypeSize(elementType);
            int? arrayLength = TryGetArrayLength(field, defaultValue);

            if (arrayLength.HasValue && arrayLength.Value >= 0)
            {
                int totalSize = 4 + (elementSize * arrayLength.Value); // 4 byte header + elements
                return SizeResult.Fixed(totalSize, $"{elementType.Name}[{arrayLength.Value}] ({elementSize}B × {arrayLength.Value} + 4B header)");
            }

            // Unknown size - use default estimate
            int estimatedSize = 4 + (elementSize * DEFAULT_ARRAY_SIZE);
            return SizeResult.Variable(4, estimatedSize, $"{elementType.Name}[] (unknown length, estimated {DEFAULT_ARRAY_SIZE} elements)");
        }

        private int? TryGetArrayLength(FieldInfo field, object defaultValue)
        {
            // Try to get from default value
            if (defaultValue is Array arr)
            {
                return arr.Length;
            }

            // Try to get from field's declaring type instance
            if (field != null)
            {
                // Check for MaxLength attribute
                var maxLengthAttr = field.GetCustomAttribute<MaxLengthAttribute>();
                if (maxLengthAttr != null)
                {
                    return maxLengthAttr.Length;
                }
            }

            return null;
        }

        private string GetTypeDescription(Type type)
        {
            if (type == typeof(bool)) return "bool (1 byte)";
            if (type == typeof(byte)) return "byte (1 byte)";
            if (type == typeof(sbyte)) return "sbyte (1 byte)";
            if (type == typeof(short)) return "short (2 bytes)";
            if (type == typeof(ushort)) return "ushort (2 bytes)";
            if (type == typeof(int)) return "int (4 bytes)";
            if (type == typeof(uint)) return "uint (4 bytes)";
            if (type == typeof(long)) return "long (8 bytes)";
            if (type == typeof(ulong)) return "ulong (8 bytes)";
            if (type == typeof(float)) return "float (4 bytes)";
            if (type == typeof(double)) return "double (8 bytes)";
            if (type == typeof(char)) return "char (2 bytes)";
            if (type == typeof(Vector2)) return "Vector2 (8 bytes)";
            if (type == typeof(Vector3)) return "Vector3 (12 bytes)";
            if (type == typeof(Vector4)) return "Vector4 (16 bytes)";
            if (type == typeof(Quaternion)) return "Quaternion (16 bytes)";
            if (type == typeof(Color)) return "Color (16 bytes)";
            if (type == typeof(Color32)) return "Color32 (4 bytes)";
            if (type == typeof(Vector2Int)) return "Vector2Int (8 bytes)";
            if (type == typeof(Vector3Int)) return "Vector3Int (12 bytes)";
            if (type == typeof(Rect)) return "Rect (16 bytes)";
            if (type == typeof(Bounds)) return "Bounds (24 bytes)";

            return $"{type.Name} ({GetTypeSize(type)} bytes)";
        }

        #endregion

        #region Static Helpers

        /// <summary>
        /// Formats a byte count for display.
        /// </summary>
        public static string FormatBytes(int bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1024 * 1024)
                return $"{bytes / 1024f:F1} KB";
            return $"{bytes / (1024f * 1024f):F2} MB";
        }

        /// <summary>
        /// Calculates quantization savings based on precision value.
        /// </summary>
        /// <param name="quantize">Quantization precision (e.g., 0.01 for 1cm).</param>
        /// <returns>Percentage of bytes saved (0.0 to 1.0).</returns>
        public static float CalculateQuantizationSavings(float quantize)
        {
            if (quantize <= 0)
                return 0f;

            // Higher precision = more bytes needed
            // Lower precision = fewer bytes needed
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
        /// Default delta encoding savings for sparse array updates.
        /// </summary>
        public const float DEFAULT_DELTA_SAVINGS = 0.5f;

        #endregion
    }
}

