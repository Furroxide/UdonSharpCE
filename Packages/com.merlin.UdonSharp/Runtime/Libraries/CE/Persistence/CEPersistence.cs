using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.Persistence;
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
    ///     CEPersistence.Register<PlayerSaveData>(
    ///         toData: data => {
    ///             var d = new DataDictionary();
    ///             d["xp"] = data.experience;
    ///             d["lvl"] = data.level;
    ///             return d;
    ///         },
    ///         fromData: d => new PlayerSaveData {
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
    public static partial class CEPersistence
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
#if COMPILER_UDONSHARP
            Debug.LogError($"[CE.Persistence] Register<{GetTypeName<T>()}>: delegate-based converters are not supported in Udon. Use the converter object overload.");
            return;
#else
            if (toData == null || fromData == null)
            {
                Debug.LogError($"[CE.Persistence] Register<{GetTypeName<T>()}>: converters cannot be null");
                return;
            }

            if (string.IsNullOrEmpty(key))
            {
                Debug.LogError($"[CE.Persistence] Register<{GetTypeName<T>()}>: key cannot be null or empty");
                return;
            }

            PersistenceModelStorage<T>.Converter = new PersistenceModelConverter<T>(toData, fromData, validate);
            PersistenceModelStorage<T>.Key = key;
            PersistenceModelStorage<T>.Version = version;

            Debug.Log($"[CE.Persistence] Registered persistent model: {GetTypeName<T>()} (key: {key}, version: {version})");
#endif
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
#if COMPILER_UDONSHARP
            PersistenceModelConverter<T> converter = null;
#else
            var converter = PersistenceModelStorage<T>.Converter;
#endif
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: type not registered. Call Register<{GetTypeName<T>()}>() first.");
                return FinalizeSave<T>(SaveResult.NotRegistered);
            }

            if (model == null)
            {
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: model cannot be null");
                return FinalizeSave<T>(SaveResult.ValidationFailed);
            }

            // Validate if validator is registered
            List<ValidationError> validationErrors = null;
#if !COMPILER_UDONSHARP
            if (converter.Validate != null)
            {
                validationErrors = converter.Validate(model);
            }
#endif

            if (validationErrors != null && validationErrors.Count > 0)
            {
                foreach (var error in validationErrors)
                {
                    Debug.LogError($"[CE.Persistence] Validation error: {error}");
                }
                return FinalizeSave<T>(SaveResult.ValidationFailed);
            }

            // Convert to DataDictionary
            DataDictionary data;
#if COMPILER_UDONSHARP
            data = new DataDictionary();
#else
            try
            {
                data = converter.ToData(model);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: conversion failed - {e.Message}");
                return FinalizeSave<T>(SaveResult.ValidationFailed);
            }
#endif

            if (data == null)
            {
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: converter returned null DataDictionary");
                data = new DataDictionary();
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
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: estimated size ({estimatedSize} bytes) exceeds quota ({PLAYER_DATA_QUOTA} bytes)");
                return FinalizeSave<T>(SaveResult.QuotaExceeded);
            }

            if (estimatedSize > PLAYER_DATA_QUOTA * 0.8f)
            {
                Debug.LogWarning($"[CE.Persistence] Save<{GetTypeName<T>()}>: estimated size ({estimatedSize} bytes) is approaching quota limit ({PLAYER_DATA_QUOTA} bytes)");
            }

            // Save to VRChat PlayerData
#if COMPILER_UDONSHARP
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer == null || !localPlayer.IsValid())
                {
                    Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: local player not available");
                    return FinalizeSave<T>(SaveResult.NotAllowed);
                }

                if (!VRCJson.TrySerializeToJson(data, JsonExportType.Minify, out DataToken jsonToken))
                {
                    Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: JSON serialization failed");
                    return FinalizeSave<T>(SaveResult.ValidationFailed);
                }

                PlayerData.SetString(key, jsonToken.String);
                Debug.Log($"[CE.Persistence] Saved {GetTypeName<T>()} to key '{key}' ({estimatedSize} bytes)");
                return FinalizeSave<T>(SaveResult.Success);
            }
#else
            try
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer == null || !localPlayer.IsValid())
                {
                    Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: local player not available");
                    return FinalizeSave<T>(SaveResult.NotAllowed);
                }

                if (!VRCJson.TrySerializeToJson(data, JsonExportType.Minify, out DataToken jsonToken))
                {
                    Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: JSON serialization failed");
                    return FinalizeSave<T>(SaveResult.ValidationFailed);
                }

                PlayerData.SetString(key, jsonToken.String);
                Debug.Log($"[CE.Persistence] Saved {GetTypeName<T>()} to key '{key}' ({estimatedSize} bytes)");
                return FinalizeSave<T>(SaveResult.Success);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: failed - {e.Message}");
                return FinalizeSave<T>(SaveResult.NetworkError);
            }
#endif
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
#if COMPILER_UDONSHARP
            PersistenceModelConverter<T> converter = null;
#else
            var converter = PersistenceModelStorage<T>.Converter;
#endif
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] ToData<{GetTypeName<T>()}>: type not registered");
                return new DataDictionary();
            }

            if (model == null)
            {
                return new DataDictionary();
            }

#if COMPILER_UDONSHARP
            DataDictionary data;
            data = new DataDictionary();

            if (data == null)
            {
                Debug.LogError($"[CE.Persistence] ToData<{GetTypeName<T>()}>: converter returned null DataDictionary");
                return new DataDictionary();
            }
            data[VERSION_KEY] = PersistenceModelStorage<T>.Version;
            data[MODEL_KEY] = PersistenceModelStorage<T>.Key;
            return data;
#else
            try
            {
                DataDictionary data;
                data = converter.ToData(model);
                if (data == null)
                {
                    Debug.LogError($"[CE.Persistence] ToData<{GetTypeName<T>()}>: converter returned null DataDictionary");
                    return new DataDictionary();
                }
                data[VERSION_KEY] = PersistenceModelStorage<T>.Version;
                data[MODEL_KEY] = PersistenceModelStorage<T>.Key;
                return data;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] ToData<{GetTypeName<T>()}>: conversion failed - {e.Message}");
                return new DataDictionary();
            }
#endif
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

            Debug.LogError($"[CE.Persistence] ToJson<{GetTypeName<T>()}>: serialization failed");
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

#if COMPILER_UDONSHARP
            PersistenceModelConverter<T> converter = null;
#else
            var converter = PersistenceModelStorage<T>.Converter;
#endif
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] Restore<{GetTypeName<T>()}>: type not registered");
                return FinalizeRestore<T>(RestoreResult.ParseError);
            }

            string key = PersistenceModelStorage<T>.Key;
            int expectedVersion = PersistenceModelStorage<T>.Version;

#if COMPILER_UDONSHARP
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer == null || !localPlayer.IsValid())
                {
                    Debug.LogWarning($"[CE.Persistence] Restore<{GetTypeName<T>()}>: local player not available");
                    return FinalizeRestore<T>(RestoreResult.NotReady);
                }

                string json;
                if (!PlayerData.TryGetString(localPlayer, key, out json))
                {
                    Debug.Log($"[CE.Persistence] Restore<{GetTypeName<T>()}>: no existing data for key '{key}'");
                    return FinalizeRestore<T>(RestoreResult.NoData);
                }

                RestoreResult result = FromJson(json, out model);
                return FinalizeRestore<T>(result);
            }
#else
            try
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer == null || !localPlayer.IsValid())
                {
                    Debug.LogWarning($"[CE.Persistence] Restore<{GetTypeName<T>()}>: local player not available");
                    return FinalizeRestore<T>(RestoreResult.NotReady);
                }

                string json;
                if (!PlayerData.TryGetString(localPlayer, key, out json))
                {
                    Debug.Log($"[CE.Persistence] Restore<{GetTypeName<T>()}>: no existing data for key '{key}'");
                    return FinalizeRestore<T>(RestoreResult.NoData);
                }

                RestoreResult result = FromJson(json, out model);
                return FinalizeRestore<T>(result);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] Restore<{GetTypeName<T>()}>: failed - {e.Message}");
                return FinalizeRestore<T>(RestoreResult.NetworkError);
            }
#endif
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

#if COMPILER_UDONSHARP
            PersistenceModelConverter<T> converter = null;
#else
            var converter = PersistenceModelStorage<T>.Converter;
#endif
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] FromData<{GetTypeName<T>()}>: type not registered");
                return RestoreResult.ParseError;
            }

            if (data == null)
            {
                return RestoreResult.NoData;
            }

            string expectedKey = PersistenceModelStorage<T>.Key;
            if (data.TryGetValue(MODEL_KEY, out DataToken modelToken))
            {
                if (modelToken.TokenType != TokenType.String)
                {
                    string details = $"Model key token type {modelToken.TokenType} is invalid";
                    Debug.LogError($"[CE.Persistence] FromData<{GetTypeName<T>()}>: {details}");
                    PersistenceLifecycle.NotifyDataCorrupted(new CorruptionInfo(expectedKey, MODEL_KEY, CorruptionType.InvalidType, details));
                    return RestoreResult.ParseError;
                }

                string storedKey = modelToken.String;
                if (!string.IsNullOrEmpty(expectedKey) && storedKey != expectedKey)
                {
                    string details = $"Stored key '{storedKey}' does not match expected '{expectedKey}'";
                    Debug.LogError($"[CE.Persistence] FromData<{GetTypeName<T>()}>: {details}");
                    PersistenceLifecycle.NotifyDataCorrupted(new CorruptionInfo(expectedKey, MODEL_KEY, CorruptionType.SchemaViolation, details));
                    return RestoreResult.ParseError;
                }
            }

            // Check version
            int expectedVersion = PersistenceModelStorage<T>.Version;
            if (data.TryGetValue(VERSION_KEY, out DataToken versionToken))
            {
                if (!IsNumericToken(versionToken.TokenType))
                {
                    string details = $"Version token type {versionToken.TokenType} is invalid";
                    Debug.LogError($"[CE.Persistence] FromData<{GetTypeName<T>()}>: {details}");
                    PersistenceLifecycle.NotifyDataCorrupted(new CorruptionInfo(expectedKey, VERSION_KEY, CorruptionType.InvalidType, details));
                    return RestoreResult.ParseError;
                }

                int storedVersion = (int)versionToken.Double;
                if (storedVersion != expectedVersion)
                {
                    Debug.LogWarning($"[CE.Persistence] FromData<{GetTypeName<T>()}>: version mismatch (stored: {storedVersion}, expected: {expectedVersion})");
                    return RestoreResult.VersionMismatch;
                }
            }

#if COMPILER_UDONSHARP
            model = default;
            return RestoreResult.ParseError;
#else
            try
            {
                model = converter.FromData(data);
                return RestoreResult.Success;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] FromData<{GetTypeName<T>()}>: conversion failed - {e.Message}");
                PersistenceLifecycle.NotifyDataCorrupted(new CorruptionInfo(expectedKey, null, CorruptionType.SchemaViolation, e.Message));
                return RestoreResult.ParseError;
            }
#endif
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
                Debug.LogError($"[CE.Persistence] FromJson<{GetTypeName<T>()}>: JSON parse failed");
                string modelKey = PersistenceModelStorage<T>.Key;
                PersistenceLifecycle.NotifyDataCorrupted(new CorruptionInfo(modelKey, null, CorruptionType.MalformedJson, "JSON parse failed"));
                return RestoreResult.ParseError;
            }

            if (token.TokenType != TokenType.DataDictionary)
            {
                Debug.LogError($"[CE.Persistence] FromJson<{GetTypeName<T>()}>: expected object, got {token.TokenType}");
                string modelKey = PersistenceModelStorage<T>.Key;
                PersistenceLifecycle.NotifyDataCorrupted(new CorruptionInfo(modelKey, null, CorruptionType.SchemaViolation, $"Expected object, got {token.TokenType}"));
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

#if COMPILER_UDONSHARP
            PersistenceModelConverter<T> converter = null;
#else
            var converter = PersistenceModelStorage<T>.Converter;
#endif
            if (converter == null)
            {
                errors.Add(new ValidationError("_type", "_type", $"Type {GetTypeName<T>()} is not registered"));
                return false;
            }

            if (model == null)
            {
                errors.Add(new ValidationError("_model", "_model", "Model cannot be null"));
                return false;
            }

#if !COMPILER_UDONSHARP
            if (converter.Validate != null)
            {
                errors = converter.Validate(model) ?? new List<ValidationError>();
            }
#endif

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
#if COMPILER_UDONSHARP
            PersistenceModelConverter<T> converter = null;
#else
            var converter = PersistenceModelStorage<T>.Converter;
#endif
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] EstimateSize<{GetTypeName<T>()}>: type not registered");
                return -1;
            }

            if (model == null)
            {
                return 0;
            }

#if COMPILER_UDONSHARP
            DataDictionary data;
            data = new DataDictionary();

            if (data == null)
            {
                Debug.LogError($"[CE.Persistence] EstimateSize<{GetTypeName<T>()}>: converter returned null DataDictionary");
                return -1;
            }
            data[VERSION_KEY] = PersistenceModelStorage<T>.Version;
            data[MODEL_KEY] = PersistenceModelStorage<T>.Key;
            return SizeEstimator.EstimateDataDictionarySize(data);
#else
            try
            {
                DataDictionary data;
                data = converter.ToData(model);
                if (data == null)
                {
                    Debug.LogError($"[CE.Persistence] EstimateSize<{GetTypeName<T>()}>: converter returned null DataDictionary");
                    return -1;
                }
                data[VERSION_KEY] = PersistenceModelStorage<T>.Version;
                data[MODEL_KEY] = PersistenceModelStorage<T>.Key;
                return SizeEstimator.EstimateDataDictionarySize(data);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] EstimateSize<{GetTypeName<T>()}>: failed - {e.Message}");
                return -1;
            }
#endif
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

        #region Helpers

        private static string GetTypeName<T>()
        {
#if COMPILER_UDONSHARP
            return "T";
#else
            return typeof(T).Name;
#endif
        }

        private static SaveResult FinalizeSave<T>(SaveResult result)
        {
            string modelKey = PersistenceModelStorage<T>.Key;
            PersistenceLifecycle.NotifyDataSaved(result, modelKey);
            return result;
        }

        private static RestoreResult FinalizeRestore<T>(RestoreResult result)
        {
            string modelKey = PersistenceModelStorage<T>.Key;
            PersistenceLifecycle.NotifyDataRestored(result, modelKey);
            return result;
        }

        private static bool IsNumericToken(TokenType tokenType)
        {
            switch (tokenType)
            {
                case TokenType.SByte:
                case TokenType.Byte:
                case TokenType.Short:
                case TokenType.UShort:
                case TokenType.Int:
                case TokenType.UInt:
                case TokenType.Long:
                case TokenType.ULong:
                case TokenType.Float:
                case TokenType.Double:
                    return true;
                default:
                    return false;
            }
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
