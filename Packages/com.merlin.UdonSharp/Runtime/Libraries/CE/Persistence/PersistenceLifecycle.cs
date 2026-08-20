using System;
using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Types of data corruption that can be detected during restore operations.
    /// </summary>
    [PublicAPI]
    public enum CorruptionType
    {
        /// <summary>
        /// A field value has an unexpected or incompatible type.
        /// </summary>
        InvalidType = 0,

        /// <summary>
        /// A required field is missing from the stored data.
        /// </summary>
        MissingField = 1,

        /// <summary>
        /// A field value is outside its valid range constraints.
        /// </summary>
        OutOfRange = 2,

        /// <summary>
        /// The stored JSON data is malformed and cannot be parsed.
        /// </summary>
        MalformedJson = 3,

        /// <summary>
        /// The data structure doesn't match the expected schema.
        /// </summary>
        SchemaViolation = 4
    }

    /// <summary>
    /// Information about data corruption detected during a restore operation.
    ///
    /// Provides details about what went wrong to help with debugging
    /// and recovery logic.
    /// </summary>
    [PublicAPI]
    public class CorruptionInfo
    {
        /// <summary>
        /// The persistence key of the data model where corruption was detected.
        /// </summary>
        public string ModelKey
#if COMPILER_UDONSHARP
        ;
#else
        { get; }
#endif

        /// <summary>
        /// The specific field name where corruption was found, if applicable.
        /// May be null if the entire data structure is corrupted.
        /// </summary>
        public string FieldName
#if COMPILER_UDONSHARP
        ;
#else
        { get; }
#endif

        /// <summary>
        /// The type of corruption that was detected.
        /// </summary>
        public CorruptionType Type
#if COMPILER_UDONSHARP
        ;
#else
        { get; }
#endif

        /// <summary>
        /// A human-readable description of the corruption issue.
        /// </summary>
        public string Details
#if COMPILER_UDONSHARP
        ;
#else
        { get; }
#endif

        /// <summary>
        /// Creates a new CorruptionInfo instance.
        /// </summary>
        /// <param name="modelKey">The persistence key of the affected model.</param>
        /// <param name="fieldName">The name of the corrupted field, or null.</param>
        /// <param name="type">The type of corruption detected.</param>
        /// <param name="details">A detailed description of the issue.</param>
        public CorruptionInfo(string modelKey, string fieldName, CorruptionType type, string details)
        {
            ModelKey = modelKey;
            FieldName = fieldName;
            Type = type;
            Details = details;
        }

        /// <summary>
        /// Returns a string representation of the corruption info.
        /// </summary>
        public override string ToString()
        {
            if (string.IsNullOrEmpty(FieldName))
            {
                return $"[{ModelKey}] {Type}: {Details}";
            }
            return $"[{ModelKey}.{FieldName}] {Type}: {Details}";
        }
    }

    /// <summary>
    /// Removed. The cross-behaviour callback registry this interface belonged to could never work in Udon.
    /// </summary>
    [PublicAPI]
    [Obsolete(PersistenceLifecycle.RegistryRemovedMessage, true)]
    public interface IPersistenceCallbacks
    {
        /// <summary>
        /// Called after a data restore operation completes.
        /// </summary>
        void OnDataRestored();

        /// <summary>
        /// Called after a data save operation completes.
        /// </summary>
        void OnDataSaved();

        /// <summary>
        /// Called when data corruption is detected during a restore operation.
        /// </summary>
        void OnDataCorrupted();
    }

    /// <summary>
    /// Records the outcome of the most recent persistence operation performed by the calling behaviour.
    ///
    /// <see cref="CEPersistence"/> writes the result of every save and restore here, along with the corruption
    /// detail that <see cref="RestoreResult.ParseError"/> alone cannot carry. Read it from the same behaviour
    /// that issued the operation, immediately after the call returns.
    /// </summary>
    /// <remarks>
    /// This state is per-behaviour, not global. UdonSharp emits the statics of a non-behaviour type into each
    /// calling behaviour's own program heap, so behaviour A's records are invisible to behaviour B - which is
    /// why the old cross-behaviour callback registry was removed rather than repaired. If several behaviours
    /// need to hear about one save, have the behaviour that called Save forward the result itself with
    /// SendCustomEvent to references it holds.
    /// </remarks>
    /// <example>
    /// <code>
    /// RestoreResult result = CEPersistence.Restore(out PlayerSaveData loaded);
    /// if (result == RestoreResult.ParseError)
    /// {
    ///     CorruptionInfo info = PersistenceLifecycle.GetPendingCorruptionInfo();
    ///     Debug.LogError($"Save data is unusable: {info}");
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public static class PersistenceLifecycle
    {
        #region Constants

        internal const string RegistryRemovedMessage =
            "[CE.Persistence] The PersistenceLifecycle callback registry has been removed: it could not work under Udon. UdonSharp emits the statics of a non-behaviour type into each calling behaviour's own program heap, so behaviour A's RegisterCallback(this) wrote A's private copy while a CEPersistence.Save inside behaviour B checked B's empty copy - callbacks only ever reached the behaviour that registered them. Use the SaveResult/RestoreResult that Save/Restore return, call PersistenceLifecycle.GetPendingCorruptionInfo() in the same behaviour for corruption detail, and forward the outcome to other behaviours yourself with SendCustomEvent.";

        /// <summary>
        /// Removed along with the callback registry.
        /// </summary>
        [Obsolete(RegistryRemovedMessage, true)]
        public const int MAX_CALLBACKS = 32;

        #endregion

        #region State

        // Behaviour-local record of the last operation. See the remarks on the class: these are not shared.
        private static RestoreResult _pendingRestoreResult;
        private static SaveResult _pendingSaveResult;
        private static string _pendingModelKey;
        private static CorruptionInfo _pendingCorruptionInfo;

        // Set by RecordCorruption and consumed by the RecordRestoreResult that closes the same restore, so a
        // CorruptionInfo only survives the operation that produced it.
        private static bool _corruptionRecordedThisRestore;

        #endregion

        #region Removed Registration API

        /// <summary>
        /// Removed. See <see cref="PersistenceLifecycle"/> remarks for the replacement pattern.
        /// </summary>
        [Obsolete(RegistryRemovedMessage, true)]
        public static bool RegisterCallback(UdonSharpBehaviour behaviour)
        {
            return false;
        }

        /// <summary>
        /// Removed. See <see cref="PersistenceLifecycle"/> remarks for the replacement pattern.
        /// </summary>
        [Obsolete(RegistryRemovedMessage, true)]
        public static bool UnregisterCallback(UdonSharpBehaviour behaviour)
        {
            return false;
        }

        /// <summary>
        /// Removed. See <see cref="PersistenceLifecycle"/> remarks for the replacement pattern.
        /// </summary>
        [Obsolete(RegistryRemovedMessage, true)]
        public static void CleanupDestroyedCallbacks()
        {
        }

        /// <summary>
        /// Removed. See <see cref="PersistenceLifecycle"/> remarks for the replacement pattern.
        /// </summary>
        [Obsolete(RegistryRemovedMessage, true)]
        public static int CallbackCount => 0;

        #endregion

        #region Result Accessors

        /// <summary>
        /// Gets the result of the last restore this behaviour performed.
        /// </summary>
        public static RestoreResult GetPendingRestoreResult() => _pendingRestoreResult;

        /// <summary>
        /// Gets the result of the last save this behaviour performed.
        /// </summary>
        public static SaveResult GetPendingSaveResult() => _pendingSaveResult;

        /// <summary>
        /// Gets the model key of the last save or restore this behaviour performed.
        /// </summary>
        public static string GetPendingModelKey() => _pendingModelKey;

        /// <summary>
        /// Gets the corruption detail recorded by the last restore this behaviour performed, or null when
        /// that restore reported no corruption. Detail from an earlier restore is never returned.
        /// </summary>
        public static CorruptionInfo GetPendingCorruptionInfo() => _pendingCorruptionInfo;

        #endregion

        #region Internal Recording

        /// <summary>
        /// Records the outcome of a restore operation. Called internally by CEPersistence.
        /// </summary>
        /// <param name="result">The result of the restore operation.</param>
        /// <param name="modelKey">The persistence key of the restored model.</param>
        internal static void RecordRestoreResult(RestoreResult result, string modelKey)
        {
            _pendingRestoreResult = result;
            _pendingModelKey = modelKey;

            // Every restore invalidates whatever corruption an earlier attempt recorded, not just a successful
            // one: NotReady, NoData, VersionMismatch and NetworkError are all failures with nothing corrupt
            // about them, and leaving the old CorruptionInfo in place made callers that read
            // GetPendingCorruptionInfo() after any of them report a corruption that did not happen. ParseError
            // keeps the detail only when this operation actually diagnosed it - some ParseError paths return
            // before any corruption is identified (an unregistered type in the editor path, for one).
            if (result != RestoreResult.ParseError || !_corruptionRecordedThisRestore)
            {
                _pendingCorruptionInfo = null;
            }

            _corruptionRecordedThisRestore = false;
        }

        /// <summary>
        /// Records the outcome of a save operation. Called internally by CEPersistence.
        /// </summary>
        /// <param name="result">The result of the save operation.</param>
        /// <param name="modelKey">The persistence key of the saved model.</param>
        internal static void RecordSaveResult(SaveResult result, string modelKey)
        {
            _pendingSaveResult = result;
            _pendingModelKey = modelKey;
        }

        /// <summary>
        /// Records corruption detected during a restore. Called internally by CEPersistence.
        /// </summary>
        /// <param name="info">Details about the corruption that was detected.</param>
        internal static void RecordCorruption(CorruptionInfo info)
        {
            if (info == null)
            {
                Debug.LogError("[CE.Persistence] RecordCorruption: info cannot be null");
                return;
            }

            _pendingCorruptionInfo = info;
            _pendingModelKey = info.ModelKey;
            _corruptionRecordedThisRestore = true;

            Debug.LogWarning($"[CE.Persistence] Data corruption detected: {info}");
        }

        #endregion
    }
}
