using System.Collections.Generic;
using UdonSharp.CE.Editor.DevTools.Analysis;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates that network bandwidth usage is within VRChat limits.
    /// Delegates to WorldAnalyzer from the Bandwidth Analyzer tool.
    /// </summary>
    public class BandwidthValidator : IValidator
    {
        public string Name => "Bandwidth Limits";
        public string Category => "Networking";
        public string Description => "Checks network bandwidth usage against VRChat limits.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();

            // Use the WorldAnalyzer to get bandwidth analysis
            var analyzer = new WorldAnalyzer();
            var result = analyzer.AnalyzeBehaviours(context.Behaviours);

            // Convert world-level violations to validation issues.
            //
            // This is the only loop, and it has to stay the only one. WorldAnalyzer already copies every
            // behaviour-level error into the world list, prefixed with the behaviour's name; a second pass
            // over the per-behaviour lists re-added each of those under the same text, so one oversized
            // payload showed up as two errors and the validator's error count was double the truth.
            // Behaviour-level warnings are left to the Bandwidth Analyzer window, as they always were.
            foreach (var violation in result.Violations)
            {
                issues.Add(new ValidationIssue
                {
                    Severity = MapSeverity(violation.Severity),
                    ValidatorName = Name,
                    Message = violation.Message,
                    Details = violation.Recommendation,
                    RelatedType = violation.BehaviourType
                });
            }

            return issues;
        }

        private ValidationSeverity MapSeverity(AnalysisSeverity severity)
        {
            return severity switch
            {
                AnalysisSeverity.Error => ValidationSeverity.Error,
                AnalysisSeverity.Warning => ValidationSeverity.Warning,
                _ => ValidationSeverity.Info
            };
        }
    }
}

