using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Detects synced fields written on a Manual-sync behaviour that never calls RequestSerialization().
    ///
    /// Manual sync transmits nothing on its own. Without a RequestSerialization() call the owner's writes
    /// stay on the owner's client forever, and because the owner sees its own values update, the behaviour
    /// looks correct in single-player testing and is broken the moment a second player joins.
    /// </summary>
    /// <example>
    /// // Warning - the write never leaves the owner
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class ScoreBoard : UdonSharpBehaviour
    /// {
    ///     [UdonSynced] private int _score;
    ///
    ///     public void AddPoint()
    ///     {
    ///         _score++;  // CE0015: nothing calls RequestSerialization()
    ///     }
    /// }
    /// </example>
    internal class ManualSyncWithoutSerializationAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0015";
        public string AnalyzerName => "Manual Sync Without RequestSerialization";
        public string Description => "Detects Manual-sync behaviours that write synced fields but never call RequestSerialization().";
        public bool IsEnabledByDefault => true;

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            if (type == null || compilationContext == null)
                return diagnostics;

            if (CENetworkAnalysis.GetDeclaredSyncMode(type) != BehaviourSyncMode.Manual)
                return diagnostics;

            if (CENetworkAnalysis.GetSyncedFields(type).Count == 0)
                return diagnostics;

            // No SyncOnJoin exemption: source generation is Phase A of the compile and analyzers run after
            // Bind, so the payload field and the RequestSerialization() the pipeline generates are already
            // in the tree by the time this runs. Skipping those behaviours excused the one case where the
            // generated glue failing to appear is worth reporting.
            if (CENetworkAnalysis.FindFirstRequestSerializationIncludingBases(type, compilationContext) != null)
                return diagnostics;

            CENetworkAnalysis.BehaviourMethodGraph graph = CENetworkAnalysis.BuildMethodGraph(type);
            if (graph.Methods.Count == 0)
                return diagnostics;

            List<CENetworkAnalysis.SyncedFieldWrite> writes =
                CENetworkAnalysis.FindSyncedFieldWrites(type, graph.Methods, compilationContext);

            var reported = new HashSet<string>(StringComparer.Ordinal);

            foreach (CENetworkAnalysis.SyncedFieldWrite write in writes)
            {
                // One report per field, at its first write. Repeating it per assignment would bury the
                // fact that the whole behaviour is missing a single call.
                if (!reported.Add(write.Field.Name))
                    continue;

                diagnostics.Add(AnalyzerDiagnostic.Warning(
                    write.Node?.GetLocation() ?? CENetworkAnalysis.GetFieldLocation(write.Field),
                    $"Synced field '{write.Field.Name}' is written in '{write.MethodName}', but '{type.Name}' uses " +
                    "BehaviourSyncMode.Manual and never calls RequestSerialization(). Manual sync transmits nothing " +
                    $"until RequestSerialization() runs, so '{write.Field.Name}' never reaches any other player.",
                    AnalyzerId,
                    AnalyzerId,
                    $"Call RequestSerialization() on the owner after writing '{write.Field.Name}' (typically at the end of " +
                    $"'{write.MethodName}'), or change the behaviour to [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)] " +
                    "if the payload is small enough and contains no arrays. If another behaviour calls RequestSerialization() " +
                    "on this one, move that call next to the write so the two cannot drift apart."
                ));
            }

            return diagnostics;
        }
    }
}
