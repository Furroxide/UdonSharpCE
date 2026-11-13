using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Marks a method as local-only: one the behaviour that declares it must not send a network event to.
    /// CE0012 is the check, and what it covers is specific -- see the remarks.
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
    /// What is checked. CE0012 is a compile error on a send, written inside the declaring behaviour, that
    /// names this method: SendCustomNetworkEvent or CENet.Rpc, with the event name given as a string
    /// literal or a nameof, and sent to this behaviour itself (no receiver, this, base) or to a reference
    /// whose type is the declaring type or derives from it.
    ///
    /// What is not. The attribute is only ever read by editor-side checks: it does not change how the
    /// method is compiled, and it is not a runtime barrier. A send from a different behaviour type,
    /// a send through a base-typed reference, and an event name built at runtime are all outside the
    /// check. This summary used to promise an error whenever the method "is used with network calls",
    /// which the analyzer has never been able to deliver.
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
