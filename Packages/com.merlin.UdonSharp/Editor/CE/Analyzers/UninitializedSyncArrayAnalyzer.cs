using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Detects synced array fields that are not initialized.
    ///
    /// VRChat requires synced arrays to be initialized at declaration time with a fixed size.
    /// Arrays that are not initialized will cause runtime errors or unexpected sync behavior.
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
                        $"Synced array field '{field.Name}' must be initialized at declaration. " +
                        "VRChat requires synced arrays to have a fixed size defined at compile time.",
                        AnalyzerId,
                        AnalyzerId,
                        $"Initialize the array: private int[] {field.Name} = new int[SIZE];"
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
                            "VRChat requires synced arrays to be initialized with a fixed-size array.",
                            AnalyzerId,
                            AnalyzerId,
                            $"Initialize the array: private int[] {field.Name} = new int[SIZE];"
                        ));
                    }
                }
            }

            return diagnostics;
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
