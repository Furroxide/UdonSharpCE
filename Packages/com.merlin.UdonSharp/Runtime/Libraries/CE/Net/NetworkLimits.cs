using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Constants defining VRChat's network limits.
    /// Use these to ensure your sync payloads stay within bounds.
    /// </summary>
    [PublicAPI]
    public static class NetworkLimits
    {
        /// <summary>
        /// Maximum bytes for continuous sync mode (~200 bytes).
        /// Exceeding this limit causes sync issues and potential data loss.
        /// </summary>
        public const int ContinuousSyncMaxBytes = 200;

        /// <summary>
        /// Network budget in bytes per second (~11 KB/s).
        /// This is shared across all synced objects in the world.
        /// </summary>
        public const int NetworkBudgetBytesPerSecond = 11264;

        /// <summary>
        /// Maximum string length for synced string variables (~50 characters).
        /// Longer strings may be truncated or cause sync failures.
        /// </summary>
        public const int MaxSyncedStringLength = 50;

        /// <summary>
        /// Maximum number of parameters for SendCustomNetworkEvent.
        /// VRChat supports up to 8 parameters per RPC call.
        /// </summary>
        public const int MaxRpcParameters = 8;

        /// <summary>
        /// Warning threshold as percentage of continuous sync limit.
        /// Triggers warnings when approaching the limit.
        /// </summary>
        public const float WarningThreshold = 0.8f;

        /// <summary>
        /// Gets the warning threshold in bytes for continuous sync.
        /// </summary>
        public static int ContinuousSyncWarningBytes => (int)(ContinuousSyncMaxBytes * WarningThreshold);
    }
}
