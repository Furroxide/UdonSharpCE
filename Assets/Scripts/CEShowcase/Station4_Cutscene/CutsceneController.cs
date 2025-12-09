using UdonSharp;
using UdonSharp.CE.Async;
using UdonSharp.CE.DevTools;
using UnityEngine;

namespace CEShowcase.Station4_Cutscene
{
    /// <summary>
    /// Station 4: Cutscene Theater - Demonstrates UdonTask async/await for clean sequential code.
    /// Shows how async patterns replace callback spaghetti with readable linear code.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CutsceneController : UdonSharpBehaviour
    {
        [Header("Screen Fade")]
        [SerializeField] private CanvasGroup _fadeCanvasGroup;
        [SerializeField] private float _fadeDuration = 1f;
        
        [Header("Camera")]
        [SerializeField] private Transform _cameraRig;
        [SerializeField] private Transform[] _cameraWaypoints;
        [SerializeField] private float _cameraMoveSpeed = 2f;
        
        [Header("Theater Elements")]
        [SerializeField] private GameObject[] _stageElements;
        [SerializeField] private Light _spotlight;
        [SerializeField] private Transform _curtainLeft;
        [SerializeField] private Transform _curtainRight;
        [SerializeField] private Vector3 _curtainOpenOffset = new Vector3(3f, 0, 0);
        
        [Header("Title Display")]
        [SerializeField] private TMPro.TextMeshProUGUI _titleText;
        [SerializeField] private CanvasGroup _titleCanvasGroup;
        
        [Header("Audio")]
        [SerializeField] private AudioSource _musicSource;
        [SerializeField] private AudioSource _sfxSource;
        [SerializeField] private AudioClip _curtainSound;
        [SerializeField] private AudioClip _applauseSound;
        
        [Header("UI")]
        [SerializeField] private TMPro.TextMeshProUGUI _statusText;
        [SerializeField] private GameObject _playButton;
        
        [Header("Effects Reference")]
        [SerializeField] private TheaterEffects _effects;
        
        // State
        private bool _isPlaying;
        private int _currentState;
        private float _stateTimer;
        private float _stateTarget;
        
        // Animation targets
        private float _fadeTarget;
        private Vector3 _cameraTargetPos;
        private Quaternion _cameraTargetRot;
        private int _currentWaypoint;
        
        // Original positions
        private Vector3 _curtainLeftStart;
        private Vector3 _curtainRightStart;
        
        // State machine constants
        private const int STATE_IDLE = 0;
        private const int STATE_FADE_OUT = 1;
        private const int STATE_OPEN_CURTAINS = 2;
        private const int STATE_CAMERA_PAN = 3;
        private const int STATE_SHOW_TITLE = 4;
        private const int STATE_SPOTLIGHT_SEQUENCE = 5;
        private const int STATE_EFFECTS = 6;
        private const int STATE_HIDE_TITLE = 7;
        private const int STATE_CLOSE_CURTAINS = 8;
        private const int STATE_FADE_IN = 9;
        private const int STATE_COMPLETE = 10;
        
        void Start()
        {
            // Store initial positions
            if (_curtainLeft != null) _curtainLeftStart = _curtainLeft.localPosition;
            if (_curtainRight != null) _curtainRightStart = _curtainRight.localPosition;
            
            // Hide stage elements initially
            SetStageElementsActive(false);
            
            // Reset state
            ResetCutscene();
            
            CELogger.Info("Cutscene", "Cutscene Theater initialized");
        }
        
        void Update()
        {
            if (!_isPlaying) return;
            
            _stateTimer += Time.deltaTime;
            
            // Update current state animation
            UpdateStateAnimation();
            
            // Check for state transition
            if (_stateTimer >= _stateTarget)
            {
                AdvanceState();
            }
        }
        
        private void UpdateStateAnimation()
        {
            float t = _stateTarget > 0 ? _stateTimer / _stateTarget : 1f;
            t = Mathf.Clamp01(t);
            
            switch (_currentState)
            {
                case STATE_FADE_OUT:
                    if (_fadeCanvasGroup != null)
                        _fadeCanvasGroup.alpha = Mathf.Lerp(0, 1, t);
                    break;
                    
                case STATE_FADE_IN:
                    if (_fadeCanvasGroup != null)
                        _fadeCanvasGroup.alpha = Mathf.Lerp(1, 0, t);
                    break;
                    
                case STATE_OPEN_CURTAINS:
                    UpdateCurtains(t, true);
                    break;
                    
                case STATE_CLOSE_CURTAINS:
                    UpdateCurtains(t, false);
                    break;
                    
                case STATE_CAMERA_PAN:
                    UpdateCameraMove(t);
                    break;
                    
                case STATE_SHOW_TITLE:
                    if (_titleCanvasGroup != null)
                        _titleCanvasGroup.alpha = Mathf.Lerp(0, 1, t);
                    break;
                    
                case STATE_HIDE_TITLE:
                    if (_titleCanvasGroup != null)
                        _titleCanvasGroup.alpha = Mathf.Lerp(1, 0, t);
                    break;
                    
                case STATE_SPOTLIGHT_SEQUENCE:
                    UpdateSpotlight(t);
                    break;
            }
        }
        
        private void UpdateCurtains(float t, bool opening)
        {
            if (_curtainLeft == null || _curtainRight == null) return;
            
            float progress = opening ? t : (1 - t);
            
            _curtainLeft.localPosition = Vector3.Lerp(_curtainLeftStart, _curtainLeftStart - _curtainOpenOffset, progress);
            _curtainRight.localPosition = Vector3.Lerp(_curtainRightStart, _curtainRightStart + _curtainOpenOffset, progress);
        }
        
        private void UpdateCameraMove(float t)
        {
            if (_cameraRig == null) return;
            
            t = SmoothStep(t); // Apply easing
            
            _cameraRig.position = Vector3.Lerp(_cameraRig.position, _cameraTargetPos, t * 0.1f);
            _cameraRig.rotation = Quaternion.Slerp(_cameraRig.rotation, _cameraTargetRot, t * 0.1f);
        }
        
        private void UpdateSpotlight(float t)
        {
            if (_spotlight == null) return;
            
            // Pulsing intensity
            float pulse = Mathf.Sin(t * Mathf.PI * 4) * 0.3f + 0.7f;
            _spotlight.intensity = pulse * 2f;
            
            // Color shift
            float hue = t * 0.3f; // Cycle through some colors
            _spotlight.color = Color.HSVToRGB(hue, 0.7f, 1f);
        }
        
        private float SmoothStep(float t)
        {
            return t * t * (3f - 2f * t);
        }
        
        private void AdvanceState()
        {
            _currentState++;
            _stateTimer = 0f;
            
            switch (_currentState)
            {
                case STATE_FADE_OUT:
                    _stateTarget = _fadeDuration;
                    UpdateStatus("Fading out...");
                    break;
                    
                case STATE_OPEN_CURTAINS:
                    _stateTarget = 2f;
                    SetStageElementsActive(true);
                    PlaySound(_curtainSound);
                    UpdateStatus("Opening curtains...");
                    break;
                    
                case STATE_CAMERA_PAN:
                    _stateTarget = 3f;
                    SetupNextWaypoint();
                    UpdateStatus("Camera pan...");
                    break;
                    
                case STATE_SHOW_TITLE:
                    _stateTarget = 1f;
                    if (_titleText != null)
                        _titleText.text = "✨ UdonSharpCE ✨\n<size=50%>Async Made Simple</size>";
                    UpdateStatus("Showing title...");
                    break;
                    
                case STATE_SPOTLIGHT_SEQUENCE:
                    _stateTarget = 4f;
                    if (_effects != null) _effects.PlaySparkles();
                    UpdateStatus("Spotlight sequence...");
                    break;
                    
                case STATE_EFFECTS:
                    _stateTarget = 2f;
                    if (_effects != null) _effects.PlayFireworks();
                    PlaySound(_applauseSound);
                    UpdateStatus("Effects...");
                    break;
                    
                case STATE_HIDE_TITLE:
                    _stateTarget = 0.5f;
                    UpdateStatus("Hiding title...");
                    break;
                    
                case STATE_CLOSE_CURTAINS:
                    _stateTarget = 2f;
                    PlaySound(_curtainSound);
                    UpdateStatus("Closing curtains...");
                    break;
                    
                case STATE_FADE_IN:
                    _stateTarget = _fadeDuration;
                    SetStageElementsActive(false);
                    UpdateStatus("Fading in...");
                    break;
                    
                case STATE_COMPLETE:
                    _isPlaying = false;
                    if (_playButton != null) _playButton.SetActive(true);
                    UpdateStatus("Cutscene complete! (Click to replay)");
                    CELogger.Info("Cutscene", "Cutscene playback complete");
                    break;
            }
        }
        
        private void SetupNextWaypoint()
        {
            if (_cameraWaypoints == null || _cameraWaypoints.Length == 0) return;
            
            _currentWaypoint = (_currentWaypoint + 1) % _cameraWaypoints.Length;
            Transform waypoint = _cameraWaypoints[_currentWaypoint];
            
            if (waypoint != null)
            {
                _cameraTargetPos = waypoint.position;
                _cameraTargetRot = waypoint.rotation;
            }
        }
        
        private void SetStageElementsActive(bool active)
        {
            if (_stageElements == null) return;
            
            for (int i = 0; i < _stageElements.Length; i++)
            {
                if (_stageElements[i] != null)
                {
                    _stageElements[i].SetActive(active);
                }
            }
        }
        
        private void PlaySound(AudioClip clip)
        {
            if (_sfxSource != null && clip != null)
            {
                _sfxSource.PlayOneShot(clip);
            }
        }
        
        private void UpdateStatus(string status)
        {
            if (_statusText == null) return;
            
            _statusText.text = $"<b>CUTSCENE STATE</b>\n" +
                              $"State: {GetStateName(_currentState)}\n" +
                              $"Progress: {_stateTimer:F1}s / {_stateTarget:F1}s\n" +
                              $"Status: {status}";
        }
        
        private string GetStateName(int state)
        {
            switch (state)
            {
                case STATE_IDLE: return "Idle";
                case STATE_FADE_OUT: return "FadeOut";
                case STATE_OPEN_CURTAINS: return "OpenCurtains";
                case STATE_CAMERA_PAN: return "CameraPan";
                case STATE_SHOW_TITLE: return "ShowTitle";
                case STATE_SPOTLIGHT_SEQUENCE: return "Spotlight";
                case STATE_EFFECTS: return "Effects";
                case STATE_HIDE_TITLE: return "HideTitle";
                case STATE_CLOSE_CURTAINS: return "CloseCurtains";
                case STATE_FADE_IN: return "FadeIn";
                case STATE_COMPLETE: return "Complete";
                default: return "Unknown";
            }
        }
        
        public void ResetCutscene()
        {
            _isPlaying = false;
            _currentState = STATE_IDLE;
            _stateTimer = 0f;
            _stateTarget = 0f;
            _currentWaypoint = -1;
            
            // Reset visual state
            if (_fadeCanvasGroup != null) _fadeCanvasGroup.alpha = 0f;
            if (_titleCanvasGroup != null) _titleCanvasGroup.alpha = 0f;
            
            // Reset curtains
            if (_curtainLeft != null) _curtainLeft.localPosition = _curtainLeftStart;
            if (_curtainRight != null) _curtainRight.localPosition = _curtainRightStart;
            
            // Reset spotlight
            if (_spotlight != null)
            {
                _spotlight.intensity = 1f;
                _spotlight.color = Color.white;
            }
            
            SetStageElementsActive(false);
            
            if (_playButton != null) _playButton.SetActive(true);
            
            UpdateStatus("Ready to play");
        }
        
        // UI Callbacks
        public void OnPlayCutscene()
        {
            if (_isPlaying) return;
            
            CELogger.Info("Cutscene", "Starting cutscene playback");
            
            _isPlaying = true;
            _currentState = STATE_IDLE;
            _stateTimer = 0f;
            _stateTarget = 0f;
            
            if (_playButton != null) _playButton.SetActive(false);
            
            // Start the state machine
            AdvanceState();
        }
        
        public void OnSkipCutscene()
        {
            if (!_isPlaying) return;
            
            // Skip to end
            _currentState = STATE_COMPLETE - 1;
            _stateTimer = _stateTarget;
            
            CELogger.Info("Cutscene", "Cutscene skipped");
        }
        
        public void OnResetCutscene()
        {
            ResetCutscene();
            CELogger.Info("Cutscene", "Cutscene reset");
        }
        
        /* 
         * NOTE: The code below shows the IDEAL async/await syntax that CE enables.
         * This is what the cutscene WOULD look like with full UdonTask support:
         * 
         * public UdonTask PlayCutsceneAsync()
         * {
         *     // Fade to black
         *     await FadeScreen(1f, Color.black);
         *     
         *     // Open curtains
         *     await OpenCurtains();
         *     
         *     // Camera pan
         *     await MoveCameraToWaypoint(0);
         *     
         *     // Show title with fade
         *     await ShowTitle("UdonSharpCE", "Async Made Simple");
         *     await UdonTask.Delay(2f);
         *     
         *     // Parallel effects
         *     await UdonTask.WhenAll(
         *         PlaySpotlightSequence(),
         *         PlaySparkles()
         *     );
         *     
         *     // Fireworks and applause
         *     await PlayFireworks();
         *     
         *     // Hide title
         *     await HideTitle();
         *     
         *     // Close curtains
         *     await CloseCurtains();
         *     
         *     // Fade back in
         *     await FadeScreen(1f, Color.clear);
         * }
         * 
         * Compare this ~25 lines to the 300+ lines of state machine above!
         * This demonstrates the power of async/await syntax.
         */
    }
}
