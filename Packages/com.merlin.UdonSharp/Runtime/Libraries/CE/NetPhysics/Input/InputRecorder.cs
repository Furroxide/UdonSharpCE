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
        [Tooltip("When off, the recorder keeps recording and publishing but reports neutral input, as if nothing were pressed. For a driver who should not be steering right now, e.g. one standing outside the arena.")]
        public bool InputEnabled = true;
        [Tooltip("Fewest input frames carried by each packet. A packet carries more than this whenever the send interval needs it: enough to repeat everything the previous packet held, so a single dropped packet does not leave a hole in the receiver's buffer.")]
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
        private int _lastRecordedFrame;
        private float _lastSendTime;
        private int _lastProcessedSequence;

        private InputFrame _currentInput;
        private NetVehicle _vehicle;

        // VRChat's own framing for the synced byte[] and the sequence int on top of the payload. Only
        // used to size the packet against the bandwidth budget, so an estimate is enough.
        private const int PacketOverheadBytes = 12;

        // Frames kept for resending. A packet never carries more than it holds, and the count travels
        // in a single byte, so this must stay at or below 255.
        private const int RecentInputCapacity = 64;
        private const int MaxPacketFrameCount = 255;

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

            // Sized for the longest send interval, not for RedundantFrames: a ring that only held one
            // packet's nominal worth had already overwritten the frames a slower send rate needed.
            int size = Mathf.Clamp(RedundantFrames, RecentInputCapacity, MaxPacketFrameCount);
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

            ComposeInput(World.CurrentFrame, Time.time);
        }

        /// <summary>
        /// Builds the current input frame from the latest input callbacks, stamped with the frame it is
        /// for. Only reached on the client driving this car.
        /// </summary>
        private void ComposeInput(int frameNumber, float time)
        {
            // Switched off, the driver still has a car in the simulation and every other client is
            // still predicting it from this stream, so the stream has to keep flowing: going quiet
            // would leave the authority repeating whatever was held last. Neutral input is published
            // instead, stamped like any other frame.
            if (!InputEnabled)
            {
                ClearCurrentInput();
                _currentInput.FrameNumber = frameNumber;
                _currentInput.Timestamp = time;
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

            _currentInput.FrameNumber = frameNumber;
            _currentInput.Timestamp = time;
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

            // Store into ring buffer, one entry per simulation frame. The receiver files input by the
            // frame it is stamped with and keeps one per frame, so a second sample carrying the same
            // stamp -- two physics steps with no simulation tick or no rendered frame between them --
            // replaces the first rather than spending a ring slot and ten bytes of every packet that
            // repeats it on something the receiver throws away.
            if (_recordedCount > 0 && _currentInput.FrameNumber == _lastRecordedFrame)
            {
                int newest = (_head - 1 + _recentInputs.Length) % _recentInputs.Length;
                _recentInputs[newest].CopyFrom(_currentInput);
            }
            else
            {
                _recentInputs[_head].CopyFrom(_currentInput);
                _head = (_head + 1) % _recentInputs.Length;
                _recordedCount = Mathf.Min(_recordedCount + 1, _recentInputs.Length);
                _lastRecordedFrame = _currentInput.FrameNumber;
            }

            // Recording is local and free; publishing is neither. Ownership can lag the assignment by a
            // round trip, and a write from a client that does not own the object is dropped silently.
            if (!CanPublish)
                return;

            float interval = GetSendInterval();
            if (interval <= 0f)
                return;

            if (Time.time - _lastSendTime < interval)
                return;

            SendInputPacket(interval);
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

        /// <summary>
        /// How many of the most recent input frames a packet has to carry.
        /// </summary>
        /// <remarks>
        /// This used to be a fixed count, which only overlaps the previous packet while the send
        /// interval stays short. The interval is not fixed: it stretches as more cars share the input
        /// budget, and at the floor rate a fixed ten frames covered exactly what had been recorded
        /// since the last send, so one dropped packet lost ten frames of input outright -- and a client
        /// recording faster than that lost frames with nothing dropped at all. The count is therefore
        /// derived from time.
        ///
        /// Everything recorded since the previous send is required: without it the receiver has a hole
        /// on a perfect link. The same again is carried, so that any single lost packet is covered by
        /// the next one. Neither part gives way to the byte budget. The budget has already set the
        /// interval, and it is a share this world allots itself, not VRChat's own limit; trimming the
        /// overlap to fit it would strip the redundancy from a busy instance, which is where
        /// serializations start being superseded before they are sent and the overlap is what covers
        /// the gap.
        /// </remarks>
        /// <param name="sendIntervalSeconds">Seconds between packets.</param>
        /// <param name="recordIntervalSeconds">Shortest time between two recorded frames.</param>
        /// <param name="minimumFrames">Fewest frames to carry however short the interval.</param>
        /// <param name="capacity">Most frames available to send.</param>
        public static int CalculatePacketFrameCount(float sendIntervalSeconds, float recordIntervalSeconds, int minimumFrames, int capacity)
        {
            if (recordIntervalSeconds <= 0f)
                recordIntervalSeconds = 0.001f;
            if (sendIntervalSeconds < 0f)
                sendIntervalSeconds = 0f;

            // The send test runs once per recorded frame, so a send can land one frame late: hence the
            // extra one. The small bias keeps an interval that is an exact multiple of the record
            // interval from rounding up a second time on float error.
            float recordsPerSend = sendIntervalSeconds / recordIntervalSeconds;
            if (recordsPerSend > capacity)
                recordsPerSend = capacity;

            int required = Mathf.CeilToInt(recordsPerSend - 0.001f) + 1;

            int count = required * 2;
            if (count < minimumFrames)
                count = minimumFrames;
            if (count > capacity)
                count = capacity;

            return count < 1 ? 1 : count;
        }

        /// <summary>
        /// Frames the next packet should carry at the given send interval, before it is limited to what
        /// has actually been recorded.
        /// </summary>
        private int GetPacketFrameCount(float interval)
        {
            int capacity = _recentInputs.Length;
            if (capacity > MaxPacketFrameCount)
                capacity = MaxPacketFrameCount;

            // Frames are recorded once per physics step and at most once per simulation frame, so the
            // slower of the two is how quickly they can accumulate.
            float recordInterval = Time.fixedDeltaTime;
            if (World != null && World.TickRate > 0)
            {
                float tickDelta = 1f / World.TickRate;
                if (tickDelta > recordInterval)
                    recordInterval = tickDelta;
            }

            return CalculatePacketFrameCount(interval, recordInterval, RedundantFrames, capacity);
        }

        private void SendInputPacket(float interval)
        {
            int count = Mathf.Min(GetPacketFrameCount(interval), _recordedCount);
            if (count < 1)
                count = 1;

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

            // The owner gets no OnDeserialization for its own serialization, so a recorder used on its own
            // hands the packet to the local world directly. On a vehicle that would be wasted work: the
            // world reads the local driver's input straight from this recorder every tick and never
            // takes it from the buffer, so on the authority the looped-back frames filled the driver's
            // buffer slot to the brim and were shuffled along one by one, forever, to be read by nobody.
            if (_vehicle == null)
            {
                VRCPlayerApi local = Networking.LocalPlayer;
                if (local != null && local.IsValid())
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
