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
    /// Reports unsupported lambda or anonymous method usage that survives CE optimizers.
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
                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        lambda.GetLocation(),
                        "Lambda/anonymous methods are not supported by Udon at runtime unless they are " +
                        "inlined by CE (immediate invocation) or used in supported ForEach helpers. " +
                        "Rewrite as an explicit loop or extract a method and use CECallback.",
                        AnalyzerId,
                        AnalyzerId,
                        "Use a block with an explicit loop, or replace with a method + CECallback."
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
