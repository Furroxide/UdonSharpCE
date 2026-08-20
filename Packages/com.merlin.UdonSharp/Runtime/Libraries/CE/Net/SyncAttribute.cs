using System;
using JetBrains.Annotations;

namespace UdonSharp.CE.Net
{
    /// <summary>
    /// Enhanced synchronization attribute. Declares a synced field's authority, and optionally its
    /// interpolation, quantization, and advisory bandwidth hints.
    /// This inherits from <see cref="UdonSyncedAttribute"/> so the upstream sync pipeline treats
    /// <c>[Sync]</c> exactly like <c>[UdonSynced]</c>.
    /// </summary>
    /// <remarks>
    /// Authority is positional and required; everything else is named. That asymmetry is deliberate.
    /// The named options are tuning: pick the wrong interpolation and remote clients look janky, pick
    /// the wrong quantization and the value is coarse -- both are visible and both are cheap to fix.
    /// Authority is not tuning. It is the difference between a write that reaches other clients and a
    /// write that silently does not, and that failure is invisible on the machine that caused it. So it
    /// is the one thing a reader of the field is never asked to infer from the surrounding code.
    /// See <see cref="Net.Authority"/> for what each role costs and buys.
    /// </remarks>
    /// <example>
    /// <code>
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class GameState : UdonSharpBehaviour
    /// {
    ///     // Authoritative: one client decides the score and the rest are told. CE0014 checks that
    ///     // every write to it is ownership-guarded.
    ///     [Sync(Authority.Owner)]
    ///     public int score;
    ///
    ///     // Still owner-authoritative, but smoothed on remotes and quantized to shrink the payload.
    ///     [Sync(Authority.Owner, InterpolationMode.Smooth, Quantize = 0.01f)]
    ///     public Vector3 position;
    ///
    ///     // Every client drains this locally so the meter does not stall between packets; the owner's
    ///     // periodic serialization is what stops the guesses drifting apart.
    ///     [Sync(Authority.Predicted)]
    ///     public float boostRemaining;
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    [AttributeUsage(AttributeTargets.Field)]
    public class SyncAttribute : UdonSyncedAttribute
    {
        private InterpolationMode _interpolation;

        /// <summary>
        /// Who is allowed to write this field, and therefore what a write on a non-owner means.
        /// Set from the constructor; see <see cref="Net.Authority"/>.
        /// </summary>
        public Authority Authority { get; }

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
        /// Not implemented. Nothing reads this.
        /// </summary>
        /// <remarks>
        /// VRChat has no priority-based sync: every synced variable on a behaviour goes out in the same
        /// serialization, in the order the compiler emitted them. The documentation previously called this
        /// "advisory", but no analyzer read it either, so setting it had no effect anywhere at all.
        /// To make some state travel independently of the rest, put it on a separate behaviour -- each
        /// behaviour serializes on its own.
        /// </remarks>
        [Obsolete("Sync.Priority is not implemented and nothing reads it. VRChat has no priority-based sync; " +
                  "all of a behaviour's synced variables travel in one serialization. Put independently-" +
                  "changing state on a separate behaviour instead.", true)]
        public float Priority { get; set; } = 0.5f;

        /// <summary>
        /// Creates a new Sync attribute declaring who owns the field's value.
        /// </summary>
        /// <param name="authority">
        /// Who may write this field. Required, and required positionally: wrong interpolation merely
        /// looks janky, while wrong authority is a silent desync, so the mandatory slot belongs to
        /// authority rather than to any of the tuning options.
        /// </param>
        /// <param name="interpolation">The interpolation mode to use. Defaults to None.</param>
        public SyncAttribute(Authority authority, InterpolationMode interpolation = InterpolationMode.None)
            : base(GetSyncMode(interpolation))
        {
            Authority = authority;

            // Assigned through the property, not the backing field: the setter is what keeps the
            // inherited NetworkSyncType in step, and that is the value the upstream pipeline actually
            // serializes with. Setting _interpolation directly would leave the two disagreeing and
            // silently disarm CE0010's Manual-mode interpolation check.
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
