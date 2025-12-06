using System;
using JetBrains.Annotations;
using UnityEngine;
using VRC.SDK3.Data;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Utility class for estimating serialized data sizes.
    ///
    /// Provides methods to estimate the byte size of data when serialized to JSON,
    /// helping ensure persistence data stays within VRChat's 100KB quota.
    /// </summary>
    /// <remarks>
    /// Size estimates are approximate and include JSON formatting overhead.
    /// Actual storage size may vary slightly depending on VRChat's internal storage format.
    /// </remarks>
    [PublicAPI]
    public static class SizeEstimator
    {
        #region Constants

        // JSON overhead estimates (in bytes)
        private const int OBJECT_OVERHEAD = 2;        // {}
        private const int ARRAY_OVERHEAD = 2;         // []
        private const int KEY_OVERHEAD = 3;           // "":
        private const int SEPARATOR_OVERHEAD = 1;     // ,
        private const int STRING_OVERHEAD = 2;        // ""
        private const int NULL_SIZE = 4;              // null

        // Approximate sizes for common types
        private const int BOOL_SIZE = 5;              // false (max)
        private const int INT_MAX_SIZE = 11;          // -2147483648
        private const int LONG_MAX_SIZE = 20;         // -9223372036854775808
        private const int FLOAT_MAX_SIZE = 15;        // -3.402823E+38
        private const int DOUBLE_MAX_SIZE = 24;       // -1.7976931348623157E+308

        #endregion

        #region DataToken Size Estimation

        /// <summary>
        /// Estimates the serialized JSON size of a DataToken.
        /// </summary>
        /// <param name="token">The DataToken to estimate.</param>
        /// <returns>Estimated size in bytes.</returns>
        public static int EstimateDataTokenSize(DataToken token)
        {
            switch (token.TokenType)
            {
                case TokenType.Null:
                    return NULL_SIZE;

                case TokenType.Boolean:
                    return token.Boolean ? 4 : 5; // true or false

                case TokenType.SByte:
                case TokenType.Byte:
                case TokenType.Short:
                case TokenType.UShort:
                case TokenType.Int:
                case TokenType.UInt:
                    return EstimateIntegerSize((long)token.Double);

                case TokenType.Long:
                case TokenType.ULong:
                    return LONG_MAX_SIZE; // Conservative estimate

                case TokenType.Float:
                    return EstimateFloatSize(token.Float);

                case TokenType.Double:
                    return EstimateDoubleSize(token.Double);

                case TokenType.String:
                    return EstimateStringSize(token.String);

                case TokenType.DataList:
                    return EstimateDataListSize(token.DataList);

                case TokenType.DataDictionary:
                    return EstimateDataDictionarySize(token.DataDictionary);

                default:
                    Debug.LogWarning($"[CE.Persistence] SizeEstimator: unknown token type {token.TokenType}");
                    return 0;
            }
        }

        #endregion

        #region Collection Size Estimation

        /// <summary>
        /// Estimates the serialized JSON size of a DataDictionary.
        /// </summary>
        /// <param name="dict">The DataDictionary to estimate.</param>
        /// <returns>Estimated size in bytes.</returns>
        public static int EstimateDataDictionarySize(DataDictionary dict)
        {
            if (dict == null)
                return NULL_SIZE;

            int size = OBJECT_OVERHEAD; // {}
            DataList keys = dict.GetKeys();
            int count = keys.Count;

            for (int i = 0; i < count; i++)
            {
                if (!keys.TryGetValue(i, out DataToken keyToken))
                    continue;

                string key = keyToken.String;
                if (!dict.TryGetValue(key, out DataToken valueToken))
                    continue;

                // Key size: "key":
                size += STRING_OVERHEAD + key.Length + KEY_OVERHEAD;

                // Value size
                size += EstimateDataTokenSize(valueToken);

                // Separator (except for last item)
                if (i < count - 1)
                    size += SEPARATOR_OVERHEAD;
            }

            return size;
        }

        /// <summary>
        /// Estimates the serialized JSON size of a DataList.
        /// </summary>
        /// <param name="list">The DataList to estimate.</param>
        /// <returns>Estimated size in bytes.</returns>
        public static int EstimateDataListSize(DataList list)
        {
            if (list == null)
                return NULL_SIZE;

            int size = ARRAY_OVERHEAD; // []
            int count = list.Count;

            for (int i = 0; i < count; i++)
            {
                if (!list.TryGetValue(i, out DataToken token))
                    continue;

                size += EstimateDataTokenSize(token);

                // Separator (except for last item)
                if (i < count - 1)
                    size += SEPARATOR_OVERHEAD;
            }

            return size;
        }

        #endregion

        #region Primitive Size Estimation

        /// <summary>
        /// Estimates the serialized size of an integer value.
        /// </summary>
        /// <param name="value">The integer value.</param>
        /// <returns>Estimated size in bytes.</returns>
        public static int EstimateIntegerSize(long value)
        {
            if (value == 0)
                return 1;

            int digits = 0;
            if (value < 0)
            {
                digits = 1; // minus sign
                value = -value;
            }

            while (value > 0)
            {
                digits++;
                value /= 10;
            }

            return digits;
        }

        /// <summary>
        /// Estimates the serialized size of a float value.
        /// </summary>
        /// <param name="value">The float value.</param>
        /// <returns>Estimated size in bytes.</returns>
        public static int EstimateFloatSize(float value)
        {
            // For simplicity, use a conservative estimate
            // Actual JSON representation depends on the value
            if (float.IsNaN(value) || float.IsInfinity(value))
                return NULL_SIZE; // JSON typically uses null for special values

            if (value == 0f)
                return 1;

            // Check if it's an integer value
            if (value == (int)value && value >= -2147483648 && value <= 2147483647)
                return EstimateIntegerSize((int)value);

            return FLOAT_MAX_SIZE;
        }

        /// <summary>
        /// Estimates the serialized size of a double value.
        /// </summary>
        /// <param name="value">The double value.</param>
        /// <returns>Estimated size in bytes.</returns>
        public static int EstimateDoubleSize(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return NULL_SIZE;

            if (value == 0d)
                return 1;

            // Check if it's an integer value
            if (value == (long)value && value >= long.MinValue && value <= long.MaxValue)
                return EstimateIntegerSize((long)value);

            return DOUBLE_MAX_SIZE;
        }

        /// <summary>
        /// Estimates the serialized size of a string value.
        /// </summary>
        /// <param name="value">The string value.</param>
        /// <returns>Estimated size in bytes.</returns>
        public static int EstimateStringSize(string value)
        {
            if (value == null)
                return NULL_SIZE;

            // Account for string quotes and potential escaping
            int size = STRING_OVERHEAD + value.Length;

            // Add extra for escape sequences
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"':
                    case '\\':
                    case '\n':
                    case '\r':
                    case '\t':
                        size += 1; // escape character
                        break;
                }

                // Unicode escape sequences for control characters
                if (c < 32)
                    size += 5; // \uXXXX
            }

            return size;
        }

        #endregion

        #region Type-Based Size Estimation

        /// <summary>
        /// Estimates the serialized size of a common Unity/VRChat type.
        /// </summary>
        /// <param name="type">The System.Type to estimate.</param>
        /// <returns>Estimated size in bytes, or -1 if unknown.</returns>
        public static int EstimateTypeSize(Type type)
        {
            if (type == typeof(bool))
                return BOOL_SIZE;
            if (type == typeof(byte) || type == typeof(sbyte))
                return 4; // max 3 digits + possible minus
            if (type == typeof(short) || type == typeof(ushort))
                return 6;
            if (type == typeof(int) || type == typeof(uint))
                return INT_MAX_SIZE;
            if (type == typeof(long) || type == typeof(ulong))
                return LONG_MAX_SIZE;
            if (type == typeof(float))
                return FLOAT_MAX_SIZE;
            if (type == typeof(double))
                return DOUBLE_MAX_SIZE;
            if (type == typeof(string))
                return -1; // Variable length
            if (type == typeof(Vector2))
                return 50; // {"x":...,"y":...}
            if (type == typeof(Vector3))
                return 70; // {"x":...,"y":...,"z":...}
            if (type == typeof(Vector4) || type == typeof(Quaternion))
                return 90; // {"x":...,"y":...,"z":...,"w":...}
            if (type == typeof(Color))
                return 90; // {"r":...,"g":...,"b":...,"a":...}
            if (type == typeof(Color32))
                return 40; // 4 bytes as integers

            return -1; // Unknown type
        }

        /// <summary>
        /// Estimates the average size per element for an array type.
        /// </summary>
        /// <param name="elementType">The array element type.</param>
        /// <returns>Estimated size per element in bytes, or -1 if unknown.</returns>
        public static int EstimateArrayElementSize(Type elementType)
        {
            int baseSize = EstimateTypeSize(elementType);
            if (baseSize < 0)
                return -1;

            // Add separator overhead (average)
            return baseSize + SEPARATOR_OVERHEAD;
        }

        #endregion

        #region Quota Helpers

        /// <summary>
        /// Checks if an estimated size is within the PlayerData quota.
        /// </summary>
        /// <param name="estimatedSize">The estimated size in bytes.</param>
        /// <returns>True if within quota, false otherwise.</returns>
        public static bool IsWithinQuota(int estimatedSize)
        {
            return estimatedSize <= CEPersistence.PLAYER_DATA_QUOTA;
        }

        /// <summary>
        /// Gets the percentage of quota used by an estimated size.
        /// </summary>
        /// <param name="estimatedSize">The estimated size in bytes.</param>
        /// <returns>Percentage of quota used (0-100+).</returns>
        public static float GetQuotaPercentage(int estimatedSize)
        {
            return (estimatedSize / (float)CEPersistence.PLAYER_DATA_QUOTA) * 100f;
        }

        /// <summary>
        /// Formats a byte size into a human-readable string.
        /// </summary>
        /// <param name="bytes">The size in bytes.</param>
        /// <returns>Formatted string (e.g., "15.2 KB").</returns>
        public static string FormatSize(int bytes)
        {
            if (bytes < 1024)
                return $"{bytes} B";
            if (bytes < 1048576)
                return $"{bytes / 1024f:F1} KB";
            return $"{bytes / 1048576f:F2} MB";
        }

        #endregion
    }
}
