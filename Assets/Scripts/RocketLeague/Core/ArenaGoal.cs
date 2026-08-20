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
    // NoVariableSync, not None: this behaviour has no synced variables, but it must still be able to
    // receive SendCustomNetworkEvent so goal effects fire on every client. BehaviourSyncMode.None
    // disables network events entirely, which is why goal light/particles/sound only ever played for
    // the instance master.
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
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

            if (Manager == null)
                return;

            // Test the same condition RocketLeagueManager.OnGoalScored early-returns on, and test it
            // before announcing anything. The ball keeps rolling through the goal mouth during the
            // celebration and the kickoff countdown, and an unconditional broadcast turned each of those
            // stray passes into a full horn/particles/light replay for the whole instance -- a glitch that
            // used to be cosmetic and local to the master.
            GameState state = Manager.State;
            if (state != GameState.Playing && state != GameState.Overtime)
                return;

            Manager.OnGoalScored(Team);

            // Broadcast instead of calling locally. Only the master ever reaches this line, so the direct
            // call meant the light, the particles and the horn played for exactly one player in the
            // instance while everyone else watched a silent, unlit goal. NetworkEventTarget.All includes
            // the sender, so the master still sees its own effects.
            SendCustomNetworkEvent(VRC.Udon.Common.Interfaces.NetworkEventTarget.All, nameof(TriggerGoalEffects));
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

