using UdonSharp;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Procgen;
using UnityEngine;

namespace CEShowcase.Station6_Procgen
{
    /// <summary>
    /// Station 6: Procedural Generation - Demonstrates CERandom and CENoise for deterministic generation.
    /// 
    /// This showcases:
    /// - CERandom for deterministic random sequences (same seed = same results everywhere)
    /// - CENoise for coherent noise functions (Perlin, Simplex, Worley, Fractal)
    /// - Synchronized procedural content across all clients
    /// - Terrain heightmap generation
    /// - Weighted random loot tables
    /// - Deterministic dungeon/pattern generation
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ProcgenDemo : UdonSharpBehaviour
    {
        [Header("Seed Settings")]
        [SerializeField] private int _worldSeed = 12345;
        
        [Header("Terrain Visualization")]
        [SerializeField] private Transform _terrainParent;
        [SerializeField] private GameObject _terrainBlockPrefab;
        [SerializeField] private int _terrainWidth = 16;
        [SerializeField] private int _terrainDepth = 16;
        [SerializeField] private float _terrainScale = 0.5f;
        [SerializeField] private float _heightScale = 5f;
        [SerializeField] private float _noiseScale = 0.1f;
        
        [Header("Visual Fallback (when no prefab assigned)")]
        [SerializeField] private Material _fallbackMaterial;
        
        [Header("Noise Visualization")]
        [SerializeField] private Renderer _noiseDisplayRenderer;
        [SerializeField] private int _noiseTextureSize = 64;
        
        [Header("Loot Table")]
        [SerializeField] private string[] _lootItems;
        [SerializeField] private float[] _lootWeights;
        
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _statsText;
        [SerializeField] private TMPro.TextMeshProUGUI _seedText;
        [SerializeField] private TMPro.TextMeshProUGUI _lootResultText;
        
        // State
        private CERandom _rng;
        private Transform[] _terrainBlocks;
        private Texture2D _noiseTexture;
        private Color[] _noisePixels;
        
        // Demo state
        private int _noiseMode = 0; // 0=Perlin, 1=Simplex, 2=Worley, 3=Fractal, 4=Ridged
        private int _lootRollCount;
        private int[] _lootDistribution;
        
        // Stats
        private int _generationCount;
        private float _lastGenerationTime;
        
        void Start()
        {
            Initialize();
            GenerateAll();
            
            CELogger.Info("Procgen", $"Procedural Generation Demo initialized with seed {_worldSeed}");
        }
        
        private void Initialize()
        {
            // Initialize deterministic RNG
            _rng = new CERandom(_worldSeed);
            
            // Initialize noise system
            CENoise.Initialize(_worldSeed);
            
            // Initialize terrain blocks array
            int blockCount = _terrainWidth * _terrainDepth;
            _terrainBlocks = new Transform[blockCount];
            
            // Initialize noise texture
            _noiseTexture = new Texture2D(_noiseTextureSize, _noiseTextureSize, TextureFormat.RGB24, false);
            _noiseTexture.filterMode = FilterMode.Point;
            _noisePixels = new Color[_noiseTextureSize * _noiseTextureSize];
            
            // Initialize loot table defaults
            if (_lootItems == null || _lootItems.Length == 0)
            {
                _lootItems = new string[]
                {
                    "Common Junk", "Copper Coin", "Iron Ore",
                    "Silver Ring", "Gold Bar", "Magic Crystal",
                    "Rare Gem", "Epic Weapon", "Legendary Artifact"
                };
            }
            
            if (_lootWeights == null || _lootWeights.Length == 0)
            {
                // Rarity weights: common items have higher weight
                _lootWeights = new float[]
                {
                    100f, 80f, 60f,  // Common
                    30f, 15f, 8f,    // Uncommon
                    4f, 1f, 0.1f     // Rare
                };
            }
            
            _lootDistribution = new int[_lootItems.Length];
        }
        
        /// <summary>
        /// Regenerates all procedural content with the current seed.
        /// </summary>
        public void GenerateAll()
        {
            float startTime = Time.realtimeSinceStartup;
            
            // Reset RNG to ensure determinism
            _rng.Reset();
            CENoise.Initialize(_worldSeed);
            
            GenerateTerrain();
            GenerateNoiseTexture();
            
            _lastGenerationTime = (Time.realtimeSinceStartup - startTime) * 1000f;
            _generationCount++;
            
            UpdateDisplay();
            
            CELogger.Info("Procgen", $"Generated world with seed {_worldSeed} in {_lastGenerationTime:F2}ms");
        }
        
        // ========================================
        // TERRAIN GENERATION (CENoise)
        // ========================================
        
        /// <summary>
        /// Generates terrain using CENoise fractal noise.
        /// Demonstrates coherent noise for natural-looking landscapes.
        /// </summary>
        private void GenerateTerrain()
        {
            if (_terrainParent == null) return;
            
            // Get or create the terrain material
            Material terrainMaterial = GetOrCreateTerrainMaterial();
            
            // Clean up existing blocks
            for (int i = 0; i < _terrainBlocks.Length; i++)
            {
                if (_terrainBlocks[i] != null)
                {
                    Destroy(_terrainBlocks[i].gameObject);
                }
            }
            
            int index = 0;
            for (int z = 0; z < _terrainDepth; z++)
            {
                for (int x = 0; x < _terrainWidth; x++)
                {
                    // Use fractal noise for natural terrain
                    float nx = x * _noiseScale;
                    float nz = z * _noiseScale;
                    
                    // Combine multiple noise types for interesting terrain
                    float height = CENoise.Fractal2D(nx, nz, 4, 0.5f, 2f);
                    
                    // Add some variation with ridged noise for mountains
                    height += CENoise.Ridged2D(nx * 0.5f, nz * 0.5f, 2, 0.6f, 2f) * 0.3f;
                    
                    // Normalize to 0-1 range
                    height = (height + 1f) * 0.5f;
                    
                    // Create block
                    Vector3 position = new Vector3(
                        (x - _terrainWidth * 0.5f) * _terrainScale,
                        height * _heightScale,
                        (z - _terrainDepth * 0.5f) * _terrainScale
                    );
                    
                    GameObject block;
                    if (_terrainBlockPrefab != null)
                    {
                        block = Instantiate(_terrainBlockPrefab, _terrainParent);
                    }
                    else
                    {
                        // Create a cube primitive as fallback
                        block = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        block.transform.SetParent(_terrainParent);
                        block.name = $"TerrainBlock_{x}_{z}";
                        
                        // Remove collider to avoid physics overhead
                        var blockCollider = block.GetComponent<Collider>();
                        if (blockCollider != null) Destroy(blockCollider);
                        
                        // Apply base material
                        var blockBaseRenderer = block.GetComponent<Renderer>();
                        if (blockBaseRenderer != null)
                        {
                            blockBaseRenderer.sharedMaterial = terrainMaterial;
                        }
                    }
                    
                    block.transform.localPosition = position;
                    block.transform.localScale = Vector3.one * _terrainScale;
                    
                    // Color based on height
                    Renderer blockRenderer = block.GetComponent<Renderer>();
                    if (blockRenderer != null)
                    {
                        Color color = GetTerrainColor(height);
                        MaterialPropertyBlock props = new MaterialPropertyBlock();
                        props.SetColor("_Color", color);
                        blockRenderer.SetPropertyBlock(props);
                    }
                    
                    _terrainBlocks[index] = block.transform;
                    index++;
                }
            }
            
            if (_terrainBlockPrefab == null)
            {
                CELogger.Debug("Procgen", $"Terrain generated with {index} blocks (using primitive cubes)");
            }
        }
        
        /// <summary>
        /// Gets the fallback material or creates a default one for terrain blocks.
        /// </summary>
        private Material GetOrCreateTerrainMaterial()
        {
            if (_fallbackMaterial != null)
            {
                return _fallbackMaterial;
            }
            
            // Create a simple standard material - color will be set via MaterialPropertyBlock
            var material = new Material(Shader.Find("Standard"));
            material.name = "TerrainMaterial_Generated";
            material.color = Color.white;
            
            return material;
        }
        
        /// <summary>
        /// Maps height to terrain color (water -> grass -> rock -> snow).
        /// </summary>
        private Color GetTerrainColor(float height)
        {
            if (height < 0.3f)
                return new Color(0.2f, 0.3f, 0.8f); // Water blue
            else if (height < 0.45f)
                return new Color(0.8f, 0.7f, 0.5f); // Sand
            else if (height < 0.65f)
                return new Color(0.3f, 0.6f, 0.2f); // Grass green
            else if (height < 0.85f)
                return new Color(0.5f, 0.4f, 0.3f); // Rock brown
            else
                return new Color(0.95f, 0.95f, 1f); // Snow white
        }
        
        // ========================================
        // NOISE VISUALIZATION
        // ========================================
        
        /// <summary>
        /// Generates a noise texture showing the current noise mode.
        /// </summary>
        private void GenerateNoiseTexture()
        {
            if (_noiseDisplayRenderer == null) return;
            
            for (int y = 0; y < _noiseTextureSize; y++)
            {
                for (int x = 0; x < _noiseTextureSize; x++)
                {
                    float nx = x * _noiseScale * 2f;
                    float ny = y * _noiseScale * 2f;
                    
                    float value;
                    switch (_noiseMode)
                    {
                        case 0: // Perlin
                            value = CENoise.Perlin2D(nx, ny);
                            break;
                        case 1: // Simplex
                            value = CENoise.Simplex2D(nx, ny);
                            break;
                        case 2: // Worley
                            value = CENoise.Worley2D(nx * 0.5f, ny * 0.5f);
                            value = 1f - Mathf.Clamp01(value); // Invert for better visibility
                            break;
                        case 3: // Fractal
                            value = CENoise.Fractal2D(nx, ny, 4, 0.5f, 2f);
                            break;
                        case 4: // Ridged
                            value = CENoise.Ridged2D(nx, ny, 4, 0.5f, 2f);
                            break;
                        default:
                            value = CENoise.Turbulence2D(nx, ny, 4, 0.5f, 2f);
                            break;
                    }
                    
                    // Normalize to 0-1
                    value = (value + 1f) * 0.5f;
                    value = Mathf.Clamp01(value);
                    
                    _noisePixels[y * _noiseTextureSize + x] = new Color(value, value, value);
                }
            }
            
            _noiseTexture.SetPixels(_noisePixels);
            _noiseTexture.Apply();
            
            _noiseDisplayRenderer.material.mainTexture = _noiseTexture;
        }
        
        // ========================================
        // WEIGHTED RANDOM (CERandom)
        // ========================================
        
        /// <summary>
        /// Rolls for loot using CERandom weighted selection.
        /// Demonstrates deterministic loot tables.
        /// </summary>
        public void RollLoot()
        {
            // Use weighted random selection
            int index = _rng.WeightedChoice(_lootWeights);
            
            if (index >= 0 && index < _lootItems.Length)
            {
                string item = _lootItems[index];
                _lootDistribution[index]++;
                _lootRollCount++;
                
                // Determine rarity color
                string color;
                if (index < 3) color = "#AAAAAA"; // Common
                else if (index < 6) color = "#00FF00"; // Uncommon
                else if (index < 8) color = "#AA00FF"; // Rare
                else color = "#FFAA00"; // Legendary
                
                if (_lootResultText != null)
                {
                    _lootResultText.text = $"<b>LOOT DROP</b>\n" +
                        $"<color={color}>{item}</color>\n" +
                        $"<size=70%>Roll #{_lootRollCount}</size>";
                }
                
                CELogger.Debug("Procgen", $"Loot roll: {item}");
            }
            
            UpdateDisplay();
        }
        
        /// <summary>
        /// Rolls multiple times to show distribution.
        /// </summary>
        public void RollLoot100()
        {
            for (int i = 0; i < 100; i++)
            {
                int index = _rng.WeightedChoice(_lootWeights);
                if (index >= 0 && index < _lootItems.Length)
                {
                    _lootDistribution[index]++;
                    _lootRollCount++;
                }
            }
            
            UpdateDisplay();
            CELogger.Info("Procgen", "Rolled 100 loot items");
        }
        
        // ========================================
        // UI CALLBACKS
        // ========================================
        
        public void OnChangeSeed()
        {
            _worldSeed = Random.Range(0, int.MaxValue);
            _rng = new CERandom(_worldSeed);
            
            // Reset loot stats
            for (int i = 0; i < _lootDistribution.Length; i++)
            {
                _lootDistribution[i] = 0;
            }
            _lootRollCount = 0;
            
            GenerateAll();
            
            if (_seedText != null)
            {
                _seedText.text = $"Seed: {_worldSeed}";
            }
        }
        
        public void OnSetSeed()
        {
            // For demo, cycle through preset seeds
            int[] presetSeeds = { 12345, 42, 1337, 9001, 31415 };
            int currentIndex = -1;
            for (int i = 0; i < presetSeeds.Length; i++)
            {
                if (presetSeeds[i] == _worldSeed)
                {
                    currentIndex = i;
                    break;
                }
            }
            
            _worldSeed = presetSeeds[(currentIndex + 1) % presetSeeds.Length];
            _rng = new CERandom(_worldSeed);
            
            GenerateAll();
        }
        
        public void OnNextNoiseMode()
        {
            _noiseMode = (_noiseMode + 1) % 6;
            GenerateNoiseTexture();
            UpdateDisplay();
        }
        
        public void OnPrevNoiseMode()
        {
            _noiseMode = (_noiseMode + 5) % 6;
            GenerateNoiseTexture();
            UpdateDisplay();
        }
        
        public void OnResetLootStats()
        {
            for (int i = 0; i < _lootDistribution.Length; i++)
            {
                _lootDistribution[i] = 0;
            }
            _lootRollCount = 0;
            
            // Reset RNG for fresh sequence
            _rng.Reset();
            
            UpdateDisplay();
            CELogger.Info("Procgen", "Loot stats reset");
        }
        
        // ========================================
        // DISPLAY
        // ========================================
        
        private void UpdateDisplay()
        {
            if (_statsText == null) return;
            
            string noiseName;
            switch (_noiseMode)
            {
                case 0: noiseName = "Perlin"; break;
                case 1: noiseName = "Simplex"; break;
                case 2: noiseName = "Worley (Cellular)"; break;
                case 3: noiseName = "Fractal (fBm)"; break;
                case 4: noiseName = "Ridged"; break;
                default: noiseName = "Turbulence"; break;
            }
            
            // Build loot distribution string
            string lootDist = "";
            if (_lootRollCount > 0)
            {
                lootDist = "\n<size=70%>";
                for (int i = 0; i < Mathf.Min(_lootItems.Length, 5); i++)
                {
                    float percent = (_lootDistribution[i] / (float)_lootRollCount) * 100f;
                    lootDist += $"{_lootItems[i]}: {percent:F1}%\n";
                }
                lootDist += "</size>";
            }
            
            _statsText.text = $"<b>PROCGEN METRICS</b>\n" +
                             $"Seed: <color=#00FFFF>{_worldSeed}</color>\n" +
                             $"Noise: <color=#FFFF00>{noiseName}</color>\n" +
                             $"Gen Time: {_lastGenerationTime:F2}ms\n" +
                             $"Generations: {_generationCount}\n" +
                             $"Loot Rolls: {_lootRollCount}\n" +
                             $"<color=#00FF00>Deterministic: Same seed = same world!</color>" +
                             lootDist;
            
            if (_seedText != null)
            {
                _seedText.text = $"<b>Current Seed:</b> {_worldSeed}";
            }
        }
        
        // ========================================
        // ADDITIONAL DEMOS
        // ========================================
        
        /// <summary>
        /// Demonstrates CERandom vector generation.
        /// </summary>
        public void OnGenerateRandomVectors()
        {
            Vector3 insideSphere = _rng.InsideUnitSphere();
            Vector3 onSphere = _rng.OnUnitSphere();
            Vector2 insideCircle = _rng.InsideUnitCircle();
            Quaternion rotation = _rng.RotationUniform();
            Color color = _rng.ColorHSV();
            
            CELogger.Info("Procgen", 
                $"Random Vectors:\n" +
                $"  InsideSphere: {insideSphere}\n" +
                $"  OnSphere: {onSphere}\n" +
                $"  InsideCircle: {insideCircle}\n" +
                $"  Rotation: {rotation.eulerAngles}\n" +
                $"  Color: {color}");
        }
        
        /// <summary>
        /// Demonstrates CERandom array shuffling.
        /// </summary>
        public void OnShuffleDemo()
        {
            int[] deck = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
            _rng.Shuffle(deck);
            
            string result = "Shuffled: ";
            for (int i = 0; i < deck.Length; i++)
            {
                result += deck[i] + " ";
            }
            
            CELogger.Info("Procgen", result);
        }
    }
}













