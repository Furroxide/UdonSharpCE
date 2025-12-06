using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Marks a method as local-only, preventing it from being called via SendCustomNetworkEvent.
    /// The analyzer will generate an error if this method is used with network calls.
    /// </summary>
    /// <example>
    /// <code>
    /// public class GameManager : UdonSharpBehaviour
    /// {
    ///     // This method should never be called over the network
    ///     [LocalOnly("Visual effects should only run locally")]
    ///     private void PlayLocalVFX(int effectId)
    ///     {
    ///         vfxPool[effectId].Play();
    ///     }
    ///
    ///     // RPC that uses the local-only method internally
    ///     [Rpc(Target = RpcTarget.All)]
    ///     public void TriggerEffect(int effectId)
    ///     {
    ///         PlayLocalVFX(effectId); // OK - called locally
    ///     }
    ///
    ///     // This would generate a compile-time error:
    ///     // public void BadExample()
    ///     // {
    ///     //     SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayLocalVFX));
    ///     // }
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// Use [LocalOnly] for:
    /// - Visual effects that should only render locally
    /// - Audio that should only play locally
    /// - Performance-critical code that shouldn't be network-triggered
    /// - Helper methods that access local-only state
    ///
    /// This is similar to Issue #112/#143 concepts for [NonNetworked] methods.
    /// </remarks>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Method)]
    public class LocalOnlyAttribute : Attribute
    {
        /// <summary>
        /// Optional message explaining why this method is local-only.
        /// Displayed in analyzer errors when network calls are attempted.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Creates a new LocalOnly attribute.
        /// </summary>
        /// <param name="message">Optional message explaining why this method is local-only.</param>
        public LocalOnlyAttribute(string message = null)
        {
            Message = message;
        }
    }
}
