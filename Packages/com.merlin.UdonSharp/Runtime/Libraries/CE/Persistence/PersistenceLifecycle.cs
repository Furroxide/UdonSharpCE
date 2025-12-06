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
        public string ModelKey { get; }

        /// <summary>
        /// The specific field name where corruption was found, if applicable.
        /// May be null if the entire data structure is corrupted.
        /// </summary>
        public string FieldName { get; }

        /// <summary>
        /// The type of corruption that was detected.
        /// </summary>
        public CorruptionType Type { get; }

        /// <summary>
        /// A human-readable description of the corruption issue.
        /// </summary>
        public string Details { get; }

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
    /// Interface for receiving persistence lifecycle callbacks.
    ///
    /// Implement this interface on your UdonSharpBehaviour and register with
    /// <see cref="PersistenceLifecycle.RegisterCallback"/> to receive notifications
    /// about save and restore operations.
    /// </summary>
    /// <example>
    /// <code>
    /// public class MySaveManager : UdonSharpBehaviour, IPersistenceCallbacks
    /// {
    ///     void Start()
    ///     {
    ///         PersistenceLifecycle.RegisterCallback(this);
    ///     }
    ///
    ///     public void OnDataRestored(RestoreResult result, string modelKey)
    ///     {
    ///         if (result == RestoreResult.Success)
    ///             Debug.Log($"Data loaded for {modelKey}!");
    ///     }
    ///
    ///     public void OnDataSaved(SaveResult result, string modelKey)
    ///     {
    ///         if (result == SaveResult.Success)
    ///             Debug.Log($"Data saved for {modelKey}!");
    ///     }
    ///
    ///     public void OnDataCorrupted(CorruptionInfo info)
    ///     {
    ///         Debug.LogError($"Data corruption: {info}");
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public interface IPersistenceCallbacks
    {
        /// <summary>
        /// Called after a data restore operation completes.
        /// </summary>
        /// <param name="result">The result of the restore operation.</param>
        /// <param name="modelKey">The persistence key of the model that was restored.</param>
        void OnDataRestored(RestoreResult result, string modelKey);

        /// <summary>
        /// Called after a data save operation completes.
        /// </summary>
        /// <param name="result">The result of the save operation.</param>
        /// <param name="modelKey">The persistence key of the model that was saved.</param>
        void OnDataSaved(SaveResult result, string modelKey);

        /// <summary>
        /// Called when data corruption is detected during a restore operation.
        /// </summary>
        /// <param name="info">Details about the corruption that was detected.</param>
        void OnDataCorrupted(CorruptionInfo info);
    }

    /// <summary>
    /// Manages persistence lifecycle events and callback notifications.
    ///
    /// This class provides a registration system for behaviours that want to
    /// receive notifications about persistence operations. Register your callback
    /// handler in Start() and unregister in OnDestroy().
    /// </summary>
    /// <remarks>
    /// Due to Udon limitations, callbacks are invoked via SendCustomEvent rather
    /// than direct method calls. Registered behaviours must implement the callback
    /// methods and mark them with appropriate attributes for event export.
    /// </remarks>
    [PublicAPI]
    public static class PersistenceLifecycle
    {
        #region Constants

        /// <summary>
        /// Maximum number of callback handlers that can be registered.
        /// </summary>
        public const int MAX_CALLBACKS = 32;

        // Event method names for SendCustomEvent
        private const string EVENT_ON_DATA_RESTORED = "_CE_OnDataRestored";
        private const string EVENT_ON_DATA_SAVED = "_CE_OnDataSaved";
        private const string EVENT_ON_DATA_CORRUPTED = "_CE_OnDataCorrupted";

        #endregion

        #region State

        // Registered callback handlers
        private static UdonSharpBehaviour[] _callbacks;
        private static int _callbackCount;

        // Pending event data (used to pass data to callbacks via SendCustomEvent)
        private static RestoreResult _pendingRestoreResult;
        private static SaveResult _pendingSaveResult;
        private static string _pendingModelKey;
        private static CorruptionInfo _pendingCorruptionInfo;

        #endregion

        #region Registration

        /// <summary>
        /// Registers a behaviour to receive persistence lifecycle callbacks.
        ///
        /// The behaviour should implement <see cref="IPersistenceCallbacks"/> or
        /// provide the appropriate callback methods.
        /// </summary>
        /// <param name="behaviour">The behaviour to register.</param>
        /// <returns>True if registration was successful, false otherwise.</returns>
        public static bool RegisterCallback(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
            {
                Debug.LogError("[CE.Persistence] RegisterCallback: behaviour cannot be null");
                return false;
            }

            // Initialize array on first use
            if (_callbacks == null)
            {
                _callbacks = new UdonSharpBehaviour[MAX_CALLBACKS];
                _callbackCount = 0;
            }

            // Check if already registered
            for (int i = 0; i < _callbackCount; i++)
            {
                if (_callbacks[i] == behaviour)
                {
                    Debug.LogWarning($"[CE.Persistence] RegisterCallback: {behaviour.name} is already registered");
                    return true;
                }
            }

            // Check capacity
            if (_callbackCount >= MAX_CALLBACKS)
            {
                Debug.LogError($"[CE.Persistence] RegisterCallback: maximum callbacks ({MAX_CALLBACKS}) reached");
                return false;
            }

            // Add to array
            _callbacks[_callbackCount] = behaviour;
            _callbackCount++;

            Debug.Log($"[CE.Persistence] Registered callback: {behaviour.name}");
            return true;
        }

        /// <summary>
        /// Unregisters a behaviour from receiving persistence callbacks.
        /// </summary>
        /// <param name="behaviour">The behaviour to unregister.</param>
        /// <returns>True if the behaviour was found and removed, false otherwise.</returns>
        public static bool UnregisterCallback(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null || _callbacks == null)
            {
                return false;
            }

            for (int i = 0; i < _callbackCount; i++)
            {
                if (_callbacks[i] == behaviour)
                {
                    // Swap with last element and shrink
                    _callbackCount--;
                    _callbacks[i] = _callbacks[_callbackCount];
                    _callbacks[_callbackCount] = null;

                    Debug.Log($"[CE.Persistence] Unregistered callback: {behaviour.name}");
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the number of registered callback handlers.
        /// </summary>
        public static int CallbackCount => _callbackCount;

        #endregion

        #region Event Accessors

        /// <summary>
        /// Gets the pending restore result for the current callback.
        /// Call this from your OnDataRestored handler to get the result.
        /// </summary>
        public static RestoreResult GetPendingRestoreResult() => _pendingRestoreResult;

        /// <summary>
        /// Gets the pending save result for the current callback.
        /// Call this from your OnDataSaved handler to get the result.
        /// </summary>
        public static SaveResult GetPendingSaveResult() => _pendingSaveResult;

        /// <summary>
        /// Gets the pending model key for the current callback.
        /// Call this from your callback handler to get the model key.
        /// </summary>
        public static string GetPendingModelKey() => _pendingModelKey;

        /// <summary>
        /// Gets the pending corruption info for the current callback.
        /// Call this from your OnDataCorrupted handler to get the corruption details.
        /// </summary>
        public static CorruptionInfo GetPendingCorruptionInfo() => _pendingCorruptionInfo;

        #endregion

        #region Internal Notifications

        /// <summary>
        /// Notifies all registered callbacks that a restore operation completed.
        /// Called internally by CEPersistence.
        /// </summary>
        /// <param name="result">The result of the restore operation.</param>
        /// <param name="modelKey">The persistence key of the restored model.</param>
        internal static void NotifyDataRestored(RestoreResult result, string modelKey)
        {
            if (_callbacks == null || _callbackCount == 0)
                return;

            _pendingRestoreResult = result;
            _pendingModelKey = modelKey;

            for (int i = 0; i < _callbackCount; i++)
            {
                var callback = _callbacks[i];
                if (callback != null && callback.gameObject != null)
                {
                    try
                    {
                        callback.SendCustomEvent(EVENT_ON_DATA_RESTORED);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[CE.Persistence] NotifyDataRestored: callback {callback.name} failed - {e.Message}");
                    }
                }
            }

            // Clear pending data
            _pendingModelKey = null;
        }

        /// <summary>
        /// Notifies all registered callbacks that a save operation completed.
        /// Called internally by CEPersistence.
        /// </summary>
        /// <param name="result">The result of the save operation.</param>
        /// <param name="modelKey">The persistence key of the saved model.</param>
        internal static void NotifyDataSaved(SaveResult result, string modelKey)
        {
            if (_callbacks == null || _callbackCount == 0)
                return;

            _pendingSaveResult = result;
            _pendingModelKey = modelKey;

            for (int i = 0; i < _callbackCount; i++)
            {
                var callback = _callbacks[i];
                if (callback != null && callback.gameObject != null)
                {
                    try
                    {
                        callback.SendCustomEvent(EVENT_ON_DATA_SAVED);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[CE.Persistence] NotifyDataSaved: callback {callback.name} failed - {e.Message}");
                    }
                }
            }

            // Clear pending data
            _pendingModelKey = null;
        }

        /// <summary>
        /// Notifies all registered callbacks that data corruption was detected.
        /// Called internally by CEPersistence.
        /// </summary>
        /// <param name="info">Details about the corruption that was detected.</param>
        internal static void NotifyDataCorrupted(CorruptionInfo info)
        {
            if (_callbacks == null || _callbackCount == 0)
                return;

            if (info == null)
            {
                Debug.LogError("[CE.Persistence] NotifyDataCorrupted: info cannot be null");
                return;
            }

            _pendingCorruptionInfo = info;
            _pendingModelKey = info.ModelKey;

            Debug.LogWarning($"[CE.Persistence] Data corruption detected: {info}");

            for (int i = 0; i < _callbackCount; i++)
            {
                var callback = _callbacks[i];
                if (callback != null && callback.gameObject != null)
                {
                    try
                    {
                        callback.SendCustomEvent(EVENT_ON_DATA_CORRUPTED);
                    }
                    catch (Exception e)
                    {
                        Debug.LogError($"[CE.Persistence] NotifyDataCorrupted: callback {callback.name} failed - {e.Message}");
                    }
                }
            }

            // Clear pending data
            _pendingCorruptionInfo = null;
            _pendingModelKey = null;
        }

        #endregion

        #region Cleanup

        /// <summary>
        /// Removes all null or destroyed callbacks from the registry.
        /// Called automatically during notification, but can be called manually
        /// for cleanup.
        /// </summary>
        public static void CleanupDestroyedCallbacks()
        {
            if (_callbacks == null || _callbackCount == 0)
                return;

            int writeIndex = 0;
            for (int readIndex = 0; readIndex < _callbackCount; readIndex++)
            {
                var callback = _callbacks[readIndex];
                if (callback != null && callback.gameObject != null)
                {
                    _callbacks[writeIndex] = callback;
                    writeIndex++;
                }
            }

            // Clear remaining slots
            for (int i = writeIndex; i < _callbackCount; i++)
            {
                _callbacks[i] = null;
            }

            if (writeIndex != _callbackCount)
            {
                Debug.Log($"[CE.Persistence] CleanupDestroyedCallbacks: removed {_callbackCount - writeIndex} destroyed callbacks");
            }

            _callbackCount = writeIndex;
        }

        #endregion
    }
}
