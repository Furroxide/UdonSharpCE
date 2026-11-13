using System;
using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Runtime rate limiter for anything you send or run on a schedule of your own.
    /// Use this to pace hand-written network sends, serialization requests, or any hot local call.
    /// </summary>
    /// <remarks>
    /// The limit is a minimum interval, not a count. A limiter built with a rate of N lets a call through
    /// and then treats every call for the next 1/N seconds as over the limit -- refused when DropOnLimit
    /// is true, let through with a logged warning when it is false. It does not allow "any N calls
    /// within a second": <c>new RateLimiter(5f)</c> counts the second of two calls made 50 ms apart as
    /// over the limit even when nothing else was called that second, so a burst is thinned to one call
    /// per interval rather than passed through up to a budget. Time is read from <c>Time.time</c>, which
    /// advances once per frame, so two calls in the same frame are never both within the limit, and the
    /// rate actually achieved is at most one call per frame and a little under N whenever the interval
    /// does not land on a frame boundary.
    ///
    /// Do not stack one of these on an [Rpc] method: VRChat already enforces that method's declared rate
    /// through [NetworkCallable], and a second limiter in front of it only makes the effective rate
    /// harder to reason about.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class ChatSystem : UdonSharpBehaviour
    /// {
    ///     // Rate limiter: at most one message every half second (a rate of 2 per second)
    ///     private RateLimiter _chatLimiter;
    ///
    ///     void Start()
    ///     {
    ///         _chatLimiter = new RateLimiter(2f, true);
    ///     }
    ///
    ///     public void SendChatMessage(string message)
    ///     {
    ///         if (!_chatLimiter.TryCall())
    ///         {
    ///             Debug.Log("Chat rate limited!");
    ///             return;
    ///         }
    ///
    ///         SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ReceiveMessage), message);
    ///     }
    /// }
    /// </code>
    /// </example>
    /// <remarks>
    /// Rate limiting is essential for preventing network spam in VRChat worlds.
    /// Without rate limiting, malicious or buggy code can flood the network
    /// and cause lag for all players.
    ///
    /// Note: Rate limiter state resets on world reload. For persistent rate
    /// limiting across sessions, consider using PlayerData.
    /// </remarks>
    [PublicAPI]
    public class RateLimiter
    {
        private float _lastCallTime;
        private float _minInterval;
        private bool _dropOnLimit;
        private string _name;

        /// <summary>
        /// Gets or sets the rate in calls per second, enforced as a minimum interval of 1 / value seconds
        /// between calls rather than as a count per second. 0 means unlimited.
        /// </summary>
        public float RateLimit
        {
            get => _minInterval > 0f ? 1f / _minInterval : 0f;
            set => _minInterval = value > 0f ? 1f / value : 0f;
        }

        /// <summary>
        /// Gets or sets whether to drop rate-limited calls.
        /// If false, TryCall() always returns true but logs warnings.
        /// </summary>
        public bool DropOnLimit
        {
            get => _dropOnLimit;
            set => _dropOnLimit = value;
        }

        /// <summary>
        /// Gets the time since the last successful call in seconds.
        /// </summary>
        public float TimeSinceLastCall => Time.time - _lastCallTime;

        /// <summary>
        /// Gets whether a call would be allowed right now without consuming the rate limit.
        /// </summary>
        public bool CanCall => _minInterval <= 0f || TimeSinceLastCall >= _minInterval;

        /// <summary>
        /// Creates a new rate limiter.
        /// </summary>
        /// <param name="rateLimit">
        /// Rate in calls per second (0 = unlimited). Enforced as a minimum interval of 1 / rateLimit
        /// seconds between calls, so it is a spacing, not a budget of that many calls in any one second.
        /// </param>
        /// <param name="dropOnLimit">Whether to drop rate-limited calls (true) or just warn (false).</param>
        /// <param name="name">Optional name for logging purposes.</param>
        public RateLimiter(float rateLimit = 0f, bool dropOnLimit = true, string name = null)
        {
            _minInterval = rateLimit > 0f ? 1f / rateLimit : 0f;
            _dropOnLimit = dropOnLimit;
            _name = name;
            _lastCallTime = float.MinValue;
        }

        /// <summary>
        /// Attempts to make a call, updating the last call time if successful.
        /// </summary>
        /// <returns>True if the call should proceed, false if rate limited and DropOnLimit is true.</returns>
        public bool TryCall()
        {
            if (_minInterval <= 0f)
            {
                _lastCallTime = Time.time;
                return true;
            }

            float currentTime = Time.time;
            float elapsed = currentTime - _lastCallTime;

            if (elapsed >= _minInterval)
            {
                _lastCallTime = currentTime;
                return true;
            }

            // Rate limited
            string logName = string.IsNullOrEmpty(_name) ? "RPC" : _name;
            float remaining = _minInterval - elapsed;
            Debug.LogWarning($"[CE.Net] {logName} rate limited. Next call allowed in {remaining:F2}s");

            if (!_dropOnLimit)
            {
                _lastCallTime = currentTime;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Resets the rate limiter, allowing the next call immediately.
        /// </summary>
        public void Reset()
        {
            _lastCallTime = float.MinValue;
        }

        /// <summary>
        /// Creates a rate limiter from an RpcAttribute's settings.
        /// </summary>
        /// <param name="rpcAttribute">The RPC attribute to read settings from.</param>
        /// <param name="methodName">Optional method name for logging.</param>
        /// <returns>A configured rate limiter, or null if no rate limit is set.</returns>
        /// <remarks>
        /// [Rpc(RateLimit = n)] compiles into [NetworkCallable(n)] and VRChat enforces it natively, so a
        /// limiter built from the same attribute throttles the same call a second time: the effective
        /// rate becomes whichever of the two fires first, and calls disappear at a rate neither the
        /// attribute nor this object describes on its own.
        /// </remarks>
        [Obsolete("VRChat enforces [Rpc(RateLimit = n)] itself through [NetworkCallable(n)]; a limiter " +
                  "built from the same attribute throttles the call twice. Construct a RateLimiter " +
                  "directly for pacing that is not an [Rpc].")]
        public static RateLimiter FromAttribute(RpcAttribute rpcAttribute, string methodName = null)
        {
            if (rpcAttribute == null || rpcAttribute.RateLimit <= 0f)
                return null;

            // DropOnRateLimit is gone from the attribute, so there is no drop-or-warn choice left to read
            // from it. Dropping is what this always built when the attribute left the option alone.
            return new RateLimiter(rpcAttribute.RateLimit, true, methodName);
        }
    }
}
