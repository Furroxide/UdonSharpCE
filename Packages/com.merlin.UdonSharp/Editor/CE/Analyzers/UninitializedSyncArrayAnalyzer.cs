using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Detects synced array fields that are not initialized.
    ///
    /// A synced array that is still null when its behaviour serializes cannot be sent, and nothing
    /// reports it: the sync simply stops working. CE closes that off by requiring every synced array to
    /// have a non-null initializer at its declaration. The length written there is not a commitment --
    /// under Manual sync the array can be replaced by one of another length at any time.
    /// </summary>
    /// <example>
    /// // Bad - will trigger CE0001 error
    /// [UdonSynced] private int[] scores;
    ///
    /// // Good - properly initialized
    /// [UdonSynced] private int[] scores = new int[16];
    /// </example>
    internal class UninitializedSyncArrayAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0001";
        public string AnalyzerName => "Uninitialized Synced Array";
        public string Description => "Detects synced array fields that are not initialized at declaration time.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Check all field symbols in the type
            if (type.FieldSymbols.IsDefaultOrEmpty)
                return diagnostics;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                // Skip non-synced fields
                if (!field.IsSynced)
                    continue;

                // Skip non-array fields
                if (!field.Type.IsArray)
                    continue;

                // Check if the field has an initializer
                if (field.InitializerSyntax == null)
                {
                    // Get the location for the diagnostic
                    Location location = GetFieldLocation(field);

                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        location,
                        $"Synced array field '{field.Name}' has no initializer, so it is null until something assigns it. " +
                        NullArrayConsequence,
                        AnalyzerId,
                        AnalyzerId,
                        BuildInitializerSuggestion(field)
                    ));
                }
                else
                {
                    // Check if the initializer is null literal
                    string initText = field.InitializerSyntax.ToString().Trim();
                    if (initText == "null")
                    {
                        Location location = GetFieldLocation(field);

                        diagnostics.Add(AnalyzerDiagnostic.Error(
                            location,
                            $"Synced array field '{field.Name}' is initialized to null. " +
                            NullArrayConsequence,
                            AnalyzerId,
                            AnalyzerId,
                            BuildInitializerSuggestion(field)
                        ));
                    }
                }
            }

            return diagnostics;
        }

        /// <summary>
        /// Why the rule exists, in terms of what happens rather than of a requirement VRChat does not have.
        ///
        /// The text used to say VRChat needs synced arrays "to have a fixed size defined at compile time".
        /// It does not -- this package's own NetPhysicsWorld declares 'new byte[0]' and replaces it with an
        /// array of a different length on every send -- and an author who believed the message would
        /// conclude that a resizable synced array is impossible. What actually goes wrong is the null.
        /// </summary>
        private const string NullArrayConsequence =
            "Udon cannot serialize a null synced array, so the behaviour's variable sync fails for as long as the " +
            "field is null. CE requires the array to exist from its declaration so that cannot happen; the length " +
            "given there is only a starting value, and under Manual sync the array may be replaced by one of a " +
            "different length at runtime.";

        /// <summary>
        /// The declaration to write instead, spelled with the field's own accessibility and array type.
        ///
        /// The suggestion was a fixed 'private int[] name = new int[SIZE];' whatever the field was, so a
        /// 'public Vector3[] points;' was told to become a private int array. Pasted as written, that is a
        /// second compile error in place of the first.
        /// </summary>
        private static string BuildInitializerSuggestion(FieldSymbol field)
        {
            string arrayType = GetDeclaredTypeText(field);

            // 'Vector3[]' becomes 'Vector3[SIZE]', and a jagged 'int[][]' becomes 'int[SIZE][]', which is
            // how C# spells allocating the outer array.
            int firstBracket = arrayType.IndexOf('[');
            string creation = firstBracket >= 0
                ? arrayType.Insert(firstBracket + 1, "SIZE")
                : arrayType + "[SIZE]";

            return "Initialize it where it is declared: " +
                   $"{GetAccessibilityText(field.RoslynSymbol.DeclaredAccessibility)} {arrayType} {field.Name} = new {creation};";
        }

        /// <summary>
        /// The field's type as its author wrote it, falling back to Roslyn's short spelling.
        /// </summary>
        private static string GetDeclaredTypeText(FieldSymbol field)
        {
            SyntaxNode declarator = field.RoslynSymbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();

            if (declarator?.Parent is VariableDeclarationSyntax declaration)
                return declaration.Type.ToString();

            return field.Type.RoslynSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        }

        private static string GetAccessibilityText(Accessibility accessibility)
        {
            switch (accessibility)
            {
                case Accessibility.Public:
                    return "public";

                case Accessibility.Protected:
                    return "protected";

                case Accessibility.Internal:
                    return "internal";

                case Accessibility.ProtectedOrInternal:
                    return "protected internal";

                case Accessibility.ProtectedAndInternal:
                    return "private protected";

                default:
                    return "private";
            }
        }

        /// <summary>
        /// Gets the Roslyn Location for a field symbol.
        /// </summary>
        private Location GetFieldLocation(FieldSymbol field)
        {
            var declaringSyntaxRefs = field.RoslynSymbol.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs.Length > 0)
            {
                return declaringSyntaxRefs.First().GetSyntax().GetLocation();
            }

            // Fallback to symbol locations
            var locations = field.RoslynSymbol.Locations;
            if (locations.Length > 0)
            {
                return locations.First();
            }

            return Location.None;
        }
    }
}
