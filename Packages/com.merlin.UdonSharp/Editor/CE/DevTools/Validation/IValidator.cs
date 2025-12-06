using System.Collections.Generic;

namespace UdonSharp.CE.Editor.DevTools.Validation
{
    /// <summary>
    /// Interface for runtime validators that check UdonSharpBehaviour scenes for issues.
    /// Distinct from compile-time analyzers (ICompileTimeAnalyzer) which run during compilation.
    /// </summary>
    public interface IValidator
    {
        /// <summary>
        /// Unique human-readable name for this validator.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Category for grouping validators (e.g., "Performance", "Networking", "Safety").
        /// </summary>
        string Category { get; }

        /// <summary>
        /// Description of what this validator checks for.
        /// </summary>
        string Description { get; }

        /// <summary>
        /// Whether this validator is enabled by default.
        /// </summary>
        bool IsEnabledByDefault { get; }

        /// <summary>
        /// Validates the scene and returns any issues found.
        /// </summary>
        /// <param name="context">Context containing behaviours, types, and methods to validate.</param>
        /// <returns>Collection of validation issues found.</returns>
        IEnumerable<ValidationIssue> Validate(ValidationContext context);
    }
}

