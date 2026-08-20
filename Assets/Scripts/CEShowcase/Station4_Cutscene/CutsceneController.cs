using UdonSharp;
using UdonSharp.CE.Async;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Net;
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
    /// - awaiting nested async helpers (fade, curtains, camera moves) from one flat sequence
    /// - CancellationTokenSource / CancellationToken for a sequence that can be stopped part-way
    ///
    /// Deliberately not UdonTask.WhenAll(): the compiler has no lowering for it, so awaiting one
    /// would never resume. Every step below is sequential.
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

        // Cancellation is cooperative: Udon cannot interrupt a running method, so the source is only a
        // flag that every await point in this file checks. Without it a skip left the old sequence
        // driving the curtains, camera and fade straight over the top of the reset state.
        private CancellationTokenSource _cancellation;

        // Authored on the AudioSource in the scene; the fades lerp back to this rather than to a literal.
        private float _musicVolume = 0.5f;

        // The frame a sequence was last cancelled on. A cancelled state machine only tears itself down
        // when it next resumes, so for the rest of that frame it is still holding the shared locals and
        // the generated guard refuses anything new -- and the refused start's scheduled resume is lost
        // with it. Waiting one frame costs nothing: no player can press two buttons inside a single frame.
        private int _cancelFrame = -1;
        // Local status text only. This was previously [SyncOnJoin], which was wrong twice over: the
        // cutscene is started by a purely local interact and driven by a local async sequence, so every
        // client writes its own value; and the behaviour is BehaviourSyncMode.None, so nothing on it can
        // reach another player anyway. See ProcgenDemo for a [SyncOnJoin] field that is genuinely shared.
        private string _currentStep = "Ready";
        
        // Original positions
        private Vector3 _curtainLeftStart;
        private Vector3 _curtainRightStart;

        // Where the crowd camera sits when nothing is playing, so a finished or skipped cutscene hands
        // the screen back to the audience view instead of parking on an empty stage.
        private Vector3 _cameraHomePosition;
        private Quaternion _cameraHomeRotation;

        // Waypoint order on the crowd camera rig.
        private const int WaypointStageWide = 0;
        private const int WaypointActorCloseup = 1;
        private const int WaypointCrowd = 2;
        
        void Start()
        {
            AutoWireMissingReferences();

            // Store initial positions
            if (_curtainLeft != null) _curtainLeftStart = _curtainLeft.localPosition;
            if (_curtainRight != null) _curtainRightStart = _curtainRight.localPosition;

            if (_cameraRig != null)
            {
                _cameraHomePosition = _cameraRig.position;
                _cameraHomeRotation = _cameraRig.rotation;
            }

            if (_musicSource != null)
            {
                _musicVolume = _musicSource.volume;
                _musicSource.volume = 0f;
            }

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
        public async UdonTask PlayCutsceneAsync(CancellationToken token)
        {
            _isPlaying = true;
            if (_playButton != null) _playButton.SetActive(false);

            CELogger.Info("Cutscene", "Starting async cutscene playback");

            // Step 1: Fade the crowd screen to black -- the audience should not watch the stage
            // being set. This fade is on the screen the camera feeds, not on anyone's eyes: Udon
            // cannot draw over a VRChat player's view.
            UpdateStatus("Fading out...");
            await FadeScreenAsync(0f, 1f, _fadeDuration, token);
            if (token.IsCancellationRequested) return;

            // Step 2: Music swells, then the curtains open and the stage is dressed behind the black
            UpdateStatus("Opening curtains...");
            await FadeMusicAsync(0f, _musicVolume, 0.8f, token);
            if (token.IsCancellationRequested) return;
            PlaySound(_curtainSound);
            SetStageElementsActive(true);
            await AnimateCurtainsAsync(true, 2f, token);
            if (token.IsCancellationRequested) return;

            // Step 3: Fade back up on the revealed stage. The screen has to return here rather than
            // at the end -- every step after this one is meant to be watched on it.
            UpdateStatus("Reveal...");
            await FadeScreenAsync(1f, 0f, _fadeDuration, token);
            if (token.IsCancellationRequested) return;

            // Step 4: Show title with fade
            UpdateStatus("Showing title...");
            if (_titleText != null)
            {
                _titleText.text = "UdonSharpCE\n<size=50%>Async Made Simple</size>";
            }
            await FadeTitleAsync(0f, 1f, 1f, token);
            if (token.IsCancellationRequested) return;
            
            // Step 5: Camera leaves the crowd view and frames the whole stage
            UpdateStatus("Camera pan...");
            await MoveCameraToWaypointAsync(WaypointStageWide, GetCameraMoveDuration(WaypointStageWide, 3f), token);
            if (token.IsCancellationRequested) return;

            // Step 6: Hold on title
            UpdateStatus("Displaying...");
            await UdonTask.Delay(2f);
            if (token.IsCancellationRequested) return;

            // Step 7: Push in on the performer
            UpdateStatus("Close-up...");
            await MoveCameraToWaypointAsync(WaypointActorCloseup, GetCameraMoveDuration(WaypointActorCloseup, 2f), token);
            if (token.IsCancellationRequested) return;

            // Step 8: Spotlight sequence with effects
            UpdateStatus("Spotlight sequence...");
            await SpotlightSequenceAsync(4f, token);
            if (token.IsCancellationRequested) return;

            // Step 9: Pull back so the finale reads wide
            UpdateStatus("Curtain call...");
            await MoveCameraToWaypointAsync(WaypointCrowd, GetCameraMoveDuration(WaypointCrowd, 3f), token);
            if (token.IsCancellationRequested) return;

            // Step 10: Fireworks and applause
            UpdateStatus("Finale effects...");
            if (_effects != null)
            {
                _effects.PlayFireworks();
                _effects.PlayConfetti();
            }
            PlaySound(_applauseSound);
            await UdonTask.Delay(2f);
            if (token.IsCancellationRequested) return;

            // Step 11: Hide title
            UpdateStatus("Hiding title...");
            await FadeTitleAsync(1f, 0f, 0.5f, token);
            if (token.IsCancellationRequested) return;

            // Step 12: Fade out again so the curtain close happens off screen
            UpdateStatus("Fading out...");
            await FadeScreenAsync(0f, 1f, _fadeDuration, token);
            if (token.IsCancellationRequested) return;

            // Step 13: Close curtains, then take the music down under the black
            UpdateStatus("Closing curtains...");
            PlaySound(_curtainSound);
            await AnimateCurtainsAsync(false, 2f, token);
            if (token.IsCancellationRequested) return;
            SetStageElementsActive(false);
            await FadeMusicAsync(_musicVolume, 0f, 1f, token);
            if (token.IsCancellationRequested) return;

            // Step 14: Fade back in
            UpdateStatus("Fading in...");
            await FadeScreenAsync(1f, 0f, _fadeDuration, token);
            if (token.IsCancellationRequested) return;
            
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
        private async UdonTask FadeScreenAsync(float from, float to, float duration, CancellationToken token)
        {
            if (_fadeCanvasGroup == null)
            {
                await UdonTask.Delay(duration);
                return;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (token.IsCancellationRequested) return;

                float t = elapsed / duration;
                _fadeCanvasGroup.alpha = Mathf.Lerp(from, to, t);

                await UdonTask.Yield(); // Wait one frame
                elapsed += Time.deltaTime;
            }

            _fadeCanvasGroup.alpha = to;
        }

        /// <summary>
        /// Fades the music bed. Returning early on cancellation deliberately skips the final snap, so a
        /// skipped cutscene leaves the volume where ResetCutscene puts it rather than where this was heading.
        /// </summary>
        private async UdonTask FadeMusicAsync(float from, float to, float duration, CancellationToken token)
        {
            if (_musicSource == null || _musicSource.clip == null)
            {
                await UdonTask.Delay(duration);
                return;
            }

            _musicSource.volume = from;
            if (to > 0f) _musicSource.Play();

            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (token.IsCancellationRequested) return;

                _musicSource.volume = Mathf.Lerp(from, to, elapsed / duration);

                await UdonTask.Yield();
                elapsed += Time.deltaTime;
            }

            _musicSource.volume = to;
            if (to <= 0f) _musicSource.Stop();
        }
        
        /// <summary>
        /// Fades the title text alpha.
        /// </summary>
        private async UdonTask FadeTitleAsync(float from, float to, float duration, CancellationToken token)
        {
            if (_titleCanvasGroup == null)
            {
                await UdonTask.Delay(duration);
                return;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (token.IsCancellationRequested) return;

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
        private async UdonTask AnimateCurtainsAsync(bool opening, float duration, CancellationToken token)
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
                if (token.IsCancellationRequested) return;

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
        private float GetCameraMoveDuration(int waypointIndex, float fallbackDuration)
        {
            if (_cameraMoveSpeed <= 0f || _cameraRig == null || _cameraWaypoints == null ||
                waypointIndex < 0 || waypointIndex >= _cameraWaypoints.Length ||
                _cameraWaypoints[waypointIndex] == null)
            {
                return fallbackDuration;
            }

            float distance = Vector3.Distance(_cameraRig.position, _cameraWaypoints[waypointIndex].position);
            return Mathf.Max(0.1f, distance / _cameraMoveSpeed);
        }

        private async UdonTask MoveCameraToWaypointAsync(int waypointIndex, float duration, CancellationToken token)
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
                if (token.IsCancellationRequested) return;

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
        private async UdonTask SpotlightSequenceAsync(float duration, CancellationToken token)
        {
            if (_spotlight == null)
            {
                await UdonTask.Delay(duration);
                return;
            }
            
            if (_effects != null)
            {
                _effects.PlaySparkles();
                _effects.StartStageLights();
            }
            
            float elapsed = 0f;
            while (elapsed < duration)
            {
                if (token.IsCancellationRequested) return;

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
            
            if (_effects != null)
            {
                _effects.StopSparkles();
                _effects.StopStageLights();
            }
        }
        
        // ========================================
        // UTILITY METHODS
        // ========================================
        
        private float SmoothStep(float t)
        {
            return t * t * (3f - 2f * t);
        }

        private void AutoWireMissingReferences()
        {
            Transform searchRoot = transform.parent != null ? transform.parent : transform;

            if (_fadeCanvasGroup == null) _fadeCanvasGroup = FindCanvasGroupByName(searchRoot, "Fade");
            if (_titleText == null) _titleText = FindTextByName(searchRoot, "Title");
            if (_titleCanvasGroup == null) _titleCanvasGroup = FindCanvasGroupByName(searchRoot, "Title");
            if (_statusText == null) _statusText = FindTextByName(searchRoot, "Status");
            if (_playButton == null) _playButton = FindGameObjectByName(searchRoot, "PlayButton");
            if (_spotlight == null) _spotlight = FindLightByName(searchRoot, "Spotlight");
            if (_effects == null) _effects = FindTheaterEffects(searchRoot);
            if (_musicSource == null) _musicSource = FindAudioSourceByName(searchRoot, "Music");
            if (_sfxSource == null) _sfxSource = FindAudioSourceByName(searchRoot, "SFX");
            // "CameraRig" only. The old fallback matched anything named ReferenceCamera, which in a
            // VRChat scene is the world's own reference camera -- flying that around mid-cutscene would
            // drag every player's post-processing reference along with it.
            if (_cameraRig == null) _cameraRig = FindTransformByName(searchRoot, "CameraRig");

            if (_cameraWaypoints == null || _cameraWaypoints.Length == 0)
            {
                Transform waypointRoot = FindTransformByName(searchRoot, "CameraWaypoints");
                if (waypointRoot != null && waypointRoot.childCount > 0)
                {
                    Transform[] waypoints = new Transform[waypointRoot.childCount];
                    for (int i = 0; i < waypointRoot.childCount; i++)
                    {
                        waypoints[i] = waypointRoot.GetChild(i);
                    }

                    _cameraWaypoints = waypoints;
                }
            }

            // "StageProps", not "Stage". FindTransformByName matches on Contains, so the old lookup
            // resolved to the stage platform and Start() then deactivated the floor the actor stands
            // on -- the platform blinked out of the world instead of dressing appearing on it.
            if (_stageElements == null || _stageElements.Length == 0)
            {
                GameObject stageProps = FindGameObjectByName(searchRoot, "StageProps");
                if (stageProps != null && stageProps != gameObject)
                {
                    _stageElements = new GameObject[] { stageProps };
                }
            }
        }

        private bool NameContains(Transform candidate, string namePart)
        {
            if (candidate == null || string.IsNullOrEmpty(namePart) || string.IsNullOrEmpty(candidate.name)) return false;
            return candidate.name.Contains(namePart);
        }

        private Transform FindTransformByName(Transform root, string namePart)
        {
            if (root == null) return null;
            if (NameContains(root, namePart)) return root;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindTransformByName(root.GetChild(i), namePart);
                if (found != null) return found;
            }

            return null;
        }

        private GameObject FindGameObjectByName(Transform root, string namePart)
        {
            Transform found = FindTransformByName(root, namePart);
            return found != null ? found.gameObject : null;
        }

        private CanvasGroup FindCanvasGroupByName(Transform root, string namePart)
        {
            if (root == null) return null;

            if (NameContains(root, namePart))
            {
                CanvasGroup component = root.GetComponent<CanvasGroup>();
                if (component != null) return component;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                CanvasGroup found = FindCanvasGroupByName(root.GetChild(i), namePart);
                if (found != null) return found;
            }

            return null;
        }

        private TMPro.TextMeshProUGUI FindTextByName(Transform root, string namePart)
        {
            if (root == null) return null;

            if (NameContains(root, namePart))
            {
                TMPro.TextMeshProUGUI component = root.GetComponent<TMPro.TextMeshProUGUI>();
                if (component != null) return component;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                TMPro.TextMeshProUGUI found = FindTextByName(root.GetChild(i), namePart);
                if (found != null) return found;
            }

            return null;
        }

        private Light FindLightByName(Transform root, string namePart)
        {
            if (root == null) return null;

            if (NameContains(root, namePart))
            {
                Light component = root.GetComponent<Light>();
                if (component != null) return component;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Light found = FindLightByName(root.GetChild(i), namePart);
                if (found != null) return found;
            }

            return null;
        }

        private AudioSource FindAudioSourceByName(Transform root, string namePart)
        {
            if (root == null) return null;

            if (NameContains(root, namePart))
            {
                AudioSource component = root.GetComponent<AudioSource>();
                if (component != null) return component;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                AudioSource found = FindAudioSourceByName(root.GetChild(i), namePart);
                if (found != null) return found;
            }

            return null;
        }

        private TheaterEffects FindTheaterEffects(Transform root)
        {
            if (root == null) return null;

            TheaterEffects component = root.GetComponent<TheaterEffects>();
            if (component != null) return component;

            for (int i = 0; i < root.childCount; i++)
            {
                TheaterEffects found = FindTheaterEffects(root.GetChild(i));
                if (found != null) return found;
            }

            return null;
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
            // Stop whatever is still awaiting before putting the stage back, or the running sequence
            // keeps animating over the reset state until it runs out of steps.
            if (_cancellation != null)
            {
                _cancellation.Cancel();
                _cancelFrame = Time.frameCount;
            }

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

            if (_cameraRig != null)
            {
                _cameraRig.position = _cameraHomePosition;
                _cameraRig.rotation = _cameraHomeRotation;
            }

            // Particles and stage lights are started mid-sequence, so a cancelled run would leave them
            // burning; StopAllEffects is the matching teardown for every one of them.
            if (_effects != null) _effects.StopAllEffects();

            if (_musicSource != null)
            {
                _musicSource.Stop();
                _musicSource.volume = 0f;
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
            if (Time.frameCount == _cancelFrame) return;

            // Fire off the async cutscene!
            // The compiler generates the state machine - we just write clean code.
            //
            // Nothing is committed here on purpose. A cancelled sequence only unwinds when its state
            // machine next resumes, so a start issued in the same frame as a cancel is refused by the
            // generated guard -- and marking the cutscene as playing before that point would hide the
            // play button forever with nothing running. Letting the body below do it means a refused
            // start changes nothing at all and the next press just works.
            _ = PlayCutsceneAsync(BeginSequence());
        }

        public void OnSkipCutscene()
        {
            if (!_isPlaying) return;

            // ResetCutscene cancels the token the running sequence is holding, so it stops at its next
            // await instead of animating on top of the state we are about to restore.
            ResetCutscene();

            CELogger.Info("Cutscene", "Cutscene skipped");
        }

        /// <summary>
        /// Starts a new cancellable sequence and stops whatever was running. A fresh source each time:
        /// CancellationTokenSource.Reset() would resurrect a token an older sequence is still holding.
        /// </summary>
        private CancellationToken BeginSequence()
        {
            if (_cancellation != null)
            {
                _cancellation.Cancel();
                _cancelFrame = Time.frameCount;

                // A sequence cancelled mid-flight never reaches its own teardown, so anything it switched
                // on -- sparkles, stage lights -- would still be running when the next one starts.
                if (_effects != null) _effects.StopAllEffects();
            }

            _cancellation = new CancellationTokenSource();
            return _cancellation.Token;
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
            if (_isPlaying) return;
            if (Time.frameCount == _cancelFrame) return;
            _ = DemoFadeAsync(BeginSequence());
        }

        private async UdonTask DemoFadeAsync(CancellationToken token)
        {
            await FadeScreenAsync(0f, 1f, 0.5f, token);
            if (token.IsCancellationRequested) return;
            await UdonTask.Delay(0.5f);
            if (token.IsCancellationRequested) return;
            await FadeScreenAsync(1f, 0f, 0.5f, token);
        }

        /// <summary>
        /// Demo button to show curtain animation with async.
        /// </summary>
        public void OnDemoCurtains()
        {
            if (_isPlaying) return;
            if (Time.frameCount == _cancelFrame) return;
            _ = DemoCurtainsAsync(BeginSequence());
        }

        private async UdonTask DemoCurtainsAsync(CancellationToken token)
        {
            SetStageElementsActive(true);
            PlaySound(_curtainSound);
            await AnimateCurtainsAsync(true, 1.5f, token);
            if (token.IsCancellationRequested) return;
            await UdonTask.Delay(1f);
            if (token.IsCancellationRequested) return;
            PlaySound(_curtainSound);
            await AnimateCurtainsAsync(false, 1.5f, token);
            if (token.IsCancellationRequested) return;
            SetStageElementsActive(false);
        }

        /// <summary>
        /// Demo button to show spotlight sequence with async.
        /// </summary>
        public void OnDemoSpotlight()
        {
            if (_isPlaying) return;
            if (Time.frameCount == _cancelFrame) return;
            _ = SpotlightSequenceAsync(3f, BeginSequence());
        }
    }
}
