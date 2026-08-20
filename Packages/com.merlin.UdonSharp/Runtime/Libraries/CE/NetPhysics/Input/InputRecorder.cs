using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Records local input each tick and publishes redundant input frames via synced fields.
    /// Belongs on the controllable object itself (the player's vehicle): only the client whose player
    /// the vehicle names as its driver samples and publishes, and only while it also owns the object.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class InputRecorder : UdonSharpBehaviour
    {
        [Header("Configuration")]
        public NetPhysicsWorld World;
        [Tooltip("Input frames carried by each packet. Older frames overlap the previous packet so a single dropped packet does not leave a hole in the receiver's buffer.")]
        public int RedundantFrames = 10;
        [Tooltip("Upper bound on this recorder's publish rate in Hz. The effective rate is also limited by InputBytesPerSecondBudget.")]
        public float SendRate = 15f;
        [Tooltip("Floor on the publish rate in Hz. Below this the jitter buffer starves and the car stutters for everybody, so the budget is allowed to be exceeded rather than dropping under it.")]
        public float MinSendRate = 5f;
        [Tooltip("Bytes per second the whole instance may spend on vehicle input, divided between every driven vehicle. VRChat gives an instance roughly 11 KB/s in total and the world's state broadcast wants a share of it too.")]
        public float InputBytesPerSecondBudget = 4096f;

        [Header("Dodge Settings")]
        [Tooltip("Use stick direction for dodge when jump pressed while airborne")]
        public bool UseStickForDodge = true;

        // Starts empty: this default is compiled into every client, and a fixed-size block of zeroes
        // is indistinguishable on arrival from a real packet describing ten frames of neutral input.
        [UdonSynced] private byte[] _inputPacket = new byte[0];
        [UdonSynced] private int _packetSequence;

        private InputFrame[] _recentInputs;
        private int _head;
        private int _recordedCount;
        private float _lastSendTime;
        private int _lastProcessedSequence;

        private InputFrame _currentInput;
        private NetVehicle _vehicle;

        // VRChat's own framing for the synced byte[] and the sequence int on top of the payload. Only
        // used to size the packet against the bandwidth budget, so an estimate is enough.
        private const int PacketOverheadBytes = 12;

        // Latest values from VRChat's input callbacks. UnityEngine.Input, which this used to read,
        // returns nothing at all in VR, so VR players had no vehicle control whatsoever. These
        // callbacks are the supported path and fire for desktop and VR alike.
        private float _moveHorizontal;
        private float _moveVertical;
        private bool _jumpHeld;
        private bool _boostHeld;
        private bool _handbrakeHeld;
        private bool _dodgeHeld;

        public InputFrame CurrentInput => _currentInput;

        /// <summary>
        /// True on the client whose player is actually driving this vehicle.
        /// </summary>
        /// <remarks>
        /// This used to be <c>Networking.IsOwner(gameObject)</c>, which answers "do I own this
        /// GameObject", not "is a player driving this car". A pooled vehicle nobody has been handed sits
        /// owned by the instance master, so the master sampled input for, recorded, and published a
        /// packet for every parked car in the pool -- N recorders' worth of bandwidth spent on cars
        /// nobody was in. The vehicle's published driver id is the only thing that says otherwise.
        /// </remarks>
        public bool IsLocalDriver
        {
            get
            {
                VRCPlayerApi local = Networking.LocalPlayer;
                if (local == null || !local.IsValid())
                    return false;

                if (_vehicle != null)
                    return _vehicle.OwnerPlayerId == local.playerId;

                // No vehicle to ask: fall back to ownership so a recorder used on its own still works.
                return Networking.IsOwner(local, gameObject);
            }
        }

        /// <summary>
        /// True when this client both drives the car and owns the object it would publish through.
        /// VRChat discards a non-owner's writes, so a driver whose ownership transfer has not landed yet
        /// records locally but must not try to send.
        /// </summary>
        public bool CanPublish => IsLocalDriver && Networking.IsOwner(gameObject);

        private void Start()
        {
            _vehicle = GetComponent<NetVehicle>();

            int size = Mathf.Max(1, RedundantFrames);
            _recentInputs = new InputFrame[size];
            for (int i = 0; i < _recentInputs.Length; i++)
                _recentInputs[i] = new InputFrame();
            _head = 0;
            _recordedCount = 0;
            _lastSendTime = -9999f;
            _currentInput = new InputFrame();

            if (World == null && _vehicle != null)
                World = _vehicle.World;
        }

        private void Update()
        {
            SampleInput();
        }

        private void FixedUpdate()
        {
            RecordAndSend();
        }

        public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
        {
            _moveHorizontal = value;
        }

        public override void InputMoveVertical(float value, UdonInputEventArgs args)
        {
            _moveVertical = value;
        }

        public override void InputJump(bool value, UdonInputEventArgs args)
        {
            _jumpHeld = value;
        }

        public override void InputUse(bool value, UdonInputEventArgs args)
        {
            _boostHeld = value;
        }

        public override void InputGrab(bool value, UdonInputEventArgs args)
        {
            _handbrakeHeld = value;
        }

        public override void InputDrop(bool value, UdonInputEventArgs args)
        {
            _dodgeHeld = value;
        }

        /// <summary>
        /// Samples input for the current simulation frame. Call in Update().
        /// </summary>
        public void SampleInput()
        {
            if (World == null || _currentInput == null)
                return;

            // The input callbacks fire on every behaviour in the scene, so a client that does not drive
            // this car would otherwise build a full input frame for it and hand it to the simulation.
            if (!IsLocalDriver)
            {
                ClearCurrentInput();
                return;
            }

            float throttle = Mathf.Clamp(_moveVertical, -1f, 1f);
            float steering = Mathf.Clamp(_moveHorizontal, -1f, 1f);

            byte buttons = 0;
            if (_jumpHeld)
                buttons |= InputFrame.BUTTON_JUMP;
            if (_handbrakeHeld)
                buttons |= InputFrame.BUTTON_HANDBRAKE;

            byte boost = 0;
            if (_boostHeld)
            {
                buttons |= InputFrame.BUTTON_BOOST;
                boost = 255;
            }

            // Dodge input - set when dodge button pressed or when using stick direction
            sbyte dodgeX = 0;
            sbyte dodgeY = 0;
            bool isDodging = _dodgeHeld;

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

            _currentInput.FrameNumber = World.CurrentFrame;
            _currentInput.Timestamp = Time.time;
            _currentInput.Throttle = (sbyte)Mathf.RoundToInt(throttle * 127f);
            _currentInput.Steering = (sbyte)Mathf.RoundToInt(steering * 127f);
            _currentInput.Boost = boost;
            _currentInput.Buttons = buttons;
            _currentInput.DodgeX = dodgeX;
            _currentInput.DodgeY = dodgeY;
        }

        /// <summary>
        /// Records the sampled input and sends at the configured rate. Call after the physics tick.
        /// </summary>
        public void RecordAndSend()
        {
            if (World == null || _recentInputs == null)
                return;

            // This ran on every client with no driver check at all. VRChat discards a non-owner's writes
            // and logs each one, and the local ReceiveInput below fed the caller's own player id, so
            // every client polluted its own input buffer and predictor with input for a car it does not
            // drive -- and the master did it for every parked car in the pool as well.
            if (!IsLocalDriver)
                return;

            // Store into ring buffer.
            _recentInputs[_head].CopyFrom(_currentInput);
            _head = (_head + 1) % _recentInputs.Length;
            _recordedCount = Mathf.Min(_recordedCount + 1, _recentInputs.Length);

            // Recording is local and free; publishing is neither. Ownership can lag the assignment by a
            // round trip, and a write from a client that does not own the object is dropped silently.
            if (!CanPublish)
                return;

            float interval = GetSendInterval();
            if (interval <= 0f)
                return;

            if (Time.time - _lastSendTime < interval)
                return;

            SendInputPacket();
            _lastSendTime = Time.time;
        }

        /// <summary>
        /// Seconds between packets, or a non-positive value when publishing is switched off.
        /// </summary>
        /// <remarks>
        /// Input used to be published by one recorder for the whole world. Moving to one recorder per
        /// vehicle -- which is what makes each driver able to publish their own car at all -- multiplied
        /// that cost by the number of driven cars, and nothing was re-sized to match: eight cars at
        /// 20 Hz with ten redundant frames each is roughly 18 KB/s of input alone, against an instance
        /// budget of about 11 KB/s that the world's state broadcast also has to come out of. The rate is
        /// therefore derived from a shared byte budget divided by the number of cars actually being
        /// driven, so a busy arena slows every car's input down together instead of every car sending as
        /// though it were alone.
        /// </remarks>
        private float GetSendInterval()
        {
            float rate = SendRate;
            if (rate <= 0f)
                return -1f;

            if (InputBytesPerSecondBudget > 0f)
            {
                int drivers = World != null ? World.GetDrivenVehicleCount() : 1;
                if (drivers < 1)
                    drivers = 1;

                float packetBytes = PacketOverheadBytes + Mathf.Max(1, RedundantFrames) * InputFrame.NetworkSizeBytes;
                float affordable = InputBytesPerSecondBudget / (drivers * packetBytes);
                if (affordable < rate)
                    rate = affordable;
            }

            // The floor deliberately wins over the budget. Under it the receiving jitter buffer starves
            // between packets and the car stutters for everybody, which is a worse instance than one
            // that is slightly over its input allowance.
            if (rate < MinSendRate)
                rate = MinSendRate;

            return rate > 0f ? 1f / rate : -1f;
        }

        private void ClearCurrentInput()
        {
            _currentInput.Throttle = 0;
            _currentInput.Steering = 0;
            _currentInput.Boost = 0;
            _currentInput.Buttons = 0;
            _currentInput.DodgeX = 0;
            _currentInput.DodgeY = 0;
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

            // Sequence 0 is the compiled-in default, so it is never a packet anyone sent.
            if (_packetSequence == 0 || _packetSequence == _lastProcessedSequence)
                return;

            _lastProcessedSequence = _packetSequence;

            // Keyed by the vehicle's published driver id, because that is what the simulation looks this
            // input up by. Falling back to the GameObject's owner is only for a recorder used without a
            // vehicle; during an ownership handover the two can disagree for a round trip, and filing
            // the packet under the wrong id hands one player's input to another player's car.
            int senderId = -1;
            if (_vehicle != null)
            {
                senderId = _vehicle.OwnerPlayerId;
            }
            else
            {
                VRCPlayerApi owner = Networking.GetOwner(gameObject);
                if (owner != null && owner.IsValid())
                    senderId = owner.playerId;
            }

            if (senderId < 0)
                return;

            World.ReceiveInput(senderId, _inputPacket);
        }
    }
}
