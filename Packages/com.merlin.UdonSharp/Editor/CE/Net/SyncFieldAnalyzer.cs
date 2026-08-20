using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Validates the tuning options on [Sync] and warns when one of them is inert.
    ///
    /// Checks exactly three things:
    /// - DeltaEncodeHint on a non-array type (ineffective)
    /// - Quantize on a non-float type (ineffective)
    /// - Interpolation under Manual sync mode (has no effect)
    ///
    /// It does not read <see cref="SyncAttribute.Authority"/> and must not be described as if it did.
    /// Authority is checked elsewhere: CE0014 requires every write to an Authority.Owner field to sit
    /// behind an ownership check, and CE0063 reports an Authority.Predicted field on a behaviour whose
    /// sync mode replicates nothing at all. The published analyzer table once advertised CE0010 as
    /// validating "authority, quantize, delta, interpolation", which promised a check no code performed --
    /// the exact silent-gap shape this diagnostic set exists to remove. Keep this list, the Description
    /// below, and the table in Tools/Docusaurus/docs/ce-editor-tools.md in step with the three cases here.
    /// </summary>
    /// <example>
    /// // Warning - DeltaEncode on non-array
    /// [Sync(Authority.Owner, DeltaEncodeHint = true)]
    /// public int score;  // CE0010: DeltaEncode only works on arrays
    ///
    /// // Warning - Quantize on non-float
    /// [Sync(Authority.Owner, Quantize = 0.01f)]
    /// public int count;  // CE0010: Quantize only works on float types
    ///
    /// // Warning - Interpolation in Manual mode
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class MyBehaviour : UdonSharpBehaviour
    /// {
    ///     [Sync(Authority.Owner, InterpolationMode.Linear)]
    ///     public float value;  // CE0010: Interpolation has no effect in Manual mode
    /// }
    /// </example>
    internal class SyncFieldAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0010";
        public string AnalyzerName => "Sync Field Validation";
        // Names the three checks rather than saying "validates [Sync] usage". This string is what the
        // Udon CE/Analyzers window shows and what GetAnalyzerInfo() reports, and the vague version is how
        // the published table came to advertise CE0010 as checking authority -- which it has never done.
        public string Description =>
            "Warns when a [Sync] option is inert: DeltaEncodeHint on a non-array, Quantize on a non-float, " +
            "or interpolation under Manual sync. Does not check Authority -- that is CE0014 and CE0063.";
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
            if (type.FieldSymbols.IsDefaultOrEmpty)
                return diagnostics;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                var syncAttr = field.GetAttribute<SyncAttribute>();
                if (syncAttr == null)
                    continue;

                Location location = GetFieldLocation(field);

                // Check: DeltaEncode on non-array types
                if (syncAttr.DeltaEncodeHint && !field.Type.IsArray)
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
            if (declaringSyntaxRefs != null && declaringSyntaxRefs.Value.Length > 0)
            {
                return declaringSyntaxRefs.Value.First().GetSyntax().GetLocation();
            }

            var locations = field.RoslynSymbol?.Locations;
            if (locations != null && locations.Value.Length > 0)
            {
                return locations.Value.First();
            }

            return Location.None;
        }
    }
}
