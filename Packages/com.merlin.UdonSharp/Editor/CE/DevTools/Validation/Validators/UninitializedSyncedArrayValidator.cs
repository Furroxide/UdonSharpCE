using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates that synced arrays are initialized.
    /// Uninitialized synced arrays cause NullReferenceException on join.
    /// </summary>
    public class UninitializedSyncedArrayValidator : IValidator
    {
        public string Name => "Uninitialized Synced Array";
        public string Category => "Networking";
        public string Description => "Detects synced arrays that are not initialized.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();

            foreach (var type in context.BehaviourTypes)
            {
                var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                foreach (var field in fields)
                {
                    // Check if field is an array
                    if (!field.FieldType.IsArray)
                        continue;

                    // Check if field participates in Udon sync.
                    if (!HasUdonSyncedAttribute(field))
                        continue;

                    // Check if field is initialized
                    if (!IsFieldInitialized(type, field))
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Error,
                            ValidatorName = Name,
                            Message = $"Synced array '{field.Name}' is not initialized",
                            // Both forms are written out in full, and the [Sync] one carries an Authority,
                            // because the previous text collapsed them into "`[UdonSynced]` or `[Sync] public
                            // int[] data = ...`" -- and bare [Sync] stopped compiling once Authority became a
                            // required positional argument. A validator that hands the author CS7036 is worse
                            // than one that says nothing. Owner is the placeholder: this validator reflects
                            // over FieldInfo and never sees the writes, so it cannot pick the authority.
                            Details = "Uninitialized synced arrays cause NullReferenceException on player join. " +
                                     "Initialize in the field declaration: `[UdonSynced] public int[] data = new int[16];` " +
                                     "or `[Sync(Authority.Owner)] public int[] data = new int[16];` -- [Sync] takes an " +
                                     "Authority as its first, required argument.",
                            RelatedType = type,
                            RelatedField = field,
                            FilePath = context.GetSourceFile(type)
                        });
                    }
                }
            }

            return issues;
        }

        private bool HasUdonSyncedAttribute(FieldInfo field)
        {
            return field.GetCustomAttribute<UdonSyncedAttribute>(true) != null;
        }

        private bool IsFieldInitialized(Type type, FieldInfo field)
        {
            // Try to check by analyzing source code
            string sourceFile = FindSourceFile(type);
            if (string.IsNullOrEmpty(sourceFile) || !System.IO.File.Exists(sourceFile))
            {
                // Can't determine - assume uninitialized to be safe
                return false;
            }

            try
            {
                string sourceCode = System.IO.File.ReadAllText(sourceFile);

                // Look for field declaration with initializer
                // Patterns:
                //   [UdonSynced] public int[] field = new int[N];
                //   [Sync(Authority.Owner)] private int[] field = new int[] { 1, 2, 3 };

                // Find field declaration
                var patterns = new[]
                {
                    // Field initializer with explicit array type
                    $@"\b{field.FieldType.GetElementType()?.Name}\s*\[\s*\]\s+{field.Name}\s*=\s*new",
                    // Field initializer with an explicit construction type
                    $@"\b{field.Name}\s*=\s*new\s+{field.FieldType.GetElementType()?.Name}\s*\[",
                    // Generic pattern for any array initializer
                    $@"\b{field.Name}\s*=\s*new\s+\w+\s*\["
                };

                foreach (var pattern in patterns)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(sourceCode, pattern))
                        return true;
                }

                // Also check for initializer in constructor or Awake/Start
                var initPatterns = new[]
                {
                    $@"void\s+(Awake|Start)\s*\([^)]*\)[^{{]*\{{[^}}]*{field.Name}\s*=\s*new",
                    $@"{type.Name}\s*\([^)]*\)[^{{]*\{{[^}}]*{field.Name}\s*=\s*new"
                };

                foreach (var pattern in initPatterns)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(sourceCode, pattern, 
                        System.Text.RegularExpressions.RegexOptions.Singleline))
                        return true;
                }
            }
            catch
            {
                // If we can't parse, assume uninitialized to be safe
            }

            return false;
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

