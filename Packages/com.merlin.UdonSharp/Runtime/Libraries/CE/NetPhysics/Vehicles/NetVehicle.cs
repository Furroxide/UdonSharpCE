using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Rocket League-style networked vehicle controller with double jump, dodge/flip, and air control.
    /// Prioritizes stability and predictability over realism.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class NetVehicle : NetPhysicsEntity
    {
        [Header("Preset")]
        [SerializeField] private VehiclePreset _preset;

        [Header("Tuning Curves (optional)")]
        public AnimationCurve AccelerationCurve;
        public AnimationCurve BrakingCurve;
        public AnimationCurve SteeringCurve;
        public AnimationCurve LateralFrictionCurve;

        // Throttle, brake and boost are forces in newtons and the jump and dodge values are impulses in
        // newton-seconds: all of them are divided by the Rigidbody's mass, which is how VehiclePreset
        // derives them (mass * top speed / time to reach it). Throttle, boost and the steering torque
        // used to be applied as accelerations instead, which ignores mass -- on the 180 kg body these
        // defaults are sized for, 2000 was meant to be about 11 m/s^2 and was 2000 m/s^2.
        [Header("Forces")]
        [Tooltip("Peak drive force in newtons.")]
        public float MaxThrottle = 2000f;
        [Tooltip("Peak braking / reverse force in newtons.")]
        public float MaxBrake = 3000f;
        [Tooltip("Scales the steering torque: full lock applies ten times this, in newton-metres.")]
        public float MaxSteering = 45f;
        [Tooltip("Boost force in newtons.")]
        public float MaxBoost = 5000f;
        [Tooltip("Jump impulse in newton-seconds.")]
        public float JumpImpulse = 500f;

        [Header("Dodge/Flip")]
        public float DodgeImpulse = 600f;
        public float DodgeSpinTorque = 500f;
        public float DoubleJumpWindow = 1.5f;
        public float DoubleJumpImpulse = 400f;

        [Header("Air Control")]
        public float AirPitchTorque = 15f;
        public float AirYawTorque = 10f;
        public float AirRollTorque = 20f;

        [Header("Stability")]
        public float StabilityRollTorque = 50f;
        public float StabilityDownForce = 100f;
        [Tooltip("How far below the underside of the collider the ground may be for the car to count as grounded, in metres.")]
        public float GroundedThreshold = 0.2f;

        [Header("Boost")]
        public float MaxBoostSeconds = 2f;
        public float BoostRegenRate = 0f;

        [Header("Input")]
        [Tooltip("Recorder that captures this vehicle's driver input. Found on this GameObject when left empty.")]
        [SerializeField] private InputRecorder _recorder;

        [Header("Sync")]
        [Tooltip("Owner-side publish rate in Hz for every synced field: boost, the dodge window, the air-state flags and the driver id. Only sends when one of them actually changed. Boost, the dodge window and the driver id are predicted on every client, so this cadence is what pulls those guesses back onto the owner's values; 0 stops the send entirely.")]
        public float StateSendRate = 5f;

        // Every one of these decides how a remote client simulates this car, so every one of them has
        // to travel. Without them a remote car boosted forever, dodged twice, and -- because the driver
        // id never left the master -- was reported as somebody else's car even to its own driver.
        //
        // Authority records who may write a field, not where the local copy of the value came from.
        // NetPhysicsWorld runs TickInput for every vehicle on every client, so boost and the dodge
        // window are integrated by all of them; the driver id is written by all of them too, applied
        // from the AssignDriver broadcast (see LatchDriverId) so input routing starts on the frame the
        // assignment is heard instead of stalling until the owner's next serialization arrives. That the
        // driver id reaches its write from a packet and the other two from integration says nothing
        // about who performs the write, so all three are Predicted: the unguarded local write is the
        // design, and the owner's send is there to bound the copies rather than to produce them.
        // _airStateFlags is the only one that is genuinely owner-written -- its single write is in
        // OnPreSerialization, which runs on the client that is serializing and nowhere else -- so it
        // stays Owner and CE0014 keeps checking that no other path starts writing it.
        [Sync(Authority.Predicted)] private int _ownerPlayerId = -1;
        [Sync(Authority.Predicted)] private float _boostRemaining;
        [Sync(Authority.Predicted)] private float _jumpTimer;
        [Sync(Authority.Owner)] private byte _airStateFlags;

        // Only the bits a remote client cannot work out for itself are carried. Grounded used to sit
        // here too and it was pure wire cost: UpdateGroundedState raycasts it at the top of every
        // TickInput, and NetPhysicsWorld runs TickInput for every vehicle on every client at the tick
        // rate, so a received grounded bit was overwritten before anything could read it. These two are
        // different -- while a car is airborne nothing local recomputes them, so without them a remote
        // client kept offering a second dodge the driver had already spent.
        private const byte FLAG_DOUBLE_JUMP = 1;
        private const byte FLAG_DODGE_USED = 2;

        private float _lastStateSendTime;
        private byte _lastSentAirStateFlags;
        private float _lastSentBoostRemaining;
        private float _lastSentJumpTimer;
        private int _lastSentOwnerPlayerId = -1;
        private bool _stateSendFailed;

        // Last driver assignment this client saw, held until this client is in a position to publish it.
        // Ownership and the assignment arrive independently and in either order, so the assignment has
        // to survive arriving first.
        private int _pendingDriverId = -1;
        private bool _hasPendingDriverId;

        // Simulated time this TickInput call is allowed to integrate. Zero for a frame already integrated.
        private int _lastIntegratedFrame;
        private bool _hasIntegratedFrame;
        private float _integrationSeconds;

        private bool _isGrounded;
        private int _groundedWheelCount;
        private Vector3 _groundNormal = Vector3.up;

        // Where the ground ray starts, in local space, and how far it has to travel just to get out of
        // the vehicle's own collider. See RefreshGroundProbe.
        private Vector3 _groundProbeCenter;
        private float _groundProbeHalfHeight;
        private bool _groundProbeReady;

        // Jump/Dodge state
        private bool _hasDoubleJump;
        private bool _jumpHeldLastFrame;
        private bool _dodgeUsed;

        // Set by a jump and held until the car has either left the ground ray's reach or stopped
        // rising. See UpdateGroundedState.
        private bool _takingOff;
        private int _takeOffStep;

        // Speed away from the ground, in m/s, above which a car that has just jumped is still on its
        // way up rather than back down.
        private const float TakeOffSpeed = 0.1f;

        // What ApplyForces acts on: the newest tick's input and the share of boost that tick was
        // allowed to burn. Copied, because the frames handed to TickInput are scratch objects their
        // owners overwrite.
        private InputFrame _forceInput;
        private float _boostThrust;

        // Physics steps this vehicle has been stepped through, counted by ApplyForces.
        private int _physicsSteps;

        public VehiclePreset Preset
        {
            get => _preset;
            set
            {
                _preset = value;
                if (_preset != null)
                    _preset.ApplyTo(this);

                // A preset resizes the collider the ground ray is measured from.
                RefreshGroundProbe();
            }
        }

        public override EntityType EntityType
        {
            get
            {
                VRCPlayerApi local = Networking.LocalPlayer;
                if (local != null && local.IsValid() && local.playerId == _ownerPlayerId)
                    return EntityType.LocalVehicle;
                return EntityType.OtherVehicle;
            }
        }

        /// <summary>
        /// Recorder supplying this vehicle's driver input. One per vehicle: a single shared recorder
        /// meant every client sampled the same object, so only one of them could own it and publish.
        /// </summary>
        public InputRecorder Recorder => _recorder;

        public bool IsGrounded => _isGrounded;
        public int GroundedWheelCount => _groundedWheelCount;
        public float BoostAmount => MaxBoostSeconds > 0f ? Mathf.Clamp01(_boostRemaining / MaxBoostSeconds) : 0f;
        public bool CanJump => _isGrounded || (_hasDoubleJump && _jumpTimer < DoubleJumpWindow);
        public bool CanDodge => !_isGrounded && _hasDoubleJump && !_dodgeUsed && _jumpTimer < DoubleJumpWindow;
        public bool HasDoubleJump => _hasDoubleJump;
        public float JumpTimer => _jumpTimer;

        protected override void Start()
        {
            base.Start();

            if (_recorder == null)
                _recorder = GetComponent<InputRecorder>();

            // Only the owner seeds the synced state. A late joiner's copy can already hold the owner's
            // values by now: VRChat writes received values into the program before Start, although it
            // does not run OnDeserialization until Start has. Seeding them on every client replaces the
            // boost level and dodge timer that client had already been told about with a full tank and
            // a fresh window it had not earned, which is what resetting the timer here for everybody
            // used to do.
            if (Networking.IsOwner(gameObject))
            {
                _boostRemaining = MaxBoostSeconds;
                _jumpTimer = 0f;
            }

            // For the same reason the air state cannot wait for OnDeserialization: a packet that landed
            // before Start never ran it, and clearing the state here instead left a late joiner's copy
            // of an airborne car without the double jump its driver still held, so that copy could not
            // follow the driver's dodge. Where nothing has arrived the flags are clear, so this still
            // starts the car with neither.
            ApplyAirStateFlags();

            _lastStateSendTime = Time.time;
            if (_preset != null)
                _preset.ApplyTo(this);

            RefreshGroundProbe();
        }

        protected override void FixedUpdate()
        {
            base.FixedUpdate();

            if (StateSendRate <= 0f || !Networking.IsOwner(gameObject))
                return;

            if (Time.time - _lastStateSendTime < 1f / StateSendRate)
                return;

            _lastStateSendTime = Time.time;

            // Serializing on a fixed cadence regardless of change would spend a slice of the instance's
            // ~11 KB/s on a parked car, so only a real change is worth a packet. A dropped serialization
            // counts as a change: the trackers below were advanced for a packet that never arrived.
            byte flags = PackAirState();
            if (!_stateSendFailed &&
                flags == _lastSentAirStateFlags &&
                _ownerPlayerId == _lastSentOwnerPlayerId &&
                Mathf.Abs(_boostRemaining - _lastSentBoostRemaining) < 0.01f &&
                Mathf.Abs(_jumpTimer - _lastSentJumpTimer) < 0.02f)
                return;

            MarkStatePublished(flags);
            RequestSerialization();
        }

        public override void OnPreSerialization()
        {
            _airStateFlags = PackAirState();
        }

        public override void OnPostSerialization(SerializationResult result)
        {
            // A dropped serialization was invisible here: the trackers had already advanced, so the
            // change test above never fired again and every other client kept simulating this car from
            // whatever the last packet that did arrive said -- including a driver id that may have been
            // the reason the packet was sent. Re-arming lets the next due send republish; the send-rate
            // gate above still decides when that is.
            _stateSendFailed = !result.success;
        }

        public override void OnDeserialization()
        {
            // This is the reconciliation half of the Predicted fields: remote clients simulate this car
            // from decayed predicted input, which drifts, and the owner's boost and jump state arriving
            // here is the only thing that stops a remote car boosting indefinitely or being offered a
            // second dodge it never had. Grounded is deliberately not read from the packet -- see the
            // flag constants for why it is not sent. The driver id is predicted too but needs no line
            // here: nothing derives a second local copy from it, so the field being overwritten by the
            // owner's value is the whole of its reconciliation.
            ApplyAirStateFlags();
        }

        /// <summary>
        /// Derives the local air state from the synced flags. Run from OnDeserialization and from Start,
        /// since VRChat only runs the former once the latter has.
        /// </summary>
        private void ApplyAirStateFlags()
        {
            _hasDoubleJump = (_airStateFlags & FLAG_DOUBLE_JUMP) != 0;
            _dodgeUsed = (_airStateFlags & FLAG_DODGE_USED) != 0;
        }

        private byte PackAirState()
        {
            byte flags = 0;
            if (_hasDoubleJump)
                flags |= FLAG_DOUBLE_JUMP;
            if (_dodgeUsed)
                flags |= FLAG_DODGE_USED;
            return flags;
        }

        /// <summary>
        /// Records what the packet about to go out contains, so the change test above does not resend it.
        /// </summary>
        private void MarkStatePublished(byte flags)
        {
            _lastSentAirStateFlags = flags;
            _lastSentBoostRemaining = _boostRemaining;
            _lastSentJumpTimer = _jumpTimer;
            _lastSentOwnerPlayerId = _ownerPlayerId;
            _stateSendFailed = false;
        }

        /// <summary>
        /// Adds boost to the vehicle (e.g., from boost pads).
        /// </summary>
        /// <remarks>
        /// A grant is not a prediction. <c>_boostRemaining</c> is <c>Authority.Predicted</c> because
        /// every client drains it from input they all have, but a pad grant is new information no other
        /// client can derive, so it has to happen on the owner or it never reaches the wire. Boost pads
        /// run their trigger on the master, which owns the pad and not the car it just fed, so writing
        /// the field here directly played the pickup effects and granted nothing.
        /// </remarks>
        public void AddBoost(float amount)
        {
            if (amount <= 0f)
                return;

            if (Networking.IsOwner(gameObject))
            {
                ApplyBoostGrant(amount);
                return;
            }

            // Ownership does not move while a car is being driven, so the Owner target resolves to the
            // driver and the grant lands on the one client whose write survives.
            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(GrantBoost), amount);
        }

        /// <summary>
        /// Network entry point for <see cref="AddBoost"/> called from a client that does not own this
        /// vehicle. Runs on the owner.
        /// </summary>
        [NetworkCallable(10)]
        public void GrantBoost(float amount)
        {
            if (amount <= 0f || !Networking.IsOwner(gameObject))
                return;

            ApplyBoostGrant(amount);
        }

        private void ApplyBoostGrant(float amount)
        {
            float granted = Mathf.Min(MaxBoostSeconds, _boostRemaining + amount);
            if (granted <= _boostRemaining)
                return;

            _boostRemaining = granted;

            // A pad grant is a discrete event, not a value the periodic gate can be trusted to notice
            // in time: the driver can spend it again before the next scheduled send would have run.
            MarkStatePublished(PackAirState());
            RequestSerialization();
        }

        /// <summary>
        /// Resets the vehicle state for respawn.
        /// </summary>
        /// <remarks>
        /// Same ownership problem as <see cref="AddBoost"/>: the spawner respawns cars from the master,
        /// which is not the driver that owns them, so this used to clear the tank on the master's local
        /// copy only and leave every other client -- including the driver -- on the old value.
        /// </remarks>
        public void ResetVehicle()
        {
            if (Networking.IsOwner(gameObject))
            {
                ApplyVehicleReset();
                return;
            }

            SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ApplyVehicleReset));
        }

        /// <summary>
        /// Network entry point for <see cref="ResetVehicle"/>. Runs on the owner.
        /// </summary>
        public void ApplyVehicleReset()
        {
            if (!Networking.IsOwner(gameObject))
                return;

            _boostRemaining = MaxBoostSeconds;
            _hasDoubleJump = false;
            _jumpTimer = 0f;
            _dodgeUsed = false;
            _isGrounded = false;
            _jumpHeldLastFrame = false;
            _takingOff = false;

            MarkStatePublished(PackAirState());
            RequestSerialization();
        }

        /// <summary>
        /// Records which player drives this vehicle. Safe to call from any client.
        /// </summary>
        /// <remarks>
        /// The driver id is a synced field, so only the object's owner can publish it, and the client
        /// that decides the assignment is almost never that owner: the spawner runs on the master and
        /// hands ownership to the driver one instruction before naming them. What this used to do --
        /// write the field and fire a NetworkEventTarget.Owner event asking the new owner to publish --
        /// lost a race whenever the ownership transfer had not landed on the driver's client yet: the
        /// event's own ownership guard dropped it, nothing was ever published, and the car stayed
        /// driverless on every client but the master, which silently killed all input for it.
        ///
        /// So the assignment is latched locally on every client that hears it and published by whoever
        /// turns out to own the object, now or as soon as ownership arrives. Which client runs this
        /// method no longer matters.
        /// </remarks>
        public void SetOwner(VRCPlayerApi player)
        {
            int driverId = player != null && player.IsValid() ? player.playerId : -1;

            LatchDriverId(driverId);

            if (Networking.IsOwner(gameObject))
            {
                PublishDriverId();
                return;
            }

            // Only the master originates an assignment. A client mirroring a decision it was told about
            // -- which is what a spawner replaying its synced slot table on every client is doing --
            // must not rebroadcast it, or one assignment becomes one event per player per player.
            if (Networking.IsMaster)
                SendCustomNetworkEvent(NetworkEventTarget.All, nameof(AssignDriver), driverId);
        }

        /// <summary>
        /// Network entry point for <see cref="SetOwner"/>. Runs on every client, including the sender.
        /// </summary>
        [NetworkCallable(5)]
        public void AssignDriver(int driverId)
        {
            // CallingPlayer is null for a local call and the sender otherwise. Assignments come from the
            // master; accepting one from anybody else would let any client put any player in any car.
            VRCPlayerApi caller = NetworkCalling.CallingPlayer;
            if (caller != null && caller.IsValid() && !caller.isMaster)
                return;

            LatchDriverId(driverId);
            PublishDriverId();
        }

        /// <summary>
        /// Applies an assignment locally and remembers it until this client can publish it.
        /// </summary>
        private void LatchDriverId(int driverId)
        {
            _pendingDriverId = driverId;
            _hasPendingDriverId = true;

            // Applied on every client that hears the assignment, not only on the one that will publish
            // it, so the simulation can start routing this car's input on the same frame instead of
            // stalling until the owner's next serialization arrives. The local value is an echo of a
            // broadcast every client received rather than an independent guess, and PublishDriverId
            // re-sends it from the owner, which is what the copies converge on -- apply locally, owner
            // reconciles, which is what the field's Predicted declaration says.
            _ownerPlayerId = driverId;
        }

        /// <summary>
        /// Publishes the latched driver assignment if this client owns the vehicle. Cheap and idempotent,
        /// so it is safe to call from every path that could have made this client the owner.
        /// </summary>
        public void PublishDriverId()
        {
            if (!_hasPendingDriverId || !Networking.IsOwner(gameObject))
                return;

            _hasPendingDriverId = false;
            _ownerPlayerId = _pendingDriverId;

            MarkStatePublished(PackAirState());
            RequestSerialization();
        }

        public override void OnOwnershipTransferred(VRCPlayerApi player)
        {
            // This is the other half of the race described on SetOwner: the assignment can arrive before
            // ownership does, in which case this is the moment the new owner is finally able to publish
            // what it was told.
            PublishDriverId();
        }

        public override void OnPlayerLeft(VRCPlayerApi player)
        {
            if (player == null || _ownerPlayerId < 0 || player.playerId != _ownerPlayerId)
                return;

            // Every client can work this out for itself, so no packet is needed to agree on it. Leaving
            // the departed driver's id in place would have every client keep feeding predicted input
            // into a car nobody is driving. The latch means that if this client is not the owner today
            // but inherits the orphaned object later, it still publishes the release.
            LatchDriverId(-1);
            PublishDriverId();
        }

        public int OwnerPlayerId => _ownerPlayerId;

        /// <summary>
        /// Runs one simulation tick and one physics step's worth of forces. For a caller that steps
        /// the vehicle itself, exactly once per FixedUpdate; <see cref="NetPhysicsWorld"/> does not use
        /// it, because its ticks and its physics steps are not one-to-one -- it calls
        /// <see cref="TickInput"/> per tick and <see cref="ApplyForces"/> per physics step.
        /// </summary>
        public void ApplyInput(InputFrame input)
        {
            TickInput(input);
            ApplyForces();
        }

        /// <summary>
        /// The per-tick half of the simulation: grounding, the jump timer, the boost tank, and the
        /// jump, double-jump and dodge edges. Call once per simulation frame.
        /// </summary>
        /// <remarks>
        /// Thrust, steering, friction and air control are deliberately not here. Simulation ticks run
        /// at the tick rate against the shared clock; PhysX integrates once per FixedUpdate. The two
        /// rates differ, so a FixedUpdate sees zero, one, two or three ticks, and a continuous force
        /// added once per tick was integrated that many times in a single physics step: no drive at all
        /// on some steps, double or triple on others, 20% too much on average at the default rates,
        /// and a friction term strong enough to reverse the car's sideways velocity whenever two ticks
        /// landed together. Everything here is either an event (an impulse is owed once however many
        /// steps there are) or integrates simulated time itself.
        /// </remarks>
        public void TickInput(InputFrame input)
        {
            if (input == null)
                input = InputFrame.Empty;

            Rigidbody rb = RigidbodyComponent;
            if (rb == null)
                return;

            BeginSimulationStep();

            UpdateGroundedState(rb);
            UpdateJumpTimer();

            // Reset jump state when grounded. A car that has just jumped does not count as grounded
            // while it is still on its way up -- see UpdateGroundedState.
            if (_isGrounded)
                ResetJumpState();

            // Boost (works both grounded and airborne). The tank is spent here, per tick; the thrust it
            // pays for is applied by ApplyForces.
            _boostThrust = 0f;
            if ((input.Buttons & InputFrame.BUTTON_BOOST) != 0 && input.Boost > 0)
            {
                if (_boostRemaining > 0f)
                {
                    float amount = input.Boost / 255f;
                    _boostThrust = amount;

                    // Drained on every client, not just the owner: that is what Predicted buys, a meter
                    // that keeps moving between packets instead of stepping once per serialization.
                    _boostRemaining = Mathf.Max(0f, _boostRemaining - _integrationSeconds * amount);
                }
            }
            else if (BoostRegenRate > 0f)
            {
                // Passive boost regeneration when not boosting, run on every client -- this is what
                // Predicted declares, and the owner's packet is what keeps the tanks agreeing.
                _boostRemaining = Mathf.Min(MaxBoostSeconds, _boostRemaining + BoostRegenRate * _integrationSeconds);
            }

            // Jump handling with edge detection
            bool jumpPressed = (input.Buttons & InputFrame.BUTTON_JUMP) != 0;
            bool jumpJustPressed = jumpPressed && !_jumpHeldLastFrame;

            if (jumpJustPressed)
            {
                if (_isGrounded)
                {
                    ApplyJump(rb);
                }
                else if (CanDodge && input.IsDodging)
                {
                    ApplyDodge(rb, input.DodgeX, input.DodgeY);
                }
                else if (_hasDoubleJump && _jumpTimer < DoubleJumpWindow)
                {
                    ApplyDoubleJump(rb);
                }
            }

            _jumpHeldLastFrame = jumpPressed;

            if (_forceInput == null)
                _forceInput = new InputFrame();
            _forceInput.CopyFrom(input);
        }

        /// <summary>
        /// The per-physics-step half of the simulation: thrust, steering, friction, stability and air
        /// control, from the input of the most recent <see cref="TickInput"/>. Call exactly once per
        /// FixedUpdate, after that step's ticks; a FixedUpdate with no tick still needs it, since the
        /// driver is still holding the throttle.
        /// </summary>
        public void ApplyForces()
        {
            Rigidbody rb = RigidbodyComponent;
            if (rb == null)
                return;

            if (_forceInput != null)
            {
                if (_isGrounded)
                {
                    ApplyThrottle(rb, _forceInput.Throttle / 127f);
                    ApplySteering(rb, _forceInput.Steering / 127f);
                    ApplyLateralFriction(rb);
                    ApplyStabilityForces(rb);
                }
                else
                {
                    // Air control when not grounded
                    ApplyAirControl(rb, _forceInput);
                }

                if (_boostThrust > 0f)
                    rb.AddForce(transform.forward * (MaxBoost * _boostThrust), ForceMode.Force);
            }

            // Counted last: a jump stamps itself with the count as it stood during the tick, and the
            // count moving on is how UpdateGroundedState knows that jump's impulse has been integrated.
            _physicsSteps++;
        }

        public void SetVisualWheelPositions(Vector3[] positions) { }

        /// <summary>
        /// Works out how much simulated time this <see cref="TickInput"/> call may integrate.
        /// </summary>
        /// <remarks>
        /// Integration is driven by the simulated frame number, not by the wall clock or by the number
        /// of calls: the boost tank and the double-jump timer are synced, so a value spent twice for
        /// one frame is not a local glitch but something the owner then publishes for every other
        /// client to believe. A frame that has already been integrated contributes nothing.
        /// </remarks>
        private void BeginSimulationStep()
        {
            int frame = World != null ? World.CurrentFrame : _lastIntegratedFrame + 1;
            float tickDelta = World != null && World.TickRate > 0 ? 1f / World.TickRate : Time.fixedDeltaTime;

            if (!_hasIntegratedFrame)
            {
                _hasIntegratedFrame = true;
                _lastIntegratedFrame = frame;
                _integrationSeconds = tickDelta;
                return;
            }

            int steps = frame - _lastIntegratedFrame;

            if (steps == 0)
            {
                // The same frame again. Nothing may be spent or aged twice.
                _integrationSeconds = 0f;
                return;
            }

            if (steps < 0)
            {
                // The frame counter only ever moves backwards when the world re-anchors it to the server
                // clock. Re-anchor with it. This used to be taken for a replayed frame unless the jump
                // was longer than the whole history window, so a correction of anything from twenty
                // frames to a couple of seconds froze the boost drain and the jump timer until the
                // counter had climbed back to where it was -- free boost for as long as that took.
                _lastIntegratedFrame = frame;
                _integrationSeconds = tickDelta;
                return;
            }

            // A frame gap this side of the resync threshold is a stall the world already capped when it
            // decided how many ticks to run; capping here the same way keeps a hitch from draining a
            // full tank in one step.
            int maxSteps = World != null ? Mathf.Max(1, World.MaxTicksPerFrame) : 4;
            if (steps > maxSteps)
                steps = maxSteps;

            _lastIntegratedFrame = frame;
            _integrationSeconds = steps * tickDelta;
        }

        private void ApplyThrottle(Rigidbody rb, float throttle)
        {
            if (Mathf.Abs(throttle) < 0.001f)
                return;

            float forwardSpeed = Vector3.Dot(rb.velocity, transform.forward);
            float forceMagnitude;

            if (throttle > 0f)
            {
                float curve = AccelerationCurve != null && AccelerationCurve.length > 0
                    ? AccelerationCurve.Evaluate(Mathf.Abs(forwardSpeed))
                    : 1f;
                forceMagnitude = curve * MaxThrottle * throttle;
            }
            else
            {
                float curve = BrakingCurve != null && BrakingCurve.length > 0
                    ? BrakingCurve.Evaluate(Mathf.Abs(forwardSpeed))
                    : 1f;
                forceMagnitude = curve * MaxBrake * throttle;
            }

            rb.AddForce(transform.forward * forceMagnitude, ForceMode.Force);
        }

        private void ApplySteering(Rigidbody rb, float steering)
        {
            if (Mathf.Abs(steering) < 0.001f)
                return;

            float speed = rb.velocity.magnitude;
            if (speed < 0.5f)
                return;

            float curve = SteeringCurve != null && SteeringCurve.length > 0
                ? SteeringCurve.Evaluate(speed)
                : 1f;

            float maxAngle = curve * MaxSteering;
            float yawTorque = steering * maxAngle * 10f;
            rb.AddTorque(transform.up * yawTorque, ForceMode.Force);
        }

        private void ApplyLateralFriction(Rigidbody rb)
        {
            Vector3 localVel = transform.InverseTransformDirection(rb.velocity);
            float sideSpeed = Mathf.Abs(localVel.x);
            float forwardSpeed = Mathf.Abs(localVel.z);

            if (sideSpeed + forwardSpeed < 0.1f)
                return;

            float ratio = sideSpeed / (sideSpeed + forwardSpeed);
            float friction = LateralFrictionCurve != null && LateralFrictionCurve.length > 0
                ? LateralFrictionCurve.Evaluate(ratio)
                : 0.8f;

            Vector3 sideVelocity = transform.right * localVel.x;
            Vector3 frictionForce = -sideVelocity * friction * 50f;
            rb.AddForce(frictionForce, ForceMode.Acceleration);
        }

        private void ApplyStabilityForces(Rigidbody rb)
        {
            if (_groundedWheelCount == 4)
                return;

            if (_groundedWheelCount > 0)
            {
                Vector3 targetUp = _groundNormal;
                Vector3 currentUp = transform.up;
                Vector3 rollAxis = Vector3.Cross(currentUp, targetUp);
                float rollAngle = Vector3.Angle(currentUp, targetUp);

                if (rollAngle > 1f)
                    rb.AddTorque(rollAxis * rollAngle * StabilityRollTorque, ForceMode.Acceleration);

                rb.AddForce(-_groundNormal * StabilityDownForce, ForceMode.Acceleration);
            }
        }

        private void ApplyJump(Rigidbody rb)
        {
            if (!_isGrounded)
                return;

            rb.AddForce(transform.up * JumpImpulse, ForceMode.Impulse);

            // Start jump timer and enable double jump
            _hasDoubleJump = true;
            _jumpTimer = 0f;
            _dodgeUsed = false;
            _isGrounded = false;
            _takingOff = true;
            _takeOffStep = _physicsSteps;
        }

        private void ApplyDoubleJump(Rigidbody rb)
        {
            if (!_hasDoubleJump || _jumpTimer >= DoubleJumpWindow)
                return;

            rb.AddForce(transform.up * DoubleJumpImpulse, ForceMode.Impulse);
            _hasDoubleJump = false;
        }

        private void ApplyDodge(Rigidbody rb, sbyte dodgeX, sbyte dodgeY)
        {
            if (!_hasDoubleJump || _dodgeUsed)
                return;

            float dx = dodgeX / 127f;
            float dy = dodgeY / 127f;

            // Calculate dodge direction in world space
            Vector3 dodgeDir = transform.forward * dy + transform.right * dx;

            if (dodgeDir.sqrMagnitude < 0.01f)
            {
                // No direction specified - forward flip
                dodgeDir = transform.forward;
            }
            else
            {
                dodgeDir = dodgeDir.normalized;
            }

            // Apply dodge impulse
            rb.AddForce(dodgeDir * DodgeImpulse, ForceMode.Impulse);

            // Apply flip rotation (spin around the perpendicular axis)
            Vector3 spinAxis = Vector3.Cross(Vector3.up, dodgeDir);
            if (spinAxis.sqrMagnitude > 0.01f)
            {
                rb.AddTorque(spinAxis.normalized * DodgeSpinTorque, ForceMode.Impulse);
            }
            else
            {
                // Forward/back dodge - pitch rotation
                rb.AddTorque(transform.right * DodgeSpinTorque * -dy, ForceMode.Impulse);
            }

            _hasDoubleJump = false;
            _dodgeUsed = true;
        }

        private void ApplyAirControl(Rigidbody rb, InputFrame input)
        {
            // Pitch (forward/back on stick)
            float pitch = input.Throttle / 127f;
            if (Mathf.Abs(pitch) > 0.1f)
            {
                rb.AddTorque(transform.right * pitch * AirPitchTorque, ForceMode.Acceleration);
            }

            // Yaw (left/right on stick)
            float yaw = input.Steering / 127f;
            if (Mathf.Abs(yaw) > 0.1f)
            {
                rb.AddTorque(transform.up * yaw * AirYawTorque, ForceMode.Acceleration);
            }

            // Roll (using handbrake + steering for air roll)
            if ((input.Buttons & InputFrame.BUTTON_HANDBRAKE) != 0 && Mathf.Abs(yaw) > 0.1f)
            {
                rb.AddTorque(transform.forward * -yaw * AirRollTorque, ForceMode.Acceleration);
            }
        }

        private void UpdateJumpTimer()
        {
            if (!_isGrounded && _hasDoubleJump)
            {
                // Aged on every client, per Predicted, in simulated rather than wall-clock time: a frame
                // already integrated contributes nothing, so the dodge window cannot be aged twice for
                // one frame. See BeginSimulationStep.
                _jumpTimer += _integrationSeconds;
            }
        }

        private void ResetJumpState()
        {
            _hasDoubleJump = false;
            _jumpTimer = 0f;
            _dodgeUsed = false;
        }

        /// <summary>
        /// Measures the ground ray from the vehicle's own collider.
        /// </summary>
        /// <remarks>
        /// The ray used to start a tenth of a metre above the pivot and run for GroundedThreshold, which
        /// on the shipped car -- a unit box scaled to half a metre tall -- ended inside the car's own
        /// collider, a quarter of a metre short of the floor it was resting on. The car was never
        /// grounded, so throttle, steering and the jump were all unreachable. It now starts at the
        /// collider's centre, where Unity's raycast cannot see the collider it starts inside, and runs
        /// for the collider's half-height plus GroundedThreshold, so GroundedThreshold means what its
        /// tooltip says whatever the car's size and scale.
        /// </remarks>
        private void RefreshGroundProbe()
        {
            BoxCollider box = GetComponent<BoxCollider>();
            _groundProbeCenter = box != null ? box.center : Vector3.zero;
            _groundProbeHalfHeight = box != null ? box.size.y * 0.5f : 0.5f;
            _groundProbeReady = true;
        }

        private void UpdateGroundedState(Rigidbody rb)
        {
            if (!_groundProbeReady)
                RefreshGroundProbe();

            _groundNormal = Vector3.up;
            _groundedWheelCount = 0;

            // Triggers are ignored: a boost pad or a goal mouth is not something to drive on.
            float reach = transform.TransformVector(Vector3.up * _groundProbeHalfHeight).magnitude + GroundedThreshold;
            Ray ray = new Ray(transform.TransformPoint(_groundProbeCenter), -transform.up);
            bool hitGround = Physics.Raycast(ray, out RaycastHit hit, reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (_takingOff)
            {
                // A car that has just jumped is still within reach of the ground for several ticks, and
                // counting it as grounded there cleared the double jump and the dodge the jump had just
                // granted -- neither could ever be used. It stays airborne until it is out of reach or
                // has stopped moving away. A queued impulse has not changed the velocity until PhysX
                // takes its next step, so until then there is nothing to read.
                if (_physicsSteps == _takeOffStep || (hitGround && Vector3.Dot(rb.velocity, hit.normal) > TakeOffSpeed))
                {
                    _isGrounded = false;
                    return;
                }
                _takingOff = false;
            }

            if (hitGround)
            {
                _isGrounded = true;
                _groundNormal = hit.normal;
                _groundedWheelCount = 4;
            }
            else
            {
                _isGrounded = false;
            }
        }
    }
}
