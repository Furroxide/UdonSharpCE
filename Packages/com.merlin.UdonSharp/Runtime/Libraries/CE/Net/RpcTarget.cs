using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Specifies the target recipients for an RPC call.
    /// Maps to VRChat's NetworkEventTarget but provides clearer naming.
    /// </summary>
    [PublicAPI]
    public enum RpcTarget
    {
        /// <summary>
        /// Send to all players including the sender.
        /// Maps to NetworkEventTarget.All.
        /// </summary>
        All = 0,

        /// <summary>
        /// Send only to the owner of this object.
        /// Maps to NetworkEventTarget.Owner.
        /// </summary>
        Owner = 1,

        /// <summary>
        /// Send to all players except the sender.
        /// Useful for avoiding duplicate local execution.
        /// </summary>
        Others = 2,

        /// <summary>
        /// Execute locally only (no network transmission).
        /// Equivalent to calling the method directly.
        /// </summary>
        Self = 3
    }
}
