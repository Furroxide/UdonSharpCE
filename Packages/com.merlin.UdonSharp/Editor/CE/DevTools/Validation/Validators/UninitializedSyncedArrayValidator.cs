using System;
using System.Collections.Generic;
using System.Reflection;
using UdonSharp;
using UdonSharp.CE.Editor.DevTools.Analysis;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates that synced arrays are initialized.
    /// Udon cannot serialize a null synced array, and a behaviour holding one stops syncing.
    ///
    /// The check reads the field off the behaviours in the scene. It used to look for an initializer by
    /// running regular expressions over the source file of the behaviour's own type, and "could not find
    /// one" was reported as an error. That made it wrong in every direction the compiler is right in: an
    /// array a base class declares and initializes is not in the subclass's file, 'int[] a = { 1, 2 };'
    /// and 'new[] { ... }' contain no 'new T[', and a helper-built default contains no 'new' at all -- each
    /// one an Error on a behaviour whose array is plainly there. CE0001 already rejects a missing
    /// initializer at compile time, so in a project that builds, those false errors were the only thing
    /// this validator could add. The object itself does not have that problem: its array is null or it is
    /// not.
    /// </summary>
    public class UninitializedSyncedArrayValidator : IValidator
    {
        public string Name => "Uninitialized Synced Array";
        public string Category => "Networking";
        public string Description => "Detects synced arrays that are null on a behaviour in the scene.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();

            if (context?.Behaviours == null)
                return issues;

            // One report per field, not per object: forty pooled bullets with the same null array are one
            // declaration to fix.
            var reported = new HashSet<string>(StringComparer.Ordinal);

            foreach (UdonSharpBehaviour behaviour in context.Behaviours)
            {
                // Unity's null: a destroyed behaviour left in the list has nothing to read.
                if (behaviour == null)
                    continue;

                // Includes private fields declared on base classes, which Type.GetFields on the concrete
                // type leaves out and which sync like any other.
                foreach (FieldInfo field in BehaviourAnalyzer.GetInstanceFieldsIncludingBases(behaviour.GetType()))
                {
                    // Check if field is an array
                    if (!field.FieldType.IsArray)
                        continue;

                    // Check if field participates in Udon sync.
                    if (!HasUdonSyncedAttribute(field))
                        continue;

                    if (field.GetValue(behaviour) != null)
                        continue;

                    Type declaringType = field.DeclaringType ?? behaviour.GetType();

                    if (!reported.Add(declaringType.FullName + "." + field.Name))
                        continue;

                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Error,
                        ValidatorName = Name,
                        Message = $"Synced array '{field.Name}' is null on '{behaviour.name}' ({declaringType.Name})",
                        // Both forms are written out in full, and the [Sync] one carries an Authority,
                        // because the previous text collapsed them into "`[UdonSynced]` or `[Sync] public
                        // int[] data = ...`" -- and bare [Sync] stopped compiling once Authority became a
                        // required positional argument. A validator that hands the author CS7036 is worse
                        // than one that says nothing. Owner is the placeholder: this validator reflects
                        // over FieldInfo and never sees the writes, so it cannot pick the authority.
                        Details = "Udon cannot serialize a null synced array, so this behaviour's sync fails for as " +
                                 "long as the field is null. Give it a value where it is declared: " +
                                 "`[UdonSynced] public int[] data = new int[16];` " +
                                 "or `[Sync(Authority.Owner)] public int[] data = new int[16];` -- [Sync] takes an " +
                                 "Authority as its first, required argument.",
                        Context = behaviour,
                        RelatedType = declaringType,
                        RelatedField = field,
                        // The file that declares the field, which for an inherited array is the base
                        // class's and not the file of the behaviour that happens to be in the scene.
                        FilePath = context.GetSourceFile(declaringType)
                    });
                }
            }

            return issues;
        }

        private bool HasUdonSyncedAttribute(FieldInfo field)
        {
            return field.GetCustomAttribute<UdonSyncedAttribute>(true) != null;
        }
    }
}
