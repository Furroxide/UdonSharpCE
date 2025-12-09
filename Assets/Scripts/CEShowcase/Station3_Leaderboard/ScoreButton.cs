using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace CEShowcase.Station3_Leaderboard
{
    /// <summary>
    /// Interactive score button for the Leaderboard station.
    /// Allows players to earn points by interacting.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ScoreButton : UdonSharpBehaviour
    {
        [Header("Score Settings")]
        [SerializeField] private LeaderboardManager _leaderboardManager;
        [SerializeField] private int _scoreAmount = 10;
        
        [Header("Interaction")]
        [SerializeField] private string _interactionText = "Score +10";
        
        [Header("Feedback")]
        [SerializeField] private AudioSource _clickSound;
        [SerializeField] private ParticleSystem _clickParticles;
        [SerializeField] private float _cooldown = 0.5f;
        
        [Header("Visual Feedback")]
        [SerializeField] private Renderer _buttonRenderer;
        [SerializeField] private Material _normalMaterial;
        [SerializeField] private Material _pressedMaterial;
        
        private float _lastInteractTime;
        private float _resetColorTime;
        private bool _isPressed;
        
        void Start()
        {
            if (!string.IsNullOrEmpty(_interactionText))
            {
                InteractionText = _interactionText;
            }
        }
        
        void Update()
        {
            // Handle button color reset
            if (_isPressed && Time.time >= _resetColorTime)
            {
                ResetButtonColor();
            }
        }
        
        public override void Interact()
        {
            // Check cooldown
            if (Time.time - _lastInteractTime < _cooldown)
            {
                return;
            }
            
            _lastInteractTime = Time.time;
            
            // Add score
            if (_leaderboardManager != null)
            {
                VRCPlayerApi localPlayer = Networking.LocalPlayer;
                if (localPlayer != null && localPlayer.IsValid())
                {
                    _leaderboardManager.AddScore(localPlayer.playerId, _scoreAmount);
                }
            }
            
            // Visual/audio feedback
            PlayFeedback();
            
            // Visual button press - will reset in Update
            SetButtonPressed(true);
            _resetColorTime = Time.time + 0.1f;
        }
        
        private void PlayFeedback()
        {
            if (_clickSound != null)
            {
                _clickSound.Play();
            }
            
            if (_clickParticles != null)
            {
                _clickParticles.Play();
            }
        }
        
        public void ResetButtonColor()
        {
            SetButtonPressed(false);
        }
        
        private void SetButtonPressed(bool pressed)
        {
            _isPressed = pressed;
            
            if (_buttonRenderer == null) return;
            
            if (pressed && _pressedMaterial != null)
            {
                _buttonRenderer.material = _pressedMaterial;
            }
            else if (!pressed && _normalMaterial != null)
            {
                _buttonRenderer.material = _normalMaterial;
            }
        }
    }
}
