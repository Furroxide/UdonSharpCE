using UdonSharp;
using UnityEngine;

namespace CEShowcase.Station1_BulletHell
{
    /// <summary>
    /// Marks a GameObject as a surface that bullets should bounce off.
    /// Place this on any object with a Collider to make bullets reflect off it.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    [RequireComponent(typeof(Collider))]
    public class BounceWall : UdonSharpBehaviour
    {
        private Collider _collider;
        
        /// <summary>
        /// Gets the world-space bounds of this bounce wall.
        /// </summary>
        public Bounds GetBounds()
        {
            if (_collider == null)
            {
                _collider = GetComponent<Collider>();
            }
            return _collider.bounds;
        }
        
        void Start()
        {
            _collider = GetComponent<Collider>();
        }
    }
}




















