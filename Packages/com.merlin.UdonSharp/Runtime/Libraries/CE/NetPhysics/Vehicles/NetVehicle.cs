using JetBrains.Annotations;
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Rocket League-style networked vehicle controller with double jump, dodge/flip, and air control.
    /// Prioritizes stability and predictability over realism.
    /// </summary>
    [PublicAPI]
    public class NetVehicle : NetPhysicsEntity
    {
        [Header("Preset")]
        [SerializeField] private VehiclePreset _preset;

        [Header("Tuning Curves (optional)")]
        public AnimationCurve AccelerationCurve;
        public AnimationCurve BrakingCurve;
        public AnimationCurve SteeringCurve;
        public AnimationCurve LateralFrictionCurve;

        [Header("Forces")]
        public float MaxThrottle = 2000f;
        public float MaxBrake = 3000f;
        public float MaxSteering = 45f;
        public float MaxBoost = 5000f;
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
        public float GroundedThreshold = 0.2f;

        [Header("Boost")]
        public float MaxBoostSeconds = 2f;
        public float BoostRegenRate = 0f;

        private int _ownerPlayerId = -1;
        private float _boostRemaining;

        private bool _isGrounded;
        private int _groundedWheelCount;
        private Vector3 _groundNormal = Vector3.up;

        // Jump/Dodge state
        private bool _hasDoubleJump;
        private float _jumpTimer;
        private bool _jumpHeldLastFrame;
        private bool _dodgeUsed;

        public VehiclePreset Preset
        {
            get => _preset;
            set
            {
                _preset = value;
                if (_preset != null)
                    _preset.ApplyTo(this);
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

            _boostRemaining = MaxBoostSeconds;
            _hasDoubleJump = false;
            _jumpTimer = 0f;
            _dodgeUsed = false;
            if (_preset != null)
                _preset.ApplyTo(this);
        }

        /// <summary>
        /// Adds boost to the vehicle (e.g., from boost pads).
        /// </summary>
        public void AddBoost(float amount)
        {
            _boostRemaining = Mathf.Min(MaxBoostSeconds, _boostRemaining + amount);
        }

        /// <summary>
        /// Resets the vehicle state for respawn.
        /// </summary>
        public void ResetVehicle()
        {
            _boostRemaining = MaxBoostSeconds;
            _hasDoubleJump = false;
            _jumpTimer = 0f;
            _dodgeUsed = false;
            _isGrounded = false;
            _jumpHeldLastFrame = false;
        }

        public void SetOwner(VRCPlayerApi player)
        {
            _ownerPlayerId = player != null && player.IsValid() ? player.playerId : -1;
        }

        public int OwnerPlayerId => _ownerPlayerId;

        /// <summary>
        /// Applies an input frame to the vehicle. Call during the simulation tick.
        /// </summary>
        public void ApplyInput(InputFrame input)
        {
            Rigidbody rb = RigidbodyComponent;
            if (rb == null)
                return;

            UpdateGroundedState(rb);
            UpdateJumpTimer();

            if (_isGrounded)
            {
                ApplyThrottle(rb, input.Throttle / 127f);
                ApplySteering(rb, input.Steering / 127f);
                ApplyLateralFriction(rb);
                ApplyStabilityForces(rb);

                // Reset jump state when grounded
                ResetJumpState();
            }
            else
            {
                // Air control when not grounded
                ApplyAirControl(rb, input);
            }

            // Boost (works both grounded and airborne)
            if ((input.Buttons & InputFrame.BUTTON_BOOST) != 0 && input.Boost > 0)
            {
                ApplyBoost(rb, input.Boost / 255f);
            }
            else if (BoostRegenRate > 0f)
            {
                // Passive boost regeneration when not boosting
                _boostRemaining = Mathf.Min(MaxBoostSeconds, _boostRemaining + BoostRegenRate * Time.fixedDeltaTime);
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
        }

        public void SetVisualWheelPositions(Vector3[] positions) { }

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

            rb.AddForce(transform.forward * forceMagnitude, ForceMode.Acceleration);
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
            rb.AddTorque(transform.up * yawTorque, ForceMode.Acceleration);
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

        private void ApplyBoost(Rigidbody rb, float amount)
        {
            if (_boostRemaining <= 0f || amount <= 0f)
                return;

            rb.AddForce(transform.forward * (MaxBoost * amount), ForceMode.Acceleration);
            _boostRemaining = Mathf.Max(0f, _boostRemaining - Time.fixedDeltaTime * amount);
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
                _jumpTimer += Time.fixedDeltaTime;
            }
        }

        private void ResetJumpState()
        {
            _hasDoubleJump = false;
            _jumpTimer = 0f;
            _dodgeUsed = false;
        }

        private void UpdateGroundedState(Rigidbody rb)
        {
            _groundNormal = Vector3.up;
            _groundedWheelCount = 0;

            Ray ray = new Ray(transform.position + transform.up * 0.1f, -transform.up);
            if (Physics.Raycast(ray, out RaycastHit hit, GroundedThreshold))
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
