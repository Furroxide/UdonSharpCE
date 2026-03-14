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
        private float _respawnTimer;
        private Color _originalColor;
        private float _originalLightIntensity;
        
        void Start()
        {
            if (_glowRenderer != null)
            {
                _originalColor = _glowRenderer.material.color;
            }
            if (_pointLight != null)
            {
                _originalLightIntensity = _pointLight.intensity;
            }
            UpdateVisualState();
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
            
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }
            
            Dig();
            _isDug = true;
            RequestSerialization();
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
                _feedbackText.text = $"Found: {treasure}\nRarity: {rarityName}";
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
            
            // Simple rarity determination based on treasure name
            if (treasure.Contains("Legendary") || treasure.Contains("Dragon") || treasure.Contains("Crown"))
                return 2; // Legendary
            if (treasure.Contains("Rare") || treasure.Contains("Ruby") || treasure.Contains("Emerald"))
                return 1; // Rare
            return 0; // Common
        }
        
        private void Respawn()
        {
            _isDug = false;
            UpdateVisualState();
            
            if (Networking.IsOwner(gameObject))
            {
                RequestSerialization();
            }
        }
        
        private void UpdateVisualState()
        {
            // Update visual representation
            if (_glowRenderer != null)
            {
                Color targetColor = _isDug ? Color.gray * 0.3f : _originalColor;
                _glowRenderer.material.color = targetColor;
            }
            
            if (_pointLight != null)
            {
                _pointLight.enabled = !_isDug;
            }
            
            // Update interaction text
            InteractionText = _isDug ? "" : "Dig for Treasure!";
        }
        
        public override void OnDeserialization()
        {
            UpdateVisualState();
            if (_isDug)
            {
                _respawnTimer = _respawnTime;
            }
        }
        
        // Public setters for programmatic configuration
        public void SetDigSiteId(int id) => _digSiteId = id;
        public void SetLootTable(string[] table) => _lootTable = table;
        public void SetLootWeights(float[] weights) => _lootWeights = weights;
        public void SetRespawnTime(float time) => _respawnTime = time;
    }
}

