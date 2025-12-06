using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UdonSharp.CE.Net;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates that SendCustomNetworkEvent is not used to call [LocalOnly] methods.
    /// Methods marked [LocalOnly] should not be called over the network.
    /// </summary>
    public class LocalOnlyNetworkCallValidator : IValidator
    {
        public string Name => "LocalOnly Network Call";
        public string Category => "Networking";
        public string Description => "Detects SendCustomNetworkEvent targeting [LocalOnly] methods.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();

            foreach (var type in context.BehaviourTypes)
            {
                // Get all [LocalOnly] methods in this type
                var localOnlyMethods = GetLocalOnlyMethods(type);
                if (localOnlyMethods.Count == 0)
                    continue;

                // Find all SendCustomNetworkEvent calls
                var networkCalls = FindNetworkEventCalls(type);

                foreach (var call in networkCalls)
                {
                    if (localOnlyMethods.Contains(call.EventName))
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Error,
                            ValidatorName = Name,
                            Message = $"SendCustomNetworkEvent targets [LocalOnly] method '{call.EventName}'",
                            Details = "Methods marked [LocalOnly] are not meant to be called over the network. " +
                                     "Either remove [LocalOnly] from the method, or use SendCustomEvent for local calls.",
                            RelatedType = type,
                            FilePath = context.GetSourceFile(type),
                            LineNumber = call.LineNumber
                        });
                    }
                }
            }

            return issues;
        }

        private HashSet<string> GetLocalOnlyMethods(Type type)
        {
            var methods = new HashSet<string>();

            var allMethods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var method in allMethods)
            {
                // Check for [LocalOnly] attribute
                var attrs = method.GetCustomAttributes(true);
                foreach (var attr in attrs)
                {
                    string attrName = attr.GetType().Name;
                    if (attrName == "LocalOnlyAttribute" || attrName == "LocalOnly")
                    {
                        methods.Add(method.Name);
                        break;
                    }
                }
            }

            return methods;
        }

        private List<NetworkEventCall> FindNetworkEventCalls(Type type)
        {
            var calls = new List<NetworkEventCall>();

            string sourceFile = FindSourceFile(type);
            if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
                return calls;

            try
            {
                string sourceCode = System.IO.File.ReadAllText(sourceFile);

                // Find SendCustomNetworkEvent calls
                // Patterns:
                //   SendCustomNetworkEvent(NetworkEventTarget.All, "MethodName")
                //   SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(MethodName))

                var patterns = new[]
                {
                    // String literal
                    @"SendCustomNetworkEvent\s*\(\s*NetworkEventTarget\.\w+\s*,\s*""(\w+)""",
                    // nameof
                    @"SendCustomNetworkEvent\s*\(\s*NetworkEventTarget\.\w+\s*,\s*nameof\s*\(\s*(\w+)\s*\)"
                };

                foreach (var pattern in patterns)
                {
                    var matches = Regex.Matches(sourceCode, pattern);
                    foreach (Match match in matches)
                    {
                        int lineNumber = sourceCode.Substring(0, match.Index).Count(c => c == '\n') + 1;
                        calls.Add(new NetworkEventCall
                        {
                            EventName = match.Groups[1].Value,
                            LineNumber = lineNumber
                        });
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            return calls;
        }

        private string FindSourceFile(Type type)
        {
#if UNITY_EDITOR
            var scripts = UnityEditor.MonoImporter.GetAllRuntimeMonoScripts();
            foreach (var script in scripts)
            {
                if (script.GetClass() == type)
                {
                    string assetPath = UnityEditor.AssetDatabase.GetAssetPath(script);
                    return System.IO.Path.GetFullPath(assetPath);
                }
            }
#endif
            return null;
        }

        private class NetworkEventCall
        {
            public string EventName { get; set; }
            public int LineNumber { get; set; }
        }
    }
}

