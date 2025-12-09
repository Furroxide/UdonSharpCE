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
    /// Detects attempts to call [LocalOnly] methods via SendCustomNetworkEvent.
    ///
    /// This analyzer helps prevent accidental network calls to methods that are
    /// designed to only run locally, such as visual effects or audio playback.
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

                // Find all invocation expressions
                var invocations = methodSyntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    string invokedMethodName = GetMethodName(invocation);

                    if (invokedMethodName == null || !NetworkEventMethods.Contains(invokedMethodName))
                        continue;

                    // Check the second argument (event name)
                    var args = invocation.ArgumentList?.Arguments;
                    if (!args.HasValue || args.Value.Count < 2)
                        continue;

                    string targetMethodName = GetStringLiteralOrNameof(args.Value[1].Expression);
                    if (targetMethodName == null)
                        continue;

                    // Check if the target method is LocalOnly
                    if (localOnlyMethods.TryGetValue(targetMethodName, out var localOnlyAttr))
                    {
                        string message = $"Cannot call [LocalOnly] method '{targetMethodName}' via SendCustomNetworkEvent.";
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
            }

            return diagnostics;
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

                // nameof(MethodName)
                case InvocationExpressionSyntax invocation:
                    if (invocation.Expression is IdentifierNameSyntax identifier &&
                        identifier.Identifier.Text == "nameof")
                    {
                        var arg = invocation.ArgumentList?.Arguments.FirstOrDefault();
                        if (arg?.Expression is IdentifierNameSyntax nameofTarget)
                        {
                            return nameofTarget.Identifier.Text;
                        }
                    }
                    break;
            }

            return null;
        }
    }
}
