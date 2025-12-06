using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Marks a field to be synchronized when a new player joins.
    ///
    /// Use this for world state that needs to be sent to late joiners,
    /// such as game state, puzzle progress, or environmental changes.
    /// </summary>
    /// <remarks>
    /// Fields marked with [SyncOnJoin] are automatically serialized and
    /// sent to new players when they join the instance. The master client
    /// handles the synchronization process.
    ///
    /// This is useful for data that:
    /// - Changes infrequently but must be accurate for late joiners
    /// - Exceeds normal sync bandwidth if synced continuously
    /// - Represents world state rather than player state
    ///
    /// Priority determines sync order when multiple behaviours have
    /// [SyncOnJoin] fields. Lower values sync first.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class GameManager : UdonSharpBehaviour
    /// {
    ///     [SyncOnJoin(Priority = 0)]
    ///     public int gamePhase;
    ///
    ///     [SyncOnJoin(Priority = 1)]
    ///     public bool[] completedObjectives;
    ///
    ///     [SyncOnJoin(Priority = 2, Compress = true)]
    ///     public int[] worldEventHistory;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class SyncOnJoinAttribute : Attribute
    {
        /// <summary>
        /// Sync priority. Lower values sync first.
        /// Default is 100.
        /// </summary>
        /// <remarks>
        /// Use priorities to ensure dependencies sync before dependents:
        /// - 0-49: Critical game state (phase, mode, core settings)
        /// - 50-99: Important state (player assignments, scores)
        /// - 100-149: Normal state (object positions, toggles)
        /// - 150+: Low priority (cosmetic state, history)
        /// </remarks>
        public int Priority { get; set; } = 100;

        /// <summary>
        /// Whether to compress the data before sending.
        /// Useful for large arrays.
        /// </summary>
        /// <remarks>
        /// Compression adds CPU overhead but reduces network usage.
        /// Recommended for arrays with more than 32 elements.
        /// </remarks>
        public bool Compress { get; set; } = false;

        /// <summary>
        /// Optional group name for batch synchronization.
        /// Fields in the same group are sent together.
        /// </summary>
        public string Group { get; set; } = null;

        /// <summary>
        /// Whether this field is critical and must be confirmed.
        /// Critical fields use reliable delivery with acknowledgment.
        /// </summary>
        public bool Critical { get; set; } = false;
    }

    /// <summary>
    /// Specifies a custom serialization method for [SyncOnJoin] fields.
    /// </summary>
    /// <remarks>
    /// Use this when the default serialization is insufficient,
    /// such as for complex data structures or custom compression.
    /// </remarks>
    /// <example>
    /// <code>
    /// [SyncOnJoin]
    /// [SyncSerializer(nameof(SerializeGameState), nameof(DeserializeGameState))]
    /// public GameState currentState;
    ///
    /// public string SerializeGameState()
    /// {
    ///     return JsonUtility.ToJson(currentState);
    /// }
    ///
    /// public void DeserializeGameState(string data)
    /// {
    ///     currentState = JsonUtility.FromJson&lt;GameState&gt;(data);
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class SyncSerializerAttribute : Attribute
    {
        /// <summary>
        /// Name of the method that serializes this field to a string.
        /// Method signature: string MethodName()
        /// </summary>
        public string SerializeMethod { get; }

        /// <summary>
        /// Name of the method that deserializes a string to this field.
        /// Method signature: void MethodName(string data)
        /// </summary>
        public string DeserializeMethod { get; }

        public SyncSerializerAttribute(string serializeMethod, string deserializeMethod)
        {
            SerializeMethod = serializeMethod;
            DeserializeMethod = deserializeMethod;
        }
    }
}
