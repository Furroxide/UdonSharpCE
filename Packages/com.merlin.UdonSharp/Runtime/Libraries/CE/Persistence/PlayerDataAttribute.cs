using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Marks a class as persistent player data that maps to VRChat's PlayerData API.
    ///
    /// Use this attribute to define data models that will be saved to and restored from
    /// VRChat's persistent storage system. Each model must have a unique key within the world.
    ///
    /// The attribute is the whole registration step. CE finds every <c>[PlayerData]</c> class during the Udon
    /// compile and generates that model's converter plus non-generic Save/Restore/ToData/FromData/ToJson/
    /// FromJson/Validate/EstimateSize overloads that take the model type directly.
    /// </summary>
    /// <example>
    /// <code>
    /// [PlayerData("rpg_save", Version = 1)]
    /// public class PlayerSaveData
    /// {
    ///     [PersistKey("xp")] public int experience;
    ///     [PersistKey("lvl")] public int level = 1;
    ///     [PersistKey("name")] public string displayName;
    ///     [PersistKey("inv")] public int[] inventory = new int[50];
    /// }
    ///
    /// // No registration call - use the generated overloads with the model type:
    /// PlayerSaveData save = new PlayerSaveData();
    /// SaveResult saveResult = CEPersistence.Save(save);
    /// RestoreResult restoreResult = CEPersistence.Restore(out PlayerSaveData loaded);
    /// </code>
    /// </example>
    /// <remarks>
    /// Do not call CEPersistence.Register from behaviour code: it is editor-only and is a compile error under
    /// Udon. UdonSharp emits the statics of a non-behaviour type into each calling behaviour's own program
    /// heap, so a registration performed in one behaviour is invisible to a save issued from another - which is
    /// why the attribute, resolved at compile time, is the only registration path that works.
    ///
    /// VRChat PlayerData has a 100KB limit per player that every model shares. Check a model against
    /// <see cref="CEPersistence.GetRemainingQuota"/> with the generated <c>EstimateSize(model)</c> overload
    /// before deployment.
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
        /// Reserved; nothing reads it. Setting it to true does not make anything save on its own: neither the
        /// generated code nor the runtime schedules a save from this value, so every save still has to be an
        /// explicit CEPersistence.Save call.
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
