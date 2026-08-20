using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.Compiler;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Transforms Action delegate usage into Udon-compatible CECallback usage at compile time.
    ///
    /// This optimizer enables UdonSharp scripts to use familiar C# delegate syntax while
    /// generating code that works with Udon's limitations. Since Udon doesn't support
    /// delegates (Action, Func, etc.), this transformer converts them to CECallback structs
    /// that use SendCustomEvent under the hood.
    ///
    /// Transformations:
    /// - Action type → CECallback
    /// - Action field/variable → CECallback field/variable
    /// - new Action(Method) → new CECallback(this, nameof(Method))
    /// - () => Method() lambdas → new CECallback(this, "Method")
    /// </summary>
    /// <remarks>
    /// This transformer has a very early priority (3) to ensure Action types are converted
    /// before other optimizers process the code.
    ///
    /// Limitations:
    /// - Only supports parameterless Action (not Action&lt;T&gt;)
    /// - Lambda expressions must be simple method calls (no complex expressions)
    /// - Does not support closures or captured variables
    ///
    /// The type rewrite and the value rewrites are decided together, for the whole file, before anything
    /// is rewritten. They have to agree: CECallback is a struct, so a field whose type became CECallback
    /// while its initializer stayed a lambda, a method group or <c>null</c> is a raw CS0029/CS1503/CS0037
    /// on a line the author never wrote CECallback into. When any System.Action usage in the file is a
    /// shape CE cannot convert, the file keeps System.Action exactly as written and U# rejects the
    /// delegate in its own terms instead.
    ///
    /// This agreement is file-scoped. A public Action member read from another file is still transformed
    /// per file, so a declaration CE converts in one file and a use it declines in another can still
    /// disagree across the boundary.
    /// </remarks>
    internal class ActionToCallbackTransformer : ICompileTimeOptimizer
    {
        private const string ActionIdentifier = "Action";

        public string OptimizerId => "CEOPT007";
        public string OptimizerName => "Action to CECallback Transformer";
        public string Description => "Transforms Action delegate usage into Udon-compatible CECallback.";
        public bool IsEnabledByDefault => true;
        public int Priority => 3; // Very early - must run before other transforms

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            SyntaxNode root = tree.GetRoot();

            // Cheap token scan first. Binding every expression in a file that never names Action costs the
            // whole project a semantic query per node for nothing.
            if (!MentionsAction(root))
                return tree;

            var policy = new ActionConversionPolicy(context.GetSemanticModel(tree));

            if (!policy.CanTransformTree(root))
                return tree;

            var rewriter = new ActionTransformRewriter(context, policy);
            SyntaxNode newRoot = rewriter.Visit(root);

            if (rewriter.ChangesMade)
            {
                // Ensure the using directive is added
                newRoot = EnsureUsingDirective(newRoot);
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
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
        /// Ensures the UdonSharp.CE.Core using directive is present in the file.
        /// </summary>
        private SyntaxNode EnsureUsingDirective(SyntaxNode root)
        {
            if (root is CompilationUnitSyntax compilationUnit)
            {
                // Check if using already exists
                bool hasUsing = false;
                foreach (var usingDirective in compilationUnit.Usings)
                {
                    if (usingDirective.Name?.ToString() == "UdonSharp.CE.Core")
                    {
                        hasUsing = true;
                        break;
                    }
                }

                if (!hasUsing)
                {
                    var newUsing = SyntaxFactory.UsingDirective(
                        SyntaxFactory.QualifiedName(
                            SyntaxFactory.QualifiedName(
                                SyntaxFactory.IdentifierName("UdonSharp"),
                                SyntaxFactory.IdentifierName("CE")),
                            SyntaxFactory.IdentifierName("Core")))
                        .WithTrailingTrivia(SyntaxFactory.CarriageReturnLineFeed);

                    return compilationUnit.AddUsings(newUsing);
                }
            }

            return root;
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
            /// Whether CE may rewrite System.Action anywhere in this file.
            ///
            /// Every expression is checked before a single node is replaced. One unsupported System.Action
            /// usage disables the whole file, because a half-applied rewrite does not compile: the alternative
            /// is a type error inside user code that names a type the author never used.
            /// </summary>
            public bool CanTransformTree(SyntaxNode root)
            {
                if (_semanticModel == null)
                    return false;

                foreach (SyntaxNode node in root.DescendantNodes())
                {
                    if (node is ExpressionSyntax expression && !IsSupportedExpression(expression))
                        return false;
                }

                return true;
            }

            private bool IsSupportedExpression(ExpressionSyntax expression)
            {
                if (IsInPreserveActionScope(expression))
                    return true;

                // 'Action' written as a type: there is no value here, only a declaration whose type the
                // rewriter will change, so all that matters is whether that position survives the change.
                if (expression is TypeSyntax typeSyntax && IsActionTypeReference(typeSyntax))
                    return IsRewritableTypePosition(typeSyntax);

                TypeInfo typeInfo = _semanticModel.GetTypeInfo(expression);
                bool convertedToAction = IsSystemAction(typeInfo.ConvertedType);
                bool isActionValue = IsSystemAction(typeInfo.Type);

                if (!convertedToAction && !isActionValue)
                    return true;

                switch (expression)
                {
                    case ParenthesizedLambdaExpressionSyntax lambda:
                        return TryGetCallbackMethodName(lambda, out _);
                    case AnonymousMethodExpressionSyntax anonymousMethod:
                        return TryGetCallbackMethodName(anonymousMethod, out _);
                    case ObjectCreationExpressionSyntax creation:
                        return TryGetCallbackMethodName(creation, out _);
                }

                // Converted to System.Action without being one: a method group, 'null', 'default', a lambda
                // with parameters. CE emits no CECallback for any of these, and CECallback is a struct so
                // even 'Action handler = null;' stops compiling once the type is rewritten.
                if (!isActionValue)
                    return false;

                return IsSupportedActionValueUse(expression);
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
                SyntaxNode parent = type.Parent;

                // Unwrap the constructions that only carry an element or argument type: 'System.Action',
                // 'Action[]', 'List<Action>'. The declaration underneath is what decides.
                while (parent is QualifiedNameSyntax ||
                       parent is AliasQualifiedNameSyntax ||
                       parent is ArrayTypeSyntax ||
                       parent is TypeArgumentListSyntax ||
                       parent is GenericNameSyntax)
                {
                    parent = parent.Parent;
                }

                switch (parent)
                {
                    case VariableDeclarationSyntax _:      // 'Action handler;' / 'private Action _handler;'
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

            public bool TryGetCallbackMethodName(ParenthesizedLambdaExpressionSyntax lambda, out string methodName)
            {
                methodName = null;

                if (lambda.ParameterList.Parameters.Count != 0 || !IsActionTargetType(lambda))
                    return false;

                if (TryGetParameterlessMethodName(lambda.ExpressionBody, out methodName, out InvocationExpressionSyntax invocation))
                    return CanEmitCallback(lambda, methodName, ResolveInvokedMethod(invocation));

                if (lambda.Block != null &&
                    TryGetBlockSingleInvocationMethodName(lambda.Block, out methodName, out InvocationExpressionSyntax blockInvocation))
                {
                    return CanEmitCallback(lambda, methodName, ResolveInvokedMethod(blockInvocation));
                }

                return false;
            }

            public bool TryGetCallbackMethodName(AnonymousMethodExpressionSyntax anonymousMethod, out string methodName)
            {
                methodName = null;

                if (anonymousMethod.ParameterList != null && anonymousMethod.ParameterList.Parameters.Count != 0)
                    return false;

                if (anonymousMethod.Block == null || !IsActionTargetType(anonymousMethod))
                    return false;

                if (!TryGetBlockSingleInvocationMethodName(anonymousMethod.Block, out methodName, out InvocationExpressionSyntax invocation))
                    return false;

                return CanEmitCallback(anonymousMethod, methodName, ResolveInvokedMethod(invocation));
            }

            /// <summary>
            /// Matched on the ORIGINAL node, before the type identifier is rewritten.
            ///
            /// The old match ran on the rewritten node and accepted any 'new CECallback(x)' with a single
            /// identifier argument, so a hand-written CECallback construction was rewritten into a form the
            /// author never asked for.
            /// </summary>
            public bool TryGetCallbackMethodName(ObjectCreationExpressionSyntax creation, out string methodName)
            {
                methodName = null;

                if (!IsActionTypeReference(creation.Type))
                    return false;

                if (creation.ArgumentList?.Arguments.Count != 1 ||
                    !(creation.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax methodRef))
                {
                    return false;
                }

                methodName = methodRef.Identifier.ValueText;

                return CanEmitCallback(creation, methodName, ResolveMethodGroup(methodRef));
            }

            /// <summary>
            /// Also yields the invocation node so the caller can ask the semantic model what the callback
            /// will actually name -- the string handed to SendCustomEvent only resolves for a public,
            /// parameterless, instance method.
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
            /// Decides whether a delegate expression may become <c>new CECallback(this, "Method")</c>.
            ///
            /// All three failures below used to be emitted regardless. A static context produced CS0026 on
            /// the 'this' capture; a non-behaviour had no SendCustomEvent to raise the event on; and a
            /// target the program never exports compiled cleanly but could never be found at runtime, so
            /// the callback silently did nothing. Anything the semantic model cannot answer returns false
            /// and the delegate is left as written.
            /// </summary>
            private bool CanEmitCallback(SyntaxNode site, string methodName, IMethodSymbol target)
            {
                if (_semanticModel == null)
                    return false;

                TypeDeclarationSyntax containingType = site.FirstAncestorOrSelf<TypeDeclarationSyntax>();
                INamedTypeSymbol containingSymbol = containingType == null
                    ? null
                    : _semanticModel.GetDeclaredSymbol(containingType);

                if (containingSymbol == null)
                    return false;

                if (!IsUdonSharpBehaviour(containingSymbol))
                {
                    throw new CodeGenerationException(
                        "CE0061",
                        site.GetLocation(),
                        $"CE cannot turn '{site}' into a CECallback: '{containingSymbol.Name}' is not an " +
                        "UdonSharpBehaviour, and a callback needs a behaviour instance to raise the event on. " +
                        "Fix: move this code onto an UdonSharpBehaviour, or mark the type or method with " +
                        "[CEPreserveAction] to keep the delegate exactly as written.");
                }

                if (IsInStaticContext(site))
                {
                    throw new CodeGenerationException(
                        "CE0061",
                        site.GetLocation(),
                        $"CE cannot turn '{site}' into a CECallback from a static member: the callback has to " +
                        "capture 'this' to know which behaviour to raise the event on. " +
                        "Fix: make the enclosing member an instance member, or mark the type or method with " +
                        "[CEPreserveAction] to keep the delegate exactly as written.");
                }

                if (target == null)
                    return false;

                if (target.IsStatic || target.Parameters.Length != 0 || target.DeclaredAccessibility != Accessibility.Public)
                {
                    throw new CodeGenerationException(
                        "CE0062",
                        site.GetLocation(),
                        $"'{methodName}' cannot be the target of a CECallback. Invocation goes through " +
                        "SendCustomEvent, which only reaches public parameterless instance methods -- U# does " +
                        "not export anything else under its own name, so the callback would compile and then " +
                        $"never fire. Fix: declare '{methodName}' as a public parameterless instance method.");
                }

                return true;
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
            /// the whole-file precheck, so the declaration's values are ones CE also rewrites.
            /// </summary>
            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                if (_policy.IsActionTypeReference(node) && !IsInPreserveActionScope(node))
                {
                    RecordTransformation(node, "Type reference: Action → CECallback");
                    ChangesMade = true;
                    return SyntaxFactory.IdentifierName("CECallback")
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
                    return SyntaxFactory.IdentifierName("CECallback")
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
            /// Transforms new Action(Method) to new CECallback(this, nameof(Method)).
            /// </summary>
            public override SyntaxNode VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                if (!IsInPreserveActionScope(node) &&
                    _policy.TryGetCallbackMethodName(node, out string methodName))
                {
                    RecordTransformation(node, $"Constructor: new Action({methodName}) → new CECallback(this, nameof({methodName}))");
                    ChangesMade = true;

                    return SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.IdentifierName("CECallback"))
                        .WithArgumentList(
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SeparatedList(new[]
                                {
                                    SyntaxFactory.Argument(SyntaxFactory.ThisExpression()),
                                    SyntaxFactory.Argument(
                                        SyntaxFactory.InvocationExpression(
                                            SyntaxFactory.IdentifierName("nameof"),
                                            SyntaxFactory.ArgumentList(
                                                SyntaxFactory.SingletonSeparatedList(
                                                    SyntaxFactory.Argument(
                                                        SyntaxFactory.IdentifierName(methodName))))))
                                })))
                        .WithTriviaFrom(node);
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
                        ? $"Lambda block: () => {{ {methodName}(); }} → new CECallback(this, \"{methodName}\")"
                        : $"Lambda: () => {methodName}() → new CECallback(this, \"{methodName}\")");
                    ChangesMade = true;

                    return CreateCallbackCreation(methodName).WithTriviaFrom(node);
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
                    RecordTransformation(node, $"Anonymous method: delegate {{ {methodName}(); }} → new CECallback(this, \"{methodName}\")");
                    ChangesMade = true;
                    return CreateCallbackCreation(methodName).WithTriviaFrom(node);
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

            private static ObjectCreationExpressionSyntax CreateCallbackCreation(string methodName)
            {
                return SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.IdentifierName("CECallback"))
                    .WithArgumentList(
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList(new[]
                            {
                                SyntaxFactory.Argument(SyntaxFactory.ThisExpression()),
                                SyntaxFactory.Argument(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal(methodName)))
                            })));
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
