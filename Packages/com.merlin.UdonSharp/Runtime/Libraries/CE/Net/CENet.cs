using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Explicit network sends, for the call sites the [Rpc] rewrite deliberately refuses to rewrite.
    ///
    /// Calling an [Rpc] method normally is the common path: the compiler turns the call into a
    /// SendCustomNetworkEvent with the declared target. That rewrite is refused for a call made from
    /// inside another [Rpc] method (CE0045), because every receiving client would re-broadcast and
    /// multiply the traffic by the player count on each hop. Sometimes that fan-out is exactly what the
    /// author wants -- an owner-only command that relays to everyone, for instance. This class is the
    /// supported way to say so, at the call site, where a reader can see the send.
    /// </summary>
    /// <example>
    /// <code>
    /// [Rpc(Target = RpcTarget.Owner, OwnerOnly = true)]
    /// public void RequestStart()
    /// {
    ///     // A plain StartGame() call here would be CE0045: the rewrite would make every receiver
    ///     // re-broadcast. Sending explicitly is one hop and reads as one hop.
    ///     CENet.Rpc(this, RpcTarget.All, nameof(StartGame));
    /// }
    ///
    /// [Rpc]
    /// public void StartGame() { gameStarted = true; }
    /// </code>
    /// </example>
    /// <remarks>
    /// This is hand-written rather than generated on purpose. CE's transforms run inside UdonSharp's own
    /// private compilation while Unity compiles the original source, so a generated member is invisible
    /// at the call site and could never be called from user code.
    ///
    /// The target method still has to be network callable in its own right -- public, or marked
    /// [NetworkCallable] (which [Rpc] applies for you). VRChat's own rate limit for that method still
    /// applies; routing through here does not bypass it.
    ///
    /// <see cref="RpcTarget.Self"/> here is not the same as <see cref="RpcTarget.Self"/> on an [Rpc]
    /// attribute. The attribute's Self means "never send, just call the method"; passing Self here still
    /// goes through VRChat's network event path, which serializes and deserializes the parameters before
    /// delivering them locally.
    /// </remarks>
    [PublicAPI]
    public static class CENet
    {
        /// <summary>
        /// Sends a parameterless network event to <paramref name="eventName"/> on <paramref name="target"/>.
        /// </summary>
        /// <param name="target">The behaviour that owns the event method.</param>
        /// <param name="to">Who should run it.</param>
        /// <param name="eventName">Name of the method to run. Use nameof so a rename cannot silently break the send.</param>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName);
        }

        /// <summary>
        /// Sends a network event carrying one parameter.
        /// </summary>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName, object param0)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName, param0);
        }

        /// <summary>
        /// Sends a network event carrying two parameters.
        /// </summary>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName, object param0, object param1)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName, param0, param1);
        }

        /// <summary>
        /// Sends a network event carrying three parameters.
        /// </summary>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName, object param0, object param1, object param2)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName, param0, param1, param2);
        }

        /// <summary>
        /// Sends a network event carrying four parameters.
        /// </summary>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName, object param0, object param1, object param2, object param3)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName, param0, param1, param2, param3);
        }

        /// <summary>
        /// Sends a network event carrying five parameters.
        /// </summary>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName, object param0, object param1, object param2, object param3, object param4)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName, param0, param1, param2, param3, param4);
        }

        /// <summary>
        /// Sends a network event carrying six parameters.
        /// </summary>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName, object param0, object param1, object param2, object param3, object param4, object param5)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName, param0, param1, param2, param3, param4, param5);
        }

        /// <summary>
        /// Sends a network event carrying seven parameters.
        /// </summary>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName, object param0, object param1, object param2, object param3, object param4, object param5, object param6)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName, param0, param1, param2, param3, param4, param5, param6);
        }

        /// <summary>
        /// Sends a network event carrying eight parameters, VRChat's maximum.
        /// </summary>
        public static void Rpc(UdonSharpBehaviour target, RpcTarget to, string eventName, object param0, object param1, object param2, object param3, object param4, object param5, object param6, object param7)
        {
            if (!Validate(target, eventName))
                return;

            target.SendCustomNetworkEvent(ToNetworkEventTarget(to), eventName, param0, param1, param2, param3, param4, param5, param6, param7);
        }

        /// <summary>
        /// Maps CE's <see cref="RpcTarget"/> onto VRChat's NetworkEventTarget.
        /// </summary>
        /// <remarks>
        /// Public because hand-written SendCustomNetworkEvent calls sitting next to [Rpc] declarations
        /// should be able to spell their target the same way the attribute does.
        /// </remarks>
        public static NetworkEventTarget ToNetworkEventTarget(RpcTarget target)
        {
            switch (target)
            {
                case RpcTarget.Owner:
                    return NetworkEventTarget.Owner;

                case RpcTarget.Others:
                    return NetworkEventTarget.Others;

                case RpcTarget.Self:
                    return NetworkEventTarget.Self;

                default:
                    return NetworkEventTarget.All;
            }
        }

        /// <summary>
        /// Rejects the two shapes that would otherwise be dropped by VRChat with no local trace.
        /// </summary>
        /// <remarks>
        /// A null target sends nothing at all, and an empty event name reaches VRChat as a lookup that
        /// fails on the receiving client rather than on the sending one -- so the author never sees it.
        /// Logging here puts the failure on the machine that caused it.
        /// </remarks>
        private static bool Validate(UdonSharpBehaviour target, string eventName)
        {
            if (target == null)
            {
                Debug.LogError("[CE.Net] CENet.Rpc was given a null target behaviour; nothing was sent.");
                return false;
            }

            if (string.IsNullOrEmpty(eventName))
            {
                Debug.LogError("[CE.Net] CENet.Rpc was given an empty event name; nothing was sent.");
                return false;
            }

            return true;
        }
    }
}
