using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Inlines a lambda passed to a collection's ForEach helper into a direct loop, because Udon has no
    /// delegate to hand the lambda to.
    ///
    /// Supported, as a statement, with a lambda or a method group as the only argument:
    /// - <c>CEList&lt;T&gt;.ForEach</c>, <c>ForEachWithIndex</c> and <c>ForEachUntil</c>
    /// - <c>System.Collections.Generic.List&lt;T&gt;.ForEach</c>
    /// </summary>
    /// <remarks>
    /// Every decision is made on the original tree through the semantic model, and the lambda body is
    /// spliced in under the parameter names its author wrote.
    ///
    /// The receiver used to be recognised by the spelling of its declared type ("List", "Queue", "Stack")
    /// and the body by renaming every identifier that matched a parameter. Both guessed: a
    /// <c>System.Collections.Generic.List&lt;T&gt;</c> was rewritten to <c>GetUnchecked</c>, which only the
    /// CE collections have; a parameter that shadowed a collection field was inlined as that field; and the
    /// rename also hit named-argument labels and initializer member names while missing the parameter
    /// inside a nested lambda. A call this pass cannot lower exactly is left as written, so CE0021 reports
    /// the lambda instead of the build failing on generated code.
    ///
    /// This pass sees every file U# compiles, which includes plain MonoBehaviours in Assembly-CSharp. A
    /// rewrite here must therefore still be valid C# against the BCL types, not only against the types
    /// Udon forwards them to.
    /// </remarks>
    internal class LambdaInliningOptimizer : ICompileTimeOptimizer
    {
        private const string CEListTypeName = "UdonSharp.CE.Data.CEList<T>";
        private const string SystemListTypeName = "System.Collections.Generic.List<T>";

        private const string ForEachName = "ForEach";
        private const string ForEachWithIndexName = "ForEachWithIndex";
        private const string ForEachUntilName = "ForEachUntil";

        private const string ReceiverPlaceholder = "__ce_each_receiver";
        private const string CallbackPlaceholder = "__ce_each_callback";
        private const string PredicatePlaceholder = "__ce_each_predicate";

        public string OptimizerId => "CEOPT011";
        public string OptimizerName => "Lambda Inlining";
        public string Description => "Inlines lambdas passed to supported ForEach helpers into direct loops.";
        public bool IsEnabledByDefault => true;
        public int Priority => 2; // Run before ActionToCallbackTransformer

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            SyntaxNode root = tree.GetRoot();

            // Cheap token scan first. Every candidate is confirmed by binding its invocation, and a file
            // that never names one of the helpers has nothing to bind.
            if (!MentionsForEach(root))
                return tree;

            SemanticModel semanticModel = context.GetSemanticModel(tree);
            if (semanticModel == null)
                return tree;

            var rewriter = new LambdaInliningRewriter(context, semanticModel);
            var newRoot = rewriter.Visit(root);

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private static bool MentionsForEach(SyntaxNode root)
        {
            foreach (SyntaxToken token in root.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.IdentifierToken) && IsForEachName(token.ValueText))
                    return true;
            }

            return false;
        }

        private static bool IsForEachName(string name)
        {
            return name == ForEachName || name == ForEachWithIndexName || name == ForEachUntilName;
        }

        private class LambdaInliningRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly SemanticModel _semanticModel;
            private readonly SourceText _sourceText;
            private readonly string _endOfLine;
            private int _tempIndex;

            public bool ChangesMade { get; private set; }

            public LambdaInliningRewriter(OptimizationContext context, SemanticModel semanticModel)
            {
                _context = context;
                _semanticModel = semanticModel;
                _sourceText = semanticModel.SyntaxTree.GetText();
                _endOfLine = RewriteLineBreaks.DetectEndOfLine(_sourceText);
            }

            public override SyntaxNode VisitExpressionStatement(ExpressionStatementSyntax node)
            {
                if (!IsInPreserveActionScope(node) &&
                    node.Expression is InvocationExpressionSyntax invocation &&
                    TryInlineForEach(invocation, node.Span, out BlockSyntax loop))
                {
                    ChangesMade = true;
                    return loop.WithTriviaFrom(node);
                }

                return base.VisitExpressionStatement(node);
            }

            /// <summary>
            /// Builds the loop that replaces one ForEach call.
            ///
            /// <paramref name="invocation"/> has to be a node of the original tree: the receiver type, the
            /// method group and the name-collision check are all answered by the semantic model, which
            /// knows nothing about nodes this rewriter produced. That is why the lambda body is visited
            /// from here, before it is spliced, rather than by re-visiting the finished loop -- a nested
            /// ForEach is inlined while it can still be bound.
            ///
            /// <paramref name="callSpan"/> is the source span the loop stands in for (the whole statement,
            /// or just the call when it is a lambda's expression body). The loop is given exactly as many
            /// line breaks as that span had, see <see cref="RewriteLineBreaks"/>.
            /// </summary>
            private bool TryInlineForEach(InvocationExpressionSyntax invocation, TextSpan callSpan, out BlockSyntax loop)
            {
                loop = null;

                if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess) ||
                    !memberAccess.IsKind(SyntaxKind.SimpleMemberAccessExpression) ||
                    invocation.ArgumentList.Arguments.Count != 1)
                {
                    return false;
                }

                string methodName = memberAccess.Name.Identifier.ValueText;
                if (!IsForEachName(methodName))
                    return false;

                ArgumentSyntax argument = invocation.ArgumentList.Arguments[0];
                ExpressionSyntax receiver = memberAccess.Expression;
                ExpressionSyntax callback = argument.Expression;

                // A preprocessor directive inside the call lives in the trivia of tokens this rewrite
                // drops, and 'base' cannot be copied into a local.
                if (invocation.ContainsDirectives ||
                    !argument.RefKindKeyword.IsKind(SyntaxKind.None) ||
                    receiver is BaseExpressionSyntax)
                {
                    return false;
                }

                if (!TryResolveElementAccess(invocation, out bool uncheckedAccess))
                    return false;

                bool isPredicate = methodName == ForEachUntilName;
                bool withIndex = methodName == ForEachWithIndexName;

                // What the loop body is made of, and where in the source it came from.
                LambdaShape lambda = default;
                ExpressionSyntax predicate = null;
                int bodyStart;
                int bodyEnd;

                bool isLambda = TryGetLambdaShape(callback, out lambda);

                if (isLambda)
                {
                    if (lambda.Parameters != null && lambda.Parameters.Length != (withIndex ? 2 : 1))
                        return false;

                    // 'x => throw ...' is legal only because a lambda body may be a throw expression; spliced
                    // in as a statement or as the loop's condition it is CS8115.
                    if (lambda.Expression is ThrowExpressionSyntax)
                        return false;

                    if (isPredicate)
                    {
                        if (!TryGetPredicateExpression(lambda, out predicate))
                            return false;

                        bodyStart = predicate.SpanStart;
                        bodyEnd = predicate.Span.End;
                    }
                    else if (lambda.Block != null)
                    {
                        if (!IsInlinableActionBlock(lambda.Block))
                            return false;

                        SyntaxList<StatementSyntax> statements = lambda.Block.Statements;
                        bodyStart = statements.Count > 0 ? statements[0].FullSpan.Start : lambda.Block.OpenBraceToken.Span.End;
                        bodyEnd = statements.Count > 0 ? statements[statements.Count - 1].FullSpan.End : bodyStart;
                    }
                    else if (lambda.Expression != null)
                    {
                        bodyStart = lambda.Expression.SpanStart;
                        bodyEnd = lambda.Expression.Span.End;
                    }
                    else
                    {
                        return false;
                    }
                }
                else
                {
                    if (!IsInlinableMethodGroup(callback))
                        return false;

                    bodyStart = callback.SpanStart;
                    bodyEnd = callback.Span.End;
                }

                // The loop's own locals sit in the author's scope, so a name the file already uses anywhere
                // would either collide with it or be silently shadowed by it.
                var usedNames = new HashSet<string>(invocation.SyntaxTree.GetRoot().DescendantTokens()
                    .Where(token => token.IsKind(SyntaxKind.IdentifierToken)).Select(token => token.ValueText));
                int id;
                do { id = _tempIndex++; }
                while (usedNames.Contains($"__ce_each_collection{id}") || usedNames.Contains($"__ce_each_count{id}") ||
                       usedNames.Contains($"__ce_each_i{id}") || usedNames.Contains($"__ce_each_item{id}"));
                string collectionVar = $"__ce_each_collection{id}";
                string countVar = $"__ce_each_count{id}";
                string loopVar = $"__ce_each_i{id}";

                // The element is read through the indexer on a BCL list, because that is what the call has
                // to bind against before U# forwards the type -- GetUnchecked exists only on CEList.
                string itemAccess = uncheckedAccess
                    ? $"{collectionVar}.GetUnchecked({loopVar})"
                    : $"{collectionVar}[{loopVar}]";

                string itemName = null;
                string indexName = null;

                if (isLambda)
                {
                    if (lambda.Parameters != null)
                    {
                        // More than one '_' makes every one of them a discard the body cannot read.
                        bool discards = lambda.Parameters.Count(parameter => parameter.ValueText == "_") > 1;

                        if (!discards || lambda.Parameters[0].ValueText != "_")
                            itemName = lambda.Parameters[0].Text;

                        if (withIndex && (!discards || lambda.Parameters[1].ValueText != "_"))
                            indexName = lambda.Parameters[1].Text;
                    }

                    if (CollidesWithEnclosingScope(invocation, lambda))
                        return false;
                }
                else
                {
                    itemName = $"__ce_each_item{id}";
                }

                var loopStatements = new List<StatementSyntax>();

                if (itemName != null)
                    loopStatements.Add(SyntaxFactory.ParseStatement($"var {itemName} = {itemAccess}; "));

                if (indexName != null)
                    loopStatements.Add(SyntaxFactory.ParseStatement($"int {indexName} = {loopVar}; "));

                int declarationCount = loopStatements.Count;

                if (!isLambda)
                {
                    string call = withIndex
                        ? $"{CallbackPlaceholder}({itemName}, {loopVar})"
                        : $"{CallbackPlaceholder}({itemName})";

                    StatementSyntax callStatement = SyntaxFactory.ParseStatement(isPredicate
                        ? $"if (!{call}) break; "
                        : $"{call}; ");

                    loopStatements.Add(ReplacePlaceholder(callStatement, CallbackPlaceholder, callback.WithoutTrivia()));
                }
                else if (isPredicate)
                {
                    StatementSyntax breakStatement = SyntaxFactory.ParseStatement($"if (!({PredicatePlaceholder})) break; ");
                    ExpressionSyntax visitedPredicate = ((ExpressionSyntax)Visit(predicate)).WithoutTrivia();

                    loopStatements.Add(ReplacePlaceholder(breakStatement, PredicatePlaceholder, visitedPredicate));
                }
                else if (lambda.Block != null)
                {
                    var returnRewriter = new ReturnToContinueRewriter();

                    foreach (StatementSyntax statement in lambda.Block.Statements)
                        loopStatements.Add((StatementSyntax)returnRewriter.Visit(Visit(statement)));
                }
                else
                {
                    loopStatements.Add(InlineExpressionBody(lambda.Expression));
                }

                ExpressionSyntax visitedReceiver = ((ExpressionSyntax)Visit(receiver)).WithoutTrivia();

                // Line breaks that sat on the tokens this rewrite drops ('.ForEach(x =>', the lambda's
                // braces, the closing parenthesis). The ones before the body go back in front of it so the
                // author's statements stay on their own lines; the rest go behind it.
                int breaksBeforeBody =
                    RewriteLineBreaks.Count(_sourceText, callSpan.Start, bodyStart) -
                    RewriteLineBreaks.Count(_sourceText, receiver.SpanStart, receiver.Span.End);
                int breaksAfterBody = RewriteLineBreaks.Count(_sourceText, bodyEnd, callSpan.End);

                var template = (BlockSyntax)SyntaxFactory.ParseStatement(
                    $"{{ var {collectionVar} = {ReceiverPlaceholder}; int {countVar} = {collectionVar}.Count; " +
                    $"for (int {loopVar} = 0; {loopVar} < {countVar}; {loopVar}++) {{ }} }}");

                var forStatement = (ForStatementSyntax)template.Statements[2];
                var loopBody = (BlockSyntax)forStatement.Statement;

                SyntaxTriviaList leadingBreaks = RewriteLineBreaks.Create(breaksBeforeBody, _endOfLine);

                if (declarationCount > 0)
                {
                    StatementSyntax lastDeclaration = loopStatements[declarationCount - 1];
                    loopStatements[declarationCount - 1] =
                        lastDeclaration.WithTrailingTrivia(lastDeclaration.GetTrailingTrivia().AddRange(leadingBreaks));
                }
                else
                {
                    SyntaxToken openBrace = loopBody.OpenBraceToken;
                    loopBody = loopBody.WithOpenBraceToken(
                        openBrace.WithTrailingTrivia(openBrace.TrailingTrivia.AddRange(leadingBreaks)));
                }

                loopBody = loopBody
                    .WithStatements(SyntaxFactory.List(loopStatements))
                    .WithCloseBraceToken(loopBody.CloseBraceToken.WithLeadingTrivia(
                        RewriteLineBreaks.Create(breaksAfterBody, _endOfLine)));

                loop = template.WithStatements(SyntaxFactory.List(new[]
                {
                    ReplacePlaceholder(template.Statements[0], ReceiverPlaceholder, visitedReceiver),
                    template.Statements[1],
                    forStatement.WithStatement(loopBody)
                }));

                _context.RecordOptimization(
                    "CEOPT011",
                    isLambda ? $"Inlined {methodName} lambda" : $"Inlined {methodName} method group",
                    invocation.GetLocation(),
                    invocation.ToString(),
                    loop.ToString());

                return true;
            }

            /// <summary>
            /// Whether the call binds to a ForEach helper this pass knows how to replace, and how the loop
            /// reads an element from that collection.
            ///
            /// The declaring type of the bound method decides, not the receiver's spelling: an extension
            /// method or an unrelated type that happens to expose 'ForEach' is left alone.
            /// </summary>
            private bool TryResolveElementAccess(InvocationExpressionSyntax invocation, out bool uncheckedAccess)
            {
                uncheckedAccess = false;

                if (!(_semanticModel.GetSymbolInfo(invocation).Symbol is IMethodSymbol method) ||
                    method.MethodKind != MethodKind.Ordinary ||
                    method.IsStatic ||
                    method.Parameters.Length != 1)
                {
                    return false;
                }

                switch (method.ContainingType?.OriginalDefinition.ToDisplayString())
                {
                    case CEListTypeName:
                        uncheckedAccess = true;
                        return true;
                    case SystemListTypeName:
                        return true;
                    default:
                        return false;
                }
            }

            private static bool TryGetLambdaShape(ExpressionSyntax expression, out LambdaShape shape)
            {
                shape = default;

                if (!(expression is AnonymousFunctionExpressionSyntax function))
                    return false;

                // An async body can await, and the method it is inlined into is not async.
                if (function.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword))
                    return false;

                switch (function)
                {
                    case ParenthesizedLambdaExpressionSyntax parenthesized:
                        if (parenthesized.ParameterList.Parameters.Any(parameter => parameter.Modifiers.Count != 0))
                            return false;

                        shape.Parameters = parenthesized.ParameterList.Parameters
                            .Select(parameter => parameter.Identifier)
                            .ToArray();
                        shape.Block = parenthesized.Block;
                        shape.Expression = parenthesized.ExpressionBody;
                        return true;
                    case SimpleLambdaExpressionSyntax simple:
                        shape.Parameters = new[] { simple.Parameter.Identifier };
                        shape.Block = simple.Block;
                        shape.Expression = simple.ExpressionBody;
                        return true;
                    case AnonymousMethodExpressionSyntax anonymous:
                        if (anonymous.ParameterList != null &&
                            anonymous.ParameterList.Parameters.Any(parameter => parameter.Modifiers.Count != 0))
                        {
                            return false;
                        }

                        // 'delegate { ... }' declares no parameters at all, so there is nothing to name.
                        shape.Parameters = anonymous.ParameterList?.Parameters
                            .Select(parameter => parameter.Identifier)
                            .ToArray();
                        shape.Block = anonymous.Block;
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>
            /// ForEachUntil takes the predicate's value from an expression body or from a block that is a
            /// single <c>return</c>. Anything longer would need its control flow rebuilt around the break.
            /// </summary>
            private static bool TryGetPredicateExpression(LambdaShape lambda, out ExpressionSyntax predicate)
            {
                predicate = lambda.Expression;

                if (predicate == null &&
                    lambda.Block != null &&
                    lambda.Block.Statements.Count == 1 &&
                    lambda.Block.Statements[0] is ReturnStatementSyntax returnStatement)
                {
                    predicate = returnStatement.Expression;
                }

                return predicate != null;
            }

            /// <summary>
            /// Whether a block body means the same thing once it is the body of the generated loop.
            ///
            /// In the lambda, <c>return;</c> ends the current element. Spliced verbatim it ended the method
            /// the ForEach was written in, so every element after the first early-out was skipped along
            /// with the rest of that method. <c>continue;</c> is the exact equivalent, but only while no
            /// loop of the author's own sits between the return and the generated one -- inside such a
            /// loop it would continue the wrong one, and that body is left as a lambda.
            /// </summary>
            private static bool IsInlinableActionBlock(BlockSyntax block)
            {
                foreach (SyntaxNode node in block.DescendantNodes(candidate => !IsNestedFunction(candidate)))
                {
                    switch (node)
                    {
                        case ReturnStatementSyntax returnStatement:
                            if (returnStatement.Expression != null || IsInsideLoop(returnStatement, block))
                                return false;
                            break;

                        // A label is scoped to its method, and this body is about to join another one.
                        case LabeledStatementSyntax _:
                            return false;
                    }
                }

                return true;
            }

            private static bool IsInsideLoop(SyntaxNode node, SyntaxNode boundary)
            {
                for (SyntaxNode current = node.Parent; current != null && current != boundary; current = current.Parent)
                {
                    if (current is ForStatementSyntax ||
                        current is CommonForEachStatementSyntax ||
                        current is WhileStatementSyntax ||
                        current is DoStatementSyntax)
                    {
                        return true;
                    }
                }

                return false;
            }

            private static bool IsNestedFunction(SyntaxNode node)
            {
                return node is AnonymousFunctionExpressionSyntax || node is LocalFunctionStatementSyntax;
            }

            /// <summary>
            /// A method group can stand in for the call it converts to only when writing the call per
            /// element evaluates nothing the conversion evaluated once: a method of this object or a static
            /// method. A group taken from another expression ('GetSink().Write') would re-run that
            /// expression for every element.
            /// </summary>
            private bool IsInlinableMethodGroup(ExpressionSyntax callback)
            {
                if (!(_semanticModel.GetSymbolInfo(callback).Symbol is IMethodSymbol target) ||
                    target.MethodKind != MethodKind.Ordinary)
                {
                    return false;
                }

                switch (callback)
                {
                    case SimpleNameSyntax _:
                        return true;
                    case MemberAccessExpressionSyntax access:
                        return target.IsStatic || access.Expression is ThisExpressionSyntax;
                    default:
                        return false;
                }
            }

            /// <summary>
            /// The body keeps the names its author wrote, so the loop declares the element under the
            /// lambda's own parameter name. A lambda may reuse a name that is already a local or parameter
            /// of the enclosing method; a nested block may not, so such a lambda is left standing.
            /// </summary>
            private bool CollidesWithEnclosingScope(InvocationExpressionSyntax invocation, LambdaShape lambda)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);

                if (lambda.Parameters != null)
                {
                    foreach (SyntaxToken parameter in lambda.Parameters)
                        names.Add(parameter.ValueText);
                }

                CollectDeclaredNames((SyntaxNode)lambda.Block ?? lambda.Expression, names);

                foreach (string name in names)
                {
                    if (name.Length == 0)
                        continue;

                    foreach (ISymbol symbol in _semanticModel.LookupSymbols(invocation.SpanStart, name: name))
                    {
                        if (symbol.Kind == SymbolKind.Local ||
                            symbol.Kind == SymbolKind.Parameter ||
                            symbol.Kind == SymbolKind.RangeVariable)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private static void CollectDeclaredNames(SyntaxNode body, HashSet<string> names)
            {
                if (body == null)
                    return;

                foreach (SyntaxNode node in body.DescendantNodesAndSelf())
                {
                    switch (node)
                    {
                        case VariableDeclaratorSyntax declarator:
                            names.Add(declarator.Identifier.ValueText);
                            break;
                        case SingleVariableDesignationSyntax designation:
                            names.Add(designation.Identifier.ValueText);
                            break;
                        case ForEachStatementSyntax forEach:
                            names.Add(forEach.Identifier.ValueText);
                            break;
                        case CatchDeclarationSyntax catchDeclaration:
                            names.Add(catchDeclaration.Identifier.ValueText);
                            break;
                        case LocalFunctionStatementSyntax localFunction:
                            names.Add(localFunction.Identifier.ValueText);
                            break;
                        case FromClauseSyntax fromClause:
                            names.Add(fromClause.Identifier.ValueText);
                            break;
                        case LetClauseSyntax letClause:
                            names.Add(letClause.Identifier.ValueText);
                            break;
                        case JoinClauseSyntax joinClause:
                            names.Add(joinClause.Identifier.ValueText);
                            break;
                        case JoinIntoClauseSyntax joinInto:
                            names.Add(joinInto.Identifier.ValueText);
                            break;
                        case QueryContinuationSyntax continuation:
                            names.Add(continuation.Identifier.ValueText);
                            break;
                    }
                }
            }

            /// <summary>
            /// An expression-bodied lambda whose whole body is another supported ForEach call becomes that
            /// call's loop directly; any other expression becomes a statement.
            /// </summary>
            private StatementSyntax InlineExpressionBody(ExpressionSyntax expression)
            {
                if (expression is InvocationExpressionSyntax nested &&
                    TryInlineForEach(nested, nested.Span, out BlockSyntax nestedLoop))
                {
                    return nestedLoop;
                }

                return SyntaxFactory.ExpressionStatement(((ExpressionSyntax)Visit(expression)).WithoutTrivia());
            }

            private static TNode ReplacePlaceholder<TNode>(TNode node, string placeholder, ExpressionSyntax replacement)
                where TNode : SyntaxNode
            {
                IdentifierNameSyntax target = node.DescendantNodes()
                    .OfType<IdentifierNameSyntax>()
                    .First(identifier => identifier.Identifier.ValueText == placeholder);

                return node.ReplaceNode(target, replacement);
            }

            private static bool IsInPreserveActionScope(SyntaxNode node)
            {
                var current = node.Parent;
                while (current != null)
                {
                    if (current is MethodDeclarationSyntax methodDecl &&
                        HasPreserveActionAttribute(methodDecl.AttributeLists))
                        return true;
                    if (current is TypeDeclarationSyntax typeDecl &&
                        HasPreserveActionAttribute(typeDecl.AttributeLists))
                        return true;
                    current = current.Parent;
                }

                return false;
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

            private struct LambdaShape
            {
                /// <summary>Null for <c>delegate { ... }</c>, which names no parameters.</summary>
                public SyntaxToken[] Parameters;
                public BlockSyntax Block;
                public ExpressionSyntax Expression;
            }

            /// <summary>
            /// Turns the <c>return;</c> statements of an inlined body into <c>continue;</c>. Only run on a
            /// body <see cref="IsInlinableActionBlock"/> accepted, so every return left here is one the
            /// generated loop is the innermost loop of. Nested ForEach calls were inlined before this runs
            /// and rewrote their own returns; a lambda that was left standing keeps its returns.
            /// </summary>
            private sealed class ReturnToContinueRewriter : CSharpSyntaxRewriter
            {
                public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node)
                {
                    return SyntaxFactory.ContinueStatement().WithTriviaFrom(node);
                }

                public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
                {
                    return node;
                }

                public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
                {
                    return node;
                }

                public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
                {
                    return node;
                }

                public override SyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
                {
                    return node;
                }
            }
        }
    }

    /// <summary>
    /// Keeps a rewritten node on as many lines as the node it replaces.
    ///
    /// The CE source-to-source passes build their replacement out of the pieces they keep and drop the
    /// tokens in between -- and a line break belongs to the trivia of whichever token precedes it. Dropping
    /// a comma or a brace therefore pulled every later line of the file up, and the Action pass pushed
    /// every line down by appending a using directive. Diagnostics and the debug-info line map are read off
    /// the rewritten tree, so each one after such a rewrite named the wrong source line. A pass counts the
    /// breaks its dropped tokens carried and puts that many back.
    /// </summary>
    internal static class RewriteLineBreaks
    {
        /// <summary>The number of line breaks between two positions of the original text.</summary>
        public static int Count(SourceText text, int start, int end)
        {
            if (end <= start)
                return 0;

            return text.Lines.GetLineFromPosition(end).LineNumber - text.Lines.GetLineFromPosition(start).LineNumber;
        }

        public static SyntaxTriviaList Create(int count, string endOfLine)
        {
            SyntaxTriviaList trivia = SyntaxFactory.TriviaList();

            for (int i = 0; i < count; i++)
                trivia = trivia.Add(SyntaxFactory.EndOfLine(endOfLine));

            return trivia;
        }

        /// <summary>The line terminator the file already uses, so restored breaks match the rest of it.</summary>
        public static string DetectEndOfLine(SourceText text)
        {
            foreach (TextLine line in text.Lines)
            {
                if (line.EndIncludingLineBreak > line.End)
                    return text.ToString(TextSpan.FromBounds(line.End, line.EndIncludingLineBreak));
            }

            return "\n";
        }
    }
}
