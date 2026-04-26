using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Enhanced synchronization attribute with interpolation, quantization, and advisory bandwidth hints.
    /// This inherits from <see cref="UdonSyncedAttribute"/> so the upstream sync pipeline treats
    /// <c>[Sync]</c> exactly like <c>[UdonSynced]</c>.
    /// </summary>
    /// <example>
    /// <code>
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class GameState : UdonSharpBehaviour
    /// {
    ///     // Basic sync with linear interpolation
    ///     [Sync(InterpolationMode.Linear)]
    ///     public int score;
    ///
    ///     // Position with quantization for bandwidth savings
    ///     [Sync(InterpolationMode.Smooth, Quantize = 0.01f)]
    ///     public Vector3 position;
    ///
    ///     // Large array with delta encoding
    ///     [Sync(DeltaEncode = true)]
    ///     public int[] playerScores = new int[32];
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncAttribute : UdonSyncedAttribute
    {
        private InterpolationMode _interpolation;

        /// <summary>
        /// Interpolation mode for this variable.
        /// Determines how values are smoothed between sync updates.
        /// </summary>
        public InterpolationMode Interpolation
        {
            get => _interpolation;
            set
            {
                _interpolation = value;
                NetworkSyncType = GetSyncMode(value);
            }
        }

        /// <summary>
        /// Advisory hint used by analyzers and bandwidth estimation.
        /// VRChat does not provide true delta-sync support for Udon synced fields.
        /// </summary>
        /// <remarks>
        /// For real delta-style behaviour, use manual sync with <c>RequestSerialization()</c>
        /// and a custom serializer that transmits only the changed payload.
        /// </remarks>
        public bool DeltaEncodeHint { get; set; } = false;

        /// <summary>
        /// Backwards-compatible alias for <see cref="DeltaEncodeHint"/>.
        /// </summary>
        [Obsolete("DeltaEncode is a bandwidth-estimator hint only; VRChat does not support true delta sync. Use Manual sync + a custom serializer for real delta encoding.", false)]
        public bool DeltaEncode
        {
            get => DeltaEncodeHint;
            set => DeltaEncodeHint = value;
        }

        /// <summary>
        /// Quantization precision for float-compatible types.
        /// Values are rounded to the nearest multiple of this value during <c>OnPreSerialization</c>.
        /// Set to 0 for no quantization (default).
        /// </summary>
        /// <remarks>
        /// Quantization reduces effective precision before sync.
        /// VRChat still transmits its normal synced field payload, but repeated values
        /// become more compressible and less noisy.
        /// </remarks>
        public float Quantize { get; set; } = 0f;

        /// <summary>
        /// Sync priority (0.0 to 1.0). Higher values indicate more important data.
        /// Used by analyzers to provide warnings about sync payload organization.
        /// </summary>
        /// <remarks>
        /// Priority is advisory only. VRChat doesn't support priority-based sync.
        /// Consider using separate behaviours for different priority levels.
        /// </remarks>
        public float Priority { get; set; } = 0.5f;

        /// <summary>
        /// Creates a new Sync attribute with the specified interpolation mode.
        /// </summary>
        /// <param name="interpolation">The interpolation mode to use. Defaults to None.</param>
        public SyncAttribute(InterpolationMode interpolation = InterpolationMode.None)
            : base(GetSyncMode(interpolation))
        {
            Interpolation = interpolation;
        }

        private static UdonSyncMode GetSyncMode(InterpolationMode interpolation)
        {
            return interpolation switch
            {
                InterpolationMode.Linear => UdonSyncMode.Linear,
                InterpolationMode.Smooth => UdonSyncMode.Smooth,
                _ => UdonSyncMode.None
            };
        }
    }
}
