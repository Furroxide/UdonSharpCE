using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Records local input each tick and publishes redundant input frames via synced fields.
    /// Designed to be attached to a player-owned object (e.g., their vehicle).
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class InputRecorder : UdonSharpBehaviour
    {
        [Header("Configuration")]
        public NetPhysicsWorld World;
        public int RedundantFrames = 10;
        public float SendRate = 20f;

        [Header("Unity Input Mapping")]
        public string ThrottleAxis = "Vertical";
        public string SteeringAxis = "Horizontal";
        public string JumpButton = "Jump";
        public string BoostButton = "Boost";
        public string HandbrakeButton = "Fire3";
        public string DodgeButton = "Fire2";

        [Header("Dodge Settings")]
        [Tooltip("Use stick direction for dodge when jump pressed while airborne")]
        public bool UseStickForDodge = true;

        // Max packet size: 1 header byte + RedundantFrames * NetworkSizeBytes (10 * 10 = 100)
        [UdonSynced] private byte[] _inputPacket = new byte[101];
        [UdonSynced] private int _packetSequence;

        private InputFrame[] _recentInputs;
        private int _head;
        private int _recordedCount;
        private float _lastSendTime;
        private int _lastProcessedSequence = int.MinValue;

        private InputFrame _currentInput;

        public InputFrame CurrentInput => _currentInput;

        private void Start()
        {
            int size = Mathf.Max(1, RedundantFrames);
            _recentInputs = new InputFrame[size];
            _head = 0;
            _recordedCount = 0;
            _lastSendTime = -9999f;
        }

        private void Update()
        {
            SampleInput();
        }

        private void FixedUpdate()
        {
            RecordAndSend();
        }

        /// <summary>
        /// Samples input for the current simulation frame. Call in Update().
        /// </summary>
        public void SampleInput()
        {
            if (World == null)
                return;

            float throttle = Mathf.Clamp(Input.GetAxisRaw(ThrottleAxis), -1f, 1f);
            float steering = Mathf.Clamp(Input.GetAxisRaw(SteeringAxis), -1f, 1f);

            byte buttons = 0;
            if (!string.IsNullOrEmpty(JumpButton) && Input.GetButton(JumpButton))
                buttons |= InputFrame.BUTTON_JUMP;
            if (!string.IsNullOrEmpty(HandbrakeButton) && Input.GetButton(HandbrakeButton))
                buttons |= InputFrame.BUTTON_HANDBRAKE;

            byte boost = 0;
            if (!string.IsNullOrEmpty(BoostButton) && Input.GetButton(BoostButton))
            {
                buttons |= InputFrame.BUTTON_BOOST;
                boost = 255;
            }

            // Dodge input - set when dodge button pressed or when using stick direction
            sbyte dodgeX = 0;
            sbyte dodgeY = 0;
            bool isDodging = false;

            if (!string.IsNullOrEmpty(DodgeButton) && Input.GetButton(DodgeButton))
            {
                isDodging = true;
            }

            // If using stick for dodge, capture direction when jump is pressed
            if (UseStickForDodge && (buttons & InputFrame.BUTTON_JUMP) != 0)
            {
                float stickMag = Mathf.Sqrt(throttle * throttle + steering * steering);
                if (stickMag > 0.3f)
                {
                    isDodging = true;
                    dodgeX = (sbyte)Mathf.RoundToInt(steering * 127f);
                    dodgeY = (sbyte)Mathf.RoundToInt(throttle * 127f);
                }
            }

            if (isDodging)
            {
                buttons |= InputFrame.BUTTON_DODGE;
            }

            _currentInput = new InputFrame
            {
                FrameNumber = World.CurrentFrame,
                Timestamp = Time.time,
                Throttle = (sbyte)Mathf.RoundToInt(throttle * 127f),
                Steering = (sbyte)Mathf.RoundToInt(steering * 127f),
                Boost = boost,
                Buttons = buttons,
                DodgeX = dodgeX,
                DodgeY = dodgeY,
            };
        }

        /// <summary>
        /// Records the sampled input and sends at the configured rate. Call after the physics tick.
        /// </summary>
        public void RecordAndSend()
        {
            if (World == null || _recentInputs == null)
                return;

            // Store into ring buffer.
            _recentInputs[_head] = _currentInput;
            _head = (_head + 1) % _recentInputs.Length;
            _recordedCount = Mathf.Min(_recordedCount + 1, _recentInputs.Length);

            if (SendRate <= 0f)
                return;

            if (Time.time - _lastSendTime < 1f / SendRate)
                return;

            SendInputPacket();
            _lastSendTime = Time.time;
        }

        private void SendInputPacket()
        {
            int count = Mathf.Min(Mathf.Max(1, RedundantFrames), _recordedCount);
            int totalBytes = 1 + count * InputFrame.NetworkSizeBytes;

            byte[] packet = new byte[totalBytes];
            packet[0] = (byte)count;

            int offset = 1;
            int start = (_head - count + _recentInputs.Length) % _recentInputs.Length;

            for (int i = 0; i < count; i++)
            {
                int idx = (start + i) % _recentInputs.Length;
                _recentInputs[idx].WriteNetworkBytes(packet, ref offset);
            }

            _inputPacket = packet;
            _packetSequence++;

            // Update local world immediately (owner doesn't receive OnDeserialization for its own serialization).
            VRCPlayerApi local = Networking.LocalPlayer;
            if (local != null && local.IsValid())
            {
                World.ReceiveInput(local.playerId, packet);
            }

            RequestSerialization();
        }

        public override void OnDeserialization()
        {
            if (World == null || _inputPacket == null || _inputPacket.Length == 0)
                return;

            // Ignore repeat calls with identical packet sequence.
            if (_packetSequence == _lastProcessedSequence)
                return;

            _lastProcessedSequence = _packetSequence;

            VRCPlayerApi owner = Networking.GetOwner(gameObject);
            if (owner == null || !owner.IsValid())
                return;

            World.ReceiveInput(owner.playerId, _inputPacket);
        }
    }
}
