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

        // The respawn *intent*, not the countdown: the server time at which this pad comes back. Syncing
        // a per-frame countdown meant every unrelated serialization of this behaviour shipped a
        // meaningless mid-flight value, and a master handover restarted the countdown from scratch
        // because the new owner had no idea how much of it had already elapsed. A deadline is a fact
        // every client agrees on, costs the same eight bytes once, and needs no repeat traffic.
        [UdonSynced] private double _respawnServerTime;

        private const float SerializationRetryDelay = 0.5f;

        private Vector3 _basePosition;
        private bool _localAvailable = true;
        private bool _serializationRetryPending;

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

            EnsureMasterOwnership();
            ApplyAvailability();
        }

        private void OnEnable()
        {
            // Re-apply on enable. Shared state arrives via OnDeserialization, which a disabled behaviour
            // never receives, so a pad enabled later would otherwise render its compiled heap default
            // (available) until the owner happened to serialize again -- which, once every pad has
            // respawned, may be never. Reads synced state, writes none of it.
            ApplyAvailability();
        }

        private void Update()
        {
            // Only master handles respawn timer
            if (Networking.IsMaster && !_isAvailable && EnsureMasterOwnership())
            {
                // Compare against the shared deadline rather than counting down a local float. Every
                // client can already see when the pad is due back, so no per-frame traffic is needed.
                if (Networking.GetServerTimeInSeconds() >= _respawnServerTime)
                {
                    _isAvailable = true;
                    _respawnServerTime = 0d;
                    RequestBoostSerialization();
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

            if (!EnsureMasterOwnership())
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
            _respawnServerTime = Networking.GetServerTimeInSeconds() + RespawnTime;

            // Give boost to vehicle
            vehicle.AddBoost(BoostAmount);

            RequestBoostSerialization();
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
            ApplyAvailability();
        }

        /// <summary>
        /// Idempotent re-apply of the shared availability flag. Writes no synced field, so it is safe to
        /// call from a non-owner on enable, on Start and on deserialization alike.
        /// </summary>
        private void ApplyAvailability()
        {
            _localAvailable = _isAvailable;
            UpdateVisuals();
        }

        public override void OnPostSerialization(VRC.Udon.Common.SerializationResult result)
        {
            if (result.success)
            {
                _serializationRetryPending = false;
                return;
            }

            if (Networking.IsOwner(gameObject))
                ScheduleBoostSerializationRetry();
        }

        public override void OnMasterTransferred(VRCPlayerApi newMaster)
        {
            if (Utilities.IsValid(newMaster) && newMaster.isLocal)
                EnsureMasterOwnership();
        }

        public void _CE_RetryBoostSerialization()
        {
            _serializationRetryPending = false;
            RequestBoostSerialization();
        }

        private bool EnsureMasterOwnership()
        {
            if (!Networking.IsMaster)
                return false;

            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (!Utilities.IsValid(localPlayer))
                return false;

            if (Networking.IsOwner(localPlayer, gameObject))
                return true;

            // Report the state after the transfer, not before it. Networking.SetOwner applies locally and
            // synchronously, so returning a bare false here failed the very call that had just succeeded:
            // the first collection or respawn tick after a master handover was dropped on the floor and
            // the pad sat dead until something else happened to poke it.
            Networking.SetOwner(localPlayer, gameObject);
            return Networking.IsOwner(localPlayer, gameObject);
        }

        private void RequestBoostSerialization()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            if (Networking.IsClogged)
            {
                ScheduleBoostSerializationRetry();
                return;
            }

            RequestSerialization();
        }

        private void ScheduleBoostSerializationRetry()
        {
            if (_serializationRetryPending)
                return;

            _serializationRetryPending = true;
            SendCustomEventDelayedSeconds(
                nameof(_CE_RetryBoostSerialization),
                SerializationRetryDelay,
                VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);
        }
    }
}

