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
    /// The method has to be an instance method that returns void (CE0011, CE0043): a network event is
    /// delivered to a behaviour instance and carries nothing back. Arguments are sent as the declared
    /// parameter types -- a call that relies on an implicit conversion, such as passing 1 to a float
    /// parameter, is converted before it is sent, exactly as a direct call would convert it.
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
        /// Maximum events per second, as a whole number from 1 to 100. Leaving it at 0 does not mean
        /// unlimited: it means "not declared", and the method gets the [NetworkCallable] default of 5.
        /// VRChat enforces the limit on the sending client.
        /// </summary>
        /// <remarks>
        /// Compiled into [NetworkCallable(maxEventsPerSecond)], so VRChat enforces the limit itself.
        /// That attribute takes a whole number of events per second between 1 and 100 and defaults to 5,
        /// and the SDK rejects anything outside that range for every network-callable method, with or
        /// without parameters -- there is no unthrottled [Rpc]. This comment used to say that 0 meant
        /// unlimited and that parameterless events were unthrottled; the emitter believed it, and a
        /// parameterless [Rpc] with no RateLimit did not compile.
        ///
        /// A declared value outside 1..100, or one with a fractional part, cannot be expressed. It is
        /// rounded up and held to that range, and the compile says so with a console warning naming the
        /// rate VRChat will actually enforce, rather than adjusting it in silence -- a RateLimit of 500
        /// that becomes 100 is a cap the author never agreed to, and one of 0.2 that becomes 1 is five
        /// times the traffic they asked for.
        /// </remarks>
        public float RateLimit { get; set; } = 0f;

        /// <summary>
        /// Removed. What happens to a network event past its method's rate limit is decided inside
        /// VRChat's own networking layer, and [NetworkCallable] has no setting for it, so neither value on
        /// this property could ever have changed what happens.
        /// </summary>
        /// <remarks>
        /// This errors rather than warns because the only reason to write it was DropOnRateLimit = false,
        /// and that read as a guarantee CE was making about the excess -- one nothing here could keep.
        /// The SDK does let a behaviour read how many of its events are still waiting to be sent
        /// (NetworkCalling.GetQueuedEvents and GetAllQueuedEvents); it offers nothing to choose between
        /// outcomes. If the timing of a burst matters, pace the calls before they are sent, with a
        /// RateLimiter.
        /// </remarks>
        [Obsolete("DropOnRateLimit cannot be honoured: what happens to a network event past its rate limit " +
                  "is decided by VRChat, and [NetworkCallable] has no setting for it. Delete this argument " +
                  "and pace calls with a RateLimiter if the timing of a burst matters.", true)]
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
