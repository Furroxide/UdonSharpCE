using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Inlines certain lambda usages into direct statements to avoid delegate creation.
    ///
    /// Supported:
    /// - Immediate-invocation lambdas: (() => { ... })();
    /// - Known collection ForEach variants (List/Queue/Stack/CEList) with lambdas
    /// </summary>
    internal class LambdaInliningOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT011";
        public string OptimizerName => "Lambda Inlining";
        public string Description => "Inlines immediate lambdas and supported ForEach calls into direct loops.";
        public bool IsEnabledByDefault => true;
        public int Priority => 2; // Run before ActionToCallbackTransformer

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new LambdaInliningRewriter(context);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private enum CollectionKind
        {
            ListLike,
            Queue,
            Stack
        }

        private class LambdaInliningRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly Stack<Dictionary<string, CollectionKind>> _scopes = new Stack<Dictionary<string, CollectionKind>>();
            private Dictionary<string, CollectionKind> _fieldTypes;
            private Dictionary<string, CollectionKind> _parameterTypes;
            private int _tempIndex;

            public bool ChangesMade { get; private set; }

            public LambdaInliningRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                var previousFields = _fieldTypes;
                _fieldTypes = CollectFieldTypes(node);

                var result = base.VisitClassDeclaration(node);

                _fieldTypes = previousFields;
                return result;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                var previousParams = _parameterTypes;
                _parameterTypes = CollectParameterTypes(node.ParameterList);

                var result = base.VisitMethodDeclaration(node);

                _parameterTypes = previousParams;
                return result;
            }

            public override SyntaxNode VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
            {
                var previousParams = _parameterTypes;
                _parameterTypes = CollectParameterTypes(node.ParameterList);

                var result = base.VisitConstructorDeclaration(node);

                _parameterTypes = previousParams;
                return result;
            }

            public override SyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
            {
                var previousParams = _parameterTypes;
                _parameterTypes = CollectParameterTypes(node.ParameterList);

                var result = base.VisitLocalFunctionStatement(node);

                _parameterTypes = previousParams;
                return result;
            }

            public override SyntaxNode VisitBlock(BlockSyntax node)
            {
                _scopes.Push(new Dictionary<string, CollectionKind>(StringComparer.Ordinal));

                var statements = new List<StatementSyntax>(node.Statements.Count);
                foreach (var statement in node.Statements)
                {
                    var visited = (StatementSyntax)Visit(statement);
                    statements.Add(visited);

                    if (statement is LocalDeclarationStatementSyntax localDecl)
                    {
                        RecordLocalDeclaration(localDecl.Declaration);
                    }
                }

                _scopes.Pop();

                return node.WithStatements(SyntaxFactory.List(statements));
            }

            public override SyntaxNode VisitExpressionStatement(ExpressionStatementSyntax node)
            {
                if (IsInPreserveActionScope(node))
                    return base.VisitExpressionStatement(node);

                if (TryRewriteIife(node, out StatementSyntax iifeReplacement))
                {
                    ChangesMade = true;
                    return iifeReplacement.WithTriviaFrom(node);
                }

                if (TryRewriteForEach(node, out StatementSyntax foreachReplacement))
                {
                    ChangesMade = true;
                    return foreachReplacement.WithTriviaFrom(node);
                }

                return base.VisitExpressionStatement(node);
            }

            private bool TryRewriteIife(ExpressionStatementSyntax node, out StatementSyntax replacement)
            {
                replacement = null;

                if (!(node.Expression is InvocationExpressionSyntax invocation))
                    return false;

                if (invocation.ArgumentList == null || invocation.ArgumentList.Arguments.Count != 0)
                    return false;

                if (!TryGetZeroParamLambdaBody(invocation.Expression, out BlockSyntax blockBody, out ExpressionSyntax exprBody))
                    return false;

                StatementSyntax resultStatement;
                if (blockBody != null)
                {
                    var visitedBlock = (BlockSyntax)VisitBlock(blockBody);
                    resultStatement = visitedBlock;
                }
                else if (exprBody != null)
                {
                    var visitedExpr = (ExpressionSyntax)Visit(exprBody);
                    resultStatement = SyntaxFactory.ExpressionStatement(visitedExpr);
                }
                else
                {
                    return false;
                }

                _context.RecordOptimization(
                    "CEOPT011",
                    "Inlined immediate-invocation lambda",
                    node.GetLocation(),
                    node.ToString(),
                    resultStatement.ToString());

                replacement = resultStatement;
                return true;
            }

            private bool TryRewriteForEach(ExpressionStatementSyntax node, out StatementSyntax replacement)
            {
                replacement = null;

                if (!(node.Expression is InvocationExpressionSyntax invocation))
                    return false;

                if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess))
                    return false;

                string methodName = memberAccess.Name.Identifier.ValueText;
                if (methodName != "ForEach" && methodName != "ForEachWithIndex" && methodName != "ForEachUntil")
                    return false;

                if (invocation.ArgumentList == null || invocation.ArgumentList.Arguments.Count != 1)
                    return false;

                var receiver = memberAccess.Expression;
                if (!TryResolveCollectionKind(receiver, out CollectionKind kind))
                    return false;

                var callbackExpr = invocation.ArgumentList.Arguments[0].Expression;

                if (!TryBuildForEachBlock(methodName, kind, receiver, callbackExpr, out BlockSyntax block))
                    return false;

                var visitedBlock = (BlockSyntax)VisitBlock(block);

                _context.RecordOptimization(
                    "CEOPT011",
                    $"Inlined {methodName} lambda",
                    node.GetLocation(),
                    node.ToString(),
                    visitedBlock.ToString());

                replacement = visitedBlock;
                return true;
            }

            private bool TryGetZeroParamLambdaBody(
                ExpressionSyntax expression,
                out BlockSyntax blockBody,
                out ExpressionSyntax expressionBody)
            {
                blockBody = null;
                expressionBody = null;

                switch (expression)
                {
                    case ParenthesizedLambdaExpressionSyntax parenthesized:
                        if (parenthesized.ParameterList.Parameters.Count != 0)
                            return false;
                        blockBody = parenthesized.Block;
                        expressionBody = parenthesized.ExpressionBody;
                        return true;
                    case SimpleLambdaExpressionSyntax simple:
                        return false;
                    case AnonymousMethodExpressionSyntax anonymous:
                        if (anonymous.ParameterList != null && anonymous.ParameterList.Parameters.Count != 0)
                            return false;
                        blockBody = anonymous.Block;
                        return true;
                    default:
                        return false;
                }
            }

            private bool TryBuildForEachBlock(
                string methodName,
                CollectionKind kind,
                ExpressionSyntax receiver,
                ExpressionSyntax callbackExpr,
                out BlockSyntax block)
            {
                block = null;

                if (!TryGetLambdaInfo(callbackExpr, out LambdaInfo lambdaInfo))
                {
                    // Method group or delegate variable
                    if (!TryGetMethodGroupInvocation(callbackExpr, methodName, kind, receiver, out BlockSyntax methodGroupBlock))
                        return false;

                    block = methodGroupBlock;
                    return true;
                }

                int expectedParams = methodName == "ForEachWithIndex" ? 2 : 1;
                if (lambdaInfo.ParameterNames.Length != expectedParams)
                    return false;

                int tempId = _tempIndex++;
                string collectionVar = $"__ce_each_collection{tempId}";
                string countVar = $"__ce_each_count{tempId}";
                string loopIndexVar = $"__ce_each_i{tempId}";
                string itemVar = $"__ce_each_item{tempId}";
                string indexVar = $"__ce_each_index{tempId}";

                var collectionDecl = CreateVarDeclaration(collectionVar, receiver);
                var countDecl = CreateTypedDeclaration("int", countVar,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(collectionVar),
                        SyntaxFactory.IdentifierName("Count")));

                var itemExpression = CreateItemExpression(kind, collectionVar, loopIndexVar);

                var itemDecl = CreateVarDeclaration(itemVar, itemExpression);

                var replacements = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { lambdaInfo.ParameterNames[0], itemVar }
                };

                if (expectedParams == 2)
                {
                    replacements[lambdaInfo.ParameterNames[1]] = indexVar;
                }

                var bodyStatements = new List<StatementSyntax>
                {
                    itemDecl
                };

                if (expectedParams == 2)
                {
                    var indexDecl = CreateTypedDeclaration("int", indexVar, CreateIndexExpression(kind, loopIndexVar, countVar));
                    bodyStatements.Add(indexDecl);
                }

                if (methodName == "ForEach")
                {
                    if (!TryBuildActionBody(lambdaInfo, replacements, out IEnumerable<StatementSyntax> lambdaStatements))
                        return false;

                    bodyStatements.AddRange(lambdaStatements);
                }
                else if (methodName == "ForEachWithIndex")
                {
                    if (!TryBuildActionBody(lambdaInfo, replacements, out IEnumerable<StatementSyntax> lambdaStatements))
                        return false;

                    bodyStatements.AddRange(lambdaStatements);
                }
                else if (methodName == "ForEachUntil")
                {
                    if (!TryBuildPredicateStatement(lambdaInfo, replacements, out StatementSyntax predicateStatement))
                        return false;

                    bodyStatements.Add(predicateStatement);
                }
                else
                {
                    return false;
                }

                var loopBody = SyntaxFactory.Block(bodyStatements);
                var loopStatement = CreateForLoop(kind, loopIndexVar, countVar, loopBody);

                block = SyntaxFactory.Block(collectionDecl, countDecl, loopStatement);
                return true;
            }

            private bool TryGetMethodGroupInvocation(
                ExpressionSyntax callbackExpr,
                string methodName,
                CollectionKind kind,
                ExpressionSyntax receiver,
                out BlockSyntax block)
            {
                block = null;

                if (!(callbackExpr is IdentifierNameSyntax || callbackExpr is MemberAccessExpressionSyntax))
                    return false;

                int tempId = _tempIndex++;
                string collectionVar = $"__ce_each_collection{tempId}";
                string countVar = $"__ce_each_count{tempId}";
                string loopIndexVar = $"__ce_each_i{tempId}";
                string itemVar = $"__ce_each_item{tempId}";

                var collectionDecl = CreateVarDeclaration(collectionVar, receiver);
                var countDecl = CreateTypedDeclaration("int", countVar,
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(collectionVar),
                        SyntaxFactory.IdentifierName("Count")));

                var itemExpression = CreateItemExpression(kind, collectionVar, loopIndexVar);

                var itemDecl = CreateVarDeclaration(itemVar, itemExpression);

                var arguments = new List<ArgumentSyntax>
                {
                    SyntaxFactory.Argument(SyntaxFactory.IdentifierName(itemVar))
                };

                if (methodName == "ForEachWithIndex")
                {
                    arguments.Add(SyntaxFactory.Argument(CreateIndexExpression(kind, loopIndexVar, countVar)));
                }

                ExpressionSyntax invocationExpr = SyntaxFactory.InvocationExpression(
                    callbackExpr,
                    SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(arguments)));

                StatementSyntax invokeStatement;
                if (methodName == "ForEachUntil")
                {
                    invokeStatement = SyntaxFactory.IfStatement(
                        SyntaxFactory.PrefixUnaryExpression(
                            SyntaxKind.LogicalNotExpression,
                            invocationExpr),
                        SyntaxFactory.BreakStatement());
                }
                else
                {
                    invokeStatement = SyntaxFactory.ExpressionStatement(invocationExpr);
                }

                var loopBody = SyntaxFactory.Block(itemDecl, invokeStatement);
                var loopStatement = CreateForLoop(kind, loopIndexVar, countVar, loopBody);

                block = SyntaxFactory.Block(collectionDecl, countDecl, loopStatement);
                return true;
            }

            private bool TryBuildActionBody(
                LambdaInfo lambdaInfo,
                Dictionary<string, string> replacements,
                out IEnumerable<StatementSyntax> statements)
            {
                statements = null;

                var rewriter = new ParameterSubstitutionRewriter(replacements);

                if (lambdaInfo.BlockBody != null)
                {
                    var rewrittenBlock = (BlockSyntax)rewriter.Visit(lambdaInfo.BlockBody);
                    statements = rewrittenBlock.Statements;
                    return true;
                }

                if (lambdaInfo.ExpressionBody != null)
                {
                    var rewrittenExpr = (ExpressionSyntax)rewriter.Visit(lambdaInfo.ExpressionBody);
                    statements = new[] { SyntaxFactory.ExpressionStatement(rewrittenExpr) };
                    return true;
                }

                return false;
            }

            private bool TryBuildPredicateStatement(
                LambdaInfo lambdaInfo,
                Dictionary<string, string> replacements,
                out StatementSyntax statement)
            {
                statement = null;

                var rewriter = new ParameterSubstitutionRewriter(replacements);

                ExpressionSyntax predicateExpr = null;

                if (lambdaInfo.ExpressionBody != null)
                {
                    predicateExpr = (ExpressionSyntax)rewriter.Visit(lambdaInfo.ExpressionBody);
                }
                else if (lambdaInfo.BlockBody != null)
                {
                    if (lambdaInfo.BlockBody.Statements.Count == 1 &&
                        lambdaInfo.BlockBody.Statements[0] is ReturnStatementSyntax returnStmt &&
                        returnStmt.Expression != null)
                    {
                        predicateExpr = (ExpressionSyntax)rewriter.Visit(returnStmt.Expression);
                    }
                }

                if (predicateExpr == null)
                    return false;

                statement = SyntaxFactory.IfStatement(
                    SyntaxFactory.PrefixUnaryExpression(
                        SyntaxKind.LogicalNotExpression,
                        predicateExpr),
                    SyntaxFactory.BreakStatement());
                return true;
            }

            private bool TryGetLambdaInfo(ExpressionSyntax expr, out LambdaInfo info)
            {
                info = default;

                switch (expr)
                {
                    case ParenthesizedLambdaExpressionSyntax parenthesized:
                        info.ParameterNames = parenthesized.ParameterList.Parameters
                            .Select(p => p.Identifier.ValueText)
                            .ToArray();
                        info.BlockBody = parenthesized.Block;
                        info.ExpressionBody = parenthesized.ExpressionBody;
                        return true;
                    case SimpleLambdaExpressionSyntax simple:
                        info.ParameterNames = new[] { simple.Parameter.Identifier.ValueText };
                        info.BlockBody = simple.Block;
                        info.ExpressionBody = simple.ExpressionBody;
                        return true;
                    case AnonymousMethodExpressionSyntax anonymous:
                        info.ParameterNames = anonymous.ParameterList == null
                            ? Array.Empty<string>()
                            : anonymous.ParameterList.Parameters.Select(p => p.Identifier.ValueText).ToArray();
                        info.BlockBody = anonymous.Block;
                        info.ExpressionBody = null;
                        return true;
                    default:
                        return false;
                }
            }

            private ExpressionSyntax CreateIndexExpression(CollectionKind kind, string loopIndexVar, string countVar)
            {
                if (kind == CollectionKind.Stack)
                {
                    return SyntaxFactory.BinaryExpression(
                        SyntaxKind.SubtractExpression,
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.SubtractExpression,
                            SyntaxFactory.IdentifierName(countVar),
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.NumericLiteralExpression,
                                SyntaxFactory.Literal(1))),
                        SyntaxFactory.IdentifierName(loopIndexVar));
                }

                return SyntaxFactory.IdentifierName(loopIndexVar);
            }

            private static ExpressionSyntax CreateItemExpression(CollectionKind kind, string collectionVar, string loopIndexVar)
            {
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName(collectionVar),
                        SyntaxFactory.IdentifierName("GetUnchecked")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(SyntaxFactory.IdentifierName(loopIndexVar)))));
            }

            private static LocalDeclarationStatementSyntax CreateVarDeclaration(string name, ExpressionSyntax initializer)
            {
                return SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.IdentifierName("var"))
                    .WithVariables(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(
                                SyntaxFactory.Identifier(name))
                            .WithInitializer(
                                SyntaxFactory.EqualsValueClause(initializer)))));
            }

            private static LocalDeclarationStatementSyntax CreateTypedDeclaration(string typeName, string name, ExpressionSyntax initializer)
            {
                return SyntaxFactory.LocalDeclarationStatement(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.IdentifierName(typeName))
                    .WithVariables(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(
                                SyntaxFactory.Identifier(name))
                            .WithInitializer(
                                SyntaxFactory.EqualsValueClause(initializer)))));
            }

            private static ForStatementSyntax CreateForLoop(
                CollectionKind kind,
                string loopIndexVar,
                string countVar,
                BlockSyntax body)
            {
                ExpressionSyntax initValue;
                ExpressionSyntax condition;
                ExpressionSyntax incrementor;

                if (kind == CollectionKind.Stack)
                {
                    initValue = SyntaxFactory.BinaryExpression(
                        SyntaxKind.SubtractExpression,
                        SyntaxFactory.IdentifierName(countVar),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));

                    condition = SyntaxFactory.BinaryExpression(
                        SyntaxKind.GreaterThanOrEqualExpression,
                        SyntaxFactory.IdentifierName(loopIndexVar),
                        SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0)));

                    incrementor = SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.PostDecrementExpression,
                        SyntaxFactory.IdentifierName(loopIndexVar));
                }
                else
                {
                    initValue = SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0));

                    condition = SyntaxFactory.BinaryExpression(
                        SyntaxKind.LessThanExpression,
                        SyntaxFactory.IdentifierName(loopIndexVar),
                        SyntaxFactory.IdentifierName(countVar));

                    incrementor = SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.PostIncrementExpression,
                        SyntaxFactory.IdentifierName(loopIndexVar));
                }

                return SyntaxFactory.ForStatement(
                        body)
                    .WithDeclaration(
                        SyntaxFactory.VariableDeclaration(
                                SyntaxFactory.IdentifierName("int"))
                            .WithVariables(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.VariableDeclarator(
                                            SyntaxFactory.Identifier(loopIndexVar))
                                        .WithInitializer(
                                            SyntaxFactory.EqualsValueClause(initValue)))))
                    .WithCondition(condition)
                    .WithIncrementors(
                        SyntaxFactory.SingletonSeparatedList(incrementor));
            }

            private bool TryResolveCollectionKind(ExpressionSyntax receiver, out CollectionKind kind)
            {
                kind = default;

                if (receiver is IdentifierNameSyntax identifier)
                {
                    string name = identifier.Identifier.ValueText;
                    if (TryResolveSymbolKind(name, out kind))
                        return true;
                }

                if (receiver is MemberAccessExpressionSyntax memberAccess &&
                    memberAccess.Expression is ThisExpressionSyntax &&
                    memberAccess.Name is IdentifierNameSyntax memberName)
                {
                    string name = memberName.Identifier.ValueText;
                    if (_fieldTypes != null && _fieldTypes.TryGetValue(name, out kind))
                        return true;
                }

                if (receiver is ObjectCreationExpressionSyntax creation)
                {
                    if (TryGetCollectionKindFromTypeSyntax(creation.Type, out kind))
                        return true;
                }

                if (receiver is CastExpressionSyntax castExpr)
                {
                    if (TryGetCollectionKindFromTypeSyntax(castExpr.Type, out kind))
                        return true;
                }

                return false;
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

            private bool TryResolveSymbolKind(string name, out CollectionKind kind)
            {
                kind = default;

                if (_scopes.Count > 0)
                {
                    foreach (var scope in _scopes)
                    {
                        if (scope.TryGetValue(name, out kind))
                            return true;
                    }
                }

                if (_parameterTypes != null && _parameterTypes.TryGetValue(name, out kind))
                    return true;

                if (_fieldTypes != null && _fieldTypes.TryGetValue(name, out kind))
                    return true;

                return false;
            }

            private static Dictionary<string, CollectionKind> CollectFieldTypes(ClassDeclarationSyntax node)
            {
                var map = new Dictionary<string, CollectionKind>(StringComparer.Ordinal);

                foreach (var member in node.Members.OfType<FieldDeclarationSyntax>())
                {
                    if (!TryGetCollectionKindFromTypeSyntax(member.Declaration.Type, out CollectionKind kind))
                        continue;

                    foreach (var variable in member.Declaration.Variables)
                    {
                        string name = variable.Identifier.ValueText;
                        if (!map.ContainsKey(name))
                            map.Add(name, kind);
                    }
                }

                return map;
            }

            private static Dictionary<string, CollectionKind> CollectParameterTypes(ParameterListSyntax parameters)
            {
                var map = new Dictionary<string, CollectionKind>(StringComparer.Ordinal);

                if (parameters == null)
                    return map;

                foreach (var parameter in parameters.Parameters)
                {
                    if (parameter.Type == null)
                        continue;

                    if (TryGetCollectionKindFromTypeSyntax(parameter.Type, out CollectionKind kind))
                    {
                        string name = parameter.Identifier.ValueText;
                        if (!map.ContainsKey(name))
                            map.Add(name, kind);
                    }
                }

                return map;
            }

            private void RecordLocalDeclaration(VariableDeclarationSyntax declaration)
            {
                if (declaration == null || _scopes.Count == 0)
                    return;

                if (TryGetCollectionKindFromTypeSyntax(declaration.Type, out CollectionKind kind))
                {
                    foreach (var variable in declaration.Variables)
                    {
                        string name = variable.Identifier.ValueText;
                        _scopes.Peek()[name] = kind;
                    }
                    return;
                }

                if (declaration.Type is IdentifierNameSyntax id && id.Identifier.ValueText == "var")
                {
                    foreach (var variable in declaration.Variables)
                    {
                        if (variable.Initializer?.Value is ObjectCreationExpressionSyntax creation &&
                            TryGetCollectionKindFromTypeSyntax(creation.Type, out CollectionKind initKind))
                        {
                            string name = variable.Identifier.ValueText;
                            _scopes.Peek()[name] = initKind;
                        }
                    }
                }
            }

            private static bool TryGetCollectionKindFromTypeSyntax(TypeSyntax type, out CollectionKind kind)
            {
                kind = default;
                if (type == null)
                    return false;

                string typeName = GetTypeIdentifier(type);
                if (string.IsNullOrEmpty(typeName))
                    return false;

                switch (typeName)
                {
                    case "List":
                    case "CEList":
                        kind = CollectionKind.ListLike;
                        return true;
                    case "Queue":
                        kind = CollectionKind.Queue;
                        return true;
                    case "Stack":
                        kind = CollectionKind.Stack;
                        return true;
                    default:
                        return false;
                }
            }

            private static string GetTypeIdentifier(TypeSyntax type)
            {
                switch (type)
                {
                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.ValueText;
                    case GenericNameSyntax generic:
                        return generic.Identifier.ValueText;
                    case QualifiedNameSyntax qualified:
                        return GetTypeIdentifier(qualified.Right);
                    case AliasQualifiedNameSyntax alias:
                        return alias.Name.Identifier.ValueText;
                    default:
                        return null;
                }
            }

            private struct LambdaInfo
            {
                public string[] ParameterNames;
                public BlockSyntax BlockBody;
                public ExpressionSyntax ExpressionBody;
            }

            private class ParameterSubstitutionRewriter : CSharpSyntaxRewriter
            {
                private readonly Dictionary<string, string> _replacements;

                public ParameterSubstitutionRewriter(Dictionary<string, string> replacements)
                {
                    _replacements = replacements;
                }

                public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
                {
                    if (_replacements.TryGetValue(node.Identifier.ValueText, out string replacement))
                    {
                        return SyntaxFactory.IdentifierName(replacement).WithTriviaFrom(node);
                    }

                    return base.VisitIdentifierName(node);
                }

                public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
                {
                    var visitedExpression = (ExpressionSyntax)Visit(node.Expression);
                    return node.WithExpression(visitedExpression);
                }

                public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
                {
                    if (node.Expression is IdentifierNameSyntax identifier &&
                        identifier.Identifier.ValueText == "nameof" &&
                        node.ArgumentList.Arguments.Count == 1 &&
                        node.ArgumentList.Arguments[0].Expression is IdentifierNameSyntax argId &&
                        _replacements.ContainsKey(argId.Identifier.ValueText))
                    {
                        return SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(argId.Identifier.ValueText))
                            .WithTriviaFrom(node);
                    }

                    return base.VisitInvocationExpression(node);
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
            }
        }
    }
}
