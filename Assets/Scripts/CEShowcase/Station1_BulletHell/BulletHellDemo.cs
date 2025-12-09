using UdonSharp;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Perf;
using UnityEngine;

namespace CEShowcase.Station1_BulletHell
{
    /// <summary>
    /// Station 1: Bullet Storm - Demonstrates CEWorld ECS + CEPool handling 2000+ simultaneous projectiles.
    /// Shows the performance benefits of batched entity processing vs individual GameObjects.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class BulletHellDemo : UdonSharpBehaviour
    {
        [Header("Pool Settings")]
        [SerializeField] private GameObject _bulletPrefab;
        [SerializeField] private Transform _poolParent;
        [SerializeField] private int _maxBullets = 2000;
        
        [Header("Spawn Settings")]
        [SerializeField] private Transform[] _spawnPoints;
        [SerializeField] private float _spawnRate = 500f; // Bullets per second
        [SerializeField] private float _bulletSpeed = 15f;
        [SerializeField] private float _bulletLifetime = 3f;
        
        [Header("Pattern Settings")]
        [SerializeField] private int _patternMode = 0; // 0=Spiral, 1=Wave, 2=Random
        
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _statsText;
        [SerializeField] private UnityEngine.UI.Slider _spawnRateSlider;
        
        // ECS World
        private CEWorld _world;
        
        // Component Arrays (Structure of Arrays layout)
        private Vector3[] _positions;
        private Vector3[] _velocities;
        private float[] _lifetimes;
        private bool[] _active;
        
        // Visual pool
        private Transform[] _bulletTransforms;
        private int[] _entityToTransform; // Maps entity ID to transform index
        
        // Pool management
        private int[] _freeTransformStack;
        private int _freeTransformCount;
        
        // Stats
        private int _activeCount;
        private int _createdThisFrame;
        private int _destroyedThisFrame;
        private float _spawnAccumulator;
        private float _spiralAngle;
        
        // Component slot indices
        private const int SLOT_POSITION = 0;
        private const int SLOT_VELOCITY = 1;
        private const int SLOT_LIFETIME = 2;
        private const int SLOT_ACTIVE = 3;
        
        void Start()
        {
            InitializeECS();
            InitializePool();
            
            CELogger.Info("BulletHell", $"Bullet Hell Demo initialized with capacity for {_maxBullets} bullets");
        }
        
        private void InitializeECS()
        {
            // Create the ECS world
            _world = new CEWorld(_maxBullets);
            
            // Allocate component arrays
            _positions = new Vector3[_maxBullets];
            _velocities = new Vector3[_maxBullets];
            _lifetimes = new float[_maxBullets];
            _active = new bool[_maxBullets];
            
            // Register components with the world
            _world.RegisterComponent(SLOT_POSITION, _positions);
            _world.RegisterComponent(SLOT_VELOCITY, _velocities);
            _world.RegisterComponent(SLOT_LIFETIME, _lifetimes);
            _world.RegisterComponent(SLOT_ACTIVE, _active);
            
            // Entity-to-transform mapping
            _entityToTransform = new int[_maxBullets];
            for (int i = 0; i < _maxBullets; i++)
            {
                _entityToTransform[i] = -1;
            }
        }
        
        private void InitializePool()
        {
            _bulletTransforms = new Transform[_maxBullets];
            _freeTransformStack = new int[_maxBullets];
            _freeTransformCount = _maxBullets;
            
            // Pre-instantiate all bullet visuals
            for (int i = 0; i < _maxBullets; i++)
            {
                if (_bulletPrefab != null)
                {
                    GameObject bullet = Instantiate(_bulletPrefab, _poolParent);
                    bullet.SetActive(false);
                    _bulletTransforms[i] = bullet.transform;
                }
                
                // Initialize free stack (all transforms available)
                _freeTransformStack[i] = i;
            }
            
            CELogger.Debug("BulletHell", $"Pool initialized with {_maxBullets} bullet transforms");
        }
        
        void Update()
        {
            float dt = Time.deltaTime;
            
            _createdThisFrame = 0;
            _destroyedThisFrame = 0;
            
            // Spawn new bullets
            SpawnBullets(dt);
            
            // Update all entities (the core ECS loop)
            UpdateMovementSystem(dt);
            UpdateLifetimeSystem(dt);
            
            // Sync transforms
            SyncTransforms();
            
            // Process deferred destruction
            ProcessDestroyQueue();
            
            // Update UI
            UpdateStats();
        }
        
        private void SpawnBullets(float dt)
        {
            _spawnAccumulator += _spawnRate * dt;
            
            int toSpawn = (int)_spawnAccumulator;
            _spawnAccumulator -= toSpawn;
            
            for (int i = 0; i < toSpawn; i++)
            {
                SpawnBullet();
            }
        }
        
        private void SpawnBullet()
        {
            // Check if we can spawn
            if (_freeTransformCount <= 0) return;
            if (_world.ActiveEntityCount >= _maxBullets) return;
            
            // Create entity
            int entityId = _world.CreateEntity();
            if (entityId == CEWorld.InvalidEntity) return;
            
            // Acquire transform from pool
            _freeTransformCount--;
            int transformIndex = _freeTransformStack[_freeTransformCount];
            _entityToTransform[entityId] = transformIndex;
            
            // Calculate spawn position and direction
            Vector3 spawnPos = GetSpawnPosition();
            Vector3 direction = GetBulletDirection();
            
            // Set component data
            _positions[entityId] = spawnPos;
            _velocities[entityId] = direction * _bulletSpeed;
            _lifetimes[entityId] = _bulletLifetime;
            _active[entityId] = true;
            
            // Activate visual
            Transform t = _bulletTransforms[transformIndex];
            if (t != null)
            {
                t.position = spawnPos;
                t.gameObject.SetActive(true);
            }
            
            _createdThisFrame++;
            _activeCount++;
        }
        
        private Vector3 GetSpawnPosition()
        {
            if (_spawnPoints == null || _spawnPoints.Length == 0)
            {
                return transform.position;
            }
            
            // Alternate between spawn points
            int index = _createdThisFrame % _spawnPoints.Length;
            return _spawnPoints[index].position;
        }
        
        private Vector3 GetBulletDirection()
        {
            switch (_patternMode)
            {
                case 0: // Spiral
                    _spiralAngle += 15f;
                    float rad = _spiralAngle * Mathf.Deg2Rad;
                    return new Vector3(Mathf.Sin(rad), 0, Mathf.Cos(rad));
                    
                case 1: // Wave
                    float wave = Mathf.Sin(Time.time * 5f + _createdThisFrame * 0.1f);
                    return new Vector3(wave * 0.5f, 0, 1).normalized;
                    
                case 2: // Random
                    return new Vector3(
                        Random.Range(-1f, 1f),
                        Random.Range(-0.2f, 0.2f),
                        Random.Range(-1f, 1f)
                    ).normalized;
                    
                default:
                    return Vector3.forward;
            }
        }
        
        /// <summary>
        /// Movement system - updates all bullet positions in a single batch.
        /// This is where ECS shines: cache-friendly iteration over parallel arrays.
        /// </summary>
        private void UpdateMovementSystem(float dt)
        {
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_active[i]) continue;
                
                // Position += Velocity * dt
                _positions[i] += _velocities[i] * dt;
            }
        }
        
        /// <summary>
        /// Lifetime system - decrements lifetimes and marks expired entities for destruction.
        /// Uses deferred destruction to avoid iterator invalidation.
        /// </summary>
        private void UpdateLifetimeSystem(float dt)
        {
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_active[i]) continue;
                
                _lifetimes[i] -= dt;
                
                if (_lifetimes[i] <= 0)
                {
                    // Mark for deferred destruction
                    _world.DestroyEntityDeferred(i);
                    _active[i] = false;
                }
            }
        }
        
        /// <summary>
        /// Syncs ECS positions to visual transforms.
        /// Only updates transforms for active entities.
        /// </summary>
        private void SyncTransforms()
        {
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_active[i]) continue;
                
                int transformIndex = _entityToTransform[i];
                if (transformIndex >= 0 && transformIndex < _bulletTransforms.Length)
                {
                    Transform t = _bulletTransforms[transformIndex];
                    if (t != null)
                    {
                        t.position = _positions[i];
                    }
                }
            }
        }
        
        /// <summary>
        /// Process entities marked for destruction.
        /// Returns transforms to the pool and cleans up entity data.
        /// </summary>
        private void ProcessDestroyQueue()
        {
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                EntityState state = _world.GetEntityState(i);
                if (state != EntityState.PendingDestroy) continue;
                
                // Return transform to pool
                int transformIndex = _entityToTransform[i];
                if (transformIndex >= 0)
                {
                    Transform t = _bulletTransforms[transformIndex];
                    if (t != null)
                    {
                        t.gameObject.SetActive(false);
                    }
                    
                    // Return to free stack
                    _freeTransformStack[_freeTransformCount] = transformIndex;
                    _freeTransformCount++;
                    _entityToTransform[i] = -1;
                }
                
                // Actually destroy the entity
                _world.DestroyEntity(i);
                _destroyedThisFrame++;
                _activeCount--;
            }
        }
        
        private void UpdateStats()
        {
            if (_statsText == null) return;
            
            float poolUtilization = (_maxBullets - _freeTransformCount) / (float)_maxBullets * 100f;
            float frameTime = Time.deltaTime * 1000f;
            
            _statsText.text = $"<b>BULLET HELL METRICS</b>\n" +
                             $"Active Entities: <color=#00FF00>{_world.ActiveEntityCount}</color>\n" +
                             $"Frame Time: <color=#FFFF00>{frameTime:F2}ms</color>\n" +
                             $"Pool Usage: <color=#00FFFF>{poolUtilization:F1}%</color>\n" +
                             $"Spawned/Frame: {_createdThisFrame}\n" +
                             $"Destroyed/Frame: {_destroyedThisFrame}\n" +
                             $"Spawn Rate: {_spawnRate:F0}/sec";
        }
        
        // UI Callbacks
        public void OnSpawnRateChanged()
        {
            if (_spawnRateSlider != null)
            {
                _spawnRate = _spawnRateSlider.value;
            }
        }
        
        public void SetPatternSpiral() => _patternMode = 0;
        public void SetPatternWave() => _patternMode = 1;
        public void SetPatternRandom() => _patternMode = 2;
        
        public void IncreaseSpawnRate()
        {
            _spawnRate = Mathf.Min(_spawnRate + 100, 2000);
            CELogger.Info("BulletHell", $"Spawn rate increased to {_spawnRate}/sec");
        }
        
        public void DecreaseSpawnRate()
        {
            _spawnRate = Mathf.Max(_spawnRate - 100, 50);
            CELogger.Info("BulletHell", $"Spawn rate decreased to {_spawnRate}/sec");
        }
        
        public void ClearAllBullets()
        {
            // Deactivate all active bullets
            for (int i = 0; i < _maxBullets; i++)
            {
                if (_active[i])
                {
                    _world.DestroyEntityDeferred(i);
                    _active[i] = false;
                }
            }
            
            ProcessDestroyQueue();
            CELogger.Info("BulletHell", "All bullets cleared");
        }
        
        // Public API for performance monitoring
        public int GetActiveEntityCount() => _world.ActiveEntityCount;
        public int GetMaxEntities() => _maxBullets;
        public float GetPoolUtilization() => (_maxBullets - _freeTransformCount) / (float)_maxBullets;
    }
}
