using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Eases a pose correction into a Rigidbody over several physics steps.
    /// </summary>
    /// <remarks>
    /// What is blended is an offset, not a destination. This used to hold a fixed world-space target and
    /// pull the body 20% of the way toward it every step, which only ever finishes for a body at rest:
    /// one that is moving leaves the target behind faster than the pull closes the gap, so it settled a
    /// fixed distance short of a point it could never reach and was held there until something replaced
    /// the target. On a client that had just become the authority nothing ever did, and the ball stayed
    /// tethered to the spot the last packet named.
    ///
    /// An offset is "how far the body still has to be moved". Each step hands over a fixed fraction of
    /// what is left, so the remainder shrinks geometrically whatever the body is doing in the meantime
    /// and the blend is finished after a bounded number of steps.
    /// </remarks>
    [PublicAPI]
    public class CorrectionBlender
    {
        // Slowest rate a blend is allowed to run at. Zero would hand over nothing per step and bring
        // back the blend that never ends.
        private const float MinBlendRate = 0.01f;

        private Vector3 _positionOffset;
        private Quaternion _rotationOffset;
        private bool _positionPending;
        private bool _rotationPending;

        public float PositionBlendRate = 0.15f;
        public float RotationBlendRate = 0.15f;

        /// <summary>
        /// Not read by this class. Velocity corrections are applied in full when they are measured:
        /// easing them in left the body travelling on a velocity already known to be wrong for the
        /// length of the blend, which is new position error the blend then had to remove as well.
        /// </summary>
        public float VelocityBlendRate = 0.25f;

        /// <summary>
        /// Remaining position offset in metres below which the rest is handed over in one go.
        /// </summary>
        public float CompletionThreshold = 0.001f;

        /// <summary>
        /// Remaining rotation offset in degrees below which the rest is handed over in one go.
        /// </summary>
        public float RotationCompletionDegrees = 0.1f;

        /// <summary>
        /// True while part of a correction has still to be moved onto the body.
        /// </summary>
        public bool HasOffset => _positionPending || _rotationPending;

        /// <summary>
        /// Same as <see cref="HasOffset"/>; the name dates from when this held a target pose.
        /// </summary>
        public bool HasTarget => _positionPending || _rotationPending;

        /// <summary>
        /// Position correction not yet moved onto the body.
        /// </summary>
        public Vector3 PendingPosition => _positionPending ? _positionOffset : Vector3.zero;

        /// <summary>
        /// Rotation correction not yet moved onto the body, as a world-space rotation to apply on top
        /// of the body's current one.
        /// </summary>
        public Quaternion PendingRotation => _rotationPending ? _rotationOffset : Quaternion.identity;

        /// <summary>
        /// Drops whatever is still pending. The body stays where it is.
        /// </summary>
        public void Clear()
        {
            ClearPosition();
            ClearRotation();
        }

        /// <summary>
        /// Same as <see cref="Clear"/>; the name dates from when this held a target pose.
        /// </summary>
        public void ClearTarget()
        {
            Clear();
        }

        public void ClearPosition()
        {
            _positionOffset = Vector3.zero;
            _positionPending = false;
        }

        public void ClearRotation()
        {
            _rotationOffset = Quaternion.identity;
            _rotationPending = false;
        }

        /// <summary>
        /// Adds a correction to what is still pending. Corrections stack: a second one arriving before
        /// the first has finished is measured against the fully corrected pose, so it is owed on top.
        /// </summary>
        public void AddOffset(Vector3 positionOffset, Quaternion rotationOffset)
        {
            if (!_positionPending)
                _positionOffset = Vector3.zero;

            _positionOffset += positionOffset;
            _positionPending = true;

            // The stored rotation is only meaningful while one is pending; a default Quaternion is all
            // zeroes, which is not a rotation and poisons everything multiplied by it.
            if (!_rotationPending)
                _rotationOffset = Quaternion.identity;

            _rotationOffset = rotationOffset * _rotationOffset;
            _rotationPending = true;
        }

        /// <summary>
        /// Takes this step's share of the pending position offset and removes it from what is left.
        /// </summary>
        public Vector3 TakePositionStep()
        {
            if (!_positionPending)
                return Vector3.zero;

            Vector3 step = _positionOffset * ClampRate(PositionBlendRate);
            _positionOffset -= step;

            if (_positionOffset.sqrMagnitude <= CompletionThreshold * CompletionThreshold)
            {
                step += _positionOffset;
                ClearPosition();
            }

            return step;
        }

        /// <summary>
        /// Takes this step's share of the pending rotation offset and removes it from what is left.
        /// </summary>
        public Quaternion TakeRotationStep()
        {
            if (!_rotationPending)
                return Quaternion.identity;

            Quaternion step = Quaternion.Slerp(Quaternion.identity, _rotationOffset, ClampRate(RotationBlendRate));

            // Whatever is left has to satisfy remaining * step == offset.
            _rotationOffset = _rotationOffset * Quaternion.Inverse(step);

            if (Quaternion.Angle(Quaternion.identity, _rotationOffset) <= RotationCompletionDegrees)
            {
                step = _rotationOffset * step;
                ClearRotation();
            }

            return step;
        }

        /// <summary>
        /// Moves one step's share of the pending correction onto the body. Call once per physics step.
        /// </summary>
        public void ApplyBlend(Rigidbody rb)
        {
            if (rb == null)
                return;

            if (_positionPending)
                rb.position = rb.position + TakePositionStep();

            if (_rotationPending)
                rb.rotation = TakeRotationStep() * rb.rotation;
        }

        private static float ClampRate(float rate)
        {
            if (rate < MinBlendRate)
                return MinBlendRate;

            return rate > 1f ? 1f : rate;
        }
    }
}
