using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;
using VRC.SDKBase;

namespace CEShowcase.Station5_Persistence
{
    /// <summary>
    /// Collectible item that can be picked up by players.
    /// Reports to InventorySystem when collected.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class CollectibleItem : UdonSharpBehaviour
    {
        [Header("Item Settings")]
        [SerializeField] private int _itemId = 0;
        [SerializeField] private string _itemName = "Item";
        
        [Header("References")]
        [SerializeField] private InventorySystem _inventorySystem;
        
        [Header("Visual")]
        [SerializeField] private GameObject _visualObject;
        [SerializeField] private ParticleSystem _collectParticles;
        [SerializeField] private float _bobAmplitude = 0.2f;
        [SerializeField] private float _bobSpeed = 2f;
        [SerializeField] private float _rotateSpeed = 45f;
        
        [Header("Audio")]
        [SerializeField] private AudioSource _collectSound;
        
        [Header("Respawn")]
        [SerializeField] private bool _canRespawn = true;
        [SerializeField] private float _respawnTime = 10f;
        
        [Header("Interaction")]
        [SerializeField] private string _interactionText = "Collect";
        
        // Synced state
        [UdonSynced] private bool _isCollected;
        
        // Local state
        private Vector3 _startPosition;
        private bool _localCollected;
        private float _respawnTimer;
        
        void Start()
        {
            _startPosition = transform.position;
            
            if (!string.IsNullOrEmpty(_interactionText))
            {
                InteractionText = $"{_interactionText} {_itemName}";
            }
            
            // Reset to uncollected state
            _isCollected = false;
            _localCollected = false;
            
            if (_visualObject != null)
            {
                _visualObject.SetActive(true);
            }
        }
        
        void Update()
        {
            // Bob and rotate animation
            if (!_localCollected && _visualObject != null)
            {
                // Bobbing motion
                float yOffset = Mathf.Sin(Time.time * _bobSpeed) * _bobAmplitude;
                _visualObject.transform.position = _startPosition + Vector3.up * yOffset;
                
                // Rotation
                _visualObject.transform.Rotate(Vector3.up, _rotateSpeed * Time.deltaTime);
            }
            
            // Respawn timer
            if (_localCollected && _canRespawn)
            {
                _respawnTimer -= Time.deltaTime;
                if (_respawnTimer <= 0)
                {
                    Respawn();
                }
            }
        }
        
        public override void Interact()
        {
            if (_localCollected) return;
            
            // Request ownership if needed for syncing
            if (!Networking.IsOwner(gameObject))
            {
                Networking.SetOwner(Networking.LocalPlayer, gameObject);
            }
            
            // Collect the item
            Collect();
            
            // Sync to others
            _isCollected = true;
            RequestSerialization();
        }
        
        private void Collect()
        {
            if (_localCollected) return;
            
            _localCollected = true;
            
            // Report to inventory system
            if (_inventorySystem != null)
            {
                _inventorySystem.OnItemCollected(_itemId);
            }
            
            // Visual feedback
            if (_visualObject != null)
            {
                _visualObject.SetActive(false);
            }
            
            if (_collectParticles != null)
            {
                _collectParticles.transform.position = _startPosition;
                _collectParticles.Play();
            }
            
            if (_collectSound != null)
            {
                _collectSound.Play();
            }
            
            CELogger.Debug("Collectible", $"Collected: {_itemName}");
            
            // Start respawn timer
            if (_canRespawn)
            {
                _respawnTimer = _respawnTime;
            }
        }
        
        private void Respawn()
        {
            _localCollected = false;
            _isCollected = false;
            
            if (_visualObject != null)
            {
                _visualObject.SetActive(true);
                _visualObject.transform.position = _startPosition;
            }
            
            // Sync respawn
            if (Networking.IsOwner(gameObject))
            {
                RequestSerialization();
            }
            
            CELogger.Debug("Collectible", $"Respawned: {_itemName}");
        }
        
        public override void OnDeserialization()
        {
            // Sync state from network
            if (_isCollected && !_localCollected)
            {
                // Someone else collected this
                _localCollected = true;
                
                if (_visualObject != null)
                {
                    _visualObject.SetActive(false);
                }
                
                if (_canRespawn)
                {
                    _respawnTimer = _respawnTime;
                }
            }
            else if (!_isCollected && _localCollected)
            {
                // Item was respawned
                Respawn();
            }
        }
        
        // Editor helpers
        public void SetItemId(int id) => _itemId = id;
        public void SetItemName(string name)
        {
            _itemName = name;
            if (!string.IsNullOrEmpty(_interactionText))
            {
                InteractionText = $"{_interactionText} {_itemName}";
            }
        }
        public void SetInventorySystem(InventorySystem system) => _inventorySystem = system;
        
        public int GetItemId() => _itemId;
        public string GetItemName() => _itemName;
        public bool IsCollected() => _localCollected;
    }
}
