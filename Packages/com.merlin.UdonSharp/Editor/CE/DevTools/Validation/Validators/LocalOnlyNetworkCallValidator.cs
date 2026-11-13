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
                if (GetLocalOnlyMethods(type).Count == 0)
                    continue;

                string sourceFile = FindSourceFile(type);
                if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
                    continue;

                string sourceCode;

                try
                {
                    sourceCode = System.IO.File.ReadAllText(sourceFile);
                }
                catch
                {
                    // An unreadable file is the same as no file: nothing to report on.
                    continue;
                }

                issues.AddRange(ValidateSource(type, sourceCode, context.GetSourceFile(type)));
            }

            return issues;
        }

        /// <summary>
        /// Checks one behaviour type against its source text. Separate from <see cref="Validate"/> so the
        /// call matching can be exercised without a script asset on disk.
        /// </summary>
        public List<ValidationIssue> ValidateSource(Type type, string sourceCode, string filePath = null)
        {
            var issues = new List<ValidationIssue>();

            if (type == null || string.IsNullOrEmpty(sourceCode))
                return issues;

            var localOnlyMethods = GetLocalOnlyMethods(type);
            if (localOnlyMethods.Count == 0)
                return issues;

            // Find all SendCustomNetworkEvent calls this behaviour makes on itself
            foreach (var call in FindNetworkEventCalls(sourceCode))
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
                        FilePath = filePath,
                        LineNumber = call.LineNumber
                    });
                }
            }

            return issues;
        }

        /// <summary>
        /// Whether the call at this position is made on the behaviour itself: written bare, or through
        /// 'this.' or 'base.'.
        ///
        /// 'other.SendCustomNetworkEvent(NetworkEventTarget.All, "Refresh")' runs Refresh on the other
        /// behaviour. Whether this type happens to have a [LocalOnly] method of the same name has nothing
        /// to do with it, and reading every call in the file as a call on this type reported exactly that
        /// coincidence as an error.
        /// </summary>
        private static bool IsCallOnSelf(string sourceCode, int callIndex)
        {
            int position = callIndex - 1;

            // The match sits inside a longer name -- 'MySendCustomNetworkEvent(...)' is some other method.
            if (position >= 0 && IsIdentifierCharacter(sourceCode[position]))
                return false;

            while (position >= 0 && char.IsWhiteSpace(sourceCode[position]))
                position--;

            if (position < 0 || sourceCode[position] != '.')
                return true;

            position--;

            while (position >= 0 && char.IsWhiteSpace(sourceCode[position]))
                position--;

            int receiverEnd = position;

            while (position >= 0 && IsIdentifierCharacter(sourceCode[position]))
                position--;

            // Not an identifier at all -- 'GetTarget().SendCustomNetworkEvent(...)', 'targets[i].Send...'.
            if (receiverEnd < 0 || position == receiverEnd)
                return false;

            // The whole identifier is read, so 'database.SendCustomNetworkEvent' is not taken for 'base.'.
            string receiver = sourceCode.Substring(position + 1, receiverEnd - position);

            return receiver == "this" || receiver == "base";
        }

        private static bool IsIdentifierCharacter(char character)
        {
            return char.IsLetterOrDigit(character) || character == '_';
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

        private List<NetworkEventCall> FindNetworkEventCalls(string sourceCode)
        {
            var calls = new List<NetworkEventCall>();

            try
            {
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
                        if (!IsCallOnSelf(sourceCode, match.Index))
                            continue;

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

