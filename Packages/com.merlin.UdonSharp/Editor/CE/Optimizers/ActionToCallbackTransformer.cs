using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Udon;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Transforms parameterless <c>System.Action</c> usage into Udon-compatible CECallback usage at
    /// compile time.
    ///
    /// This optimizer enables UdonSharp scripts to use familiar C# delegate syntax while
    /// generating code that works with Udon's limitations. Since Udon doesn't support
    /// delegates (Action, Func, etc.), this transformer converts them to CECallback structs
    /// that use SendCustomEvent under the hood.
    ///
    /// Transformations:
    /// - Action as a declared type (field, local, parameter, property, method return type, type
    ///   argument) → CECallback
    /// - () => Method(), () => { Method(); } and delegate { Method(); } → new CECallback(this, "Method")
    /// - new Action(Method) → new CECallback(this, "Method")
    ///
    /// The string is the name U# exports the method under, which is the written name for a public
    /// parameterless method and the Udon event name for a built-in event (Interact → "_interact").
    /// </summary>
    /// <remarks>
    /// This transformer has a very early priority (3) to ensure Action types are converted
    /// before other optimizers process the code.
    ///
    /// Limitations:
    /// - Only supports parameterless Action (not Action&lt;T&gt;)
    /// - Lambda expressions must be a single parameterless call to a method of the same behaviour
    /// - Does not support closures or captured variables
    /// - An Action has to stay an Action: invoked with <c>.Invoke()</c>, stored, passed and returned,
    ///   but not compared, combined, null-checked, boxed or handed to an API that is not U# source
    ///
    /// The type rewrite and the value rewrites are decided together, for the whole compilation, before
    /// anything is rewritten. They have to agree: CECallback is a struct, so a field whose type became
    /// CECallback while its initializer stayed a lambda, a method group or <c>null</c> is a raw
    /// CS0029/CS1503/CS0037 on a line the author never wrote CECallback into. When any System.Action
    /// usage in a file is a shape CE cannot convert, the file keeps System.Action exactly as written and
    /// U# rejects the delegate in its own terms instead.
    ///
    /// The agreement spans files. A file that declares an Action member and every file that uses, overrides
    /// or implements that member are converted together or not at all, so 'timer.Register(() => Done())' is
    /// rewritten exactly when 'Register(Action)' is. See <see cref="ConversionPlan"/>.
    ///
    /// This pass sees every file U# compiles, which includes plain MonoBehaviours and static utilities in
    /// Assembly-CSharp that U# itself never binds. Nothing here may turn such a file into a compile
    /// error: a delegate that cannot become a callback because there is no behaviour to raise the event
    /// on leaves its file untouched.
    /// </remarks>
    internal class ActionToCallbackTransformer : ICompileTimeOptimizer
    {
        private const string ActionIdentifier = "Action";

        /// <summary>
        /// Written out in full at every use instead of being imported. A using directive appended to the
        /// file moved every line below it down by one, and the bare name could bind to a user type that
        /// happens to be called CECallback.
        /// </summary>
        private const string CallbackTypeName = "global::UdonSharp.CE.Core.CECallback";

        /// <summary>
        /// One plan per pass. The runner hands this transformer one tree at a time, but every tree of the
        /// pass shares the compilation the semantic models were created from, so the compilation is the
        /// key the whole-compilation decision is stored under.
        /// </summary>
        private static readonly ConditionalWeakTable<Compilation, ConversionPlan> Plans =
            new ConditionalWeakTable<Compilation, ConversionPlan>();

        public string OptimizerId => "CEOPT007";
        public string OptimizerName => "Action to CECallback Transformer";
        public string Description => "Transforms Action delegate usage into Udon-compatible CECallback.";
        public bool IsEnabledByDefault => true;
        public int Priority => 3; // Very early - must run before other transforms

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            SemanticModel semanticModel = context.GetSemanticModel(tree);
            if (semanticModel == null)
                return tree;

            Compilation compilation = semanticModel.Compilation;

            if (!Plans.TryGetValue(compilation, out ConversionPlan plan))
            {
                plan = ConversionPlan.Build(compilation, context);
                Plans.Add(compilation, plan);
            }

            // Raised from the file the target is written in, so the runner attaches the right path.
            if (plan.TryGetInvalidTarget(tree, out CodeGenerationException invalidTarget))
                throw invalidTarget;

            if (!plan.ShouldConvert(tree))
                return tree;

            var rewriter = new ActionTransformRewriter(context, new ActionConversionPolicy(semanticModel));
            SyntaxNode newRoot = rewriter.Visit(tree.GetRoot());

            return rewriter.ChangesMade
                ? tree.WithRootAndOptions(newRoot, tree.Options)
                : tree;
        }

        private static bool MentionsAction(SyntaxNode root)
        {
            foreach (SyntaxToken token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken) && token.ValueText == ActionIdentifier)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether a file that never spells 'Action' can still take part in a conversion.
        ///
        /// It can in two ways: a lambda or anonymous method is target-typed to an Action declared in
        /// another file ('timer.Register(() => Done())'), or the file reads such a member by name
        /// ('timer.Handler.Invoke()', 'if (timer.Handler != null)'). Scanning for the token 'Action' alone
        /// skipped both, so the declaring file was converted and the caller was not.
        /// </summary>
        private static bool MayReachActionMembers(SyntaxNode root, HashSet<string> memberNames)
        {
            foreach (SyntaxToken token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.DelegateKeyword))
                    return true;

                if (token.IsKind(SyntaxKind.EqualsGreaterThanToken) && token.Parent is AnonymousFunctionExpressionSyntax)
                    return true;

                if (token.IsKind(SyntaxKind.IdentifierToken) && memberNames.Contains(token.ValueText))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Checks if a node is inside a type or method marked with [CEPreserveAction].
        /// </summary>
        private static bool IsInPreserveActionScope(SyntaxNode node)
        {
            var current = node.Parent;
            while (current != null)
            {
                // Check type declarations (class, struct)
                if (current is TypeDeclarationSyntax typeDecl)
                {
                    if (HasPreserveActionAttribute(typeDecl.AttributeLists))
                        return true;
                }
                // Check method declarations
                else if (current is MethodDeclarationSyntax methodDecl)
                {
                    if (HasPreserveActionAttribute(methodDecl.AttributeLists))
                        return true;
                }
                current = current.Parent;
            }
            return false;
        }

        /// <summary>
        /// Checks if a declaration is itself marked with [CEPreserveAction] or sits inside something that
        /// is. A marked method is the scope, not merely inside one.
        /// </summary>
        private static bool IsPreservedDeclaration(SyntaxNode declaration)
        {
            if (declaration is TypeDeclarationSyntax typeDecl && HasPreserveActionAttribute(typeDecl.AttributeLists))
                return true;

            if (declaration is MethodDeclarationSyntax methodDecl && HasPreserveActionAttribute(methodDecl.AttributeLists))
                return true;

            return IsInPreserveActionScope(declaration);
        }

        /// <summary>
        /// Checks if an attribute list contains [CEPreserveAction].
        /// </summary>
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

        /// <summary>
        /// The whole-compilation answer to "which files are rewritten".
        ///
        /// A file used to be judged on its own, and only if it spelled 'Action'. Both halves let the type
        /// rewrite and the value rewrite disagree across a file boundary: 'Register(Action)' became
        /// 'Register(CECallback)' while the caller in the next file still passed a lambda (CS1660), or a
        /// method group, or compared the member against null. So every file that takes part is examined
        /// first, files that share an Action member are linked, and a group of linked files is converted
        /// only when every file in it is convertible.
        /// </summary>
        private sealed class ConversionPlan
        {
            private readonly HashSet<SyntaxTree> _convertible = new HashSet<SyntaxTree>();
            private readonly Dictionary<SyntaxTree, CodeGenerationException> _invalidTargets =
                new Dictionary<SyntaxTree, CodeGenerationException>();

            public bool ShouldConvert(SyntaxTree tree)
            {
                return _convertible.Contains(tree);
            }

            public bool TryGetInvalidTarget(SyntaxTree tree, out CodeGenerationException invalidTarget)
            {
                return _invalidTargets.TryGetValue(tree, out invalidTarget);
            }

            public static ConversionPlan Build(Compilation compilation, OptimizationContext context)
            {
                var plan = new ConversionPlan();
                var policies = new Dictionary<SyntaxTree, ActionConversionPolicy>();
                var memberNames = new HashSet<string>(StringComparer.Ordinal);
                var pending = new Queue<SyntaxTree>();

                // Cheap token scan first. Binding every expression in a file that never names Action costs
                // the whole project a semantic query per node for nothing.
                foreach (SyntaxTree tree in compilation.SyntaxTrees)
                {
                    if (MentionsAction(tree.GetRoot()))
                        pending.Enqueue(tree);
                }

                Examine(pending, policies, memberNames, compilation, context);

                // Only a declared Action member can pull in a file that does not spell 'Action' itself, so
                // a project with none pays for no second scan.
                if (memberNames.Count > 0)
                {
                    foreach (SyntaxTree tree in compilation.SyntaxTrees)
                    {
                        if (!policies.ContainsKey(tree) && MayReachActionMembers(tree.GetRoot(), memberNames))
                            pending.Enqueue(tree);
                    }

                    Examine(pending, policies, memberNames, compilation, context);
                }

                // One file that cannot be converted keeps every file it shares an Action member with as
                // written, in whichever direction the link was found.
                var neighbours = new Dictionary<SyntaxTree, List<SyntaxTree>>();
                var unsupported = new List<SyntaxTree>();
                var unreachableTargets = new List<SyntaxTree>();

                foreach (KeyValuePair<SyntaxTree, ActionConversionPolicy> entry in policies)
                {
                    foreach (SyntaxTree linked in entry.Value.LinkedTrees)
                    {
                        AddNeighbour(neighbours, entry.Key, linked);
                        AddNeighbour(neighbours, linked, entry.Key);
                    }

                    if (!entry.Value.Supported)
                        unsupported.Add(entry.Key);
                    else if (entry.Value.InvalidTarget != null)
                        unreachableTargets.Add(entry.Key);
                }

                // A file hears that its callback target is unreachable (CE0062) only when that target is all
                // that stops it from being converted. A file CE leaves as written for another reason creates
                // no callback at all, so a diagnostic saying the callback "would never fire" would be false
                // there -- U# reports the standing lambda itself. Deciding this on the whole set, rather than
                // at whichever usage the walk reached first, keeps the outcome independent of member order.
                HashSet<SyntaxTree> blockedRegardless = CloseOverLinks(unsupported, neighbours);

                foreach (SyntaxTree tree in unreachableTargets)
                {
                    if (!blockedRegardless.Contains(tree))
                        plan._invalidTargets[tree] = policies[tree].InvalidTarget;
                }

                HashSet<SyntaxTree> blocked = CloseOverLinks(unsupported.Concat(unreachableTargets), neighbours);

                foreach (SyntaxTree tree in policies.Keys)
                {
                    if (!blocked.Contains(tree))
                        plan._convertible.Add(tree);
                }

                return plan;
            }

            /// <summary>The given files plus every file reachable from them through shared Action members.</summary>
            private static HashSet<SyntaxTree> CloseOverLinks(
                IEnumerable<SyntaxTree> seeds,
                Dictionary<SyntaxTree, List<SyntaxTree>> neighbours)
            {
                var closed = new HashSet<SyntaxTree>();
                var frontier = new Queue<SyntaxTree>();

                foreach (SyntaxTree seed in seeds)
                {
                    if (closed.Add(seed))
                        frontier.Enqueue(seed);
                }

                while (frontier.Count > 0)
                {
                    if (!neighbours.TryGetValue(frontier.Dequeue(), out List<SyntaxTree> linkedTrees))
                        continue;

                    foreach (SyntaxTree linked in linkedTrees)
                    {
                        if (closed.Add(linked))
                            frontier.Enqueue(linked);
                    }
                }

                return closed;
            }

            private static void Examine(
                Queue<SyntaxTree> pending,
                Dictionary<SyntaxTree, ActionConversionPolicy> policies,
                HashSet<string> memberNames,
                Compilation compilation,
                OptimizationContext context)
            {
                while (pending.Count > 0)
                {
                    SyntaxTree tree = pending.Dequeue();

                    if (policies.ContainsKey(tree))
                        continue;

                    SemanticModel semanticModel = context.GetSemanticModel(tree);

                    // The plan is only meaningful if every file is judged against the same compilation.
                    if (semanticModel == null || semanticModel.Compilation != compilation)
                        semanticModel = compilation.GetSemanticModel(tree, ignoreAccessibility: true);

                    var policy = new ActionConversionPolicy(semanticModel);
                    policy.Analyze(tree.GetRoot());
                    policies.Add(tree, policy);

                    memberNames.UnionWith(policy.DeclaredMemberNames);

                    foreach (SyntaxTree linked in policy.LinkedTrees)
                    {
                        if (!policies.ContainsKey(linked))
                            pending.Enqueue(linked);
                    }
                }
            }

            private static void AddNeighbour(Dictionary<SyntaxTree, List<SyntaxTree>> neighbours, SyntaxTree from, SyntaxTree to)
            {
                if (!neighbours.TryGetValue(from, out List<SyntaxTree> list))
                {
                    list = new List<SyntaxTree>();
                    neighbours.Add(from, list);
                }

                list.Add(to);
            }
        }

        /// <summary>
        /// The single place that decides what CE will and will not turn into a CECallback.
        ///
        /// Both the whole-file precheck and the rewriter ask this object, so the type rewrite and the value
        /// rewrites are answers to the same question and cannot drift apart.
        /// </summary>
        private sealed class ActionConversionPolicy
        {
            private readonly SemanticModel _semanticModel;

            public ActionConversionPolicy(SemanticModel semanticModel)
            {
                _semanticModel = semanticModel;
            }

            /// <summary>
            /// Whether every System.Action usage in the analyzed file is one CE can rewrite -- leaving aside
            /// a callback whose target is unreachable, which <see cref="InvalidTarget"/> carries instead. One
            /// unsupported usage disables the whole file, because a half-applied rewrite does not compile:
            /// the alternative is a type error inside user code that names a type the author never used.
            /// </summary>
            public bool Supported { get; private set; } = true;

            /// <summary>
            /// The first delegate in the file that is the right shape for a callback but names a target
            /// SendCustomEvent cannot reach. It keeps the file as written just as an unsupported usage
            /// does. Kept rather than thrown where it is found: the file is examined as part of the
            /// whole-compilation plan, usually while a different file is being rewritten, the diagnostic
            /// has to be raised from its own file, and only the plan knows whether it applies at all.
            /// </summary>
            public CodeGenerationException InvalidTarget { get; private set; }

            /// <summary>
            /// Other files that declare an Action member this file uses, overrides or implements. They are
            /// converted with this file or not at all.
            /// </summary>
            public HashSet<SyntaxTree> LinkedTrees { get; } = new HashSet<SyntaxTree>();

            /// <summary>
            /// Names under which other files can reach an Action declared in this one: its Action fields
            /// and properties, and the methods, constructors and delegates with an Action in their
            /// signature.
            /// </summary>
            public HashSet<string> DeclaredMemberNames { get; } = new HashSet<string>(StringComparer.Ordinal);

            /// <summary>
            /// Examines every expression of the file. Nothing stops at the first unsupported usage: which
            /// usage comes first is an accident of member order, and the verdict -- including whether an
            /// invalid callback target is reported -- must not depend on it.
            /// </summary>
            public void Analyze(SyntaxNode root)
            {
                if (_semanticModel == null)
                {
                    Supported = false;
                    return;
                }

                foreach (SyntaxNode node in root.DescendantNodes())
                {
                    if (node is ExpressionSyntax expression && !IsInPreserveActionScope(expression))
                        Examine(expression);
                }
            }

            private void Examine(ExpressionSyntax expression)
            {
                // 'Action' written as a type: there is no value here, only a declaration whose type the
                // rewriter will change, so all that matters is whether that position survives the change.
                if (expression is TypeSyntax typeSyntax && IsActionTypeReference(typeSyntax))
                {
                    if (IsRewritableTypePosition(typeSyntax))
                    {
                        NoteDeclaredMember(typeSyntax);
                        LinkInheritedDeclarations(typeSyntax);
                    }
                    else
                    {
                        Supported = false;
                    }

                    return;
                }

                ExamineMemberReference(expression);

                TypeInfo typeInfo = _semanticModel.GetTypeInfo(expression);
                bool convertedToAction = IsSystemAction(typeInfo.ConvertedType);
                bool isActionValue = IsSystemAction(typeInfo.Type);

                if (!convertedToAction && !isActionValue)
                    return;

                if (expression is ParenthesizedLambdaExpressionSyntax ||
                    expression is AnonymousMethodExpressionSyntax ||
                    IsActionCreation(expression))
                {
                    if (!TryGetCallbackMethodName(expression, out _, out CodeGenerationException invalidTarget))
                    {
                        if (invalidTarget == null)
                            Supported = false;
                        else if (InvalidTarget == null)
                            InvalidTarget = invalidTarget;
                    }

                    // A lambda has no type of its own to check further; 'new Action(M)' is also a value.
                    if (!isActionValue)
                        return;
                }
                else if (!isActionValue)
                {
                    // Converted to System.Action without being one: a method group, 'null', 'default', a
                    // lambda with parameters. CE emits no CECallback for any of these, and CECallback is a
                    // struct so even 'Action handler = null;' stops compiling once the type is rewritten.
                    // The one exception is the method group inside 'new Action(Method)', which is judged
                    // as part of that creation -- rejecting it here made the documented conversion
                    // unreachable.
                    if (!IsDelegateCreationArgument(expression))
                        Supported = false;

                    return;
                }

                // An Action that is converted to anything else stops being one the rewrite can follow:
                // 'object o = handler' and 'ReferenceEquals(handler, null)' box the struct, so a null test
                // that was true turns silently false, and 'Delegate d = handler' is a raw CS0029.
                if (typeInfo.ConvertedType != null && !IsSystemAction(typeInfo.ConvertedType))
                    Supported = false;

                if (!IsSupportedActionValueUse(expression))
                    Supported = false;
            }

            /// <summary>
            /// Follows a member reference to its declaration.
            ///
            /// A member whose signature carries System.Action is rewritten only where it is declared in
            /// convertible U# source. One declared anywhere else keeps the delegate type -- a metadata API
            /// such as <c>Task.Run(Action)</c>, or source under [CEPreserveAction] -- so a file that uses
            /// it cannot have its own Actions turned into structs: the value would no longer fit
            /// (CS1503). One declared in another file ties the two files together.
            /// </summary>
            private void ExamineMemberReference(ExpressionSyntax expression)
            {
                if (!(expression is SimpleNameSyntax ||
                      expression is ObjectCreationExpressionSyntax ||
                      expression is ElementAccessExpressionSyntax))
                {
                    return;
                }

                ISymbol symbol = _semanticModel.GetSymbolInfo(expression).Symbol;

                if (symbol is IMethodSymbol method)
                    symbol = method.ReducedFrom ?? method;

                symbol = symbol?.OriginalDefinition;

                if (symbol == null || !CarriesAction(symbol))
                    return;

                LinkDeclaration(symbol);
            }

            /// <summary>
            /// An override or an interface implementation has to keep the signature of the member it
            /// overrides or implements, and neither declaration names the other through an expression.
            /// Converting 'override Action GetHandler()' while the virtual in another file stayed
            /// System.Action was CS0508, and converting the virtual alone left the override with nothing to
            /// override (CS0115) -- so the two files are tied together like a caller and its callee.
            /// </summary>
            private void LinkInheritedDeclarations(TypeSyntax type)
            {
                SyntaxNode declaration = GetDeclarationOfType(type);

                if (declaration is ParameterSyntax parameter)
                    declaration = parameter.Parent?.Parent;

                if (!(declaration is MethodDeclarationSyntax || declaration is PropertyDeclarationSyntax))
                    return;

                ISymbol member = _semanticModel.GetDeclaredSymbol(declaration);
                if (member == null)
                    return;

                switch (member)
                {
                    case IMethodSymbol method:
                        if (method.OverriddenMethod != null)
                            LinkDeclaration(method.OverriddenMethod.OriginalDefinition);

                        foreach (IMethodSymbol implemented in method.ExplicitInterfaceImplementations)
                            LinkDeclaration(implemented.OriginalDefinition);
                        break;
                    case IPropertySymbol property:
                        if (property.OverriddenProperty != null)
                            LinkDeclaration(property.OverriddenProperty.OriginalDefinition);

                        foreach (IPropertySymbol implemented in property.ExplicitInterfaceImplementations)
                            LinkDeclaration(implemented.OriginalDefinition);
                        break;
                }

                INamedTypeSymbol containingType = member.ContainingType;
                if (containingType == null)
                    return;

                foreach (INamedTypeSymbol implementedInterface in containingType.AllInterfaces)
                {
                    foreach (ISymbol interfaceMember in implementedInterface.GetMembers(member.Name))
                    {
                        if (SymbolEqualityComparer.Default.Equals(containingType.FindImplementationForInterfaceMember(interfaceMember), member))
                            LinkDeclaration(interfaceMember.OriginalDefinition);
                    }
                }
            }

            /// <summary>
            /// Ties this file to wherever <paramref name="symbol"/> is declared. A declaration CE cannot
            /// rewrite with it -- metadata, or source under [CEPreserveAction] -- keeps this file as written.
            /// </summary>
            private void LinkDeclaration(ISymbol symbol)
            {
                if (symbol.DeclaringSyntaxReferences.IsDefaultOrEmpty)
                {
                    Supported = false;
                    return;
                }

                foreach (SyntaxReference reference in symbol.DeclaringSyntaxReferences)
                {
                    if (IsPreservedDeclaration(reference.GetSyntax()))
                        Supported = false;
                    else if (reference.SyntaxTree != _semanticModel.SyntaxTree)
                        LinkedTrees.Add(reference.SyntaxTree);
                }
            }

            /// <summary>
            /// Whether a member hands a System.Action in or out. The unconstructed definition is asked, so
            /// <c>List&lt;Action&gt;.Add(T)</c> does not count: its parameter follows the type argument,
            /// which is rewritten wherever it is written.
            /// </summary>
            private static bool CarriesAction(ISymbol member)
            {
                switch (member)
                {
                    case IFieldSymbol field:
                        return ContainsAction(field.Type);
                    case IEventSymbol eventSymbol:
                        return ContainsAction(eventSymbol.Type);
                    case IPropertySymbol property:
                        return ContainsAction(property.Type) || property.Parameters.Any(parameter => ContainsAction(parameter.Type));
                    case IMethodSymbol method:
                        return ContainsAction(method.ReturnType) || method.Parameters.Any(parameter => ContainsAction(parameter.Type));
                    default:
                        return false;
                }
            }

            private static bool ContainsAction(ITypeSymbol type)
            {
                switch (type)
                {
                    case IArrayTypeSymbol array:
                        return ContainsAction(array.ElementType);
                    case INamedTypeSymbol named:
                        return IsSystemAction(named) || named.TypeArguments.Any(ContainsAction);
                    default:
                        return false;
                }
            }

            /// <summary>
            /// Records the name other files would use to reach the declaration this 'Action' belongs to.
            /// Locals, lambda parameters and local functions are not recorded: nothing outside the file
            /// can name them.
            /// </summary>
            private void NoteDeclaredMember(TypeSyntax type)
            {
                switch (GetDeclarationOfType(type))
                {
                    case VariableDeclarationSyntax declaration when declaration.Parent is BaseFieldDeclarationSyntax:
                        foreach (VariableDeclaratorSyntax variable in declaration.Variables)
                            DeclaredMemberNames.Add(variable.Identifier.ValueText);
                        break;
                    case PropertyDeclarationSyntax property:
                        DeclaredMemberNames.Add(property.Identifier.ValueText);
                        break;
                    case MethodDeclarationSyntax method:
                        DeclaredMemberNames.Add(method.Identifier.ValueText);
                        break;
                    case ParameterSyntax parameter:
                        switch (parameter.Parent?.Parent)
                        {
                            case MethodDeclarationSyntax ownerMethod:
                                DeclaredMemberNames.Add(ownerMethod.Identifier.ValueText);
                                break;
                            case ConstructorDeclarationSyntax ownerConstructor:
                                DeclaredMemberNames.Add(ownerConstructor.Identifier.ValueText);
                                break;
                            case DelegateDeclarationSyntax ownerDelegate:
                                DeclaredMemberNames.Add(ownerDelegate.Identifier.ValueText);
                                break;
                        }
                        break;
                }
            }

            /// <summary>
            /// An existing Action value flows through the rewrite unchanged -- it just becomes a CECallback
            /// value -- but only in the shapes a plain struct supports. Delegate combination, null-conditional
            /// invocation, comparison against null and direct <c>handler()</c> invocation all have no
            /// CECallback equivalent, and the transformer deliberately never rewrote invocations.
            /// </summary>
            private static bool IsSupportedActionValueUse(ExpressionSyntax expression)
            {
                switch (expression)
                {
                    case IdentifierNameSyntax _:
                    case MemberAccessExpressionSyntax _:
                    case ElementAccessExpressionSyntax _:
                    case InvocationExpressionSyntax _:
                    case ParenthesizedExpressionSyntax _:
                    case ConditionalExpressionSyntax _:
                    case ObjectCreationExpressionSyntax _:
                        break;
                    case AssignmentExpressionSyntax simpleAssignment
                        when simpleAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                        break;
                    default:
                        return false;
                }

                switch (expression.Parent)
                {
                    // 'handler + other', 'handler == null', 'handler ?? fallback'
                    case BinaryExpressionSyntax _:
                        return false;

                    // 'handler += Method'
                    case AssignmentExpressionSyntax compoundAssignment
                        when !compoundAssignment.IsKind(SyntaxKind.SimpleAssignmentExpression):
                        return false;

                    // 'handler?.Invoke()' -- null-conditional access on a struct is CS0023
                    case ConditionalAccessExpressionSyntax conditionalAccess
                        when conditionalAccess.Expression == expression:
                        return false;

                    // 'handler()' -- CECallback has no invocation form, only .Invoke()
                    case InvocationExpressionSyntax invocation when invocation.Expression == expression:
                        return false;

                    // 'handler.Method', 'handler.GetInvocationList()' -- Invoke() is the one member a
                    // delegate and a CECallback have in common with the same meaning
                    case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression == expression:
                        return memberAccess.Name.Identifier.ValueText == "Invoke";

                    default:
                        return true;
                }
            }

            /// <summary>
            /// Whether an 'Action' type reference sits somewhere the CECallback substitution keeps meaning
            /// what the author wrote.
            /// </summary>
            private static bool IsRewritableTypePosition(TypeSyntax type)
            {
                switch (GetDeclarationOfType(type))
                {
                    // 'Action handler;' / 'private Action _handler;' -- but not 'event Action Done;', which
                    // has to stay a delegate type (CS0066)
                    case VariableDeclarationSyntax declaration:
                        return !(declaration.Parent is EventFieldDeclarationSyntax);
                    case ParameterSyntax _:                // 'void Register(Action callback)'
                    case PropertyDeclarationSyntax _:      // 'public Action Handler { get; set; }'
                    case MethodDeclarationSyntax _:        // 'Action GetHandler()'
                    case ObjectCreationExpressionSyntax _: // 'new Action(Method)', re-checked as a value
                        return true;
                    default:
                        // typeof(Action), (Action)x, x as Action, default(Action), Action?, a local function
                        // return type. CE has no substitution that preserves these, so the file keeps
                        // System.Action and U# reports the unsupported delegate itself.
                        return false;
                }
            }

            /// <summary>
            /// Unwraps the constructions that only carry an element or argument type: 'System.Action',
            /// 'Action[]', 'List&lt;Action&gt;'. The declaration underneath is what decides.
            /// </summary>
            private static SyntaxNode GetDeclarationOfType(TypeSyntax type)
            {
                SyntaxNode parent = type.Parent;

                while (parent is QualifiedNameSyntax ||
                       parent is AliasQualifiedNameSyntax ||
                       parent is ArrayTypeSyntax ||
                       parent is TypeArgumentListSyntax ||
                       parent is GenericNameSyntax)
                {
                    parent = parent.Parent;
                }

                return parent;
            }

            /// <summary>
            /// Whether a type syntax node names <c>System.Action</c>. Answered only by the binder: a written
            /// name match would let the type rewrite fire where the value rewrite -- which is entirely
            /// semantic -- would not, which is exactly the disagreement this class exists to prevent.
            /// </summary>
            public bool IsActionTypeReference(TypeSyntax type)
            {
                if (_semanticModel == null || type == null)
                    return false;

                return IsSystemAction(_semanticModel.GetSymbolInfo(type).Symbol as ITypeSymbol);
            }

            private bool IsActionCreation(ExpressionSyntax expression)
            {
                return expression is ObjectCreationExpressionSyntax creation && IsActionTypeReference(creation.Type);
            }

            /// <summary>The 'Method' of 'new Action(Method)'.</summary>
            private bool IsDelegateCreationArgument(ExpressionSyntax expression)
            {
                return expression.Parent is ArgumentSyntax argument &&
                       argument.Parent is ArgumentListSyntax argumentList &&
                       argumentList.Parent is ObjectCreationExpressionSyntax creation &&
                       IsActionTypeReference(creation.Type);
            }

            /// <summary>
            /// Whether the delegate expression's target type is <c>System.Action</c>. An unresolved target
            /// type answers "no": a rewrite performed on a guess produces a type error at the use site.
            /// </summary>
            private bool IsActionTargetType(ExpressionSyntax delegateExpression)
            {
                if (_semanticModel == null)
                    return false;

                return IsSystemAction(_semanticModel.GetTypeInfo(delegateExpression).ConvertedType);
            }

            private static bool IsSystemAction(ITypeSymbol type)
            {
                return type is INamedTypeSymbol named &&
                       named.TypeKind == TypeKind.Delegate &&
                       named.Arity == 0 &&
                       named.ToDisplayString() == "System.Action";
            }

            public bool TryGetCallbackMethodName(ExpressionSyntax delegateExpression, out string methodName)
            {
                return TryGetCallbackMethodName(delegateExpression, out methodName, out _);
            }

            /// <summary>
            /// Whether a lambda, an anonymous method or a <c>new Action(Method)</c> may become
            /// <c>new CECallback(this, "name")</c>, and the name to emit.
            ///
            /// Matched on the ORIGINAL node, before the type identifier is rewritten. The old match for
            /// creations ran on the rewritten node and accepted any 'new CECallback(x)' with a single
            /// identifier argument, so a hand-written CECallback construction was rewritten into a form the
            /// author never asked for.
            /// </summary>
            /// <param name="invalidTarget">
            /// Set when the expression is exactly the shape CE converts but its target can never be raised
            /// as an event. Null when the expression is simply not that shape.
            /// </param>
            public bool TryGetCallbackMethodName(
                ExpressionSyntax delegateExpression,
                out string methodName,
                out CodeGenerationException invalidTarget)
            {
                methodName = null;
                invalidTarget = null;

                string writtenName;
                IMethodSymbol target;

                switch (delegateExpression)
                {
                    case ParenthesizedLambdaExpressionSyntax lambda:
                    {
                        if (lambda.ParameterList.Parameters.Count != 0 || !IsActionTargetType(lambda))
                            return false;

                        if (!TryGetParameterlessMethodName(lambda.ExpressionBody, out writtenName, out InvocationExpressionSyntax invocation) &&
                            !TryGetBlockSingleInvocationMethodName(lambda.Block, out writtenName, out invocation))
                        {
                            return false;
                        }

                        target = ResolveInvokedMethod(invocation);
                        break;
                    }
                    case AnonymousMethodExpressionSyntax anonymousMethod:
                    {
                        if (anonymousMethod.ParameterList != null && anonymousMethod.ParameterList.Parameters.Count != 0)
                            return false;

                        if (anonymousMethod.Block == null || !IsActionTargetType(anonymousMethod))
                            return false;

                        if (!TryGetBlockSingleInvocationMethodName(anonymousMethod.Block, out writtenName, out InvocationExpressionSyntax invocation))
                            return false;

                        target = ResolveInvokedMethod(invocation);
                        break;
                    }
                    case ObjectCreationExpressionSyntax creation:
                    {
                        if (!IsActionTypeReference(creation.Type))
                            return false;

                        if (creation.ArgumentList?.Arguments.Count != 1 ||
                            !(creation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax methodRef))
                        {
                            return false;
                        }

                        writtenName = methodRef.Identifier.ValueText;
                        target = ResolveMethodGroup(methodRef);
                        break;
                    }
                    default:
                        return false;
                }

                return TryResolveCallbackTarget(delegateExpression, writtenName, target, out methodName, out invalidTarget);
            }

            /// <summary>
            /// Also yields the invocation node so the caller can ask the semantic model what the callback
            /// will actually name -- the string handed to SendCustomEvent only resolves for a method U#
            /// exports as an event.
            /// </summary>
            private static bool TryGetParameterlessMethodName(
                ExpressionSyntax expression,
                out string methodName,
                out InvocationExpressionSyntax methodInvocation)
            {
                methodName = null;
                methodInvocation = null;

                if (expression is InvocationExpressionSyntax invocation &&
                    invocation.ArgumentList.Arguments.Count == 0)
                {
                    if (invocation.Expression is IdentifierNameSyntax identifier)
                    {
                        methodName = identifier.Identifier.ValueText;
                        methodInvocation = invocation;
                        return true;
                    }

                    if (invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                        memberAccess.Expression is ThisExpressionSyntax &&
                        memberAccess.Name is IdentifierNameSyntax memberIdentifier)
                    {
                        methodName = memberIdentifier.Identifier.ValueText;
                        methodInvocation = invocation;
                        return true;
                    }
                }

                return false;
            }

            private static bool TryGetBlockSingleInvocationMethodName(
                BlockSyntax block,
                out string methodName,
                out InvocationExpressionSyntax methodInvocation)
            {
                methodName = null;
                methodInvocation = null;

                if (block == null || block.Statements.Count != 1)
                    return false;

                if (block.Statements[0] is ExpressionStatementSyntax exprStmt &&
                    TryGetParameterlessMethodName(exprStmt.Expression, out string found, out InvocationExpressionSyntax foundInvocation))
                {
                    methodName = found;
                    methodInvocation = foundInvocation;
                    return true;
                }

                return false;
            }

            private IMethodSymbol ResolveInvokedMethod(InvocationExpressionSyntax invocation)
            {
                if (_semanticModel == null || invocation == null)
                    return null;

                return _semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;
            }

            /// <summary>
            /// A method group used as a delegate argument binds through a conversion, so the single
            /// candidate is where the symbol shows up rather than <c>SymbolInfo.Symbol</c>.
            /// </summary>
            private IMethodSymbol ResolveMethodGroup(ExpressionSyntax expression)
            {
                if (_semanticModel == null)
                    return null;

                SymbolInfo info = _semanticModel.GetSymbolInfo(expression);

                if (info.Symbol is IMethodSymbol resolved)
                    return resolved;

                return info.CandidateSymbols.Length == 1 ? info.CandidateSymbols[0] as IMethodSymbol : null;
            }

            /// <summary>
            /// Decides whether a delegate expression may become <c>new CECallback(this, "name")</c> and
            /// which name SendCustomEvent will find the target under.
            ///
            /// A callback captures 'this' as the behaviour to raise the event on. In a type that is not an
            /// UdonSharpBehaviour, or in a static member, there is nothing to capture, so the delegate is
            /// left exactly as written. That used to be a CE0061 compile error, which failed the whole U#
            /// build over a plain MonoBehaviour or a static utility in Assembly-CSharp -- code U# never
            /// binds -- and only when the lambda happened to sit above the first unsupported usage in the
            /// file. If the type is one U# does compile, its binder rejects the lambda itself.
            ///
            /// Inside a behaviour the target has to be an event of the compiled program, or the callback
            /// compiles cleanly and then silently never fires. Three kinds of target are not:
            /// a Udon event is exported under its Udon name, so 'Interact' has to be emitted as
            /// "_interact"; a method declared outside U# source (RequestSerialization(), or an Interact()
            /// the behaviour inherits from UdonSharpBehaviour without overriding it) is an extern with no
            /// entry point at all; and a delegate value or a local function is not a method of the program.
            /// The first is translated with the compiler's own table, the other two are CE0062. The extern
            /// check runs before the event table, because an event name alone does not make an entry point.
            /// </summary>
            private bool TryResolveCallbackTarget(
                SyntaxNode site,
                string writtenName,
                IMethodSymbol target,
                out string exportedName,
                out CodeGenerationException invalidTarget)
            {
                exportedName = null;
                invalidTarget = null;

                if (_semanticModel == null)
                    return false;

                TypeDeclarationSyntax containingType = site.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                INamedTypeSymbol containingSymbol = containingType == null
                    ? null
                    : _semanticModel.GetDeclaredSymbol(containingType);

                if (containingSymbol == null || !IsUdonSharpBehaviour(containingSymbol) || IsInStaticContext(site))
                    return false;

                if (target == null)
                    return false;

                if (target.MethodKind != MethodKind.Ordinary)
                {
                    invalidTarget = CreateInvalidTarget(site, writtenName,
                        $"'{writtenName}' is not a method of this behaviour (it is a delegate value or a local " +
                        "function), so the compiled program has no event of that name.",
                        "call it from a public parameterless instance method and use that method as the target.");
                    return false;
                }

                if (target.IsStatic || target.Parameters.Length != 0)
                {
                    invalidTarget = CreateInvalidTarget(site, writtenName,
                        "U# exports only parameterless instance methods as events.",
                        $"declare '{writtenName}' as a public parameterless instance method.");
                    return false;
                }

                if (target.DeclaringSyntaxReferences.IsDefaultOrEmpty)
                {
                    bool overridableEvent = (target.IsVirtual || target.IsAbstract) &&
                                            CompilerUdonInterface.IsUdonEventName(target.Name);

                    invalidTarget = CreateInvalidTarget(site, writtenName,
                        $"'{writtenName}' is declared on '{target.ContainingType?.Name}', outside U# source, so it " +
                        "is an extern call and the compiled program has no event of that name.",
                        overridableEvent
                            ? $"override '{writtenName}' in this behaviour, which makes the program export it as an event."
                            : "wrap the call in a public parameterless method of this behaviour and use that method as the target.");
                    return false;
                }

                // Exported for any accessibility, but under the Udon name rather than the written one.
                if (CompilerUdonInterface.IsUdonEventName(target.Name))
                {
                    exportedName = CompilerUdonInterface.GetUdonEventName(target.Name);
                    return true;
                }

                if (target.DeclaredAccessibility != Accessibility.Public)
                {
                    invalidTarget = CreateInvalidTarget(site, writtenName,
                        "U# exports a method under its own name only when it is public.",
                        $"declare '{writtenName}' as a public parameterless instance method.");
                    return false;
                }

                exportedName = target.Name;
                return true;
            }

            private static CodeGenerationException CreateInvalidTarget(SyntaxNode site, string writtenName, string reason, string fix)
            {
                return new CodeGenerationException(
                    "CE0062",
                    site.GetLocation(),
                    $"'{writtenName}' cannot be the target of a CECallback. Invocation goes through " +
                    $"SendCustomEvent, which only reaches events of the compiled program. {reason} The callback " +
                    $"would compile and then never fire. Fix: {fix}");
            }

            /// <summary>
            /// A 'this' capture is illegal in a static method, a static constructor, a static property or a
            /// static field initializer. The walk stops at the enclosing type so a nested non-static local
            /// function inside a static method still reports static.
            /// </summary>
            private static bool IsInStaticContext(SyntaxNode node)
            {
                for (SyntaxNode current = node; current != null; current = current.Parent)
                {
                    switch (current)
                    {
                        case MethodDeclarationSyntax method:
                            return method.Modifiers.Any(SyntaxKind.StaticKeyword);
                        case ConstructorDeclarationSyntax constructor:
                            return constructor.Modifiers.Any(SyntaxKind.StaticKeyword);
                        case PropertyDeclarationSyntax property:
                            return property.Modifiers.Any(SyntaxKind.StaticKeyword);
                        case FieldDeclarationSyntax field:
                            return field.Modifiers.Any(SyntaxKind.StaticKeyword);
                        case LocalFunctionStatementSyntax localFunction when localFunction.Modifiers.Any(SyntaxKind.StaticKeyword):
                            return true;
                        case TypeDeclarationSyntax _:
                            return false;
                    }
                }

                return false;
            }

            private static bool IsUdonSharpBehaviour(INamedTypeSymbol typeSymbol)
            {
                for (INamedTypeSymbol current = typeSymbol; current != null; current = current.BaseType)
                {
                    if (current.ToDisplayString() == "UdonSharp.UdonSharpBehaviour")
                        return true;
                }

                return false;
            }
        }

        private class ActionTransformRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly ActionConversionPolicy _policy;
            public bool ChangesMade { get; private set; }

            public ActionTransformRewriter(OptimizationContext context, ActionConversionPolicy policy)
            {
                _context = context;
                _policy = policy;
            }

            /// <summary>
            /// Transforms Action type references to CECallback. Every position reached here already passed
            /// the whole-compilation precheck, so the declaration's values are ones CE also rewrites.
            /// </summary>
            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                if (_policy.IsActionTypeReference(node) && !IsInPreserveActionScope(node))
                {
                    RecordTransformation(node, "Type reference: Action → CECallback");
                    ChangesMade = true;
                    return SyntaxFactory.ParseTypeName(CallbackTypeName)
                        .WithTriviaFrom(node);
                }

                return base.VisitIdentifierName(node);
            }

            /// <summary>
            /// Transforms qualified System.Action type references.
            /// </summary>
            public override SyntaxNode VisitQualifiedName(QualifiedNameSyntax node)
            {
                if (_policy.IsActionTypeReference(node) && !IsInPreserveActionScope(node))
                {
                    RecordTransformation(node, "Type reference: System.Action → CECallback");
                    ChangesMade = true;
                    return SyntaxFactory.ParseTypeName(CallbackTypeName)
                        .WithTriviaFrom(node);
                }

                return base.VisitQualifiedName(node);
            }

            /// <summary>
            /// Visit invocation expressions - we DON'T transform these because we can't reliably
            /// distinguish between method calls and delegate invocations without semantic analysis.
            /// The CECallback.Invoke() must be called explicitly by the user or by CEWorld internals.
            /// </summary>
            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // We intentionally DO NOT transform invocations like "callback()" to "callback.Invoke()"
                // because we cannot distinguish between:
                // - SortSystems() - a method call
                // - _callback() - a delegate invocation
                //
                // A file that invokes an Action that way is rejected by the precheck instead, so the type
                // rewrite never leaves behind a struct being called like a delegate.
                return base.VisitInvocationExpression(node);
            }

            /// <summary>
            /// Transforms new Action(Method) to new CECallback(this, "Method").
            /// </summary>
            public override SyntaxNode VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                if (!IsInPreserveActionScope(node) &&
                    _policy.TryGetCallbackMethodName(node, out string methodName))
                {
                    RecordTransformation(node, $"Constructor: new Action(...) → new CECallback(this, \"{methodName}\")");
                    ChangesMade = true;
                    return CreateCallbackCreation(node, methodName);
                }

                return base.VisitObjectCreationExpression(node);
            }

            /// <summary>
            /// Transforms lambda expressions () => Method() to CECallback creation.
            /// </summary>
            public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            {
                if (!IsInPreserveActionScope(node) &&
                    _policy.TryGetCallbackMethodName(node, out string methodName))
                {
                    RecordTransformation(node, node.Block != null
                        ? $"Lambda block: () => {{ ...(); }} → new CECallback(this, \"{methodName}\")"
                        : $"Lambda: () => ...() → new CECallback(this, \"{methodName}\")");
                    ChangesMade = true;

                    return CreateCallbackCreation(node, methodName);
                }

                return base.VisitParenthesizedLambdaExpression(node);
            }

            /// <summary>
            /// Transforms anonymous methods delegate { Method(); } to CECallback creation.
            /// </summary>
            public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
            {
                if (!IsInPreserveActionScope(node) &&
                    _policy.TryGetCallbackMethodName(node, out string methodName))
                {
                    RecordTransformation(node, $"Anonymous method: delegate {{ ...(); }} → new CECallback(this, \"{methodName}\")");
                    ChangesMade = true;
                    return CreateCallbackCreation(node, methodName);
                }

                return base.VisitAnonymousMethodExpression(node);
            }

            /// <summary>
            /// Transforms simple lambda expressions x => Method() to CECallback creation.
            /// This handles the case where the lambda has a body block.
            /// </summary>
            public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
            {
                // We don't transform simple lambdas with parameters as CECallback doesn't support parameters
                return base.VisitSimpleLambdaExpression(node);
            }

            /// <summary>
            /// Builds the callback that stands in for <paramref name="original"/>.
            ///
            /// The method name is always a string literal. 'new Action(Method)' used to be given
            /// nameof(Method) assembled from SyntaxFactory.IdentifierName("nameof"), which is an ordinary
            /// identifier rather than the contextual keyword and binds as a call to a method named
            /// 'nameof' (CS0103) -- and nameof would also yield the written name where the exported one is
            /// needed. The expression is parsed for the same reason: a parsed node carries the token kinds
            /// the binder looks for.
            /// </summary>
            private static ExpressionSyntax CreateCallbackCreation(ExpressionSyntax original, string methodName)
            {
                ExpressionSyntax creation = SyntaxFactory.ParseExpression(
                    $"new {CallbackTypeName}(this, {SyntaxFactory.Literal(methodName).Text})");

                // A block-bodied lambda spans lines the one-line callback does not.
                Microsoft.CodeAnalysis.Text.SourceText text = original.SyntaxTree.GetText();
                SyntaxTriviaList droppedBreaks = RewriteLineBreaks.Create(
                    RewriteLineBreaks.Count(text, original.SpanStart, original.Span.End),
                    RewriteLineBreaks.DetectEndOfLine(text));

                return creation
                    .WithLeadingTrivia(original.GetLeadingTrivia())
                    .WithTrailingTrivia(droppedBreaks.AddRange(original.GetTrailingTrivia()));
            }

            private void RecordTransformation(SyntaxNode node, string description)
            {
                _context.RecordOptimization(
                    "CEOPT007",
                    description,
                    node.GetLocation(),
                    node.ToString(),
                    "");
            }
        }
    }
}
