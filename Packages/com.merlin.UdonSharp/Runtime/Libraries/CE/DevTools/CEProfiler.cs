using UdonSharp;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace UdonSharp.CE.DevTools
{
    /// <summary>
    /// Basic profiler for measuring frame timing and Update costs.
    /// Provides FPS tracking, frame time averaging, and manual section timing.
    ///
    /// Features:
    /// - Automatic frame time capture
    /// - Rolling average calculations
    /// - Manual section timing with BeginSection/EndSection
    /// - Optional UI overlay display
    /// </summary>
    /// <example>
    /// <code>
    /// // Get profiler reference
    /// CEProfiler profiler = GetComponent<CEProfiler>();
    ///
    /// // Manual section timing
    /// profiler.BeginSection("Physics");
    /// // ... do physics work ...
    /// profiler.EndSection();
    ///
    /// // Get metrics
    /// float fps = profiler.GetFPS();
    /// float avgFrameTime = profiler.GetAverageFrameTime();
    ///
    /// // Log summary
    /// profiler.LogSummary();
    /// </code>
    /// </example>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CEProfiler : UdonSharpBehaviour
    {
        #region Inspector Fields

        [Header("Configuration")]
        [Tooltip("Number of frames to average for timing calculations.")]
        [SerializeField] private int sampleCount = 60;

        [Tooltip("Start profiling automatically on Start().")]
        [SerializeField] private bool autoStart = true;

        [Header("UI Display (Optional)")]
        [Tooltip("TextMeshPro text for FPS/timing display.")]
        [SerializeField] private TextMeshProUGUI displayTextTMP;

        [Tooltip("Legacy Text for FPS/timing display.")]
        [SerializeField] private Text displayTextLegacy;

        [Tooltip("Update interval for UI display in seconds.")]
        [SerializeField] private float displayUpdateInterval = 0.5f;

        [Header("Section Timing")]
        [Tooltip("Maximum number of named sections to track.")]
        [SerializeField] private int maxSections = 16;

        #endregion

        #region Private State

        // Frame timing
        private float[] _frameTimes;
        private int _sampleIndex;
        private int _samplesFilled;
        private bool _isRunning;

        // Section timing
        private string[] _sectionNames;
        private float[] _sectionTotals;
        private int[] _sectionCounts;
        private int _sectionCount;
        private float _currentSectionStart;
        private int _currentSectionIndex;

        // UI update
        private float _lastDisplayUpdate;

        #endregion

        #region Lifecycle

        private void Start()
        {
            // Initialize frame timing
            _frameTimes = new float[sampleCount];
            _sampleIndex = 0;
            _samplesFilled = 0;

            // Initialize section timing
            _sectionNames = new string[maxSections];
            _sectionTotals = new float[maxSections];
            _sectionCounts = new int[maxSections];
            _sectionCount = 0;
            _currentSectionIndex = -1;

            _lastDisplayUpdate = 0f;

            if (autoStart)
            {
                StartProfiling();
            }
        }

        private void Update()
        {
            if (!_isRunning) return;

            // Record frame time
            float deltaTime = Time.deltaTime;
            _frameTimes[_sampleIndex] = deltaTime;
            _sampleIndex = (_sampleIndex + 1) % _frameTimes.Length;

            if (_samplesFilled < _frameTimes.Length)
            {
                _samplesFilled++;
            }

            // Update display if configured
            if (displayTextTMP != null || displayTextLegacy != null)
            {
                if (Time.time - _lastDisplayUpdate >= displayUpdateInterval)
                {
                    UpdateDisplay();
                    _lastDisplayUpdate = Time.time;
                }
            }
        }

        #endregion

        #region Public API - Control

        /// <summary>
        /// Starts or resumes profiling.
        /// </summary>
        public void StartProfiling()
        {
            _isRunning = true;
            CELogger.Debug("Profiler", "Profiling started");
        }

        /// <summary>
        /// Stops profiling.
        /// </summary>
        public void StopProfiling()
        {
            _isRunning = false;
            CELogger.Debug("Profiler", "Profiling stopped");
        }

        /// <summary>
        /// Resets all timing data.
        /// </summary>
        public void Reset()
        {
            System.Array.Clear(_frameTimes, 0, _frameTimes.Length);
            _sampleIndex = 0;
            _samplesFilled = 0;

            System.Array.Clear(_sectionTotals, 0, _sectionTotals.Length);
            System.Array.Clear(_sectionCounts, 0, _sectionCounts.Length);

            CELogger.Debug("Profiler", "Profiling data reset");
        }

        /// <summary>
        /// Gets whether profiling is currently running.
        /// </summary>
        public bool IsRunning => _isRunning;

        #endregion

        #region Public API - Frame Timing

        /// <summary>
        /// Gets the average frame time in seconds over the sample window.
        /// </summary>
        public float GetAverageFrameTime()
        {
            if (_samplesFilled == 0) return 0f;

            float total = 0f;
            int count = _samplesFilled;

            for (int i = 0; i < count; i++)
            {
                total += _frameTimes[i];
            }

            return total / count;
        }

        /// <summary>
        /// Gets the average frame time in milliseconds.
        /// </summary>
        public float GetAverageFrameTimeMs()
        {
            return GetAverageFrameTime() * 1000f;
        }

        /// <summary>
        /// Gets the maximum frame time in the sample window.
        /// </summary>
        public float GetMaxFrameTime()
        {
            if (_samplesFilled == 0) return 0f;

            float max = 0f;
            int count = _samplesFilled;

            for (int i = 0; i < count; i++)
            {
                if (_frameTimes[i] > max)
                {
                    max = _frameTimes[i];
                }
            }

            return max;
        }

        /// <summary>
        /// Gets the minimum frame time in the sample window.
        /// </summary>
        public float GetMinFrameTime()
        {
            if (_samplesFilled == 0) return 0f;

            float min = float.MaxValue;
            int count = _samplesFilled;

            for (int i = 0; i < count; i++)
            {
                if (_frameTimes[i] < min)
                {
                    min = _frameTimes[i];
                }
            }

            return min;
        }

        /// <summary>
        /// Gets the average frames per second.
        /// </summary>
        public float GetFPS()
        {
            float avgTime = GetAverageFrameTime();
            if (avgTime <= 0f) return 0f;
            return 1f / avgTime;
        }

        /// <summary>
        /// Gets the current instantaneous frame time (last frame).
        /// </summary>
        public float GetCurrentFrameTime()
        {
            if (_samplesFilled == 0) return 0f;
            int lastIndex = (_sampleIndex - 1 + _frameTimes.Length) % _frameTimes.Length;
            return _frameTimes[lastIndex];
        }

        #endregion

        #region Public API - Section Timing

        /// <summary>
        /// Begins timing a named section.
        /// Call EndSection() when the section completes.
        /// </summary>
        public void BeginSection(string sectionName)
        {
            if (string.IsNullOrEmpty(sectionName)) return;

            // Find or create section
            int index = FindOrCreateSection(sectionName);
            if (index < 0) return;

            _currentSectionIndex = index;
            _currentSectionStart = Time.realtimeSinceStartup;
        }

        /// <summary>
        /// Ends the current section timing.
        /// </summary>
        public void EndSection()
        {
            if (_currentSectionIndex < 0) return;

            float elapsed = Time.realtimeSinceStartup - _currentSectionStart;
            _sectionTotals[_currentSectionIndex] += elapsed;
            _sectionCounts[_currentSectionIndex]++;

            _currentSectionIndex = -1;
        }

        /// <summary>
        /// Gets the total accumulated time for a section in seconds.
        /// </summary>
        public float GetSectionTotalTime(string sectionName)
        {
            int index = FindSection(sectionName);
            if (index < 0) return 0f;
            return _sectionTotals[index];
        }

        /// <summary>
        /// Gets the average time per call for a section in seconds.
        /// </summary>
        public float GetSectionAverageTime(string sectionName)
        {
            int index = FindSection(sectionName);
            if (index < 0) return 0f;
            if (_sectionCounts[index] == 0) return 0f;
            return _sectionTotals[index] / _sectionCounts[index];
        }

        /// <summary>
        /// Gets all registered section names.
        /// </summary>
        public string[] GetSectionNames()
        {
            string[] result = new string[_sectionCount];
            System.Array.Copy(_sectionNames, result, _sectionCount);
            return result;
        }

        /// <summary>
        /// Resets timing data for all sections.
        /// </summary>
        public void ResetSections()
        {
            System.Array.Clear(_sectionTotals, 0, _sectionTotals.Length);
            System.Array.Clear(_sectionCounts, 0, _sectionCounts.Length);
        }

        #endregion

        #region Public API - Summary

        /// <summary>
        /// Gets a formatted summary string of profiling data.
        /// </summary>
        public string GetSummary()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            sb.AppendLine("=== CE Profiler Summary ===");
            sb.AppendLine($"FPS: {GetFPS():F1}");
            sb.AppendLine($"Frame Time: {GetAverageFrameTimeMs():F2}ms (avg)");
            sb.AppendLine($"Frame Time: {GetMinFrameTime() * 1000f:F2}ms (min) / {GetMaxFrameTime() * 1000f:F2}ms (max)");

            if (_sectionCount > 0)
            {
                sb.AppendLine("\n--- Sections ---");
                for (int i = 0; i < _sectionCount; i++)
                {
                    string name = _sectionNames[i];
                    float total = _sectionTotals[i] * 1000f;
                    int count = _sectionCounts[i];
                    float avg = count > 0 ? total / count : 0f;
                    sb.AppendLine($"  {name}: {total:F2}ms total, {avg:F3}ms avg ({count} calls)");
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Logs the profiling summary to CELogger.
        /// </summary>
        public void LogSummary()
        {
            CELogger.Info("Profiler", GetSummary());
        }

        #endregion

        #region Private Helpers

        private int FindSection(string name)
        {
            for (int i = 0; i < _sectionCount; i++)
            {
                if (_sectionNames[i] == name)
                {
                    return i;
                }
            }
            return -1;
        }

        private int FindOrCreateSection(string name)
        {
            int index = FindSection(name);
            if (index >= 0) return index;

            if (_sectionCount >= maxSections)
            {
                CELogger.Warning("Profiler", $"Maximum sections ({maxSections}) reached, cannot add '{name}'");
                return -1;
            }

            index = _sectionCount;
            _sectionNames[index] = name;
            _sectionTotals[index] = 0f;
            _sectionCounts[index] = 0;
            _sectionCount++;

            return index;
        }

        private void UpdateDisplay()
        {
            string text = $"FPS: {GetFPS():F0}\nFrame: {GetAverageFrameTimeMs():F1}ms";

            if (displayTextTMP != null)
            {
                displayTextTMP.text = text;
            }
            else if (displayTextLegacy != null)
            {
                displayTextLegacy.text = text;
            }
        }

        #endregion
    }
}
