using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Replaces small fixed-iteration loops with straight-line code.
    /// 
    /// This optimizer detects for loops where:
    /// - Iteration count is constant (e.g., for i = 0; i &lt; 4; i++)
    /// - Iteration count ≤ MaxUnrollIterations (default 4)
    /// - Loop body is simple (≤ MaxBodyStatements statements)
    /// - No break/continue/return inside the loop
    /// 
    /// Example:
    /// for (int i = 0; i &lt; 4; i++) { corners[i] = points[i]; }
    /// →
    /// corners[0] = points[0];
    /// corners[1] = points[1];
    /// corners[2] = points[2];
    /// corners[3] = points[3];
    /// </summary>
    internal class SmallLoopUnrollingOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT003";
        public string OptimizerName => "Small Loop Unrolling";
        public string Description => "Unrolls small fixed-iteration loops to eliminate loop overhead.";
        public bool IsEnabledByDefault => true;
        public int Priority => 110; // After dead code elimination

        private const int MaxUnrollIterations = 4;
        private const int MaxBodyStatements = 5;

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new LoopUnrollingRewriter(context);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private class LoopUnrollingRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            public bool ChangesMade { get; private set; }

            public LoopUnrollingRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitForStatement(ForStatementSyntax node)
            {
                // First visit children
                var visited = (ForStatementSyntax)base.VisitForStatement(node);

                // Try to analyze the loop
                var loopInfo = AnalyzeForLoop(visited);
                if (loopInfo == null)
                    return visited;

                // Check if loop is unrollable
                if (!CanUnroll(loopInfo, visited.Statement))
                    return visited;

                // Unroll the loop
                var unrolledStatements = UnrollLoop(loopInfo, visited.Statement);
                if (unrolledStatements == null)
                    return visited;

                _context.RecordOptimization(
                    "CEOPT003",
                    $"Unrolled loop with {loopInfo.IterationCount} iterations",
                    node.GetLocation(),
                    $"for ({loopInfo.VariableName} = {loopInfo.StartValue}; {loopInfo.VariableName} < {loopInfo.EndValue}; ...)",
                    $"{loopInfo.IterationCount} unrolled statements");

                ChangesMade = true;

                // Return a block containing all unrolled statements
                return SyntaxFactory.Block(unrolledStatements).WithTriviaFrom(node);
            }

            private ForLoopInfo AnalyzeForLoop(ForStatementSyntax node)
            {
                // Must have exactly one declaration or initializer
                if (node.Declaration == null || node.Declaration.Variables.Count != 1)
                    return null;

                var declaration = node.Declaration.Variables[0];
                var variableName = declaration.Identifier.Text;

                // Get initial value
                if (declaration.Initializer?.Value == null)
                    return null;

                var startValue = GetConstantIntValue(declaration.Initializer.Value);
                if (!startValue.HasValue)
                    return null;

                // Analyze condition (must be i < N or i <= N)
                if (!(node.Condition is BinaryExpressionSyntax condition))
                    return null;

                // Left side must be the loop variable
                if (!(condition.Left is IdentifierNameSyntax leftId) || leftId.Identifier.Text != variableName)
                    return null;

                var endValue = GetConstantIntValue(condition.Right);
                if (!endValue.HasValue)
                    return null;

                bool isInclusive = condition.Kind() == SyntaxKind.LessThanOrEqualExpression;
                bool isLessThan = condition.Kind() == SyntaxKind.LessThanExpression;

                if (!isInclusive && !isLessThan)
                    return null;

                int actualEndValue = isInclusive ? endValue.Value + 1 : endValue.Value;

                // Analyze incrementor (must be i++ or i += 1)
                if (node.Incrementors.Count != 1)
                    return null;

                var incrementor = node.Incrementors[0];
                if (!IsSimpleIncrement(incrementor, variableName))
                    return null;

                int iterationCount = actualEndValue - startValue.Value;

                return new ForLoopInfo
                {
                    VariableName = variableName,
                    StartValue = startValue.Value,
                    EndValue = actualEndValue,
                    IterationCount = iterationCount
                };
            }

            private bool CanUnroll(ForLoopInfo loopInfo, StatementSyntax body)
            {
                // Check iteration count
                if (loopInfo.IterationCount <= 0 || loopInfo.IterationCount > MaxUnrollIterations)
                    return false;

                // Check body complexity
                int statementCount = CountStatements(body);
                if (statementCount > MaxBodyStatements)
                    return false;

                // Check for break/continue/return
                if (ContainsControlFlow(body))
                    return false;

                return true;
            }

            private SyntaxList<StatementSyntax> UnrollLoop(ForLoopInfo loopInfo, StatementSyntax body)
            {
                var statements = new List<StatementSyntax>();

                for (int i = loopInfo.StartValue; i < loopInfo.EndValue; i++)
                {
                    // Replace all occurrences of the loop variable with the current value
                    var replacer = new VariableReplacer(loopInfo.VariableName, i);
                    var unrolledBody = (StatementSyntax)replacer.Visit(body);

                    // If body is a block, extract its statements
                    if (unrolledBody is BlockSyntax block)
                    {
                        foreach (var stmt in block.Statements)
                        {
                            statements.Add(stmt);
                        }
                    }
                    else
                    {
                        statements.Add(unrolledBody);
                    }
                }

                return SyntaxFactory.List(statements);
            }

            private int? GetConstantIntValue(ExpressionSyntax expr)
            {
                switch (expr)
                {
                    case LiteralExpressionSyntax literal when literal.Token.Value is int i:
                        return i;

                    case ParenthesizedExpressionSyntax paren:
                        return GetConstantIntValue(paren.Expression);

                    case PrefixUnaryExpressionSyntax unary when unary.Kind() == SyntaxKind.UnaryMinusExpression:
                        var inner = GetConstantIntValue(unary.Operand);
                        return inner.HasValue ? -inner.Value : (int?)null;

                    default:
                        return null;
                }
            }

            private bool IsSimpleIncrement(ExpressionSyntax expr, string variableName)
            {
                switch (expr)
                {
                    // i++
                    case PostfixUnaryExpressionSyntax postfix
                        when postfix.Kind() == SyntaxKind.PostIncrementExpression
                             && postfix.Operand is IdentifierNameSyntax id
                             && id.Identifier.Text == variableName:
                        return true;

                    // ++i
                    case PrefixUnaryExpressionSyntax prefix
                        when prefix.Kind() == SyntaxKind.PreIncrementExpression
                             && prefix.Operand is IdentifierNameSyntax id2
                             && id2.Identifier.Text == variableName:
                        return true;

                    // i += 1
                    case AssignmentExpressionSyntax assignment
                        when assignment.Kind() == SyntaxKind.AddAssignmentExpression
                             && assignment.Left is IdentifierNameSyntax leftId
                             && leftId.Identifier.Text == variableName
                             && GetConstantIntValue(assignment.Right) == 1:
                        return true;

                    default:
                        return false;
                }
            }

            private int CountStatements(StatementSyntax statement)
            {
                if (statement is BlockSyntax block)
                    return block.Statements.Count;
                return 1;
            }

            private bool ContainsControlFlow(SyntaxNode node)
            {
                foreach (var descendant in node.DescendantNodes())
                {
                    switch (descendant)
                    {
                        case BreakStatementSyntax _:
                        case ContinueStatementSyntax _:
                        case ReturnStatementSyntax _:
                        case GotoStatementSyntax _:
                        case ThrowStatementSyntax _:
                            return true;

                        // Don't check nested loops/switches - their control flow is contained
                        case ForStatementSyntax _:
                        case ForEachStatementSyntax _:
                        case WhileStatementSyntax _:
                        case DoStatementSyntax _:
                        case SwitchStatementSyntax _:
                            return false;
                    }
                }

                return false;
            }

            private class ForLoopInfo
            {
                public string VariableName { get; set; }
                public int StartValue { get; set; }
                public int EndValue { get; set; }
                public int IterationCount { get; set; }
            }

            private class VariableReplacer : CSharpSyntaxRewriter
            {
                private readonly string _variableName;
                private readonly int _value;

                public VariableReplacer(string variableName, int value)
                {
                    _variableName = variableName;
                    _value = value;
                }

                public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
                {
                    if (node.Identifier.Text == _variableName)
                    {
                        return SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(_value)).WithTriviaFrom(node);
                    }

                    return base.VisitIdentifierName(node);
                }
            }
        }
    }
}

