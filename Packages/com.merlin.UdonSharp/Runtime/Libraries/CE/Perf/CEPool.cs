using System;
using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.Perf
{
    /// <summary>
    /// Handle for a pooled object. Use this for O(1) release operations.
    /// </summary>
    /// <typeparam name="T">The type of the pooled object.</typeparam>
    [PublicAPI]
    public struct PoolHandle<T> where T : class
    {
        /// <summary>
        /// The pool index of the object.
        /// </summary>
        public readonly int Index;
        
        /// <summary>
        /// The pooled object.
        /// </summary>
        public readonly T Object;
        
        /// <summary>
        /// Creates a new pool handle.
        /// </summary>
        internal PoolHandle(int index, T obj)
        {
            Index = index;
            Object = obj;
        }
        
        /// <summary>
        /// Whether this handle is valid (has a valid index).
        /// </summary>
        public bool IsValid => Index >= 0;
        
        /// <summary>
        /// Returns an invalid pool handle.
        /// </summary>
        public static PoolHandle<T> Invalid => new PoolHandle<T>(-1, null);
    }

    /// <summary>
    /// Generic object pool for reusing objects without garbage collection.
    ///
    /// Pre-allocates a fixed number of objects at initialization and recycles them
    /// through acquire/release operations. Essential for maintaining stable frame rates
    /// in Udon's garbage-collection-sensitive environment.
    /// </summary>
    /// <typeparam name="T">The type of objects to pool. Must be a class type.</typeparam>
    /// <remarks>
    /// Pooling is critical for VRChat worlds with high object turnover:
    /// - Projectiles in shooter games
    /// - Particle effects (when Unity particles aren't sufficient)
    /// - Spawned items/pickups
    /// - Network objects
    ///
    /// The pool tracks active objects and provides callbacks for initialization
    /// and cleanup when objects are acquired or released.
    /// </remarks>
    /// <example>
    /// <code>
    /// public class BulletPool : UdonSharpBehaviour
    /// {
    ///     [SerializeField] private GameObject bulletPrefab;
    ///     [SerializeField] private int poolSize = 100;
    ///
    ///     private CEPool&lt;GameObject&gt; pool;
    ///
    ///     void Start()
    ///     {
    ///         pool = new CEPool&lt;GameObject&gt;(poolSize);
    ///         pool.Initialize(CreateBullet, OnBulletAcquired, OnBulletReleased);
    ///     }
    ///
    ///     GameObject CreateBullet(int index)
    ///     {
    ///         var bullet = Instantiate(bulletPrefab);
    ///         bullet.SetActive(false);
    ///         return bullet;
    ///     }
    ///
    ///     void OnBulletAcquired(GameObject bullet) => bullet.SetActive(true);
    ///     void OnBulletReleased(GameObject bullet) => bullet.SetActive(false);
    ///
    ///     public GameObject GetBullet() => pool.Acquire();
    ///     public void ReturnBullet(GameObject bullet) => pool.Release(bullet);
    /// }
    /// </code>
    /// </example>
    [PublicAPI]
    public class CEPool<T> where T : class
    {
        #region Fields

        /// <summary>
        /// Pool capacity (maximum pooled objects).
        /// </summary>
        private readonly int _capacity;

        /// <summary>
        /// All pooled objects.
        /// </summary>
        private readonly T[] _objects;

        /// <summary>
        /// Whether each slot is currently in use.
        /// </summary>
        private readonly bool[] _inUse;

        /// <summary>
        /// Stack of available slot indices for O(1) acquire.
        /// </summary>
        private readonly int[] _freeStack;

        /// <summary>
        /// Number of items in the free stack.
        /// </summary>
        private int _freeCount;

        /// <summary>
        /// Number of currently active (acquired) objects.
        /// </summary>
        private int _activeCount;

        /// <summary>
        /// Whether the pool has been initialized.
        /// </summary>
        private bool _initialized;

        /// <summary>
        /// Callback when an object is acquired from the pool.
        /// </summary>
        private Action<T> _onAcquire;

        /// <summary>
        /// Callback when an object is released back to the pool.
        /// </summary>
        private Action<T> _onRelease;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the pool capacity.
        /// </summary>
        public int Capacity => _capacity;

        /// <summary>
        /// Gets the number of currently active objects.
        /// </summary>
        public int ActiveCount => _activeCount;

        /// <summary>
        /// Gets the number of available objects.
        /// </summary>
        public int AvailableCount => _freeCount;

        /// <summary>
        /// Gets whether the pool has been initialized.
        /// </summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Gets whether the pool is full (no objects available).
        /// </summary>
        public bool IsFull => _freeCount == 0;

        /// <summary>
        /// Gets whether the pool is empty (all objects available).
        /// </summary>
        public bool IsEmpty => _activeCount == 0;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new object pool with the specified capacity.
        /// </summary>
        /// <param name="capacity">Maximum number of objects to pool.</param>
        public CEPool(int capacity)
        {
            if (capacity < 1) capacity = 1;
            if (capacity > 65535) capacity = 65535;

            _capacity = capacity;
            _objects = new T[capacity];
            _inUse = new bool[capacity];
            _freeStack = new int[capacity];
            _freeCount = 0;
            _activeCount = 0;
            _initialized = false;
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Initializes the pool by creating all objects.
        /// </summary>
        /// <param name="factory">Function to create each object. Receives the index.</param>
        /// <param name="onAcquire">Optional callback when an object is acquired.</param>
        /// <param name="onRelease">Optional callback when an object is released.</param>
        public void Initialize(Func<int, T> factory, Action<T> onAcquire = null, Action<T> onRelease = null)
        {
            if (_initialized)
            {
                Debug.LogWarning("[CE.Perf] CEPool: Already initialized");
                return;
            }

            if (factory == null)
            {
                Debug.LogError("[CE.Perf] CEPool: Factory cannot be null");
                return;
            }

            _onAcquire = onAcquire;
            _onRelease = onRelease;

            // Create all objects
            for (int i = 0; i < _capacity; i++)
            {
                _objects[i] = factory(i);
                _inUse[i] = false;
                _freeStack[i] = i;
            }

            _freeCount = _capacity;
            _activeCount = 0;
            _initialized = true;
        }

        /// <summary>
        /// Initializes the pool with a pre-created array of objects.
        /// </summary>
        /// <param name="objects">Array of pre-created objects.</param>
        /// <param name="onAcquire">Optional callback when an object is acquired.</param>
        /// <param name="onRelease">Optional callback when an object is released.</param>
        public void InitializeWithArray(T[] objects, Action<T> onAcquire = null, Action<T> onRelease = null)
        {
            if (_initialized)
            {
                Debug.LogWarning("[CE.Perf] CEPool: Already initialized");
                return;
            }

            if (objects == null)
            {
                Debug.LogError("[CE.Perf] CEPool: Objects array cannot be null");
                return;
            }

            _onAcquire = onAcquire;
            _onRelease = onRelease;

            int count = Math.Min(objects.Length, _capacity);
            for (int i = 0; i < count; i++)
            {
                _objects[i] = objects[i];
                _inUse[i] = false;
                _freeStack[i] = i;
            }

            _freeCount = count;
            _activeCount = 0;
            _initialized = true;
        }

        #endregion

        #region Acquire / Release

        /// <summary>
        /// Acquires an object from the pool.
        /// </summary>
        /// <returns>The acquired object, or null if the pool is empty.</returns>
        public T Acquire()
        {
            if (!_initialized)
            {
                Debug.LogError("[CE.Perf] CEPool: Not initialized");
                return null;
            }

            if (_freeCount == 0)
            {
                Debug.LogWarning("[CE.Perf] CEPool: Pool exhausted");
                return null;
            }

            // Pop from free stack
            _freeCount--;
            int index = _freeStack[_freeCount];

            _inUse[index] = true;
            _activeCount++;

            T obj = _objects[index];

            // Call acquire callback
            if (_onAcquire != null)
            {
                _onAcquire(obj);
            }

            return obj;
        }

        /// <summary>
        /// Acquires an object and returns its pool index.
        /// </summary>
        /// <param name="index">Output parameter for the object's index.</param>
        /// <returns>The acquired object, or null if the pool is empty.</returns>
        public T AcquireWithIndex(out int index)
        {
            index = -1;

            if (!_initialized)
            {
                Debug.LogError("[CE.Perf] CEPool: Not initialized");
                return null;
            }

            if (_freeCount == 0)
            {
                Debug.LogWarning("[CE.Perf] CEPool: Pool exhausted");
                return null;
            }

            // Pop from free stack
            _freeCount--;
            index = _freeStack[_freeCount];

            _inUse[index] = true;
            _activeCount++;

            T obj = _objects[index];

            if (_onAcquire != null)
            {
                _onAcquire(obj);
            }

            return obj;
        }

        /// <summary>
        /// Acquires an object from the pool and returns a handle for O(1) release.
        /// Preferred over Acquire() when you need to release the object later.
        /// </summary>
        /// <returns>A handle containing the object and its index, or Invalid if pool is empty.</returns>
        public PoolHandle<T> AcquireHandle()
        {
            if (!_initialized)
            {
                Debug.LogError("[CE.Perf] CEPool: Not initialized");
                return PoolHandle<T>.Invalid;
            }

            if (_freeCount == 0)
            {
                Debug.LogWarning("[CE.Perf] CEPool: Pool exhausted");
                return PoolHandle<T>.Invalid;
            }

            // Pop from free stack
            _freeCount--;
            int index = _freeStack[_freeCount];

            _inUse[index] = true;
            _activeCount++;

            T obj = _objects[index];

            // Call acquire callback
            if (_onAcquire != null)
            {
                _onAcquire(obj);
            }

            return new PoolHandle<T>(index, obj);
        }

        /// <summary>
        /// Releases an object back to the pool using its handle.
        /// O(1) operation - much faster than Release(obj).
        /// </summary>
        /// <param name="handle">The handle returned from AcquireHandle().</param>
        /// <returns>True if the object was released.</returns>
        public bool Release(PoolHandle<T> handle)
        {
            if (!handle.IsValid) return false;
            return ReleaseByIndex(handle.Index);
        }

        /// <summary>
        /// Releases an object back to the pool.
        /// O(n) operation - consider using AcquireHandle() and Release(handle) for O(1) performance.
        /// </summary>
        /// <param name="obj">The object to release.</param>
        /// <returns>True if the object was released, false if not found.</returns>
        public bool Release(T obj)
        {
            if (!_initialized || obj == null) return false;

            // Find the object in the pool - O(n) search
            int index = -1;
            for (int i = 0; i < _capacity; i++)
            {
                if (_objects[i] == obj && _inUse[i])
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
            {
                Debug.LogWarning("[CE.Perf] CEPool: Object not found in pool");
                return false;
            }

            return ReleaseByIndex(index);
        }

        /// <summary>
        /// Releases an object by its pool index.
        /// More efficient than Release() when the index is known.
        /// </summary>
        /// <param name="index">The pool index of the object.</param>
        /// <returns>True if the object was released.</returns>
        public bool ReleaseByIndex(int index)
        {
            if (!_initialized) return false;
            if (index < 0 || index >= _capacity) return false;
            if (!_inUse[index]) return false;

            // Call release callback
            if (_onRelease != null)
            {
                _onRelease(_objects[index]);
            }

            _inUse[index] = false;
            _activeCount--;

            // Push to free stack
            _freeStack[_freeCount] = index;
            _freeCount++;

            return true;
        }

        /// <summary>
        /// Releases all active objects back to the pool.
        /// </summary>
        public void ReleaseAll()
        {
            if (!_initialized) return;

            for (int i = 0; i < _capacity; i++)
            {
                if (_inUse[i])
                {
                    if (_onRelease != null)
                    {
                        _onRelease(_objects[i]);
                    }

                    _inUse[i] = false;
                    _freeStack[_freeCount] = i;
                    _freeCount++;
                }
            }

            _activeCount = 0;
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Gets an object by its pool index.
        /// </summary>
        /// <param name="index">The pool index.</param>
        /// <returns>The object, or null if invalid.</returns>
        public T GetByIndex(int index)
        {
            if (!_initialized) return null;
            if (index < 0 || index >= _capacity) return null;
            return _objects[index];
        }

        /// <summary>
        /// Checks if a pool index is currently in use.
        /// </summary>
        /// <param name="index">The pool index.</param>
        /// <returns>True if the slot is in use.</returns>
        public bool IsInUse(int index)
        {
            if (!_initialized) return false;
            if (index < 0 || index >= _capacity) return false;
            return _inUse[index];
        }

        /// <summary>
        /// Gets all active objects.
        /// </summary>
        /// <param name="result">Array to fill with active objects.</param>
        /// <returns>Number of objects written to result.</returns>
        public int GetActiveObjects(T[] result)
        {
            if (!_initialized || result == null) return 0;

            int count = 0;
            int maxCount = result.Length;

            for (int i = 0; i < _capacity && count < maxCount; i++)
            {
                if (_inUse[i])
                {
                    result[count] = _objects[i];
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Gets all active pool indices.
        /// </summary>
        /// <param name="result">Array to fill with indices.</param>
        /// <returns>Number of indices written to result.</returns>
        public int GetActiveIndices(int[] result)
        {
            if (!_initialized || result == null) return 0;

            int count = 0;
            int maxCount = result.Length;

            for (int i = 0; i < _capacity && count < maxCount; i++)
            {
                if (_inUse[i])
                {
                    result[count] = i;
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Iterates over all active objects and calls an action.
        /// </summary>
        /// <param name="action">Action to call for each active object.</param>
        public void ForEachActive(Action<T> action)
        {
            if (!_initialized || action == null) return;

            for (int i = 0; i < _capacity; i++)
            {
                if (_inUse[i])
                {
                    action(_objects[i]);
                }
            }
        }

        /// <summary>
        /// Iterates over all active objects and calls an action with index.
        /// </summary>
        /// <param name="action">Action to call for each active object with its index.</param>
        public void ForEachActiveWithIndex(Action<T, int> action)
        {
            if (!_initialized || action == null) return;

            for (int i = 0; i < _capacity; i++)
            {
                if (_inUse[i])
                {
                    action(_objects[i], i);
                }
            }
        }

        #endregion
    }
}
