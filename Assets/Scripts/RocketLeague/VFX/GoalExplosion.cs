using JetBrains.Annotations;
using UdonSharp;
using UnityEngine;

namespace RocketLeague
{
    /// <summary>
    /// Controls goal explosion visual effects.
    /// </summary>
    [PublicAPI]
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class GoalExplosion : UdonSharpBehaviour
    {
        [Header("Effects")]
        public ParticleSystem[] ParticleSystems;
        public Light ExplosionLight;
        public AudioSource ExplosionSound;

        [Header("Settings")]
        public float LightDuration = 1f;
        public float LightIntensity = 10f;
        public AnimationCurve LightFalloff;

        [Header("Team Colors")]
        public Color BlueTeamColor = new Color(0.2f, 0.5f, 1f);
        public Color OrangeTeamColor = new Color(1f, 0.5f, 0.1f);

        private float _lightTimer;
        private bool _isPlaying;

        private void Start()
        {
            if (ExplosionLight != null)
            {
                ExplosionLight.intensity = 0f;
            }

            if (LightFalloff == null || LightFalloff.length == 0)
            {
                LightFalloff = AnimationCurve.EaseInOut(0f, 1f, 1f, 0f);
            }
        }

        private void Update()
        {
            if (_isPlaying && ExplosionLight != null)
            {
                _lightTimer += Time.deltaTime;
                float t = Mathf.Clamp01(_lightTimer / LightDuration);
                ExplosionLight.intensity = LightIntensity * LightFalloff.Evaluate(t);

                if (_lightTimer >= LightDuration)
                {
                    _isPlaying = false;
                    ExplosionLight.intensity = 0f;
                }
            }
        }

        /// <summary>
        /// Triggers the goal explosion effect for the specified team.
        /// </summary>
        public void Trigger(int scoringTeam)
        {
            Color teamColor = scoringTeam == 1 ? BlueTeamColor : OrangeTeamColor;

            // Set particle colors
            if (ParticleSystems != null)
            {
                for (int i = 0; i < ParticleSystems.Length; i++)
                {
                    if (ParticleSystems[i] != null)
                    {
                        var main = ParticleSystems[i].main;
                        main.startColor = teamColor;
                        ParticleSystems[i].Play();
                    }
                }
            }

            // Set light color and trigger
            if (ExplosionLight != null)
            {
                ExplosionLight.color = teamColor;
                ExplosionLight.intensity = LightIntensity;
                _lightTimer = 0f;
                _isPlaying = true;
            }

            // Play sound
            if (ExplosionSound != null)
            {
                ExplosionSound.Play();
            }
        }

        /// <summary>
        /// Stops all effects.
        /// </summary>
        public void Stop()
        {
            _isPlaying = false;

            if (ParticleSystems != null)
            {
                for (int i = 0; i < ParticleSystems.Length; i++)
                {
                    if (ParticleSystems[i] != null)
                    {
                        ParticleSystems[i].Stop();
                    }
                }
            }

            if (ExplosionLight != null)
            {
                ExplosionLight.intensity = 0f;
            }
        }
    }
}

