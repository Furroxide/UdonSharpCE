
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.Examples.Utilities
{
    /// <summary>
    /// Sets player movement modifiers on Start
    /// </summary>
    [AddComponentMenu("Udon Sharp/Utilities/Player Mod Setter")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class PlayerModSetter : UdonSharpBehaviour
    {
        public float jumpHeight = 3f;
        public float runSpeed = 4f;
        public float walkSpeed = 2f;
        public float strafeSpeed = 2f;
        public float gravity = 1f;
        
        [Tooltip("Enables legacy locomotion which allows stutter stepping and wall climbing")]
        public bool useLegacyLocomotion = false;

        void Start()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;
            if (localPlayer == null) return;

            localPlayer.SetJumpImpulse(jumpHeight);
            localPlayer.SetRunSpeed(runSpeed);
            localPlayer.SetWalkSpeed(walkSpeed);
            localPlayer.SetStrafeSpeed(strafeSpeed);
            localPlayer.SetGravityStrength(gravity);
        }
    }
}

























