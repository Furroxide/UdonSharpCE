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
    /// state across several payloads, each restored only when it changed.
    ///
    /// The payload is restored in OnDeserialization, and in Start as well:
    /// VRChat drops an OnDeserialization that arrives before the behaviour has
    /// started -- the usual case for an object that is inactive when a player
    /// joins -- so a payload already in place by then is restored when Start
    /// runs. That restore runs before your own Start body.
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
    ///     // Its own payload: a change to the history does not re-apply the phase.
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
        /// Each group gets its own synced string, its own capture, and its own restore. A client decodes
        /// only the groups whose string changed, and capture only asks VRChat to serialize when some
        /// group's string actually changed.
        ///
        /// Group does not change what one serialization sends. VRChat serializes all of a behaviour's
        /// synced variables together, so every group's string is in every serialization of the behaviour,
        /// changed or not, and the whole serialization has to fit in
        /// <see cref="NetworkLimits.ManualSyncMaxBytes"/>. State that should go on the wire less often
        /// than the rest belongs on a separate behaviour, which serializes on its own.
        ///
        /// The name is yours; it never appears on the wire. It is used to name generated members, so
        /// characters outside a-z, A-Z, 0-9 and _ become underscores -- two names that differ only in
        /// punctuation are a compile error rather than a silent merge back into one payload.
        ///
        /// Grouping is per behaviour. A separate behaviour on the same GameObject works as long as it is
        /// Manual as well: VRChat requires every synced UdonBehaviour on one GameObject to share a sync
        /// method, and [SyncOnJoin] makes a behaviour Manual.
        /// </remarks>
        /// <example>
        /// <code>
        /// // Large, changes once a round.
        /// [SyncOnJoin(Group = "terrain")]
        /// public int[] heightmap;
        ///
        /// // Small, changes constantly -- and no longer makes every client re-decode the heightmap.
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
        /// behaviour, not per field, so "confirmed delivery" for one field cannot be observed.
        ///
        /// Nothing retries a failed serialization either. Capture asks VRChat to serialize only when a
        /// payload changed, so a serialization VRChat did not deliver -- one larger than
        /// <see cref="NetworkLimits.ManualSyncMaxBytes"/>, which the generated glue logs as an error --
        /// is sent again only once the state changes, and late joiners keep the previous snapshot until
        /// then.
        /// </remarks>
        [Obsolete("SyncOnJoin.Critical is not implementable: VRChat reports serialization success per " +
                  "behaviour, not per variable, so a single field's delivery cannot be acknowledged. " +
                  "A failed serialization is not retried; the payload is sent again when the state next changes.", true)]
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
