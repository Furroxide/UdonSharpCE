using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEngine;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates that GetComponent is not called in Update/FixedUpdate/LateUpdate methods.
    /// GetComponent is expensive and should be cached in Start() or Awake().
    /// </summary>
    public class GetComponentInUpdateValidator : IValidator
    {
        public string Name => "GetComponent in Update";
        public string Category => "Performance";
        public string Description => "Detects GetComponent calls in Update methods that should be cached.";
        public bool IsEnabledByDefault => true;

        private static readonly string[] UpdateMethods = { "Update", "FixedUpdate", "LateUpdate" };

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();

            foreach (var type in context.BehaviourTypes)
            {
                foreach (var methodName in UpdateMethods)
                {
                    var method = type.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (method == null)
                        continue;

                    // Check if method body contains GetComponent calls
                    var getComponentCalls = FindGetComponentCalls(type, method);

                    foreach (var call in getComponentCalls)
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Warning,
                            ValidatorName = Name,
                            Message = $"GetComponent<{call.GenericTypeName}>() called in {methodName}",
                            Details = "GetComponent is expensive. Cache the result in Start() or Awake() instead of calling it every frame.",
                            RelatedType = type,
                            RelatedMethod = method,
                            FilePath = context.GetSourceFile(type),
                            LineNumber = call.LineNumber
                        });
                    }
                }
            }

            return issues;
        }

        private List<GetComponentCallInfo> FindGetComponentCalls(Type type, MethodInfo method)
        {
            var calls = new List<GetComponentCallInfo>();

            try
            {
                // Try to find the source file and parse it
                string sourceFile = FindSourceFile(type);
                if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
                    return calls;

                string sourceCode = System.IO.File.ReadAllText(sourceFile);

                // Find the method body
                string methodPattern = $@"(void|public|private|protected)\s+{method.Name}\s*\(\s*\)";
                var methodMatch = Regex.Match(sourceCode, methodPattern);
                if (!methodMatch.Success)
                    return calls;

                // Find GetComponent calls within a reasonable range after the method signature
                int methodStart = methodMatch.Index;
                int searchEnd = Math.Min(methodStart + 5000, sourceCode.Length);
                string methodSection = sourceCode.Substring(methodStart, searchEnd - methodStart);

                // Find brace-delimited method body
                int braceCount = 0;
                int bodyStart = -1;
                int bodyEnd = -1;

                for (int i = 0; i < methodSection.Length; i++)
                {
                    if (methodSection[i] == '{')
                    {
                        if (bodyStart < 0)
                            bodyStart = i;
                        braceCount++;
                    }
                    else if (methodSection[i] == '}')
                    {
                        braceCount--;
                        if (braceCount == 0 && bodyStart >= 0)
                        {
                            bodyEnd = i;
                            break;
                        }
                    }
                }

                if (bodyStart < 0 || bodyEnd < 0)
                    return calls;

                string methodBody = methodSection.Substring(bodyStart, bodyEnd - bodyStart + 1);

                // Find GetComponent calls
                var getComponentPattern = new Regex(@"GetComponent\s*<\s*(\w+)\s*>\s*\(");
                var matches = getComponentPattern.Matches(methodBody);

                foreach (Match match in matches)
                {
                    // Calculate line number
                    int absolutePosition = methodStart + bodyStart + match.Index;
                    int lineNumber = sourceCode.Substring(0, absolutePosition).Count(c => c == '\n') + 1;

                    calls.Add(new GetComponentCallInfo
                    {
                        GenericTypeName = match.Groups[1].Value,
                        LineNumber = lineNumber
                    });
                }

                // Also check non-generic GetComponent
                var nonGenericPattern = new Regex(@"GetComponent\s*\(\s*typeof\s*\(\s*(\w+)\s*\)");
                matches = nonGenericPattern.Matches(methodBody);

                foreach (Match match in matches)
                {
                    int absolutePosition = methodStart + bodyStart + match.Index;
                    int lineNumber = sourceCode.Substring(0, absolutePosition).Count(c => c == '\n') + 1;

                    calls.Add(new GetComponentCallInfo
                    {
                        GenericTypeName = match.Groups[1].Value,
                        LineNumber = lineNumber
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[CE.Validator] Failed to analyze {type.Name}.{method.Name}: {ex.Message}");
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

        private class GetComponentCallInfo
        {
            public string GenericTypeName { get; set; }
            public int LineNumber { get; set; }
        }
    }
}

