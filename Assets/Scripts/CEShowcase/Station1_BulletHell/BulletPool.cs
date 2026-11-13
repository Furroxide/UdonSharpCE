using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;

namespace CEShowcase.Station1_BulletHell
{
    /// <summary>
    /// Simple bullet pool for the Bullet Hell demo.
    /// Uses direct array management instead of CEPool (which requires lambdas).
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BulletPool : UdonSharpBehaviour
    {
        [Header("Pool Settings")]
        [SerializeField] private GameObject _bulletPrefab;
        [SerializeField] private int _poolSize = 2500;
        
        [Header("Visual Settings")]
        [SerializeField] private Material _bulletMaterial;
        
        // Direct array access for fast position updates
        private Transform[] _bulletTransforms;
        private bool[] _bulletActive;
        
        // Free list for O(1) acquire
        private int[] _freeStack;
        private int _freeCount;
        
        // Stats
        private int _acquireCount;
        private int _releaseCount;
        
        // The pool is built by the first AcquireBullet, not in Start. The showcase scene carries this
        // component and nothing in it ever acquires from it -- BulletHellDemo keeps its own transforms --
        // so building eagerly instantiated 2,500 bullets nobody would see, on every client, at world
        // load. A pool that is used still pays the whole cost in one go, on its first acquire.
        private bool _poolBuilt;

        private void EnsurePool()
        {
            if (_poolBuilt) return;

            _poolBuilt = true;
            InitializePool();
        }
        
        private void InitializePool()
        {
            CELogger.Info("BulletPool", "Initializing pool with " + _poolSize + " bullets");
            
            // Pre-allocate arrays
            _bulletTransforms = new Transform[_poolSize];
            _bulletActive = new bool[_poolSize];
            _freeStack = new int[_poolSize];
            _freeCount = 0;
            
            if (_bulletPrefab == null)
            {
                // Leave the pool empty rather than carry on: with nothing to instantiate every slot
                // would hold a null transform, and the first acquire would halt this behaviour on it.
                CELogger.Error("BulletPool", "Bullet prefab is required! GameObject.CreatePrimitive is not exposed to Udon.");
                return;
            }

            // Create every pool object in one pass (no Instantiate after this)
            for (int i = 0; i < _poolSize; i++)
            {
                _bulletTransforms[i] = CreateBullet(i);
                _bulletActive[i] = false;
                
                // All indices start free
                _freeStack[_freeCount] = i;
                _freeCount++;
            }
            
            CELogger.Info("BulletPool", "Pool initialization complete");
        }
        
        private Transform CreateBullet(int index)
        {
            GameObject bullet = Instantiate(_bulletPrefab, transform);
            bullet.name = "Bullet_" + index;
            bullet.SetActive(false);
            
            return bullet.transform;
        }
        
        /// <summary>
        /// Acquire a bullet from the pool.
        /// Returns the pool index for O(1) release later.
        /// </summary>
        public int AcquireBullet(Vector3 position)
        {
            EnsurePool();

            if (_freeCount <= 0)
            {
                // Pool exhausted
                return -1;
            }
            
            _freeCount--;
            int index = _freeStack[_freeCount];
            
            _bulletActive[index] = true;
            _bulletTransforms[index].position = position;
            _bulletTransforms[index].gameObject.SetActive(true);
            _acquireCount++;
            
            return index;
        }
        
        /// <summary>
        /// Release bullet back to pool by index (O(1) operation).
        /// </summary>
        public void ReleaseBullet(int index)
        {
            if (!_poolBuilt) return;
            if (index < 0 || index >= _poolSize) return;
            if (!_bulletActive[index]) return;
            
            _bulletActive[index] = false;
            _bulletTransforms[index].gameObject.SetActive(false);
            
            // Return to free stack
            _freeStack[_freeCount] = index;
            _freeCount++;
            _releaseCount++;
        }
        
        /// <summary>
        /// Update bullet position directly (fast path).
        /// </summary>
        public void UpdateBulletPosition(int index, Vector3 position)
        {
            if (!_poolBuilt) return;
            if (index < 0 || index >= _poolSize) return;
            if (!_bulletActive[index]) return;
            
            _bulletTransforms[index].position = position;
        }
        
        /// <summary>
        /// Release all active bullets
        /// </summary>
        public void ReleaseAll()
        {
            // Nothing has been handed out by a pool that was never built
            if (!_poolBuilt) return;

            _freeCount = 0;
            
            for (int i = 0; i < _poolSize; i++)
            {
                // A pool built without a prefab has no bullets to offer
                if (_bulletTransforms[i] == null) continue;

                if (_bulletActive[i])
                {
                    _bulletActive[i] = false;
                    _bulletTransforms[i].gameObject.SetActive(false);
                }
                
                // Rebuild free stack
                _freeStack[_freeCount] = i;
                _freeCount++;
            }
            
            _releaseCount = 0;
            _acquireCount = 0;
        }
        
        // ============ Stats ============
        
        // Counted from what was handed out, not from the free stack: the stack is empty until the pool
        // is built, which would report an untouched pool as fully in use.
        public int GetActiveCount() { return _acquireCount - _releaseCount; }
        public int GetCapacity() { return _poolSize; }
        public int GetAcquireCount() { return _acquireCount; }
        public int GetReleaseCount() { return _releaseCount; }
        
        /// <summary>
        /// Get pool utilization as percentage (0-100)
        /// </summary>
        public float GetUtilization()
        {
            if (_poolSize <= 0) return 0f;

            return (GetActiveCount() / (float)_poolSize) * 100f;
        }
    }
}
