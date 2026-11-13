using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using UdonSharp.Compiler;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Guardrail: no method in the runtime libraries may be able to re-enter itself unless it is marked
    /// <c>[RecursiveMethod]</c>.
    ///
    /// Udon has no call frames. UdonSharp gives every method that is not <c>[RecursiveMethod]</c> exactly
    /// one set of heap slots for its parameters and locals, so a second activation of the same method
    /// overwrites the first one's arguments. Nothing reports this: it compiles, it runs, and in C# -- where
    /// every editor-side test of these libraries runs -- each call has its own frame and the code is
    /// simply correct.
    ///
    /// Two shipped instances of exactly this prompted the test. <c>CEDictionary.Add</c> called
    /// <c>Resize</c>, which re-inserted every entry by calling <c>Add</c>: the entry that triggered the
    /// growth was lost and an existing one was inserted twice, on every resize. And the stock
    /// <c>Dictionary</c>/<c>HashSet</c> rebuilt themselves from serialized data by calling <c>Add</c> from
    /// inside <c>Add</c>. Both were invisible to every C# test that existed, so the check has to be on the
    /// call graph rather than on behaviour.
    ///
    /// The graph is built from the semantic model, not from names, so an overload calling its sibling
    /// (<c>ToJson()</c> calling <c>ToJson(bool)</c>) is not mistaken for recursion. It is deliberately
    /// conservative about what it follows -- see <see cref="ReentrancyAnalysis"/> -- because a guardrail
    /// that fails on correct code gets deleted.
    /// </summary>
    public class CERuntimeReentrancyGuardrailTests
    {
        private const string RuntimeLibraryRoot = "Packages/com.merlin.UdonSharp/Runtime/Libraries";

        /// <summary>
        /// Cycles that have been read and judged safe, keyed by the exact text the failure message prints
        /// for them. Every entry needs a reason next to it. Empty is the expected state.
        /// </summary>
        private static readonly string[] ReviewedCycles = Array.Empty<string>();

        [Test]
        public void NoRuntimeLibraryMethod_CanReenterItself_WithoutRecursiveMethod()
        {
            string root = Path.GetFullPath(RuntimeLibraryRoot);

            Assert.That(Directory.Exists(root), Is.True,
                $"Runtime library folder not found at '{root}'. If the package moved, this guardrail is " +
                "checking nothing and must be pointed at the new location.");

            string[] files = Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories);

            Assert.That(files.Length, Is.GreaterThan(50),
                "Suspiciously few runtime library sources were found; the guardrail would pass vacuously.");

            var trees = new List<SyntaxTree>(files.Length);

            foreach (string file in files)
                trees.Add(ReentrancyAnalysis.Parse(File.ReadAllText(file), file));

            List<string> cycles = ReentrancyAnalysis.FindUnmarkedCycles(trees)
                .Where(cycle => !ReviewedCycles.Contains(cycle))
                .ToList();

            Assert.That(cycles, Is.Empty,
                "These runtime library methods can re-enter themselves but are not marked [RecursiveMethod]. " +
                "Under Udon the inner activation overwrites the outer one's parameters and locals, so the " +
                "outer call continues with the wrong values -- silently, and only in the Udon VM. Break the " +
                "cycle (usually by having the helper do the work inline instead of calling back into its " +
                "caller), or mark every method in it [RecursiveMethod]:\n\n" + string.Join("\n\n", cycles));
        }

        [Test]
        public void Analysis_ReportsTheShapeThatShipped()
        {
            // The exact shape of the CEDictionary defect: a public method whose helper calls back into it.
            const string source = @"
public class Table
{
    private int[] _slots = new int[4];
    private int _count;

    public void Add(int value)
    {
        if (_count == _slots.Length) Grow();
        _slots[_count++] = value;
    }

    private void Grow()
    {
        int[] old = _slots;
        _slots = new int[old.Length * 2];
        _count = 0;
        for (int i = 0; i < old.Length; i++) Add(old[i]);
    }
}";

            List<string> cycles = ReentrancyAnalysis.FindUnmarkedCycles(new[] { ReentrancyAnalysis.Parse(source, "Table.cs") });

            Assert.That(cycles, Has.Count.EqualTo(1), "Add -> Grow -> Add must be reported as one cycle.");
            Assert.That(cycles[0], Does.Contain("Table.Add(int)").And.Contain("Table.Grow()"));
        }

        [Test]
        public void Analysis_BaseCall_IsNotVirtualDispatchBackToTheOverride()
        {
            const string source = "class Parent { public virtual void Run(){} } class Child : Parent { public override void Run(){base.Run();} }";
            Assert.That(ReentrancyAnalysis.FindUnmarkedCycles(new[] { ReentrancyAnalysis.Parse(source, "BaseCall.cs") }), Is.Empty);
        }

        [Test]
        public void Analysis_AcceptsWhatIsActuallySafe()
        {
            const string source = @"
public class RecursiveMethodAttribute : System.Attribute { }
public class UdonSharpBehaviour { }

public class Tree
{
    public Tree Left, Right;

    // Marked: UdonSharp saves and restores this method's slots around the recursive call.
    [RecursiveMethod]
    public int Count() { return 1 + (Left != null ? Left.Count() : 0) + (Right != null ? Right.Count() : 0); }

    // An overload calling its sibling is two different methods.
    public string Describe() { return Describe(false); }
    public string Describe(bool verbose) { return verbose ? ""tree (verbose)"" : ""tree""; }
}

public class Relay : UdonSharpBehaviour
{
    public Relay Next;

    // A call on ANOTHER behaviour instance runs in that instance's own program and heap, so it cannot
    // overwrite this instance's slots.
    public void Forward() { if (Next != null) Next.Forward(); }
}";

            List<string> cycles = ReentrancyAnalysis.FindUnmarkedCycles(new[] { ReentrancyAnalysis.Parse(source, "Safe.cs") });

            Assert.That(cycles, Is.Empty,
                "A marked recursive method, sibling overloads, and a call onto a different behaviour " +
                "instance are all safe and must not be reported:\n" + string.Join("\n", cycles));
        }

        [Test]
        public void Analysis_ReportsSelfCallsOnABehaviourAndRecursionThroughPlainClasses()
        {
            const string source = @"
public class UdonSharpBehaviour { }

public class Countdown : UdonSharpBehaviour
{
    // Same instance: this really is the method re-entering itself.
    public void Tick(int remaining) { if (remaining > 0) Tick(remaining - 1); }
}

public class Node
{
    public Node Child;

    // A plain class is compiled into the calling behaviour's program, where every instance shares the
    // method's slots -- so recursing onto a different Node clobbers just the same.
    public int Depth() { return Child == null ? 1 : 1 + Child.Depth(); }
}";

            List<string> cycles = ReentrancyAnalysis.FindUnmarkedCycles(new[] { ReentrancyAnalysis.Parse(source, "Unsafe.cs") });

            Assert.That(cycles, Has.Count.EqualTo(2));
            Assert.That(string.Join("\n", cycles), Does.Contain("Countdown.Tick(int)").And.Contain("Node.Depth()"));
        }

        /// <summary>
        /// Call-graph construction and cycle search, separated from the test so the fixtures above can
        /// prove it detects what it claims to.
        ///
        /// What counts as an edge:
        ///  - invocations, constructor calls, property and indexer accessors, user-defined operators and
        ///    conversions, and the enumerator members a <c>foreach</c> calls;
        ///  - for a virtual, abstract or interface member, every override or implementation declared in
        ///    the analysed sources.
        ///
        /// What deliberately does not:
        ///  - an instance member of an UdonSharpBehaviour reached through some other receiver. That runs
        ///    in the other behaviour's own program, with its own heap, and cannot touch this one's slots;
        ///  - anything that only exists in metadata. It has no slots of ours to overwrite.
        /// </summary>
        internal static class ReentrancyAnalysis
        {
            public static SyntaxTree Parse(string source, string path)
            {
                // COMPILER_UDONSHARP selects the branches the Udon compiler actually sees.
                CSharpParseOptions options = CSharpParseOptions.Default
                    .WithDocumentationMode(DocumentationMode.None)
                    .WithPreprocessorSymbols("COMPILER_UDONSHARP", "UDONSHARP", "UNITY_2022_3_OR_NEWER", "VRC_SDK_VRCSDK3", "UDON")
                    .WithLanguageVersion(LanguageVersion.CSharp9);

                return CSharpSyntaxTree.ParseText(source, options, path);
            }

            public static List<string> FindUnmarkedCycles(IReadOnlyList<SyntaxTree> trees)
            {
                CSharpCompilation compilation = CSharpCompilation.Create(
                    "CERuntimeReentrancyGuardrail_" + Guid.NewGuid().ToString("N"),
                    trees,
                    CompilationContext.GetMetadataReferences(),
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                var comparer = SymbolEqualityComparer.Default;
                var bodies = new Dictionary<IMethodSymbol, List<SyntaxNode>>(comparer);
                var models = new Dictionary<SyntaxTree, SemanticModel>();

                foreach (SyntaxTree tree in trees)
                {
                    SemanticModel model = compilation.GetSemanticModel(tree, ignoreAccessibility: true);
                    models[tree] = model;
                    CollectBodies(tree.GetRoot(), model, bodies);
                }

                // Overrides and interface implementations declared in source, keyed by the member a call
                // site would name.
                var implementations = new Dictionary<IMethodSymbol, List<IMethodSymbol>>(comparer);

                foreach (IMethodSymbol method in bodies.Keys)
                {
                    for (IMethodSymbol overridden = method.OverriddenMethod; overridden != null; overridden = overridden.OverriddenMethod)
                        AddImplementation(implementations, overridden.OriginalDefinition, method);

                    INamedTypeSymbol containing = method.ContainingType;
                    if (containing == null)
                        continue;

                    foreach (INamedTypeSymbol contract in containing.AllInterfaces)
                    {
                        foreach (IMethodSymbol member in contract.GetMembers().OfType<IMethodSymbol>())
                        {
                            ISymbol implementation = containing.FindImplementationForInterfaceMember(member);

                            if (implementation != null && comparer.Equals(implementation.OriginalDefinition, method))
                                AddImplementation(implementations, member.OriginalDefinition, method);
                        }
                    }
                }

                var edges = new Dictionary<IMethodSymbol, HashSet<IMethodSymbol>>(comparer);

                foreach (KeyValuePair<IMethodSymbol, List<SyntaxNode>> entry in bodies)
                {
                    var callees = new HashSet<IMethodSymbol>(comparer);
                    var directCallees = new HashSet<IMethodSymbol>(comparer);

                    foreach (SyntaxNode body in entry.Value)
                        CollectCallees(body, models[body.SyntaxTree], callees, directCallees);

                    var resolved = new HashSet<IMethodSymbol>(comparer);

                    foreach (IMethodSymbol callee in callees)
                    {
                        if (bodies.ContainsKey(callee))
                            resolved.Add(callee);

                        if (implementations.TryGetValue(callee, out List<IMethodSymbol> targets))
                        {
                            foreach (IMethodSymbol target in targets)
                                resolved.Add(target);
                        }
                    }

                    resolved.UnionWith(directCallees.Where(bodies.ContainsKey));
                    edges[entry.Key] = resolved;
                }

                var reports = new List<string>();

                foreach (List<IMethodSymbol> component in StronglyConnectedComponents(bodies.Keys, edges, comparer))
                {
                    bool isCycle = component.Count > 1 || edges[component[0]].Contains(component[0]);

                    if (!isCycle)
                        continue;

                    List<IMethodSymbol> unmarked = component.Where(method => !IsMarkedRecursive(method)).ToList();

                    if (unmarked.Count == 0)
                        continue;

                    reports.Add(Describe(component, unmarked));
                }

                reports.Sort(StringComparer.Ordinal);
                return reports;
            }

            private static void AddImplementation(Dictionary<IMethodSymbol, List<IMethodSymbol>> map, IMethodSymbol contract, IMethodSymbol implementation)
            {
                if (!map.TryGetValue(contract, out List<IMethodSymbol> list))
                    map[contract] = list = new List<IMethodSymbol>();

                list.Add(implementation);
            }

            /// <summary>Every declaration that becomes a method in the emitted program, with its body.</summary>
            private static void CollectBodies(SyntaxNode root, SemanticModel model, Dictionary<IMethodSymbol, List<SyntaxNode>> bodies)
            {
                foreach (SyntaxNode node in root.DescendantNodes())
                {
                    IMethodSymbol symbol = null;
                    SyntaxNode body = null;

                    switch (node)
                    {
                        case BaseMethodDeclarationSyntax method:
                            symbol = model.GetDeclaredSymbol(method);
                            body = (SyntaxNode)method.Body ?? method.ExpressionBody;
                            break;

                        case AccessorDeclarationSyntax accessor:
                            symbol = model.GetDeclaredSymbol(accessor);
                            body = (SyntaxNode)accessor.Body ?? accessor.ExpressionBody;
                            break;

                        case PropertyDeclarationSyntax property when property.ExpressionBody != null:
                            symbol = model.GetDeclaredSymbol(property)?.GetMethod;
                            body = property.ExpressionBody;
                            break;

                        case IndexerDeclarationSyntax indexer when indexer.ExpressionBody != null:
                            symbol = model.GetDeclaredSymbol(indexer)?.GetMethod;
                            body = indexer.ExpressionBody;
                            break;
                    }

                    if (symbol == null || body == null)
                        continue;

                    symbol = symbol.OriginalDefinition;

                    if (!bodies.TryGetValue(symbol, out List<SyntaxNode> list))
                        bodies[symbol] = list = new List<SyntaxNode>();

                    // A partial method contributes one body; anything else declares exactly one.
                    list.Add(body);
                }
            }

            private static void CollectCallees(SyntaxNode body, SemanticModel model, HashSet<IMethodSymbol> callees, HashSet<IMethodSymbol> directCallees)
            {
                foreach (SyntaxNode node in body.DescendantNodesAndSelf())
                {
                    switch (node)
                    {
                        case InvocationExpressionSyntax invocation:
                            AddCallee(model.GetSymbolInfo(invocation).Symbol as IMethodSymbol, ReceiverOf(invocation.Expression), ReceiverOf(invocation.Expression) is BaseExpressionSyntax ? directCallees : callees);
                            break;

                        case BaseObjectCreationExpressionSyntax creation:
                            AddCallee(model.GetSymbolInfo(creation).Symbol as IMethodSymbol, null, callees);
                            break;

                        case ForEachStatementSyntax forEach:
                            ForEachStatementInfo info = model.GetForEachStatementInfo(forEach);
                            AddCallee(info.GetEnumeratorMethod, forEach.Expression, callees);
                            AddCallee(info.MoveNextMethod, forEach.Expression, callees);
                            AddCallee(info.CurrentProperty?.GetMethod, forEach.Expression, callees);
                            break;

                        case BinaryExpressionSyntax _:
                        case PrefixUnaryExpressionSyntax _:
                        case PostfixUnaryExpressionSyntax _:
                        case CastExpressionSyntax _:
                            // Only user-defined operators and conversions bind to a method here.
                            AddCallee(model.GetSymbolInfo(node).Symbol as IMethodSymbol, null, callees);
                            break;

                        case ElementAccessExpressionSyntax elementAccess:
                            AddAccessors(model.GetSymbolInfo(elementAccess).Symbol as IPropertySymbol, elementAccess, elementAccess.Expression, callees);
                            break;

                        case MemberAccessExpressionSyntax memberAccess:
                            AddAccessors(model.GetSymbolInfo(memberAccess).Symbol as IPropertySymbol, memberAccess, memberAccess.Expression, memberAccess.Expression is BaseExpressionSyntax ? directCallees : callees);
                            break;

                        case IdentifierNameSyntax identifier when !(identifier.Parent is MemberAccessExpressionSyntax parent && parent.Name == identifier):
                            AddAccessors(model.GetSymbolInfo(identifier).Symbol as IPropertySymbol, identifier, null, callees);
                            break;
                    }
                }
            }

            /// <summary>The receiver expression of a call, or null when it is an implicit <c>this</c>.</summary>
            private static ExpressionSyntax ReceiverOf(ExpressionSyntax callTarget)
            {
                return callTarget is MemberAccessExpressionSyntax memberAccess ? memberAccess.Expression : null;
            }

            private static void AddAccessors(IPropertySymbol property, ExpressionSyntax usage, ExpressionSyntax receiver, HashSet<IMethodSymbol> callees)
            {
                if (property == null)
                    return;

                bool written = false;
                bool read = true;

                if (usage.Parent is AssignmentExpressionSyntax assignment && assignment.Left == usage)
                {
                    written = true;
                    read = !assignment.IsKind(SyntaxKind.SimpleAssignmentExpression);
                }
                else if (usage.Parent is PrefixUnaryExpressionSyntax prefix &&
                         (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                {
                    written = true;
                }
                else if (usage.Parent is PostfixUnaryExpressionSyntax postfix &&
                         (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                {
                    written = true;
                }

                if (read)
                    AddCallee(property.GetMethod, receiver, callees);

                if (written)
                    AddCallee(property.SetMethod, receiver, callees);
            }

            private static void AddCallee(IMethodSymbol callee, ExpressionSyntax receiver, HashSet<IMethodSymbol> callees)
            {
                if (callee == null)
                    return;

                callee = (callee.ReducedFrom ?? callee).OriginalDefinition;

                // A different behaviour instance runs in its own program; only a call that stays on this
                // instance can come back around to the same slots.
                bool staysOnThisInstance = receiver == null || receiver is ThisExpressionSyntax || receiver is BaseExpressionSyntax;

                if (!callee.IsStatic && !staysOnThisInstance && IsBehaviour(callee.ContainingType))
                    return;

                callees.Add(callee);
            }

            private static bool IsBehaviour(INamedTypeSymbol type)
            {
                for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
                {
                    if (current.Name == "UdonSharpBehaviour")
                        return true;
                }

                return false;
            }

            private static bool IsMarkedRecursive(IMethodSymbol method)
            {
                if (HasRecursiveAttribute(method))
                    return true;

                // An accessor carries no attribute list of its own that UdonSharp reads; accept one on the
                // property so a recursive property is expressible at all.
                return method.AssociatedSymbol != null && HasRecursiveAttribute(method.AssociatedSymbol);
            }

            private static bool HasRecursiveAttribute(ISymbol symbol)
            {
                foreach (AttributeData attribute in symbol.GetAttributes())
                {
                    string name = attribute.AttributeClass?.Name;

                    if (name == "RecursiveMethodAttribute" || name == "RecursiveMethod")
                        return true;
                }

                return false;
            }

            private static string Describe(List<IMethodSymbol> component, List<IMethodSymbol> unmarked)
            {
                var format = new SymbolDisplayFormat(
                    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypes,
                    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                    memberOptions: SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
                    parameterOptions: SymbolDisplayParameterOptions.IncludeType,
                    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

                var builder = new StringBuilder();

                builder.Append("cycle: ");
                builder.Append(string.Join(" <-> ", component
                    .Select(method => method.ToDisplayString(format))
                    .OrderBy(name => name, StringComparer.Ordinal)));

                foreach (IMethodSymbol method in unmarked.OrderBy(m => m.ToDisplayString(format), StringComparer.Ordinal))
                {
                    Location location = method.Locations.FirstOrDefault(l => l.IsInSource);
                    FileLinePositionSpan span = location != null ? location.GetLineSpan() : default;

                    builder.Append("\n  not [RecursiveMethod]: ");
                    builder.Append(method.ToDisplayString(format));

                    if (location != null)
                    {
                        builder.Append("  (");
                        builder.Append(Path.GetFileName(span.Path));
                        builder.Append(':');
                        builder.Append(span.StartLinePosition.Line + 1);
                        builder.Append(')');
                    }
                }

                return builder.ToString();
            }

            /// <summary>Tarjan's algorithm, iterative so a deep call chain cannot overflow the test's own stack.</summary>
            private static List<List<IMethodSymbol>> StronglyConnectedComponents(
                IEnumerable<IMethodSymbol> nodes,
                Dictionary<IMethodSymbol, HashSet<IMethodSymbol>> edges,
                SymbolEqualityComparer comparer)
            {
                var index = new Dictionary<IMethodSymbol, int>(comparer);
                var lowLink = new Dictionary<IMethodSymbol, int>(comparer);
                var onStack = new HashSet<IMethodSymbol>(comparer);
                var stack = new Stack<IMethodSymbol>();
                var components = new List<List<IMethodSymbol>>();
                int nextIndex = 0;

                foreach (IMethodSymbol start in nodes)
                {
                    if (index.ContainsKey(start))
                        continue;

                    var work = new Stack<(IMethodSymbol Node, IEnumerator<IMethodSymbol> Successors)>();

                    index[start] = lowLink[start] = nextIndex++;
                    stack.Push(start);
                    onStack.Add(start);
                    work.Push((start, edges[start].GetEnumerator()));

                    while (work.Count > 0)
                    {
                        (IMethodSymbol node, IEnumerator<IMethodSymbol> successors) = work.Peek();

                        if (successors.MoveNext())
                        {
                            IMethodSymbol next = successors.Current;

                            if (!index.ContainsKey(next))
                            {
                                index[next] = lowLink[next] = nextIndex++;
                                stack.Push(next);
                                onStack.Add(next);
                                work.Push((next, edges[next].GetEnumerator()));
                            }
                            else if (onStack.Contains(next))
                            {
                                lowLink[node] = Math.Min(lowLink[node], index[next]);
                            }

                            continue;
                        }

                        work.Pop();

                        if (work.Count > 0)
                        {
                            IMethodSymbol parent = work.Peek().Node;
                            lowLink[parent] = Math.Min(lowLink[parent], lowLink[node]);
                        }

                        if (lowLink[node] != index[node])
                            continue;

                        var component = new List<IMethodSymbol>();
                        IMethodSymbol member;

                        do
                        {
                            member = stack.Pop();
                            onStack.Remove(member);
                            component.Add(member);
                        } while (!comparer.Equals(member, node));

                        components.Add(component);
                    }
                }

                return components;
            }
        }
    }
}
