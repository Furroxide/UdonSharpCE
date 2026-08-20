using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Marks a method as a network-callable RPC with validation and rate limiting.
    /// Methods marked with [Rpc] are validated at compile-time for proper signatures.
    /// </summary>
    /// <example>
    /// <code>
    /// public class ChatSystem : UdonSharpBehaviour
    /// {
    ///     // Basic RPC to all players
    ///     [Rpc(Target = RpcTarget.All)]
    ///     public void SendMessage(string message)
    ///     {
    ///         DisplayMessage(message);
    ///     }
    ///
    ///     // Rate-limited RPC (max 5 calls per second)
    ///     [Rpc(Target = RpcTarget.All, RateLimit = 5f)]
    ///     public void PlaySound(int soundId)
    ///     {
    ///         audioSource.PlayOneShot(sounds[soundId]);
    ///     }
    ///
    ///     // Owner-only RPC for game control
    ///     [Rpc(Target = RpcTarget.All, OwnerOnly = true)]
    ///     public void StartGame()
    ///     {
    ///         gameStarted = true;
    ///     }
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// VRChat Limits:
    /// - Maximum 8 parameters per RPC
    /// - Parameters must be serializable types (primitives, Vector3, etc.)
    /// - Network budget: ~11 KB/s shared across all RPCs
    ///
    /// The compiler marks the method [NetworkCallable] and rewrites calls to it into
    /// SendCustomNetworkEvent with the declared target, so calling the method normally sends it.
    /// Use RpcTarget.Self when you want a plain local call.
    ///
    /// Calling one [Rpc] from inside another is CE0045: every client that received the first would send
    /// the second, so the traffic multiplies by the player count on each hop. Two shapes are still open.
    /// A method calling itself is left as an ordinary local call, so a recursive [Rpc] unwinds on each
    /// client instead of fanning out. And when the second hop is genuinely what you want, send it
    /// yourself with <see cref="CENet"/>, which is never rewritten.
    /// </remarks>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Method)]
    public class RpcAttribute : Attribute
    {
        /// <summary>
        /// Who should receive this RPC.
        /// </summary>
        public RpcTarget Target { get; set; } = RpcTarget.All;

        /// <summary>
        /// Maximum calls per second (0 = unlimited).
        /// Excess calls are dropped by VRChat before they leave the sending client.
        /// </summary>
        /// <remarks>
        /// Compiled into [NetworkCallable(maxEventsPerSecond)], so VRChat enforces the limit itself.
        /// That attribute takes a whole number of events per second between 1 and 100 for a method with
        /// parameters, and defaults to 5; parameterless events are unthrottled. A value outside that
        /// range, or one with a fractional part, cannot be expressed and is reported at compile time
        /// rather than quietly adjusted -- a RateLimit of 500 that becomes 100 is a cap the author never
        /// agreed to, and one of 0.2 that becomes 1 is five times the traffic they asked for.
        /// </remarks>
        public float RateLimit { get; set; } = 0f;

        /// <summary>
        /// Removed. VRChat drops network events past the per-method rate limit inside its own networking
        /// layer and exposes no queueing API, so neither value on this property could ever have changed
        /// what happens.
        /// </summary>
        /// <remarks>
        /// This errors rather than warns because the only reason to write it was DropOnRateLimit = false,
        /// and that read as "queue the excess instead of losing it" -- a guarantee nothing was making.
        /// If you need calls to survive a burst, pace them before they are sent, with a RateLimiter.
        /// </remarks>
        [Obsolete("DropOnRateLimit cannot be honoured: VRChat drops rate-limited network events itself and " +
                  "has no queueing API. Delete this argument and pace calls with a RateLimiter if you need " +
                  "them to survive a burst.", true)]
        public bool DropOnRateLimit { get; set; } = true;

        /// <summary>
        /// Whether only the owner can call this RPC.
        /// Enforced on the receiver via NetworkCalling.CallingPlayer: a call from a non-owner is dropped.
        /// A null calling player means a local call, which is always allowed.
        /// </summary>
        /// <remarks>
        /// The guard is injected as the first statement of the method body. An expression-bodied method
        /// is converted to a block first, so <c>[Rpc(OwnerOnly = true)] public void Reset() =&gt; DoReset();</c>
        /// is guarded like any other -- it used to be the one shape that got [NetworkCallable] with no
        /// check at all.
        /// </remarks>
        public bool OwnerOnly { get; set; } = false;

        /// <summary>
        /// Removed. Parameter types are validated on every [Rpc], unconditionally.
        /// </summary>
        /// <remarks>
        /// This errors rather than warns because ValidateParameters = false only ever suppressed the
        /// analyzer, not the failure: the compiler's emit phase rejects an unencodable parameter anyway,
        /// so the setting bought a later error with less context. Suppressing a check that is about to
        /// fire regardless is the same silent-failure shape [Rpc] exists to remove.
        /// </remarks>
        [Obsolete("Parameter validation is unconditional. This only ever moved the same error from the " +
                  "analyzer to the emit phase, so delete the argument.", true)]
        public bool ValidateParameters { get; set; } = true;

        /// <summary>
        /// Creates a new Rpc attribute with the specified target.
        /// </summary>
        /// <param name="target">Who should receive this RPC. Defaults to All.</param>
        public RpcAttribute(RpcTarget target = RpcTarget.All)
        {
            Target = target;
        }
    }

    /// <summary>
    /// Shorthand for [Rpc(OwnerOnly = true)].
    /// Marks a method as an owner-only RPC that can only be called by the object's owner.
    /// </summary>
    /// <example>
    /// <code>
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class ScoreBoard : UdonSharpBehaviour
    /// {
    ///     [Sync(Authority.Owner)] public int score;
    ///
    ///     // Only the owner can reset scores. The compiler prepends the ownership check, so writing
    ///     // one by hand here would only run it twice.
    ///     [RpcOwnerOnly]
    ///     public void ResetScore()
    ///     {
    ///         score = 0;
    ///         RequestSerialization();
    ///     }
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Method)]
    public class RpcOwnerOnlyAttribute : RpcAttribute
    {
        /// <summary>
        /// Creates a new owner-only RPC attribute.
        /// </summary>
        public RpcOwnerOnlyAttribute() : base(RpcTarget.All)
        {
            OwnerOnly = true;
        }
    }
}
