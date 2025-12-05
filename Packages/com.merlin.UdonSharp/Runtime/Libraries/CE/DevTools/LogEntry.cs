using UnityEngine;

namespace UdonSharp.CE.DevTools
{
    /// <summary>
    /// Log severity levels for CE.DevTools logging.
    /// </summary>
    public enum LogLevel
    {
        /// <summary>Detailed tracing information for debugging.</summary>
        Trace = 0,
        /// <summary>Debug information useful during development.</summary>
        Debug = 1,
        /// <summary>General informational messages.</summary>
        Info = 2,
        /// <summary>Warning messages for potentially problematic situations.</summary>
        Warning = 3,
        /// <summary>Error messages for failures and exceptions.</summary>
        Error = 4
    }

    /// <summary>
    /// Represents a single log entry with timestamp and metadata.
    /// </summary>
    public class LogEntry
    {
        /// <summary>The log message text.</summary>
        public string Message;

        /// <summary>The severity level of this log entry.</summary>
        public LogLevel Level;

        /// <summary>Optional tag/category for filtering.</summary>
        public string Tag;

        /// <summary>Time.time when this log was created.</summary>
        public float Timestamp;

        /// <summary>Frame number when this log was created.</summary>
        public int FrameCount;

        /// <summary>
        /// Creates a new log entry with the current timestamp.
        /// </summary>
        public LogEntry()
        {
            Timestamp = Time.time;
            FrameCount = Time.frameCount;
        }

        /// <summary>
        /// Creates a new log entry with the specified message and level.
        /// </summary>
        public LogEntry(string message, LogLevel level)
        {
            Message = message;
            Level = level;
            Tag = null;
            Timestamp = Time.time;
            FrameCount = Time.frameCount;
        }

        /// <summary>
        /// Creates a new log entry with the specified message, level, and tag.
        /// </summary>
        public LogEntry(string message, LogLevel level, string tag)
        {
            Message = message;
            Level = level;
            Tag = tag;
            Timestamp = Time.time;
            FrameCount = Time.frameCount;
        }

        /// <summary>
        /// Gets the display color for this log level.
        /// </summary>
        public Color GetLevelColor()
        {
            switch (Level)
            {
                case LogLevel.Trace: return new Color(0.5f, 0.5f, 0.5f); // Gray
                case LogLevel.Debug: return new Color(0.6f, 0.6f, 0.8f); // Light blue-gray
                case LogLevel.Info: return Color.white;
                case LogLevel.Warning: return new Color(1f, 0.8f, 0f); // Yellow-orange
                case LogLevel.Error: return new Color(1f, 0.3f, 0.3f); // Red
                default: return Color.white;
            }
        }

        /// <summary>
        /// Gets the short prefix string for this log level.
        /// </summary>
        public string GetLevelPrefix()
        {
            switch (Level)
            {
                case LogLevel.Trace: return "[TRC]";
                case LogLevel.Debug: return "[DBG]";
                case LogLevel.Info: return "[INF]";
                case LogLevel.Warning: return "[WRN]";
                case LogLevel.Error: return "[ERR]";
                default: return "[???]";
            }
        }

        /// <summary>
        /// Formats the log entry as a string with timestamp and level.
        /// </summary>
        public string Format(bool showTimestamp = true, bool showFrame = false)
        {
            string prefix = GetLevelPrefix();
            string timeStr = showTimestamp ? $"[{Timestamp:F2}]" : "";
            string frameStr = showFrame ? $"[F{FrameCount}]" : "";
            string tagStr = string.IsNullOrEmpty(Tag) ? "" : $"[{Tag}]";

            return $"{timeStr}{frameStr}{prefix}{tagStr} {Message}";
        }

        /// <summary>
        /// Formats the log entry with rich text color tags.
        /// </summary>
        public string FormatRichText(bool showTimestamp = true, bool showFrame = false)
        {
            Color color = GetLevelColor();
            string colorHex = ColorUtility.ToHtmlStringRGB(color);
            string formatted = Format(showTimestamp, showFrame);
            return $"<color=#{colorHex}>{formatted}</color>";
        }
    }
}
