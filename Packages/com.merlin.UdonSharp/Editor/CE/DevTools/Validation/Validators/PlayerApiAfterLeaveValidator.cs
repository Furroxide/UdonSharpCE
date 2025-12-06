using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates that VRCPlayerApi is not used unsafely in OnPlayerLeft.
    /// The player parameter becomes invalid after the callback returns.
    /// </summary>
    public class PlayerApiAfterLeaveValidator : IValidator
    {
        public string Name => "Player API After Leave";
        public string Category => "Safety";
        public string Description => "Detects potentially invalid VRCPlayerApi usage in OnPlayerLeft.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();

            foreach (var type in context.BehaviourTypes)
            {
                var onPlayerLeft = type.GetMethod("OnPlayerLeft",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (onPlayerLeft == null)
                    continue;

                // Analyze the method for potentially dangerous patterns
                var dangerousPatterns = AnalyzeOnPlayerLeft(type, onPlayerLeft);

                foreach (var pattern in dangerousPatterns)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Warning,
                        ValidatorName = Name,
                        Message = pattern.Issue,
                        Details = pattern.Suggestion,
                        RelatedType = type,
                        RelatedMethod = onPlayerLeft,
                        FilePath = context.GetSourceFile(type),
                        LineNumber = pattern.LineNumber
                    });
                }
            }

            return issues;
        }

        private List<DangerousPattern> AnalyzeOnPlayerLeft(Type type, MethodInfo method)
        {
            var patterns = new List<DangerousPattern>();

            string sourceFile = FindSourceFile(type);
            if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
                return patterns;

            try
            {
                string sourceCode = System.IO.File.ReadAllText(sourceFile);

                // Find OnPlayerLeft method body
                var methodPattern = @"(public\s+override\s+void|override\s+public\s+void|void)\s+OnPlayerLeft\s*\(\s*VRCPlayerApi\s+(\w+)\s*\)";
                var methodMatch = Regex.Match(sourceCode, methodPattern);

                if (!methodMatch.Success)
                    return patterns;

                string playerParamName = methodMatch.Groups[2].Value;
                int methodStart = methodMatch.Index;

                // Find method body
                int bodyStart = sourceCode.IndexOf('{', methodStart);
                if (bodyStart < 0)
                    return patterns;

                int braceCount = 1;
                int bodyEnd = bodyStart + 1;
                while (bodyEnd < sourceCode.Length && braceCount > 0)
                {
                    if (sourceCode[bodyEnd] == '{') braceCount++;
                    else if (sourceCode[bodyEnd] == '}') braceCount--;
                    bodyEnd++;
                }

                string methodBody = sourceCode.Substring(bodyStart, bodyEnd - bodyStart);

                // Check for storing player reference in field
                var fieldStorePattern = new Regex($@"(this\.)?\w+\s*=\s*{playerParamName}\s*;");
                var fieldMatch = fieldStorePattern.Match(methodBody);
                if (fieldMatch.Success)
                {
                    int lineNumber = CountLines(sourceCode, bodyStart + fieldMatch.Index);
                    patterns.Add(new DangerousPattern
                    {
                        Issue = $"Storing '{playerParamName}' in field - reference becomes invalid after OnPlayerLeft",
                        Suggestion = "Copy needed data (playerId, displayName) to local variables instead of storing the VRCPlayerApi reference.",
                        LineNumber = lineNumber
                    });
                }

                // Check for SendCustomEventDelayedSeconds with player reference
                var delayedPattern = new Regex($@"SendCustomEventDelayedSeconds\s*\([^)]*{playerParamName}");
                var delayedMatch = delayedPattern.Match(methodBody);
                if (delayedMatch.Success)
                {
                    int lineNumber = CountLines(sourceCode, bodyStart + delayedMatch.Index);
                    patterns.Add(new DangerousPattern
                    {
                        Issue = $"Using '{playerParamName}' with SendCustomEventDelayedSeconds - player may be invalid when event fires",
                        Suggestion = "Store player.playerId instead and look up the player when the delayed event fires.",
                        LineNumber = lineNumber
                    });
                }

                // Check for accessing player after SendCustomEvent (async pattern)
                var asyncAccessPattern = new Regex($@"SendCustomEvent[^;]*;[^}}]*{playerParamName}\.");
                if (asyncAccessPattern.IsMatch(methodBody))
                {
                    patterns.Add(new DangerousPattern
                    {
                        Issue = $"Accessing '{playerParamName}' after async operation - player may be invalid",
                        Suggestion = "Complete all player API access before triggering async events.",
                        LineNumber = CountLines(sourceCode, methodStart)
                    });
                }

                // Check for player.isLocal comparison that might be used to save data
                var savePattern = new Regex($@"if\s*\(\s*{playerParamName}\.isLocal\s*\)[^}}]*Save");
                var saveMatch = savePattern.Match(methodBody);
                if (saveMatch.Success)
                {
                    int lineNumber = CountLines(sourceCode, bodyStart + saveMatch.Index);
                    patterns.Add(new DangerousPattern
                    {
                        Issue = "Attempting to save data in OnPlayerLeft - data may not persist",
                        Suggestion = "Save player data periodically or on game events, not in OnPlayerLeft. VRChat does not guarantee persistence callbacks will complete.",
                        LineNumber = lineNumber
                    });
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            return patterns;
        }

        private int CountLines(string text, int position)
        {
            int lines = 1;
            for (int i = 0; i < position && i < text.Length; i++)
            {
                if (text[i] == '\n')
                    lines++;
            }
            return lines;
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

        private class DangerousPattern
        {
            public string Issue { get; set; }
            public string Suggestion { get; set; }
            public int LineNumber { get; set; }
        }
    }
}

