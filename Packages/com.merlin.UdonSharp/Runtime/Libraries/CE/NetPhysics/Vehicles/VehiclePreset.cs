using JetBrains.Annotations;
using UnityEngine;

namespace UdonSharp.CE.NetPhysics
{
    /// <summary>
    /// Reusable physics preset for <see cref="NetVehicle"/> tuning.
    /// </summary>
    [PublicAPI]
    [CreateAssetMenu(fileName = "VehiclePreset", menuName = "CE/NetPhysics/VehiclePreset")]
    public class VehiclePreset : ScriptableObject
    {
        [Header("Dimensions")]
        public Vector3 CollisionSize = new Vector3(1.8f, 0.5f, 3.5f);
        public Vector3 CenterOfMass = new Vector3(0, -0.2f, 0.1f);

        [Header("Performance")]
        public float Mass = 180f;
        public float MaxSpeed = 30f;
        public float AccelerationTime = 2f;
        public float BrakeTime = 1f;

        [Header("Handling")]
        public float TurnRadius = 8f;
        public float DriftFriction = 0.3f;
        public float GripFriction = 0.95f;

        [Header("Aerial")]
        public float JumpForce = 800f;

        public void ApplyTo(NetVehicle vehicle)
        {
            if (vehicle == null)
                return;

            var box = vehicle.GetComponent<BoxCollider>();
            if (box != null)
                box.size = CollisionSize;

            var rb = vehicle.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.mass = Mass;
                rb.centerOfMass = CenterOfMass;
            }

            vehicle.AccelerationCurve = GenerateAccelCurve();
            vehicle.BrakingCurve = GenerateBrakeCurve();
            vehicle.SteeringCurve = GenerateSteeringCurve();
            vehicle.LateralFrictionCurve = GenerateFrictionCurve();

            float accelTime = Mathf.Max(0.1f, AccelerationTime);
            float brakeTime = Mathf.Max(0.1f, BrakeTime);

            vehicle.MaxThrottle = Mass * MaxSpeed / accelTime;
            vehicle.MaxBrake = Mass * MaxSpeed / brakeTime;
            vehicle.JumpImpulse = JumpForce;
        }

        private AnimationCurve GenerateAccelCurve()
        {
            return AnimationCurve.Linear(0f, 1f, MaxSpeed, 0.2f);
        }

        private AnimationCurve GenerateBrakeCurve()
        {
            return AnimationCurve.Linear(0f, 1f, MaxSpeed, 1f);
        }

        private AnimationCurve GenerateSteeringCurve()
        {
            // High speed => reduced steering.
            return AnimationCurve.Linear(0f, 1f, MaxSpeed, 0.3f);
        }

        private AnimationCurve GenerateFrictionCurve()
        {
            // Ratio closer to 1 => more sideways => lower friction.
            var curve = new AnimationCurve();
            curve.AddKey(0f, GripFriction);
            curve.AddKey(1f, DriftFriction);
            return curve;
        }
    }
}

