using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools.Validation
{
    /// <summary>
    /// Severity level for validation issues.
    /// </summary>
    public enum ValidationSeverity
    {
        /// <summary>Will cause runtime failure.</summary>
        Error,
        /// <summary>Likely bug or performance issue.</summary>
        Warning,
        /// <summary>Suggestion or style issue.</summary>
        Info,
        /// <summary>Passed check (for reporting purposes).</summary>
        Hidden
    }

    /// <summary>
    /// Context provided to validators containing scene data.
    /// </summary>
    public class ValidationContext
    {
        /// <summary>
        /// All UdonSharpBehaviour instances in the scene.
        /// </summary>
        public List<UdonSharpBehaviour> Behaviours { get; set; } = new List<UdonSharpBehaviour>();

        /// <summary>
        /// Distinct behaviour types found in the scene.
        /// </summary>
        public List<Type> BehaviourTypes { get; set; } = new List<Type>();

        /// <summary>
        /// Methods by type for efficient lookup.
        /// </summary>
        public Dictionary<Type, List<MethodInfo>> Methods { get; set; } = new Dictionary<Type, List<MethodInfo>>();

        /// <summary>
        /// Fields by type for efficient lookup.
        /// </summary>
        public Dictionary<Type, List<FieldInfo>> Fields { get; set; } = new Dictionary<Type, List<FieldInfo>>();

        /// <summary>
        /// Gets all methods for a type, including inherited.
        /// </summary>
        public List<MethodInfo> GetMethods(Type type)
        {
            if (Methods.TryGetValue(type, out var methods))
                return methods;
            return new List<MethodInfo>();
        }

        /// <summary>
        /// Gets all fields for a type, including inherited.
        /// </summary>
        public List<FieldInfo> GetFields(Type type)
        {
            if (Fields.TryGetValue(type, out var fields))
                return fields;
            return new List<FieldInfo>();
        }

        /// <summary>
        /// Tries to get the source file path for a type.
        /// </summary>
        public string GetSourceFile(Type type)
        {
#if UNITY_EDITOR
            // Try to find the MonoScript for this type
            var scripts = UnityEditor.MonoImporter.GetAllRuntimeMonoScripts();
            foreach (var script in scripts)
            {
                if (script.GetClass() == type)
                {
                    return UnityEditor.AssetDatabase.GetAssetPath(script);
                }
            }
#endif
            return null;
        }
    }

    /// <summary>
    /// A single validation issue found by a validator.
    /// </summary>
    public class ValidationIssue
    {
        /// <summary>
        /// Severity of the issue.
        /// </summary>
        public ValidationSeverity Severity { get; set; }

        /// <summary>
        /// Name of the validator that found this issue.
        /// </summary>
        public string ValidatorName { get; set; }

        /// <summary>
        /// Brief description of the issue.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Detailed explanation of the issue.
        /// </summary>
        public string Details { get; set; }

        /// <summary>
        /// Unity object related to this issue (for selection/ping).
        /// </summary>
        public UnityEngine.Object Context { get; set; }

        /// <summary>
        /// Source file path if available.
        /// </summary>
        public string FilePath { get; set; }

        /// <summary>
        /// Line number in source file if available.
        /// </summary>
        public int LineNumber { get; set; }

        /// <summary>
        /// Optional auto-fix function. Returns true if fix was successful.
        /// </summary>
        public Func<bool> AutoFix { get; set; }

        /// <summary>
        /// The type this issue relates to.
        /// </summary>
        public Type RelatedType { get; set; }

        /// <summary>
        /// The field this issue relates to (if any).
        /// </summary>
        public FieldInfo RelatedField { get; set; }

        /// <summary>
        /// The method this issue relates to (if any).
        /// </summary>
        public MethodInfo RelatedMethod { get; set; }

        /// <summary>
        /// Whether this issue can be auto-fixed.
        /// </summary>
        public bool CanAutoFix => AutoFix != null;

        /// <summary>
        /// Creates an error issue.
        /// </summary>
        public static ValidationIssue Error(string validatorName, string message, string details = null)
        {
            return new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                ValidatorName = validatorName,
                Message = message,
                Details = details
            };
        }

        /// <summary>
        /// Creates a warning issue.
        /// </summary>
        public static ValidationIssue Warning(string validatorName, string message, string details = null)
        {
            return new ValidationIssue
            {
                Severity = ValidationSeverity.Warning,
                ValidatorName = validatorName,
                Message = message,
                Details = details
            };
        }

        /// <summary>
        /// Creates an info issue.
        /// </summary>
        public static ValidationIssue Info(string validatorName, string message, string details = null)
        {
            return new ValidationIssue
            {
                Severity = ValidationSeverity.Info,
                ValidatorName = validatorName,
                Message = message,
                Details = details
            };
        }
    }

    /// <summary>
    /// Result of running a single validator.
    /// </summary>
    public class ValidatorResult
    {
        /// <summary>
        /// Name of the validator.
        /// </summary>
        public string ValidatorName { get; set; }

        /// <summary>
        /// Whether the validator passed (no errors).
        /// </summary>
        public bool Passed { get; set; }

        /// <summary>
        /// Number of errors found.
        /// </summary>
        public int ErrorCount { get; set; }

        /// <summary>
        /// Number of warnings found.
        /// </summary>
        public int WarningCount { get; set; }

        /// <summary>
        /// Number of info items found.
        /// </summary>
        public int InfoCount { get; set; }
    }

    /// <summary>
    /// Complete validation report for a scene.
    /// </summary>
    public class ValidationReport
    {
        /// <summary>
        /// All issues found across all validators.
        /// </summary>
        public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();

        /// <summary>
        /// Results by validator.
        /// </summary>
        public Dictionary<string, ValidatorResult> ValidatorResults { get; } = new Dictionary<string, ValidatorResult>();

        /// <summary>
        /// Total number of errors.
        /// </summary>
        public int TotalErrors { get; set; }

        /// <summary>
        /// Total number of warnings.
        /// </summary>
        public int TotalWarnings { get; set; }

        /// <summary>
        /// Total number of info items.
        /// </summary>
        public int TotalInfo { get; set; }

        /// <summary>
        /// Whether all validators passed (no errors).
        /// </summary>
        public bool AllPassed { get; set; }

        /// <summary>
        /// Number of issues that can be auto-fixed.
        /// </summary>
        public int AutoFixableCount => Issues.FindAll(i => i.CanAutoFix).Count;

        /// <summary>
        /// Timestamp when validation was run.
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>
        /// Gets issues filtered by severity.
        /// </summary>
        public List<ValidationIssue> GetIssuesBySeverity(ValidationSeverity severity)
        {
            return Issues.FindAll(i => i.Severity == severity);
        }

        /// <summary>
        /// Gets issues filtered by validator.
        /// </summary>
        public List<ValidationIssue> GetIssuesByValidator(string validatorName)
        {
            return Issues.FindAll(i => i.ValidatorName == validatorName);
        }

        /// <summary>
        /// Gets distinct categories from all validators.
        /// </summary>
        public List<string> GetCategories()
        {
            var categories = new HashSet<string>();
            foreach (var result in ValidatorResults.Values)
            {
                // Categories would need to be stored separately
            }
            return new List<string>(categories);
        }
    }
}

