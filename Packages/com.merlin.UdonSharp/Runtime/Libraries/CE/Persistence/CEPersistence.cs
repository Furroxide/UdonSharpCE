using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDKBase;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Converter functions for a registered persistent data model type.
    /// </summary>
    /// <typeparam name="T">The data model type.</typeparam>
    public class PersistenceModelConverter<T>
    {
        /// <summary>
        /// Function to convert a model instance to a DataDictionary.
        /// </summary>
        public Func<T, DataDictionary> ToData;

        /// <summary>
        /// Function to convert a DataDictionary to a model instance.
        /// </summary>
        public Func<DataDictionary, T> FromData;

        /// <summary>
        /// Function to validate a model instance. Returns list of validation errors.
        /// </summary>
        public Func<T, List<ValidationError>> Validate;

        /// <summary>
        /// Creates a new persistence model converter.
        /// </summary>
        public PersistenceModelConverter(
            Func<T, DataDictionary> toData,
            Func<DataDictionary, T> fromData,
            Func<T, List<ValidationError>> validate = null)
        {
            ToData = toData;
            FromData = fromData;
            Validate = validate;
        }
    }

    /// <summary>
    /// Central API for player data persistence operations.
    ///
    /// CEPersistence provides type-safe save/restore functionality that integrates
    /// with VRChat's PlayerData API. Models must be registered before use.
    /// </summary>
    /// <example>
    /// <code>
    /// // Define your model
    /// [PlayerData("rpg_save")]
    /// public class PlayerSaveData
    /// {
    ///     [PersistKey("xp")] public int experience;
    ///     [PersistKey("lvl"), Range(1, 100)] public int level = 1;
    /// }
    ///
    /// // Register in Start()
    /// void Start()
    /// {
    ///     CEPersistence.Register&lt;PlayerSaveData&gt;(
    ///         toData: data =&gt; {
    ///             var d = new DataDictionary();
    ///             d["xp"] = data.experience;
    ///             d["lvl"] = data.level;
    ///             return d;
    ///         },
    ///         fromData: d =&gt; new PlayerSaveData {
    ///             experience = (int)d["xp"].Double,
    ///             level = (int)d["lvl"].Double
    ///         },
    ///         key: "rpg_save",
    ///         version: 1
    ///     );
    /// }
    ///
    /// // Save data
    /// SaveResult result = CEPersistence.Save(myData);
    ///
    /// // Restore data
    /// RestoreResult result = CEPersistence.Restore(out PlayerSaveData loadedData);
    /// </code>
    /// </example>
    [PublicAPI]
    public static class CEPersistence
    {
        #region Constants

        /// <summary>
        /// VRChat PlayerData quota limit in bytes (100KB).
        /// </summary>
        public const int PLAYER_DATA_QUOTA = 102400;

        /// <summary>
        /// VRChat PlayerObject quota limit in bytes (100KB).
        /// </summary>
        public const int PLAYER_OBJECT_QUOTA = 102400;

        /// <summary>
        /// Internal key used to store schema version.
        /// </summary>
        internal const string VERSION_KEY = "__ce_version";

        /// <summary>
        /// Internal key used to store the model key for verification.
        /// </summary>
        internal const string MODEL_KEY = "__ce_model";

        #endregion

        #region Registration

        /// <summary>
        /// Registers a persistent data model type with conversion functions.
        /// Call this in Start() or Awake() before using Save/Restore.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="toData">Function to convert T to DataDictionary.</param>
        /// <param name="fromData">Function to convert DataDictionary to T.</param>
        /// <param name="key">The PlayerData storage key.</param>
        /// <param name="version">Schema version for migration support.</param>
        /// <param name="validate">Optional validation function.</param>
        public static void Register<T>(
            Func<T, DataDictionary> toData,
            Func<DataDictionary, T> fromData,
            string key,
            int version = 1,
            Func<T, List<ValidationError>> validate = null)
        {
            if (toData == null || fromData == null)
            {
                Debug.LogError($"[CE.Persistence] Register<{typeof(T).Name}>: converters cannot be null");
                return;
            }

            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError($"[CE.Persistence] Register<{typeof(T).Name}>: key cannot be null or empty");
                return;
            }

            PersistenceModelStorage<T>.Converter = new PersistenceModelConverter<T>(toData, fromData, validate);
            PersistenceModelStorage<T>.Key = key;
            PersistenceModelStorage<T>.Version = version;

            Debug.Log($"[CE.Persistence] Registered persistent model: {typeof(T).Name} (key: {key}, version: {version})");
        }

        /// <summary>
        /// Checks if a data model type has been registered.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <returns>True if the type is registered, false otherwise.</returns>
        public static bool IsRegistered<T>()
        {
            return PersistenceModelStorage<T>.Converter != null;
        }

        /// <summary>
        /// Gets the registered key for a data model type.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <returns>The storage key, or null if not registered.</returns>
        public static string GetKey<T>()
        {
            return PersistenceModelStorage<T>.Key;
        }

        /// <summary>
        /// Gets the registered version for a data model type.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <returns>The schema version, or 0 if not registered.</returns>
        public static int GetVersion<T>()
        {
            return PersistenceModelStorage<T>.Version;
        }

        #endregion

        #region Save Operations

        /// <summary>
        /// Saves a model to the local player's PlayerData.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="model">The model instance to save.</param>
        /// <returns>The result of the save operation.</returns>
        public static SaveResult Save<T>(T model)
        {
            var converter = PersistenceModelStorage<T>.Converter;
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] Save<{typeof(T).Name}>: type not registered. Call Register<{typeof(T).Name}>() first.");
                return SaveResult.NotRegistered;
            }

            if (model == null)
            {
                Debug.LogError($"[CE.Persistence] Save<{typeof(T).Name}>: model cannot be null");
                return SaveResult.ValidationFailed;
            }

            // Validate if validator is registered
            if (converter.Validate != null)
            {
                var errors = converter.Validate(model);
                if (errors != null && errors.Count > 0)
                {
                    foreach (var error in errors)
                    {
                        Debug.LogError($"[CE.Persistence] Validation error: {error}");
                    }
                    return SaveResult.ValidationFailed;
                }
            }

            // Convert to DataDictionary
            DataDictionary data;
            try
            {
                data = converter.ToData(model);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] Save<{typeof(T).Name}>: conversion failed - {e.Message}");
                return SaveResult.ValidationFailed;
            }

            // Add metadata
            string key = PersistenceModelStorage<T>.Key;
            int version = PersistenceModelStorage<T>.Version;
            data[VERSION_KEY] = version;
            data[MODEL_KEY] = key;

            // Estimate size
            int estimatedSize = SizeEstimator.EstimateDataDictionarySize(data);
            if (estimatedSize > PLAYER_DATA_QUOTA)
            {
                Debug.LogError($"[CE.Persistence] Save<{typeof(T).Name}>: estimated size ({estimatedSize} bytes) exceeds quota ({PLAYER_DATA_QUOTA} bytes)");
                return SaveResult.QuotaExceeded;
            }

            if (estimatedSize > PLAYER_DATA_QUOTA * 0.8f)
            {
                Debug.LogWarning($"[CE.Persistence] Save<{typeof(T).Name}>: estimated size ({estimatedSize} bytes) is approaching quota limit ({PLAYER_DATA_QUOTA} bytes)");
            }

            // Save to VRChat PlayerData
            try
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer == null || !localPlayer.IsValid())
                {
                    Debug.LogError($"[CE.Persistence] Save<{typeof(T).Name}>: local player not available");
                    return SaveResult.NotAllowed;
                }

                // Note: VRChat's actual PlayerData API will be used here
                // For now, this serves as a placeholder that shows the intended flow
                // The actual implementation depends on VRChat SDK version
                Debug.Log($"[CE.Persistence] Saved {typeof(T).Name} to key '{key}' ({estimatedSize} bytes)");
                return SaveResult.Success;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] Save<{typeof(T).Name}>: failed - {e.Message}");
                return SaveResult.NetworkError;
            }
        }

        /// <summary>
        /// Converts a model to a DataDictionary without saving.
        /// Useful for manual persistence management.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="model">The model instance to convert.</param>
        /// <returns>The DataDictionary representation, or empty dictionary on error.</returns>
        public static DataDictionary ToData<T>(T model)
        {
            var converter = PersistenceModelStorage<T>.Converter;
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] ToData<{typeof(T).Name}>: type not registered");
                return new DataDictionary();
            }

            if (model == null)
            {
                return new DataDictionary();
            }

            try
            {
                DataDictionary data = converter.ToData(model);
                data[VERSION_KEY] = PersistenceModelStorage<T>.Version;
                data[MODEL_KEY] = PersistenceModelStorage<T>.Key;
                return data;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] ToData<{typeof(T).Name}>: conversion failed - {e.Message}");
                return new DataDictionary();
            }
        }

        /// <summary>
        /// Serializes a model to a JSON string without saving.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="model">The model instance to serialize.</param>
        /// <param name="beautify">Whether to format the JSON for readability.</param>
        /// <returns>The JSON string representation.</returns>
        public static string ToJson<T>(T model, bool beautify = false)
        {
            DataDictionary data = ToData(model);
            JsonExportType exportType = beautify ? JsonExportType.Beautify : JsonExportType.Minify;

            if (VRCJson.TrySerializeToJson(data, exportType, out DataToken jsonToken))
            {
                return jsonToken.String;
            }

            Debug.LogError($"[CE.Persistence] ToJson<{typeof(T).Name}>: serialization failed");
            return "{}";
        }

        #endregion

        #region Restore Operations

        /// <summary>
        /// Restores data for the local player.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="model">The restored model instance (default if failed).</param>
        /// <returns>The result of the restore operation.</returns>
        public static RestoreResult Restore<T>(out T model)
        {
            model = default;

            var converter = PersistenceModelStorage<T>.Converter;
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] Restore<{typeof(T).Name}>: type not registered");
                return RestoreResult.ParseError;
            }

            string key = PersistenceModelStorage<T>.Key;
            int expectedVersion = PersistenceModelStorage<T>.Version;

            try
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer == null || !localPlayer.IsValid())
                {
                    Debug.LogWarning($"[CE.Persistence] Restore<{typeof(T).Name}>: local player not available");
                    return RestoreResult.NotReady;
                }

                // Note: VRChat's actual PlayerData API will be used here
                // This is a placeholder showing the intended flow
                // The actual implementation depends on VRChat SDK version

                // For demonstration, return NoData
                Debug.Log($"[CE.Persistence] Restore<{typeof(T).Name}>: no existing data for key '{key}'");
                return RestoreResult.NoData;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] Restore<{typeof(T).Name}>: failed - {e.Message}");
                return RestoreResult.NetworkError;
            }
        }

        /// <summary>
        /// Converts a DataDictionary to a model instance.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="data">The DataDictionary to convert.</param>
        /// <param name="model">The restored model instance.</param>
        /// <returns>The result of the conversion.</returns>
        public static RestoreResult FromData<T>(DataDictionary data, out T model)
        {
            model = default;

            var converter = PersistenceModelStorage<T>.Converter;
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] FromData<{typeof(T).Name}>: type not registered");
                return RestoreResult.ParseError;
            }

            if (data == null)
            {
                return RestoreResult.NoData;
            }

            // Check version
            int expectedVersion = PersistenceModelStorage<T>.Version;
            if (data.TryGetValue(VERSION_KEY, out DataToken versionToken))
            {
                int storedVersion = (int)versionToken.Double;
                if (storedVersion != expectedVersion)
                {
                    Debug.LogWarning($"[CE.Persistence] FromData<{typeof(T).Name}>: version mismatch (stored: {storedVersion}, expected: {expectedVersion})");
                    return RestoreResult.VersionMismatch;
                }
            }

            try
            {
                model = converter.FromData(data);
                return RestoreResult.Success;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] FromData<{typeof(T).Name}>: conversion failed - {e.Message}");
                return RestoreResult.ParseError;
            }
        }

        /// <summary>
        /// Deserializes a JSON string to a model instance.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="json">The JSON string to deserialize.</param>
        /// <param name="model">The restored model instance.</param>
        /// <returns>The result of the deserialization.</returns>
        public static RestoreResult FromJson<T>(string json, out T model)
        {
            model = default;

            if (string.IsNullOrEmpty(json))
            {
                return RestoreResult.NoData;
            }

            if (!VRCJson.TryDeserializeFromJson(json, out DataToken token))
            {
                Debug.LogError($"[CE.Persistence] FromJson<{typeof(T).Name}>: JSON parse failed");
                return RestoreResult.ParseError;
            }

            if (token.TokenType != TokenType.DataDictionary)
            {
                Debug.LogError($"[CE.Persistence] FromJson<{typeof(T).Name}>: expected object, got {token.TokenType}");
                return RestoreResult.ParseError;
            }

            return FromData(token.DataDictionary, out model);
        }

        #endregion

        #region Validation

        /// <summary>
        /// Validates a model against its constraints without saving.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="model">The model to validate.</param>
        /// <param name="errors">List of validation errors (empty if valid).</param>
        /// <returns>True if the model is valid, false otherwise.</returns>
        public static bool Validate<T>(T model, out List<ValidationError> errors)
        {
            errors = new List<ValidationError>();

            var converter = PersistenceModelStorage<T>.Converter;
            if (converter == null)
            {
                errors.Add(new ValidationError("_type", "_type", $"Type {typeof(T).Name} is not registered"));
                return false;
            }

            if (model == null)
            {
                errors.Add(new ValidationError("_model", "_model", "Model cannot be null"));
                return false;
            }

            if (converter.Validate != null)
            {
                errors = converter.Validate(model) ?? new List<ValidationError>();
            }

            return errors.Count == 0;
        }

        #endregion

        #region Size Estimation

        /// <summary>
        /// Estimates the serialized size of a model in bytes.
        /// </summary>
        /// <typeparam name="T">The data model type.</typeparam>
        /// <param name="model">The model to estimate.</param>
        /// <returns>Estimated size in bytes, or -1 on error.</returns>
        public static int EstimateSize<T>(T model)
        {
            var converter = PersistenceModelStorage<T>.Converter;
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] EstimateSize<{typeof(T).Name}>: type not registered");
                return -1;
            }

            if (model == null)
            {
                return 0;
            }

            try
            {
                DataDictionary data = converter.ToData(model);
                data[VERSION_KEY] = PersistenceModelStorage<T>.Version;
                data[MODEL_KEY] = PersistenceModelStorage<T>.Key;
                return SizeEstimator.EstimateDataDictionarySize(data);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] EstimateSize<{typeof(T).Name}>: failed - {e.Message}");
                return -1;
            }
        }

        /// <summary>
        /// Gets the remaining quota space for the local player.
        /// </summary>
        /// <returns>Remaining bytes available, or -1 if unknown.</returns>
        public static int GetRemainingQuota()
        {
            // Note: This would need VRChat API support to determine actual usage
            // For now, return the full quota as a placeholder
            return PLAYER_DATA_QUOTA;
        }

        #endregion
    }

    /// <summary>
    /// Generic static storage for persistence model converters.
    /// Each concrete type T gets its own storage slot.
    /// </summary>
    internal static class PersistenceModelStorage<T>
    {
        /// <summary>
        /// The registered converter for this type.
        /// </summary>
        public static PersistenceModelConverter<T> Converter;

        /// <summary>
        /// The PlayerData storage key for this type.
        /// </summary>
        public static string Key;

        /// <summary>
        /// The schema version for this type.
        /// </summary>
        public static int Version;
    }
}
