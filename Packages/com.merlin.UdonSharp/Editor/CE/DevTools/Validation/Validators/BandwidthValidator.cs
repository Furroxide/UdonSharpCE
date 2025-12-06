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

            // Convert world-level violations to validation issues
            foreach (var violation in result.Violations)
            {
                issues.Add(new ValidationIssue
                {
                    Severity = MapSeverity(violation.Severity),
                    ValidatorName = Name,
                    Message = violation.Message,
                    Details = violation.Recommendation
                });
            }

            // Convert behaviour-level violations
            foreach (var behaviour in result.BehaviourResults)
            {
                foreach (var violation in behaviour.Violations)
                {
                    // Only propagate errors (warnings are handled by bandwidth analyzer window)
                    if (violation.Severity == AnalysisSeverity.Error)
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Error,
                            ValidatorName = Name,
                            Message = $"{behaviour.BehaviourType.Name}: {violation.Message}",
                            Details = violation.Recommendation,
                            RelatedType = behaviour.BehaviourType
                        });
                    }
                }
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

