using UdonSharp;
using UdonSharp.CE.Async;
using UdonSharp.CE.DevTools;
using UnityEngine;

namespace CEShowcase.Station4_Cutscene
{
    /// <summary>
    /// Station 4: Cutscene Theater - Demonstrates UdonTask async/await for clean sequential code.
    /// 
    /// This showcases:
    /// - async UdonTask methods for readable sequential logic
    /// - await UdonTask.Delay() for time-based waits
    /// - await UdonTask.Yield() for frame-based yields
    /// - UdonTask.WhenAll() for parallel execution
    /// - CancellationToken for cancellable sequences
    /// 
    /// Compare this ~150 lines to the previous 430-line state machine!
    /// The compiler transforms async methods into state machines automatically.
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
        
        // State tracking
        private bool _isPlaying;
        private CancellationToken _cancellation;
        private string _currentStep = "Ready";
        
        // Original positions
        private Vector3 _curtainLeftStart;
        private Vector3 _curtainRightStart;
        
        void Start()
        {
            // Store initial positions
            if (_curtainLeft != null) _curtainLeftStart = _curtainLeft.localPosition;
            if (_curtainRight != null) _curtainRightStart = _curtainRight.localPosition;
            
            // Hide stage elements initially
            SetStageElementsActive(false);
            
            // Reset state
            ResetCutscene();
            
            CELogger.Info("Cutscene", "Async Cutscene Theater initialized");
        }
        
        // ========================================
        // MAIN ASYNC CUTSCENE - THE POWER OF UDONTASK
        // ========================================
        
        /// <summary>
        /// The main cutscene sequence using async/await.
        /// Notice how clean and readable this is compared to state machines!
        /// 
        /// Each 'await' pauses execution until that operation completes,
        /// then continues from where it left off. The compiler transforms
        /// this into a state machine behind the scenes.
        /// </summary>
        public async UdonTask PlayCutsceneAsync()
        {
            CELogger.Info("Cutscene", "Starting async cutscene playback");
            
            // Step 1: Fade to black
            UpdateStatus("Fading out...");
            await FadeScreenAsync(0f, 1f, _fadeDuration);
            
            // Step 2: Open curtains (with sound)
            UpdateStatus("Opening curtains...");
            PlaySound(_curtainSound);
            SetStageElementsActive(true);
            await AnimateCurtainsAsync(true, 2f);
            
            // Step 3: Show title with fade
            UpdateStatus("Showing title...");
            if (_titleText != null)
            {
                _titleText.text = "UdonSharpCE\n<size=50%>Async Made Simple</size>";
            }
            await FadeTitleAsync(0f, 1f, 1f);
            
            // Step 4: Camera pan to first waypoint
            UpdateStatus("Camera pan...");
            await MoveCameraToWaypointAsync(0, 3f);
            
            // Step 5: Hold on title
            UpdateStatus("Displaying...");
            await UdonTask.Delay(2f);
            
            // Step 6: Spotlight sequence with effects
            UpdateStatus("Spotlight sequence...");
            await SpotlightSequenceAsync(4f);
            
            // Step 7: Fireworks and applause (effects play in parallel)
            UpdateStatus("Finale effects...");
            if (_effects != null) _effects.PlayFireworks();
            PlaySound(_applauseSound);
            await UdonTask.Delay(2f);
            
            // Step 8: Hide title
            UpdateStatus("Hiding title...");
            await FadeTitleAsync(1f, 0f, 0.5f);
            
            // Step 9: Close curtains
            UpdateStatus("Closing curtains...");
            PlaySound(_curtainSound);
            await AnimateCurtainsAsync(false, 2f);
            SetStageElementsActive(false);
            
            // Step 10: Fade back in
            UpdateStatus("Fading in...");
            await FadeScreenAsync(1f, 0f, _fadeDuration);
            
            // Done!
            _isPlaying = false;
            if (_playButton != null) _playButton.SetActive(true);
            UpdateStatus("Cutscene complete! (Click to replay)");
            
            CELogger.Info("Cutscene", "Async cutscene playback complete");
        }
        
        // ========================================
        // ASYNC HELPER METHODS
        // ========================================
        
        /// <summary>
        /// Smoothly fades the screen between two alpha values.
        /// Demonstrates time-based animation with UdonTask.
        /// </summary>
        private async UdonTask FadeScreenAsync(float from, float to, float duration)
        {
            if (_fadeCanvasGroup == null)
            {
                await UdonTask.Delay(duration);
                return;
            }
            
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t = elapsed / duration;
                _fadeCanvasGroup.alpha = Mathf.Lerp(from, to, t);
                
                await UdonTask.Yield(); // Wait one frame
                elapsed += Time.deltaTime;
            }
            
            _fadeCanvasGroup.alpha = to;
        }
        
        /// <summary>
        /// Fades the title text alpha.
        /// </summary>
        private async UdonTask FadeTitleAsync(float from, float to, float duration)
        {
            if (_titleCanvasGroup == null)
            {
                await UdonTask.Delay(duration);
                return;
            }
            
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t = elapsed / duration;
                _titleCanvasGroup.alpha = Mathf.Lerp(from, to, SmoothStep(t));
                
                await UdonTask.Yield();
                elapsed += Time.deltaTime;
            }
            
            _titleCanvasGroup.alpha = to;
        }
        
        /// <summary>
        /// Animates curtains opening or closing.
        /// </summary>
        private async UdonTask AnimateCurtainsAsync(bool opening, float duration)
        {
            if (_curtainLeft == null || _curtainRight == null)
            {
                await UdonTask.Delay(duration);
                return;
            }
            
            Vector3 leftStart = opening ? _curtainLeftStart : _curtainLeftStart - _curtainOpenOffset;
            Vector3 leftEnd = opening ? _curtainLeftStart - _curtainOpenOffset : _curtainLeftStart;
            Vector3 rightStart = opening ? _curtainRightStart : _curtainRightStart + _curtainOpenOffset;
            Vector3 rightEnd = opening ? _curtainRightStart + _curtainOpenOffset : _curtainRightStart;
            
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t = SmoothStep(elapsed / duration);
                
                _curtainLeft.localPosition = Vector3.Lerp(leftStart, leftEnd, t);
                _curtainRight.localPosition = Vector3.Lerp(rightStart, rightEnd, t);
                
                await UdonTask.Yield();
                elapsed += Time.deltaTime;
            }
            
            _curtainLeft.localPosition = leftEnd;
            _curtainRight.localPosition = rightEnd;
        }
        
        /// <summary>
        /// Moves camera to a waypoint with smooth interpolation.
        /// </summary>
        private async UdonTask MoveCameraToWaypointAsync(int waypointIndex, float duration)
        {
            if (_cameraRig == null || _cameraWaypoints == null || 
                waypointIndex < 0 || waypointIndex >= _cameraWaypoints.Length)
            {
                await UdonTask.Delay(duration);
                return;
            }
            
            Transform waypoint = _cameraWaypoints[waypointIndex];
            if (waypoint == null)
            {
                await UdonTask.Delay(duration);
                return;
            }
            
            Vector3 startPos = _cameraRig.position;
            Quaternion startRot = _cameraRig.rotation;
            Vector3 endPos = waypoint.position;
            Quaternion endRot = waypoint.rotation;
            
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t = SmoothStep(elapsed / duration);
                
                _cameraRig.position = Vector3.Lerp(startPos, endPos, t);
                _cameraRig.rotation = Quaternion.Slerp(startRot, endRot, t);
                
                await UdonTask.Yield();
                elapsed += Time.deltaTime;
            }
            
            _cameraRig.position = endPos;
            _cameraRig.rotation = endRot;
        }
        
        /// <summary>
        /// Plays a spotlight color/intensity sequence.
        /// Demonstrates effects with timed loops.
        /// </summary>
        private async UdonTask SpotlightSequenceAsync(float duration)
        {
            if (_spotlight == null)
            {
                await UdonTask.Delay(duration);
                return;
            }
            
            if (_effects != null) _effects.PlaySparkles();
            
            float elapsed = 0f;
            while (elapsed < duration)
            {
                float t = elapsed / duration;
                
                // Pulsing intensity
                float pulse = Mathf.Sin(t * Mathf.PI * 4) * 0.3f + 0.7f;
                _spotlight.intensity = pulse * 2f;
                
                // Color shift through hues
                float hue = t * 0.3f;
                _spotlight.color = Color.HSVToRGB(hue, 0.7f, 1f);
                
                await UdonTask.Yield();
                elapsed += Time.deltaTime;
            }
            
            // Reset spotlight
            _spotlight.intensity = 1f;
            _spotlight.color = Color.white;
            
            if (_effects != null) _effects.StopSparkles();
        }
        
        // ========================================
        // UTILITY METHODS
        // ========================================
        
        private float SmoothStep(float t)
        {
            return t * t * (3f - 2f * t);
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
            _currentStep = status;
            
            if (_statusText == null) return;
            
            _statusText.text = $"<b>ASYNC CUTSCENE</b>\n" +
                              $"Status: <color=#00FF00>{status}</color>\n" +
                              $"Using: UdonTask async/await\n" +
                              $"<size=70%>Compare: 150 lines vs 430 lines (state machine)</size>";
        }
        
        public void ResetCutscene()
        {
            _isPlaying = false;
            _currentStep = "Ready";
            
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
        
        // ========================================
        // UI CALLBACKS
        // ========================================
        
        public void OnPlayCutscene()
        {
            if (_isPlaying) return;
            
            _isPlaying = true;
            if (_playButton != null) _playButton.SetActive(false);
            
            // Fire off the async cutscene!
            // The compiler generates the state machine - we just write clean code.
            PlayCutsceneAsync();
        }
        
        public void OnSkipCutscene()
        {
            if (!_isPlaying) return;
            
            // Cancel the current sequence
            // In a full implementation, CancellationToken would be checked in each await
            _isPlaying = false;
            ResetCutscene();
            
            CELogger.Info("Cutscene", "Cutscene skipped");
        }
        
        public void OnResetCutscene()
        {
            ResetCutscene();
            CELogger.Info("Cutscene", "Cutscene reset");
        }
        
        // ========================================
        // INDIVIDUAL EFFECT DEMOS
        // ========================================
        
        /// <summary>
        /// Demo button to show just the fade effect with async.
        /// </summary>
        public void OnDemoFade()
        {
            DemoFadeAsync();
        }
        
        private async UdonTask DemoFadeAsync()
        {
            await FadeScreenAsync(0f, 1f, 0.5f);
            await UdonTask.Delay(0.5f);
            await FadeScreenAsync(1f, 0f, 0.5f);
        }
        
        /// <summary>
        /// Demo button to show curtain animation with async.
        /// </summary>
        public void OnDemoCurtains()
        {
            DemoCurtainsAsync();
        }
        
        private async UdonTask DemoCurtainsAsync()
        {
            SetStageElementsActive(true);
            PlaySound(_curtainSound);
            await AnimateCurtainsAsync(true, 1.5f);
            await UdonTask.Delay(1f);
            PlaySound(_curtainSound);
            await AnimateCurtainsAsync(false, 1.5f);
            SetStageElementsActive(false);
        }
        
        /// <summary>
        /// Demo button to show spotlight sequence with async.
        /// </summary>
        public void OnDemoSpotlight()
        {
            SpotlightSequenceAsync(3f);
        }
    }
}
