using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Tracks predicted collisions to avoid duplicating effects during correction/rollback.
    /// </summary>
    [PublicAPI]
    public class CollisionTracker
    {
        public int DeDupWindowFrames = 3;

        private readonly int[] _recentHitFrames;
        private int _hitCount;

        public CollisionTracker(int capacity = 16)
        {
            _recentHitFrames = new int[Mathf.Max(1, capacity)];
            _hitCount = 0;
        }

        public void OnPredictedHit(int frame, Vector3 hitPoint, float hitForce)
        {
            for (int i = 0; i < Mathf.Min(_hitCount, _recentHitFrames.Length); i++)
            {
                if (Mathf.Abs(_recentHitFrames[i] - frame) < DeDupWindowFrames)
                    return;
            }

            _recentHitFrames[_hitCount % _recentHitFrames.Length] = frame;
            _hitCount++;

            PlayHitSound(hitPoint, hitForce);
            SpawnHitParticles(hitPoint);
        }

        protected virtual void PlayHitSound(Vector3 hitPoint, float hitForce) { }
        protected virtual void SpawnHitParticles(Vector3 hitPoint) { }
    }
}

