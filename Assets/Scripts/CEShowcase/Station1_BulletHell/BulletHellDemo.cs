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
        private const string LogTag = "BulletHell";
        
        [Header("Pool Settings")]
        [SerializeField] private GameObject _bulletPrefab;
        [SerializeField] private Transform _poolParent;
        [SerializeField] private int _maxBullets = 2000;
        
        [Header("Visual Settings")]
        [SerializeField] private Color _bulletColor = new Color(1f, 0.4f, 0.1f, 1f); // Orange glow
        [SerializeField] private float _bulletScale = 0.15f;
        
        [Header("Spawn Settings")]
        [SerializeField] private Transform[] _spawnPoints;
        [SerializeField] private float _spawnRate = 500f; // Bullets per second
        [SerializeField] private float _bulletSpeed = 15f;
        [SerializeField] private float _bulletLifetime = 10f; // Increased since we use bounds checking now
        
        [Header("Station Bounds")]
        [SerializeField] private Vector3 _stationCenter = new Vector3(40f, 2f, 0f);
        [SerializeField] private Vector3 _stationHalfExtents = new Vector3(18f, 5f, 18f);
        
        [Header("Pattern Settings")]
        [SerializeField] private int _patternMode = 2; // Force random for bounce demo
        
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
        private int[] _bounceCount; // Track bounces - bullets despawn after leaving bounds only if bounced
        
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
        
        // Threshold tracking (for logging significant events without spamming)
        private bool _wasPoolExhausted;
        private bool _wasEntityLimitReached;
        
        // Component slot indices
        private const int SLOT_POSITION = 0;
        private const int SLOT_VELOCITY = 1;
        private const int SLOT_LIFETIME = 2;
        private const int SLOT_ACTIVE = 3;
        private const int SLOT_BOUNCE = 4;
        
        #region Helper Methods
        
        private string GetPatternName(int mode)
        {
            switch (mode)
            {
                case 0: return "Spiral";
                case 1: return "Wave";
                case 2: return "Random";
                default: return $"Unknown({mode})";
            }
        }
        
        #endregion
        
        void Start()
        {
            CELogger.Debug(LogTag, "=== BulletHellDemo Starting ===");
            LogConfiguration();
            ValidateConfiguration();
            
            InitializeECS();
            InitializePool();
            
            CELogger.Info(LogTag, $"Bullet Hell Demo initialized successfully with capacity for {_maxBullets} bullets");
            CELogger.Debug(LogTag, $"Initial state: SpawnRate={_spawnRate}/s, Speed={_bulletSpeed}, Lifetime={_bulletLifetime}s, Pattern={GetPatternName(_patternMode)}");
        }
        
        private void LogConfiguration()
        {
            CELogger.Debug(LogTag, $"Configuration:");
            CELogger.Debug(LogTag, $"  MaxBullets: {_maxBullets}");
            CELogger.Debug(LogTag, $"  SpawnRate: {_spawnRate}/sec");
            CELogger.Debug(LogTag, $"  BulletSpeed: {_bulletSpeed}");
            CELogger.Debug(LogTag, $"  BulletLifetime: {_bulletLifetime}s");
            CELogger.Debug(LogTag, $"  PatternMode: {GetPatternName(_patternMode)}");
            CELogger.Debug(LogTag, $"  SpawnPoints: {(_spawnPoints != null ? _spawnPoints.Length : 0)}");
        }
        
        private void ValidateConfiguration()
        {
            bool hasWarnings = false;
            
            if (_bulletPrefab == null)
            {
                CELogger.Warning(LogTag, "BulletPrefab is not assigned - bullets will have no visual representation");
                hasWarnings = true;
            }
            
            if (_poolParent == null)
            {
                CELogger.Warning(LogTag, "PoolParent is not assigned - bullets will be instantiated at root level");
                hasWarnings = true;
            }
            
            if (_spawnPoints == null || _spawnPoints.Length == 0)
            {
                CELogger.Warning(LogTag, "No spawn points assigned - using transform.position as fallback");
                hasWarnings = true;
            }
            else
            {
                // Check for null spawn points
                int nullCount = 0;
                for (int i = 0; i < _spawnPoints.Length; i++)
                {
                    if (_spawnPoints[i] == null) nullCount++;
                }
                if (nullCount > 0)
                {
                    CELogger.Warning(LogTag, $"{nullCount}/{_spawnPoints.Length} spawn points are null");
                    hasWarnings = true;
                }
            }
            
            if (_statsText == null)
            {
                CELogger.Debug(LogTag, "StatsText not assigned - stats display disabled");
            }
            
            if (_spawnRateSlider == null)
            {
                CELogger.Debug(LogTag, "SpawnRateSlider not assigned - slider control disabled");
            }
            
            if (_maxBullets <= 0)
            {
                CELogger.Error(LogTag, $"Invalid MaxBullets value: {_maxBullets}. Must be > 0");
            }
            
            if (!hasWarnings)
            {
                CELogger.Debug(LogTag, "Configuration validation passed - no issues detected");
            }
        }
        
        private void InitializeECS()
        {
            CELogger.Debug(LogTag, "Initializing ECS world...");
            
            // Create the ECS world
            _world = new CEWorld(_maxBullets);
            CELogger.Debug(LogTag, $"CEWorld created with capacity {_maxBullets}");
            
            // Allocate component arrays
            _positions = new Vector3[_maxBullets];
            _velocities = new Vector3[_maxBullets];
            _lifetimes = new float[_maxBullets];
            _active = new bool[_maxBullets];
            _bounceCount = new int[_maxBullets];
            
            // Register components with the world
            _world.RegisterComponent(SLOT_POSITION, _positions);
            _world.RegisterComponent(SLOT_VELOCITY, _velocities);
            _world.RegisterComponent(SLOT_LIFETIME, _lifetimes);
            _world.RegisterComponent(SLOT_ACTIVE, _active);
            _world.RegisterComponent(SLOT_BOUNCE, _bounceCount);
            CELogger.Debug(LogTag, $"Registered 5 component arrays (Position, Velocity, Lifetime, Active, Bounce)");
            
            // Entity-to-transform mapping
            _entityToTransform = new int[_maxBullets];
            for (int i = 0; i < _maxBullets; i++)
            {
                _entityToTransform[i] = -1;
            }
            
            CELogger.Debug(LogTag, "ECS initialization complete");
        }
        
        private void InitializePool()
        {
            CELogger.Debug(LogTag, "Initializing transform pool...");
            
            _bulletTransforms = new Transform[_maxBullets];
            _freeTransformStack = new int[_maxBullets];
            _freeTransformCount = _maxBullets;
            
            if (_bulletPrefab == null)
            {
                CELogger.Error(LogTag, "Bullet prefab is required! Assign a prefab in the inspector.");
                return;
            }
            
            int successfulInstantiations = 0;
            
            // Pre-instantiate all bullet visuals
            for (int i = 0; i < _maxBullets; i++)
            {
                GameObject bullet = Instantiate(_bulletPrefab, _poolParent);
                bullet.transform.localScale = Vector3.one * _bulletScale;
                bullet.name = $"Bullet_{i}";
                
                bullet.SetActive(false);
                _bulletTransforms[i] = bullet.transform;
                successfulInstantiations++;
                
                // Initialize free stack (all transforms available)
                _freeTransformStack[i] = i;
            }
            
            CELogger.Debug(LogTag, $"Pool initialized: {successfulInstantiations}/{_maxBullets} transforms instantiated");
        }
        
        void OnEnable()
        {
            CELogger.Debug(LogTag, $"Station enabled - resuming bullet simulation (active entities: {(_world != null ? _world.ActiveEntityCount : 0)})");
        }
        
        void OnDisable()
        {
            CELogger.Debug(LogTag, $"Station disabled - pausing bullet simulation (active entities: {(_world != null ? _world.ActiveEntityCount : 0)})");
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
            UpdateBounceAndBoundsSystem(); // Check bounds and handle bouncing
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
            // Check if we can spawn - with threshold logging
            if (_freeTransformCount <= 0)
            {
                if (!_wasPoolExhausted)
                {
                    CELogger.Warning(LogTag, "Transform pool exhausted - cannot spawn more bullets until some are released");
                    _wasPoolExhausted = true;
                }
                return;
            }
            else if (_wasPoolExhausted)
            {
                CELogger.Debug(LogTag, $"Transform pool recovered - {_freeTransformCount} slots available");
                _wasPoolExhausted = false;
            }
            
            if (_world.ActiveEntityCount >= _maxBullets)
            {
                if (!_wasEntityLimitReached)
                {
                    CELogger.Warning(LogTag, $"Entity limit reached ({_maxBullets}) - cannot spawn more bullets");
                    _wasEntityLimitReached = true;
                }
                return;
            }
            else if (_wasEntityLimitReached)
            {
                CELogger.Debug(LogTag, $"Entity limit recovered - active count: {_world.ActiveEntityCount}");
                _wasEntityLimitReached = false;
            }
            
            // Create entity
            int entityId = _world.CreateEntity();
            if (entityId == CEWorld.InvalidEntity)
            {
                CELogger.Error(LogTag, "Failed to create entity - CEWorld returned InvalidEntity");
                return;
            }
            
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
            _bounceCount[entityId] = 0; // Fresh bullet hasn't bounced yet
            
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
            if (_spawnPoints != null && _spawnPoints.Length > 0)
            {
                // Check for valid spawn points
                int index = _createdThisFrame % _spawnPoints.Length;
                if (_spawnPoints[index] != null)
                {
                    return _spawnPoints[index].position;
                }
            }
            
            // Fallback: spawn at center of station bounds for maximum travel distance
            return _stationCenter;
        }
        
        private Vector3 GetBulletDirection()
        {
            switch (_patternMode)
            {
                case 0: // Spiral - 3D helix pattern
                    _spiralAngle += 15f;
                    float rad = _spiralAngle * Mathf.Deg2Rad;
                    float ySpiral = Mathf.Sin(_spiralAngle * 0.1f * Mathf.Deg2Rad); // Slower vertical oscillation
                    return new Vector3(Mathf.Sin(rad), ySpiral, Mathf.Cos(rad)).normalized;
                    
                case 1: // Wave - 3D wave pattern
                    float waveXZ = Mathf.Sin(Time.time * 5f + _createdThisFrame * 0.1f);
                    float waveY = Mathf.Cos(Time.time * 3f + _createdThisFrame * 0.15f) * 0.7f;
                    return new Vector3(waveXZ * 0.5f, waveY, 1).normalized;
                    
                case 2: // Random - full omnidirectional
                default:
                    float xDir = Random.Range(-1f, 1f);
                    float yDir = Random.Range(-1f, 1f);
                    float zDir = Random.Range(-1f, 1f);
                    return new Vector3(xDir, yDir, zDir).normalized;
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
        /// Bounce and bounds system - handles bouncing off station boundaries.
        /// Bullets bounce once, then despawn when leaving bounds again.
        /// </summary>
        private void UpdateBounceAndBoundsSystem()
        {
            int maxEntities = _world.MaxEntities;
            
            Vector3 minBounds = _stationCenter - _stationHalfExtents;
            Vector3 maxBounds = _stationCenter + _stationHalfExtents;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_active[i]) continue;
                
                Vector3 pos = _positions[i];
                Vector3 vel = _velocities[i];
                bool isOutOfBounds = false;
                bool didBounce = false;
                
                // Check X bounds
                if (pos.x < minBounds.x || pos.x > maxBounds.x)
                {
                    isOutOfBounds = true;
                    if (_bounceCount[i] < 1)
                    {
                        // Bounce: reflect velocity and clamp position
                        vel.x = -vel.x;
                        pos.x = Mathf.Clamp(pos.x, minBounds.x, maxBounds.x);
                        didBounce = true;
                    }
                }
                
                // Check Y bounds
                if (pos.y < minBounds.y || pos.y > maxBounds.y)
                {
                    isOutOfBounds = true;
                    if (_bounceCount[i] < 1)
                    {
                        vel.y = -vel.y;
                        pos.y = Mathf.Clamp(pos.y, minBounds.y, maxBounds.y);
                        didBounce = true;
                    }
                }
                
                // Check Z bounds
                if (pos.z < minBounds.z || pos.z > maxBounds.z)
                {
                    isOutOfBounds = true;
                    if (_bounceCount[i] < 1)
                    {
                        vel.z = -vel.z;
                        pos.z = Mathf.Clamp(pos.z, minBounds.z, maxBounds.z);
                        didBounce = true;
                    }
                }
                
                if (didBounce)
                {
                    _bounceCount[i]++;
                    _positions[i] = pos;
                    _velocities[i] = vel;
                }
                else if (isOutOfBounds && _bounceCount[i] >= 1)
                {
                    // Already bounced once, now leaving bounds - despawn
                    _world.DestroyEntityDeferred(i);
                    _active[i] = false;
                }
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
                float oldRate = _spawnRate;
                _spawnRate = _spawnRateSlider.value;
                CELogger.Debug(LogTag, $"Spawn rate changed via slider: {oldRate:F0} -> {_spawnRate:F0}/sec");
            }
        }
        
        public void SetPatternSpiral()
        {
            int oldPattern = _patternMode;
            _patternMode = 0;
            CELogger.Info(LogTag, $"Pattern changed: {GetPatternName(oldPattern)} -> {GetPatternName(_patternMode)}");
        }
        
        public void SetPatternWave()
        {
            int oldPattern = _patternMode;
            _patternMode = 1;
            CELogger.Info(LogTag, $"Pattern changed: {GetPatternName(oldPattern)} -> {GetPatternName(_patternMode)}");
        }
        
        public void SetPatternRandom()
        {
            int oldPattern = _patternMode;
            _patternMode = 2;
            CELogger.Info(LogTag, $"Pattern changed: {GetPatternName(oldPattern)} -> {GetPatternName(_patternMode)}");
        }
        
        public void IncreaseSpawnRate()
        {
            float oldRate = _spawnRate;
            _spawnRate = Mathf.Min(_spawnRate + 100, 2000);
            
            if (_spawnRate >= 2000)
            {
                CELogger.Info(LogTag, $"Spawn rate at maximum: {_spawnRate}/sec");
            }
            else
            {
                CELogger.Info(LogTag, $"Spawn rate increased: {oldRate:F0} -> {_spawnRate}/sec");
            }
        }
        
        public void DecreaseSpawnRate()
        {
            float oldRate = _spawnRate;
            _spawnRate = Mathf.Max(_spawnRate - 100, 50);
            
            if (_spawnRate <= 50)
            {
                CELogger.Info(LogTag, $"Spawn rate at minimum: {_spawnRate}/sec");
            }
            else
            {
                CELogger.Info(LogTag, $"Spawn rate decreased: {oldRate:F0} -> {_spawnRate}/sec");
            }
        }
        
        public void ClearAllBullets()
        {
            int activeBeforeClear = _world.ActiveEntityCount;
            CELogger.Debug(LogTag, $"Clearing all bullets (active count: {activeBeforeClear})...");
            
            // Deactivate all active bullets
            int markedForDestroy = 0;
            for (int i = 0; i < _maxBullets; i++)
            {
                if (_active[i])
                {
                    _world.DestroyEntityDeferred(i);
                    _active[i] = false;
                    _bounceCount[i] = 0;
                    markedForDestroy++;
                }
            }
            
            ProcessDestroyQueue();
            
            // Reset threshold tracking
            _wasPoolExhausted = false;
            _wasEntityLimitReached = false;
            
            CELogger.Info(LogTag, $"All bullets cleared: {markedForDestroy} entities destroyed, pool fully available ({_freeTransformCount} slots)");
        }
        
        #region Public API
        
        // Public API for performance monitoring
        public int GetActiveEntityCount() => _world.ActiveEntityCount;
        public int GetMaxEntities() => _maxBullets;
        public float GetPoolUtilization() => (_maxBullets - _freeTransformCount) / (float)_maxBullets;
        
        #endregion
    }
}

