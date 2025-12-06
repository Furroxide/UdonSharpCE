using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Constrains a numeric field to a specified range.
    ///
    /// This validation is checked at save time by CEPersistence.Save() and CEPersistence.Validate().
    /// Values outside the range will cause validation to fail.
    /// </summary>
    /// <example>
    /// <code>
    /// [PlayerData("rpg")]
    /// public class RPGStats
    /// {
    ///     [PersistKey("level"), Range(1, 100)]
    ///     public int level = 1;
    ///
    ///     [PersistKey("health"), Range(0, 9999)]
    ///     public int health = 100;
    ///
    ///     [PersistKey("speed"), Range(0.1, 10.0)]
    ///     public float moveSpeed = 1.0f;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class RangeAttribute : Attribute
    {
        /// <summary>
        /// The minimum allowed value (inclusive).
        /// </summary>
        public double Min { get; }

        /// <summary>
        /// The maximum allowed value (inclusive).
        /// </summary>
        public double Max { get; }

        /// <summary>
        /// Creates a new Range constraint with the specified bounds.
        /// </summary>
        /// <param name="min">Minimum value (inclusive).</param>
        /// <param name="max">Maximum value (inclusive).</param>
        /// <exception cref="ArgumentException">Thrown if min is greater than max.</exception>
        public RangeAttribute(double min, double max)
        {
            if (min > max)
            {
                throw new ArgumentException($"Range minimum ({min}) cannot be greater than maximum ({max})");
            }
            Min = min;
            Max = max;
        }

        /// <summary>
        /// Creates a new Range constraint with integer bounds.
        /// </summary>
        /// <param name="min">Minimum value (inclusive).</param>
        /// <param name="max">Maximum value (inclusive).</param>
        public RangeAttribute(int min, int max) : this((double)min, (double)max)
        {
        }

        /// <summary>
        /// Validates that a value is within the specified range.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <returns>True if the value is within range, false otherwise.</returns>
        public bool IsValid(double value)
        {
            return value >= Min && value <= Max;
        }

        /// <summary>
        /// Validates that an integer value is within the specified range.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <returns>True if the value is within range, false otherwise.</returns>
        public bool IsValid(int value)
        {
            return value >= Min && value <= Max;
        }

        /// <summary>
        /// Validates that a float value is within the specified range.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <returns>True if the value is within range, false otherwise.</returns>
        public bool IsValid(float value)
        {
            return value >= Min && value <= Max;
        }
    }

    /// <summary>
    /// Constrains the length of a string or array field.
    ///
    /// This validation is checked at save time by CEPersistence.Save() and CEPersistence.Validate().
    /// Values exceeding the maximum length will cause validation to fail.
    /// </summary>
    /// <example>
    /// <code>
    /// [PlayerData("profile")]
    /// public class PlayerProfile
    /// {
    ///     [PersistKey("name"), MaxLength(32)]
    ///     public string displayName;
    ///
    ///     [PersistKey("bio"), MaxLength(256)]
    ///     public string biography;
    ///
    ///     [PersistKey("inventory"), MaxLength(100)]
    ///     public int[] itemIds = new int[100];
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class MaxLengthAttribute : Attribute
    {
        /// <summary>
        /// The maximum allowed length.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Creates a new MaxLength constraint.
        /// </summary>
        /// <param name="length">The maximum allowed length.</param>
        /// <exception cref="ArgumentException">Thrown if length is not positive.</exception>
        public MaxLengthAttribute(int length)
        {
            if (length <= 0)
            {
                throw new ArgumentException("MaxLength must be a positive number", nameof(length));
            }
            Length = length;
        }

        /// <summary>
        /// Validates that a string does not exceed the maximum length.
        /// </summary>
        /// <param name="value">The string to validate.</param>
        /// <returns>True if the string is within the length limit, false otherwise. Null strings are considered valid.</returns>
        public bool IsValid(string value)
        {
            return value == null || value.Length <= Length;
        }

        /// <summary>
        /// Validates that an array does not exceed the maximum length.
        /// </summary>
        /// <param name="array">The array to validate.</param>
        /// <returns>True if the array is within the length limit, false otherwise. Null arrays are considered valid.</returns>
        public bool IsValid(Array array)
        {
            return array == null || array.Length <= Length;
        }
    }

    /// <summary>
    /// Constrains a string field to not be null or empty.
    ///
    /// This validation is checked at save time by CEPersistence.Save() and CEPersistence.Validate().
    /// </summary>
    /// <example>
    /// <code>
    /// [PlayerData("account")]
    /// public class AccountData
    /// {
    ///     [PersistKey("id"), Required]
    ///     public string uniqueId;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public class RequiredAttribute : Attribute
    {
        /// <summary>
        /// Custom error message to display when validation fails.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Validates that a value is not null or empty.
        /// </summary>
        /// <param name="value">The value to validate.</param>
        /// <returns>True if the value is not null/empty, false otherwise.</returns>
        public bool IsValid(object value)
        {
            if (value == null)
                return false;

            if (value is string str)
                return !string.IsNullOrEmpty(str);

            if (value is Array arr)
                return arr.Length > 0;

            return true;
        }
    }
}
