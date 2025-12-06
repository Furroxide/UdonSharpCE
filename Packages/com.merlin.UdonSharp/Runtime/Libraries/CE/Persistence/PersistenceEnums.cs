using JetBrains.Annotations;

namespace UdonSharp.CE.Persistence
{
    /// <summary>
    /// Result codes for data restoration operations.
    ///
    /// Returned by <see cref="CEPersistence.Restore{T}"/> and related methods
    /// to indicate the outcome of loading persistent data.
    /// </summary>
    [PublicAPI]
    public enum RestoreResult
    {
        /// <summary>
        /// Data was successfully restored from storage.
        /// </summary>
        Success = 0,

        /// <summary>
        /// No data exists for this player/key combination.
        /// This is normal for first-time players. Initialize default values.
        /// </summary>
        NoData = 1,

        /// <summary>
        /// Data exists but has a different schema version.
        /// Consider implementing migration logic or resetting to defaults.
        /// </summary>
        VersionMismatch = 2,

        /// <summary>
        /// Data exists but could not be parsed.
        /// The data may be corrupted or in an unexpected format.
        /// </summary>
        ParseError = 3,

        /// <summary>
        /// A network error occurred while retrieving data.
        /// This may be temporary; consider retrying.
        /// </summary>
        NetworkError = 4,

        /// <summary>
        /// The stored data exceeds the quota limit.
        /// This should not normally occur if data was validated before saving.
        /// </summary>
        QuotaExceeded = 5,

        /// <summary>
        /// Player data is not yet available (still loading).
        /// Wait for the OnPlayerDataUpdated callback.
        /// </summary>
        NotReady = 6
    }

    /// <summary>
    /// Result codes for data save operations.
    ///
    /// Returned by <see cref="CEPersistence.Save{T}"/> and related methods
    /// to indicate the outcome of saving persistent data.
    /// </summary>
    [PublicAPI]
    public enum SaveResult
    {
        /// <summary>
        /// Data was successfully saved to storage.
        /// </summary>
        Success = 0,

        /// <summary>
        /// Validation failed for one or more fields.
        /// Check field constraints (Range, MaxLength, Required).
        /// Use CEPersistence.Validate() to get detailed error messages.
        /// </summary>
        ValidationFailed = 1,

        /// <summary>
        /// The serialized data exceeds the 100KB PlayerData quota.
        /// Reduce the amount of data being stored.
        /// </summary>
        QuotaExceeded = 2,

        /// <summary>
        /// A network error occurred while saving data.
        /// This may be temporary; consider retrying.
        /// </summary>
        NetworkError = 3,

        /// <summary>
        /// Cannot save data for another player (not the owner).
        /// Only the local player can save their own PlayerData.
        /// </summary>
        NotOwner = 4,

        /// <summary>
        /// The data model type has not been registered.
        /// Call CEPersistence.Register() first.
        /// </summary>
        NotRegistered = 5,

        /// <summary>
        /// Save operation is not allowed at this time.
        /// For example, during OnPlayerLeft callback.
        /// </summary>
        NotAllowed = 6
    }

    /// <summary>
    /// Information about a validation error.
    /// </summary>
    [PublicAPI]
    public class ValidationError
    {
        /// <summary>
        /// The name of the field that failed validation.
        /// </summary>
        public string FieldName { get; }

        /// <summary>
        /// The persistence key of the field.
        /// </summary>
        public string PersistKey { get; }

        /// <summary>
        /// A human-readable error message describing the validation failure.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// The actual value that failed validation.
        /// </summary>
        public object ActualValue { get; }

        /// <summary>
        /// Creates a new validation error.
        /// </summary>
        public ValidationError(string fieldName, string persistKey, string message, object actualValue = null)
        {
            FieldName = fieldName;
            PersistKey = persistKey;
            Message = message;
            ActualValue = actualValue;
        }

        /// <summary>
        /// Returns a string representation of the validation error.
        /// </summary>
        public override string ToString()
        {
            if (ActualValue != null)
            {
                return $"[{PersistKey}] {FieldName}: {Message} (was: {ActualValue})";
            }
            return $"[{PersistKey}] {FieldName}: {Message}";
        }
    }
}
