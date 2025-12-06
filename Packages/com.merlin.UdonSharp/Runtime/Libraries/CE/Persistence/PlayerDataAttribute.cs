using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Marks a class as persistent player data that maps to VRChat's PlayerData API.
    ///
    /// Use this attribute to define data models that will be saved to and restored from
    /// VRChat's persistent storage system. Each model must have a unique key within the world.
    /// </summary>
    /// <example>
    /// <code>
    /// [PlayerData("rpg_save")]
    /// public class PlayerSaveData
    /// {
    ///     [PersistKey("xp")] public int experience;
    ///     [PersistKey("lvl")] public int level = 1;
    ///     [PersistKey("name")] public string displayName;
    ///     [PersistKey("inv")] public int[] inventory = new int[50];
    /// }
    ///
    /// // Registration (Phase 2 - manual):
    /// CEPersistence.Register&lt;PlayerSaveData&gt;(
    ///     toData: data =&gt; { /* conversion */ },
    ///     fromData: dict =&gt; { /* conversion */ },
    ///     key: "rpg_save",
    ///     version: 1
    /// );
    /// </code>
    /// </example>
    /// <remarks>
    /// VRChat PlayerData has a 100KB limit per player. Use <see cref="CEPersistence.EstimateSize{T}"/>
    /// to check your model's serialized size before deployment.
    /// </remarks>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public class PlayerDataAttribute : Attribute
    {
        /// <summary>
        /// The root key under which this data model is stored in PlayerData.
        /// Must be unique per world. This key is used when calling VRChat's PlayerData API.
        /// </summary>
        public string Key { get; }

        /// <summary>
        /// Schema version for migration support.
        /// Increment this when making breaking changes to field layout.
        /// The version is stored alongside the data and checked during restore.
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// Whether to automatically save when fields are modified.
        /// Default is false (manual save required via CEPersistence.Save).
        /// Note: Auto-save may increase network traffic and should be used carefully.
        /// </summary>
        public bool AutoSave { get; set; } = false;

        /// <summary>
        /// Creates a new PlayerData attribute with the specified storage key.
        /// </summary>
        /// <param name="key">The unique key for this data model in PlayerData storage.</param>
        /// <exception cref="ArgumentException">Thrown if key is null or empty.</exception>
        public PlayerDataAttribute(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                throw new ArgumentException("PlayerData key cannot be null or empty", nameof(key));
            }
            Key = key;
        }
    }
}
