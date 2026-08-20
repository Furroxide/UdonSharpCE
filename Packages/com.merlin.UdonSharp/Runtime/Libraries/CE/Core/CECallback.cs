using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Core
{
    /// <summary>
    /// Udon-compatible callback abstraction that mimics delegate behavior.
    /// 
    /// Since Udon doesn't support delegates (Action, Func, etc.), CECallback provides
    /// an alternative using UdonSharpBehaviour references and method name strings.
    /// Invocation is performed via SendCustomEvent.
    /// </summary>
    /// <remarks>
    /// This struct is designed to work transparently with the CE compile-time
    /// Action transformer, which converts standard Action usage to CECallback.
    /// 
    /// Usage:
    /// <code>
    /// // Manual usage
    /// var callback = new CECallback(myBehaviour, "OnComplete");
    /// callback.Invoke();
    /// 
    /// // Or use the static Create method
    /// var callback = CECallback.Create(myBehaviour, nameof(OnComplete));
    /// </code>
    /// </remarks>
    [PublicAPI]
    public struct CECallback
    {
        /// <summary>
        /// The UdonSharpBehaviour that owns the method to invoke.
        /// </summary>
        public UdonSharpBehaviour Target;

        /// <summary>
        /// The name of the method to invoke on the target behaviour.
        /// </summary>
        public string MethodName;

        /// <summary>
        /// Creates a new CECallback with the specified target and method name.
        /// </summary>
        /// <param name="target">The behaviour to invoke the method on.</param>
        /// <param name="methodName">The name of the method to invoke.</param>
        public CECallback(UdonSharpBehaviour target, string methodName)
        {
            Target = target;
            MethodName = methodName;
        }

        /// <summary>
        /// Gets whether this callback is valid and can be invoked.
        /// A callback is valid if it has a non-null target and a non-empty method name.
        /// </summary>
        public bool IsValid => Target != null && !string.IsNullOrEmpty(MethodName);

        /// <summary>
        /// Invokes the callback by calling SendCustomEvent on the target behaviour.
        /// Does nothing if the callback is invalid.
        /// </summary>
        /// <remarks>
        /// <see cref="MethodName"/> has to name a <b>public, parameterless, instance</b> method on
        /// <see cref="Target"/>. U# only exports those under their written name -- anything else is either
        /// left out of the program entirely or exported under a mangled one, and SendCustomEvent then finds
        /// no entry point and returns without raising anything.
        /// </remarks>
        public void Invoke()
        {
            if (IsValid)
            {
                Target.SendCustomEvent(MethodName);
            }
        }

        /// <summary>
        /// Invokes the callback with error logging if the target is invalid.
        /// Useful for debugging callback registration issues.
        /// </summary>
        /// <param name="context">Context string for the error message (e.g., "SystemTick").</param>
        public void InvokeWithLogging(string context)
        {
            if (Target == null)
            {
                Debug.LogWarning($"[CE.Callback] {context}: Cannot invoke callback - target is null");
                return;
            }

            if (string.IsNullOrEmpty(MethodName))
            {
                Debug.LogWarning($"[CE.Callback] {context}: Cannot invoke callback - method name is empty");
                return;
            }

            Target.SendCustomEvent(MethodName);
        }

        /// <summary>
        /// Creates a new CECallback instance.
        /// This is a convenience method that can be used with nameof().
        /// </summary>
        /// <param name="target">The behaviour to invoke the method on.</param>
        /// <param name="methodName">The name of the method to invoke (use nameof()).</param>
        /// <returns>A new CECallback instance.</returns>
        /// <example>
        /// <code>
        /// var callback = CECallback.Create(this, nameof(OnComplete));
        /// </code>
        /// </example>
        public static CECallback Create(UdonSharpBehaviour target, string methodName)
        {
            return new CECallback(target, methodName);
        }

        /// <summary>
        /// Returns an invalid/empty callback instance.
        /// </summary>
        /// <remarks>
        /// Constructed explicitly rather than returned as <c>default</c>. Udon represents a user type as an
        /// <c>object[]</c>, and the compiler lowers <c>default</c> for one to a plain null reference -- so
        /// the "empty" callback had no backing array at all and reading <see cref="IsValid"/> on it threw
        /// instead of answering false.
        /// </remarks>
        public static CECallback None => new CECallback(null, null);

        /// <summary>
        /// Returns a string representation of this callback for debugging.
        /// </summary>
        public override string ToString()
        {
            if (!IsValid)
                return "CECallback(Invalid)";
            
            string targetName = Target != null ? Target.name : "null";
            return $"CECallback({targetName}.{MethodName})";
        }
    }
}








































