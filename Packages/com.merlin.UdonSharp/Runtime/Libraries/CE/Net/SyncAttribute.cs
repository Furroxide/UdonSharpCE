using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Enhanced synchronization attribute with interpolation, quantization, and delta encoding options.
    /// This is a CE wrapper around [UdonSynced] that adds compile-time validation and additional features.
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
    /// <remarks>
    /// The [Sync] attribute works alongside [UdonSynced]. If both are present on a field,
    /// [Sync] settings take precedence for CE analyzers but the underlying [UdonSynced]
    /// behavior is preserved for VRChat compatibility.
    ///
    /// VRChat Limits:
    /// - Continuous sync: ~200 bytes maximum
    /// - Network budget: ~11 KB/s shared across all objects
    /// - String length: ~50 characters
    /// </remarks>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncAttribute : Attribute
    {
        /// <summary>
        /// The underlying UdonSyncMode to use.
        /// Automatically derived from InterpolationMode.
        /// </summary>
        public UdonSyncMode SyncMode { get; }

        /// <summary>
        /// Interpolation mode for this variable.
        /// Determines how values are smoothed between sync updates.
        /// </summary>
        public InterpolationMode Interpolation { get; set; }

        /// <summary>
        /// Enable delta encoding for arrays.
        /// When true, only changed elements are considered for bandwidth estimation.
        /// Note: This is a hint for analyzers; actual sync still sends full arrays.
        /// </summary>
        /// <remarks>
        /// Delta encoding is most effective for sparse updates where only a few
        /// elements change between syncs. For frequently changing arrays,
        /// consider using Manual sync mode and batching updates.
        /// </remarks>
        public bool DeltaEncode { get; set; } = false;

        /// <summary>
        /// Quantization precision for float types.
        /// Values are conceptually rounded to the nearest multiple of this value.
        /// Set to 0 for no quantization (default).
        /// </summary>
        /// <remarks>
        /// Quantization hints help analyzers estimate bandwidth savings.
        /// For example, Quantize = 0.01f on a Vector3 suggests ~50% bandwidth reduction.
        ///
        /// Note: VRChat doesn't support true quantization at the network level.
        /// This is primarily for documentation and analyzer hints.
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
        {
            Interpolation = interpolation;
            SyncMode = interpolation switch
            {
                InterpolationMode.Linear => UdonSyncMode.Linear,
                InterpolationMode.Smooth => UdonSyncMode.Smooth,
                _ => UdonSyncMode.None
            };
        }
    }
}
