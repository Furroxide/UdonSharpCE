using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Runtime rate limiter for RPC calls.
    /// Use this to enforce rate limits on frequently-called network methods.
    /// </summary>
    /// <example>
    /// <code>
    /// public class ChatSystem : UdonSharpBehaviour
    /// {
    ///     // Rate limiter: max 2 messages per second
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
        /// Gets or sets the rate limit in calls per second.
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
        /// <param name="rateLimit">Maximum calls per second (0 = unlimited).</param>
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
        public static RateLimiter FromAttribute(RpcAttribute rpcAttribute, string methodName = null)
        {
            if (rpcAttribute == null || rpcAttribute.RateLimit <= 0f)
                return null;

            return new RateLimiter(rpcAttribute.RateLimit, rpcAttribute.DropOnRateLimit, methodName);
        }
    }
}
