using UdonSharp;
using UnityEngine;

namespace UdonSharp.CE.DevTools
{
    /// <summary>
    /// Simple performance monitoring utility for UdonSharpCE demos.
    /// Tracks frame time, update counts, and basic performance metrics.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class PerformanceMonitor : UdonSharpBehaviour
    {
        [Header("Display")]
        [SerializeField] private TMPro.TextMeshProUGUI _displayText;
        
        [Header("Settings")]
        [SerializeField] private float _updateInterval = 0.5f;
        
        // Performance tracking
        private float _deltaTime;
        private float _fps;
        private float _updateTimer;
        private int _frameCount;
        
        // Stats
        private float _minFps = float.MaxValue;
        private float _maxFps = 0f;
        private float _avgFps;
        private int _avgSamples;
        
        void Update()
        {
            Sample(Time.unscaledDeltaTime);
        }

        private void Sample(float frameTime)
        {
            // Seed the running average with the first real frame time. Starting it at zero left it short
            // of the true frame time by a factor of 0.9^n after n frames, so the first reading came out
            // high -- 62.7 at a steady 60 fps, 37.8 at 30 -- and that reading is the one Max FPS keeps.
            if (_deltaTime <= 0f)
                _deltaTime = frameTime;
            else
                _deltaTime += (frameTime - _deltaTime) * 0.1f;

            _frameCount++;
            _updateTimer += frameTime;
            
            // Update display periodically
            if (_updateTimer >= _updateInterval)
            {
                // No frame has taken any time yet, so there is no rate to report and nothing to record.
                if (_deltaTime <= 0f)
                    return;

                _fps = 1.0f / _deltaTime;
                
                // Track min/max/avg
                if (_fps < _minFps) _minFps = _fps;
                if (_fps > _maxFps) _maxFps = _fps;
                
                _avgFps = ((_avgFps * _avgSamples) + _fps) / (_avgSamples + 1);
                _avgSamples++;
                
                UpdateDisplay();
                _updateTimer = 0f;
            }
        }
        
        private void UpdateDisplay()
        {
            if (_displayText == null) return;
            
            _displayText.text = $"FPS: {_fps:F1}\n" +
                               $"Min: {_minFps:F1} | Max: {_maxFps:F1}\n" +
                               $"Avg: {_avgFps:F1}\n" +
                               $"Frame: {_deltaTime * 1000f:F2}ms";
        }
        
        /// <summary>
        /// Get current FPS.
        /// </summary>
        public float GetFPS() => _fps;
        
        /// <summary>
        /// Get current frame time in milliseconds.
        /// </summary>
        public float GetFrameTimeMs() => _deltaTime * 1000f;
        
        /// <summary>
        /// Get minimum recorded FPS, or 0 when nothing has been recorded yet.
        /// </summary>
        /// <remarks>
        /// The tracker starts at float.MaxValue so the first reading always replaces it; before that
        /// reading (and again after ResetStats) that sentinel is not a frame rate and must not be shown.
        /// </remarks>
        public float GetMinFPS() => _avgSamples > 0 ? _minFps : 0f;
        
        /// <summary>
        /// Get maximum recorded FPS.
        /// </summary>
        public float GetMaxFPS() => _maxFps;
        
        /// <summary>
        /// Get average FPS.
        /// </summary>
        public float GetAvgFPS() => _avgFps;
        
        /// <summary>
        /// Reset all statistics.
        /// </summary>
        public void ResetStats()
        {
            _minFps = float.MaxValue;
            _maxFps = 0f;
            _avgFps = 0f;
            _avgSamples = 0;
        }
    }
}








































