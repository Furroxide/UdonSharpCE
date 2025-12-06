using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UdonSharp.CE.Editor.DevTools.Validation.Validators;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UdonSharp.CE.Editor.DevTools.Validation
{
    /// <summary>
    /// Orchestrates running all validators on a scene and aggregates results.
    /// Discovers validators dynamically and provides thread-safe execution.
    /// </summary>
    public class ValidationRunner
    {
        #region Fields

        private readonly List<IValidator> _validators;
        private bool _enableDebugLogging = false;

        #endregion

        #region Constructor

        /// <summary>
        /// Creates a new ValidationRunner with all available validators.
        /// </summary>
        public ValidationRunner()
        {
            _validators = DiscoverValidators();
        }

        #endregion

        #region Public Methods

        /// <summary>
        /// Runs all enabled validators on the current scene.
        /// </summary>
        /// <returns>Complete validation report.</returns>
        public ValidationReport RunAll()
        {
            var context = BuildContext();
            return RunAll(context);
        }

        /// <summary>
        /// Runs all enabled validators with a provided context.
        /// </summary>
        /// <param name="context">Pre-built validation context.</param>
        /// <returns>Complete validation report.</returns>
        public ValidationReport RunAll(ValidationContext context)
        {
            var report = new ValidationReport();

            foreach (var validator in _validators)
            {
                if (!validator.IsEnabledByDefault)
                    continue;

                RunSingleValidator(validator, context, report);
            }

            // Calculate totals
            report.TotalErrors = report.Issues.Count(i => i.Severity == ValidationSeverity.Error);
            report.TotalWarnings = report.Issues.Count(i => i.Severity == ValidationSeverity.Warning);
            report.TotalInfo = report.Issues.Count(i => i.Severity == ValidationSeverity.Info);
            report.AllPassed = report.TotalErrors == 0;

            return report;
        }

        /// <summary>
        /// Runs a specific validator by name.
        /// </summary>
        /// <param name="validatorName">Name of the validator to run.</param>
        /// <returns>Validation report with results from the single validator.</returns>
        public ValidationReport RunValidator(string validatorName)
        {
            var validator = _validators.FirstOrDefault(v => v.Name == validatorName);
            if (validator == null)
            {
                var report = new ValidationReport();
                report.Issues.Add(ValidationIssue.Error("ValidationRunner", $"Validator '{validatorName}' not found"));
                return report;
            }

            var context = BuildContext();
            var result = new ValidationReport();
            RunSingleValidator(validator, context, result);

            result.TotalErrors = result.Issues.Count(i => i.Severity == ValidationSeverity.Error);
            result.TotalWarnings = result.Issues.Count(i => i.Severity == ValidationSeverity.Warning);
            result.TotalInfo = result.Issues.Count(i => i.Severity == ValidationSeverity.Info);
            result.AllPassed = result.TotalErrors == 0;

            return result;
        }

        /// <summary>
        /// Gets information about all registered validators.
        /// </summary>
        /// <returns>List of validator info tuples.</returns>
        public List<(string Name, string Category, string Description, bool Enabled)> GetValidatorInfo()
        {
            return _validators.Select(v => (v.Name, v.Category, v.Description, v.IsEnabledByDefault)).ToList();
        }

        /// <summary>
        /// Gets distinct categories from all validators.
        /// </summary>
        /// <returns>List of category names.</returns>
        public List<string> GetCategories()
        {
            return _validators.Select(v => v.Category).Distinct().OrderBy(c => c).ToList();
        }

        /// <summary>
        /// Enables or disables debug logging.
        /// </summary>
        public void SetDebugLogging(bool enabled)
        {
            _enableDebugLogging = enabled;
        }

        #endregion

        #region Private Methods

        private List<IValidator> DiscoverValidators()
        {
            var validators = new List<IValidator>();

            // Register known validators explicitly for reliability
            validators.Add(new GetComponentInUpdateValidator());
            validators.Add(new UninitializedSyncedArrayValidator());
            validators.Add(new PlayerApiAfterLeaveValidator());
            validators.Add(new LocalOnlyNetworkCallValidator());
            validators.Add(new SyncModeValidator());
            validators.Add(new BandwidthValidator());
            validators.Add(new PersistenceSizeValidator());

            // Also try dynamic discovery for extensibility
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var validatorTypes = assembly.GetTypes()
                    .Where(t => typeof(IValidator).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                foreach (var type in validatorTypes)
                {
                    // Skip if already registered
                    if (validators.Any(v => v.GetType() == type))
                        continue;

                    try
                    {
                        var instance = (IValidator)Activator.CreateInstance(type);
                        validators.Add(instance);
                    }
                    catch (Exception ex)
                    {
                        Debug.LogWarning($"[CE.Validator] Failed to instantiate validator {type.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CE.Validator] Dynamic validator discovery failed: {ex.Message}");
            }

            return validators;
        }

        private void RunSingleValidator(IValidator validator, ValidationContext context, ValidationReport report)
        {
            if (_enableDebugLogging)
            {
                Debug.Log($"[CE.Validator] Running: {validator.Name}");
            }

            var result = new ValidatorResult
            {
                ValidatorName = validator.Name
            };

            try
            {
                var issues = validator.Validate(context);

                if (issues != null)
                {
                    foreach (var issue in issues)
                    {
                        if (issue == null)
                            continue;

                        report.Issues.Add(issue);

                        switch (issue.Severity)
                        {
                            case ValidationSeverity.Error:
                                result.ErrorCount++;
                                break;
                            case ValidationSeverity.Warning:
                                result.WarningCount++;
                                break;
                            case ValidationSeverity.Info:
                                result.InfoCount++;
                                break;
                        }
                    }
                }

                result.Passed = result.ErrorCount == 0;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CE.Validator] {validator.Name} failed: {ex.Message}");

                report.Issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Error,
                    ValidatorName = validator.Name,
                    Message = $"Validator crashed: {ex.Message}",
                    Details = ex.StackTrace
                });

                result.Passed = false;
                result.ErrorCount++;
            }

            report.ValidatorResults[validator.Name] = result;

            if (_enableDebugLogging)
            {
                Debug.Log($"[CE.Validator] {validator.Name}: {result.ErrorCount} errors, {result.WarningCount} warnings");
            }
        }

        private ValidationContext BuildContext()
        {
            var context = new ValidationContext();

            // Find all UdonSharpBehaviours in the scene
            var behaviours = FindAllUdonSharpBehaviours();
            context.Behaviours = behaviours;

            // Get distinct types
            context.BehaviourTypes = behaviours.Select(b => b.GetType()).Distinct().ToList();

            // Build method and field lookups
            foreach (var type in context.BehaviourTypes)
            {
                var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                context.Methods[type] = methods.ToList();

                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                context.Fields[type] = fields.ToList();
            }

            return context;
        }

        private List<UdonSharpBehaviour> FindAllUdonSharpBehaviours()
        {
            var behaviours = new List<UdonSharpBehaviour>();

            // Search all loaded scenes
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded)
                    continue;

                var rootObjects = scene.GetRootGameObjects();
                foreach (var root in rootObjects)
                {
                    var found = root.GetComponentsInChildren<UdonSharpBehaviour>(true);
                    behaviours.AddRange(found);
                }
            }

            // Also use FindObjectsOfType for completeness
#if UNITY_EDITOR
            var allBehaviours = UnityEngine.Object.FindObjectsOfType<UdonSharpBehaviour>(true);
            foreach (var b in allBehaviours)
            {
                if (!behaviours.Contains(b))
                    behaviours.Add(b);
            }
#endif

            return behaviours;
        }

        #endregion
    }
}

