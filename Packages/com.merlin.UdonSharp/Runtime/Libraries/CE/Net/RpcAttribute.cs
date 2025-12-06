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
    /// The [Rpc] attribute is primarily for documentation and compile-time validation.
    /// At runtime, you still call SendCustomNetworkEvent() to invoke the RPC.
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
        /// Excess calls are logged as warnings and optionally dropped.
        /// </summary>
        /// <remarks>
        /// Rate limiting is enforced at runtime via the RateLimiter utility.
        /// The analyzer will warn if no rate limit is set on public RPCs.
        /// </remarks>
        public float RateLimit { get; set; } = 0f;

        /// <summary>
        /// Whether to drop rate-limited calls (true) or queue them (false).
        /// Default is true (drop excess calls).
        /// </summary>
        /// <remarks>
        /// Dropping is safer for most use cases to prevent network spam.
        /// Queueing may cause delays and unexpected behavior.
        /// </remarks>
        public bool DropOnRateLimit { get; set; } = true;

        /// <summary>
        /// Whether only the owner can call this RPC.
        /// If true, non-owners calling this RPC will be logged as warnings.
        /// </summary>
        public bool OwnerOnly { get; set; } = false;

        /// <summary>
        /// Whether to validate parameters at compile time.
        /// When true, the analyzer checks that all parameter types are serializable.
        /// </summary>
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
    ///     [Sync] public int score;
    ///
    ///     // Only the owner can reset scores
    ///     [RpcOwnerOnly]
    ///     public void ResetScore()
    ///     {
    ///         if (!Networking.IsOwner(gameObject)) return;
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
