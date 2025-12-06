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
    /// Warns when GetComponent variants are called in Update/FixedUpdate/LateUpdate.
    ///
    /// GetComponent calls are expensive in Udon (much more so than in regular Unity).
    /// Calling them every frame causes significant performance degradation.
    /// Component references should be cached in Start() or Awake().
    /// </summary>
    /// <example>
    /// // Bad - will trigger CE0002 warning
    /// void Update()
    /// {
    ///     var renderer = GetComponent&lt;Renderer&gt;(); // CE0002
    /// }
    ///
    /// // Good - cached reference
    /// private Renderer _renderer;
    /// void Start()
    /// {
    ///     _renderer = GetComponent&lt;Renderer&gt;();
    /// }
    /// </example>
    internal class GetComponentInUpdateAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0002";
        public string AnalyzerName => "GetComponent in Update";
        public string Description => "Warns when GetComponent is called in frequently-executed methods like Update.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// Methods that are called frequently and should not contain GetComponent calls.
        /// </summary>
        private static readonly HashSet<string> HotPathMethods = new HashSet<string>
        {
            "Update",
            "FixedUpdate",
            "LateUpdate",
            "OnAnimatorIK",
            "OnAnimatorMove",
            "OnRenderObject",
            "OnDrawGizmos",
            "OnGUI"
        };

        /// <summary>
        /// GetComponent method names to detect.
        /// </summary>
        private static readonly HashSet<string> GetComponentMethods = new HashSet<string>
        {
            "GetComponent",
            "GetComponents",
            "GetComponentInChildren",
            "GetComponentsInChildren",
            "GetComponentInParent",
            "GetComponentsInParent"
        };

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Get all methods in the type
            var methods = type.GetMembers<MethodSymbol>(context);

            foreach (MethodSymbol method in methods)
            {
                // Only check hot path methods
                if (!HotPathMethods.Contains(method.Name))
                    continue;

                // Check the method's syntax for GetComponent calls
                var syntaxRefs = method.RoslynSymbol?.DeclaringSyntaxReferences;
                if (syntaxRefs == null || syntaxRefs.Length == 0)
                    continue;

                var methodSyntax = syntaxRefs.First().GetSyntax();

                // Find all invocation expressions in the method
                var invocations = methodSyntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    string methodName = GetMethodName(invocation);

                    if (methodName != null && GetComponentMethods.Contains(methodName))
                    {
                        diagnostics.Add(AnalyzerDiagnostic.Warning(
                            invocation.GetLocation(),
                            $"'{methodName}' called in '{method.Name}'. " +
                            "GetComponent is expensive in Udon and should not be called every frame.",
                            AnalyzerId,
                            AnalyzerId,
                            "Cache the component reference in Start() or Awake() instead."
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

                case MemberBindingExpressionSyntax memberBinding:
                    if (memberBinding.Name is GenericNameSyntax bindingGeneric)
                        return bindingGeneric.Identifier.Text;
                    return memberBinding.Name.Identifier.Text;

                default:
                    return null;
            }
        }
    }
}
