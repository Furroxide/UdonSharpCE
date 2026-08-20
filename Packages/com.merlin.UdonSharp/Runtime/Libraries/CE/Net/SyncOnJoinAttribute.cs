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
    /// Fields marked with [SyncOnJoin] are serialized into a generated
    /// <c>[UdonSynced]</c> string on the behaviour itself, so VRChat delivers
    /// them to late joiners and authority follows normal object ownership --
    /// state authored by any client reaches every later joiner, not just the
    /// master's view.
    ///
    /// This is useful for data that:
    /// - Changes infrequently but must be accurate for late joiners
    /// - Exceeds normal sync bandwidth if synced continuously
    /// - Represents world state rather than player state
    ///
    /// Priority orders fields within one payload. Group splits a behaviour's
    /// state across several payloads so unrelated state stops travelling
    /// together.
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
    ///     // Its own payload: rewriting the history no longer re-sends the phase.
    ///     [SyncOnJoin(Group = "history")]
    ///     public int[] worldEventHistory;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public class SyncOnJoinAttribute : Attribute
    {
        /// <summary>
        /// Order within a payload. Lower values are written and restored first.
        /// Default is 100.
        /// </summary>
        /// <remarks>
        /// Use priorities to ensure dependencies are restored before dependents:
        /// - 0-49: Critical game state (phase, mode, core settings)
        /// - 50-99: Important state (player assignments, scores)
        /// - 100-149: Normal state (object positions, toggles)
        /// - 150+: Low priority (cosmetic state, history)
        ///
        /// Priority orders fields inside ONE payload, so it only relates fields
        /// that share a <see cref="Group"/>. Two groups are two synced variables
        /// and VRChat may deliver them in either order; a dependency that has to
        /// hold belongs in one group.
        /// </remarks>
        public int Priority { get; set; } = 100;

        /// <summary>
        /// Not implemented. Nothing reads this.
        /// </summary>
        /// <remarks>
        /// Udon exposes no deflate extern, so a hand-rolled compressor would cost more instructions than
        /// the bytes it saves. The realistic win would be positional JSON encoding (<c>{"_a":3,"_b":{}}</c>
        /// emitted as <c>[3,{}]</c>, typically 30-60% smaller and free because sender and receiver run the
        /// identical program), which is a payload-format change rather than a per-field toggle.
        /// </remarks>
        [Obsolete("SyncOnJoin.Compress is not implemented and nothing reads it; setting it changed nothing. " +
                  "Udon has no deflate extern. To reduce payload size, sync fewer or smaller fields, or supply " +
                  "[SyncSerializer] with your own compact encoding.", true)]
        public bool Compress { get; set; } = false;

        /// <summary>
        /// Which payload this field rides in. Fields sharing a group share one <c>[UdonSynced]</c> string;
        /// fields naming no group share the default one.
        /// </summary>
        /// <remarks>
        /// Group exists to stop unrelated state travelling together. Without it a behaviour has exactly one
        /// payload, so touching any [SyncOnJoin] field re-serializes and re-sends all of them. A Manual
        /// payload may be <see cref="NetworkLimits.ManualSyncMaxBytes"/> while the whole instance shares
        /// <see cref="NetworkLimits.NetworkBudgetBytesPerSecond"/> per second, and VRChat's rate limiting is
        /// size-proportional -- so a behaviour mixing a large cold snapshot with a small hot field starves
        /// the instance AND throttles the hot field, precisely because the two are bundled.
        ///
        /// Each group gets its own synced string, its own capture, and its own restore, and capture only
        /// asks VRChat to serialize when some group's payload actually changed. Splitting the two above puts
        /// the cold snapshot on the wire when it changes, and never because the hot field moved.
        ///
        /// The name is yours; it never appears on the wire. It is used to name generated members, so
        /// characters outside a-z, A-Z, 0-9 and _ become underscores -- two names that differ only in
        /// punctuation are a compile error rather than a silent merge back into one payload.
        ///
        /// Grouping is per behaviour. Grouping alone does not need a second GameObject, which the previous
        /// "split it onto another behaviour" workaround did: VRChat requires every synced UdonBehaviour on
        /// one GameObject to share a sync method.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Large, changes once a round.
        /// [SyncOnJoin(Group = "terrain")]
        /// public int[] heightmap;
        ///
        /// // Small, changes constantly -- and no longer drags the heightmap along.
        /// [SyncOnJoin(Group = "score")]
        /// public int score;
        /// </code>
        /// </example>
        public string Group { get; set; } = null;

        /// <summary>
        /// Not implementable. Nothing reads this.
        /// </summary>
        /// <remarks>
        /// There is no per-variable acknowledgement in Udon: <c>SerializationResult</c> is reported per
        /// behaviour, not per field, so "confirmed delivery" for one field cannot be observed. Retry on a
        /// failed serialization is unconditional instead, which makes the flag redundant as well as inert.
        /// </remarks>
        [Obsolete("SyncOnJoin.Critical is not implementable: VRChat reports serialization success per " +
                  "behaviour, not per variable, so a single field's delivery cannot be acknowledged. " +
                  "Retry on failure is already unconditional.", true)]
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
    ///     currentState = JsonUtility.FromJson<GameState>(data);
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

    /// <summary>
    /// Marks compiler-emitted SyncOnJoin glue so repeat optimizer passes can detect it.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class SyncOnJoinGenerated : Attribute
    {
    }
}
