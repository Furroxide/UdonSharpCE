using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Specifies the target recipients for an RPC call.
    /// Maps to VRChat's NetworkEventTarget but provides clearer naming.
    /// </summary>
    /// <remarks>
    /// <see cref="CENet.ToNetworkEventTarget"/> performs the mapping, and is public so hand-written
    /// SendCustomNetworkEvent calls can spell their target the same way an [Rpc] attribute does.
    /// </remarks>
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
        /// On an [Rpc] attribute: calls to the method are left as plain local calls, and nothing is sent.
        /// </summary>
        /// <remarks>
        /// That is a statement about the call sites, not about the method. The method is still marked
        /// [NetworkCallable] like every other [Rpc], so it remains a network event that an explicit
        /// SendCustomNetworkEvent or <see cref="CENet"/> send can reach; Self does not make it local-only.
        ///
        /// Passed to a <see cref="CENet"/> send it means something else again: NetworkEventTarget.Self,
        /// which does go through VRChat's network event path and delivers to the sending client.
        /// </remarks>
        Self = 3
    }
}
