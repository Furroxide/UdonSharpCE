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
        /// pattern matching can be exercised without a script asset on disk.
        /// </summary>
        public List<ValidationIssue> ValidateSource(Type type, string sourceCode, string filePath = null)
        {
            var issues = new List<ValidationIssue>();

            if (type == null || string.IsNullOrEmpty(sourceCode))
                return issues;

            MethodInfo onPlayerLeft = FindOnPlayerLeft(type);

            if (onPlayerLeft == null)
                return issues;

            // Analyze the method for potentially dangerous patterns
            foreach (var pattern in AnalyzeOnPlayerLeft(type, sourceCode))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = ValidationSeverity.Warning,
                    ValidatorName = Name,
                    Message = pattern.Issue,
                    Details = pattern.Suggestion,
                    RelatedType = type,
                    RelatedMethod = onPlayerLeft,
                    FilePath = filePath,
                    LineNumber = pattern.LineNumber
                });
            }

            return issues;
        }

        /// <summary>
        /// Looked up by scanning rather than with <c>Type.GetMethod(name, flags)</c>, which throws
        /// AmbiguousMatchException the moment a second method shares the name.
        /// </summary>
        private static MethodInfo FindOnPlayerLeft(Type type)
        {
            foreach (MethodInfo candidate in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (candidate.Name != "OnPlayerLeft")
                    continue;

                ParameterInfo[] parameters = candidate.GetParameters();

                if (parameters.Length == 1 && parameters[0].ParameterType.Name == "VRCPlayerApi")
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// Whether the type, or a class it derives from, declares an instance field of that name.
        /// </summary>
        private static bool HasInstanceField(Type type, string fieldName)
        {
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                FieldInfo field = current.GetField(
                    fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                if (field != null)
                    return true;
            }

            return false;
        }

        private List<DangerousPattern> AnalyzeOnPlayerLeft(Type type, string sourceCode)
        {
            var patterns = new List<DangerousPattern>();

            try
            {
                // Find OnPlayerLeft method body
                var methodPattern = @"(public\s+override\s+void|override\s+public\s+void|void)\s+OnPlayerLeft\s*\(\s*VRCPlayerApi\s+(\w+)\s*\)";
                var methodMatch = Regex.Match(sourceCode, methodPattern);

                if (!methodMatch.Success)
                    return patterns;

                string playerParamName = methodMatch.Groups[2].Value;

                // Find method body. The shared scan reads an expression body as one; looking for the next
                // '{' instead walked out of 'OnPlayerLeft(VRCPlayerApi player) => Log(player);' into the
                // following method and reported whatever it did with its own parameters.
                if (!GetComponentInUpdateValidator.TryFindMethodBody(
                        sourceCode, methodMatch.Index + methodMatch.Length, out int bodyStart, out int bodyLength))
                {
                    return patterns;
                }

                string methodBody = sourceCode.Substring(bodyStart, bodyLength);

                // Check for storing player reference in field.
                //
                // The assigned name has to be a field of this behaviour. The pattern alone also matches
                // 'VRCPlayerApi leaving = player;', a local that dies with the callback and stores nothing,
                // and that was reported as "Storing 'player' in field" -- a warning about a line that is
                // exactly what the suggestion below tells the author to write.
                var fieldStorePattern = new Regex($@"(this\.)?(\w+)\s*=\s*{playerParamName}\s*;");
                foreach (Match fieldMatch in fieldStorePattern.Matches(methodBody))
                {
                    string assignedName = fieldMatch.Groups[2].Value;

                    if (!HasInstanceField(type, assignedName))
                        continue;

                    int lineNumber = CountLines(sourceCode, bodyStart + fieldMatch.Index);
                    patterns.Add(new DangerousPattern
                    {
                        Issue = $"Storing '{playerParamName}' in field '{assignedName}' - reference becomes invalid after OnPlayerLeft",
                        Suggestion = "Copy needed data (playerId, displayName) to local variables instead of storing the VRCPlayerApi reference.",
                        LineNumber = lineNumber
                    });

                    break;
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

                // There is deliberately no "accessing the player after SendCustomEvent" check here. It used
                // to flag any read of the player that came after any SendCustomEvent* call in the callback
                // as happening "after async operation". SendCustomEvent runs its handler before it returns,
                // and the delayed and network forms only schedule one -- either way the next statement
                // still executes inside OnPlayerLeft, where the player is valid by this validator's own
                // premise. Every report it made was on correct code.

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

