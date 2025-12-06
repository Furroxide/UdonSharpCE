using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates sync mode appropriateness.
    /// Detects continuous sync on rarely-changed data that should use manual sync.
    /// </summary>
    public class SyncModeValidator : IValidator
    {
        public string Name => "Sync Mode Appropriateness";
        public string Category => "Performance";
        public string Description => "Detects inefficient sync mode usage for rarely-changed data.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();

            foreach (var type in context.BehaviourTypes)
            {
                string syncMode = GetSyncMode(type);
                if (syncMode != "Continuous")
                    continue;

                // Get synced fields
                var syncedFields = GetSyncedFields(type);
                if (syncedFields.Count == 0)
                    continue;

                // Analyze each field for write patterns
                foreach (var field in syncedFields)
                {
                    var writeLocations = AnalyzeFieldWrites(type, field);

                    // If field is only written in Start/Awake/constructor
                    if (writeLocations.Count > 0 && writeLocations.All(w => IsInitializationMethod(w)))
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Warning,
                            ValidatorName = Name,
                            Message = $"Field '{field.Name}' only set during initialization but uses Continuous sync",
                            Details = "This field appears to be set once during Start/Awake but the behaviour uses Continuous sync mode. " +
                                     "Consider using Manual sync mode ([UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]) " +
                                     "and call RequestSerialization() when data changes.",
                            RelatedType = type,
                            RelatedField = field,
                            FilePath = context.GetSourceFile(type)
                        });
                    }
                }

                // Check for too many synced fields in continuous mode
                if (syncedFields.Count > 5)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Info,
                        ValidatorName = Name,
                        Message = $"Behaviour has {syncedFields.Count} synced fields with Continuous sync",
                        Details = "Many fields in Continuous sync can waste bandwidth. " +
                                 "Consider grouping related data or using Manual sync with delta updates.",
                        RelatedType = type,
                        FilePath = context.GetSourceFile(type)
                    });
                }
            }

            return issues;
        }

        private string GetSyncMode(Type type)
        {
            var attrs = type.GetCustomAttributes(true);
            foreach (var attr in attrs)
            {
                string attrName = attr.GetType().Name;
                if (attrName == "UdonBehaviourSyncModeAttribute" || attrName == "UdonBehaviourSyncMode")
                {
                    var modeProperty = attr.GetType().GetProperty("behaviourSyncMode");
                    if (modeProperty != null)
                    {
                        var value = modeProperty.GetValue(attr);
                        return value?.ToString() ?? "None";
                    }

                    var modeField = attr.GetType().GetField("behaviourSyncMode");
                    if (modeField != null)
                    {
                        var value = modeField.GetValue(attr);
                        return value?.ToString() ?? "None";
                    }
                }
            }
            return "None";
        }

        private List<FieldInfo> GetSyncedFields(Type type)
        {
            var fields = new List<FieldInfo>();
            var allFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in allFields)
            {
                var attrs = field.GetCustomAttributes(true);
                foreach (var attr in attrs)
                {
                    string attrName = attr.GetType().Name;
                    if (attrName == "UdonSyncedAttribute" || attrName == "UdonSynced")
                    {
                        fields.Add(field);
                        break;
                    }
                }
            }

            return fields;
        }

        private List<string> AnalyzeFieldWrites(Type type, FieldInfo field)
        {
            var writeLocations = new List<string>();

            string sourceFile = FindSourceFile(type);
            if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
                return writeLocations;

            try
            {
                string sourceCode = System.IO.File.ReadAllText(sourceFile);

                // Find methods that write to this field
                var methods = new[] { "Start", "Awake", "OnEnable", "Update", "FixedUpdate", "LateUpdate", 
                                      "OnPlayerJoined", "OnPlayerLeft", "Interact", "OnPickup", "OnDrop" };

                foreach (var methodName in methods)
                {
                    // Find method body
                    var methodPattern = $@"(void|public|private|protected)\s+{methodName}\s*\([^)]*\)\s*\{{";
                    var methodMatch = Regex.Match(sourceCode, methodPattern);

                    if (!methodMatch.Success)
                        continue;

                    // Extract method body
                    int bodyStart = methodMatch.Index + methodMatch.Length - 1;
                    string methodBody = ExtractMethodBody(sourceCode, bodyStart);

                    if (string.IsNullOrEmpty(methodBody))
                        continue;

                    // Check for field assignment
                    var assignPattern = $@"\b{field.Name}\s*=(?!=)";
                    if (Regex.IsMatch(methodBody, assignPattern))
                    {
                        writeLocations.Add(methodName);
                    }

                    // Also check for compound assignments (+=, -=, etc.)
                    var compoundPattern = $@"\b{field.Name}\s*[\+\-\*\/\%\&\|\^]=";
                    if (Regex.IsMatch(methodBody, compoundPattern))
                    {
                        writeLocations.Add(methodName);
                    }

                    // Check for increment/decrement
                    var incDecPattern = $@"(\+\+|\-\-)\s*{field.Name}|{field.Name}\s*(\+\+|\-\-)";
                    if (Regex.IsMatch(methodBody, incDecPattern))
                    {
                        writeLocations.Add(methodName);
                    }
                }

                // Check for field initializer
                var initPattern = $@"\b{field.Name}\s*=\s*[^;]+;";
                var initMatch = Regex.Match(sourceCode, initPattern);
                if (initMatch.Success)
                {
                    // Check if this is in class body (field initialization)
                    int classBodyStart = sourceCode.IndexOf("class ");
                    int firstMethodStart = FindFirstMethodStart(sourceCode);

                    if (initMatch.Index > classBodyStart && initMatch.Index < firstMethodStart)
                    {
                        writeLocations.Add(".ctor"); // Field initializer
                    }
                }
            }
            catch
            {
                // Ignore parsing errors
            }

            return writeLocations.Distinct().ToList();
        }

        private string ExtractMethodBody(string sourceCode, int bodyStart)
        {
            if (bodyStart >= sourceCode.Length || sourceCode[bodyStart] != '{')
                return null;

            int braceCount = 1;
            int pos = bodyStart + 1;

            while (pos < sourceCode.Length && braceCount > 0)
            {
                if (sourceCode[pos] == '{') braceCount++;
                else if (sourceCode[pos] == '}') braceCount--;
                pos++;
            }

            return sourceCode.Substring(bodyStart, pos - bodyStart);
        }

        private int FindFirstMethodStart(string sourceCode)
        {
            var methodPattern = @"(void|public|private|protected|internal)\s+\w+\s*\([^)]*\)\s*\{";
            var match = Regex.Match(sourceCode, methodPattern);
            return match.Success ? match.Index : sourceCode.Length;
        }

        private bool IsInitializationMethod(string methodName)
        {
            return methodName == "Start" || methodName == "Awake" || methodName == ".ctor" || methodName == "OnEnable";
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
    }
}

