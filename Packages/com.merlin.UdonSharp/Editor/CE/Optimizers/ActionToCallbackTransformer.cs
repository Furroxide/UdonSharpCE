using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

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
    /// - action() invocations → action.Invoke()
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
    /// </remarks>
    internal class ActionToCallbackTransformer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT007";
        public string OptimizerName => "Action to CECallback Transformer";
        public string Description => "Transforms Action delegate usage into Udon-compatible CECallback.";
        public bool IsEnabledByDefault => true;
        public int Priority => 3; // Very early - must run before other transforms

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new ActionTransformRewriter(context);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                // Ensure the using directive is added
                newRoot = EnsureUsingDirective(newRoot);
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
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

        private class ActionTransformRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            public bool ChangesMade { get; private set; }

            public ActionTransformRewriter(OptimizationContext context)
            {
                _context = context;
            }

            /// <summary>
            /// Transforms Action type references to CECallback.
            /// </summary>
            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                // Transform "Action" type to "CECallback"
                if (node.Identifier.ValueText == "Action")
                {
                    // Check if this is a type reference (not a variable named Action)
                    if (IsTypeReference(node))
                    {
                        RecordTransformation(node, "Type reference: Action → CECallback");
                        ChangesMade = true;
                        return SyntaxFactory.IdentifierName("CECallback")
                            .WithTriviaFrom(node);
                    }
                }

                return base.VisitIdentifierName(node);
            }

            /// <summary>
            /// Transforms qualified System.Action type references.
            /// </summary>
            public override SyntaxNode VisitQualifiedName(QualifiedNameSyntax node)
            {
                // Transform "System.Action" to "CECallback"
                if (node.ToString() == "System.Action")
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
                // The CEWorld implementation directly calls .Invoke() on CECallback instances,
                // so this transformation is not needed for internal code.
                //
                // Users who want delegate-like syntax will need to use callback.Invoke() explicitly.
                return base.VisitInvocationExpression(node);
            }

            /// <summary>
            /// Transforms new Action(Method) to CECallback.Create(this, nameof(Method)).
            /// </summary>
            public override SyntaxNode VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
            {
                var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node);

                // Check for "new Action(methodName)" pattern
                if (visited.Type is IdentifierNameSyntax typeName &&
                    typeName.Identifier.ValueText == "CECallback" && // Already transformed or is CECallback
                    visited.ArgumentList?.Arguments.Count == 1)
                {
                    var arg = visited.ArgumentList.Arguments[0].Expression;
                    
                    // If the argument is a simple identifier (method reference), transform to CECallback.Create
                    if (arg is IdentifierNameSyntax methodRef)
                    {
                        string methodName = methodRef.Identifier.ValueText;
                        
                        RecordTransformation(node, $"Constructor: new CECallback({methodName}) → new CECallback(this, nameof({methodName}))");
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
                }

                return visited;
            }

            /// <summary>
            /// Transforms lambda expressions () => Method() to CECallback creation.
            /// </summary>
            public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
            {
                // Check for () => Method() pattern
                if (node.ParameterList.Parameters.Count == 0 && 
                    node.ExpressionBody is InvocationExpressionSyntax invocation &&
                    invocation.ArgumentList.Arguments.Count == 0 &&
                    invocation.Expression is IdentifierNameSyntax methodName)
                {
                    RecordTransformation(node, $"Lambda: () => {methodName.Identifier.ValueText}() → new CECallback(this, \"{methodName.Identifier.ValueText}\")");
                    ChangesMade = true;

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
                                            SyntaxFactory.Literal(methodName.Identifier.ValueText)))
                                })))
                        .WithTriviaFrom(node);
                }

                // Check for () => this.Method() pattern
                if (node.ParameterList.Parameters.Count == 0 && 
                    node.ExpressionBody is InvocationExpressionSyntax invocation2 &&
                    invocation2.ArgumentList.Arguments.Count == 0 &&
                    invocation2.Expression is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Expression is ThisExpressionSyntax &&
                    memberAccess.Name is IdentifierNameSyntax methodName2)
                {
                    RecordTransformation(node, $"Lambda: () => this.{methodName2.Identifier.ValueText}() → new CECallback(this, \"{methodName2.Identifier.ValueText}\")");
                    ChangesMade = true;

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
                                            SyntaxFactory.Literal(methodName2.Identifier.ValueText)))
                                })))
                        .WithTriviaFrom(node);
                }

                return base.VisitParenthesizedLambdaExpression(node);
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
            /// Checks if an identifier name is being used as a type reference.
            /// </summary>
            private bool IsTypeReference(IdentifierNameSyntax node)
            {
                // Check parent context to determine if this is a type usage
                var parent = node.Parent;

                if (parent == null)
                    return false;

                // Common type reference contexts
                return parent is VariableDeclarationSyntax ||          // Action myAction
                       parent is ParameterSyntax ||                    // void Method(Action callback)
                       parent is TypeOfExpressionSyntax ||             // typeof(Action)
                       parent is ArrayTypeSyntax ||                    // Action[]
                       parent is NullableTypeSyntax ||                 // Action?
                       parent is ObjectCreationExpressionSyntax ||     // new Action(...)
                       parent is CastExpressionSyntax ||               // (Action)x
                       parent is BinaryExpressionSyntax bin && bin.IsKind(SyntaxKind.AsExpression) || // x as Action
                       parent is PropertyDeclarationSyntax ||          // public Action MyProp { get; set; }
                       parent is FieldDeclarationSyntax ||             // private Action _field;
                       parent is GenericNameSyntax;                    // List<Action>
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

