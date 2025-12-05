using UdonSharp;
using UnityEngine;
using UdonSharp.CE.DevTools;

namespace UdonSharp.CE.Samples
{
    /// <summary>
    /// Demo script showing how to use CELogger and CEDebugConsole.
    /// Generates sample log messages at various levels.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class DebugConsoleDemo : UdonSharpBehaviour
    {
        [Header("Demo Settings")]
        [SerializeField] private bool logOnStart = true;
        [SerializeField] private bool continuousLogging = false;
        [SerializeField] private float logInterval = 2f;

        [Header("References")]
        [SerializeField] private CEProfiler profiler;

        private float _lastLogTime;
        private int _logCounter;

        private void Start()
        {
            if (logOnStart)
            {
                LogAllLevels();
            }
        }

        private void Update()
        {
            if (continuousLogging && Time.time - _lastLogTime >= logInterval)
            {
                _lastLogTime = Time.time;
                _logCounter++;
                CELogger.Info("Demo", $"Periodic log #{_logCounter}");
            }
        }

        /// <summary>
        /// Logs messages at all severity levels.
        /// </summary>
        public void LogAllLevels()
        {
            CELogger.Info("Demo", "=== CE.DevTools Demo ===");

            CELogger.Trace("Demo", "This is a TRACE message - most detailed level");
            CELogger.Debug("Demo", "This is a DEBUG message - development info");
            CELogger.Info("Demo", "This is an INFO message - general information");
            CELogger.Warning("Demo", "This is a WARNING message - potential issue");
            CELogger.Error("Demo", "This is an ERROR message - something went wrong");

            CELogger.Info("Demo", "Press ` (backtick) to toggle the console");
        }

        /// <summary>
        /// Logs a burst of messages for stress testing.
        /// </summary>
        public void LogBurst()
        {
            CELogger.Info("Demo", "Starting log burst...");

            for (int i = 0; i < 50; i++)
            {
                LogLevel level = (LogLevel)(i % 5);
                CELogger.Log($"Burst message {i + 1}/50", level);
            }

            CELogger.Info("Demo", "Log burst complete!");
        }

        /// <summary>
        /// Demonstrates tagged logging for filtering.
        /// </summary>
        public void LogWithTags()
        {
            CELogger.Info("Network", "Connection established");
            CELogger.Debug("Network", "Sending packet #1234");
            CELogger.Warning("Network", "High latency detected: 150ms");

            CELogger.Info("Gameplay", "Player spawned at origin");
            CELogger.Debug("Gameplay", "Loading inventory...");

            CELogger.Info("Audio", "Playing background music");
            CELogger.Debug("Audio", "Volume set to 0.8");

            CELogger.Info("Demo", "Tagged logging demo complete!");
        }

        /// <summary>
        /// Shows profiler integration.
        /// </summary>
        public void DemoProfiler()
        {
            if (profiler == null)
            {
                CELogger.Warning("Demo", "No profiler assigned");
                return;
            }

            CELogger.Info("Demo", "=== Profiler Demo ===");

            // Log current metrics
            float fps = profiler.GetFPS();
            float frameTime = profiler.GetAverageFrameTimeMs();

            CELogger.Info("Demo", $"Current FPS: {fps:F1}");
            CELogger.Info("Demo", $"Avg Frame Time: {frameTime:F2}ms");

            // Demo section timing
            profiler.BeginSection("TestSection");
            // Simulate work
            float sum = 0;
            for (int i = 0; i < 10000; i++)
            {
                sum += Mathf.Sin(i * 0.001f);
            }
            profiler.EndSection();

            float sectionTime = profiler.GetSectionAverageTime("TestSection");
            CELogger.Info("Demo", $"TestSection took: {sectionTime * 1000f:F3}ms");

            // Full summary
            profiler.LogSummary();
        }

        /// <summary>
        /// Changes the log level filter.
        /// </summary>
        public void SetLogLevelTrace() { CELogger.MinLevel = LogLevel.Trace; LogFilterChanged(); }
        public void SetLogLevelDebug() { CELogger.MinLevel = LogLevel.Debug; LogFilterChanged(); }
        public void SetLogLevelInfo() { CELogger.MinLevel = LogLevel.Info; LogFilterChanged(); }
        public void SetLogLevelWarning() { CELogger.MinLevel = LogLevel.Warning; LogFilterChanged(); }
        public void SetLogLevelError() { CELogger.MinLevel = LogLevel.Error; LogFilterChanged(); }

        private void LogFilterChanged()
        {
            CELogger.Info("Demo", $"Log filter set to: {CELogger.MinLevel}");
        }

        // UI Button Handlers
        public void OnLogAllLevelsClicked() { LogAllLevels(); }
        public void OnLogBurstClicked() { LogBurst(); }
        public void OnLogWithTagsClicked() { LogWithTags(); }
        public void OnDemoProfilerClicked() { DemoProfiler(); }
        public void OnToggleContinuousClicked() { continuousLogging = !continuousLogging; }
    }
}
