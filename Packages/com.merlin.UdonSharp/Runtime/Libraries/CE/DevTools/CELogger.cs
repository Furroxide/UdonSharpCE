using UnityEngine;

namespace UdonSharp.CE.DevTools
{
    /// <summary>
    /// Static logging utility for CE.DevTools.
    /// Provides structured logging with level filtering and optional console integration.
    ///
    /// Logs are sent to Unity's Debug.Log system and optionally to registered
    /// CEDebugConsole instances for in-world display.
    /// </summary>
    /// <example>
    /// <code>
    /// // Basic logging
    /// CELogger.Info("Player joined the game");
    /// CELogger.Warning("Low health detected");
    /// CELogger.Error("Failed to load data");
    ///
    /// // With tag for filtering
    /// CELogger.Log("Network", "Connection established", LogLevel.Info);
    ///
    /// // Set minimum level to reduce noise
    /// CELogger.MinLevel = LogLevel.Warning;  // Only show warnings and errors
    /// </code>
    /// </example>
    public static class CELogger
    {
        /// <summary>
        /// Minimum log level to output. Messages below this level are ignored.
        /// Default is Debug (shows Debug, Info, Warning, Error).
        /// </summary>
        public static LogLevel MinLevel = LogLevel.Debug;

        /// <summary>
        /// Whether to also output logs to Unity's Debug.Log system.
        /// Default is true.
        /// </summary>
        public static bool OutputToUnityLog = true;

        /// <summary>
        /// Prefix for all CE log messages in Unity console.
        /// </summary>
        private const string CEPrefix = "[CE]";

        // Registered console instances (using array since we can't use events in Udon)
        private static CEDebugConsole[] _consoles = new CEDebugConsole[8];
        private static int _consoleCount = 0;

        // Log buffer for late-registered consoles
        private static LogEntry[] _logBuffer = new LogEntry[100];
        private static int _logBufferHead = 0;
        private static int _logBufferCount = 0;

        #region Public Logging API

        /// <summary>
        /// Logs a message at the specified level.
        /// </summary>
        public static void Log(string message, LogLevel level = LogLevel.Info)
        {
            if (level < MinLevel) return;

            LogEntry entry = new LogEntry(message, level);
            ProcessLogEntry(entry);
        }

        /// <summary>
        /// Logs a message with a tag at the specified level.
        /// </summary>
        public static void Log(string tag, string message, LogLevel level)
        {
            if (level < MinLevel) return;

            LogEntry entry = new LogEntry(message, level, tag);
            ProcessLogEntry(entry);
        }

        /// <summary>
        /// Logs a trace message (very detailed debugging info).
        /// </summary>
        public static void Trace(string message)
        {
            Log(message, LogLevel.Trace);
        }

        /// <summary>
        /// Logs a debug message.
        /// </summary>
        public static void Debug(string message)
        {
            Log(message, LogLevel.Debug);
        }

        /// <summary>
        /// Logs an informational message.
        /// </summary>
        public static void Info(string message)
        {
            Log(message, LogLevel.Info);
        }

        /// <summary>
        /// Logs a warning message.
        /// </summary>
        public static void Warning(string message)
        {
            Log(message, LogLevel.Warning);
        }

        /// <summary>
        /// Logs an error message.
        /// </summary>
        public static void Error(string message)
        {
            Log(message, LogLevel.Error);
        }

        /// <summary>
        /// Logs a trace message with a tag.
        /// </summary>
        public static void Trace(string tag, string message)
        {
            Log(tag, message, LogLevel.Trace);
        }

        /// <summary>
        /// Logs a debug message with a tag.
        /// </summary>
        public static void Debug(string tag, string message)
        {
            Log(tag, message, LogLevel.Debug);
        }

        /// <summary>
        /// Logs an info message with a tag.
        /// </summary>
        public static void Info(string tag, string message)
        {
            Log(tag, message, LogLevel.Info);
        }

        /// <summary>
        /// Logs a warning message with a tag.
        /// </summary>
        public static void Warning(string tag, string message)
        {
            Log(tag, message, LogLevel.Warning);
        }

        /// <summary>
        /// Logs an error message with a tag.
        /// </summary>
        public static void Error(string tag, string message)
        {
            Log(tag, message, LogLevel.Error);
        }

        #endregion

        #region Console Registration

        /// <summary>
        /// Registers a debug console to receive log entries.
        /// Called automatically by CEDebugConsole.Start().
        /// </summary>
        internal static void RegisterConsole(CEDebugConsole console)
        {
            if (console == null) return;

            // Check if already registered
            for (int i = 0; i < _consoleCount; i++)
            {
                if (_consoles[i] == console) return;
            }

            // Expand array if needed
            if (_consoleCount >= _consoles.Length)
            {
                CEDebugConsole[] newArray = new CEDebugConsole[_consoles.Length * 2];
                System.Array.Copy(_consoles, newArray, _consoles.Length);
                _consoles = newArray;
            }

            _consoles[_consoleCount++] = console;

            // Send buffered logs to new console
            SendBufferedLogs(console);

            UnityEngine.Debug.Log($"{CEPrefix} Debug console registered");
        }

        /// <summary>
        /// Unregisters a debug console.
        /// Called automatically by CEDebugConsole.OnDestroy().
        /// </summary>
        internal static void UnregisterConsole(CEDebugConsole console)
        {
            if (console == null) return;

            for (int i = 0; i < _consoleCount; i++)
            {
                if (_consoles[i] == console)
                {
                    // Shift remaining consoles down
                    for (int j = i; j < _consoleCount - 1; j++)
                    {
                        _consoles[j] = _consoles[j + 1];
                    }
                    _consoles[--_consoleCount] = null;
                    return;
                }
            }
        }

        #endregion

        #region Private Helpers

        private static void ProcessLogEntry(LogEntry entry)
        {
            // Add to buffer
            AddToBuffer(entry);

            // Send to Unity console
            if (OutputToUnityLog)
            {
                OutputToUnity(entry);
            }

            // Send to registered consoles
            for (int i = 0; i < _consoleCount; i++)
            {
                CEDebugConsole console = _consoles[i];
                if (console != null)
                {
                    console.AddLog(entry);
                }
            }
        }

        private static void AddToBuffer(LogEntry entry)
        {
            int index = (_logBufferHead + _logBufferCount) % _logBuffer.Length;

            if (_logBufferCount < _logBuffer.Length)
            {
                _logBufferCount++;
            }
            else
            {
                // Buffer full, advance head (oldest entry lost)
                _logBufferHead = (_logBufferHead + 1) % _logBuffer.Length;
            }

            _logBuffer[index] = entry;
        }

        private static void SendBufferedLogs(CEDebugConsole console)
        {
            for (int i = 0; i < _logBufferCount; i++)
            {
                int index = (_logBufferHead + i) % _logBuffer.Length;
                LogEntry entry = _logBuffer[index];
                if (entry != null)
                {
                    console.AddLog(entry);
                }
            }
        }

        private static void OutputToUnity(LogEntry entry)
        {
            string formatted = entry.Format(showTimestamp: false);
            string output = string.IsNullOrEmpty(entry.Tag)
                ? $"{CEPrefix} {formatted}"
                : $"{CEPrefix}[{entry.Tag}] {formatted}";

            switch (entry.Level)
            {
                case LogLevel.Trace:
                case LogLevel.Debug:
                case LogLevel.Info:
                    UnityEngine.Debug.Log(output);
                    break;
                case LogLevel.Warning:
                    UnityEngine.Debug.LogWarning(output);
                    break;
                case LogLevel.Error:
                    UnityEngine.Debug.LogError(output);
                    break;
            }
        }

        #endregion

        #region Utility

        /// <summary>
        /// Clears the internal log buffer.
        /// Does not clear registered console displays.
        /// </summary>
        public static void ClearBuffer()
        {
            System.Array.Clear(_logBuffer, 0, _logBuffer.Length);
            _logBufferHead = 0;
            _logBufferCount = 0;
        }

        /// <summary>
        /// Gets the number of logs currently in the buffer.
        /// </summary>
        public static int BufferCount => _logBufferCount;

        #endregion
    }
}
