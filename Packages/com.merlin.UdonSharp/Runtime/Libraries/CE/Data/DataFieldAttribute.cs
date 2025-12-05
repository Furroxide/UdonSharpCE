using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Data
{
    /// <summary>
    /// Marks a field for inclusion in DataDictionary serialization with a custom key name.
    ///
    /// Use this attribute on fields of a [DataModel] class to specify the key used
    /// when serializing to DataDictionary or JSON.
    /// </summary>
    /// <example>
    /// <code>
    /// [DataModel]
    /// public class PlayerStats
    /// {
    ///     [DataField("hp")] public int health;
    ///     [DataField("mp")] public int mana;
    ///     [DataField("xp", Optional = true)] public int experience;
    ///     [DataField("lvl", DefaultValue = 1)] public int level;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public class DataFieldAttribute : Attribute
    {
        /// <summary>
        /// The key name used in DataDictionary serialization.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// If true, this field is optional and will not cause errors if missing during deserialization.
        /// </summary>
        public bool Optional { get; set; }

        /// <summary>
        /// Default value to use if the field is missing during deserialization.
        /// Only applies if Optional is true.
        /// </summary>
        public object DefaultValue { get; set; }

        /// <summary>
        /// Creates a DataField attribute with the specified key.
        /// </summary>
        /// <param name="key">The key name to use in serialization</param>
        public DataFieldAttribute(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("DataField key cannot be null or empty", nameof(key));
            }
            Key = key;
        }
    }

    /// <summary>
    /// Marks a field to be excluded from DataDictionary serialization.
    /// Use on fields that should not be persisted.
    /// </summary>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, Inherited = false)]
    public class DataIgnoreAttribute : Attribute
    {
    }
}
