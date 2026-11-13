using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
                if (!syntaxRefs.HasValue || syntaxRefs.Value.Length == 0)
                    continue;

                var methodSyntax = syntaxRefs.Value.First().GetSyntax();

                // Find all invocation expressions in the method
                var invocations = methodSyntax.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>();

                foreach (var invocation in invocations)
                {
                    string methodName = GetMethodName(invocation);

                    if (methodName != null && GetComponentMethods.Contains(methodName))
                    {
                        if (IsLazyCacheFill(invocation, methodSyntax))
                            continue;

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
        /// Whether the call only runs to fill an empty cache:
        /// 'if (_renderer == null) _renderer = GetComponent&lt;Renderer&gt;();'.
        ///
        /// That line sits in Update and calls GetComponent, and it is the fix this diagnostic asks for
        /// written lazily -- the lookup happens on the first frame and never again. Reporting it told the
        /// author that a call which runs once "should not be called every frame", and the only way to make
        /// the warning go away was to move correct code.
        ///
        /// The shape is matched narrowly on purpose. The result has to be assigned to a name, the
        /// assignment has to sit in the body of an 'if' that tests that same name for being missing, and
        /// the name must not be a local of the method: a local is empty again on every call, so a
        /// null-checked local caches nothing.
        /// </summary>
        private bool IsLazyCacheFill(InvocationExpressionSyntax invocation, SyntaxNode methodSyntax)
        {
            AssignmentExpressionSyntax assignment = null;

            for (SyntaxNode node = invocation.Parent; node != null && node != methodSyntax; node = node.Parent)
            {
                if (node is AssignmentExpressionSyntax candidate)
                {
                    if (candidate.IsKind(SyntaxKind.SimpleAssignmentExpression) && candidate.Right.Span.Contains(invocation.Span))
                        assignment = candidate;

                    break;
                }

                if (node is StatementSyntax)
                    break;
            }

            string cacheName = GetSimpleName(assignment?.Left);
            if (cacheName == null)
                return false;

            foreach (VariableDeclaratorSyntax local in methodSyntax.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (local.Identifier.Text == cacheName)
                    return false;
            }

            for (SyntaxNode node = assignment.Parent; node != null && node != methodSyntax; node = node.Parent)
            {
                if (node is IfStatementSyntax ifStatement &&
                    ifStatement.Statement.Span.Contains(assignment.Span) &&
                    TestsForMissing(ifStatement.Condition, cacheName))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a condition asks "is this reference empty": 'name == null', '!name', or
        /// '!Utilities.IsValid(name)'.
        /// </summary>
        private bool TestsForMissing(ExpressionSyntax condition, string name)
        {
            foreach (SyntaxNode node in condition.DescendantNodesAndSelf())
            {
                if (node is BinaryExpressionSyntax comparison && comparison.IsKind(SyntaxKind.EqualsExpression))
                {
                    if (GetSimpleName(comparison.Left) == name && comparison.Right.IsKind(SyntaxKind.NullLiteralExpression))
                        return true;

                    if (GetSimpleName(comparison.Right) == name && comparison.Left.IsKind(SyntaxKind.NullLiteralExpression))
                        return true;
                }

                if (node is PrefixUnaryExpressionSyntax negation && negation.IsKind(SyntaxKind.LogicalNotExpression))
                {
                    if (GetSimpleName(negation.Operand) == name)
                        return true;

                    if (negation.Operand is InvocationExpressionSyntax validity &&
                        GetMethodName(validity) == "IsValid" &&
                        validity.ArgumentList.Arguments.Count == 1 &&
                        GetSimpleName(validity.ArgumentList.Arguments[0].Expression) == name)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The name an expression refers to when it is 'name' or 'this.name', otherwise null.
        /// </summary>
        private static string GetSimpleName(ExpressionSyntax expression)
        {
            switch (expression)
            {
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.Text;

                case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression is ThisExpressionSyntax:
                    return memberAccess.Name.Identifier.Text;

                default:
                    return null;
            }
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
