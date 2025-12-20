using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;
using VRC.SDKBase;

namespace RocketLeague
{
    /// <summary>
    /// Boost pickup pad. Refills vehicle boost when driven over.
    /// Large pads give full boost, small pads give partial.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class BoostPickup : UdonSharpBehaviour
    {
        [Header("Boost Settings")]
        [Tooltip("Amount of boost to add (in seconds of boost time)")]
        public float BoostAmount = 0.33f;

        [Tooltip("Time before pad respawns after being collected")]
        public float RespawnTime = 4f;

        [Tooltip("Is this a large (full boost) pad?")]
        public bool IsLargePad;

        [Header("Visuals")]
        public GameObject ActiveVisual;
        public GameObject InactiveVisual;
        public ParticleSystem CollectEffect;
        public AudioSource CollectSound;

        [Header("Animation")]
        public float BobSpeed = 2f;
        public float BobHeight = 0.1f;
        public float RotationSpeed = 90f;

        [UdonSynced] private bool _isAvailable = true;
        [UdonSynced] private float _respawnTimer;

        private Vector3 _basePosition;
        private bool _localAvailable = true;

        private void Start()
        {
            if (ActiveVisual != null)
                _basePosition = ActiveVisual.transform.localPosition;

            // Large pads give full boost and have longer respawn
            if (IsLargePad)
            {
                BoostAmount = 2f; // Full 2 seconds of boost
                RespawnTime = 10f;
            }

            UpdateVisuals();
        }

        private void Update()
        {
            // Only master handles respawn timer
            if (Networking.IsMaster && !_isAvailable)
            {
                _respawnTimer -= Time.deltaTime;
                if (_respawnTimer <= 0f)
                {
                    _isAvailable = true;
                    RequestSerialization();
                    SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(OnRespawnRemote));
                }
            }

            // Animate active visual
            if (_isAvailable && ActiveVisual != null)
            {
                // Bob up and down
                float bobOffset = Mathf.Sin(Time.time * BobSpeed) * BobHeight;
                ActiveVisual.transform.localPosition = _basePosition + Vector3.up * bobOffset;

                // Rotate
                ActiveVisual.transform.Rotate(Vector3.up, RotationSpeed * Time.deltaTime);
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other == null || !_isAvailable)
                return;

            // Only master handles collection
            if (!Networking.IsMaster)
                return;

            // Check if it's a vehicle
            NetVehicle vehicle = other.GetComponent<NetVehicle>();
            if (vehicle == null)
            {
                // Check parent
                vehicle = other.GetComponentInParent<NetVehicle>();
            }

            if (vehicle == null)
                return;

            // Collect the boost
            Collect(vehicle);
        }

        private void Collect(NetVehicle vehicle)
        {
            if (!_isAvailable)
                return;

            _isAvailable = false;
            _respawnTimer = RespawnTime;

            // Give boost to vehicle
            vehicle.AddBoost(BoostAmount);

            RequestSerialization();
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(OnCollectRemote));
        }

        public void OnCollectRemote()
        {
            _localAvailable = false;
            UpdateVisuals();

            if (CollectEffect != null)
                CollectEffect.Play();

            if (CollectSound != null)
                CollectSound.Play();
        }

        public void OnRespawnRemote()
        {
            _localAvailable = true;
            UpdateVisuals();
        }

        private void UpdateVisuals()
        {
            if (ActiveVisual != null)
                ActiveVisual.SetActive(_localAvailable);

            if (InactiveVisual != null)
                InactiveVisual.SetActive(!_localAvailable);
        }

        public override void OnDeserialization()
        {
            if (_localAvailable != _isAvailable)
            {
                _localAvailable = _isAvailable;
                UpdateVisuals();
            }
        }
    }
}

