using UdonSharp;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Perf;
using UnityEngine;
using VRC.SDKBase;

namespace CEShowcase.Station2_Flocking
{
    /// <summary>
    /// Station 2: Crowd Simulation - Demonstrates CEWorld + CEGrid spatial partitioning
    /// for efficient flocking behavior with 500+ agents.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class FlockingDemo : UdonSharpBehaviour
    {
        [Header("Agent Settings")]
        [SerializeField] private GameObject _agentPrefab;
        [SerializeField] private Transform _agentParent;
        [SerializeField] private int _maxAgents = 500;
        [SerializeField] private int _initialAgents = 200;
        
        [Header("Arena Bounds")]
        [SerializeField] private Vector3 _arenaMin = new Vector3(-25, 0, -25);
        [SerializeField] private Vector3 _arenaMax = new Vector3(25, 5, 25);
        [SerializeField] private float _cellSize = 5f;
        
        [Header("Flocking Behavior")]
        [SerializeField] private float _separationWeight = 1.5f;
        [SerializeField] private float _alignmentWeight = 1.0f;
        [SerializeField] private float _cohesionWeight = 1.0f;
        [SerializeField] private float _neighborRadius = 5f;
        [SerializeField] private float _separationRadius = 2f;
        [SerializeField] private float _maxSpeed = 5f;
        [SerializeField] private float _maxForce = 10f;
        
        [Header("Player Interaction")]
        [SerializeField] private float _fleeRadius = 8f;
        [SerializeField] private float _fleeStrength = 3f;
        
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _statsText;
        
        [Header("Agent Colors")]
        [SerializeField] private Color _idleColor = Color.blue;
        [SerializeField] private Color _flockingColor = Color.green;
        [SerializeField] private Color _fleeingColor = Color.red;
        
        // ECS World
        private CEWorld _world;
        private CEGrid _spatialGrid;
        
        // Component Arrays
        private Vector3[] _positions;
        private Vector3[] _velocities;
        private int[] _states; // 0=idle, 1=flocking, 2=fleeing
        private Vector3[] _targets;
        
        // Visual pool
        private Transform[] _agentTransforms;
        private Renderer[] _agentRenderers;
        private int[] _entityToAgent;
        
        // Query buffers
        private int[] _nearbyBuffer;
        
        // Stats
        private int _activeAgents;
        private int _neighborQueriesThisFrame;
        private float _avgQueryTime;
        
        // Component slots
        private const int SLOT_POSITION = 0;
        private const int SLOT_VELOCITY = 1;
        private const int SLOT_STATE = 2;
        private const int SLOT_TARGET = 3;
        
        // Agent states
        private const int STATE_IDLE = 0;
        private const int STATE_FLOCKING = 1;
        private const int STATE_FLEEING = 2;
        
        void Start()
        {
            InitializeECS();
            InitializeGrid();
            InitializeAgents();
            SpawnInitialAgents();
            
            CELogger.Info("Flocking", $"Flocking Demo initialized with {_initialAgents} agents");
        }
        
        private void InitializeECS()
        {
            _world = new CEWorld(_maxAgents);
            
            _positions = new Vector3[_maxAgents];
            _velocities = new Vector3[_maxAgents];
            _states = new int[_maxAgents];
            _targets = new Vector3[_maxAgents];
            
            _world.RegisterComponent(SLOT_POSITION, _positions);
            _world.RegisterComponent(SLOT_VELOCITY, _velocities);
            _world.RegisterComponent(SLOT_STATE, _states);
            _world.RegisterComponent(SLOT_TARGET, _targets);
            
            _entityToAgent = new int[_maxAgents];
            for (int i = 0; i < _maxAgents; i++)
            {
                _entityToAgent[i] = -1;
            }
        }
        
        private void InitializeGrid()
        {
            _spatialGrid = new CEGrid(_arenaMin, _arenaMax, _cellSize, 32);
            _nearbyBuffer = new int[64];
            
            CELogger.Debug("Flocking", $"Spatial grid: {_spatialGrid.Dimensions} cells, {_cellSize}m cell size");
        }
        
        private void InitializeAgents()
        {
            _agentTransforms = new Transform[_maxAgents];
            _agentRenderers = new Renderer[_maxAgents];
            
            for (int i = 0; i < _maxAgents; i++)
            {
                if (_agentPrefab != null)
                {
                    GameObject agent = Instantiate(_agentPrefab, _agentParent);
                    agent.SetActive(false);
                    _agentTransforms[i] = agent.transform;
                    _agentRenderers[i] = agent.GetComponentInChildren<Renderer>();
                }
            }
        }
        
        private void SpawnInitialAgents()
        {
            for (int i = 0; i < _initialAgents; i++)
            {
                SpawnAgent();
            }
        }
        
        public void SpawnAgent()
        {
            if (_world.ActiveEntityCount >= _maxAgents) return;
            
            int entityId = _world.CreateEntity();
            if (entityId == CEWorld.InvalidEntity) return;
            
            // Find free agent slot
            int agentIndex = -1;
            for (int i = 0; i < _maxAgents; i++)
            {
                if (_agentTransforms[i] != null && !_agentTransforms[i].gameObject.activeSelf)
                {
                    agentIndex = i;
                    break;
                }
            }
            
            if (agentIndex < 0) return;
            
            _entityToAgent[entityId] = agentIndex;
            
            // Random position in arena
            Vector3 pos = new Vector3(
                Random.Range(_arenaMin.x + 2, _arenaMax.x - 2),
                Random.Range(_arenaMin.y, _arenaMax.y),
                Random.Range(_arenaMin.z + 2, _arenaMax.z - 2)
            );
            
            // Random initial velocity
            Vector3 vel = Random.insideUnitSphere * _maxSpeed * 0.5f;
            vel.y = 0;
            
            _positions[entityId] = pos;
            _velocities[entityId] = vel;
            _states[entityId] = STATE_IDLE;
            _targets[entityId] = pos + vel.normalized * 10f;
            
            // Activate visual
            Transform t = _agentTransforms[agentIndex];
            if (t != null)
            {
                t.position = pos;
                t.gameObject.SetActive(true);
            }
            
            _activeAgents++;
        }
        
        void Update()
        {
            float dt = Time.deltaTime;
            
            _neighborQueriesThisFrame = 0;
            float queryStartTime = Time.realtimeSinceStartup;
            
            // Rebuild spatial grid
            RebuildGrid();
            
            // Get player position for flee behavior
            Vector3 playerPos = GetLocalPlayerPosition();
            
            // Update all agents
            UpdateFlockingSystem(dt, playerPos);
            UpdateMovementSystem(dt);
            UpdateBoundsSystem();
            
            // Sync visuals
            SyncTransforms();
            
            // Calculate query time
            float queryEndTime = Time.realtimeSinceStartup;
            _avgQueryTime = (queryEndTime - queryStartTime) * 1000f / Mathf.Max(1, _neighborQueriesThisFrame);
            
            UpdateStats();
        }
        
        private Vector3 GetLocalPlayerPosition()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player != null && player.IsValid())
            {
                return player.GetPosition();
            }
            return Vector3.zero;
        }
        
        private void RebuildGrid()
        {
            _spatialGrid.Clear();
            
            int maxEntities = _world.MaxEntities;
            for (int i = 0; i < maxEntities; i++)
            {
                if (_world.IsValidEntity(i))
                {
                    _spatialGrid.Insert(i, _positions[i]);
                }
            }
        }
        
        private void UpdateFlockingSystem(float dt, Vector3 playerPos)
        {
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_world.IsValidEntity(i)) continue;
                
                Vector3 pos = _positions[i];
                Vector3 vel = _velocities[i];
                
                // Query neighbors using spatial grid
                int neighborCount = _spatialGrid.QueryRadius(pos, _neighborRadius, _nearbyBuffer);
                _neighborQueriesThisFrame++;
                
                // Calculate flocking forces
                Vector3 separation = Vector3.zero;
                Vector3 alignment = Vector3.zero;
                Vector3 cohesion = Vector3.zero;
                int flockCount = 0;
                
                for (int n = 0; n < neighborCount; n++)
                {
                    int neighborId = _nearbyBuffer[n];
                    if (neighborId == i) continue; // Skip self
                    if (!_world.IsValidEntity(neighborId)) continue;
                    
                    Vector3 neighborPos = _positions[neighborId];
                    Vector3 diff = pos - neighborPos;
                    float dist = diff.magnitude;
                    
                    // Separation: steer away from nearby neighbors
                    if (dist < _separationRadius && dist > 0.001f)
                    {
                        separation += diff.normalized / dist;
                    }
                    
                    // Alignment & Cohesion
                    if (dist < _neighborRadius)
                    {
                        alignment += _velocities[neighborId];
                        cohesion += neighborPos;
                        flockCount++;
                    }
                }
                
                // Calculate steering forces
                Vector3 steer = Vector3.zero;
                
                if (separation.sqrMagnitude > 0)
                {
                    steer += separation.normalized * _separationWeight;
                }
                
                if (flockCount > 0)
                {
                    alignment /= flockCount;
                    if (alignment.sqrMagnitude > 0)
                    {
                        steer += (alignment.normalized * _maxSpeed - vel).normalized * _alignmentWeight;
                    }
                    
                    cohesion /= flockCount;
                    Vector3 cohesionDir = (cohesion - pos).normalized;
                    steer += cohesionDir * _cohesionWeight;
                }
                
                // Player flee behavior
                float playerDist = Vector3.Distance(pos, playerPos);
                int newState = STATE_IDLE;
                
                if (playerDist < _fleeRadius)
                {
                    Vector3 fleeDir = (pos - playerPos).normalized;
                    steer += fleeDir * _fleeStrength * (1f - playerDist / _fleeRadius);
                    newState = STATE_FLEEING;
                }
                else if (flockCount > 2)
                {
                    newState = STATE_FLOCKING;
                }
                
                _states[i] = newState;
                
                // Apply steering
                if (steer.sqrMagnitude > 0)
                {
                    steer = Vector3.ClampMagnitude(steer, _maxForce);
                    _velocities[i] += steer * dt;
                    _velocities[i] = Vector3.ClampMagnitude(_velocities[i], _maxSpeed);
                }
            }
        }
        
        private void UpdateMovementSystem(float dt)
        {
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_world.IsValidEntity(i)) continue;
                
                _positions[i] += _velocities[i] * dt;
                
                // Keep Y level (ground plane movement)
                Vector3 pos = _positions[i];
                pos.y = Mathf.Clamp(pos.y, _arenaMin.y, _arenaMax.y);
                _positions[i] = pos;
            }
        }
        
        private void UpdateBoundsSystem()
        {
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_world.IsValidEntity(i)) continue;
                
                Vector3 pos = _positions[i];
                Vector3 vel = _velocities[i];
                
                // Soft boundary - steer back towards center
                if (pos.x < _arenaMin.x + 2 || pos.x > _arenaMax.x - 2)
                {
                    vel.x *= -0.5f;
                    pos.x = Mathf.Clamp(pos.x, _arenaMin.x, _arenaMax.x);
                }
                if (pos.z < _arenaMin.z + 2 || pos.z > _arenaMax.z - 2)
                {
                    vel.z *= -0.5f;
                    pos.z = Mathf.Clamp(pos.z, _arenaMin.z, _arenaMax.z);
                }
                
                _positions[i] = pos;
                _velocities[i] = vel;
            }
        }
        
        private void SyncTransforms()
        {
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_world.IsValidEntity(i)) continue;
                
                int agentIndex = _entityToAgent[i];
                if (agentIndex < 0) continue;
                
                Transform t = _agentTransforms[agentIndex];
                if (t == null) continue;
                
                t.position = _positions[i];
                
                // Face movement direction
                Vector3 vel = _velocities[i];
                if (vel.sqrMagnitude > 0.1f)
                {
                    t.rotation = Quaternion.LookRotation(vel.normalized, Vector3.up);
                }
                
                // Update color based on state
                Renderer r = _agentRenderers[agentIndex];
                if (r != null)
                {
                    Color c;
                    switch (_states[i])
                    {
                        case STATE_FLEEING: c = _fleeingColor; break;
                        case STATE_FLOCKING: c = _flockingColor; break;
                        default: c = _idleColor; break;
                    }
                    
                    MaterialPropertyBlock block = new MaterialPropertyBlock();
                    r.GetPropertyBlock(block);
                    block.SetColor("_Color", c);
                    r.SetPropertyBlock(block);
                }
            }
        }
        
        private void UpdateStats()
        {
            if (_statsText == null) return;
            
            int idleCount = 0, flockingCount = 0, fleeingCount = 0;
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities; i++)
            {
                if (!_world.IsValidEntity(i)) continue;
                
                switch (_states[i])
                {
                    case STATE_IDLE: idleCount++; break;
                    case STATE_FLOCKING: flockingCount++; break;
                    case STATE_FLEEING: fleeingCount++; break;
                }
            }
            
            _statsText.text = $"<b>FLOCKING METRICS</b>\n" +
                             $"Agents: <color=#00FF00>{_world.ActiveEntityCount}</color>\n" +
                             $"Queries/Frame: {_neighborQueriesThisFrame}\n" +
                             $"Avg Query: <color=#FFFF00>{_avgQueryTime:F3}ms</color>\n" +
                             $"Grid Cells: {_spatialGrid.TotalCells}\n" +
                             $"<color=#0088FF>Idle: {idleCount}</color> | <color=#00FF00>Flock: {flockingCount}</color> | <color=#FF0000>Flee: {fleeingCount}</color>";
        }
        
        // UI Callbacks
        public void AddAgents()
        {
            for (int i = 0; i < 50; i++)
            {
                SpawnAgent();
            }
            CELogger.Info("Flocking", $"Added 50 agents. Total: {_world.ActiveEntityCount}");
        }
        
        public void RemoveAgents()
        {
            int removed = 0;
            int maxEntities = _world.MaxEntities;
            
            for (int i = 0; i < maxEntities && removed < 50; i++)
            {
                if (_world.IsValidEntity(i))
                {
                    int agentIndex = _entityToAgent[i];
                    if (agentIndex >= 0 && _agentTransforms[agentIndex] != null)
                    {
                        _agentTransforms[agentIndex].gameObject.SetActive(false);
                    }
                    
                    _world.DestroyEntity(i);
                    _entityToAgent[i] = -1;
                    _activeAgents--;
                    removed++;
                }
            }
            
            CELogger.Info("Flocking", $"Removed {removed} agents. Total: {_world.ActiveEntityCount}");
        }
        
        public int GetActiveAgentCount() => _world.ActiveEntityCount;
        public int GetNeighborQueriesPerFrame() => _neighborQueriesThisFrame;
    }
}
