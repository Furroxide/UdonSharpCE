using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Detects SendCustomNetworkEvent calls on a behaviour that declares BehaviourSyncMode.None.
    ///
    /// None does not merely skip variable sync -- it removes the behaviour from networking entirely, so
    /// the event is never sent and no remote client ever runs the handler. The call still compiles, still
    /// runs, and still does nothing, which is why this needs to be caught at build time.
    ///
    /// NoVariableSync is a different mode and is explicitly fine here: it drops synced variables but keeps
    /// network events working. CE0011 already covers the [Rpc]-attributed form of this mistake; this
    /// completes the check for hand-written SendCustomNetworkEvent calls.
    /// </summary>
    /// <example>
    /// // Error - None disables network events
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    /// public class DoorButton : UdonSharpBehaviour
    /// {
    ///     public override void Interact()
    ///     {
    ///         SendCustomNetworkEvent(NetworkEventTarget.All, nameof(OpenDoor));  // CE0018
    ///     }
    /// }
    /// </example>
    internal class NetworkEventSyncModeAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0018";
        public string AnalyzerName => "Network Event On Non-Networked Behaviour";
        public string Description => "Detects SendCustomNetworkEvent on behaviours that declare BehaviourSyncMode.None.";
        public bool IsEnabledByDefault => true;

        private const string NetworkEventMethodName = "SendCustomNetworkEvent";

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            if (type == null || compilationContext == null)
                return diagnostics;

            if (CENetworkAnalysis.GetDeclaredSyncMode(type) != BehaviourSyncMode.None)
                return diagnostics;

            foreach (ClassDeclarationSyntax classDeclaration in CENetworkAnalysis.GetClassDeclarations(type))
            {
                foreach (InvocationExpressionSyntax invocation in classDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!string.Equals(CENetworkAnalysis.GetInvokedName(invocation), NetworkEventMethodName, StringComparison.Ordinal))
                        continue;

                    // Sending to another behaviour is governed by that behaviour's sync mode, not this one's.
                    if (!CENetworkAnalysis.IsSelfInvocation(invocation))
                        continue;

                    string methodName = CENetworkAnalysis.GetEnclosingMethodName(invocation);
                    string eventName = GetTargetEventName(invocation);

                    string eventDescription = eventName != null
                        ? $"'{eventName}'"
                        : "this event";

                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        invocation.GetLocation(),
                        $"SendCustomNetworkEvent in '{methodName}' targets {eventDescription} on '{type.Name}', which declares " +
                        "BehaviourSyncMode.None. None disables network events on the behaviour entirely, so this call can never " +
                        "reach any client and the handler only ever runs where it was already going to run: nowhere.",
                        AnalyzerId,
                        AnalyzerId,
                        "Change the attribute to [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)] -- it keeps network " +
                        "events working while still syncing no variables -- or call the method directly if it is meant to stay local."
                    ));
                }
            }

            return diagnostics;
        }

        /// <summary>
        /// The event name argument, so the message points at the handler the author expected to fire.
        /// </summary>
        private static string GetTargetEventName(InvocationExpressionSyntax invocation)
        {
            var arguments = invocation.ArgumentList?.Arguments;
            if (!arguments.HasValue || arguments.Value.Count < 2)
                return null;

            switch (arguments.Value[1].Expression)
            {
                case LiteralExpressionSyntax literal:
                    return literal.Token.ValueText;

                case InvocationExpressionSyntax nameofInvocation
                    when nameofInvocation.Expression is IdentifierNameSyntax nameofIdentifier &&
                         string.Equals(nameofIdentifier.Identifier.Text, "nameof", StringComparison.Ordinal):

                    ArgumentSyntax nameofArgument = nameofInvocation.ArgumentList?.Arguments.FirstOrDefault();
                    return (nameofArgument?.Expression as IdentifierNameSyntax)?.Identifier.Text;

                default:
                    return null;
            }
        }
    }
}
