using UdonSharp;
using UdonSharp.CE.Procgen;
using UnityEngine;

namespace UdonSharp.CE.Samples.Procgen
{
    /// <summary>
    /// Example demonstrating CE.Procgen procedural generation features.
    ///
    /// This sample showcases:
    /// - CERandom: Deterministic random number generation
    /// - CENoise: Perlin, Simplex, Worley, and fractal noise
    /// - CEDungeon: Graph-based dungeon layout generation
    /// - WFCSolver: Wave Function Collapse for tile-based generation
    ///
    /// All generation is deterministic: the same seed produces identical
    /// results across all clients, enabling synchronized procedural content
    /// in multiplayer VRChat worlds.
    /// </summary>
    /// <remarks>
    /// Usage patterns:
    /// 1. Sync a seed value across clients (e.g., via synced variable)
    /// 2. Each client generates content locally using the shared seed
    /// 3. Results are identical without needing to sync the generated data
    ///
    /// This approach saves network bandwidth and enables large-scale
    /// procedural content that would be impossible to sync directly.
    /// </remarks>
    public class ProcgenExample : UdonSharpBehaviour
    {
        #region Serialized Fields

        [Header("Configuration")]
        [SerializeField] private int worldSeed = 12345;

        [Header("Terrain Generation")]
        [SerializeField] private MeshFilter terrainMeshFilter;
        [SerializeField] private int terrainWidth = 32;
        [SerializeField] private int terrainDepth = 32;
        [SerializeField] private float terrainHeight = 10f;
        [SerializeField] private float terrainScale = 0.1f;

        [Header("Dungeon Generation")]
        [SerializeField] private GameObject roomPrefab;
        [SerializeField] private GameObject corridorPrefab;
        [SerializeField] private Transform dungeonParent;
        [SerializeField] private float dungeonTileSize = 2f;

        [Header("WFC Tile Generation")]
        [SerializeField] private GameObject[] tilePrefabs;
        [SerializeField] private Transform wfcParent;
        [SerializeField] private int wfcGridWidth = 16;
        [SerializeField] private int wfcGridHeight = 16;
        [SerializeField] private float wfcTileSize = 1f;

        [Header("Debug")]
        [SerializeField] private bool showDebugLogs = true;

        #endregion

        #region Private State

        private CERandom _rng;
        private WFCSolver _wfcSolver;
        private bool _wfcInProgress;

        #endregion

        #region Unity Events

        private void Start()
        {
            // Initialize with world seed
            InitializeProcgen(worldSeed);
        }

        private void Update()
        {
            // Time-sliced WFC solving
            if (_wfcInProgress && _wfcSolver != null)
            {
                UpdateWFCSolver();
            }
        }

        #endregion

        #region Public API

        /// <summary>
        /// Initializes all procedural generation systems with a seed.
        /// Call this when the world seed is synced from the master.
        /// </summary>
        public void InitializeProcgen(int seed)
        {
            worldSeed = seed;
            _rng = new CERandom(seed);
            CENoise.Initialize(seed);

            Log($"Procgen initialized with seed: {seed}");
        }

        /// <summary>
        /// Regenerates all content with a new random seed.
        /// </summary>
        public void RegenerateWithNewSeed()
        {
            int newSeed = (int)(Time.time * 1000);
            InitializeProcgen(newSeed);
        }

        #endregion

        #region CERandom Examples

        /// <summary>
        /// Demonstrates CERandom deterministic random generation.
        /// </summary>
        public void DemonstrateCERandom()
        {
            var rng = new CERandom(42); // Same seed = same sequence

            Log("=== CERandom Demo ===");

            // Basic random values
            Log($"NextFloat: {rng.NextFloat():F4}");
            Log($"NextBool: {rng.NextBool()}");
            Log($"Range(1, 100): {rng.Range(1, 100)}");

            // Vector generation
            Vector3 randomDir = rng.OnUnitSphere();
            Log($"Random direction: {randomDir}");

            Vector2 insideCircle = rng.InsideUnitCircle();
            Log($"Inside unit circle: {insideCircle}");

            // Weighted random choice
            float[] weights = { 1f, 2f, 5f, 2f };
            int choice = rng.WeightedChoice(weights);
            Log($"Weighted choice (weights 1,2,5,2): index {choice}");

            // Deterministic shuffle
            int[] deck = { 1, 2, 3, 4, 5, 6, 7, 8 };
            rng.Shuffle(deck);
            Log($"Shuffled deck: {string.Join(", ", deck)}");

            // Color generation
            Color randomColor = rng.ColorHSV(0.5f, 1f, 0.7f, 1f);
            Log($"Random color: R={randomColor.r:F2} G={randomColor.g:F2} B={randomColor.b:F2}");

            // Demonstrate determinism: reset and repeat
            rng.Reset();
            float firstFloat = rng.NextFloat();
            Log($"After reset, NextFloat: {firstFloat:F4} (same as first call!)");
        }

        /// <summary>
        /// Spawns objects at random positions within bounds.
        /// </summary>
        public void SpawnRandomObjects(GameObject prefab, int count, Bounds bounds)
        {
            if (prefab == null) return;

            var rng = new CERandom(worldSeed + 1000); // Offset seed for variety

            for (int i = 0; i < count; i++)
            {
                Vector3 pos = rng.PointInBounds(bounds);
                Quaternion rot = rng.RotationUniform();

                Instantiate(prefab, pos, rot);
            }

            Log($"Spawned {count} objects with deterministic positions");
        }

        #endregion

        #region CENoise Examples

        /// <summary>
        /// Demonstrates various noise functions.
        /// </summary>
        public void DemonstrateCENoise()
        {
            Log("=== CENoise Demo ===");

            // Sample noise at a point
            float x = 5.5f, y = 3.2f;

            Log($"Perlin2D({x}, {y}): {CENoise.Perlin2D(x, y):F4}");
            Log($"Simplex2D({x}, {y}): {CENoise.Simplex2D(x, y):F4}");
            Log($"Worley2D({x}, {y}): {CENoise.Worley2D(x, y):F4}");

            // Fractal combinations
            Log($"Fractal2D (4 octaves): {CENoise.Fractal2D(x, y, 4, 0.5f):F4}");
            Log($"Ridged2D (4 octaves): {CENoise.Ridged2D(x, y, 4, 0.5f):F4}");
            Log($"Turbulence2D: {CENoise.Turbulence2D(x, y, 4, 0.5f):F4}");

            // 3D noise for volumetric effects
            float z = 1.0f;
            Log($"Perlin3D({x}, {y}, {z}): {CENoise.Perlin3D(x, y, z):F4}");
        }

        /// <summary>
        /// Generates a terrain mesh using fractal noise.
        /// </summary>
        public void GenerateTerrainMesh()
        {
            if (terrainMeshFilter == null)
            {
                Log("No terrain mesh filter assigned");
                return;
            }

            int width = terrainWidth;
            int depth = terrainDepth;

            // Generate vertices using noise
            Vector3[] vertices = new Vector3[(width + 1) * (depth + 1)];
            Vector2[] uv = new Vector2[(width + 1) * (depth + 1)];

            for (int z = 0; z <= depth; z++)
            {
                for (int x = 0; x <= width; x++)
                {
                    int index = z * (width + 1) + x;

                    // Use fractal noise for natural terrain
                    float noiseX = x * terrainScale;
                    float noiseZ = z * terrainScale;
                    float height = CENoise.Fractal2D(noiseX, noiseZ, 4, 0.5f);

                    // Remap from [-1,1] to [0,1]
                    height = (height + 1f) * 0.5f;

                    vertices[index] = new Vector3(x, height * terrainHeight, z);
                    uv[index] = new Vector2((float)x / width, (float)z / depth);
                }
            }

            // Generate triangles
            int[] triangles = new int[width * depth * 6];
            int triIndex = 0;

            for (int z = 0; z < depth; z++)
            {
                for (int x = 0; x < width; x++)
                {
                    int vertIndex = z * (width + 1) + x;

                    // Two triangles per quad
                    triangles[triIndex++] = vertIndex;
                    triangles[triIndex++] = vertIndex + width + 1;
                    triangles[triIndex++] = vertIndex + 1;

                    triangles[triIndex++] = vertIndex + 1;
                    triangles[triIndex++] = vertIndex + width + 1;
                    triangles[triIndex++] = vertIndex + width + 2;
                }
            }

            // Create and assign mesh
            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.uv = uv;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            terrainMeshFilter.mesh = mesh;

            Log($"Generated terrain mesh: {width}x{depth} ({vertices.Length} vertices)");
        }

        /// <summary>
        /// Samples noise to determine biome type at a position.
        /// </summary>
        public string GetBiomeAt(float x, float z)
        {
            // Use different noise layers for moisture and temperature
            float moisture = CENoise.Fractal2D(x * 0.05f, z * 0.05f, 3, 0.5f);
            float temperature = CENoise.Fractal2D(x * 0.05f + 1000f, z * 0.05f + 1000f, 3, 0.5f);

            // Remap to 0-1
            moisture = (moisture + 1f) * 0.5f;
            temperature = (temperature + 1f) * 0.5f;

            // Simple biome classification
            if (temperature < 0.3f)
                return moisture > 0.5f ? "Tundra" : "Snow";
            if (temperature > 0.7f)
                return moisture > 0.5f ? "Rainforest" : "Desert";
            return moisture > 0.6f ? "Forest" : (moisture > 0.3f ? "Plains" : "Savanna");
        }

        #endregion

        #region CEDungeon Examples

        /// <summary>
        /// Generates and visualizes a dungeon layout.
        /// </summary>
        public void GenerateDungeon()
        {
            var rng = new CERandom(worldSeed + 2000);

            // Configure dungeon generation
            var config = new DungeonConfig
            {
                RoomCount = 12,
                MinRoomSize = new Vector2Int(4, 4),
                MaxRoomSize = new Vector2Int(10, 10),
                CorridorWidth = 2,
                Connectivity = 0.3f,
                TreasureRoomChance = 0.15f,
                SecretRoomChance = 0.1f,
                RoomSpacing = 3
            };

            // Generate layout
            DungeonLayout layout = CEDungeon.Generate(rng, config);

            if (layout == null)
            {
                Log("Dungeon generation failed");
                return;
            }

            Log($"Generated dungeon: {layout.Rooms.Length} rooms, {layout.Corridors.Length} corridors");

            // Visualize rooms
            SpawnDungeonRooms(layout);

            // Visualize corridors
            SpawnDungeonCorridors(layout);

            // Log room types
            foreach (var room in layout.Rooms)
            {
                string pathInfo = room.IsOnCriticalPath ? " [CRITICAL PATH]" : "";
                Log($"Room {room.Id}: {room.Type} at ({room.Position.x}, {room.Position.y}){pathInfo}");
            }
        }

        /// <summary>
        /// Quick dungeon generation with minimal parameters.
        /// </summary>
        public void GenerateSimpleDungeon()
        {
            var rng = new CERandom(worldSeed + 3000);

            // Use simplified overload
            DungeonLayout layout = CEDungeon.Generate(
                rng,
                roomCount: 8,
                minRoomSize: new Vector2Int(3, 3),
                maxRoomSize: new Vector2Int(7, 7),
                connectivity: 0.25f
            );

            Log($"Generated simple dungeon: {layout?.Rooms?.Length ?? 0} rooms");
        }

        private void SpawnDungeonRooms(DungeonLayout layout)
        {
            if (roomPrefab == null || dungeonParent == null) return;

            foreach (var room in layout.Rooms)
            {
                Vector3 worldPos = new Vector3(
                    room.Position.x * dungeonTileSize,
                    0f,
                    room.Position.y * dungeonTileSize
                );

                Vector3 scale = new Vector3(
                    room.Size.x * dungeonTileSize,
                    1f,
                    room.Size.y * dungeonTileSize
                );

                GameObject go = Instantiate(roomPrefab, worldPos, Quaternion.identity, dungeonParent);
                go.transform.localScale = scale;
                go.name = $"Room_{room.Id}_{room.Type}";

                // Color-code by room type
                var renderer = go.GetComponent<Renderer>();
                if (renderer != null)
                {
                    Color roomColor = GetRoomColor(room.Type);
                    var block = new MaterialPropertyBlock();
                    block.SetColor("_Color", roomColor);
                    renderer.SetPropertyBlock(block);
                }
            }
        }

        private void SpawnDungeonCorridors(DungeonLayout layout)
        {
            if (corridorPrefab == null || dungeonParent == null) return;

            foreach (var corridor in layout.Corridors)
            {
                foreach (var point in corridor.Path)
                {
                    Vector3 worldPos = new Vector3(
                        point.x * dungeonTileSize,
                        -0.1f, // Slightly below rooms
                        point.y * dungeonTileSize
                    );

                    Vector3 scale = new Vector3(
                        corridor.Width * dungeonTileSize,
                        0.5f,
                        corridor.Width * dungeonTileSize
                    );

                    GameObject go = Instantiate(corridorPrefab, worldPos, Quaternion.identity, dungeonParent);
                    go.transform.localScale = scale;
                }
            }
        }

        private Color GetRoomColor(RoomType type)
        {
            switch (type)
            {
                case RoomType.Start: return Color.green;
                case RoomType.Boss: return Color.red;
                case RoomType.Treasure: return Color.yellow;
                case RoomType.Secret: return new Color(0.5f, 0f, 0.5f); // Purple
                default: return Color.gray;
            }
        }

        #endregion

        #region WFCSolver Examples

        /// <summary>
        /// Starts Wave Function Collapse tile generation.
        /// This runs over multiple frames to avoid hitching.
        /// </summary>
        public void StartWFCGeneration()
        {
            // Define tile types with adjacency constraints
            var tiles = CreateWFCTileSet();

            var config = new WFCConfig
            {
                Tiles = tiles,
                Width = wfcGridWidth,
                Height = wfcGridHeight,
                MaxIterationsPerFrame = 50, // Tune for frame budget
                WrapEdges = false
            };

            _wfcSolver = new WFCSolver(config, worldSeed + 4000);
            _wfcInProgress = true;

            Log($"Started WFC generation: {wfcGridWidth}x{wfcGridHeight} grid");
        }

        /// <summary>
        /// Creates a simple tile set for WFC (grass, water, sand transitions).
        /// </summary>
        private WFCTile[] CreateWFCTileSet()
        {
            // Tile IDs
            const int GRASS = 0;
            const int WATER = 1;
            const int SAND = 2;

            return new WFCTile[]
            {
                // Grass: can connect to grass and sand
                new WFCTile
                {
                    Id = GRASS,
                    Name = "Grass",
                    Weight = 3f, // More common
                    ValidNorth = new[] { GRASS, SAND },
                    ValidSouth = new[] { GRASS, SAND },
                    ValidEast = new[] { GRASS, SAND },
                    ValidWest = new[] { GRASS, SAND }
                },

                // Water: can only connect to water and sand
                new WFCTile
                {
                    Id = WATER,
                    Name = "Water",
                    Weight = 1f,
                    ValidNorth = new[] { WATER, SAND },
                    ValidSouth = new[] { WATER, SAND },
                    ValidEast = new[] { WATER, SAND },
                    ValidWest = new[] { WATER, SAND }
                },

                // Sand: transition tile, connects to everything
                new WFCTile
                {
                    Id = SAND,
                    Name = "Sand",
                    Weight = 0.5f, // Less common, only at transitions
                    ValidNorth = new[] { GRASS, WATER, SAND },
                    ValidSouth = new[] { GRASS, WATER, SAND },
                    ValidEast = new[] { GRASS, WATER, SAND },
                    ValidWest = new[] { GRASS, WATER, SAND }
                }
            };
        }

        private void UpdateWFCSolver()
        {
            if (_wfcSolver == null) return;

            WFCStatus status = _wfcSolver.Tick();

            switch (status)
            {
                case WFCStatus.InProgress:
                    // Still working, show progress
                    if (showDebugLogs && Time.frameCount % 30 == 0)
                    {
                        Log($"WFC progress: {_wfcSolver.Progress:P0}");
                    }
                    break;

                case WFCStatus.Completed:
                    Log("WFC generation completed!");
                    SpawnWFCTiles();
                    _wfcInProgress = false;
                    break;

                case WFCStatus.Failed:
                    Log("WFC generation failed (contradiction)");
                    _wfcInProgress = false;
                    break;

                case WFCStatus.Aborted:
                    Log("WFC generation aborted");
                    _wfcInProgress = false;
                    break;
            }
        }

        private void SpawnWFCTiles()
        {
            if (tilePrefabs == null || tilePrefabs.Length == 0 || wfcParent == null)
            {
                Log("WFC tile spawning skipped: no prefabs assigned");
                return;
            }

            int[,] result = _wfcSolver.GetResult();

            for (int x = 0; x < wfcGridWidth; x++)
            {
                for (int y = 0; y < wfcGridHeight; y++)
                {
                    int tileId = result[x, y];
                    if (tileId < 0 || tileId >= tilePrefabs.Length) continue;

                    GameObject prefab = tilePrefabs[tileId];
                    if (prefab == null) continue;

                    Vector3 pos = new Vector3(x * wfcTileSize, 0f, y * wfcTileSize);
                    Instantiate(prefab, pos, Quaternion.identity, wfcParent);
                }
            }

            Log($"Spawned {wfcGridWidth * wfcGridHeight} WFC tiles");
        }

        /// <summary>
        /// Synchronously solves WFC (for small grids or non-critical use).
        /// </summary>
        public void SolveWFCSync()
        {
            var tiles = CreateWFCTileSet();

            var config = new WFCConfig
            {
                Tiles = tiles,
                Width = 8, // Small grid for sync solve
                Height = 8
            };

            var solver = new WFCSolver(config, worldSeed);
            WFCStatus status = solver.Solve();

            Log($"Sync WFC result: {status}");

            if (status == WFCStatus.Completed)
            {
                int[,] result = solver.GetResult();
                LogWFCResult(result, 8, 8);
            }
        }

        private void LogWFCResult(int[,] result, int width, int height)
        {
            string[] symbols = { ".", "~", ":" }; // Grass, Water, Sand

            string output = "WFC Result:\n";
            for (int y = height - 1; y >= 0; y--)
            {
                for (int x = 0; x < width; x++)
                {
                    int tile = result[x, y];
                    output += tile >= 0 && tile < symbols.Length ? symbols[tile] : "?";
                }
                output += "\n";
            }
            Log(output);
        }

        #endregion

        #region Combined Examples

        /// <summary>
        /// Demonstrates combining multiple procgen systems.
        /// </summary>
        public void GenerateCompleteWorld()
        {
            Log("=== Generating Complete World ===");

            // Step 1: Generate base terrain with noise
            GenerateTerrainMesh();

            // Step 2: Place a dungeon in the terrain
            GenerateDungeon();

            // Step 3: Use WFC for detailed tile areas
            StartWFCGeneration();

            // Step 4: Scatter objects using deterministic random
            var rng = new CERandom(worldSeed + 5000);

            // Place trees based on noise density
            for (int x = 0; x < terrainWidth; x += 4)
            {
                for (int z = 0; z < terrainDepth; z += 4)
                {
                    float density = CENoise.Fractal2D(x * 0.1f, z * 0.1f, 2, 0.5f);
                    density = (density + 1f) * 0.5f; // 0 to 1

                    if (rng.Chance(density * 0.3f))
                    {
                        // Would spawn tree here
                        // SpawnTree(x, z);
                    }
                }
            }

            Log("World generation complete!");
        }

        #endregion

        #region Utilities

        private void Log(string message)
        {
            if (showDebugLogs)
            {
                Debug.Log($"[ProcgenExample] {message}");
            }
        }

        #endregion
    }
}

