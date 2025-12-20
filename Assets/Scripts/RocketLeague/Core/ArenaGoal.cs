using JetBrains.Annotations;
using UdonSharp;
using UdonSharp.CE.NetPhysics;
using UnityEngine;
using VRC.SDKBase;

namespace RocketLeague
{
    /// <summary>
    /// Goal detection trigger. Placed at each end of the arena.
    /// Team 0 = Blue goal (when ball enters, Orange scores)
    /// Team 1 = Orange goal (when ball enters, Blue scores)
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ArenaGoal : UdonSharpBehaviour
    {
        [Header("Configuration")]
        [Tooltip("0 = Blue goal, 1 = Orange goal")]
        public int Team;

        [Header("References")]
        public RocketLeagueManager Manager;

        [Header("Visual Effects")]
        public ParticleSystem GoalExplosion;
        public Light GoalLight;
        public AudioSource GoalSound;

        [Header("Goal Light Settings")]
        public Color BlueGoalColor = new Color(0.2f, 0.4f, 1f);
        public Color OrangeGoalColor = new Color(1f, 0.5f, 0.1f);
        public float LightFlashDuration = 2f;

        private float _lightTimer;
        private bool _lightActive;
        private float _baseLightIntensity;

        private void Start()
        {
            if (GoalLight != null)
            {
                _baseLightIntensity = GoalLight.intensity;
                GoalLight.intensity = 0f;
                GoalLight.color = Team == 0 ? OrangeGoalColor : BlueGoalColor;
            }
        }

        private void Update()
        {
            if (_lightActive && GoalLight != null)
            {
                _lightTimer -= Time.deltaTime;
                if (_lightTimer <= 0f)
                {
                    _lightActive = false;
                    GoalLight.intensity = 0f;
                }
                else
                {
                    float t = _lightTimer / LightFlashDuration;
                    GoalLight.intensity = _baseLightIntensity * t;
                }
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (other == null)
                return;

            // Only the master handles goal detection to prevent duplicate scoring
            if (!Networking.IsMaster)
                return;

            // Check if it's the ball
            NetBall ball = other.GetComponent<NetBall>();
            if (ball == null)
                return;

            // Notify the manager
            if (Manager != null)
            {
                Manager.OnGoalScored(Team);
            }

            // Trigger local effects
            TriggerGoalEffects();
        }

        /// <summary>
        /// Called to play goal celebration effects.
        /// </summary>
        public void TriggerGoalEffects()
        {
            if (GoalExplosion != null)
            {
                GoalExplosion.Play();
            }

            if (GoalLight != null)
            {
                _lightActive = true;
                _lightTimer = LightFlashDuration;
                GoalLight.intensity = _baseLightIntensity;
            }

            if (GoalSound != null)
            {
                GoalSound.Play();
            }
        }
    }
}

