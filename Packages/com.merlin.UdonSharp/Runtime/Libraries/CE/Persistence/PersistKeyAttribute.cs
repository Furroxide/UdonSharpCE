using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Maps a field to a persistence key within the PlayerData dictionary.
    ///
    /// Use this attribute on fields within a class marked with <see cref="PlayerDataAttribute"/>
    /// to specify how the field should be serialized to VRChat's PlayerData storage.
    /// </summary>
    /// <example>
    /// <code>
    /// [PlayerData("game_save")]
    /// public class SaveData
    /// {
    ///     [PersistKey("hp")] public int health = 100;
    ///     [PersistKey("pos")] public Vector3 position;
    ///     [PersistKey("name", Optional = true)] public string nickname;
    ///     [PersistKey("flags", Optional = true, DefaultValue = 0)] public int flags;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public class PersistKeyAttribute : Attribute
    {
        /// <summary>
        /// The key name used in the serialized DataDictionary.
        /// This is the actual string key stored in PlayerData.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Whether this field is optional during deserialization.
        /// If true, missing data will not cause an error and the field
        /// will retain its default value or the specified DefaultValue.
        /// </summary>
        public bool Optional { get; set; } = false;

        /// <summary>
        /// Default value to use if the field is missing during deserialization.
        /// Only applicable when Optional is true.
        /// </summary>
        public object DefaultValue { get; set; } = null;

        /// <summary>
        /// Creates a new PersistKey attribute with the specified key.
        /// </summary>
        /// <param name="key">The key name for serialization.</param>
        /// <exception cref="ArgumentException">Thrown if key is null or empty.</exception>
        public PersistKeyAttribute(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("PersistKey cannot be null or empty", nameof(key));
            }
            Key = key;
        }
    }

    /// <summary>
    /// Marks a field to be excluded from persistence serialization.
    ///
    /// Use this attribute on fields that should not be saved to PlayerData,
    /// such as cached references, temporary state, or computed values.
    /// </summary>
    /// <example>
    /// <code>
    /// [PlayerData("player")]
    /// public class PlayerState
    /// {
    ///     [PersistKey("score")] public int score;
    ///
    ///     [PersistIgnore] public Transform cachedTransform;  // Not saved
    ///     [PersistIgnore] public bool isInitialized;         // Not saved
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public class PersistIgnoreAttribute : Attribute
    {
    }
}
