using UdonSharp;
using UdonSharp.CE.DevTools;
using UnityEngine;

namespace CEShowcase.Station4_Cutscene
{
    /// <summary>
    /// Visual effects controller for the Cutscene Theater station.
    /// Manages sparkles, fireworks, and other theatrical effects.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class TheaterEffects : UdonSharpBehaviour
    {
        [Header("Particle Systems")]
        [SerializeField] private ParticleSystem _sparkles;
        [SerializeField] private ParticleSystem _fireworks;
        [SerializeField] private ParticleSystem _confetti;
        [SerializeField] private ParticleSystem _smoke;
        
        [Header("Lights")]
        [SerializeField] private Light[] _stageLights;
        [SerializeField] private Color[] _lightColors;
        [SerializeField] private float _lightPulseSpeed = 2f;
        
        [Header("Props")]
        [SerializeField] private Transform[] _floatingProps;
        [SerializeField] private float _floatAmplitude = 0.5f;
        [SerializeField] private float _floatSpeed = 1f;
        
        // State
        private bool _lightsActive;
        private float _lightTimer;
        private Vector3[] _propStartPositions;
        
        void Start()
        {
            // Store initial positions of floating props
            if (_floatingProps != null && _floatingProps.Length > 0)
            {
                _propStartPositions = new Vector3[_floatingProps.Length];
                for (int i = 0; i < _floatingProps.Length; i++)
                {
                    if (_floatingProps[i] != null)
                    {
                        _propStartPositions[i] = _floatingProps[i].localPosition;
                    }
                }
            }
            
            StopAllEffects();
        }
        
        void Update()
        {
            if (_lightsActive)
            {
                UpdateStageLights();
            }
            
            UpdateFloatingProps();
        }
        
        private void UpdateStageLights()
        {
            if (_stageLights == null || _lightColors == null) return;
            
            _lightTimer += Time.deltaTime * _lightPulseSpeed;
            
            for (int i = 0; i < _stageLights.Length; i++)
            {
                Light light = _stageLights[i];
                if (light == null) continue;
                
                // Phase offset per light for variety
                float phase = _lightTimer + (i * Mathf.PI * 0.5f);
                
                // Pulsing intensity
                float intensity = 1f + Mathf.Sin(phase) * 0.5f;
                light.intensity = intensity;
                
                // Color cycling
                if (_lightColors.Length > 0)
                {
                    float colorIndex = (Mathf.Sin(phase * 0.5f) + 1f) * 0.5f * (_lightColors.Length - 1);
                    int idx1 = Mathf.FloorToInt(colorIndex);
                    int idx2 = Mathf.Min(idx1 + 1, _lightColors.Length - 1);
                    float t = colorIndex - idx1;
                    
                    light.color = Color.Lerp(_lightColors[idx1], _lightColors[idx2], t);
                }
            }
        }
        
        private void UpdateFloatingProps()
        {
            if (_floatingProps == null || _propStartPositions == null) return;
            
            float time = Time.time * _floatSpeed;
            
            for (int i = 0; i < _floatingProps.Length; i++)
            {
                Transform prop = _floatingProps[i];
                if (prop == null) continue;
                
                // Offset phase per prop
                float phase = time + (i * 0.7f);
                
                Vector3 startPos = _propStartPositions[i];
                float yOffset = Mathf.Sin(phase) * _floatAmplitude;
                
                prop.localPosition = startPos + Vector3.up * yOffset;
                
                // Gentle rotation
                prop.Rotate(Vector3.up, Time.deltaTime * 15f);
            }
        }
        
        // Public effect triggers
        public void PlaySparkles()
        {
            if (_sparkles != null)
            {
                _sparkles.Play();
                CELogger.Debug("Effects", "Playing sparkles");
            }
        }
        
        public void StopSparkles()
        {
            if (_sparkles != null)
            {
                _sparkles.Stop();
            }
        }
        
        public void PlayFireworks()
        {
            if (_fireworks != null)
            {
                _fireworks.Play();
                CELogger.Debug("Effects", "Playing fireworks");
            }
        }
        
        public void StopFireworks()
        {
            if (_fireworks != null)
            {
                _fireworks.Stop();
            }
        }
        
        public void PlayConfetti()
        {
            if (_confetti != null)
            {
                _confetti.Play();
                CELogger.Debug("Effects", "Playing confetti");
            }
        }
        
        public void StopConfetti()
        {
            if (_confetti != null)
            {
                _confetti.Stop();
            }
        }
        
        public void PlaySmoke()
        {
            if (_smoke != null)
            {
                _smoke.Play();
                CELogger.Debug("Effects", "Playing smoke");
            }
        }
        
        public void StopSmoke()
        {
            if (_smoke != null)
            {
                _smoke.Stop();
            }
        }
        
        public void StartStageLights()
        {
            _lightsActive = true;
            _lightTimer = 0f;
            
            // Turn on all lights
            if (_stageLights != null)
            {
                for (int i = 0; i < _stageLights.Length; i++)
                {
                    if (_stageLights[i] != null)
                    {
                        _stageLights[i].enabled = true;
                    }
                }
            }
            
            CELogger.Debug("Effects", "Stage lights started");
        }
        
        public void StopStageLights()
        {
            _lightsActive = false;
            
            // Reset lights
            if (_stageLights != null)
            {
                for (int i = 0; i < _stageLights.Length; i++)
                {
                    if (_stageLights[i] != null)
                    {
                        _stageLights[i].intensity = 1f;
                        _stageLights[i].color = Color.white;
                    }
                }
            }
        }
        
        public void PlayAllEffects()
        {
            PlaySparkles();
            PlayFireworks();
            PlayConfetti();
            PlaySmoke();
            StartStageLights();
            
            CELogger.Info("Effects", "All effects playing");
        }
        
        public void StopAllEffects()
        {
            StopSparkles();
            StopFireworks();
            StopConfetti();
            StopSmoke();
            StopStageLights();
            
            CELogger.Debug("Effects", "All effects stopped");
        }
        
        // Quick burst effects for button presses
        public void QuickSparkle()
        {
            if (_sparkles != null)
            {
                _sparkles.Emit(50);
            }
        }
        
        public void QuickFirework()
        {
            if (_fireworks != null)
            {
                _fireworks.Emit(10);
            }
        }
        
        public void QuickConfetti()
        {
            if (_confetti != null)
            {
                _confetti.Emit(100);
            }
        }
    }
}
