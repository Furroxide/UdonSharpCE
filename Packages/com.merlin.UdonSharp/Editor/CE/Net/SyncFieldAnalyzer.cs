using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Validates [Sync] attribute usage and provides warnings for common issues.
    ///
    /// Checks for:
    /// - DeltaEncode on non-array types (ineffective)
    /// - Quantize on non-float types (ineffective)
    /// - Interpolation in Manual sync mode (has no effect)
    /// </summary>
    /// <example>
    /// // Warning - DeltaEncode on non-array
    /// [Sync(DeltaEncode = true)]
    /// public int score;  // CE0010: DeltaEncode only works on arrays
    ///
    /// // Warning - Quantize on non-float
    /// [Sync(Quantize = 0.01f)]
    /// public int count;  // CE0010: Quantize only works on float types
    ///
    /// // Warning - Interpolation in Manual mode
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class MyBehaviour : UdonSharpBehaviour
    /// {
    ///     [Sync(InterpolationMode.Linear)]
    ///     public float value;  // CE0010: Interpolation has no effect in Manual mode
    /// }
    /// </example>
    internal class SyncFieldAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0010";
        public string AnalyzerName => "Sync Field Validation";
        public string Description => "Validates [Sync] attribute usage and warns about ineffective options.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// Float-compatible types that support quantization.
        /// </summary>
        private static readonly HashSet<string> FloatTypes = new HashSet<string>
        {
            "System.Single",
            "System.Double",
            "UnityEngine.Vector2",
            "UnityEngine.Vector3",
            "UnityEngine.Vector4",
            "UnityEngine.Quaternion",
            "UnityEngine.Color"
        };

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Get behaviour sync mode
            var behaviourSyncAttr = type.GetAttribute<UdonBehaviourSyncModeAttribute>();
            bool isManualSync = behaviourSyncAttr?.behaviourSyncMode == BehaviourSyncMode.Manual;

            // Check all fields
            if (type.FieldSymbols == null)
                return diagnostics;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                var syncAttr = field.GetAttribute<SyncAttribute>();
                if (syncAttr == null)
                    continue;

                Location location = GetFieldLocation(field);

                // Check: DeltaEncode on non-array types
                if (syncAttr.DeltaEncode && !field.Type.IsArray)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Warning(
                        location,
                        $"DeltaEncode is only effective on array types, but '{field.Name}' is '{field.Type.Name}'.",
                        AnalyzerId,
                        AnalyzerId,
                        "Remove DeltaEncode or change the field type to an array."
                    ));
                }

                // Check: Quantize on non-float types
                if (syncAttr.Quantize > 0 && !IsFloatType(field.Type))
                {
                    diagnostics.Add(AnalyzerDiagnostic.Warning(
                        location,
                        $"Quantize is only effective on float types, but '{field.Name}' is '{field.Type.Name}'.",
                        AnalyzerId,
                        AnalyzerId,
                        "Remove Quantize or use a float-based type (float, Vector3, etc.)."
                    ));
                }

                // Check: Interpolation in Manual sync mode
                if (isManualSync && syncAttr.Interpolation != InterpolationMode.None)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Warning(
                        location,
                        $"Interpolation has no effect in Manual sync mode for field '{field.Name}'.",
                        AnalyzerId,
                        AnalyzerId,
                        "Use Continuous sync mode for interpolation, or remove the interpolation setting."
                    ));
                }

                // Check: Both [Sync] and [UdonSynced] present
                if (field.IsSynced)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Info(
                        location,
                        $"Field '{field.Name}' has both [Sync] and [UdonSynced] attributes. [Sync] settings will be used for CE analysis.",
                        AnalyzerId,
                        AnalyzerId
                    ));
                }
            }

            return diagnostics;
        }

        /// <summary>
        /// Checks if a type is a float-based type that supports quantization.
        /// </summary>
        private bool IsFloatType(TypeSymbol type)
        {
            // Handle arrays of float types
            if (type.IsArray)
            {
                return IsFloatType(type.ElementType);
            }

            string typeName = type.RoslynSymbol.ToString();
            return FloatTypes.Contains(typeName);
        }

        /// <summary>
        /// Gets the Roslyn Location for a field symbol.
        /// </summary>
        private Location GetFieldLocation(FieldSymbol field)
        {
            var declaringSyntaxRefs = field.RoslynSymbol?.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs != null && declaringSyntaxRefs.Length > 0)
            {
                return declaringSyntaxRefs.First().GetSyntax().GetLocation();
            }

            var locations = field.RoslynSymbol?.Locations;
            if (locations != null && locations.Length > 0)
            {
                return locations.First();
            }

            return Location.None;
        }
    }
}
