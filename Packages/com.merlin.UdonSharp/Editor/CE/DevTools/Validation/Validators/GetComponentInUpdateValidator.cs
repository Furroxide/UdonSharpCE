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
                string sourceFile = FindSourceFile(type);
                if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
                    continue;

                string sourceCode;

                try
                {
                    sourceCode = System.IO.File.ReadAllText(sourceFile);
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[CE.Validator] Failed to read {sourceFile}: {ex.Message}");
                    continue;
                }

                issues.AddRange(ValidateSource(type, sourceCode, context.GetSourceFile(type)));
            }

            return issues;
        }

        /// <summary>
        /// Checks one behaviour type against its source text.
        ///
        /// Split out from <see cref="Validate"/> so the text analysis can be driven without a script asset
        /// on disk: the mistakes this validator has made were all in how it reads source, and that is the
        /// part worth being able to put a fixture in front of.
        /// </summary>
        public List<ValidationIssue> ValidateSource(Type type, string sourceCode, string filePath = null)
        {
            var issues = new List<ValidationIssue>();

            if (type == null || string.IsNullOrEmpty(sourceCode))
                return issues;

            foreach (var methodName in UpdateMethods)
            {
                MethodInfo method = FindParameterlessMethod(type, methodName);

                if (method == null)
                    continue;

                // Check if method body contains GetComponent calls
                var getComponentCalls = FindGetComponentCalls(type, method, sourceCode);

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
                        FilePath = filePath,
                        LineNumber = call.LineNumber
                    });
                }
            }

            return issues;
        }

        /// <summary>
        /// The parameterless method of that name, which is the one Unity calls.
        ///
        /// Not <c>Type.GetMethod(name, flags)</c>: that throws AmbiguousMatchException as soon as the type
        /// also declares an overload -- 'void Update()' next to 'void Update(float deltaTime)' -- and the
        /// throw escaped this validator, so one such behaviour in the scene replaced every result it had
        /// with a single "Validator crashed".
        /// </summary>
        private static MethodInfo FindParameterlessMethod(Type type, string methodName)
        {
            foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (candidate.Name == methodName && candidate.GetParameters().Length == 0)
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Finds where a method's body starts and how long it is, given the position just past its
        /// parameter list. False when what follows is not a body this can read.
        ///
        /// An expression-bodied method has to be recognised as one. The scan used to look for the next
        /// '{' wherever it was, so for 'void Update() =&gt; Tick();' it walked on into the following method
        /// and reported that method's GetComponent call -- typically the cached lookup in Start, which is
        /// the fix this validator recommends -- as a call made in Update.
        /// </summary>
        internal static bool TryFindMethodBody(string sourceCode, int afterSignature, out int bodyStart, out int bodyLength)
        {
            bodyStart = -1;
            bodyLength = 0;

            int position = SkipWhitespaceAndComments(sourceCode, afterSignature);

            if (position >= sourceCode.Length)
                return false;

            if (sourceCode[position] == '=' && position + 1 < sourceCode.Length && sourceCode[position + 1] == '>')
            {
                int end = sourceCode.IndexOf(';', position);
                if (end < 0)
                    return false;

                bodyStart = position;
                bodyLength = end - position + 1;
                return true;
            }

            if (sourceCode[position] != '{')
                return false;

            int braceCount = 0;

            for (int i = position; i < sourceCode.Length; i++)
            {
                if (sourceCode[i] == '{')
                {
                    braceCount++;
                }
                else if (sourceCode[i] == '}')
                {
                    braceCount--;

                    if (braceCount == 0)
                    {
                        bodyStart = position;
                        bodyLength = i - position + 1;
                        return true;
                    }
                }
            }

            return false;
        }

        private static int SkipWhitespaceAndComments(string sourceCode, int position)
        {
            while (position < sourceCode.Length)
            {
                if (char.IsWhiteSpace(sourceCode[position]))
                {
                    position++;
                    continue;
                }

                if (string.CompareOrdinal(sourceCode, position, "//", 0, 2) == 0)
                {
                    int lineEnd = sourceCode.IndexOf('\n', position);
                    position = lineEnd < 0 ? sourceCode.Length : lineEnd + 1;
                    continue;
                }

                if (string.CompareOrdinal(sourceCode, position, "/*", 0, 2) == 0)
                {
                    int commentEnd = sourceCode.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    position = commentEnd < 0 ? sourceCode.Length : commentEnd + 2;
                    continue;
                }

                break;
            }

            return position;
        }

        private List<GetComponentCallInfo> FindGetComponentCalls(Type type, MethodInfo method, string sourceCode)
        {
            var calls = new List<GetComponentCallInfo>();

            try
            {
                // Find the method body
                string methodPattern = $@"(void|public|private|protected)\s+{method.Name}\s*\(\s*\)";
                var methodMatch = Regex.Match(sourceCode, methodPattern);
                if (!methodMatch.Success)
                    return calls;

                if (!TryFindMethodBody(sourceCode, methodMatch.Index + methodMatch.Length, out int bodyOffset, out int bodyLength))
                    return calls;

                string methodBody = sourceCode.Substring(bodyOffset, bodyLength);

                // Find GetComponent calls
                var getComponentPattern = new Regex(@"GetComponent\s*<\s*(\w+)\s*>\s*\(");
                var matches = getComponentPattern.Matches(methodBody);

                foreach (Match match in matches)
                {
                    // Calculate line number
                    int absolutePosition = bodyOffset + match.Index;
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
                    int absolutePosition = bodyOffset + match.Index;
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

