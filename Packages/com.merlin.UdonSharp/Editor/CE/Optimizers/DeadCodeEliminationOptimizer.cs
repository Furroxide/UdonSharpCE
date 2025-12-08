using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Removes code that can never execute.
    /// 
    /// This optimizer detects and removes:
    /// - if (false) branches
    /// - if (true) else branches
    /// - Code after unconditional return/break/continue
    /// - Empty blocks
    /// 
    /// Examples:
    /// - if (false) { DoSomething(); } → removed entirely
    /// - if (true) { A(); } else { B(); } → A();
    /// - DoWork(); return; CleanupNeverReached(); → DoWork(); return;
    /// </summary>
    internal class DeadCodeEliminationOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT002";
        public string OptimizerName => "Dead Code Elimination";
        public string Description => "Removes code that can never execute to reduce program size.";
        public bool IsEnabledByDefault => true;
        public int Priority => 100; // Standard priority

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new DeadCodeRewriter(context);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private class DeadCodeRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            public bool ChangesMade { get; private set; }

            public DeadCodeRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitIfStatement(IfStatementSyntax node)
            {
                // First visit children
                var visited = (IfStatementSyntax)base.VisitIfStatement(node);

                // Check if condition is a constant true/false
                var conditionValue = GetConstantBoolValue(visited.Condition);

                if (conditionValue == true)
                {
                    // if (true) { ... } else { ... } → just the if body
                    _context.RecordOptimization(
                        "CEOPT002",
                        "Removed always-false else branch",
                        node.GetLocation(),
                        node.ToFullString().Substring(0, System.Math.Min(50, node.ToFullString().Length)) + "...");

                    ChangesMade = true;

                    // Return just the if body (unwrap if it's a block)
                    if (visited.Statement is BlockSyntax block && block.Statements.Count == 1)
                    {
                        return block.Statements[0].WithTriviaFrom(node);
                    }

                    return visited.Statement.WithTriviaFrom(node);
                }
                else if (conditionValue == false)
                {
                    _context.RecordOptimization(
                        "CEOPT002",
                        "Removed always-false if branch",
                        node.GetLocation(),
                        node.ToFullString().Substring(0, System.Math.Min(50, node.ToFullString().Length)) + "...");

                    ChangesMade = true;

                    // if (false) { ... } else { ... } → just the else body (or nothing)
                    if (visited.Else != null)
                    {
                        // Return the else body
                        if (visited.Else.Statement is BlockSyntax elseBlock && elseBlock.Statements.Count == 1)
                        {
                            return elseBlock.Statements[0].WithTriviaFrom(node);
                        }

                        return visited.Else.Statement.WithTriviaFrom(node);
                    }
                    else
                    {
                        // No else clause, return empty statement
                        return SyntaxFactory.EmptyStatement().WithTriviaFrom(node);
                    }
                }

                return visited;
            }

            public override SyntaxNode VisitConditionalExpression(ConditionalExpressionSyntax node)
            {
                // Ternary operator: condition ? whenTrue : whenFalse
                var visited = (ConditionalExpressionSyntax)base.VisitConditionalExpression(node);

                var conditionValue = GetConstantBoolValue(visited.Condition);

                if (conditionValue == true)
                {
                    _context.RecordOptimization(
                        "CEOPT002",
                        "Simplified always-true ternary",
                        node.GetLocation(),
                        node.ToString(),
                        visited.WhenTrue.ToString());

                    ChangesMade = true;
                    return visited.WhenTrue.WithTriviaFrom(node);
                }
                else if (conditionValue == false)
                {
                    _context.RecordOptimization(
                        "CEOPT002",
                        "Simplified always-false ternary",
                        node.GetLocation(),
                        node.ToString(),
                        visited.WhenFalse.ToString());

                    ChangesMade = true;
                    return visited.WhenFalse.WithTriviaFrom(node);
                }

                return visited;
            }

            public override SyntaxNode VisitBlock(BlockSyntax node)
            {
                var visited = (BlockSyntax)base.VisitBlock(node);

                // Remove statements after unconditional returns/breaks/continues
                var statements = visited.Statements;
                var newStatements = new List<StatementSyntax>();
                bool foundTerminator = false;

                foreach (var statement in statements)
                {
                    if (foundTerminator)
                    {
                        // Skip statements after terminator
                        _context.RecordOptimization(
                            "CEOPT002",
                            "Removed unreachable code after return/break/continue",
                            statement.GetLocation(),
                            statement.ToString().Substring(0, System.Math.Min(50, statement.ToString().Length)) + "...");

                        ChangesMade = true;
                        continue;
                    }

                    // Check for empty statements
                    if (statement is EmptyStatementSyntax)
                    {
                        continue;
                    }

                    newStatements.Add(statement);

                    // Check if this statement terminates the block
                    if (IsUnconditionalTerminator(statement))
                    {
                        foundTerminator = true;
                    }
                }

                if (newStatements.Count != statements.Count)
                {
                    return visited.WithStatements(SyntaxFactory.List(newStatements));
                }

                return visited;
            }

            public override SyntaxNode VisitWhileStatement(WhileStatementSyntax node)
            {
                var visited = (WhileStatementSyntax)base.VisitWhileStatement(node);

                // while (false) { ... } → remove entirely
                var conditionValue = GetConstantBoolValue(visited.Condition);

                if (conditionValue == false)
                {
                    _context.RecordOptimization(
                        "CEOPT002",
                        "Removed while(false) loop",
                        node.GetLocation(),
                        "while (false) { ... }");

                    ChangesMade = true;
                    return SyntaxFactory.EmptyStatement().WithTriviaFrom(node);
                }

                return visited;
            }

            private bool? GetConstantBoolValue(ExpressionSyntax expr)
            {
                switch (expr)
                {
                    case LiteralExpressionSyntax literal:
                        if (literal.Kind() == SyntaxKind.TrueLiteralExpression)
                            return true;
                        if (literal.Kind() == SyntaxKind.FalseLiteralExpression)
                            return false;
                        break;

                    case ParenthesizedExpressionSyntax paren:
                        return GetConstantBoolValue(paren.Expression);

                    case PrefixUnaryExpressionSyntax unary when unary.Kind() == SyntaxKind.LogicalNotExpression:
                        var innerValue = GetConstantBoolValue(unary.Operand);
                        if (innerValue.HasValue)
                            return !innerValue.Value;
                        break;

                    case BinaryExpressionSyntax binary:
                        // Handle simple constant comparisons
                        return EvaluateConstantBoolBinary(binary);
                }

                return null;
            }

            private bool? EvaluateConstantBoolBinary(BinaryExpressionSyntax binary)
            {
                var leftValue = GetConstantBoolValue(binary.Left);
                var rightValue = GetConstantBoolValue(binary.Right);

                // Boolean operations
                if (leftValue.HasValue && rightValue.HasValue)
                {
                    switch (binary.Kind())
                    {
                        case SyntaxKind.LogicalAndExpression:
                            return leftValue.Value && rightValue.Value;
                        case SyntaxKind.LogicalOrExpression:
                            return leftValue.Value || rightValue.Value;
                        case SyntaxKind.EqualsExpression:
                            return leftValue.Value == rightValue.Value;
                        case SyntaxKind.NotEqualsExpression:
                            return leftValue.Value != rightValue.Value;
                    }
                }

                // Short-circuit: false && anything = false
                if (binary.Kind() == SyntaxKind.LogicalAndExpression && leftValue == false)
                    return false;

                // Short-circuit: true || anything = true
                if (binary.Kind() == SyntaxKind.LogicalOrExpression && leftValue == true)
                    return true;

                // Check for numeric comparisons
                var leftNum = GetConstantNumericValue(binary.Left);
                var rightNum = GetConstantNumericValue(binary.Right);

                if (leftNum.HasValue && rightNum.HasValue)
                {
                    switch (binary.Kind())
                    {
                        case SyntaxKind.EqualsExpression:
                            return leftNum.Value == rightNum.Value;
                        case SyntaxKind.NotEqualsExpression:
                            return leftNum.Value != rightNum.Value;
                        case SyntaxKind.LessThanExpression:
                            return leftNum.Value < rightNum.Value;
                        case SyntaxKind.LessThanOrEqualExpression:
                            return leftNum.Value <= rightNum.Value;
                        case SyntaxKind.GreaterThanExpression:
                            return leftNum.Value > rightNum.Value;
                        case SyntaxKind.GreaterThanOrEqualExpression:
                            return leftNum.Value >= rightNum.Value;
                    }
                }

                return null;
            }

            private double? GetConstantNumericValue(ExpressionSyntax expr)
            {
                switch (expr)
                {
                    case LiteralExpressionSyntax literal:
                        if (literal.Token.Value is int i)
                            return i;
                        if (literal.Token.Value is long l)
                            return l;
                        if (literal.Token.Value is float f)
                            return f;
                        if (literal.Token.Value is double d)
                            return d;
                        break;

                    case ParenthesizedExpressionSyntax paren:
                        return GetConstantNumericValue(paren.Expression);

                    case PrefixUnaryExpressionSyntax unary when unary.Kind() == SyntaxKind.UnaryMinusExpression:
                        var innerValue = GetConstantNumericValue(unary.Operand);
                        if (innerValue.HasValue)
                            return -innerValue.Value;
                        break;
                }

                return null;
            }

            private bool IsUnconditionalTerminator(StatementSyntax statement)
            {
                switch (statement)
                {
                    case ReturnStatementSyntax _:
                    case ThrowStatementSyntax _:
                        return true;

                    case BreakStatementSyntax _:
                    case ContinueStatementSyntax _:
                        // These only terminate in their containing loop/switch
                        return true;

                    case BlockSyntax block:
                        // A block terminates if its last statement terminates
                        return block.Statements.Count > 0 &&
                               IsUnconditionalTerminator(block.Statements.Last());

                    case IfStatementSyntax ifStmt:
                        // An if terminates only if both branches terminate
                        if (ifStmt.Else == null)
                            return false;

                        return IsUnconditionalTerminator(ifStmt.Statement) &&
                               IsUnconditionalTerminator(ifStmt.Else.Statement);

                    default:
                        return false;
                }
            }
        }
    }
}

