using Microsoft.CodeAnalysis;
using UdonSharp.Compiler;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Represents a diagnostic message produced by a compile-time analyzer.
    ///
    /// Contains all information needed to report the diagnostic through the
    /// UdonSharp compiler's diagnostic system.
    /// </summary>
    internal class AnalyzerDiagnostic
    {
        /// <summary>
        /// The severity of the diagnostic (Error, Warning, or Log).
        /// </summary>
        public DiagnosticSeverity Severity { get; }

        /// <summary>
        /// The Roslyn Location where the issue was found.
        /// Used to display file path, line, and column information.
        /// </summary>
        public Location Location { get; }

        /// <summary>
        /// A human-readable message describing the issue.
        /// </summary>
        public string Message { get; }

        /// <summary>
        /// The ID of the analyzer that produced this diagnostic.
        /// </summary>
        public string AnalyzerId { get; }

        /// <summary>
        /// The diagnostic code (e.g., "CE0001").
        /// </summary>
        public string Code { get; }

        /// <summary>
        /// Optional suggestion for how to fix the issue.
        /// </summary>
        public string FixSuggestion { get; }

        /// <summary>
        /// Creates a new analyzer diagnostic.
        /// </summary>
        /// <param name="severity">The severity level.</param>
        /// <param name="location">The source location.</param>
        /// <param name="message">The diagnostic message.</param>
        /// <param name="analyzerId">The analyzer ID.</param>
        /// <param name="code">The diagnostic code.</param>
        /// <param name="fixSuggestion">Optional fix suggestion.</param>
        public AnalyzerDiagnostic(
            DiagnosticSeverity severity,
            Location location,
            string message,
            string analyzerId,
            string code,
            string fixSuggestion = null)
        {
            Severity = severity;
            Location = location;
            Message = message;
            AnalyzerId = analyzerId;
            Code = code;
            FixSuggestion = fixSuggestion;
        }

        /// <summary>
        /// Creates an error diagnostic.
        /// </summary>
        public static AnalyzerDiagnostic Error(
            Location location,
            string message,
            string analyzerId,
            string code,
            string fixSuggestion = null)
        {
            return new AnalyzerDiagnostic(
                DiagnosticSeverity.Error,
                location,
                message,
                analyzerId,
                code,
                fixSuggestion);
        }

        /// <summary>
        /// Creates a warning diagnostic.
        /// </summary>
        public static AnalyzerDiagnostic Warning(
            Location location,
            string message,
            string analyzerId,
            string code,
            string fixSuggestion = null)
        {
            return new AnalyzerDiagnostic(
                DiagnosticSeverity.Warning,
                location,
                message,
                analyzerId,
                code,
                fixSuggestion);
        }

        /// <summary>
        /// Creates an info/log diagnostic.
        /// </summary>
        public static AnalyzerDiagnostic Info(
            Location location,
            string message,
            string analyzerId,
            string code)
        {
            return new AnalyzerDiagnostic(
                DiagnosticSeverity.Log,
                location,
                message,
                analyzerId,
                code);
        }

        /// <summary>
        /// Formats the diagnostic message with code prefix.
        /// </summary>
        public string GetFormattedMessage()
        {
            string result = $"[{Code}] {Message}";
            if (!string.IsNullOrEmpty(FixSuggestion))
            {
                result += $" Suggestion: {FixSuggestion}";
            }
            return result;
        }

        public override string ToString()
        {
            return GetFormattedMessage();
        }
    }
}
