using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Predicts ball motion locally using physics and applies corrections from server snapshots.
    /// </summary>
    [PublicAPI]
    public class BallPredictor
    {
        private NetPhysicsWorld _world;
        private Rigidbody _ballRb;
        private CorrectionBlender _blender;

        private Vector3[] _predictedPositions;
        private int _historyHead;

        public float CorrectionThreshold = 0.10f;
        public float SnapThreshold = 2.00f;

        public void Initialize(NetPhysicsWorld world, Rigidbody ballRigidbody, int historySize = 64)
        {
            _world = world;
            _ballRb = ballRigidbody;
            _blender = new CorrectionBlender();
            _predictedPositions = new Vector3[Mathf.Max(1, historySize)];
            _historyHead = 0;
        }

        public void ReceiveServerState(int frame, Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity)
        {
            if (_world == null || _ballRb == null || _predictedPositions == null)
                return;

            int framesAgo = (_world.CurrentFrame - 1) - frame;
            if (framesAgo < 0 || framesAgo >= _predictedPositions.Length)
                return;

            int idx = (_historyHead - 1 - framesAgo + _predictedPositions.Length) % _predictedPositions.Length;
            Vector3 predicted = _predictedPositions[idx];
            float error = Vector3.Distance(predicted, position);

            if (error <= CorrectionThreshold)
                return;

            CorrectBall(position, rotation, velocity, angularVelocity, error);
        }

        public void RecordPrediction()
        {
            if (_ballRb == null || _predictedPositions == null)
                return;

            _predictedPositions[_historyHead] = _ballRb.position;
            _historyHead = (_historyHead + 1) % _predictedPositions.Length;
        }

        public void ApplyBlend()
        {
            if (_blender == null || _ballRb == null)
                return;

            _blender.ApplyBlend(_ballRb);
        }

        private void CorrectBall(Vector3 position, Quaternion rotation, Vector3 velocity, Vector3 angularVelocity, float error)
        {
            if (error > SnapThreshold)
            {
                _ballRb.position = position;
                _ballRb.rotation = rotation;
                _ballRb.velocity = velocity;
                _ballRb.angularVelocity = angularVelocity;
                _blender.ClearTarget();
                return;
            }

            _blender.SetTarget(position, rotation, velocity);
        }
    }
}

