using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Detects a behaviour sending a network event to one of its own [LocalOnly] methods, through
    /// SendCustomNetworkEvent or CENet.Rpc.
    ///
    /// This analyzer helps prevent accidental network calls to methods that are
    /// designed to only run locally, such as visual effects or audio playback.
    ///
    /// What it reports is deliberately narrow: the event name has to be readable at compile time (a
    /// string literal or a <c>nameof</c>), and the behaviour the event is sent to has to be provably of
    /// the analyzed type -- no receiver, <c>this</c>, <c>base</c>, or an expression whose type is this
    /// type or derives from it. A send to any other behaviour is not this type's [LocalOnly] method being
    /// called, whatever the event is named, and a receiver that cannot be resolved is left alone.
    /// </summary>
    /// <example>
    /// public class GameManager : UdonSharpBehaviour
    /// {
    ///     [LocalOnly("VFX should only render locally")]
    ///     private void PlayVFX() { }
    ///
    ///     public void BadExample()
    ///     {
    ///         // CE0012: Cannot call [LocalOnly] method via network
    ///         SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayVFX));
    ///
    ///         // CE0012 as well: the explicit send is still a network send.
    ///         CENet.Rpc(this, RpcTarget.All, nameof(PlayVFX));
    ///     }
    /// }
    /// </example>
    internal class LocalOnlyAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0012";
        public string AnalyzerName => "LocalOnly Method Validation";
        public string Description => "Prevents network calls to [LocalOnly] methods.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// Network event method names to detect.
        /// </summary>
        private static readonly HashSet<string> NetworkEventMethods = new HashSet<string>
        {
            "SendCustomNetworkEvent"
        };

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            if (type == null || context == null)
                return diagnostics;

            // Build a set of LocalOnly method names for this type
            var localOnlyMethods = new Dictionary<string, LocalOnlyAttribute>();
            var memberMethods = type.GetMembers<MethodSymbol>(context);
            if (memberMethods == null)
                return diagnostics;
                
            foreach (var method in memberMethods)
            {
                if (method == null)
                    continue;
                    
                var localOnlyAttr = method.GetAttribute<LocalOnlyAttribute>();
                if (localOnlyAttr != null)
                {
                    localOnlyMethods[method.Name] = localOnlyAttr;
                }
            }

            // If no LocalOnly methods, nothing to check
            if (localOnlyMethods.Count == 0)
                return diagnostics;

            // Check all methods for SendCustomNetworkEvent calls
            var methods = type.GetMembers<MethodSymbol>(context);
            if (methods == null)
                return diagnostics;

            foreach (MethodSymbol method in methods)
            {
                if (method == null)
                    continue;
                var syntaxRefs = method.RoslynSymbol?.DeclaringSyntaxReferences;
                if (syntaxRefs == null || syntaxRefs.Value.Length == 0)
                    continue;

                var methodSyntax = syntaxRefs.Value.First().GetSyntax();

                // Null when the tree is not in the compilation. The checks below then report only what
                // they can prove from syntax alone, which is a send with no receiver or to 'this'.
                SemanticModel model = CENetworkAnalysis.GetSemanticModel(compilationContext, methodSyntax);

                // Find all invocation expressions
                var invocations = methodSyntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    if (!TryReadNetworkSend(invocation, model, out ExpressionSyntax sentTo,
                            out ExpressionSyntax eventName, out string sentThrough))
                    {
                        continue;
                    }

                    string targetMethodName = GetStringLiteralOrNameof(eventName);
                    if (targetMethodName == null)
                        continue;

                    // Check if the target method is LocalOnly
                    if (!localOnlyMethods.TryGetValue(targetMethodName, out var localOnlyAttr))
                        continue;

                    // The name matching one of THIS type's [LocalOnly] methods says nothing until the event
                    // is known to be sent to this type. Without this, 'fx.SendCustomNetworkEvent(All,
                    // "Open")' failed the compile of any behaviour that happened to have its own
                    // [LocalOnly] Open(), although the event went to a different behaviour entirely.
                    if (!IsSentToAnalyzedType(sentTo, model, type.RoslynSymbol))
                        continue;

                    string message = $"Cannot call [LocalOnly] method '{targetMethodName}' via {sentThrough}.";
                    if (!string.IsNullOrEmpty(localOnlyAttr.Message))
                    {
                        message += $" Reason: {localOnlyAttr.Message}";
                    }

                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        invocation.GetLocation(),
                        message,
                        AnalyzerId,
                        AnalyzerId,
                        "Call the method directly or remove the [LocalOnly] attribute."
                    ));
                }
            }

            return diagnostics;
        }

        /// <summary>
        /// Recognises the two ways a behaviour sends a network event, and picks out the behaviour it is
        /// sent to and the event name. <paramref name="sentTo"/> is null for a send with no receiver,
        /// which goes to the sending behaviour itself.
        /// </summary>
        /// <remarks>
        /// CENet.Rpc is included because it is the send CE itself recommends for a call the [Rpc] rewrite
        /// refuses, and it forwards straight to SendCustomNetworkEvent. Leaving it out made [LocalOnly]
        /// enforceable only against the spelling an author was being steered away from. It is matched
        /// through its symbol, never by name: a method that merely happens to be called Rpc is not a send.
        /// </remarks>
        private bool TryReadNetworkSend(
            InvocationExpressionSyntax invocation,
            SemanticModel model,
            out ExpressionSyntax sentTo,
            out ExpressionSyntax eventName,
            out string sentThrough)
        {
            sentTo = null;
            eventName = null;
            sentThrough = null;

            var args = invocation.ArgumentList?.Arguments;
            if (!args.HasValue)
                return false;

            string invokedMethodName = GetMethodName(invocation);
            if (invokedMethodName == null)
                return false;

            if (NetworkEventMethods.Contains(invokedMethodName))
            {
                // SendCustomNetworkEvent(target, eventName, ...), sent to whatever it is called on.
                if (args.Value.Count < 2)
                    return false;

                sentTo = (invocation.Expression as MemberAccessExpressionSyntax)?.Expression;
                eventName = args.Value[1].Expression;
                sentThrough = invokedMethodName;
                return true;
            }

            if (invokedMethodName == CENetRpcMethodName && IsCENetRpc(invocation, model))
            {
                // CENet.Rpc(target, to, eventName, ...), sent to its first argument.
                if (args.Value.Count < 3)
                    return false;

                sentTo = args.Value[0].Expression;
                eventName = args.Value[2].Expression;
                sentThrough = "CENet.Rpc";
                return true;
            }

            return false;
        }

        private const string CENetRpcMethodName = "Rpc";

        private static bool IsCENetRpc(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            if (model == null || !(model.GetSymbolInfo(invocation).Symbol is IMethodSymbol method))
                return false;

            return method.ContainingType != null &&
                   method.ContainingType.Name == "CENet" &&
                   method.ContainingType.ContainingNamespace?.ToDisplayString() == "UdonSharp.CE.Net";
        }

        /// <summary>
        /// Whether the behaviour an event is sent to is provably an instance of the analyzed type.
        /// </summary>
        /// <remarks>
        /// No receiver, 'this' and 'base' are this behaviour by construction. Anything else is decided by
        /// the expression's static type: the analyzed type itself, or a type deriving from it, which
        /// inherits the [LocalOnly] method. A base-typed or unrelated receiver is not reported, and
        /// neither is one whose type cannot be resolved -- a missed report here is the cheaper mistake,
        /// because this diagnostic is an Error and a wrong one fails a working project's compile.
        /// </remarks>
        private static bool IsSentToAnalyzedType(ExpressionSyntax sentTo, SemanticModel model, ITypeSymbol analyzedType)
        {
            if (sentTo == null || sentTo is ThisExpressionSyntax || sentTo is BaseExpressionSyntax)
                return true;

            if (model == null || analyzedType == null)
                return false;

            for (ITypeSymbol current = model.GetTypeInfo(sentTo).Type; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current, analyzedType))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Extracts the method name from an invocation expression.
        /// </summary>
        private string GetMethodName(InvocationExpressionSyntax invocation)
        {
            switch (invocation.Expression)
            {
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.Text;

                case MemberAccessExpressionSyntax memberAccess:
                    return memberAccess.Name.Identifier.Text;

                case GenericNameSyntax genericName:
                    return genericName.Identifier.Text;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Extracts the string value from a string literal or nameof expression.
        /// </summary>
        private string GetStringLiteralOrNameof(ExpressionSyntax expression)
        {
            switch (expression)
            {
                // String literal: "MethodName"
                case LiteralExpressionSyntax literal:
                    return literal.Token.ValueText;

                // nameof(MethodName), or nameof(Type.MethodName) / nameof(instance.MethodName). The qualified
                // spellings name the same event; which behaviour receives it is decided separately, from
                // the receiver of the send.
                case InvocationExpressionSyntax invocation:
                    if (invocation.Expression is IdentifierNameSyntax identifier &&
                        identifier.Identifier.Text == "nameof")
                    {
                        var arg = invocation.ArgumentList?.Arguments.FirstOrDefault();
                        if (arg?.Expression is IdentifierNameSyntax nameofTarget)
                        {
                            return nameofTarget.Identifier.Text;
                        }

                        if (arg?.Expression is MemberAccessExpressionSyntax qualifiedTarget)
                        {
                            return qualifiedTarget.Name.Identifier.Text;
                        }
                    }
                    break;
            }

            return null;
        }
    }
}
