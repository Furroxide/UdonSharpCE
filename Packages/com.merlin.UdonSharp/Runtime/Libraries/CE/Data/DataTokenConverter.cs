using System;
using UnityEngine;
using VRC.SDK3.Data;

namespace UdonSharp.CE.Data.Internal
{
    /// <summary>
    /// Internal utility for type-safe DataToken conversions.
    /// Handles conversion between C# primitive types and VRChat DataTokens.
    /// </summary>
    internal static class DataTokenConverter
    {
        #region Primitive to DataToken

        public static DataToken ToToken(bool value) => new DataToken(value);
        public static DataToken ToToken(sbyte value) => new DataToken(value);
        public static DataToken ToToken(byte value) => new DataToken(value);
        public static DataToken ToToken(short value) => new DataToken(value);
        public static DataToken ToToken(ushort value) => new DataToken(value);
        public static DataToken ToToken(int value) => new DataToken(value);
        public static DataToken ToToken(uint value) => new DataToken(value);
        public static DataToken ToToken(long value) => new DataToken(value);
        public static DataToken ToToken(ulong value) => new DataToken(value);
        public static DataToken ToToken(float value) => new DataToken(value);
        public static DataToken ToToken(double value) => new DataToken(value);
        public static DataToken ToToken(string value) => value != null ? new DataToken(value) : new DataToken();
        public static DataToken ToToken(DataList value) => value != null ? new DataToken(value) : new DataToken();
        public static DataToken ToToken(DataDictionary value) => value != null ? new DataToken(value) : new DataToken();

        #endregion

        #region DataToken to Primitive

        public static bool ToBool(DataToken token) => token.TokenType == TokenType.Boolean ? token.Boolean : default;
        public static sbyte ToSByte(DataToken token) => token.TokenType == TokenType.SByte ? token.SByte : default;
        public static byte ToByte(DataToken token) => token.TokenType == TokenType.Byte ? token.Byte : default;
        public static short ToShort(DataToken token) => token.TokenType == TokenType.Short ? token.Short : default;
        public static ushort ToUShort(DataToken token) => token.TokenType == TokenType.UShort ? token.UShort : default;
        public static int ToInt(DataToken token) => token.TokenType == TokenType.Int ? token.Int : default;
        public static uint ToUInt(DataToken token) => token.TokenType == TokenType.UInt ? token.UInt : default;
        public static long ToLong(DataToken token) => token.TokenType == TokenType.Long ? token.Long : default;
        public static ulong ToULong(DataToken token) => token.TokenType == TokenType.ULong ? token.ULong : default;
        public static float ToFloat(DataToken token) => token.TokenType == TokenType.Float ? token.Float : default;
        public static double ToDouble(DataToken token) => token.TokenType == TokenType.Double ? token.Double : default;
        public static string ToString(DataToken token) => token.TokenType == TokenType.String ? token.String : null;
        public static DataList ToDataList(DataToken token) => token.TokenType == TokenType.DataList ? token.DataList : null;
        public static DataDictionary ToDataDictionary(DataToken token) => token.TokenType == TokenType.DataDictionary ? token.DataDictionary : null;

        #endregion

        #region Generic Conversion

        /// <summary>
        /// Converts a value of type T to a DataToken.
        /// Supported types: bool, sbyte, byte, short, ushort, int, uint, long, ulong, float, double, string, DataList, DataDictionary.
        /// </summary>
        public static DataToken ToToken<T>(T value)
        {
            // Type switch pattern - UdonSharp compiler can optimize constant type checks
            if (typeof(T) == typeof(bool)) return ToToken((bool)(object)value);
            if (typeof(T) == typeof(sbyte)) return ToToken((sbyte)(object)value);
            if (typeof(T) == typeof(byte)) return ToToken((byte)(object)value);
            if (typeof(T) == typeof(short)) return ToToken((short)(object)value);
            if (typeof(T) == typeof(ushort)) return ToToken((ushort)(object)value);
            if (typeof(T) == typeof(int)) return ToToken((int)(object)value);
            if (typeof(T) == typeof(uint)) return ToToken((uint)(object)value);
            if (typeof(T) == typeof(long)) return ToToken((long)(object)value);
            if (typeof(T) == typeof(ulong)) return ToToken((ulong)(object)value);
            if (typeof(T) == typeof(float)) return ToToken((float)(object)value);
            if (typeof(T) == typeof(double)) return ToToken((double)(object)value);
            if (typeof(T) == typeof(string)) return ToToken((string)(object)value);
            if (typeof(T) == typeof(DataList)) return ToToken((DataList)(object)value);
            if (typeof(T) == typeof(DataDictionary)) return ToToken((DataDictionary)(object)value);

            Debug.LogError($"[CE.Data] Cannot convert type {typeof(T).Name} to DataToken. Supported types: bool, sbyte, byte, short, ushort, int, uint, long, ulong, float, double, string, DataList, DataDictionary.");
            return new DataToken();
        }

        /// <summary>
        /// Converts a DataToken to type T.
        /// Returns default(T) if conversion fails.
        /// </summary>
        public static T FromToken<T>(DataToken token)
        {
            // Type switch pattern
            if (typeof(T) == typeof(bool)) return (T)(object)ToBool(token);
            if (typeof(T) == typeof(sbyte)) return (T)(object)ToSByte(token);
            if (typeof(T) == typeof(byte)) return (T)(object)ToByte(token);
            if (typeof(T) == typeof(short)) return (T)(object)ToShort(token);
            if (typeof(T) == typeof(ushort)) return (T)(object)ToUShort(token);
            if (typeof(T) == typeof(int)) return (T)(object)ToInt(token);
            if (typeof(T) == typeof(uint)) return (T)(object)ToUInt(token);
            if (typeof(T) == typeof(long)) return (T)(object)ToLong(token);
            if (typeof(T) == typeof(ulong)) return (T)(object)ToULong(token);
            if (typeof(T) == typeof(float)) return (T)(object)ToFloat(token);
            if (typeof(T) == typeof(double)) return (T)(object)ToDouble(token);
            if (typeof(T) == typeof(string)) return (T)(object)ToString(token);
            if (typeof(T) == typeof(DataList)) return (T)(object)ToDataList(token);
            if (typeof(T) == typeof(DataDictionary)) return (T)(object)ToDataDictionary(token);

            Debug.LogError($"[CE.Data] Cannot convert DataToken to type {typeof(T).Name}. Supported types: bool, sbyte, byte, short, ushort, int, uint, long, ulong, float, double, string, DataList, DataDictionary.");
            return default;
        }

        #endregion

        #region Type Checking

        /// <summary>
        /// Checks if type T can be converted to/from DataToken.
        /// </summary>
        public static bool CanConvert<T>()
        {
            return typeof(T) == typeof(bool) ||
                   typeof(T) == typeof(sbyte) ||
                   typeof(T) == typeof(byte) ||
                   typeof(T) == typeof(short) ||
                   typeof(T) == typeof(ushort) ||
                   typeof(T) == typeof(int) ||
                   typeof(T) == typeof(uint) ||
                   typeof(T) == typeof(long) ||
                   typeof(T) == typeof(ulong) ||
                   typeof(T) == typeof(float) ||
                   typeof(T) == typeof(double) ||
                   typeof(T) == typeof(string) ||
                   typeof(T) == typeof(DataList) ||
                   typeof(T) == typeof(DataDictionary);
        }

        /// <summary>
        /// Gets the VRChat TokenType for type T.
        /// Returns TokenType.Error if type is not supported.
        /// </summary>
        public static TokenType GetTokenType<T>()
        {
            if (typeof(T) == typeof(bool)) return TokenType.Boolean;
            if (typeof(T) == typeof(sbyte)) return TokenType.SByte;
            if (typeof(T) == typeof(byte)) return TokenType.Byte;
            if (typeof(T) == typeof(short)) return TokenType.Short;
            if (typeof(T) == typeof(ushort)) return TokenType.UShort;
            if (typeof(T) == typeof(int)) return TokenType.Int;
            if (typeof(T) == typeof(uint)) return TokenType.UInt;
            if (typeof(T) == typeof(long)) return TokenType.Long;
            if (typeof(T) == typeof(ulong)) return TokenType.ULong;
            if (typeof(T) == typeof(float)) return TokenType.Float;
            if (typeof(T) == typeof(double)) return TokenType.Double;
            if (typeof(T) == typeof(string)) return TokenType.String;
            if (typeof(T) == typeof(DataList)) return TokenType.DataList;
            if (typeof(T) == typeof(DataDictionary)) return TokenType.DataDictionary;

            return TokenType.Error;
        }

        /// <summary>
        /// Tries to convert a DataToken to the expected type safely.
        /// </summary>
        public static bool TryFromToken<T>(DataToken token, out T result)
        {
            TokenType expectedType = GetTokenType<T>();
            if (expectedType == TokenType.Error || token.TokenType != expectedType)
            {
                result = default;
                return false;
            }

            result = FromToken<T>(token);
            return true;
        }

        #endregion

        #region Number Coercion

        /// <summary>
        /// Converts a numeric DataToken to double, handling all numeric types.
        /// Useful for JSON deserialization where all numbers become doubles.
        /// </summary>
        public static double ToNumber(DataToken token)
        {
            switch (token.TokenType)
            {
                case TokenType.Boolean: return token.Boolean ? 1.0 : 0.0;
                case TokenType.SByte: return token.SByte;
                case TokenType.Byte: return token.Byte;
                case TokenType.Short: return token.Short;
                case TokenType.UShort: return token.UShort;
                case TokenType.Int: return token.Int;
                case TokenType.UInt: return token.UInt;
                case TokenType.Long: return token.Long;
                case TokenType.ULong: return token.ULong;
                case TokenType.Float: return token.Float;
                case TokenType.Double: return token.Double;
                default: return 0.0;
            }
        }

        /// <summary>
        /// Converts a DataToken to int, coercing from any numeric type.
        /// </summary>
        public static int ToIntCoerced(DataToken token)
        {
            return (int)ToNumber(token);
        }

        /// <summary>
        /// Converts a DataToken to float, coercing from any numeric type.
        /// </summary>
        public static float ToFloatCoerced(DataToken token)
        {
            return (float)ToNumber(token);
        }

        #endregion
    }
}
