using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;
using VRC.SDKBase;

namespace CEShowcase.Station6_Procgen
{
    /// <summary>
    /// Interactive dig site for the Treasure Island Explorer demo.
    /// Players can interact with these to dig for treasure with weighted loot.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class DigSite : UdonSharpBehaviour
    {
        [Header("Dig Site Settings")]
        [SerializeField] private int _digSiteId;
        [SerializeField] private float _respawnTime = 30f;
        
        [Header("Loot Configuration")]
        [SerializeField] private string[] _lootTable;
        [SerializeField] private float[] _lootWeights;
        
        [Header("References")]
        [SerializeField] private Renderer _glowRenderer;
        [SerializeField] private Light _pointLight;
        [SerializeField] private TMPro.TextMeshProUGUI _feedbackText;
        
        [Header("Audio")]
        [SerializeField] private AudioSource _digSound;
        [SerializeField] private AudioSource _treasureFoundSound;
        
        [UdonSynced] private bool _isDug;
        private const float SERIALIZATION_RETRY_DELAY = 0.5f;
        private const float DUG_EMISSION_SCALE = 0.08f;

        private float _respawnTimer;
        private Color _originalColor;
        private float _originalLightIntensity;
        private bool _pendingDig;
        private bool _serializationRetryPending;
        private bool _visualsCached;
        private bool _defaultsApplied;
        private Color _originalEmission;
        private bool _hasEmission;
        private MaterialPropertyBlock _glowProps;

        void Start()
        {
            EnsureInitialized();
            UpdateVisualState();
        }

        private void OnEnable()
        {
            // Re-apply on enable. _isDug only reaches this client through OnDeserialization, which a
            // disabled behaviour never receives, so a dig site whose station is toggled on later would
            // render the compiled heap default (undug, glowing, interactable) even though it has already
            // been looted. Idempotent and writes no synced field.
            EnsureInitialized();
            UpdateVisualState();
        }

        private void EnsureInitialized()
        {
            ApplyConfigurationDefaults();
            CacheOriginalVisuals();
        }

        /// <summary>
        /// Fills in whatever the scene left blank. The four showcase sites shipped with an empty loot
        /// table and no references at all, so a dig had nothing to report and nothing to show: RollLoot
        /// returned "Nothing", UpdateVisualState had no renderer or light to touch, and the only lasting
        /// effect was the site switching its own interaction off. ProcgenDemo already carries this kind of
        /// fallback for its own loot table, so this follows that convention rather than depending on four
        /// hand-wired inspector slots staying wired.
        /// Runs before CacheOriginalVisuals so an auto-wired renderer still gets its pristine glow read.
        /// </summary>
        private void ApplyConfigurationDefaults()
        {
            if (_defaultsApplied)
                return;

            _defaultsApplied = true;

            if (_lootTable == null || _lootTable.Length == 0)
            {
                // Names and relative rarity mirror Station 5's InventorySystem item list, so the same
                // treasure never reads as common here and legendary in the inventory panel.
                _lootTable = new string[]
                {
                    "Gold Coin", "Silver Coin", "Crystal Shard",
                    "Ruby", "Emerald", "Sapphire", "Magic Scroll",
                    "Ancient Key", "Diamond", "Dragon Scale"
                };

                _lootWeights = new float[]
                {
                    100f, 80f, 60f,      // Common
                    25f, 25f, 25f, 20f,  // Rare
                    6f, 4f, 1f           // Legendary
                };
            }

            // A dig site is its own glow source; a hand-wired override still wins.
            if (_glowRenderer == null)
            {
                _glowRenderer = GetComponent<Renderer>();
            }

            if (_pointLight == null)
            {
                _pointLight = GetComponentInChildren<Light>();
            }
        }

        /// <summary>
        /// Captures the pristine glow before UpdateVisualState ever overwrites it. OnEnable runs before
        /// Start on the first activation, so this has to be callable from both -- caching the dug-out grey
        /// as if it were the original colour would leave the site permanently dull once it respawned.
        /// </summary>
        private void CacheOriginalVisuals()
        {
            if (_visualsCached)
                return;

            if (_glowRenderer != null)
            {
                // A property block rather than .material, matching FlockingDemo, ProcgenDemo and
                // NetworkingDemo: all four dig sites share one accent material asset, and .material would hand
                // each of them a private copy of it at runtime purely to change two colours.
                _glowProps = new MaterialPropertyBlock();

                Material glowMaterial = _glowRenderer.sharedMaterial;
                if (glowMaterial != null)
                {
                    _originalColor = glowMaterial.color;

                    // The station accent material is emissive, so albedo alone is not the visible state:
                    // dimming it while emission stays at full HDR orange leaves a spent site glowing exactly
                    // as brightly as a fresh one, which is the tell players actually read.
                    _hasEmission = glowMaterial.HasProperty("_EmissionColor");
                    if (_hasEmission)
                    {
                        _originalEmission = glowMaterial.GetColor("_EmissionColor");
                    }
                }
            }
            if (_pointLight != null)
            {
                _originalLightIntensity = _pointLight.intensity;
            }

            _visualsCached = true;
        }
        
        void Update()
        {
            if (_isDug && _respawnTimer > 0)
            {
                _respawnTimer -= Time.deltaTime;
                if (_respawnTimer <= 0)
                {
                    Respawn();
                }
            }
            
            // Pulse the glow when available
            if (!_isDug && _pointLight != null)
            {
                float pulse = 0.8f + Mathf.Sin(Time.time * 2f) * 0.2f;
                _pointLight.intensity = _originalLightIntensity * pulse;
            }
        }
        
        public override void Interact()
        {
            if (_isDug) return;
            if (!Utilities.IsValid(Networking.LocalPlayer)) return;
            
            if (Networking.IsOwner(gameObject))
            {
                DigAsOwner();
                return;
            }

            _pendingDig = true;
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }

        public override void OnOwnershipTransferred(VRCPlayerApi newOwner)
        {
            if (!_pendingDig)
                return;

            if (Utilities.IsValid(newOwner) && newOwner.isLocal)
            {
                DigAsOwner();
                return;
            }

            _pendingDig = false;
            UpdateVisualState();
        }

        private void DigAsOwner()
        {
            if (_isDug)
            {
                _pendingDig = false;
                return;
            }

            _pendingDig = false;
            _isDug = true;
            Dig();
            RequestDigSiteSerialization();
        }
        
        private void Dig()
        {
            if (_digSound != null) _digSound.Play();
            
            // Determine treasure found
            string treasure = RollLoot();
            int rarity = DetermineRarity(treasure);
            
            if (_treasureFoundSound != null && rarity >= 1)
            {
                _treasureFoundSound.pitch = 1f + (rarity * 0.2f);
                _treasureFoundSound.Play();
            }
            
            // Display feedback
            if (_feedbackText != null)
            {
                string rarityName = rarity == 2 ? "LEGENDARY" : (rarity == 1 ? "Rare" : "Common");
                string rarityColor = rarity == 2 ? "#FFAA00" : (rarity == 1 ? "#AA00FF" : "#AAAAAA");
                _feedbackText.text = $"<b>DIG SITE {_digSiteId + 1}</b>\n" +
                    $"<color={rarityColor}>{treasure}</color>\n" +
                    $"<size=70%>{rarityName}</size>";
            }
            
            CELogger.Info("DigSite", $"Player found {treasure} (rarity: {rarity}) at dig site {_digSiteId}");
            
            UpdateVisualState();
            _respawnTimer = _respawnTime;
        }
        
        private string RollLoot()
        {
            if (_lootTable == null || _lootTable.Length == 0) return "Nothing";
            if (_lootWeights == null || _lootWeights.Length != _lootTable.Length)
            {
                // Equal weights fallback
                return _lootTable[Random.Range(0, _lootTable.Length)];
            }
            
            // Calculate total weight
            float totalWeight = 0f;
            for (int i = 0; i < _lootWeights.Length; i++)
            {
                totalWeight += _lootWeights[i];
            }
            
            // Roll
            float roll = Random.Range(0f, totalWeight);
            float cumulative = 0f;
            
            for (int i = 0; i < _lootTable.Length; i++)
            {
                cumulative += _lootWeights[i];
                if (roll <= cumulative)
                {
                    return _lootTable[i];
                }
            }
            
            return _lootTable[_lootTable.Length - 1];
        }
        
        private int DetermineRarity(string treasure)
        {
            if (string.IsNullOrEmpty(treasure) || treasure == "Nothing") return -1;
            
            // Rarity by treasure name, keyed to Station 5's InventorySystem rarity table.
            if (treasure.Contains("Legendary") || treasure.Contains("Dragon") || treasure.Contains("Crown")
                || treasure.Contains("Diamond") || treasure.Contains("Ancient Key"))
                return 2; // Legendary
            if (treasure.Contains("Rare") || treasure.Contains("Ruby") || treasure.Contains("Emerald")
                || treasure.Contains("Sapphire") || treasure.Contains("Scroll"))
                return 1; // Rare
            return 0; // Common
        }
        
        private void Respawn()
        {
            _isDug = false;
            UpdateVisualState();
            
            if (Networking.IsOwner(gameObject))
            {
                RequestDigSiteSerialization();
            }
        }
        
        private void UpdateVisualState()
        {
            // Update visual representation
            if (_glowRenderer != null && _glowProps != null)
            {
                Color dugAlbedo = new Color(0.15f, 0.15f, 0.15f, 1f);
                _glowProps.SetColor("_Color", _isDug ? dugAlbedo : _originalColor);

                if (_hasEmission)
                {
                    _glowProps.SetColor(
                        "_EmissionColor",
                        _isDug ? _originalEmission * DUG_EMISSION_SCALE : _originalEmission);
                }

                _glowRenderer.SetPropertyBlock(_glowProps);
            }
            
            if (_pointLight != null)
            {
                // Intensity rather than the enabled flag: DemoManager switches lights off when it hides a
                // whole station, and a respawn tick firing behind a hidden station must not undo that.
                _pointLight.intensity = _isDug ? 0f : _originalLightIntensity;
            }
            
            // Clearing InteractionText does not remove the prompt -- VRChat falls back to a bare "Use"
            // on an empty string, so a spent site still invited a click that Interact() drops on the floor.
            InteractionText = "Dig for Treasure!";
            DisableInteractive = _isDug;
        }
        
        public override void OnDeserialization()
        {
            _pendingDig = false;
            UpdateVisualState();
            if (_isDug)
            {
                _respawnTimer = _respawnTime;
            }
        }

        public override void OnPostSerialization(VRC.Udon.Common.SerializationResult result)
        {
            if (result.success)
            {
                _serializationRetryPending = false;
                return;
            }

            if (Networking.IsOwner(gameObject))
                ScheduleDigSiteSerializationRetry();
        }

        public void _CE_RetryDigSiteSerialization()
        {
            _serializationRetryPending = false;
            RequestDigSiteSerialization();
        }

        private void RequestDigSiteSerialization()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            if (Networking.IsClogged)
            {
                ScheduleDigSiteSerializationRetry();
                return;
            }

            RequestSerialization();
        }

        private void ScheduleDigSiteSerializationRetry()
        {
            if (_serializationRetryPending)
                return;

            _serializationRetryPending = true;
            SendCustomEventDelayedSeconds(
                nameof(_CE_RetryDigSiteSerialization),
                SERIALIZATION_RETRY_DELAY,
                VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);
        }
        
        // Public setters for programmatic configuration
        public void SetDigSiteId(int id) => _digSiteId = id;
        public void SetLootTable(string[] table) => _lootTable = table;
        public void SetLootWeights(float[] weights) => _lootWeights = weights;
        public void SetRespawnTime(float time) => _respawnTime = time;
    }
}
