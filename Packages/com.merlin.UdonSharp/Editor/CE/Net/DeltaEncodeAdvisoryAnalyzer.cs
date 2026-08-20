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
    /// Reports that DeltaEncode is advisory only and does not change VRChat's sync wire format.
    /// </summary>
    internal class DeltaEncodeAdvisoryAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0013";
        public string AnalyzerName => "DeltaEncode Advisory";
        public string Description => "Warns that DeltaEncode is only a bandwidth-estimation hint.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            if (type.FieldSymbols.IsDefaultOrEmpty)
                yield break;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                SyncAttribute syncAttr = field.GetAttribute<SyncAttribute>();
                if (syncAttr == null || !syncAttr.DeltaEncodeHint)
                    continue;

                yield return AnalyzerDiagnostic.Warning(
                    GetFieldLocation(field),
                    $"DeltaEncode on '{field.Name}' is advisory only; VRChat still syncs the full field payload.",
                    AnalyzerId,
                    AnalyzerId,
                    "Use [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)] and RequestSerialization() after changing specific entries if you need real delta-style updates."
                );
            }
        }

        private static Location GetFieldLocation(FieldSymbol field)
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
