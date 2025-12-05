using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using VRC.SDKBase;

namespace UdonSharp.CE.DevTools
{
    /// <summary>
    /// In-world debug console for displaying logs and errors.
    /// Attach to a Canvas with TextMeshPro or Text components.
    ///
    /// Features:
    /// - Displays logs from CELogger
    /// - Keyboard toggle (default: backtick/tilde key)
    /// - Log level filtering
    /// - Auto-scroll option
    /// - Clear functionality
    /// </summary>
    /// <remarks>
    /// Setup:
    /// 1. Create a Canvas in your scene
    /// 2. Add a Panel with a Scroll View containing a TextMeshProUGUI text component
    /// 3. Attach this component and wire up the references
    /// 4. Use the provided prefab from Samples~/CE/DebugConsole/ for quick setup
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class CEDebugConsole : UdonSharpBehaviour
    {
        #region Inspector Fields

        [Header("UI References")]
        [Tooltip("The TextMeshPro text component for log display. Falls back to legacy Text if null.")]
        [SerializeField] private TextMeshProUGUI logTextTMP;

        [Tooltip("Legacy Unity Text component (used if TMP is null).")]
        [SerializeField] private Text logTextLegacy;

        [Tooltip("ScrollRect for auto-scrolling (optional).")]
        [SerializeField] private ScrollRect scrollRect;

        [Tooltip("The panel/container to show/hide.")]
        [SerializeField] private GameObject consolePanel;

        [Header("Configuration")]
        [Tooltip("Maximum number of log entries to display.")]
        [SerializeField] private int maxLogEntries = 100;

        [Tooltip("Minimum log level to display.")]
        [SerializeField] private LogLevel minDisplayLevel = LogLevel.Debug;

        [Tooltip("Show timestamps in log entries.")]
        [SerializeField] private bool showTimestamps = true;

        [Tooltip("Show frame numbers in log entries.")]
        [SerializeField] private bool showFrameNumbers = false;

        [Tooltip("Use rich text coloring for log levels.")]
        [SerializeField] private bool useRichText = true;

        [Tooltip("Auto-scroll to bottom when new logs arrive.")]
        [SerializeField] private bool autoScroll = true;

        [Header("Controls")]
        [Tooltip("Key to toggle console visibility.")]
        [SerializeField] private KeyCode toggleKey = KeyCode.BackQuote;

        [Tooltip("Start with console visible.")]
        [SerializeField] private bool startVisible = false;

        #endregion

        #region Private State

        private LogEntry[] _logBuffer;
        private int _logHead;
        private int _logCount;
        private bool _isVisible;
        private bool _needsRefresh;
        private string _cachedText;

        #endregion

        #region Lifecycle

        private void Start()
        {
            // Initialize buffer
            _logBuffer = new LogEntry[maxLogEntries];
            _logHead = 0;
            _logCount = 0;
            _needsRefresh = false;
            _cachedText = "";

            // Set initial visibility
            _isVisible = startVisible;
            if (consolePanel != null)
            {
                consolePanel.SetActive(_isVisible);
            }

            // Register with logger
            CELogger.RegisterConsole(this);

            // Log startup
            CELogger.Info("DevTools", "Debug console initialized");
        }

        private void Update()
        {
            // Check toggle key
            if (Input.GetKeyDown(toggleKey))
            {
                Toggle();
            }

            // Refresh display if needed and visible
            if (_needsRefresh && _isVisible)
            {
                RefreshDisplay();
                _needsRefresh = false;
            }
        }

        private void OnDestroy()
        {
            CELogger.UnregisterConsole(this);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Adds a log entry to the console.
        /// Called by CELogger when logs are created.
        /// </summary>
        public void AddLog(LogEntry entry)
        {
            if (entry == null) return;
            if (entry.Level < minDisplayLevel) return;

            // Add to circular buffer
            int index = (_logHead + _logCount) % _logBuffer.Length;

            if (_logCount < _logBuffer.Length)
            {
                _logCount++;
            }
            else
            {
                // Buffer full, advance head
                _logHead = (_logHead + 1) % _logBuffer.Length;
            }

            _logBuffer[index] = entry;
            _needsRefresh = true;
        }

        /// <summary>
        /// Adds a log entry with the specified message and level.
        /// </summary>
        public void AddLog(string message, LogLevel level)
        {
            AddLog(new LogEntry(message, level));
        }

        /// <summary>
        /// Clears all displayed logs.
        /// </summary>
        public void Clear()
        {
            System.Array.Clear(_logBuffer, 0, _logBuffer.Length);
            _logHead = 0;
            _logCount = 0;
            _cachedText = "";
            _needsRefresh = true;

            if (_isVisible)
            {
                RefreshDisplay();
            }
        }

        /// <summary>
        /// Shows the console panel.
        /// </summary>
        public void Show()
        {
            _isVisible = true;
            if (consolePanel != null)
            {
                consolePanel.SetActive(true);
            }
            RefreshDisplay();
        }

        /// <summary>
        /// Hides the console panel.
        /// </summary>
        public void Hide()
        {
            _isVisible = false;
            if (consolePanel != null)
            {
                consolePanel.SetActive(false);
            }
        }

        /// <summary>
        /// Toggles console visibility.
        /// </summary>
        public void Toggle()
        {
            if (_isVisible)
            {
                Hide();
            }
            else
            {
                Show();
            }
        }

        /// <summary>
        /// Sets the minimum display level filter.
        /// </summary>
        public void SetFilter(LogLevel level)
        {
            minDisplayLevel = level;
            _needsRefresh = true;
        }

        /// <summary>
        /// Gets whether the console is currently visible.
        /// </summary>
        public bool IsVisible => _isVisible;

        #endregion

        #region UI Event Handlers (for buttons)

        /// <summary>
        /// Called by Clear button.
        /// </summary>
        public void OnClearButtonClicked()
        {
            Clear();
        }

        /// <summary>
        /// Called by Close button.
        /// </summary>
        public void OnCloseButtonClicked()
        {
            Hide();
        }

        /// <summary>
        /// Called by filter dropdown (0=Trace, 1=Debug, 2=Info, 3=Warning, 4=Error).
        /// </summary>
        public void OnFilterChanged(int filterIndex)
        {
            SetFilter((LogLevel)filterIndex);
        }

        /// <summary>
        /// Called by auto-scroll toggle.
        /// </summary>
        public void OnAutoScrollChanged(bool value)
        {
            autoScroll = value;
        }

        #endregion

        #region Private Helpers

        private void RefreshDisplay()
        {
            // Build display text
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            for (int i = 0; i < _logCount; i++)
            {
                int index = (_logHead + i) % _logBuffer.Length;
                LogEntry entry = _logBuffer[index];

                if (entry == null) continue;
                if (entry.Level < minDisplayLevel) continue;

                string line;
                if (useRichText)
                {
                    line = entry.FormatRichText(showTimestamps, showFrameNumbers);
                }
                else
                {
                    line = entry.Format(showTimestamps, showFrameNumbers);
                }

                sb.AppendLine(line);
            }

            _cachedText = sb.ToString();

            // Update UI
            if (logTextTMP != null)
            {
                logTextTMP.text = _cachedText;
            }
            else if (logTextLegacy != null)
            {
                logTextLegacy.text = _cachedText;
            }

            // Auto-scroll
            if (autoScroll && scrollRect != null)
            {
                // Need to wait a frame for layout update
                SendCustomEventDelayedFrames(nameof(_ScrollToBottom), 1);
            }
        }

        public void _ScrollToBottom()
        {
            if (scrollRect != null)
            {
                scrollRect.verticalNormalizedPosition = 0f;
            }
        }

        #endregion
    }
}
