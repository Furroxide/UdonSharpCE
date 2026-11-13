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
    /// 4. Optionally assign a filter Dropdown and an auto-scroll Toggle, and point their On Value Changed
    ///    at UdonBehaviour.SendCustomEvent with "OnFilterDropdownChanged" / "OnAutoScrollToggleChanged"
    /// The package ships no prefab for this; the steps above are the whole setup.
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

        [Header("UI Controls (optional)")]
        [Tooltip("Dropdown that sets the level filter. Its options must be Trace, Debug, Info, Warning, Error, in that order. Wire its On Value Changed to UdonBehaviour.SendCustomEvent with \"OnFilterDropdownChanged\".")]
        [SerializeField] private Dropdown filterDropdown;

        [Tooltip("TextMeshPro dropdown, read when the legacy Filter Dropdown is empty. Same options and wiring: UdonBehaviour.SendCustomEvent with \"OnFilterDropdownChanged\".")]
        [SerializeField] private TMP_Dropdown filterDropdownTMP;

        [Tooltip("Toggle that turns auto-scroll on and off. Wire its On Value Changed to UdonBehaviour.SendCustomEvent with \"OnAutoScrollToggleChanged\".")]
        [SerializeField] private UnityEngine.UI.Toggle autoScrollToggle;

        [Header("Configuration")]
        [Tooltip("Maximum number of log entries kept, of every level. The level filter applies when they are displayed.")]
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
            EnsureInitialized();

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

            EnsureInitialized();

            // Every entry is stored; minDisplayLevel is applied when the text is built. Dropping entries
            // here as well made the filter destructive: anything logged while it was raised was gone for
            // good, so lowering it again showed a console with holes in it.

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
            EnsureInitialized();

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

        #region UI Event Handlers

        // A uGUI control reaches an UdonSharpBehaviour through UdonBehaviour.SendCustomEvent, which carries
        // no argument, and U# exports a method that takes parameters under a generated name. So only the
        // parameterless methods below can be wired to a control; each one reads what it needs from the
        // control itself.

        /// <summary>
        /// Clears the log. Wire a Button's On Click to UdonBehaviour.SendCustomEvent with "OnClearButtonClicked".
        /// </summary>
        public void OnClearButtonClicked()
        {
            Clear();
        }

        /// <summary>
        /// Hides the console. Wire a Button's On Click to UdonBehaviour.SendCustomEvent with "OnCloseButtonClicked".
        /// </summary>
        public void OnCloseButtonClicked()
        {
            Hide();
        }

        /// <summary>
        /// Applies the option selected in the Filter Dropdown, or in Filter Dropdown TMP when the legacy one
        /// is not assigned. Wire the dropdown's On Value Changed to UdonBehaviour.SendCustomEvent with
        /// "OnFilterDropdownChanged". The options must be Trace, Debug, Info, Warning, Error, in that order.
        /// Does nothing when neither dropdown is assigned.
        /// </summary>
        public void OnFilterDropdownChanged()
        {
            if (filterDropdown != null)
            {
                SetFilter((LogLevel)filterDropdown.value);
            }
            else if (filterDropdownTMP != null)
            {
                SetFilter((LogLevel)filterDropdownTMP.value);
            }
        }

        /// <summary>
        /// Applies the state of the Auto Scroll Toggle. Wire the toggle's On Value Changed to
        /// UdonBehaviour.SendCustomEvent with "OnAutoScrollToggleChanged". Does nothing when no toggle is assigned.
        /// </summary>
        public void OnAutoScrollToggleChanged()
        {
            if (autoScrollToggle != null)
            {
                autoScroll = autoScrollToggle.isOn;
            }
        }

        /// <summary>
        /// Sets the level filter from an option index (0=Trace, 1=Debug, 2=Info, 3=Warning, 4=Error).
        /// For calls from code: a Dropdown cannot reach a method with a parameter, so wire it to
        /// <see cref="OnFilterDropdownChanged"/> instead.
        /// </summary>
        public void OnFilterChanged(int filterIndex)
        {
            SetFilter((LogLevel)filterIndex);
        }

        /// <summary>
        /// Turns auto-scroll on or off. For calls from code: a Toggle cannot reach a method with a parameter,
        /// so wire it to <see cref="OnAutoScrollToggleChanged"/> instead.
        /// </summary>
        public void OnAutoScrollChanged(bool value)
        {
            autoScroll = value;
        }

        #endregion

        #region Private Helpers

        /// <summary>
        /// Allocates the log buffer on first use.
        ///
        /// Udon runs a public method on a behaviour whose Start has not happened yet -- another
        /// behaviour's earlier Start, or a console object that begins inactive. When only Start allocated
        /// the buffer, the first AddLog from such a caller dereferenced null, and an Udon exception halts
        /// the behaviour for the rest of the session: no log display and no toggle key.
        /// </summary>
        private void EnsureInitialized()
        {
            if (_logBuffer != null) return;

            // The ring index is taken modulo the buffer length, so a zero or negative size typed into the
            // inspector would be a division by zero on the first log.
            int capacity = maxLogEntries;
            if (capacity < 1) capacity = 1;

            _logBuffer = new LogEntry[capacity];
            _logHead = 0;
            _logCount = 0;
            _needsRefresh = false;
            _cachedText = "";
        }

        private void RefreshDisplay()
        {
            EnsureInitialized();

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
