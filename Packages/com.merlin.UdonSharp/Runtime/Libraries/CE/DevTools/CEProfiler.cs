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
    /// - Manual section timing with BeginSection/EndSection (sections may nest)
    /// - Optional UI overlay display
    /// </summary>
    /// <remarks>
    /// Section timing reads Time.realtimeSinceStartup, a float of seconds since the client or editor
    /// process started, so its resolution degrades with uptime: about 0.24 ms after 34 minutes, 0.5 ms
    /// after 68 minutes, 1 ms after 2.3 hours and 2 ms after 4.5 hours. A single call to a section
    /// shorter than that reads as 0 or as one whole step. The per-section totals and averages stay
    /// meaningful over many calls; an individual sub-millisecond sample in a long-running session does not.
    /// </remarks>
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
        private bool _sectionLimitWarned;

        // Open sections, innermost last. One slot per BeginSection that has not met its EndSection yet;
        // a slot holds -1 when that BeginSection could not be given a section to time.
        private const int MaxOpenSections = 16;
        private int[] _openSectionIndices;
        private float[] _openSectionStarts;
        private int _openSectionCount;

        // UI update
        private float _lastDisplayUpdate;

        #endregion

        #region Lifecycle

        private void Start()
        {
            // Initialize frame and section timing
            EnsureInitialized();

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
            EnsureInitialized();

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
        /// <remarks>
        /// Sections nest: each EndSection closes the most recent BeginSection still open, and an outer
        /// section's time includes the sections timed inside it.
        /// </remarks>
        public void BeginSection(string sectionName)
        {
            EnsureInitialized();

            // A section that cannot be timed -- no name, or the section table is full -- still takes a
            // slot. Returning without one left the caller's matching EndSection to close whichever
            // section was open around it instead.
            int index = -1;
            if (!string.IsNullOrEmpty(sectionName))
            {
                index = FindOrCreateSection(sectionName);
            }

            if (_openSectionCount >= MaxOpenSections)
            {
                // Full, which means EndSection is not being reached for some BeginSection: an early
                // return between the two is enough. Drop the oldest open section rather than refuse the
                // new one, or one leaked BeginSection per frame would fill this for good and silently
                // stop every later section from being timed.
                for (int i = 1; i < _openSectionCount; i++)
                {
                    _openSectionIndices[i - 1] = _openSectionIndices[i];
                    _openSectionStarts[i - 1] = _openSectionStarts[i];
                }

                _openSectionCount--;
            }

            _openSectionIndices[_openSectionCount] = index;
            _openSectionStarts[_openSectionCount] = Time.realtimeSinceStartup;
            _openSectionCount++;
        }

        /// <summary>
        /// Ends the most recently begun section that is still open.
        /// </summary>
        public void EndSection()
        {
            if (_openSectionCount <= 0) return;

            // One start time and one index used to be all there was, so a BeginSection inside an open
            // section overwrote it: the inner section got its time and the outer one recorded nothing.
            _openSectionCount--;

            int index = _openSectionIndices[_openSectionCount];
            if (index < 0) return;

            float elapsed = Time.realtimeSinceStartup - _openSectionStarts[_openSectionCount];
            _sectionTotals[index] += elapsed;
            _sectionCounts[index]++;
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
            EnsureInitialized();

            string[] result = new string[_sectionCount];
            System.Array.Copy(_sectionNames, result, _sectionCount);
            return result;
        }

        /// <summary>
        /// Resets timing data for all sections.
        /// </summary>
        public void ResetSections()
        {
            EnsureInitialized();

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

        /// <summary>
        /// Allocates the timing buffers on first use.
        ///
        /// Udon runs a public method on a behaviour whose Start has not happened yet -- another
        /// behaviour's earlier Start, or a profiler object that begins inactive, which is the usual state
        /// for a debug HUD. When only Start allocated these, the first BeginSection from such a caller
        /// indexed a null array, and an Udon exception halts the behaviour for the rest of the session.
        /// </summary>
        private void EnsureInitialized()
        {
            if (_frameTimes != null) return;

            // The sample index is taken modulo this length, so a zero or negative count typed into the
            // inspector would be a division by zero in the first Update.
            int frameCapacity = sampleCount;
            if (frameCapacity < 1) frameCapacity = 1;

            // Zero is a legitimate way to switch section timing off; a negative size is not an array.
            int sectionCapacity = maxSections;
            if (sectionCapacity < 0) sectionCapacity = 0;

            _sampleIndex = 0;
            _samplesFilled = 0;

            _sectionNames = new string[sectionCapacity];
            _sectionTotals = new float[sectionCapacity];
            _sectionCounts = new int[sectionCapacity];
            _sectionCount = 0;

            _openSectionIndices = new int[MaxOpenSections];
            _openSectionStarts = new float[MaxOpenSections];
            _openSectionCount = 0;

            // Last, because this is the array the guard above tests.
            _frameTimes = new float[frameCapacity];
        }

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

            // Measured against the allocated table rather than maxSections: the two differ when the
            // inspector value was clamped, and it is the table that gets indexed below.
            if (_sectionCount >= _sectionNames.Length)
            {
                // Once. This is reached on every BeginSection for a name that did not fit, which is every
                // frame for a section timed in Update.
                if (!_sectionLimitWarned)
                {
                    _sectionLimitWarned = true;
                    CELogger.Warning("Profiler", $"Maximum sections ({_sectionNames.Length}) reached, cannot add '{name}'");
                }

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
