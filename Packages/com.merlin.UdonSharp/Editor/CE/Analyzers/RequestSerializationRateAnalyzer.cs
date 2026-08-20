using System;
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
    /// Detects RequestSerialization() called from a per-frame event without a rate gate.
    ///
    /// RequestSerialization() serializes the whole behaviour, not the field that changed. VRChat gives a
    /// player roughly 11 KB/s in total, so a single behaviour re-sending its payload every frame can eat
    /// the budget on its own -- and the symptom is other people's objects going laggy, which nobody traces
    /// back to this call.
    ///
    /// A gate counts as either time-based (comparing against Time.time / server time) or change-based
    /// (an equality check, or a dirty flag the guarded block clears). Either way it has to be a real
    /// guard standing between the top of the method and the call. Merely reading a clock somewhere in
    /// the body proves nothing: '_pos += _speed * Time.deltaTime;' touches Time.deltaTime and gates
    /// nothing at all, and treating that as a rate limit suppressed this check on the exact ungated
    /// Update it exists to catch.
    /// </summary>
    /// <example>
    /// // Warning - serializes 90+ times a second
    /// private void Update()
    /// {
    ///     _position = transform.position;
    ///     RequestSerialization();  // CE0017
    /// }
    ///
    /// // Good - rate gated
    /// private void Update()
    /// {
    ///     if (Time.time - _lastSerializeTime &lt; SerializeInterval) return;
    ///     _lastSerializeTime = Time.time;
    ///     RequestSerialization();
    /// }
    /// </example>
    internal class RequestSerializationRateAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0017";
        public string AnalyzerName => "Ungated RequestSerialization";
        public string Description => "Detects RequestSerialization() called from an update loop with no rate gate.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// Comparison operators that make an enclosing 'if' a change or threshold check rather than a
        /// per-frame condition.
        /// </summary>
        private static readonly SyntaxKind[] ComparisonKinds =
        {
            SyntaxKind.EqualsExpression,
            SyntaxKind.NotEqualsExpression,
            SyntaxKind.LessThanExpression,
            SyntaxKind.LessThanOrEqualExpression,
            SyntaxKind.GreaterThanExpression,
            SyntaxKind.GreaterThanOrEqualExpression
        };

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            if (type == null || compilationContext == null)
                return diagnostics;

            CENetworkAnalysis.BehaviourMethodGraph graph = CENetworkAnalysis.BuildMethodGraph(type);
            if (graph.Methods.Count == 0)
                return diagnostics;

            foreach (MethodDeclarationSyntax updateMethod in graph.Methods)
            {
                if (!CENetworkAnalysis.UpdateMethodNames.Contains(updateMethod.Identifier.Text))
                    continue;

                // Anything reachable from the update method runs at frame rate too, so a helper that calls
                // RequestSerialization() is the same bug one level down. Ordered rather than taken straight
                // from the reachability set so the reported call site is the same on every compile.
                List<MethodDeclarationSyntax> perFrameMethods = OrderPerFramePath(updateMethod, graph);

                AnalyzerDiagnostic diagnostic = FindUngatedCall(updateMethod, perFrameMethods, compilationContext);

                if (diagnostic != null)
                    diagnostics.Add(diagnostic);
            }

            return diagnostics;
        }

        /// <summary>
        /// The per-frame call path, update method first and the rest in source order.
        /// </summary>
        private static List<MethodDeclarationSyntax> OrderPerFramePath(
            MethodDeclarationSyntax updateMethod,
            CENetworkAnalysis.BehaviourMethodGraph graph)
        {
            HashSet<MethodDeclarationSyntax> reachable = graph.Callees(updateMethod);

            var ordered = new List<MethodDeclarationSyntax> { updateMethod };

            foreach (MethodDeclarationSyntax method in graph.Methods)
            {
                if (!ReferenceEquals(method, updateMethod) && reachable.Contains(method))
                    ordered.Add(method);
            }

            return ordered;
        }

        /// <summary>
        /// Returns a diagnostic for the first ungated RequestSerialization() on the per-frame path, or null.
        /// One report per update method: the fix is a single gate, not one per call site.
        /// </summary>
        private AnalyzerDiagnostic FindUngatedCall(
            MethodDeclarationSyntax updateMethod,
            List<MethodDeclarationSyntax> perFrameMethods,
            CompilationContext compilationContext)
        {
            foreach (MethodDeclarationSyntax method in perFrameMethods)
            {
                if (method == null)
                    continue;

                SemanticModel model = CENetworkAnalysis.GetSemanticModel(compilationContext, method);

                foreach (InvocationExpressionSyntax invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!CENetworkAnalysis.IsRequestSerializationCall(invocation, model))
                        continue;

                    if (IsChangeGated(invocation, method))
                        continue;

                    if (IsTimeGated(invocation, method, model, perFrameMethods, compilationContext))
                        continue;

                    string updateName = updateMethod.Identifier.Text;
                    string callSite = method.Identifier.Text;

                    string where = string.Equals(callSite, updateName, StringComparison.Ordinal)
                        ? $"'{updateName}'"
                        : $"'{callSite}', which '{updateName}' calls";

                    return AnalyzerDiagnostic.Warning(
                        invocation.GetLocation(),
                        $"RequestSerialization() runs every frame from {where} with no rate gate. " +
                        "Each call re-serializes the entire behaviour, and VRChat budgets roughly 11 KB/s per player, " +
                        "so one ungated per-frame serialization can saturate the instance on its own.",
                        AnalyzerId,
                        AnalyzerId,
                        $"Gate the call in '{callSite}' on elapsed time " +
                        "('if (Time.time - _lastSerializeTime < SerializeInterval) return;' followed by updating _lastSerializeTime), " +
                        "or on an actual change ('if (_value != _lastSentValue)' or a dirty flag the block clears).");
                }
            }

            return null;
        }

        /// <summary>
        /// Whether a guard matching <paramref name="isGate"/> actually stands between the top of the
        /// method and this call: an 'if' that encloses it, or an 'if' that jumps out of the method earlier
        /// in the same block. Both spellings count because writing 'if (!_dirty) return;' is at least as
        /// common as wrapping the call, and handling only one of them would flag correct code.
        ///
        /// A matching condition anywhere else in the body is not a gate and must not be read as one: the
        /// call still runs every frame no matter how that condition evaluates.
        /// </summary>
        private static bool HasDominatingGate(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax method,
            Func<ExpressionSyntax, bool> isGate)
        {
            for (SyntaxNode node = invocation.Parent; node != null && node != method; node = node.Parent)
            {
                if (node is IfStatementSyntax ifStatement && isGate(ifStatement.Condition))
                    return true;
            }

            return HasPrecedingReturnGuard(invocation, method, isGate);
        }

        /// <summary>
        /// Whether a guard makes the call fire on change rather than on every frame.
        /// </summary>
        private static bool IsChangeGated(InvocationExpressionSyntax invocation, MethodDeclarationSyntax method)
        {
            HashSet<string> assignedNames = GetAssignedNames(method);

            return HasDominatingGate(invocation, method, condition => IsChangeCondition(condition, assignedNames));
        }

        /// <summary>
        /// Whether a clock gates this call.
        ///
        /// Inside the method holding the call the guard has to dominate it, which is what separates a real
        /// rate limit from an Update that merely happens to multiply by Time.deltaTime. In any other method
        /// on the per-frame path the guard is what decides whether the path is entered at all -- the
        /// 'if (Time.time - _last &lt; Interval) return; Sync();' shape, where the call lives in Sync() with
        /// no guard of its own -- so an 'if' anywhere in that method counts.
        /// </summary>
        private static bool IsTimeGated(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax method,
            SemanticModel model,
            List<MethodDeclarationSyntax> perFrameMethods,
            CompilationContext compilationContext)
        {
            HashSet<string> timeDerivedNames = GetTimeDerivedNames(method, model);

            if (HasDominatingGate(invocation, method, condition => IsTimeCondition(condition, timeDerivedNames, model)))
                return true;

            foreach (MethodDeclarationSyntax other in perFrameMethods)
            {
                if (other == null || ReferenceEquals(other, method))
                    continue;

                SemanticModel otherModel = CENetworkAnalysis.GetSemanticModel(compilationContext, other);
                HashSet<string> otherTimeDerivedNames = GetTimeDerivedNames(other, otherModel);

                foreach (IfStatementSyntax ifStatement in other.DescendantNodes().OfType<IfStatementSyntax>())
                {
                    if (IsTimeCondition(ifStatement.Condition, otherTimeDerivedNames, otherModel))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a condition either compares values ('_score != _lastSentScore') or reads a flag this
        /// method also writes, which is the dirty-flag latch that bounds the call to once per change.
        /// </summary>
        private static bool IsChangeCondition(ExpressionSyntax condition, HashSet<string> assignedNames)
        {
            if (condition == null)
                return false;

            foreach (SyntaxNode conditionNode in condition.DescendantNodesAndSelf())
            {
                foreach (SyntaxKind comparisonKind in ComparisonKinds)
                {
                    if (conditionNode.IsKind(comparisonKind))
                        return true;
                }

                if (conditionNode is IdentifierNameSyntax identifier &&
                    assignedNames.Contains(identifier.Identifier.Text))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Whether a condition tests a clock, either directly ('Time.time - _last &gt; Interval') or through
        /// a value the method derived from one, which is the accumulator shape
        /// ('_timer += Time.deltaTime; if (_timer &gt; Interval)').
        /// </summary>
        private static bool IsTimeCondition(
            ExpressionSyntax condition,
            HashSet<string> timeDerivedNames,
            SemanticModel model)
        {
            if (condition == null)
                return false;

            if (CENetworkAnalysis.ContainsTimeMember(condition, model))
                return true;

            foreach (IdentifierNameSyntax identifier in condition.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
            {
                if (timeDerivedNames.Contains(identifier.Identifier.Text))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Names this method assigns from an expression that reads a clock. An accumulator gate never
        /// mentions Time in its own condition, so without these the '_timer += Time.deltaTime' shape would
        /// be reported despite being rate limited.
        /// </summary>
        private static HashSet<string> GetTimeDerivedNames(MethodDeclarationSyntax method, SemanticModel model)
        {
            var timeDerivedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (AssignmentExpressionSyntax assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                if (!CENetworkAnalysis.ContainsTimeMember(assignment.Right, model))
                    continue;

                string assignedName = GetAssignedName(assignment.Left);

                if (assignedName != null)
                    timeDerivedNames.Add(assignedName);
            }

            // 'float elapsed = Time.time - _last;' is the same gate written as a declaration.
            foreach (VariableDeclaratorSyntax declarator in method.DescendantNodes().OfType<VariableDeclaratorSyntax>())
            {
                if (declarator.Initializer != null &&
                    CENetworkAnalysis.ContainsTimeMember(declarator.Initializer.Value, model))
                {
                    timeDerivedNames.Add(declarator.Identifier.Text);
                }
            }

            return timeDerivedNames;
        }

        /// <summary>
        /// Whether a guard that jumps out of the method already ran before this call in the same block.
        /// </summary>
        private static bool HasPrecedingReturnGuard(
            InvocationExpressionSyntax invocation,
            MethodDeclarationSyntax method,
            Func<ExpressionSyntax, bool> isGate)
        {
            for (SyntaxNode node = invocation; node != null && node != method; node = node.Parent)
            {
                if (!(node.Parent is BlockSyntax block))
                    continue;

                foreach (StatementSyntax statement in block.Statements)
                {
                    if (ReferenceEquals(statement, node))
                        break;

                    if (statement is IfStatementSyntax ifStatement &&
                        ContainsJump(ifStatement.Statement) &&
                        isGate(ifStatement.Condition))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsJump(StatementSyntax statement)
        {
            if (statement == null)
                return false;

            foreach (SyntaxNode node in statement.DescendantNodesAndSelf())
            {
                if (node is ReturnStatementSyntax || node is ContinueStatementSyntax)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Every name this method assigns to, used to recognise a flag the method itself clears.
        /// </summary>
        private static HashSet<string> GetAssignedNames(MethodDeclarationSyntax method)
        {
            var assignedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (AssignmentExpressionSyntax assignment in method.DescendantNodes().OfType<AssignmentExpressionSyntax>())
            {
                string assignedName = GetAssignedName(assignment.Left);

                if (assignedName != null)
                    assignedNames.Add(assignedName);
            }

            return assignedNames;
        }

        private static string GetAssignedName(ExpressionSyntax target)
        {
            switch (target)
            {
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.Text;

                case MemberAccessExpressionSyntax memberAccess:
                    return memberAccess.Name.Identifier.Text;

                default:
                    return null;
            }
        }
    }
}
