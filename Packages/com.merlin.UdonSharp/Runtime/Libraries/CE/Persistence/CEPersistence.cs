using System;
using System.Collections.Generic;
using JetBrains.Annotations;
using UnityEngine;
using VRC.SDK3.Data;
using VRC.SDK3.Persistence;
using VRC.SDKBase;

namespace UdonSharp.CE.Persistence
{
#if !COMPILER_UDONSHARP
    /// <summary>
    /// Converter functions for a registered persistent data model type.
    /// </summary>
    /// <typeparam name="T">The data model type.</typeparam>
    /// <remarks>
    /// Editor-only. Udon has neither delegates nor shared static storage, so under Udon CE generates one
    /// converter per <c>[PlayerData]</c> model at compile time instead of storing one of these.
    /// </remarks>
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
#endif

    /// <summary>
    /// Central API for player data persistence operations.
    ///
    /// CEPersistence provides type-safe save/restore functionality that integrates
    /// with VRChat's PlayerData API. Models are described with attributes; CE generates
    /// their converters and non-generic overloads during the Udon compile.
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
    /// // No registration call is needed. CE generates the converter plus the
    /// // non-generic Save/Restore overloads for PlayerSaveData at compile time.
    ///
    /// // Save data
    /// SaveResult result = CEPersistence.Save(myData);
    ///
    /// // Restore data, from OnPlayerRestored for the local player (see remarks)
    /// RestoreResult result = CEPersistence.Restore(out PlayerSaveData loadedData);
    /// </code>
    /// </example>
    /// <remarks>
    /// Restore is only authoritative once VRChat has restored the local player's PlayerData, which it announces
    /// with OnPlayerRestored for the local player. Before that the stored key may not be loaded yet, so a
    /// returning player can read as NoData exactly like a new one, and a world that answers NoData by saving
    /// defaults overwrites the real save. NotReady does not cover this: it only reports a missing or invalid
    /// local player.
    /// </remarks>
    [PublicAPI]
    public static partial class CEPersistence
    {
        #region Constants

        /// <summary>
        /// VRChat PlayerData quota limit in bytes (100KB). Used as the fallback when no running client can be
        /// asked for the real limit.
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

#if COMPILER_UDONSHARP

        #region Udon Compile-Time Diagnostics

        // UdonSharp emits the statics of a non-behaviour type into the calling behaviour's own program heap, so
        // there is nowhere for a shared runtime type registry to live: anything Register<T> stored would be
        // invisible to a Save<T> issued from a different behaviour. The generic entry points therefore cannot
        // be made to work here. They stay declared - with unreachable bodies - purely so that a call site the
        // CE rewriter could not resolve to a [PlayerData] model fails the compile naming the overload to call,
        // instead of returning NotRegistered at runtime in a published world where nobody reads the log.

        private const string GenericPrefix =
            "[CE.Persistence] Generic CEPersistence methods cannot run under Udon. Mark the model class [PlayerData(\"key\")] with [PersistKey] fields and call ";

        private const string GenericSuffix =
            " instead - CE generates that non-generic overload for every [PlayerData] model at compile time.";

        private const string RegisterMessage =
            "[CE.Persistence] CEPersistence.Register<T>() cannot run under Udon: converter delegates and shared static storage do not exist there. Describe the model with [PlayerData(\"key\")] and [PersistKey] field attributes instead; CE generates the converter and the non-generic Save/Restore/ToData/FromData overloads at compile time.";

        private const string ModelQueryMessage =
            "[CE.Persistence] IsRegistered<T>/GetKey<T>/GetVersion<T> are folded into constants at compile time for [PlayerData] models. CE could not resolve this type argument to a model - check that the class carries [PlayerData(\"key\")], and qualify the type argument with its namespace if two models share a short name.";

        [Obsolete(RegisterMessage, true)]
        public static void Register<T>(
            Func<T, DataDictionary> toData,
            Func<DataDictionary, T> fromData,
            string key,
            int version = 1,
            Func<T, List<ValidationError>> validate = null)
        {
        }

        [Obsolete(ModelQueryMessage, true)]
        public static bool IsRegistered<T>()
        {
            return false;
        }

        [Obsolete(ModelQueryMessage, true)]
        public static string GetKey<T>()
        {
            return null;
        }

        [Obsolete(ModelQueryMessage, true)]
        public static int GetVersion<T>()
        {
            return 0;
        }

        [Obsolete(GenericPrefix + "CEPersistence.Save(model)" + GenericSuffix, true)]
        public static SaveResult Save<T>(T model)
        {
            return SaveResult.NotRegistered;
        }

        [Obsolete(GenericPrefix + "CEPersistence.ToData(model)" + GenericSuffix, true)]
        public static DataDictionary ToData<T>(T model)
        {
            return new DataDictionary();
        }

        [Obsolete(GenericPrefix + "CEPersistence.ToJson(model, beautify)" + GenericSuffix, true)]
        public static string ToJson<T>(T model, bool beautify = false)
        {
            return "{}";
        }

        [Obsolete(GenericPrefix + "CEPersistence.Restore(out model)" + GenericSuffix, true)]
        public static RestoreResult Restore<T>(out T model)
        {
            model = default;
            return RestoreResult.ParseError;
        }

        [Obsolete(GenericPrefix + "CEPersistence.FromData(data, out model)" + GenericSuffix, true)]
        public static RestoreResult FromData<T>(DataDictionary data, out T model)
        {
            model = default;
            return RestoreResult.ParseError;
        }

        [Obsolete(GenericPrefix + "CEPersistence.FromJson(json, out model)" + GenericSuffix, true)]
        public static RestoreResult FromJson<T>(string json, out T model)
        {
            model = default;
            return RestoreResult.ParseError;
        }

        [Obsolete(GenericPrefix + "CEPersistence.Validate(model, out errors)" + GenericSuffix, true)]
        public static bool Validate<T>(T model, out List<ValidationError> errors)
        {
            errors = new List<ValidationError>();
            return false;
        }

        [Obsolete(GenericPrefix + "CEPersistence.EstimateSize(model)" + GenericSuffix, true)]
        public static int EstimateSize<T>(T model)
        {
            return -1;
        }

        #endregion

#else

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
        /// <remarks>
        /// Editor-only. Code that reaches the Udon compile must use <c>[PlayerData]</c> attributes instead;
        /// calling this there is a compile error rather than a silent runtime no-op.
        /// </remarks>
        public static void Register<T>(
            Func<T, DataDictionary> toData,
            Func<DataDictionary, T> fromData,
            string key,
            int version = 1,
            Func<T, List<ValidationError>> validate = null)
        {
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
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: type not registered");
                return FinalizeSave<T>(SaveResult.NotRegistered);
            }

            if (model == null)
            {
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: model cannot be null");
                return FinalizeSave<T>(SaveResult.ValidationFailed);
            }

            // Validate if validator is registered
            List<ValidationError> validationErrors = null;
            if (converter.Validate != null)
            {
                validationErrors = converter.Validate(model);
            }

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
            try
            {
                data = converter.ToData(model);
            }
            catch (Exception e)
            {
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: conversion failed - {e.Message}");
                return FinalizeSave<T>(SaveResult.ValidationFailed);
            }

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

            // Estimate size against what is left of the account-wide quota, not the whole limit - see
            // GetWritableQuota. Kept identical to the code the CE optimizer generates for [PlayerData] models
            // so a model behaves the same either side of the Udon compile.
            int quotaBudget = GetWritableQuota(key);
            int estimatedSize = SizeEstimator.EstimateDataDictionarySize(data);
            if (estimatedSize > quotaBudget)
            {
                Debug.LogError($"[CE.Persistence] Save<{GetTypeName<T>()}>: estimated size ({estimatedSize} bytes) exceeds remaining quota ({quotaBudget} of {GetQuotaLimit()} bytes)");
                return FinalizeSave<T>(SaveResult.QuotaExceeded);
            }

            if (estimatedSize > quotaBudget * 0.8f)
            {
                Debug.LogWarning($"[CE.Persistence] Save<{GetTypeName<T>()}>: estimated size ({estimatedSize} bytes) is approaching the remaining quota ({quotaBudget} bytes)");
            }

            // Save to VRChat PlayerData
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
                Debug.LogError($"[CE.Persistence] ToData<{GetTypeName<T>()}>: type not registered");
                return new DataDictionary();
            }

            if (model == null)
            {
                return new DataDictionary();
            }

            try
            {
                DataDictionary data = converter.ToData(model);
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

            var converter = PersistenceModelStorage<T>.Converter;
            if (converter == null)
            {
                Debug.LogError($"[CE.Persistence] Restore<{GetTypeName<T>()}>: type not registered");
                return FinalizeRestore<T>(RestoreResult.ParseError);
            }

            string key = PersistenceModelStorage<T>.Key;

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
                    PersistenceLifecycle.RecordCorruption(new CorruptionInfo(expectedKey, MODEL_KEY, CorruptionType.InvalidType, details));
                    return RestoreResult.ParseError;
                }

                string storedKey = modelToken.String;
                if (!string.IsNullOrEmpty(expectedKey) && storedKey != expectedKey)
                {
                    string details = $"Stored key '{storedKey}' does not match expected '{expectedKey}'";
                    Debug.LogError($"[CE.Persistence] FromData<{GetTypeName<T>()}>: {details}");
                    PersistenceLifecycle.RecordCorruption(new CorruptionInfo(expectedKey, MODEL_KEY, CorruptionType.SchemaViolation, details));
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
                    PersistenceLifecycle.RecordCorruption(new CorruptionInfo(expectedKey, VERSION_KEY, CorruptionType.InvalidType, details));
                    return RestoreResult.ParseError;
                }

                int storedVersion = (int)versionToken.Double;
                if (storedVersion != expectedVersion)
                {
                    Debug.LogWarning($"[CE.Persistence] FromData<{GetTypeName<T>()}>: version mismatch (stored: {storedVersion}, expected: {expectedVersion})");
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
                Debug.LogError($"[CE.Persistence] FromData<{GetTypeName<T>()}>: conversion failed - {e.Message}");
                PersistenceLifecycle.RecordCorruption(new CorruptionInfo(expectedKey, null, CorruptionType.SchemaViolation, e.Message));
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
                Debug.LogError($"[CE.Persistence] FromJson<{GetTypeName<T>()}>: JSON parse failed");
                string modelKey = PersistenceModelStorage<T>.Key;
                PersistenceLifecycle.RecordCorruption(new CorruptionInfo(modelKey, null, CorruptionType.MalformedJson, "JSON parse failed"));
                return RestoreResult.ParseError;
            }

            if (token.TokenType != TokenType.DataDictionary)
            {
                Debug.LogError($"[CE.Persistence] FromJson<{GetTypeName<T>()}>: expected object, got {token.TokenType}");
                string modelKey = PersistenceModelStorage<T>.Key;
                PersistenceLifecycle.RecordCorruption(new CorruptionInfo(modelKey, null, CorruptionType.SchemaViolation, $"Expected object, got {token.TokenType}"));
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
                errors.Add(new ValidationError("_type", "_type", $"Type {GetTypeName<T>()} is not registered"));
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
                Debug.LogError($"[CE.Persistence] EstimateSize<{GetTypeName<T>()}>: type not registered");
                return -1;
            }

            if (model == null)
            {
                return 0;
            }

            try
            {
                DataDictionary data = converter.ToData(model);
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
        }

        #endregion

        #region Generic Helpers

        private static string GetTypeName<T>()
        {
            return typeof(T).Name;
        }

        private static SaveResult FinalizeSave<T>(SaveResult result)
        {
            string modelKey = PersistenceModelStorage<T>.Key;
            PersistenceLifecycle.RecordSaveResult(result, modelKey);
            return result;
        }

        private static RestoreResult FinalizeRestore<T>(RestoreResult result)
        {
            string modelKey = PersistenceModelStorage<T>.Key;
            PersistenceLifecycle.RecordRestoreResult(result, modelKey);
            return result;
        }

        #endregion

#endif

        #region Quota

        /// <summary>
        /// Gets the PlayerData quota limit reported by the running client, in bytes.
        /// </summary>
        /// <returns>
        /// The client-reported limit, or <see cref="PLAYER_DATA_QUOTA"/> when no running client can report one.
        /// </returns>
        public static int GetQuotaLimit()
        {
            // Networking's storage APIs are only wired up while a client (or ClientSim) is running, so gate on
            // the local player first; calling them from edit mode or a unit test would fault instead of
            // answering.
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
            {
                return PLAYER_DATA_QUOTA;
            }

            int limit = Networking.GetPlayerDataStorageLimit();
            return limit > 0 ? limit : PLAYER_DATA_QUOTA;
        }

        /// <summary>
        /// Gets the PlayerData storage the local player currently occupies, in bytes.
        /// </summary>
        /// <returns>Bytes in use, or -1 when no running client can report usage.</returns>
        public static int GetUsedQuota()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
            {
                return -1;
            }

            int used = Networking.GetPlayerDataStorageUsage(localPlayer);
            return used < 0 ? -1 : used;
        }

        /// <summary>
        /// Gets the remaining quota space for the local player.
        /// </summary>
        /// <returns>Remaining bytes available, or -1 when no running client can report usage.</returns>
        public static int GetRemainingQuota()
        {
            int used = GetUsedQuota();
            if (used < 0)
            {
                return -1;
            }

            int remaining = GetQuotaLimit() - used;
            return remaining < 0 ? 0 : remaining;
        }

        /// <summary>
        /// Gets the number of bytes a save under <paramref name="key"/> may occupy.
        /// </summary>
        /// <param name="key">The PlayerData storage key the save will be written to.</param>
        /// <returns>
        /// The writable byte budget for that key. Falls back to <see cref="GetQuotaLimit"/> when no running
        /// client can report usage.
        /// </returns>
        /// <remarks>
        /// PlayerData is one account-wide budget shared by every model, so a single model must be checked
        /// against what is LEFT of the quota. Checking it against the whole limit passes several models that
        /// each "fit" while together they overrun the account.
        /// </remarks>
        public static int GetWritableQuota(string key)
        {
            int quotaLimit = GetQuotaLimit();
            int used = GetUsedQuota();
            if (used < 0)
            {
                // Edit mode, a unit test, or the frames before PlayerData is up: nothing can report usage, so
                // the full limit is the only bound that can honestly be enforced.
                return quotaLimit;
            }

            // Reported usage already counts whatever this key holds right now, and a save REPLACES those bytes
            // rather than adding to them. Without crediting them back, re-saving an unchanged model would start
            // failing as soon as it passed half the quota.
            int budget = quotaLimit - used + GetStoredKeySize(key);
            if (budget < 0)
            {
                return 0;
            }

            return budget > quotaLimit ? quotaLimit : budget;
        }

        /// <summary>
        /// Estimates how many bytes the local player's PlayerData currently holds under <paramref name="key"/>.
        /// Returns 0 when nothing is stored there or no running client can be asked.
        /// </summary>
        private static int GetStoredKeySize(string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return 0;
            }

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null || !localPlayer.IsValid())
            {
                return 0;
            }

            string stored;
            if (!PlayerData.TryGetString(localPlayer, key, out stored))
            {
                return 0;
            }

            // The stored value is the JSON text itself, so it occupies exactly its UTF-8 bytes. Sizing it as a
            // string about to be written again added two quote bytes plus an escape byte for every quote inside
            // the JSON -- several per field -- which credited back more than the key really holds and let a
            // save that no longer fit pass the budget check.
            return System.Text.Encoding.UTF8.GetByteCount(stored);
        }

        #endregion

        #region Helpers

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

#if !COMPILER_UDONSHARP
    /// <summary>
    /// Generic static storage for persistence model converters.
    /// Each concrete type T gets its own storage slot.
    /// </summary>
    /// <remarks>
    /// Editor-only: Udon gives every behaviour its own copy of these statics, which is exactly why the generic
    /// API does not exist there.
    /// </remarks>
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
#endif
}
