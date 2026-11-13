using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UdonSharp.CE.Editor.Optimizers;
using UdonSharp.Compiler;

namespace UdonSharp.CE.Editor.Async
{
    /// <summary>
    /// Lowers <c>async UdonTask</c> methods into event-driven state machines.
    ///
    /// The lowered shape is deliberately fire-and-forget. Udon has no way to park a call frame, so the
    /// public entry point starts the machine and returns immediately; the only thing that can observe the
    /// machine finishing is a continuation event name threaded through <c>__Name_Start</c>, which this
    /// generator wires up for <c>await</c> sites it lowers itself. Nothing else can await a
    /// <see cref="UdonSharp.CE.Async.UdonTask"/> and see completion, so any <c>await</c> this generator
    /// does not consume is a hard error rather than a silently-immediate resume.
    ///
    /// This is a code generator, not an optimization, so a method it owns has exactly two outcomes:
    /// correct code, or a CE002x error at the author's own source line. A shape it cannot lower is refused
    /// while it still has the author's syntax in hand -- never emitted wrong, and never left to fail as a
    /// C# error inside generated members the author has no file for. Code it does not own, such as an
    /// ordinary <c>async Task</c> method on a class that is not an UdonSharpBehaviour, is left exactly as
    /// written.
    /// </summary>
    internal class AsyncMethodTransformOptimizer : ICompileTimeOptimizer
    {
        private const string OptimizerIdValue = "CEASYNC001";

        /// <summary>Author-facing diagnostic codes. Kept distinct so a fix guide can address each case.</summary>
        private const string DiagUdonTaskOfT = "CE0022";
        private const string DiagAsyncWithoutAwait = "CE0023";
        private const string DiagUnloweredAwait = "CE0024";
        private const string DiagUnsupportedPattern = "CE0025";
        private const string DiagUninferrableLocal = "CE0026";

        private const string UdonTaskTypeName = "UdonTask";
        private const string UdonTaskNamespace = "UdonSharp.CE.Async";
        private const string BehaviourTypeName = "UdonSharpBehaviour";
        private const string BehaviourNamespace = "UdonSharp";

        public string OptimizerId => OptimizerIdValue;
        public string OptimizerName => "Async Method Transformer";
        public string Description => "Transforms async UdonTask methods into state machines using SendCustomEventDelayed*.";
        public bool IsEnabledByDefault => true;
        public int Priority => 1;

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            SyntaxNode root = tree.GetRoot();

            // Every source file of Assembly-CSharp and of each U# assembly comes through here, and almost
            // none of them hold anything this generator acts on. Leaving before a semantic model is asked
            // for means a file with no async code in it can never be the reason this pass fails a compile.
            if (!ContainsAsyncCode(root))
                return tree;

            SemanticModel semanticModel = context.GetSemanticModel(tree);
            if (semanticModel == null)
            {
                throw new CodeGenerationException(
                    OptimizerIdValue,
                    root.GetLocation(),
                    "CE could not build a semantic model for this file, so async lowering cannot resolve " +
                    "local variables or infer 'var' types. Lowering without it silently hoists the wrong " +
                    "identifiers, so the compile is aborted instead.");
            }

            var rewriter = new AsyncMethodRewriter(context, semanticModel);
            SyntaxNode newRoot = rewriter.Visit(root);

            // Every await inside a method the rewriter lowered has been consumed or refused by now, so
            // anything still standing is in code the state machine never touched -- an expression-bodied
            // member, a lambda, a method that is not async UdonTask. Left alone, an await CE owns reaches
            // Roslyn, which builds a real compiler-generated state machine class that Udon cannot run, and
            // the failure surfaces nowhere near the await.
            RejectUnloweredAwaits(root, semanticModel, rewriter.LoweredMethods);
            RejectAwaitsInGeneratedMembers(rewriter.GeneratedMembers);

            // Deliberately no NormalizeWhitespace(). It reformatted the whole file, which moved every
            // untouched line and pointed every later diagnostic and every debug-info line-map entry at the
            // wrong source line.
            return rewriter.ChangesMade
                ? tree.WithRootAndOptions(newRoot, tree.Options)
                : tree;
        }

        private static bool ContainsAsyncCode(SyntaxNode root)
        {
            foreach (SyntaxNode node in root.DescendantNodes())
            {
                if (node is AwaitExpressionSyntax)
                    return true;

                if (node is MethodDeclarationSyntax method && method.Modifiers.Any(SyntaxKind.AsyncKeyword))
                    return true;
            }

            return false;
        }

        private static bool ContainsAwait(SyntaxNode node)
        {
            return node.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();
        }

        /// <summary>
        /// Rejects awaits CE owns that no state machine consumed. This used to reject every await in every
        /// file it was handed, and it is handed all of Assembly-CSharp: one ordinary
        /// <c>async Task</c> helper on a plain class failed the U# compile of the whole project with an
        /// error about UdonTask. An await is CE's only when it awaits a UdonTask, sits in a method that
        /// returns one, or sits in an UdonSharpBehaviour -- the three places where Udon would have to run it.
        /// </summary>
        private static void RejectUnloweredAwaits(
            SyntaxNode root,
            SemanticModel semanticModel,
            HashSet<MethodDeclarationSyntax> loweredMethods)
        {
            foreach (AwaitExpressionSyntax awaitExpression in root.DescendantNodes().OfType<AwaitExpressionSyntax>())
            {
                if (awaitExpression.Ancestors().OfType<MethodDeclarationSyntax>().Any(loweredMethods.Contains))
                    continue;

                if (!IsOwnedByCE(awaitExpression, semanticModel))
                    continue;

                throw new CodeGenerationException(
                    DiagUnloweredAwait,
                    awaitExpression.GetLocation(),
                    "CE could not lower this 'await'. UdonTask is fire-and-forget: it carries no continuation, " +
                    "so code CE did not lower cannot await one and observe it completing. CE lowers only " +
                    "'async UdonTask' methods with a braced body, and only the await forms documented in " +
                    "ce-async.md. Fix: move this await into an 'async UdonTask' method with a braced body, or " +
                    "call the async method without awaiting it.");
            }
        }

        private static bool IsOwnedByCE(AwaitExpressionSyntax awaitExpression, SemanticModel semanticModel)
        {
            if (IsUdonTaskType(semanticModel.GetTypeInfo(awaitExpression.Expression).Type))
                return true;

            ISymbol enclosing = semanticModel.GetEnclosingSymbol(awaitExpression.SpanStart);

            // A lambda or local function reports itself here, so walk out to the method that holds it.
            for (ISymbol current = enclosing; current is IMethodSymbol method; current = current.ContainingSymbol)
            {
                if (IsUdonTaskType(method.ReturnType))
                    return true;
            }

            INamedTypeSymbol host = enclosing as INamedTypeSymbol ?? enclosing?.ContainingType;
            return ClassifyHost(host) == HostKind.Behaviour;
        }

        /// <summary>
        /// Last line of defence for the rule that no await reaches Roslyn inside generated code. The
        /// generator refuses every unlowered await at its source location before it emits anything, so
        /// this should be unreachable; it exists so a future emit path that forgets to cannot ship a
        /// MoveNext with a live await in it.
        /// </summary>
        private static void RejectAwaitsInGeneratedMembers(List<KeyValuePair<MemberDeclarationSyntax, Location>> generatedMembers)
        {
            foreach (KeyValuePair<MemberDeclarationSyntax, Location> generated in generatedMembers)
            {
                if (!ContainsAwait(generated.Key))
                    continue;

                throw new CodeGenerationException(
                    DiagUnloweredAwait,
                    generated.Value,
                    "CE generated a state machine member for this method that still contains an 'await'. " +
                    "That is a CE bug rather than something wrong in this method; the await it failed to " +
                    "lower is one that is not a statement of its own in the method body or a while loop body.");
            }
        }

        private enum HostKind
        {
            Behaviour,
            NotBehaviour,
            Unknown
        }

        /// <summary>
        /// Whether a type is an UdonSharpBehaviour. Unknown means a base type did not resolve, which only
        /// happens when the compilation was not given every source file; in that case nothing is rejected
        /// on the strength of a guess.
        /// </summary>
        private static HostKind ClassifyHost(INamedTypeSymbol type)
        {
            if (type == null)
                return HostKind.Unknown;

            for (INamedTypeSymbol current = type; current != null; current = current.BaseType)
            {
                bool unresolved = current.TypeKind == TypeKind.Error;

                if (string.Equals(current.Name, BehaviourTypeName, StringComparison.Ordinal) &&
                    (unresolved || IsInNamespace(current, BehaviourNamespace)))
                {
                    return HostKind.Behaviour;
                }

                if (unresolved)
                    return HostKind.Unknown;
            }

            return HostKind.NotBehaviour;
        }

        /// <summary>
        /// Recognises <c>UdonTask</c> and <c>UdonTask&lt;T&gt;</c> by symbol, so
        /// <c>global::UdonSharp.CE.Async.UdonTask</c>, a bare <c>UdonTask</c> and a
        /// <c>using Task = UdonSharp.CE.Async.UdonTask;</c> alias all classify the same, and a user type
        /// that merely shares the name in another namespace does not classify at all.
        /// </summary>
        private static bool IsUdonTaskType(ITypeSymbol type)
        {
            var named = type as INamedTypeSymbol;
            if (named == null || !string.Equals(named.Name, UdonTaskTypeName, StringComparison.Ordinal))
                return false;

            // An unresolved 'UdonTask' still counts. It can only be unresolved when the compilation was not
            // handed CE's runtime sources (a tool or test lowering one tree on its own); the real compile
            // always resolves it, and refusing to lower there would hand Roslyn a live async method.
            return named.TypeKind == TypeKind.Error || IsInNamespace(named, UdonTaskNamespace);
        }

        private static bool IsGenericUdonTask(ITypeSymbol type)
        {
            var named = type as INamedTypeSymbol;
            return named != null && named.Arity > 0;
        }

        private static bool IsInNamespace(ISymbol symbol, string namespaceName)
        {
            return symbol.ContainingNamespace != null &&
                   string.Equals(symbol.ContainingNamespace.ToDisplayString(), namespaceName, StringComparison.Ordinal);
        }

        /// <summary>
        /// The written form of the return type. Only consulted when the declaration has no symbol to ask,
        /// and to notice a generic argument list the symbol could not bind.
        /// </summary>
        private static bool TryGetUdonTaskReturn(TypeSyntax type, out bool isGeneric)
        {
            isGeneric = false;
            if (type == null)
                return false;

            TypeSyntax core = type;
            while (true)
            {
                if (core is QualifiedNameSyntax qualified)
                {
                    core = qualified.Right;
                    continue;
                }

                if (core is AliasQualifiedNameSyntax aliasQualified)
                {
                    core = aliasQualified.Name;
                    continue;
                }

                break;
            }

            if (core is GenericNameSyntax generic)
            {
                isGeneric = true;
                return string.Equals(generic.Identifier.ValueText, UdonTaskTypeName, StringComparison.Ordinal);
            }

            return core is IdentifierNameSyntax identifier &&
                   string.Equals(identifier.Identifier.ValueText, UdonTaskTypeName, StringComparison.Ordinal);
        }

        private static bool ReturnsUdonTask(MethodDeclarationSyntax method, IMethodSymbol symbol, out bool isGeneric)
        {
            bool writtenAsUdonTask = TryGetUdonTaskReturn(method.ReturnType, out isGeneric);

            if (symbol == null)
                return writtenAsUdonTask;

            if (!IsUdonTaskType(symbol.ReturnType))
            {
                isGeneric = false;
                return false;
            }

            isGeneric = isGeneric || IsGenericUdonTask(symbol.ReturnType);
            return true;
        }

        /// <summary>
        /// True for a method this generator turns into a state machine: declared <c>async</c>, returning
        /// plain UdonTask, with a braced body that awaits something. Asked of a symbol rather than of
        /// syntax so the same answer is available for methods declared in another part of a partial class
        /// or on a base class, which is where an awaited child or a clashing overload can live.
        ///
        /// Matching on the return type alone treated a plain <c>UdonTask</c> helper -- one that just
        /// returns <c>UdonTask.CompletedTask</c> -- as a state machine and rewrote it into an entry
        /// point calling generated members built from a body with no awaits. The <c>async</c> keyword
        /// is what actually marks a method as lowerable.
        /// </summary>
        private static bool IsLoweredAsyncMethod(IMethodSymbol method)
        {
            if (method == null)
                return false;

            method = method.OriginalDefinition;

            if (!method.IsAsync || !IsUdonTaskType(method.ReturnType) || IsGenericUdonTask(method.ReturnType))
                return false;

            foreach (SyntaxReference reference in method.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax() is MethodDeclarationSyntax declaration &&
                    declaration.Body != null &&
                    ContainsAwait(declaration))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The members one lowered method owns. Every name is derived from the method name alone.</summary>
        private sealed class MachineNames
        {
            public MachineNames(string methodName)
            {
                State = $"__{methodName}_state";
                Continuation = $"__{methodName}_continuation";
                PendingContinuation = $"__{methodName}_pendingContinuation";
                Running = $"__{methodName}_running";
                Start = StartMethodName(methodName);
                MoveNext = $"__{methodName}_MoveNext";
                Complete = $"__{methodName}_Complete";
            }

            public string State { get; }
            public string Continuation { get; }
            public string PendingContinuation { get; }
            public string Running { get; }
            public string Start { get; }
            public string MoveNext { get; }
            public string Complete { get; }

            public IEnumerable<string> All()
            {
                yield return State;
                yield return Continuation;
                yield return PendingContinuation;
                yield return Running;
                yield return Start;
                yield return MoveNext;
                yield return Complete;
            }

            public static string StartMethodName(string methodName)
            {
                return $"__{methodName}_Start";
            }
        }

        /// <summary>
        /// What is being built for one class declaration: the members waiting to be appended to it, and
        /// every member name that is already taken on it.
        ///
        /// The name set is shared by all of the class's state machines. Each machine used to keep its own,
        /// which only stopped a method colliding with itself: a local named <c>Start</c> took the name of
        /// the generated <c>__X_Start</c> method, and a hoisted field could land on a member the author had
        /// declared, both surfacing as CS0102 inside generated code.
        /// </summary>
        private sealed class ClassScope
        {
            public ClassScope(HashSet<string> memberNames)
            {
                MemberNames = memberNames;
            }

            public HashSet<string> MemberNames { get; }
            public List<MemberDeclarationSyntax> PendingMembers { get; } = new List<MemberDeclarationSyntax>();
        }

        private sealed class AsyncMethodRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly SemanticModel _semanticModel;

            /// <summary>
            /// One scope per class currently being visited. A single shared list cleared on entry to
            /// every class meant a nested class declared after an async method wiped the outer class's
            /// pending state-machine members, leaving the entry method calling a <c>__Name_Start</c> that
            /// was never emitted.
            /// </summary>
            private readonly Stack<ClassScope> _classes = new Stack<ClassScope>();

            public bool ChangesMade { get; private set; }

            /// <summary>The original declarations that were lowered. Their awaits are accounted for.</summary>
            public HashSet<MethodDeclarationSyntax> LoweredMethods { get; } = new HashSet<MethodDeclarationSyntax>();

            /// <summary>Every member this pass generated, with the source location of the method it came from.</summary>
            public List<KeyValuePair<MemberDeclarationSyntax, Location>> GeneratedMembers { get; } =
                new List<KeyValuePair<MemberDeclarationSyntax, Location>>();

            public AsyncMethodRewriter(OptimizationContext context, SemanticModel semanticModel)
            {
                _context = context;
                _semanticModel = semanticModel;
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                _classes.Push(OpenClassScope(node));

                var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);

                ClassScope scope = _classes.Pop();

                if (scope.PendingMembers.Count > 0)
                    visited = visited.AddMembers(scope.PendingMembers.ToArray());

                return visited;
            }

            /// <summary>
            /// Claims the generated member names of every state machine in the class before any of them is
            /// built, so the result does not depend on declaration order and a clash is reported against
            /// the method that causes it instead of as a duplicate definition in generated code.
            /// </summary>
            private ClassScope OpenClassScope(ClassDeclarationSyntax node)
            {
                var memberNames = new HashSet<string>(StringComparer.Ordinal);

                List<MethodDeclarationSyntax> lowerable = node.Members
                    .OfType<MethodDeclarationSyntax>()
                    .Where(IsLowerable)
                    .ToList();

                if (lowerable.Count == 0)
                    return new ClassScope(memberNames);

                INamedTypeSymbol type = _semanticModel.GetDeclaredSymbol(node);
                if (type != null)
                {
                    foreach (string memberName in type.MemberNames)
                        memberNames.Add(memberName);
                }

                var lowerableNames = new HashSet<string>(StringComparer.Ordinal);

                foreach (MethodDeclarationSyntax method in lowerable)
                {
                    string methodName = method.Identifier.ValueText;

                    if (!lowerableNames.Add(methodName))
                        throw OverloadedAsyncMethod(method, node.Identifier.ValueText);

                    foreach (string machineName in new MachineNames(methodName).All())
                    {
                        if (memberNames.Add(machineName))
                            continue;

                        throw new CodeGenerationException(
                            DiagUnsupportedPattern,
                            method.Identifier.GetLocation(),
                            $"Lowering '{methodName}' generates a member named '{machineName}', but " +
                            $"'{node.Identifier.ValueText}' already has a member with that name. Fix: rename " +
                            "the method or that member.");
                    }
                }

                return new ClassScope(memberNames);
            }

            private static CodeGenerationException OverloadedAsyncMethod(MethodDeclarationSyntax method, string className)
            {
                string methodName = method.Identifier.ValueText;

                return new CodeGenerationException(
                    DiagUnsupportedPattern,
                    method.Identifier.GetLocation(),
                    $"'{methodName}' is declared more than once as an async UdonTask method on '{className}'. " +
                    $"CE names the generated members after the method alone (__{methodName}_state, " +
                    $"__{methodName}_MoveNext, ...), so two overloads would share one state machine's fields. " +
                    "Fix: give each overload its own name.");
            }

            private bool IsLowerable(MethodDeclarationSyntax method)
            {
                if (method.Body == null || !method.Modifiers.Any(SyntaxKind.AsyncKeyword))
                    return false;

                return IsLoweredAsyncMethod(_semanticModel.GetDeclaredSymbol(method));
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                if (!node.Modifiers.Any(SyntaxKind.AsyncKeyword))
                    return base.VisitMethodDeclaration(node);

                IMethodSymbol symbol = _semanticModel.GetDeclaredSymbol(node);

                // Not CE's: async Task, async void, or a user type that only shares UdonTask's name. Roslyn
                // compiles those the way it always has.
                if (!ReturnsUdonTask(node, symbol, out bool isGeneric))
                    return base.VisitMethodDeclaration(node);

                ValidateAsyncDeclaration(node, isGeneric);

                // An expression-bodied async method is not lowered; its await is refused by the await scan,
                // whose message carries the 'braced body' fix.
                if (node.Body == null)
                    return base.VisitMethodDeclaration(node);

                ValidateLowerableDeclaration(node, symbol);
                new UdonSharpSyntaxCheck(_semanticModel, node.Identifier.ValueText).Check(node.Body);

                ClassScope scope = _classes.Peek();
                var generator = new MethodGenerator(node, symbol, _semanticModel, scope.MemberNames);

                MethodDeclarationSyntax entryMethod = generator.GenerateEntryMethod();
                List<MemberDeclarationSyntax> supportMembers = generator.GenerateSupportMembers();

                scope.PendingMembers.AddRange(supportMembers);

                Location origin = node.Identifier.GetLocation();
                foreach (MemberDeclarationSyntax member in supportMembers)
                    GeneratedMembers.Add(new KeyValuePair<MemberDeclarationSyntax, Location>(member, origin));

                LoweredMethods.Add(node);

                ChangesMade = true;
                _context.RecordOptimization(
                    OptimizerIdValue,
                    $"Transforming async method '{node.Identifier.ValueText}' with {generator.AwaitCount} await(s)",
                    node.GetLocation(),
                    node.Identifier.ValueText,
                    "");

                return entryMethod;
            }

            /// <summary>
            /// Rejects the async UdonTask declarations CE has nothing to lower for.
            /// </summary>
            private static void ValidateAsyncDeclaration(MethodDeclarationSyntax method, bool isGeneric)
            {
                if (isGeneric)
                {
                    // The generated entry point has no result to hand back: the machine returns to the
                    // caller at the first await, long before the value exists. Lowering it anyway produced
                    // 'return UdonTask.CompletedTask;' against a UdonTask<T> signature -- a CS0029 inside
                    // generated code the author cannot open.
                    throw new CodeGenerationException(
                        DiagUdonTaskOfT,
                        method.ReturnType.GetLocation(),
                        $"'{method.Identifier.ValueText}' is declared 'async UdonTask<T>', which CE cannot " +
                        "lower. The state machine returns to its caller at the first await, so there is no " +
                        "point at which a result can be handed back. Fix: return plain 'UdonTask' and store " +
                        "the result in a field the caller reads once the continuation runs.");
                }

                if (!ContainsAwait(method))
                {
                    // Without an await there is nothing for CE to lower, so the method reaches Roslyn with
                    // its 'async' keyword intact and Roslyn builds a real state machine class for it, which
                    // Udon cannot execute.
                    throw new CodeGenerationException(
                        DiagAsyncWithoutAwait,
                        method.Identifier.GetLocation(),
                        $"'{method.Identifier.ValueText}' is declared async but contains no 'await', so CE " +
                        "has nothing to lower and the C# compiler would build a state machine Udon cannot " +
                        "run. Fix: drop the 'async' keyword and 'return UdonTask.CompletedTask;'.");
                }
            }

            /// <summary>
            /// Rejects the declarations whose state machine could be written but could not work. Each of
            /// these used to be lowered anyway and then failed as a C# or U# error pointing into generated
            /// members: CS0120 for a static method, CS0246 for a generic one, CS0103 for a class with no
            /// SendCustomEvent, CS0102 and CS0111 for two overloads, "U# does not yet support hiding base
            /// fields" for an override of an async base method. A ref or out parameter did not fail at all.
            /// </summary>
            private void ValidateLowerableDeclaration(MethodDeclarationSyntax method, IMethodSymbol symbol)
            {
                string methodName = method.Identifier.ValueText;
                Location location = method.Identifier.GetLocation();

                if (!(method.Parent is ClassDeclarationSyntax) || _classes.Count == 0)
                {
                    // Support members can only be attached to an enclosing class. For a struct nested in a
                    // class they used to be attached to the outer class, which the struct's entry point
                    // cannot reach.
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"'{methodName}' is an async UdonTask method declared outside a class. CE emits the " +
                        "state machine as members of the declaring class, so the method must be declared " +
                        "directly in one -- not in a struct, record or interface.");
                }

                if (method.Modifiers.Any(SyntaxKind.StaticKeyword))
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"'{methodName}' is a static async UdonTask method, which CE cannot lower. The state " +
                        "machine lives in instance fields and resumes itself with SendCustomEvent on its own " +
                        "behaviour, so it needs an instance. Fix: make the method an instance method.");
                }

                if (method.TypeParameterList != null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"'{methodName}' is a generic async UdonTask method, which CE cannot lower. Its " +
                        "parameters and locals become fields of the class, and a field cannot be declared " +
                        "with the method's type parameter. Fix: write the method for the concrete type.");
                }

                foreach (ParameterSyntax parameter in method.ParameterList.Parameters)
                {
                    SyntaxToken byReference = parameter.Modifiers.FirstOrDefault(modifier =>
                        modifier.IsKind(SyntaxKind.RefKeyword) ||
                        modifier.IsKind(SyntaxKind.OutKeyword) ||
                        modifier.IsKind(SyntaxKind.InKeyword));

                    if (byReference.IsKind(SyntaxKind.None))
                        continue;

                    // C# forbids these on an async method (CS1988), but that check goes with the 'async'
                    // keyword CE removes. Lowered anyway, the body wrote to its own field and the caller's
                    // variable silently never changed.
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        parameter.GetLocation(),
                        $"'{methodName}' takes '{parameter.Identifier.ValueText}' as a '{byReference.Text}' " +
                        "parameter, which an async method cannot have. The machine keeps the value in a field " +
                        "of its own, so writes would never reach the caller's variable. Fix: pass it by value " +
                        "and hand results back through a field.");
                }

                INamedTypeSymbol host = symbol?.ContainingType;
                if (host == null)
                    return;

                if (ClassifyHost(host) == HostKind.NotBehaviour)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"'{methodName}' is an async UdonTask method on '{host.Name}', which is not an " +
                        "UdonSharpBehaviour. The state machine resumes itself with SendCustomEventDelayedSeconds " +
                        "and SendCustomEventDelayedFrames, which only a behaviour has. Fix: move the method " +
                        "onto an UdonSharpBehaviour.");
                }

                // Counted on the type rather than on this declaration so that an overload in another part
                // of a partial class is seen too.
                if (host.GetMembers(methodName).OfType<IMethodSymbol>().Count(IsLoweredAsyncMethod) > 1)
                    throw OverloadedAsyncMethod(method, host.Name);

                for (INamedTypeSymbol baseType = host.BaseType; baseType != null; baseType = baseType.BaseType)
                {
                    if (!baseType.GetMembers(methodName).OfType<IMethodSymbol>().Any(IsLoweredAsyncMethod))
                        continue;

                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"'{methodName}' is an async UdonTask method on '{host.Name}' and on its base class " +
                        $"'{baseType.Name}'. Both would generate members named __{methodName}_*, and U# " +
                        "cannot hide a base member. Fix: rename one of them, or keep the async body in one " +
                        "class and have the other call it.");
                }
            }
        }

        private sealed class MethodGenerator
        {
            private readonly MethodDeclarationSyntax _method;
            private readonly IMethodSymbol _methodSymbol;
            private readonly SemanticModel _semanticModel;

            /// <summary>
            /// The options the author's file was parsed with. Generated members are parsed with the same
            /// ones: with the defaults no preprocessor symbol is defined, so a <c>#if UNITY_EDITOR</c>
            /// carried into generated text chose its <c>#else</c> branch and code guarded by
            /// <c>#if !COMPILER_UDONSHARP</c> was compiled into the Udon program.
            /// </summary>
            private readonly CSharpParseOptions _parseOptions;

            /// <summary>
            /// Hoisted storage keyed by the declared symbol, not by name. Name keying gave one field to
            /// every local called <c>t</c> no matter which scope declared it, and substituted in member
            /// position too, so a local <c>x</c> next to a <c>vec.x</c> produced <c>vec.__Name_x</c>.
            /// </summary>
            private readonly Dictionary<ISymbol, string> _fieldsBySymbol =
                new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);

            /// <summary>Identifier occurrences to rewrite, keyed by source span so node identity is irrelevant.</summary>
            private readonly Dictionary<TextSpan, string> _identifierReplacements = new Dictionary<TextSpan, string>();

            /// <summary>Declarators whose storage moved to a field, keyed by the declared name's span.</summary>
            private readonly Dictionary<TextSpan, string> _declaratorFields = new Dictionary<TextSpan, string>();

            /// <summary>
            /// <c>nameof(...)</c> expressions that mention a hoisted variable, keyed by span, with the
            /// string they evaluate to. Rewriting the identifier inside one turned
            /// <c>nameof(duration)</c> into <c>nameof(__Fade_duration)</c>, and the author's log line
            /// printed the generated field name.
            /// </summary>
            private readonly Dictionary<TextSpan, string> _nameofValues = new Dictionary<TextSpan, string>();

            /// <summary>Awaits this generator turned into a suspension. Any other await in the method is refused.</summary>
            private readonly HashSet<AwaitExpressionSyntax> _loweredAwaits = new HashSet<AwaitExpressionSyntax>();

            /// <summary>Member names taken on the declaring class, shared with its other state machines.</summary>
            private readonly HashSet<string> _usedMemberNames;

            private readonly List<HoistedVariable> _hoistedVariables = new List<HoistedVariable>();
            private readonly List<string> _fieldDeclarations = new List<string>();
            private readonly List<string> _switchSections = new List<string>();
            private readonly string _methodName;
            private readonly MachineNames _names;
            private readonly string _continuationParameterName;
            private int _nextState = 1;

            public int AwaitCount { get; }

            public MethodGenerator(
                MethodDeclarationSyntax method,
                IMethodSymbol methodSymbol,
                SemanticModel semanticModel,
                HashSet<string> usedMemberNames)
            {
                _method = method;
                _methodSymbol = methodSymbol;
                _semanticModel = semanticModel;
                _usedMemberNames = usedMemberNames;
                _parseOptions = method.SyntaxTree.Options as CSharpParseOptions ?? CSharpParseOptions.Default;
                _methodName = method.Identifier.ValueText;
                _names = new MachineNames(_methodName);
                AwaitCount = method.DescendantNodes().OfType<AwaitExpressionSyntax>().Count();

                // The class scope has normally claimed these already. Adding them again is what keeps a
                // hoisted parameter named 'state', 'continuation' or 'running', or a local named 'Start',
                // from claiming a member the machine itself owns.
                foreach (string machineName in _names.All())
                    _usedMemberNames.Add(machineName);

                _continuationParameterName = MakeUniqueParameterName("continuation");

                CollectHoistedVariables();
                BuildIdentifierReplacements();
                BuildFieldDeclarations();
            }

            /// <summary>
            /// The entry point keeps the author's declaration and swaps only the body. It used to be
            /// rebuilt from text as modifiers, return type, name and parameter list, which silently
            /// dropped every attribute list: an <c>[Rpc]</c> on an async method vanished before the RPC
            /// pass ran, so the method stayed a local call and the "must return void" error never fired.
            /// </summary>
            public MethodDeclarationSyntax GenerateEntryMethod()
            {
                string arguments = string.Join(", ", _method.ParameterList.Parameters.Select(p => p.Identifier.Text));
                if (!string.IsNullOrEmpty(arguments))
                    arguments += ", ";

                // The returned struct is a snapshot, never a handle: UdonTask has no continuation to
                // register against, so it cannot be updated once the caller holds it. Reporting the state
                // the machine is actually in beats the old unconditional CompletedTask, which told every
                // caller a still-running sequence had already finished.
                string source =
                    "{\n" +
                    $"    {_names.Start}({arguments}\"\");\n" +
                    $"    if ({_names.Running})\n" +
                    "        return global::UdonSharp.CE.Async.UdonTask.Started;\n" +
                    "    return global::UdonSharp.CE.Async.UdonTask.CompletedTask;\n" +
                    "}";

                var body = SyntaxFactory.ParseStatement(source, 0, _parseOptions) as BlockSyntax;
                if (body == null || body.ContainsDiagnostics)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        _method.Identifier.GetLocation(),
                        $"CE generated an entry point for '{_methodName}' that does not parse:\n{source}");
                }

                body = body
                    .WithLeadingTrivia(_method.Body.GetLeadingTrivia())
                    .WithTrailingTrivia(_method.Body.GetTrailingTrivia());

                return WithoutAsyncModifier().WithBody(body);
            }

            private MethodDeclarationSyntax WithoutAsyncModifier()
            {
                SyntaxTokenList modifiers = _method.Modifiers;
                int index = modifiers.IndexOf(SyntaxKind.AsyncKeyword);
                SyntaxToken asyncToken = modifiers[index];
                SyntaxTokenList remaining = modifiers.RemoveAt(index);

                // When 'async' is the first token of the declaration it carries the declaration's leading
                // trivia -- indentation, doc comments, a closing #endif. That belongs to whatever comes next.
                if (asyncToken.HasLeadingTrivia)
                {
                    if (index < remaining.Count)
                    {
                        SyntaxToken next = remaining[index];
                        remaining = remaining.Replace(
                            next,
                            next.WithLeadingTrivia(asyncToken.LeadingTrivia.AddRange(next.LeadingTrivia)));
                    }
                    else
                    {
                        TypeSyntax returnType = _method.ReturnType;
                        return _method
                            .WithModifiers(remaining)
                            .WithReturnType(returnType.WithLeadingTrivia(
                                asyncToken.LeadingTrivia.AddRange(returnType.GetLeadingTrivia())));
                    }
                }

                return _method.WithModifiers(remaining);
            }

            public List<MemberDeclarationSyntax> GenerateSupportMembers()
            {
                string moveNextSource = GenerateMoveNextMethodSource();

                // Checked before anything is parsed, while each offending await can still be reported at
                // the line the author wrote it on.
                RejectAwaitsLeftInPlace();

                var members = new List<MemberDeclarationSyntax>();
                Location location = _method.Identifier.GetLocation();

                foreach (string field in _fieldDeclarations)
                    members.Add(ParseMember(field, location));

                members.Add(ParseMember(GenerateStartMethodSource(), location));
                members.Add(ParseMember(moveNextSource, location));
                members.Add(ParseMember(GenerateCompleteMethodSource(), location));

                return members;
            }

            /// <summary>
            /// Every await that was not lowered as a statement of its own is still sitting in an expression
            /// that was emitted verbatim: a loop or early-exit condition, another await's argument, a
            /// statement after the first await of a loop body. It used to ride into MoveNext and be caught,
            /// or not, by how the text happened to re-parse -- <c>await (X());</c> re-parsed as a call to a
            /// method named await and failed as CS0103 inside generated code.
            /// </summary>
            private void RejectAwaitsLeftInPlace()
            {
                foreach (AwaitExpressionSyntax awaitExpression in _method.DescendantNodes().OfType<AwaitExpressionSyntax>())
                {
                    if (_loweredAwaits.Contains(awaitExpression))
                        continue;

                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        awaitExpression.GetLocation(),
                        $"CE cannot lower this await in '{_methodName}': it is not a statement of its own in " +
                        "a position CE lowers. An await cannot sit inside a condition or inside another " +
                        "await's arguments, and a while loop body can hold only one. Fix: make each await a " +
                        "statement of its own directly in the method body or directly in a while loop body, " +
                        "and move a second await in a loop into an async helper method that the loop awaits.");
                }
            }

            /// <summary>
            /// True for a while loop the emitter splits into states: a statement of the method body that
            /// contains an await.
            /// </summary>
            private bool IsFlattenedWhile(WhileStatementSyntax whileStatement)
            {
                return whileStatement.Parent == _method.Body && ContainsAwait(whileStatement);
            }

            /// <summary>
            /// The statement lists the emitter flattens into <c>case</c> labels, and therefore the only
            /// declarations whose storage has to survive a return from <c>MoveNext</c>. Locals nested inside
            /// a block that is emitted whole keep their own braces and must be left alone -- hoisting those
            /// rewrote their uses to a field while leaving the declaration in place, so the loop counter in
            /// <c>if (c) { int i = 0; while (i &lt; n) ... }</c> read a field nothing ever assigned.
            /// </summary>
            private IEnumerable<StatementSyntax> FlattenedStatements()
            {
                foreach (StatementSyntax statement in _method.Body.Statements)
                {
                    yield return statement;

                    if (statement is WhileStatementSyntax whileStatement && IsFlattenedWhile(whileStatement))
                    {
                        foreach (StatementSyntax bodyStatement in GetBlockStatements(whileStatement.Statement))
                            yield return bodyStatement;
                    }
                }
            }

            private void CollectHoistedVariables()
            {
                foreach (ParameterSyntax parameter in _method.ParameterList.Parameters)
                {
                    ISymbol symbol = _semanticModel.GetDeclaredSymbol(parameter);
                    Hoist(symbol, parameter.Identifier, parameter.Type?.ToString() ?? "object");
                }

                foreach (StatementSyntax statement in FlattenedStatements())
                {
                    if (!(statement is LocalDeclarationStatementSyntax local))
                        continue;

                    // A 'using' declaration hoists like any other local. UdonSharp compiles one as a plain
                    // local and never calls Dispose, so dropping the keyword here loses nothing the
                    // behaviour would otherwise have done. A 'ref' local cannot get this far: its 'ref'
                    // initializer is syntax UdonSharpSyntaxCheck has already refused.
                    bool isConst = local.Modifiers.Any(SyntaxKind.ConstKeyword);

                    foreach (VariableDeclaratorSyntax variable in local.Declaration.Variables)
                    {
                        ISymbol symbol = _semanticModel.GetDeclaredSymbol(variable);
                        Hoist(
                            symbol,
                            variable.Identifier,
                            ResolveHoistedType(local, variable, symbol),
                            isConst ? variable.Initializer?.Value : null);
                    }
                }

                CollectExpressionVariables();
            }

            /// <summary>
            /// Variables declared inside an expression rather than by a declaration statement. The ones
            /// scoped to a statement list CE flattens were never hoisted: they were emitted in place, landed
            /// in the scope of the generated switch, and read as unassigned (CS0165) from the case after the
            /// next await.
            ///
            /// An out variable -- <c>out var n</c> -- is the one form UdonSharp compiles, and it hoists like
            /// any local. Pattern variables and deconstructions are syntax the binder refuses, which
            /// UdonSharpSyntaxCheck has reported before this runs; the guard below keeps any other form
            /// from being emitted as if it had been hoisted.
            /// </summary>
            private void CollectExpressionVariables()
            {
                foreach (SingleVariableDesignationSyntax designation in
                         _method.Body.DescendantNodes().OfType<SingleVariableDesignationSyntax>())
                {
                    if (!LandsInFlattenedScope(designation))
                        continue;

                    if (!(designation.Parent is DeclarationExpressionSyntax declaration))
                    {
                        throw new CodeGenerationException(
                            DiagUnsupportedPattern,
                            designation.Identifier.GetLocation(),
                            $"CE cannot keep '{designation.Identifier.ValueText}' in '{_methodName}' across " +
                            "an await: only a local or an out variable can move into a field. Fix: declare an " +
                            "ordinary local and assign it.");
                    }

                    ISymbol symbol = _semanticModel.GetDeclaredSymbol(designation);
                    Hoist(symbol, designation.Identifier, ResolveDesignationType(declaration, designation, symbol));
                }
            }

            /// <summary>
            /// True when a variable declared inside an expression is scoped to a statement list CE flattens:
            /// the method body, or the condition or body of an awaiting while loop. Anything declared inside
            /// a statement that is emitted whole -- a nested block, a for or foreach, a loop CE does not
            /// split, a lambda -- keeps the scope C# gave it and needs nothing.
            /// </summary>
            private bool LandsInFlattenedScope(SyntaxNode node)
            {
                SyntaxNode child = node;

                for (SyntaxNode parent = node.Parent; parent != null; child = parent, parent = parent.Parent)
                {
                    switch (parent)
                    {
                        case BlockSyntax block:
                            return block == _method.Body ||
                                   (block.Parent is WhileStatementSyntax blockOwner && IsFlattenedWhile(blockOwner));

                        case WhileStatementSyntax whileStatement:
                            return IsFlattenedWhile(whileStatement);

                        case IfStatementSyntax ifStatement:
                            // A variable declared in an if condition belongs to the enclosing scope; one
                            // declared in an unbraced branch is scoped to that branch.
                            if (child != ifStatement.Condition)
                                return false;
                            break;

                        case SwitchStatementSyntax switchStatement:
                            if (child != switchStatement.Expression)
                                return false;
                            break;

                        case ExpressionStatementSyntax _:
                        case LocalDeclarationStatementSyntax _:
                        case ReturnStatementSyntax _:
                        case ThrowStatementSyntax _:
                        case LabeledStatementSyntax _:
                            break;

                        case StatementSyntax _:
                        case ElseClauseSyntax _:
                        case SwitchSectionSyntax _:
                        case SwitchExpressionArmSyntax _:
                        case AnonymousFunctionExpressionSyntax _:
                        case CatchClauseSyntax _:
                            return false;
                    }
                }

                return false;
            }

            private string ResolveDesignationType(
                DeclarationExpressionSyntax declaration,
                SingleVariableDesignationSyntax designation,
                ISymbol symbol)
            {
                if (!declaration.Type.IsVar)
                    return declaration.Type.ToString();

                return InferHoistedType(symbol, designation.Identifier);
            }

            /// <summary>
            /// 'var' locals used to be skipped by the hoister but still emitted into a <c>case N:</c> label.
            /// Every case shares one switch scope, so a second 'var' of the same name was a CS0128 the
            /// author could not see, and any value it held was lost the moment the machine returned.
            /// </summary>
            private string ResolveHoistedType(
                LocalDeclarationStatementSyntax local,
                VariableDeclaratorSyntax variable,
                ISymbol symbol)
            {
                string writtenType = local.Declaration.Type.ToString();
                if (!string.Equals(writtenType, "var", StringComparison.Ordinal))
                    return writtenType;

                return InferHoistedType(symbol, variable.Identifier);
            }

            private string InferHoistedType(ISymbol symbol, SyntaxToken identifier)
            {
                ITypeSymbol inferred = (symbol as ILocalSymbol)?.Type;
                if (inferred == null ||
                    inferred.TypeKind == TypeKind.Error ||
                    inferred.TypeKind == TypeKind.Pointer ||
                    inferred.IsAnonymousType)
                {
                    throw new CodeGenerationException(
                        DiagUninferrableLocal,
                        identifier.GetLocation(),
                        $"CE cannot infer the type of 'var {identifier.ValueText}' in " +
                        $"'{_methodName}'. Lowering moves this local into a field, which needs a written " +
                        "type. Fix: replace 'var' with the explicit type.");
                }

                return inferred.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }

            private void Hoist(ISymbol symbol, SyntaxToken identifier, string typeName, ExpressionSyntax constantValue = null)
            {
                if (symbol == null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        identifier.GetLocation(),
                        $"CE could not resolve '{identifier.ValueText}' in '{_methodName}' to a declaration, " +
                        "so it cannot tell which uses of that name belong to it. Lowering is aborted rather " +
                        "than guessing by name.");
                }

                if (_fieldsBySymbol.ContainsKey(symbol))
                    return;

                string fieldName = MakeUniqueFieldName(identifier.ValueText);
                _fieldsBySymbol[symbol] = fieldName;
                _declaratorFields[identifier.Span] = fieldName;
                _hoistedVariables.Add(new HoistedVariable(fieldName, typeName, constantValue));
            }

            private void BuildIdentifierReplacements()
            {
                foreach (IdentifierNameSyntax identifier in _method.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (IsMemberName(identifier))
                        continue;

                    ISymbol symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
                    if (symbol == null)
                        continue;

                    if (!_fieldsBySymbol.TryGetValue(symbol, out string fieldName))
                        continue;

                    if (TryGetEnclosingNameof(identifier, out InvocationExpressionSyntax nameofExpression, out string value))
                    {
                        _nameofValues[nameofExpression.Span] = value;
                        continue;
                    }

                    _identifierReplacements[identifier.Span] = fieldName;
                }
            }

            private bool TryGetEnclosingNameof(
                IdentifierNameSyntax identifier,
                out InvocationExpressionSyntax nameofExpression,
                out string value)
            {
                foreach (SyntaxNode ancestor in identifier.Ancestors())
                {
                    if (ancestor == _method)
                        break;

                    if (!(ancestor is InvocationExpressionSyntax invocation) ||
                        !(invocation.Expression is IdentifierNameSyntax callee) ||
                        !string.Equals(callee.Identifier.ValueText, "nameof", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // A constant value is what tells the nameof operator apart from a method named nameof.
                    Optional<object> constant = _semanticModel.GetConstantValue(invocation);
                    if (constant.HasValue && constant.Value is string text)
                    {
                        nameofExpression = invocation;
                        value = text;
                        return true;
                    }
                }

                nameofExpression = null;
                value = null;
                return false;
            }

            /// <summary>
            /// True when the identifier names a member of something else rather than a variable in scope --
            /// the <c>x</c> of <c>vec.x</c>, of <c>Vector3.x</c>, or of a named argument.
            /// </summary>
            private static bool IsMemberName(IdentifierNameSyntax identifier)
            {
                switch (identifier.Parent)
                {
                    case MemberAccessExpressionSyntax memberAccess:
                        return memberAccess.Name == identifier;
                    case MemberBindingExpressionSyntax memberBinding:
                        return memberBinding.Name == identifier;
                    case QualifiedNameSyntax qualified:
                        return qualified.Right == identifier;
                    case NameColonSyntax _:
                    case NameEqualsSyntax _:
                        return true;
                    default:
                        return false;
                }
            }

            private void BuildFieldDeclarations()
            {
                _fieldDeclarations.Add($"private int {_names.State};");
                _fieldDeclarations.Add($"private string {_names.Continuation};");
                _fieldDeclarations.Add($"private string {_names.PendingContinuation};");
                _fieldDeclarations.Add($"private bool {_names.Running};");

                foreach (var variable in _hoistedVariables)
                {
                    // A const local stays a constant. As a plain field it could no longer stand in a case
                    // label or another constant's initializer, and the author would get CS0150 from a line
                    // of generated code.
                    _fieldDeclarations.Add(variable.ConstantValue != null
                        ? $"private const {variable.TypeName} {variable.FieldName} = {ReplaceIdentifiersText(variable.ConstantValue)};"
                        : $"private {variable.TypeName} {variable.FieldName};");
                }
            }

            /// <summary>
            /// Start reports whether its caller has to carry on by itself: true when the call was refused
            /// or the whole body ran without suspending, false when the machine is parked on an await and
            /// will raise the continuation when it finishes.
            ///
            /// The continuation is deliberately not installed until the first run of MoveNext has returned.
            /// A machine that finished inside that run used to raise it with SendCustomEvent while Start
            /// was still on the stack, so a parent awaiting a child in a loop re-entered itself through
            /// one nested RunProgram per iteration whenever the child had nothing to wait for. Udon halts
            /// a behaviour at a RunProgram depth of 1000, so a thousand items with nothing to do took the
            /// behaviour down for the session.
            ///
            /// Until then it waits in a field, not in the parameter. U# keeps a method's parameters in one
            /// heap slot per method rather than per call, so a refused re-entrant call to this Start made
            /// during that first run overwrites the parameter, and reading it afterwards installed the
            /// refused caller's continuation: the real caller was never resumed and stayed running.
            /// </summary>
            private string GenerateStartMethodSource()
            {
                var sb = new StringBuilder();

                sb.AppendLine($"private bool {_names.Start}({StartParameterList()})");
                sb.AppendLine("{");

                // Every local of this method lives in one set of instance fields, so a second call landing
                // while the first is still suspended would overwrite the first's state and then run both
                // machines against the mixture. There is no per-call storage to fall back on, so the second
                // call is refused loudly instead of corrupting the first. Its caller is still told to carry
                // on, so an awaiting caller does not wait on a sequence that will never start.
                sb.AppendLine($"    if ({_names.Running})");
                sb.AppendLine("    {");
                sb.AppendLine(
                    $"        global::UnityEngine.Debug.LogError(\"[CE.Async] '{_methodName}' was started " +
                    "again while the previous call was still running on this behaviour. CE stores that " +
                    "method's locals in shared fields, so the second call would corrupt the first; it was " +
                    "skipped and its caller resumed immediately. Guard the call, or move the work onto a " +
                    "second behaviour.\");");
                sb.AppendLine("        return true;");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    {_names.Running} = true;");
                sb.AppendLine($"    {_names.State} = 0;");
                sb.AppendLine($"    {_names.Continuation} = \"\";");
                sb.AppendLine($"    {_names.PendingContinuation} = {_continuationParameterName};");

                foreach (var parameter in _method.ParameterList.Parameters)
                {
                    if (_declaratorFields.TryGetValue(parameter.Identifier.Span, out string fieldName))
                        sb.AppendLine($"    {fieldName} = {parameter.Identifier.Text};");
                }

                sb.AppendLine($"    {_names.MoveNext}();");
                sb.AppendLine($"    if (!{_names.Running})");
                sb.AppendLine("    {");
                sb.AppendLine($"        {_names.PendingContinuation} = \"\";");
                sb.AppendLine("        return true;");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    {_names.Continuation} = {_names.PendingContinuation};");
                sb.AppendLine($"    {_names.PendingContinuation} = \"\";");
                sb.AppendLine("    return false;");
                sb.AppendLine("}");
                return sb.ToString();
            }

            private string GenerateMoveNextMethodSource()
            {
                _switchSections.Clear();
                EmitSequence(0, _method.Body.Statements.ToList());

                var sb = new StringBuilder();
                sb.AppendLine($"public void {_names.MoveNext}()");
                sb.AppendLine("{");
                sb.AppendLine($"    switch ({_names.State})");
                sb.AppendLine("    {");
                foreach (string section in _switchSections)
                    sb.Append(section);
                sb.AppendLine("    }");
                sb.AppendLine("}");
                return sb.ToString();
            }

            private string GenerateCompleteMethodSource()
            {
                var sb = new StringBuilder();

                sb.AppendLine($"private void {_names.Complete}()");
                sb.AppendLine("{");
                sb.AppendLine($"    {_names.Running} = false;");
                sb.AppendLine($"    if (!string.IsNullOrEmpty({_names.Continuation}))");
                sb.AppendLine("    {");
                sb.AppendLine($"        string resume = {_names.Continuation};");
                sb.AppendLine($"        {_names.Continuation} = \"\";");
                sb.AppendLine("        SendCustomEvent(resume);");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                return sb.ToString();
            }

            private void EmitSequence(int stateId, List<StatementSyntax> statements)
            {
                var lines = new List<string>();

                for (int i = 0; i < statements.Count; i++)
                {
                    StatementSyntax statement = statements[i];

                    if (TryEmitEarlyReturnIfAwait(lines, statement))
                        continue;

                    if (statement is WhileStatementSyntax whileStatement && IsFlattenedWhile(whileStatement))
                    {
                        int loopState = AllocateState();
                        int afterLoopState = AllocateState();
                        int afterAwaitState = AllocateState();

                        // The loop head gets a state of its own. Folding it into the state that already
                        // carries the statements ahead of the loop replayed those statements on every
                        // iteration, so a 'float elapsed = 0f;' before the loop was reset each pass and the
                        // loop condition could never go false.
                        lines.Add($"{_names.State} = {loopState};");
                        lines.Add($"{_names.MoveNext}();");
                        lines.Add("return;");
                        AddCase(stateId, lines);

                        EmitWhileState(loopState, whileStatement, afterLoopState, afterAwaitState);
                        stateId = afterLoopState;
                        lines = new List<string>();
                        continue;
                    }

                    if (TryGetSimpleAwait(statement, out AwaitExpressionSyntax awaitExpression))
                    {
                        int nextState = AllocateState();
                        lines.Add($"{_names.State} = {nextState};");
                        lines.AddRange(CreateAwaitSchedule(awaitExpression));
                        lines.Add("return;");
                        AddCase(stateId, lines);
                        stateId = nextState;
                        lines = new List<string>();
                        continue;
                    }

                    if (statement is ReturnStatementSyntax)
                    {
                        lines.Add($"{_names.Complete}();");
                        lines.Add("return;");
                        AddCase(stateId, lines);

                        if (i == statements.Count - 1)
                            return;

                        // What follows a return at this level is unreachable, but C# and UdonSharp both
                        // still compile it, so it is kept in a case of its own rather than silently dropped
                        // -- an await in it would otherwise be reported as one CE could not lower.
                        stateId = AllocateState();
                        lines = new List<string>();
                        continue;
                    }

                    if (ContainsAwait(statement))
                    {
                        throw new CodeGenerationException(
                            DiagUnsupportedPattern,
                            statement.GetLocation(),
                            $"CE cannot lower this {statement.Kind()} in '{_methodName}' because it contains " +
                            "an await. Fix: lift the await out so it is a statement of its own in the method " +
                            "body or in a while loop body.");
                    }

                    lines.AddRange(TransformStatement(statement, null));
                }

                lines.Add($"{_names.Complete}();");
                lines.Add("return;");
                AddCase(stateId, lines);
            }

            private void EmitWhileState(
                int loopState,
                WhileStatementSyntax whileStatement,
                int afterLoopState,
                int afterAwaitState)
            {
                string condition = ReplaceIdentifiersText(whileStatement.Condition);
                var bodyStatements = GetBlockStatements(whileStatement.Statement);
                AwaitExpressionSyntax awaitExpression = bodyStatements
                    .SelectMany(s => s.DescendantNodes().OfType<AwaitExpressionSyntax>())
                    .FirstOrDefault();

                if (awaitExpression == null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        whileStatement.GetLocation(),
                        $"CE cannot lower this while loop in '{_methodName}': its await is not in the loop " +
                        "body. Fix: put the await directly in the loop body.");
                }

                int awaitStatementIndex = bodyStatements.FindIndex(s => s.DescendantNodes().OfType<AwaitExpressionSyntax>().Contains(awaitExpression));
                if (awaitStatementIndex < 0 || !TryGetSimpleAwait(bodyStatements[awaitStatementIndex], out awaitExpression))
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        whileStatement.GetLocation(),
                        $"CE cannot lower this while loop in '{_methodName}': the await inside it must be a " +
                        "statement of its own directly in the loop body.");
                }

                // The body is emitted into cases of the MoveNext switch, where a 'break' would leave the
                // switch instead of the loop: MoveNext returned with nothing scheduled and nothing
                // completed, the machine sat with its running flag set for the rest of the session, and
                // every later call was refused. 'continue' had no loop left to continue (CS0139). Both are
                // turned into the state transition they stand for.
                var loopJumps = new LoopJumpRewriter(
                    $"{{ {_names.State} = {afterLoopState}; {_names.MoveNext}(); return; }}",
                    $"{{ {_names.State} = {loopState}; {_names.MoveNext}(); return; }}",
                    _parseOptions);

                var loopLines = new List<string>();
                loopLines.Add($"if (!({condition}))");
                loopLines.Add("{");
                loopLines.Add($"    {_names.State} = {afterLoopState};");
                loopLines.Add($"    {_names.MoveNext}();");
                loopLines.Add("    return;");
                loopLines.Add("}");

                for (int i = 0; i < awaitStatementIndex; i++)
                    loopLines.AddRange(TransformStatement(bodyStatements[i], loopJumps));

                loopLines.Add($"{_names.State} = {afterAwaitState};");
                loopLines.AddRange(CreateAwaitSchedule(awaitExpression));
                loopLines.Add("return;");
                AddCase(loopState, loopLines);

                var afterAwaitLines = new List<string>();
                for (int i = awaitStatementIndex + 1; i < bodyStatements.Count; i++)
                    afterAwaitLines.AddRange(TransformStatement(bodyStatements[i], loopJumps));

                afterAwaitLines.Add($"{_names.State} = {loopState};");
                afterAwaitLines.Add($"{_names.MoveNext}();");
                afterAwaitLines.Add("return;");
                AddCase(afterAwaitState, afterAwaitLines);
            }

            private bool TryEmitEarlyReturnIfAwait(
                List<string> lines,
                StatementSyntax statement)
            {
                if (!(statement is IfStatementSyntax ifStatement))
                    return false;

                if (!ContainsAwait(ifStatement))
                    return false;

                if (ifStatement.Else != null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        ifStatement.GetLocation(),
                        $"CE cannot lower an if/else containing an await in '{_methodName}'. Fix: split the " +
                        "branches into separate async methods and await those.");
                }

                var thenStatements = GetBlockStatements(ifStatement.Statement);
                if (thenStatements.Count != 2 ||
                    !TryGetSimpleAwait(thenStatements[0], out AwaitExpressionSyntax awaitExpression) ||
                    !(thenStatements[1] is ReturnStatementSyntax))
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        ifStatement.GetLocation(),
                        $"CE cannot lower this if in '{_methodName}'. The only awaiting if it supports is an " +
                        "early exit of exactly 'await ...; return;'.");
                }

                int completionState = AllocateState();
                string condition = ReplaceIdentifiersText(ifStatement.Condition);

                lines.Add($"if ({condition})");
                lines.Add("{");
                lines.Add($"    {_names.State} = {completionState};");
                foreach (string scheduleLine in CreateAwaitSchedule(awaitExpression))
                    lines.Add("    " + scheduleLine);
                lines.Add("    return;");
                lines.Add("}");

                var completionLines = new List<string>
                {
                    $"{_names.Complete}();",
                    "return;"
                };
                AddCase(completionState, completionLines);
                return true;
            }

            /// <summary>
            /// The lines that suspend the machine on one await. The await target is identified by symbol:
            /// matching the written text treated any receiver ending in "UdonTask" as CE's, so
            /// <c>await MyUdonTask.Yield(1, 2)</c> became a one-frame delay and the author's method was
            /// never called.
            /// </summary>
            private List<string> CreateAwaitSchedule(AwaitExpressionSyntax awaitExpression)
            {
                _loweredAwaits.Add(awaitExpression);

                if (awaitExpression.Expression is InvocationExpressionSyntax original &&
                    ReplaceIdentifiers(original) is InvocationExpressionSyntax invocation)
                {
                    IMethodSymbol target = ResolveInvocationTarget(original);

                    if (IsUdonTaskStaticCall(original, target, "Delay"))
                    {
                        string delay = invocation.ArgumentList.Arguments.Count > 0
                            ? invocation.ArgumentList.Arguments[0].Expression.ToString()
                            : "0f";
                        return new List<string>
                        {
                            $"SendCustomEventDelayedSeconds(\"{_names.MoveNext}\", {delay}, global::VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);"
                        };
                    }

                    if (IsUdonTaskStaticCall(original, target, "DelayFrames"))
                    {
                        string frames = invocation.ArgumentList.Arguments.Count > 0
                            ? invocation.ArgumentList.Arguments[0].Expression.ToString()
                            : "1";
                        return new List<string>
                        {
                            $"SendCustomEventDelayedFrames(\"{_names.MoveNext}\", {frames}, global::VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);"
                        };
                    }

                    if (IsUdonTaskStaticCall(original, target, "Yield"))
                    {
                        return new List<string>
                        {
                            $"SendCustomEventDelayedFrames(\"{_names.MoveNext}\", 1, global::VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);"
                        };
                    }

                    if (IsLoweredAsyncMethod(target))
                        return CreateChildAwait(awaitExpression, original, invocation, target);
                }

                throw new CodeGenerationException(
                    DiagUnsupportedPattern,
                    awaitExpression.GetLocation(),
                    $"CE cannot lower 'await {awaitExpression.Expression}' in '{_methodName}'. Awaitable " +
                    "targets are UdonTask.Delay, UdonTask.DelayFrames, UdonTask.Yield, and an async UdonTask " +
                    "method on this same behaviour.");
            }

            private IMethodSymbol ResolveInvocationTarget(InvocationExpressionSyntax invocation)
            {
                SymbolInfo info = _semanticModel.GetSymbolInfo(invocation);
                if (info.Symbol is IMethodSymbol method)
                    return method;

                // A lone candidate is what the model reports when the call names exactly one method but an
                // argument's type did not resolve, which is the normal state of a tree lowered without the
                // rest of the project around it.
                return info.CandidateSymbols.Length == 1
                    ? info.CandidateSymbols[0] as IMethodSymbol
                    : null;
            }

            private bool IsUdonTaskStaticCall(InvocationExpressionSyntax invocation, IMethodSymbol target, string methodName)
            {
                if (target != null)
                {
                    return target.IsStatic &&
                           string.Equals(target.Name, methodName, StringComparison.Ordinal) &&
                           IsUdonTaskType(target.ContainingType);
                }

                // No symbol at all. The real compile never gets here for a call that compiles; a tree
                // lowered without CE's runtime sources does, and there the only thing known about the
                // receiver is that it is written 'UdonTask' and names nothing else.
                if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess) ||
                    !string.Equals(memberAccess.Name.Identifier.ValueText, methodName, StringComparison.Ordinal))
                {
                    return false;
                }

                if (_semanticModel.GetSymbolInfo(memberAccess.Expression).Symbol != null)
                    return false;

                return string.Equals(RightmostName(memberAccess.Expression), UdonTaskTypeName, StringComparison.Ordinal);
            }

            private static string RightmostName(ExpressionSyntax expression)
            {
                switch (expression)
                {
                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.ValueText;
                    case MemberAccessExpressionSyntax memberAccess:
                        return memberAccess.Name.Identifier.ValueText;
                    case AliasQualifiedNameSyntax aliasQualified:
                        return aliasQualified.Name.Identifier.ValueText;
                    case QualifiedNameSyntax qualified:
                        return qualified.Right.Identifier.ValueText;
                    default:
                        return string.Empty;
                }
            }

            private List<string> CreateChildAwait(
                AwaitExpressionSyntax awaitExpression,
                InvocationExpressionSyntax original,
                InvocationExpressionSyntax invocation,
                IMethodSymbol target)
            {
                INamedTypeSymbol host = _methodSymbol?.ContainingType;

                if (host != null && !SymbolEqualityComparer.Default.Equals(target.ContainingType.OriginalDefinition, host.OriginalDefinition))
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        awaitExpression.GetLocation(),
                        $"CE cannot lower 'await {awaitExpression.Expression}' in '{_methodName}': " +
                        $"'{target.Name}' is declared on '{target.ContainingType.Name}', not on " +
                        $"'{host.Name}'. CE emits each state machine as private members of the class that " +
                        "declares the method, so only an async UdonTask method declared on this same class " +
                        "can be awaited. Fix: declare the awaited method on this class, or call it without " +
                        "awaiting and have it signal its own end.");
                }

                // The continuation is delivered by SendCustomEvent on *this* behaviour, so only a call
                // that targets this behaviour can be chained. Matching on the bare method name also
                // matched 'other.Child()' and emitted a '__Child_Start' call this class does not have.
                if (!IsSelfInvocation(original))
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        awaitExpression.GetLocation(),
                        $"CE cannot lower 'await {awaitExpression.Expression}' in '{_methodName}'. Awaitable " +
                        "targets are UdonTask.Delay, UdonTask.DelayFrames, UdonTask.Yield, and an async UdonTask " +
                        "method on this same behaviour.");
                }

                string arguments = PlaceChildArguments(awaitExpression, original, invocation, target);
                string startCall = $"{MachineNames.StartMethodName(target.Name)}({arguments}\"{_names.MoveNext}\")";

                // Start answers true when there is nothing to wait for -- the child ran to its end, or was
                // refused -- and this machine then carries on with a plain call instead of being re-entered
                // through SendCustomEvent.
                return new List<string>
                {
                    $"if ({startCall})",
                    $"    {_names.MoveNext}();"
                };
            }

            /// <summary>
            /// Lays the written arguments out in the child's parameter order. They used to be copied in the
            /// order they were written with their names dropped, so <c>await Fade(to: 1f, from: 0f)</c>
            /// started the fade with the two swapped, and a call that relied on an optional or params
            /// parameter failed as CS7036 or CS1501 against a generated Start method.
            ///
            /// Arguments are evaluated in parameter order, which is also how U# evaluates named arguments
            /// everywhere else.
            /// </summary>
            private string PlaceChildArguments(
                AwaitExpressionSyntax awaitExpression,
                InvocationExpressionSyntax original,
                InvocationExpressionSyntax invocation,
                IMethodSymbol target)
            {
                var parameters = target.Parameters;
                var placed = new string[parameters.Length];
                SeparatedSyntaxList<ArgumentSyntax> written = original.ArgumentList.Arguments;

                for (int i = 0; i < written.Count; i++)
                {
                    ArgumentSyntax argument = written[i];
                    int slot = i;

                    if (argument.NameColon != null)
                    {
                        string name = argument.NameColon.Name.Identifier.ValueText;
                        slot = -1;

                        for (int p = 0; p < parameters.Length; p++)
                        {
                            if (string.Equals(parameters[p].Name, name, StringComparison.Ordinal))
                            {
                                slot = p;
                                break;
                            }
                        }
                    }

                    bool expandsParams = slot >= parameters.Length && parameters.Length > 0 && parameters[parameters.Length - 1].IsParams;

                    if (!expandsParams && slot >= 0 && slot < parameters.Length && parameters[slot].IsParams)
                    {
                        ITypeSymbol converted = _semanticModel.GetTypeInfo(argument.Expression).ConvertedType;
                        expandsParams = !SymbolEqualityComparer.Default.Equals(converted, parameters[slot].Type);
                    }

                    if (expandsParams)
                    {
                        throw new CodeGenerationException(
                            DiagUnsupportedPattern,
                            argument.GetLocation(),
                            $"CE cannot lower 'await {awaitExpression.Expression}' in '{_methodName}': " +
                            $"'{target.Name}' takes a params array and this call lists its elements one by " +
                            "one. The generated start method takes the array itself. Fix: pass an array, " +
                            "for example 'new[] { a, b }'.");
                    }

                    if (slot < 0 || slot >= parameters.Length || placed[slot] != null)
                    {
                        throw new CodeGenerationException(
                            DiagUnsupportedPattern,
                            argument.GetLocation(),
                            $"CE cannot lower 'await {awaitExpression.Expression}' in '{_methodName}': this " +
                            $"argument does not correspond to a parameter of '{target.Name}'.");
                    }

                    placed[slot] = invocation.ArgumentList.Arguments[i].Expression.ToString();
                }

                for (int p = 0; p < parameters.Length; p++)
                {
                    if (placed[p] != null)
                        continue;

                    string omission = parameters[p].IsParams
                        ? $"passes no elements for the params parameter '{parameters[p].Name}' of '{target.Name}'"
                        : $"passes nothing for parameter '{parameters[p].Name}' of '{target.Name}' and relies on its default";

                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        original.ArgumentList.GetLocation(),
                        $"CE cannot lower 'await {awaitExpression.Expression}' in '{_methodName}': the call " +
                        $"{omission}. The generated start method takes every parameter explicitly. Fix: " +
                        "write the argument out.");
                }

                string arguments = string.Join(", ", placed);
                if (!string.IsNullOrEmpty(arguments))
                    arguments += ", ";

                return arguments;
            }

            private IEnumerable<string> TransformStatement(StatementSyntax statement, LoopJumpRewriter loopJumps)
            {
                if (statement is LocalDeclarationStatementSyntax localDeclaration)
                    return TransformLocalDeclaration(localDeclaration);

                SyntaxNode replaced = CreateIdentifierReplacer().Visit(statement);
                replaced = RewriteNestedReturns(replaced);

                // After the returns, so the 'return' inside each rewritten jump is not itself taken for an
                // early exit of the method.
                if (loopJumps != null)
                    replaced = loopJumps.Visit(replaced);

                return new[] { EmittedText(replaced) };
            }

            /// <summary>
            /// Statements emitted verbatim into a MoveNext case keep their own 'return', which exits
            /// MoveNext without running the completion. That would leave the running flag latched on
            /// forever and the behaviour would refuse every later start of this method, so each nested
            /// return has to tear the state machine down the way the top-level path in EmitSequence does.
            /// The shape CE's own CancellationToken documentation asks authors to write --
            /// 'if (token.IsCancellationRequested) return;' -- lands here.
            /// </summary>
            private SyntaxNode RewriteNestedReturns(SyntaxNode statement)
            {
                return new NestedReturnRewriter(_names.Complete, _parseOptions).Visit(statement);
            }

            private IEnumerable<string> TransformLocalDeclaration(LocalDeclarationStatementSyntax localDeclaration)
            {
                bool allHoisted = localDeclaration.Declaration.Variables
                    .All(variable => _declaratorFields.ContainsKey(variable.Identifier.Span));

                if (!allHoisted)
                {
                    // Reached by a path that does not flatten this statement, so its braces still scope the
                    // local and the declaration has to survive intact.
                    return new[] { EmittedText(CreateIdentifierReplacer().Visit(localDeclaration)) };
                }

                var lines = new List<string>();

                // A hoisted const carries its value in the field declaration; there is nothing to assign.
                if (localDeclaration.Modifiers.Any(SyntaxKind.ConstKeyword))
                    return lines;

                foreach (VariableDeclaratorSyntax variable in localDeclaration.Declaration.Variables)
                {
                    if (variable.Initializer == null)
                        continue;

                    string fieldName = _declaratorFields[variable.Identifier.Span];
                    string value = ReplaceIdentifiersText(variable.Initializer.Value);

                    // 'float[] steps = { 1f, 2f };' is only legal as a declaration. Now that it is an
                    // assignment to a field, the braces need the array type in front of them.
                    if (variable.Initializer.Value is InitializerExpressionSyntax)
                        value = $"new {localDeclaration.Declaration.Type} {value}";

                    lines.Add($"{fieldName} = {value};");
                }

                return lines;
            }

            private IdentifierReplacer CreateIdentifierReplacer()
            {
                return new IdentifierReplacer(_identifierReplacements, _declaratorFields, _nameofValues);
            }

            private ExpressionSyntax ReplaceIdentifiers(ExpressionSyntax expression)
            {
                SyntaxNode replaced = CreateIdentifierReplacer().Visit(expression);
                return (ExpressionSyntax)new DirectiveStripper().Visit(replaced);
            }

            private string ReplaceIdentifiersText(ExpressionSyntax expression)
            {
                return ReplaceIdentifiers(expression).ToString();
            }

            /// <summary>
            /// The text of a rewritten statement as it goes into generated source. Preprocessor directives
            /// and the text they disabled are dropped: the author's file has already been parsed, so every
            /// <c>#if</c> has been decided, and what is left of one inside a statement can only be decided
            /// differently -- or left unbalanced -- when the generated member is parsed again.
            /// </summary>
            private static string EmittedText(SyntaxNode node)
            {
                return new DirectiveStripper().Visit(node).ToString();
            }

            private static List<StatementSyntax> GetBlockStatements(StatementSyntax statement)
            {
                if (statement is BlockSyntax block)
                    return block.Statements.ToList();

                return new List<StatementSyntax> { statement };
            }

            private static bool TryGetSimpleAwait(StatementSyntax statement, out AwaitExpressionSyntax awaitExpression)
            {
                awaitExpression = null;
                if (statement is ExpressionStatementSyntax expressionStatement &&
                    expressionStatement.Expression is AwaitExpressionSyntax await)
                {
                    awaitExpression = await;
                    return true;
                }

                return false;
            }

            private static bool IsSelfInvocation(InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is IdentifierNameSyntax)
                    return true;

                return invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                       memberAccess.Expression is ThisExpressionSyntax;
            }

            private void AddCase(int stateId, IEnumerable<string> lines)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"        case {stateId}:");
                foreach (string line in lines)
                    sb.AppendLine($"            {line}");
                _switchSections.Add(sb.ToString());
            }

            private int AllocateState()
            {
                return _nextState++;
            }

            private string StartParameterList()
            {
                // Identifier.Text rather than ValueText: a parameter written '@event' has to stay escaped
                // where it is declared and read, or the generated method does not parse.
                var parameters = _method.ParameterList.Parameters
                    .Select(parameter => $"{parameter.Type} {parameter.Identifier.Text}")
                    .ToList();
                parameters.Add($"string {_continuationParameterName}");
                return string.Join(", ", parameters);
            }

            private string MakeUniqueFieldName(string name)
            {
                string candidate = $"__{_methodName}_{name}";
                if (_usedMemberNames.Add(candidate))
                    return candidate;

                // Two scopes in one method can each declare 't'; both land in the same class, so the second
                // needs a name of its own or the class does not compile.
                for (int suffix = 1; ; suffix++)
                {
                    string scoped = $"{candidate}_{suffix}";
                    if (_usedMemberNames.Add(scoped))
                        return scoped;
                }
            }

            private string MakeUniqueParameterName(string name)
            {
                var taken = new HashSet<string>(
                    _method.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
                    StringComparer.Ordinal);

                if (!taken.Contains(name))
                    return name;

                for (int suffix = 1; ; suffix++)
                {
                    string scoped = $"{name}{suffix}";
                    if (!taken.Contains(scoped))
                        return scoped;
                }
            }

            /// <summary>
            /// Parses generated source and refuses anything malformed. A member that does not parse used to
            /// ride the tree into Roslyn and surface as compiler errors inside code the author has no file
            /// for; failing here names the method that produced it.
            /// </summary>
            private MemberDeclarationSyntax ParseMember(string source, Location location)
            {
                MemberDeclarationSyntax member = SyntaxFactory.ParseMemberDeclaration("\n" + source + "\n", 0, _parseOptions);

                if (member == null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"CE generated a member for '{_methodName}' that is not a valid declaration:\n{source}");
                }

                Diagnostic error = member.GetDiagnostics()
                    // Qualified: UdonSharp.Compiler also declares a DiagnosticSeverity, and this file sees
                    // both through usings with neither closer, so the bare name is ambiguous here.
                    .FirstOrDefault(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

                if (error != null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"CE generated a member for '{_methodName}' that does not parse ({error.GetMessage()}):\n{source}");
                }

                return member;
            }
        }

        private sealed class HoistedVariable
        {
            public HoistedVariable(string fieldName, string typeName, ExpressionSyntax constantValue)
            {
                FieldName = fieldName;
                TypeName = typeName;
                ConstantValue = constantValue;
            }

            public string FieldName { get; }
            public string TypeName { get; }

            /// <summary>The initializer of a const local, which becomes a const field; null for everything else.</summary>
            public ExpressionSyntax ConstantValue { get; }
        }

        /// <summary>
        /// Reports, at the line the author wrote, syntax UdonSharp's binder refuses wherever it meets it. A
        /// lowered method's body is re-emitted inside the generated MoveNext, so the binder's own error for a
        /// local function, a goto or a tuple there pointed into generated text the author has no file for.
        ///
        /// The walk goes only where the binder goes, because syntax the binder never reaches compiles today
        /// and has to keep compiling. It leaves alone the branch of an 'if' whose condition UdonSharp folds to
        /// a constant (both branches, when the fold cannot be predicted here), the bodies of lambdas and
        /// anonymous methods (CE's lambda passes and the binder decide those), pattern 'case' labels (the
        /// binder skips them), the right side of '??' over a value type, and the operands of nameof, typeof,
        /// sizeof and default. Each message is the binder's own, so the error reads the way it would for the
        /// same code in an ordinary method.
        /// </summary>
        private sealed class UdonSharpSyntaxCheck
        {
            // UdonSharp's wording, from BinderSyntaxVisitor and its en-us localization.
            private const string NodeNotSupported = "UdonSharp does not currently support node type '{0}'";
            private const string LocalMethodsNotSupported = "Local method declarations are not currently supported by UdonSharp";
            private const string TryNotSupported = "Try/Catch/Finally is not supported by UdonSharp since Udon does not have a way to handle exceptions";
            private const string ThrowNotSupported = "UdonSharp does not support throwing exceptions since Udon does not have support for exception throwing at the moment";
            private const string IsWithUserTypeNotSupported = "The 'is' operator with user-defined types is not supported in UdonSharp";
            private const string IsNotSupported = "The 'is' operator is not yet supported in UdonSharp. Use explicit null checks after 'as' casts instead.";
            private const string TypeOfUserTypeNotSupported = "Cannot use typeof on user-defined types";
            private const string InitializerListsNotSupported = "U# does not support collection initializers or nested initializers. Object initializers (new Type { Field = value }) are supported.";
            private const string MultidimensionalArraysNotSupported = "Multidimensional arrays are not yet supported by UdonSharp, consider using jagged arrays instead.";

            // The binder has no message of its own for this one: it fails on the assignment with an internal
            // NullReferenceException.
            private const string DeconstructionNotSupported = "UdonSharp cannot compile a deconstruction such as 'var (a, b) = ...'; its binder stops on one with an internal error";

            private readonly SemanticModel _semanticModel;
            private readonly string _methodName;

            public UdonSharpSyntaxCheck(SemanticModel semanticModel, string methodName)
            {
                _semanticModel = semanticModel;
                _methodName = methodName;
            }

            public void Check(BlockSyntax body)
            {
                Visit(body);
            }

            private void Visit(SyntaxNode node)
            {
                switch (node)
                {
                    case AnonymousFunctionExpressionSyntax _:
                        return;

                    case LocalFunctionStatementSyntax _:
                        throw Refuse(node, LocalMethodsNotSupported);

                    case TryStatementSyntax _:
                        throw Refuse(node, TryNotSupported);

                    case ThrowStatementSyntax _:
                    case ThrowExpressionSyntax _:
                        throw Refuse(node, ThrowNotSupported);

                    case CommonForEachStatementSyntax forEach when forEach.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                        throw RefuseAwait(node, "await foreach");

                    case LocalDeclarationStatementSyntax local when local.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword):
                        throw RefuseAwait(node, "await using");

                    case IfStatementSyntax ifStatement:
                        VisitIf(ifStatement);
                        return;

                    case SwitchStatementSyntax switchStatement:
                        VisitSwitch(switchStatement);
                        return;

                    case InvocationExpressionSyntax invocation when IsNameof(invocation):
                        return;

                    case SizeOfExpressionSyntax _:
                    case DefaultExpressionSyntax _:
                        return;

                    case TypeOfExpressionSyntax typeOf:
                        if (IsUserDefined(_semanticModel.GetTypeInfo(typeOf.Type).Type))
                            throw Refuse(node, TypeOfUserTypeNotSupported);
                        return;

                    case BinaryExpressionSyntax isExpression when isExpression.IsKind(SyntaxKind.IsExpression):
                        throw Refuse(node, IsUserDefined(_semanticModel.GetTypeInfo(isExpression.Right).Type)
                            ? IsWithUserTypeNotSupported
                            : IsNotSupported);

                    case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                        // Over a value type the binder returns the left side and never binds the right one.
                        Visit(coalesce.Left);
                        if (!IsValueType(coalesce.Left))
                            Visit(coalesce.Right);
                        return;

                    case DeclarationExpressionSyntax declaration when declaration.Designation is ParenthesizedVariableDesignationSyntax:
                        throw Refuse(node, DeconstructionNotSupported);

                    case BaseObjectCreationExpressionSyntax creation when creation.Initializer != null &&
                                                                          !creation.Initializer.IsKind(SyntaxKind.ObjectInitializerExpression):
                        // The binder binds the arguments before it looks at the initializer.
                        if (creation.ArgumentList != null)
                            Visit(creation.ArgumentList);
                        throw Refuse(creation.Initializer, InitializerListsNotSupported);

                    case InitializerExpressionSyntax nested when IsObjectInitializerMemberValue(nested):
                        throw Refuse(node, string.Format(NodeNotSupported, node.Kind()));

                    case ArrayCreationExpressionSyntax array when array.Type.RankSpecifiers.Count > 0 &&
                                                                  array.Type.RankSpecifiers[0].Sizes.Count != 1:
                        throw Refuse(node, MultidimensionalArraysNotSupported);

                    case ImplicitArrayCreationExpressionSyntax implicitArray when implicitArray.Commas.Count != 0:
                        throw Refuse(node, MultidimensionalArraysNotSupported);
                }

                if (IsUnsupportedNode(node))
                    throw Refuse(node, string.Format(NodeNotSupported, node.Kind()));

                foreach (SyntaxNode child in node.ChildNodes())
                    Visit(child);
            }

            /// <summary>
            /// The binder binds only the branch a constant condition takes. Whatever sits in the other one
            /// compiles however unsupported it is, so it is not reported either -- and when the condition is
            /// one the binder may fold without this walk being able to tell which way, neither branch is.
            /// </summary>
            private void VisitIf(IfStatementSyntax ifStatement)
            {
                Visit(ifStatement.Condition);

                (bool constant, bool? value) = Fold(ifStatement.Condition);

                if (constant && !value.HasValue)
                    return;

                if (!constant || value == true)
                    Visit(ifStatement.Statement);

                if (ifStatement.Else != null && (!constant || value == false))
                    Visit(ifStatement.Else.Statement);
            }

            private void VisitSwitch(SwitchStatementSyntax switchStatement)
            {
                Visit(switchStatement.Expression);

                foreach (SwitchSectionSyntax section in switchStatement.Sections)
                {
                    // A pattern label ('case int n:', 'case X when ...:') is skipped by the binder, so nothing
                    // inside it is ever compiled.
                    foreach (SwitchLabelSyntax label in section.Labels)
                    {
                        if (label is CaseSwitchLabelSyntax caseLabel)
                            Visit(caseLabel.Value);
                    }

                    foreach (StatementSyntax statement in section.Statements)
                        Visit(statement);
                }
            }

            /// <summary>
            /// Whether UdonSharp's binder folds an expression to a constant, and the boolean value when it can
            /// be told. It leans towards 'constant': calling a condition constant only spares a branch from
            /// this check, while missing one the binder folds would report code that compiles today. Beyond
            /// C#'s own constants the binder folds typeof, sizeof, default, a struct built from constants, and
            /// any value type compared with null.
            /// </summary>
            private (bool constant, bool? value) Fold(ExpressionSyntax expression)
            {
                Optional<object> csharpConstant = _semanticModel.GetConstantValue(expression);
                if (csharpConstant.HasValue)
                    return (true, csharpConstant.Value as bool?);

                switch (expression)
                {
                    case ParenthesizedExpressionSyntax parenthesized:
                        return Fold(parenthesized.Expression);

                    case CastExpressionSyntax cast:
                        return Fold(cast.Expression);

                    case PrefixUnaryExpressionSyntax unary:
                    {
                        (bool constant, bool? value) operand = Fold(unary.Operand);
                        if (!operand.constant)
                            return (false, null);

                        return unary.IsKind(SyntaxKind.LogicalNotExpression) && operand.value.HasValue
                            ? (true, !operand.value.Value)
                            : (true, (bool?)null);
                    }

                    case BinaryExpressionSyntax binary:
                        return FoldBinary(binary);

                    case TypeOfExpressionSyntax _:
                    case SizeOfExpressionSyntax _:
                    case DefaultExpressionSyntax _:
                    case LiteralExpressionSyntax _:
                        return (true, null);

                    case InterpolatedStringExpressionSyntax interpolated
                        when !interpolated.Contents.OfType<InterpolationSyntax>().Any():
                        return (true, null);

                    case BaseObjectCreationExpressionSyntax creation when IsFoldedStructCreation(creation):
                        return (true, null);
                }

                return (false, null);
            }

            private (bool constant, bool? value) FoldBinary(BinaryExpressionSyntax binary)
            {
                bool equality = binary.IsKind(SyntaxKind.EqualsExpression) || binary.IsKind(SyntaxKind.NotEqualsExpression);

                if (equality &&
                    ((IsNullConstant(binary.Left) && IsValueType(binary.Right)) ||
                     (IsNullConstant(binary.Right) && IsValueType(binary.Left))))
                {
                    return (true, binary.IsKind(SyntaxKind.NotEqualsExpression));
                }

                (bool constant, bool? value) left = Fold(binary.Left);
                (bool constant, bool? value) right = Fold(binary.Right);

                if (!left.constant || !right.constant)
                    return (false, null);

                if (!left.value.HasValue || !right.value.HasValue)
                    return (true, null);

                bool l = left.value.Value;
                bool r = right.value.Value;

                switch (binary.Kind())
                {
                    case SyntaxKind.LogicalAndExpression:
                    case SyntaxKind.BitwiseAndExpression:
                        return (true, l && r);
                    case SyntaxKind.LogicalOrExpression:
                    case SyntaxKind.BitwiseOrExpression:
                        return (true, l || r);
                    case SyntaxKind.ExclusiveOrExpression:
                        return (true, l ^ r);
                    case SyntaxKind.EqualsExpression:
                        return (true, l == r);
                    case SyntaxKind.NotEqualsExpression:
                        return (true, l != r);
                    default:
                        return (true, null);
                }
            }

            /// <summary>The binder turns 'new SomeStruct(constants)' into a constant when the struct is Udon's.</summary>
            private bool IsFoldedStructCreation(BaseObjectCreationExpressionSyntax creation)
            {
                if (creation.Initializer != null)
                    return false;

                ITypeSymbol type = _semanticModel.GetTypeInfo(creation).Type;
                if (type == null || !type.IsValueType || IsUserDefined(type))
                    return false;

                return creation.ArgumentList == null ||
                       creation.ArgumentList.Arguments.All(argument => Fold(argument.Expression).constant);
            }

            private bool IsNameof(InvocationExpressionSyntax invocation)
            {
                return invocation.Expression is IdentifierNameSyntax name &&
                       string.Equals(name.Identifier.ValueText, "nameof", StringComparison.Ordinal) &&
                       _semanticModel.GetConstantValue(invocation).HasValue;
            }

            private bool IsNullConstant(ExpressionSyntax expression)
            {
                Optional<object> constant = _semanticModel.GetConstantValue(expression);
                return constant.HasValue && constant.Value == null;
            }

            private bool IsValueType(ExpressionSyntax expression)
            {
                return _semanticModel.GetTypeInfo(expression).Type?.IsValueType == true;
            }

            /// <summary>
            /// UdonSharp's own test: a type is extern when it comes from metadata, and anything declared in
            /// source -- every behaviour, user class and enum, and CE's runtime types -- is user-defined.
            /// </summary>
            private static bool IsUserDefined(ITypeSymbol type)
            {
                while (type is IArrayTypeSymbol array)
                    type = array.ElementType;

                if (!(type is INamedTypeSymbol named) || named.TypeKind == TypeKind.Error)
                    return false;

                return !(named.Locations.FirstOrDefault()?.IsInMetadata ?? false);
            }

            /// <summary>
            /// An initializer given as a member's value inside an object initializer, as in
            /// 'new Foo { Bar = { X = 1 } }'. The binder visits it as an expression and has no case for it.
            /// </summary>
            private static bool IsObjectInitializerMemberValue(InitializerExpressionSyntax initializer)
            {
                return initializer.Parent is AssignmentExpressionSyntax assignment &&
                       assignment.Right == initializer &&
                       assignment.Parent is InitializerExpressionSyntax outer &&
                       outer.IsKind(SyntaxKind.ObjectInitializerExpression) &&
                       outer.Parent is BaseObjectCreationExpressionSyntax;
            }

            /// <summary>Syntax the binder has no case for at all, so it always ends in its 'node type' error.</summary>
            private static bool IsUnsupportedNode(SyntaxNode node)
            {
                switch (node)
                {
                    case GotoStatementSyntax _:
                    case LabeledStatementSyntax _:
                    case LockStatementSyntax _:
                    case UsingStatementSyntax _:
                    case CheckedStatementSyntax _:
                    case ForEachVariableStatementSyntax _:
                    case TupleExpressionSyntax _:
                    case IsPatternExpressionSyntax _:
                    case ConditionalAccessExpressionSyntax _:
                    case SwitchExpressionSyntax _:
                    case RefExpressionSyntax _:
                    case AnonymousObjectCreationExpressionSyntax _:
                    case QueryExpressionSyntax _:
                        return true;
                    default:
                        return false;
                }
            }

            private CodeGenerationException Refuse(SyntaxNode node, string udonSharpMessage)
            {
                return new CodeGenerationException(
                    DiagUnsupportedPattern,
                    node.GetLocation(),
                    $"{udonSharpMessage} -- in async method '{_methodName}'. UdonSharp refuses this in any " +
                    "method; CE reports it here because it moves this method's body into a generated state " +
                    "machine, where UdonSharp's own error would point at generated code instead of this line.");
            }

            private CodeGenerationException RefuseAwait(SyntaxNode node, string form)
            {
                return new CodeGenerationException(
                    DiagUnsupportedPattern,
                    node.GetLocation(),
                    $"CE cannot lower '{form}' in '{_methodName}': its await belongs to the statement instead of " +
                    "being an await statement of its own. CE lowers only 'await UdonTask.Delay/DelayFrames/Yield' " +
                    "and an awaited async UdonTask method on this class.");
            }
        }

        /// <summary>
        /// Replaces every return that belongs to the async method body with a completion followed by the
        /// return. Returns inside a lambda, anonymous method or local function belong to that body rather
        /// than to the async method, so those are left alone.
        /// </summary>
        private sealed class NestedReturnRewriter : CSharpSyntaxRewriter
        {
            private readonly string _completeMethodName;
            private readonly CSharpParseOptions _parseOptions;

            public NestedReturnRewriter(string completeMethodName, CSharpParseOptions parseOptions)
            {
                _completeMethodName = completeMethodName;
                _parseOptions = parseOptions;
            }

            public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node)
            {
                return SyntaxFactory
                    .ParseStatement("{ " + _completeMethodName + "(); return; }", 0, _parseOptions)
                    .WithTriviaFrom(node);
            }

            public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;

            public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;

            public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

            public override SyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
        }

        /// <summary>
        /// Turns the <c>break</c> and <c>continue</c> of an awaiting while loop into state transitions.
        /// A jump that belongs to something nested inside the loop body is not the loop's to rewrite: an
        /// inner loop owns both of its own, an inner switch owns its <c>break</c> but not a
        /// <c>continue</c>, and a lambda or local function is a different body altogether.
        /// </summary>
        private sealed class LoopJumpRewriter : CSharpSyntaxRewriter
        {
            private readonly string _breakBlock;
            private readonly string _continueBlock;
            private readonly CSharpParseOptions _parseOptions;
            private int _switchDepth;

            public LoopJumpRewriter(string breakBlock, string continueBlock, CSharpParseOptions parseOptions)
            {
                _breakBlock = breakBlock;
                _continueBlock = continueBlock;
                _parseOptions = parseOptions;
            }

            public override SyntaxNode VisitBreakStatement(BreakStatementSyntax node)
            {
                if (_switchDepth > 0)
                    return node;

                return SyntaxFactory.ParseStatement(_breakBlock, 0, _parseOptions).WithTriviaFrom(node);
            }

            public override SyntaxNode VisitContinueStatement(ContinueStatementSyntax node)
            {
                return SyntaxFactory.ParseStatement(_continueBlock, 0, _parseOptions).WithTriviaFrom(node);
            }

            public override SyntaxNode VisitSwitchStatement(SwitchStatementSyntax node)
            {
                _switchDepth++;
                try
                {
                    return base.VisitSwitchStatement(node);
                }
                finally
                {
                    _switchDepth--;
                }
            }

            public override SyntaxNode VisitWhileStatement(WhileStatementSyntax node) => node;

            public override SyntaxNode VisitDoStatement(DoStatementSyntax node) => node;

            public override SyntaxNode VisitForStatement(ForStatementSyntax node) => node;

            public override SyntaxNode VisitForEachStatement(ForEachStatementSyntax node) => node;

            public override SyntaxNode VisitForEachVariableStatement(ForEachVariableStatementSyntax node) => node;

            public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;

            public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;

            public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

            public override SyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
        }

        /// <summary>
        /// Removes preprocessor directives and disabled text from a node that is about to be written out
        /// as generated source.
        /// </summary>
        private sealed class DirectiveStripper : CSharpSyntaxRewriter
        {
            public override SyntaxTrivia VisitTrivia(SyntaxTrivia trivia)
            {
                if (trivia.IsDirective || trivia.IsKind(SyntaxKind.DisabledTextTrivia))
                    return default(SyntaxTrivia);

                return trivia;
            }
        }

        private sealed class IdentifierReplacer : CSharpSyntaxRewriter
        {
            private readonly Dictionary<TextSpan, string> _replacements;
            private readonly Dictionary<TextSpan, string> _declaratorFields;
            private readonly Dictionary<TextSpan, string> _nameofValues;

            public IdentifierReplacer(
                Dictionary<TextSpan, string> replacements,
                Dictionary<TextSpan, string> declaratorFields,
                Dictionary<TextSpan, string> nameofValues)
            {
                _replacements = replacements;
                _declaratorFields = declaratorFields;
                _nameofValues = nameofValues;
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                if (_replacements.TryGetValue(node.Span, out string replacement))
                {
                    return SyntaxFactory.IdentifierName(replacement)
                        .WithLeadingTrivia(node.GetLeadingTrivia())
                        .WithTrailingTrivia(node.GetTrailingTrivia());
                }

                return base.VisitIdentifierName(node);
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (_nameofValues.TryGetValue(node.Span, out string value))
                {
                    return SyntaxFactory
                        .LiteralExpression(SyntaxKind.StringLiteralExpression, SyntaxFactory.Literal(value))
                        .WithTriviaFrom(node);
                }

                return base.VisitInvocationExpression(node);
            }

            /// <summary>
            /// An <c>out var n</c> whose variable was hoisted names the field instead of declaring anything.
            /// </summary>
            public override SyntaxNode VisitDeclarationExpression(DeclarationExpressionSyntax node)
            {
                if (node.Designation is SingleVariableDesignationSyntax single &&
                    _declaratorFields.TryGetValue(single.Identifier.Span, out string fieldName))
                {
                    return SyntaxFactory.IdentifierName(fieldName).WithTriviaFrom(node);
                }

                return base.VisitDeclarationExpression(node);
            }
        }
    }
}
