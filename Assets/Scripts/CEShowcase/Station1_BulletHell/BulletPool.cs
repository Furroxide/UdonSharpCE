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
        
        void Start()
        {
            InitializePool();
        }
        
        private void InitializePool()
        {
            CELogger.Info("BulletPool", "Initializing pool with " + _poolSize + " bullets");
            
            // Pre-allocate arrays
            _bulletTransforms = new Transform[_poolSize];
            _bulletActive = new bool[_poolSize];
            _freeStack = new int[_poolSize];
            _freeCount = _poolSize;
            
            // Initialize all pool objects upfront (avoid runtime Instantiate)
            for (int i = 0; i < _poolSize; i++)
            {
                Transform bullet = CreateBullet(i);
                _bulletTransforms[i] = bullet;
                _bulletActive[i] = false;
                bullet.gameObject.SetActive(false);
                
                // All indices start free
                _freeStack[i] = i;
            }
            
            CELogger.Info("BulletPool", "Pool initialization complete");
        }
        
        private Transform CreateBullet(int index)
        {
            if (_bulletPrefab == null)
            {
                CELogger.Error("BulletPool", "Bullet prefab is required! GameObject.CreatePrimitive is not exposed to Udon.");
                return null;
            }
            
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
            if (index < 0 || index >= _poolSize) return;
            if (!_bulletActive[index]) return;
            
            _bulletTransforms[index].position = position;
        }
        
        /// <summary>
        /// Release all active bullets
        /// </summary>
        public void ReleaseAll()
        {
            _freeCount = 0;
            
            for (int i = 0; i < _poolSize; i++)
            {
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
        
        public int GetActiveCount() { return _poolSize - _freeCount; }
        public int GetCapacity() { return _poolSize; }
        public int GetAcquireCount() { return _acquireCount; }
        public int GetReleaseCount() { return _releaseCount; }
        
        /// <summary>
        /// Get pool utilization as percentage (0-100)
        /// </summary>
        public float GetUtilization()
        {
            return ((_poolSize - _freeCount) / (float)_poolSize) * 100f;
        }
    }
}
