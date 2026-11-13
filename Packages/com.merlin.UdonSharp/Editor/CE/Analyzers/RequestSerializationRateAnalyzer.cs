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
    /// (two values compared with each other, a dirty flag, or an edge-triggered input read). Either way
    /// it has to be a real guard standing between the top of the per-frame path and the call. Merely
    /// reading a clock somewhere in the body proves nothing: '_pos += _speed * Time.deltaTime;' touches
    /// Time.deltaTime and gates nothing at all, and treating that as a rate limit suppressed this check
    /// on the exact ungated Update it exists to catch.
    ///
    /// Where the gate stands is not allowed to matter, because authors do not agree on it.
    /// 'if (p != _lastSent) { _lastSent = p; Publish(); }' puts the gate in Update and the call in
    /// Publish(); 'if (!_dirty) return; RequestSerialization();' with '_dirty = false' in
    /// OnPostSerialization puts the test in Update and the clear in the serialization callback. Both
    /// serialize once per change, and both were reported as "runs every frame" while the check only
    /// looked for a gate, and for the flag's assignment, inside the one method holding the call.
    ///
    /// What a condition compares does matter. 'if (_target == null) return;' and
    /// 'if (Networking.GetOwner(gameObject) != Networking.LocalPlayer) return;' are comparisons, and
    /// neither bounds anything: they hold or fail for seconds on end, and the call behind them still runs
    /// on every frame they let through. Reading any '==' as a change gate made the ownership-then-serialize
    /// Update -- the worked example below with one extra line -- invisible.
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

        /// <summary>
        /// The callbacks VRChat runs around a serialization. A flag cleared in one of them is cleared
        /// once per send, which is what makes testing it a gate.
        /// </summary>
        private static readonly HashSet<string> SerializationCallbackNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "OnPreSerialization",
            "OnPostSerialization"
        };

        /// <summary>
        /// UnityEngine.Input reads that are true for the single frame a key or button changes state.
        /// </summary>
        private static readonly HashSet<string> InputEdgeMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "GetKeyDown",
            "GetKeyUp",
            "GetButtonDown",
            "GetButtonUp",
            "GetMouseButtonDown",
            "GetMouseButtonUp"
        };

        private const string UnityInputTypeName = "UnityEngine.Input";

        /// <summary>
        /// What deciding "is this condition a change gate" needs to know about the behaviour around it.
        /// </summary>
        private sealed class GateContext
        {
            /// <summary>
            /// Names assigned somewhere the flag could be cleared: on this per-frame path, or in a
            /// serialization callback. Names whose value answers "do I own this object" are left out.
            /// </summary>
            public HashSet<string> DirtyFlagNames;

            public HashSet<string> OwnershipDerivedNames;
            public CompilationContext CompilationContext;
        }

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

            // Both of these are only read once a per-frame method turns up, and most behaviours have none,
            // so they are built on first use rather than for every behaviour in the project.
            HashSet<string> ownershipDerivedNames = null;
            HashSet<string> serializationCallbackAssignments = null;

            foreach (MethodDeclarationSyntax updateMethod in graph.Methods)
            {
                if (!CENetworkAnalysis.UpdateMethodNames.Contains(updateMethod.Identifier.Text))
                    continue;

                // Anything reachable from the update method runs at frame rate too, so a helper that calls
                // RequestSerialization() is the same bug one level down. Ordered rather than taken straight
                // from the reachability set so the reported call site is the same on every compile.
                List<MethodDeclarationSyntax> perFrameMethods = OrderPerFramePath(updateMethod, graph);

                if (ownershipDerivedNames == null)
                {
                    ownershipDerivedNames = CENetworkAnalysis.CollectOwnershipDerivedNames(type, compilationContext);
                    serializationCallbackAssignments = GetSerializationCallbackAssignments(graph);
                }

                var gates = new GateContext
                {
                    DirtyFlagNames = GetDirtyFlagNames(perFrameMethods, serializationCallbackAssignments, ownershipDerivedNames),
                    OwnershipDerivedNames = ownershipDerivedNames,
                    CompilationContext = compilationContext
                };

                AnalyzerDiagnostic diagnostic = FindUngatedCall(updateMethod, perFrameMethods, gates);

                if (diagnostic != null)
                    diagnostics.Add(diagnostic);
            }

            return diagnostics;
        }

        /// <summary>
        /// Every name assigned in OnPreSerialization / OnPostSerialization or in a helper one of them
        /// calls.
        /// </summary>
        private static HashSet<string> GetSerializationCallbackAssignments(CENetworkAnalysis.BehaviourMethodGraph graph)
        {
            var assignedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (MethodDeclarationSyntax method in graph.Methods)
            {
                if (!SerializationCallbackNames.Contains(method.Identifier.Text))
                    continue;

                foreach (MethodDeclarationSyntax reached in graph.Callees(method))
                    assignedNames.UnionWith(GetAssignedNames(reached));
            }

            return assignedNames;
        }

        /// <summary>
        /// The names a condition on this per-frame path may test and be believed as a dirty flag.
        ///
        /// A dirty flag is recognised by where it is cleared, and the old rule only looked in the method
        /// holding the call. That is one of three places authors put it: the caller that tested the flag
        /// ('if (_dirty) { _dirty = false; Sync(); }'), the helper that sends ('void Flush() { _dirty =
        /// false; RequestSerialization(); }'), or OnPostSerialization, which is the only place that can
        /// tell whether the send succeeded. A flag assigned nowhere on those paths is deliberately not
        /// accepted: '_initialized', set once in Start, guards most Updates and bounds none of them.
        ///
        /// Ownership-derived names are removed for the same reason an ownership comparison is not a gate.
        /// '_isOwner = Networking.IsOwner(gameObject); if (!_isOwner) return;' assigns the flag on the
        /// per-frame path and is still true on every frame the owner runs.
        /// </summary>
        private static HashSet<string> GetDirtyFlagNames(
            List<MethodDeclarationSyntax> perFrameMethods,
            HashSet<string> serializationCallbackAssignments,
            HashSet<string> ownershipDerivedNames)
        {
            var dirtyFlagNames = new HashSet<string>(serializationCallbackAssignments, StringComparer.Ordinal);

            foreach (MethodDeclarationSyntax method in perFrameMethods)
            {
                if (method != null)
                    dirtyFlagNames.UnionWith(GetAssignedNames(method));
            }

            dirtyFlagNames.ExceptWith(ownershipDerivedNames);
            return dirtyFlagNames;
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
            GateContext gates)
        {
            CompilationContext compilationContext = gates.CompilationContext;

            foreach (MethodDeclarationSyntax method in perFrameMethods)
            {
                if (method == null)
                    continue;

                SemanticModel model = CENetworkAnalysis.GetSemanticModel(compilationContext, method);

                foreach (InvocationExpressionSyntax invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!CENetworkAnalysis.IsRequestSerializationCall(invocation, model))
                        continue;

                    if (IsChangeGated(invocation, method, gates))
                        continue;

                    if (IsTimeGated(invocation, method, model, perFrameMethods, compilationContext))
                        continue;

                    if (IsChangeGatedByEveryCaller(method, updateMethod, perFrameMethods, gates, new HashSet<MethodDeclarationSyntax>()))
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
        private static bool IsChangeGated(InvocationExpressionSyntax invocation, MethodDeclarationSyntax method, GateContext gates)
        {
            SemanticModel model = CENetworkAnalysis.GetSemanticModel(gates.CompilationContext, method);

            return HasDominatingGate(invocation, method, condition => IsChangeCondition(condition, gates, model));
        }

        /// <summary>
        /// Whether every call that brings the per-frame path into <paramref name="method"/> is itself
        /// behind a change gate.
        ///
        /// This is the change-based half of what <see cref="IsTimeGated"/> has always allowed for clocks:
        /// the gate decides whether the helper is entered at all, so it stands in the caller and the
        /// helper holding RequestSerialization() has no guard of its own. Unlike the clock rule it is
        /// asked of each call site rather than of the caller as a whole, because a change test somewhere
        /// in a caller says nothing about a call that is not inside it -- and every call site has to pass,
        /// since one ungated way in is a per-frame serialization no matter how the others are written.
        ///
        /// The update method has no call sites to ask. VRChat calls it every frame, which is the fact the
        /// whole check starts from.
        /// </summary>
        private static bool IsChangeGatedByEveryCaller(
            MethodDeclarationSyntax method,
            MethodDeclarationSyntax updateMethod,
            List<MethodDeclarationSyntax> perFrameMethods,
            GateContext gates,
            HashSet<MethodDeclarationSyntax> visiting)
        {
            if (ReferenceEquals(method, updateMethod))
                return false;

            // A method already being asked about is on a recursive path, and a recursive call is not a
            // new way in. Treating it as ungated keeps a cycle from vouching for itself.
            if (!visiting.Add(method))
                return false;

            string methodName = method.Identifier.Text;
            bool hasCallSite = false;
            bool everyCallSiteGated = true;

            foreach (MethodDeclarationSyntax caller in perFrameMethods)
            {
                if (caller == null || ReferenceEquals(caller, method))
                    continue;

                foreach (InvocationExpressionSyntax callSite in caller.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!CENetworkAnalysis.IsSelfInvocation(callSite))
                        continue;

                    if (!string.Equals(CENetworkAnalysis.GetInvokedName(callSite), methodName, StringComparison.Ordinal))
                        continue;

                    hasCallSite = true;

                    if (IsChangeGated(callSite, caller, gates))
                        continue;

                    if (IsChangeGatedByEveryCaller(caller, updateMethod, perFrameMethods, gates, visiting))
                        continue;

                    everyCallSiteGated = false;
                    break;
                }

                if (!everyCallSiteGated)
                    break;
            }

            visiting.Remove(method);

            return hasCallSite && everyCallSiteGated;
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
        /// Whether a condition compares two values with each other ('_score != _lastSentScore'), reads a
        /// dirty flag, or reads an input edge -- the three shapes that bound the call to once per change.
        /// </summary>
        private static bool IsChangeCondition(ExpressionSyntax condition, GateContext gates, SemanticModel model)
        {
            if (condition == null)
                return false;

            foreach (SyntaxNode conditionNode in condition.DescendantNodesAndSelf())
            {
                if (conditionNode is BinaryExpressionSyntax comparison &&
                    IsValueComparison(comparison, gates, model))
                {
                    return true;
                }

                if (conditionNode is IdentifierNameSyntax identifier &&
                    gates.DirtyFlagNames.Contains(identifier.Identifier.Text) &&
                    IsBooleanFlag(identifier, model))
                {
                    return true;
                }

                if (conditionNode is InvocationExpressionSyntax invocation && IsInputEdgeRead(invocation, model))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a comparison is between two things that can each change, which is what makes it a
        /// change or threshold test.
        ///
        /// Two kinds of comparison are refused. One side being null, true, false or default is a state
        /// test: 'if (_target == null) return;' is true or false for as long as the reference stays that
        /// way, and every frame it lets through still serializes. An ownership comparison --
        /// 'Networking.GetOwner(gameObject) != Networking.LocalPlayer', or a cached owner flag in the
        /// same position -- is the guard this check's own worked example starts with, spelled with '=='
        /// instead of a call. Counting either as a gate hid the ungated Update behind the first line any
        /// careful author writes in one.
        /// </summary>
        private static bool IsValueComparison(BinaryExpressionSyntax comparison, GateContext gates, SemanticModel model)
        {
            bool isComparison = false;

            foreach (SyntaxKind comparisonKind in ComparisonKinds)
            {
                if (comparison.IsKind(comparisonKind))
                {
                    isComparison = true;
                    break;
                }
            }

            if (!isComparison)
                return false;

            if (IsFixedLiteral(comparison.Left) || IsFixedLiteral(comparison.Right))
                return false;

            return !CENetworkAnalysis.ContainsOwnershipGuard(comparison, model, gates.OwnershipDerivedNames);
        }

        /// <summary>
        /// Whether a name tested in a condition is a bool, which is what a dirty flag is.
        ///
        /// The flag rule accepts any name assigned on the per-frame path, and without this it took
        /// 'if (_target == null) { _target = FindTarget(); return; }' for a latch because '_target' is
        /// assigned right there -- putting back, through the side door, the null test the comparison rule
        /// refuses. A reference that has been found stays found, so everything after that line runs on
        /// every frame. With no model, or a name that does not bind, the name is all there is and it is
        /// accepted, which only ever suppresses a report.
        /// </summary>
        private static bool IsBooleanFlag(IdentifierNameSyntax identifier, SemanticModel model)
        {
            ISymbol symbol = model?.GetSymbolInfo(identifier).Symbol;

            ITypeSymbol flagType;

            switch (symbol)
            {
                case null:
                    return true;

                case IFieldSymbol field:
                    flagType = field.Type;
                    break;

                case ILocalSymbol local:
                    flagType = local.Type;
                    break;

                case IPropertySymbol property:
                    flagType = property.Type;
                    break;

                default:
                    return false;
            }

            return flagType != null && flagType.SpecialType == SpecialType.System_Boolean;
        }

        private static bool IsFixedLiteral(ExpressionSyntax expression)
        {
            while (expression is ParenthesizedExpressionSyntax parenthesized)
                expression = parenthesized.Expression;

            return expression != null &&
                   (expression.IsKind(SyntaxKind.NullLiteralExpression) ||
                    expression.IsKind(SyntaxKind.TrueLiteralExpression) ||
                    expression.IsKind(SyntaxKind.FalseLiteralExpression) ||
                    expression.IsKind(SyntaxKind.DefaultLiteralExpression) ||
                    expression.IsKind(SyntaxKind.DefaultExpression));
        }

        /// <summary>
        /// Whether the call is Input.GetKeyDown / GetButtonUp / GetMouseButtonDown or one of their
        /// siblings, which answer true for one frame per press or release.
        ///
        /// 'if (Input.GetKeyDown(KeyCode.R)) { ResetScores(); RequestSerialization(); }' contains no
        /// comparison and no flag, so it read as an ungated per-frame call and was told it "runs every
        /// frame" -- about a call that runs once per key press.
        /// </summary>
        private static bool IsInputEdgeRead(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            string invokedName = CENetworkAnalysis.GetInvokedName(invocation);

            if (invokedName == null || !InputEdgeMethodNames.Contains(invokedName))
                return false;

            ISymbol symbol = model?.GetSymbolInfo(invocation).Symbol;

            // With no model, or a call that would not bind, the name is all there is, and reading it as
            // an edge only ever suppresses a report.
            if (symbol == null)
                return true;

            return string.Equals(symbol.ContainingType?.ToDisplayString(), UnityInputTypeName, StringComparison.Ordinal);
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
