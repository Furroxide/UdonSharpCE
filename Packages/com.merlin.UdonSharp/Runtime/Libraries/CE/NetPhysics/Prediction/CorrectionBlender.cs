using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Smoothly blends a Rigidbody toward a corrected target state.
    /// </summary>
    [PublicAPI]
    public class CorrectionBlender
    {
        private Vector3 _targetPosition;
        private Quaternion _targetRotation;
        private Vector3 _targetVelocity;
        private bool _hasTarget;

        public float PositionBlendRate = 0.15f;
        public float RotationBlendRate = 0.15f;
        public float VelocityBlendRate = 0.25f;
        public float CompletionThreshold = 0.001f;

        public bool HasTarget => _hasTarget;

        public void ClearTarget()
        {
            _hasTarget = false;
        }

        public void SetTarget(Vector3 position, Quaternion rotation, Vector3 velocity)
        {
            _targetPosition = position;
            _targetRotation = rotation;
            _targetVelocity = velocity;
            _hasTarget = true;
        }

        public void ApplyBlend(Rigidbody rb)
        {
            if (!_hasTarget || rb == null)
                return;

            float completionSqr = CompletionThreshold * CompletionThreshold;

            Vector3 posDiff = _targetPosition - rb.position;
            if (posDiff.sqrMagnitude > completionSqr)
            {
                rb.MovePosition(rb.position + posDiff * PositionBlendRate);
            }

            float angleDiff = Quaternion.Angle(rb.rotation, _targetRotation);
            if (angleDiff > 0.1f)
            {
                rb.MoveRotation(Quaternion.Slerp(rb.rotation, _targetRotation, RotationBlendRate));
            }

            Vector3 velDiff = _targetVelocity - rb.velocity;
            if (velDiff.sqrMagnitude > completionSqr)
            {
                rb.velocity = Vector3.Lerp(rb.velocity, _targetVelocity, VelocityBlendRate);
            }

            float totalError = posDiff.magnitude + angleDiff * 0.01f + velDiff.magnitude * 0.1f;
            if (totalError < CompletionThreshold * 3f)
            {
                _hasTarget = false;
            }
        }
    }
}

