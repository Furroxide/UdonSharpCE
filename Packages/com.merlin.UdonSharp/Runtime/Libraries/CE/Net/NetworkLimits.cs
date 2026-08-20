using System;
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
        /// Maximum bytes for manual sync mode.
        /// </summary>
        public const int ManualSyncMaxBytes = 280496;

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
        /// Approximate size of synced strings in Udon variable sync.
        /// </summary>
        public const int SyncedStringBytesPerCharacter = 2;

        /// <summary>
        /// Default character count used for string fields when no explicit max length is known.
        /// This is an estimator fallback, not a VRChat hard cap.
        /// </summary>
        public const int DefaultEstimatedSyncedStringLength = 50;

        /// <summary>
        /// Maximum encoded parameter payload for one parameterized network event.
        /// </summary>
        public const int NetworkEventMaxParameterBytes = 16 * 1024;

        /// <summary>
        /// Network events larger than this are split internally and consume multiple queued events.
        /// </summary>
        public const int NetworkEventSplitThresholdBytes = 1024;

        /// <summary>
        /// Approximate outgoing hard cap for parameterized network events, including overhead.
        /// </summary>
        public const int NetworkEventOutgoingHardCapBytesPerSecond = 18 * 1024;

        /// <summary>
        /// Legacy name for the default string-size estimate.
        /// </summary>
        [Obsolete("Use DefaultEstimatedSyncedStringLength. VRChat does not document a 50-character synced string cap.")]
        public const int MaxSyncedStringLength = DefaultEstimatedSyncedStringLength;

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

        /// <summary>
        /// Gets the warning threshold in bytes for manual sync.
        /// </summary>
        public static int ManualSyncWarningBytes => (int)(ManualSyncMaxBytes * WarningThreshold);
    }
}
