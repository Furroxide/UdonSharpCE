using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Reports a lambda or anonymous method that is still standing after CE's ForEach inlining and its
    /// Action-to-CECallback rewrite. Udon has no delegates, so nothing else can run it.
    /// </summary>
    internal class LambdaUsageAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0021";
        public string AnalyzerName => "Lambda Usage Validation";
        public string Description => "Reports unsupported lambda/anonymous method usage in UdonSharpCE.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            var typeSyntax = type.RoslynSymbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as TypeDeclarationSyntax;
            if (typeSyntax == null)
                return diagnostics;

            if (HasPreserveActionAttribute(typeSyntax.AttributeLists))
                return diagnostics;

            var methods = type.GetMembers<MethodSymbol>(context);
            foreach (MethodSymbol method in methods)
            {
                var methodSyntax = method.RoslynSymbol?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as MethodDeclarationSyntax;
                if (methodSyntax == null)
                    continue;

                if (HasPreserveActionAttribute(methodSyntax.AttributeLists))
                    continue;

                var lambdas = methodSyntax.DescendantNodes()
                    .Where(n => n is ParenthesizedLambdaExpressionSyntax ||
                                n is SimpleLambdaExpressionSyntax ||
                                n is AnonymousMethodExpressionSyntax);

                foreach (var lambda in lambdas)
                {
                    // The message lists exactly what CE lowers. It used to also promise "immediate invocation",
                    // which no pass ever lowered, so authors rewrote code into a shape that still failed here.
                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        lambda.GetLocation(),
                        "Lambdas and anonymous methods do not exist in Udon, and CE did not lower this one. CE " +
                        "lowers two shapes only. (1) A lambda or method group that is the only argument of a " +
                        "statement-level List<T>.ForEach or CEList<T>.ForEach/ForEachWithIndex/ForEachUntil call " +
                        "is inlined into a loop -- unless its body returns from inside a loop of its own, uses a " +
                        "label, declares a name an enclosing local already has, or (ForEachUntil) is more than a " +
                        "single return. (2) A parameterless System.Action lambda or anonymous method whose body " +
                        "is one call to a parameterless method of this behaviour becomes a CECallback -- unless " +
                        "its file, or a file it shares an Action member with, uses an Action in a way a " +
                        "CECallback cannot follow (?.Invoke(), +=, a null check, a conversion).",
                        AnalyzerId,
                        AnalyzerId,
                        "Write the loop out explicitly, or move the body into a public parameterless method and " +
                        "pass that method as a CECallback."
                    ));
                }
            }

            return diagnostics;
        }

        private static bool HasPreserveActionAttribute(SyntaxList<AttributeListSyntax> attributeLists)
        {
            foreach (var attrList in attributeLists)
            {
                foreach (var attr in attrList.Attributes)
                {
                    var name = attr.Name.ToString();
                    if (name == "CEPreserveAction" || name == "CEPreserveActionAttribute")
                        return true;
                }
            }
            return false;
        }
    }
}
