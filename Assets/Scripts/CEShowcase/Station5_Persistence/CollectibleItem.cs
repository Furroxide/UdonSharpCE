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

        private const float SERIALIZATION_RETRY_DELAY = 0.5f;
        private const float COLLECT_SHRINK_DURATION = 0.25f;

        // Local state
        private Vector3 _startPosition;
        private Vector3 _startScale = Vector3.one;
        private float _collectShrinkTimer;
        private bool _localCollected;
        private bool _pendingLocalCollect;
        private bool _serializationRetryPending;
        private bool _started;
        private float _respawnTimer;
        private Renderer[] _visualRenderers;
        private Collider[] _visualColliders;

        void Start()
        {
            if (_visualObject == null)
            {
                _visualObject = gameObject;
            }

            _startPosition = _visualObject.transform.position;
            _startScale = _visualObject.transform.localScale;
            CacheVisualComponents();
            
            if (!string.IsNullOrEmpty(_interactionText))
            {
                InteractionText = $"{_interactionText} {_itemName}";
            }

            _started = true;
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

            // Collect feedback: quick pop-then-shrink before the visual switches off.
            // Purely local; touches no synced state.
            if (_collectShrinkTimer > 0f && _visualObject != null)
            {
                _collectShrinkTimer -= Time.deltaTime;
                float t = 1f - Mathf.Max(0f, _collectShrinkTimer) / COLLECT_SHRINK_DURATION;
                float scale = t < 0.3f
                    ? Mathf.Lerp(1f, 1.15f, t / 0.3f)
                    : Mathf.Lerp(1.15f, 0f, (t - 0.3f) / 0.7f);
                _visualObject.transform.localScale = _startScale * scale;

                if (_collectShrinkTimer <= 0f)
                {
                    SetVisualActive(false);
                    _visualObject.transform.localScale = _startScale;
                }
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
            RequestCollectibleSerialization();
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
            
            // Visual feedback: only the local pickup (reportToInventory) shrinks away over a
            // few frames via Update. Remote/late-join collection must hide synchronously so
            // the visual always reflects synced state the moment it lands.
            if (reportToInventory && _visualObject != null)
            {
                _collectShrinkTimer = COLLECT_SHRINK_DURATION;
            }
            else
            {
                SetVisualActive(false);
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

            // Cancel any in-flight collect shrink so the respawned visual is full size
            _collectShrinkTimer = 0f;

            SetVisualActive(true);
            if (_visualObject != null)
            {
                _visualObject.transform.position = _startPosition;
                _visualObject.transform.localScale = _startScale;
            }
            
            // Sync respawn
            if (syncState && Networking.IsOwner(gameObject))
            {
                RequestCollectibleSerialization();
            }
            
            CELogger.Debug("Collectible", $"Respawned: {_itemName}");
        }
        
        private void OnEnable()
        {
            // Re-apply on enable. Synced state arrives via OnDeserialization, which a disabled behaviour
            // never receives, so a station toggled on later would otherwise show every collectible at its
            // compiled heap default (un-collected) until the owner happened to serialize again.
            if (!_started)
            {
                // First activation: OnEnable runs before Start, and synced values can be delivered before
                // Start too -- VRChat drops the OnDeserialization for a behaviour that has not started,
                // but the data is already in place -- so _isCollected may be true here. Start caches
                // _startPosition and the renderer list and then applies the state itself, which is the
                // only time such a delivery gets applied; doing it now would play the pickup burst at
                // the world origin.
                return;
            }

            ApplySyncedState();
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

            SetVisualActive(true);
        }

        public override void OnPostSerialization(VRC.Udon.Common.SerializationResult result)
        {
            if (result.success)
            {
                _serializationRetryPending = false;
                return;
            }

            if (!Networking.IsOwner(gameObject))
                return;

            ScheduleCollectibleSerializationRetry();
        }

        public void _CE_RetryCollectibleSerialization()
        {
            _serializationRetryPending = false;
            RequestCollectibleSerialization();
        }

        private void RequestCollectibleSerialization()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            if (Networking.IsClogged)
            {
                ScheduleCollectibleSerializationRetry();
                return;
            }

            RequestSerialization();
        }

        private void ScheduleCollectibleSerializationRetry()
        {
            if (_serializationRetryPending)
                return;

            _serializationRetryPending = true;
            SendCustomEventDelayedSeconds(
                nameof(_CE_RetryCollectibleSerialization),
                SERIALIZATION_RETRY_DELAY,
                VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);
        }

        private void CacheVisualComponents()
        {
            if (_visualObject == null)
            {
                _visualRenderers = new Renderer[0];
                _visualColliders = new Collider[0];
                return;
            }

            _visualRenderers = _visualObject.GetComponentsInChildren<Renderer>(true);
            _visualColliders = _visualObject.GetComponentsInChildren<Collider>(true);
        }

        private void SetVisualActive(bool active)
        {
            if (_visualObject == null)
                return;

            if (_visualObject != gameObject)
            {
                _visualObject.SetActive(active);
                return;
            }

            if (_visualRenderers == null || _visualColliders == null)
                CacheVisualComponents();

            for (int i = 0; i < _visualRenderers.Length; i++)
            {
                if (_visualRenderers[i] != null)
                    _visualRenderers[i].enabled = active;
            }

            for (int i = 0; i < _visualColliders.Length; i++)
            {
                if (_visualColliders[i] != null)
                    _visualColliders[i].enabled = active;
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
