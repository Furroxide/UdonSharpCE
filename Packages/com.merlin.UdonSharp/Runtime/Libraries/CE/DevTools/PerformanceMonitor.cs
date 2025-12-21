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
            // Accumulate frame time
            _deltaTime += (Time.unscaledDeltaTime - _deltaTime) * 0.1f;
            _frameCount++;
            _updateTimer += Time.unscaledDeltaTime;
            
            // Update display periodically
            if (_updateTimer >= _updateInterval)
            {
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
        /// Get minimum recorded FPS.
        /// </summary>
        public float GetMinFPS() => _minFps;
        
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


























