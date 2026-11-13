using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Detects Manual-sync behaviours that declare no synced fields.
    ///
    /// This is the shape the shipped LeaderboardManager had: BehaviourSyncMode.Manual,
    /// RequestSerialization() called on every score change, and not one [UdonSynced] field for it to send.
    /// The "multiplayer leaderboard" was single-player by construction and nothing in the toolchain said
    /// so, because serializing an empty payload is not an error -- it is just a no-op that costs a
    /// network message.
    /// </summary>
    /// <example>
    /// // Warning - RequestSerialization() has nothing to send
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class LeaderboardManager : UdonSharpBehaviour
    /// {
    ///     private CEDictionary&lt;int, int&gt; _scores;  // managed object, Udon cannot sync it
    ///
    ///     public void OnScoreChanged() => RequestSerialization();  // CE0016
    /// }
    /// </example>
    internal class ManualSyncWithoutSyncedFieldsAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0016";
        public string AnalyzerName => "Manual Sync Without Synced Fields";
        public string Description => "Detects Manual-sync behaviours that have nothing to serialize.";
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

            // A SyncOnJoin behaviour passes this check on its merits rather than by exemption: source
            // generation is Phase A of the compile and analyzers run after Bind, so the generated payload
            // field is already a synced field here. Exempting them instead would have hidden a generator
            // that silently produced nothing.
            //
            // The base chain counts. A subclass of a base that declares '[UdonSynced] protected int
            // _health;' serializes _health on every RequestSerialization() it makes, because the emitter
            // collects inherited fields; judging it by its own declarations alone told its author the
            // payload was empty and that nothing reached other players, on a behaviour that syncs.
            if (CENetworkAnalysis.HasSyncedFieldsIncludingBases(type))
                return diagnostics;

            InvocationExpressionSyntax requestSerialization =
                CENetworkAnalysis.FindFirstRequestSerialization(type, compilationContext);

            if (requestSerialization != null)
            {
                // Manual sync plus a RequestSerialization() call is proof the author expected data to move.
                string methodName = CENetworkAnalysis.GetEnclosingMethodName(requestSerialization);

                diagnostics.Add(AnalyzerDiagnostic.Warning(
                    requestSerialization.GetLocation(),
                    $"'{type.Name}' uses BehaviourSyncMode.Manual but declares no synced fields, so the " +
                    $"RequestSerialization() call in '{methodName}' serializes an empty payload. Whatever state this " +
                    "behaviour is meant to replicate never reaches any other player.",
                    AnalyzerId,
                    AnalyzerId,
                    "Add [UdonSynced] (or [Sync]) fields holding the state this behaviour replicates -- managed types such as " +
                    "CEDictionary or List<T> cannot be synced, so mirror them into synced arrays. If this behaviour only needs " +
                    "network events, change the attribute to [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)] and drop " +
                    "the RequestSerialization() call."
                ));

                return diagnostics;
            }

            // No RequestSerialization() either. Manual sync on a behaviour with nothing to send is dead
            // weight rather than a live bug -- it is sometimes written just to match a sibling behaviour's
            // sync method on the same GameObject -- so this stays informational.
            diagnostics.Add(AnalyzerDiagnostic.Info(
                CENetworkAnalysis.GetTypeLocation(type),
                $"'{type.Name}' declares BehaviourSyncMode.Manual but has no synced fields and never calls " +
                "RequestSerialization(), so nothing on it is ever serialized. Use " +
                "[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)] if it only needs network events.",
                AnalyzerId,
                AnalyzerId));

            return diagnostics;
        }
    }
}
