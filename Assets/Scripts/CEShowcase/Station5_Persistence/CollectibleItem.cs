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
        [UdonSynced] private double _collectedServerTime;
        
        // Local state
        private Vector3 _startPosition;
        private bool _localCollected;
        private bool _pendingLocalCollect;
        private float _respawnTimer;
        
        void Start()
        {
            if (_visualObject == null)
            {
                _visualObject = gameObject;
            }

            _startPosition = _visualObject.transform.position;
            
            if (!string.IsNullOrEmpty(_interactionText))
            {
                InteractionText = $"{_interactionText} {_itemName}";
            }
            
            ApplySyncedState();
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
            if (_localCollected && _canRespawn && Networking.IsOwner(gameObject))
            {
                double elapsed = _collectedServerTime > 0d
                    ? Networking.GetServerTimeInSeconds() - _collectedServerTime
                    : 0d;
                _respawnTimer = Mathf.Max(0f, _respawnTime - (float)elapsed);
                if (elapsed >= _respawnTime)
                {
                    Respawn(true);
                }
            }
        }
        
        public override void Interact()
        {
            if (_localCollected) return;
            if (!Utilities.IsValid(Networking.LocalPlayer)) return;
            
            if (Networking.IsOwner(gameObject))
            {
                CollectAsOwner();
                return;
            }

            _pendingLocalCollect = true;
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
        }

        public override void OnOwnershipTransferred(VRCPlayerApi newOwner)
        {
            if (!_pendingLocalCollect)
            {
                return;
            }

            if (!Utilities.IsValid(newOwner) || !newOwner.isLocal)
            {
                _pendingLocalCollect = false;
                ApplySyncedState();
                return;
            }

            CollectAsOwner();
        }

        private void CollectAsOwner()
        {
            if (_localCollected && _isCollected) return;

            _pendingLocalCollect = false;
            _isCollected = true;
            _collectedServerTime = Networking.GetServerTimeInSeconds();
            Collect(true);
            RequestSerialization();
        }
        
        private void Collect(bool reportToInventory)
        {
            if (_localCollected) return;
            
            _localCollected = true;
            
            // Report to inventory system
            if (reportToInventory && _inventorySystem != null)
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
        
        private void Respawn(bool syncState)
        {
            _localCollected = false;
            _isCollected = false;
            _pendingLocalCollect = false;
            _collectedServerTime = 0d;
            _respawnTimer = 0f;
            
            if (_visualObject != null)
            {
                _visualObject.SetActive(true);
                _visualObject.transform.position = _startPosition;
            }
            
            // Sync respawn
            if (syncState && Networking.IsOwner(gameObject))
            {
                RequestSerialization();
            }
            
            CELogger.Debug("Collectible", $"Respawned: {_itemName}");
        }
        
        public override void OnDeserialization()
        {
            ApplySyncedState();
        }

        private void ApplySyncedState()
        {
            if (_isCollected)
            {
                if (!_localCollected)
                {
                    Collect(false);
                }

                if (_canRespawn)
                {
                    double elapsed = _collectedServerTime > 0d
                        ? Networking.GetServerTimeInSeconds() - _collectedServerTime
                        : 0d;
                    _respawnTimer = Mathf.Max(0f, _respawnTime - (float)elapsed);
                }

                return;
            }

            if (_localCollected)
            {
                Respawn(false);
                return;
            }

            if (_visualObject != null)
            {
                _visualObject.SetActive(true);
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
