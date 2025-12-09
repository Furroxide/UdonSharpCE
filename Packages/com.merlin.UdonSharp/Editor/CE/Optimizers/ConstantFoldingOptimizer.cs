using System;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Evaluates constant expressions at compile time.
    /// 
    /// This optimizer detects arithmetic operations, bitwise operations, and other
    /// expressions where all operands are constants and replaces them with their
    /// computed values.
    /// 
    /// Examples:
    /// - 2 * 3 → 6
    /// - 1 | 2 | 4 → 7
    /// - 1 &lt;&lt; 4 → 16
    /// - 2.0f * 3.14159f → 6.28318f
    /// </summary>
    internal class ConstantFoldingOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT001";
        public string OptimizerName => "Constant Folding";
        public string Description => "Evaluates constant expressions at compile time to reduce runtime computation.";
        public bool IsEnabledByDefault => true;
        public int Priority => 10; // Early optimization

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var rewriter = new ConstantFoldingRewriter(context);
            var newRoot = rewriter.Visit(tree.GetRoot());

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        private class ConstantFoldingRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            public bool ChangesMade { get; private set; }

            public ConstantFoldingRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitBinaryExpression(BinaryExpressionSyntax node)
            {
                // First visit children
                var visited = (BinaryExpressionSyntax)base.VisitBinaryExpression(node);

                // Check if both sides are literal expressions
                if (!IsConstantExpression(visited.Left) || !IsConstantExpression(visited.Right))
                    return visited;

                // Try to evaluate the expression
                var result = TryEvaluateBinary(visited);
                if (result != null)
                {
                    var originalText = node.ToString();
                    var optimizedText = result.ToString();

                    // Skip recording if the result is semantically the same
                    if (string.Equals(originalText, optimizedText, StringComparison.OrdinalIgnoreCase))
                        return visited;

                    _context.RecordOptimization(
                        "CEOPT001",
                        $"Folded constant: {originalText} → {optimizedText}",
                        node.GetLocation(),
                        originalText,
                        optimizedText);

                    ChangesMade = true;
                    return result.WithTriviaFrom(node);
                }

                return visited;
            }

            public override SyntaxNode VisitPrefixUnaryExpression(PrefixUnaryExpressionSyntax node)
            {
                var visited = (PrefixUnaryExpressionSyntax)base.VisitPrefixUnaryExpression(node);

                // If the operand is already a simple literal, there's nothing meaningful to fold.
                // E.g., -1, -1f, +5 are already in their simplest form.
                // We only want to fold compound expressions like --1 → 1 or -(1+2) → -3
                if (visited.Operand is LiteralExpressionSyntax)
                    return visited;

                if (!IsConstantExpression(visited.Operand))
                    return visited;

                var result = TryEvaluateUnary(visited);
                if (result != null)
                {
                    var originalText = node.ToString();
                    var optimizedText = result.ToString();

                    // Skip recording if the result is semantically the same (e.g., case normalization)
                    if (string.Equals(originalText, optimizedText, StringComparison.OrdinalIgnoreCase))
                        return visited;

                    _context.RecordOptimization(
                        "CEOPT001",
                        $"Folded constant: {originalText} → {optimizedText}",
                        node.GetLocation(),
                        originalText,
                        optimizedText);

                    ChangesMade = true;
                    return result.WithTriviaFrom(node);
                }

                return visited;
            }

            public override SyntaxNode VisitParenthesizedExpression(ParenthesizedExpressionSyntax node)
            {
                var visited = (ParenthesizedExpressionSyntax)base.VisitParenthesizedExpression(node);

                // If the contents are now a simple literal, remove the parentheses
                if (visited.Expression is LiteralExpressionSyntax)
                {
                    return visited.Expression.WithTriviaFrom(node);
                }

                return visited;
            }

            private bool IsConstantExpression(ExpressionSyntax expr)
            {
                switch (expr)
                {
                    case LiteralExpressionSyntax _:
                        return true;

                    case PrefixUnaryExpressionSyntax unary when
                        unary.Kind() == SyntaxKind.UnaryMinusExpression ||
                        unary.Kind() == SyntaxKind.UnaryPlusExpression ||
                        unary.Kind() == SyntaxKind.BitwiseNotExpression ||
                        unary.Kind() == SyntaxKind.LogicalNotExpression:
                        return IsConstantExpression(unary.Operand);

                    case ParenthesizedExpressionSyntax paren:
                        return IsConstantExpression(paren.Expression);

                    case BinaryExpressionSyntax binary:
                        return IsConstantExpression(binary.Left) && IsConstantExpression(binary.Right);

                    case CastExpressionSyntax cast:
                        return IsConstantExpression(cast.Expression);

                    default:
                        return false;
                }
            }

            private LiteralExpressionSyntax TryEvaluateBinary(BinaryExpressionSyntax node)
            {
                var leftValue = GetConstantValue(node.Left);
                var rightValue = GetConstantValue(node.Right);

                if (leftValue == null || rightValue == null)
                    return null;

                try
                {
                    object result = null;

                    // Integer operations
                    if (leftValue is int leftInt && rightValue is int rightInt)
                    {
                        result = EvaluateIntBinary(node.Kind(), leftInt, rightInt);
                    }
                    // Long operations
                    else if ((leftValue is long || leftValue is int) &&
                             (rightValue is long || rightValue is int))
                    {
                        long leftLong = Convert.ToInt64(leftValue);
                        long rightLong = Convert.ToInt64(rightValue);
                        result = EvaluateLongBinary(node.Kind(), leftLong, rightLong);
                    }
                    // Float operations
                    else if ((leftValue is float || leftValue is int) &&
                             (rightValue is float || rightValue is int))
                    {
                        float leftFloat = Convert.ToSingle(leftValue);
                        float rightFloat = Convert.ToSingle(rightValue);
                        result = EvaluateFloatBinary(node.Kind(), leftFloat, rightFloat);
                    }
                    // Double operations
                    else if ((leftValue is double || leftValue is float || leftValue is int) &&
                             (rightValue is double || rightValue is float || rightValue is int))
                    {
                        double leftDouble = Convert.ToDouble(leftValue);
                        double rightDouble = Convert.ToDouble(rightValue);
                        result = EvaluateDoubleBinary(node.Kind(), leftDouble, rightDouble);
                    }
                    // Boolean operations
                    else if (leftValue is bool leftBool && rightValue is bool rightBool)
                    {
                        result = EvaluateBoolBinary(node.Kind(), leftBool, rightBool);
                    }

                    if (result != null)
                    {
                        return CreateLiteral(result);
                    }
                }
                catch
                {
                    // Evaluation failed (overflow, divide by zero, etc.)
                }

                return null;
            }

            private LiteralExpressionSyntax TryEvaluateUnary(PrefixUnaryExpressionSyntax node)
            {
                var value = GetConstantValue(node.Operand);
                if (value == null)
                    return null;

                try
                {
                    object result = null;

                    switch (node.Kind())
                    {
                        case SyntaxKind.UnaryMinusExpression:
                            if (value is int i)
                                result = -i;
                            else if (value is long l)
                                result = -l;
                            else if (value is float f)
                                result = -f;
                            else if (value is double d)
                                result = -d;
                            break;

                        case SyntaxKind.UnaryPlusExpression:
                            result = value;
                            break;

                        case SyntaxKind.BitwiseNotExpression:
                            if (value is int i2)
                                result = ~i2;
                            else if (value is long l2)
                                result = ~l2;
                            break;

                        case SyntaxKind.LogicalNotExpression:
                            if (value is bool b)
                                result = !b;
                            break;
                    }

                    if (result != null)
                    {
                        return CreateLiteral(result);
                    }
                }
                catch
                {
                    // Evaluation failed
                }

                return null;
            }

            private object GetConstantValue(ExpressionSyntax expr)
            {
                switch (expr)
                {
                    case LiteralExpressionSyntax literal:
                        return literal.Token.Value;

                    case PrefixUnaryExpressionSyntax unary:
                        var operandValue = GetConstantValue(unary.Operand);
                        if (operandValue == null)
                            return null;

                        switch (unary.Kind())
                        {
                            case SyntaxKind.UnaryMinusExpression:
                                if (operandValue is int i)
                                    return -i;
                                if (operandValue is long l)
                                    return -l;
                                if (operandValue is float f)
                                    return -f;
                                if (operandValue is double d)
                                    return -d;
                                break;

                            case SyntaxKind.UnaryPlusExpression:
                                return operandValue;

                            case SyntaxKind.BitwiseNotExpression:
                                if (operandValue is int i2)
                                    return ~i2;
                                if (operandValue is long l2)
                                    return ~l2;
                                break;

                            case SyntaxKind.LogicalNotExpression:
                                if (operandValue is bool b)
                                    return !b;
                                break;
                        }
                        return null;

                    case ParenthesizedExpressionSyntax paren:
                        return GetConstantValue(paren.Expression);

                    case CastExpressionSyntax cast:
                        // Simple cast handling for numeric types
                        var innerValue = GetConstantValue(cast.Expression);
                        if (innerValue == null)
                            return null;

                        var typeName = cast.Type.ToString();
                        try
                        {
                            switch (typeName)
                            {
                                case "int":
                                    return Convert.ToInt32(innerValue);
                                case "long":
                                    return Convert.ToInt64(innerValue);
                                case "float":
                                    return Convert.ToSingle(innerValue);
                                case "double":
                                    return Convert.ToDouble(innerValue);
                                case "byte":
                                    return (int)Convert.ToByte(innerValue);
                                case "short":
                                    return (int)Convert.ToInt16(innerValue);
                            }
                        }
                        catch
                        {
                            return null;
                        }
                        return null;

                    default:
                        return null;
                }
            }

            private object EvaluateIntBinary(SyntaxKind kind, int left, int right)
            {
                switch (kind)
                {
                    case SyntaxKind.AddExpression:
                        return checked(left + right);
                    case SyntaxKind.SubtractExpression:
                        return checked(left - right);
                    case SyntaxKind.MultiplyExpression:
                        return checked(left * right);
                    case SyntaxKind.DivideExpression:
                        return left / right;
                    case SyntaxKind.ModuloExpression:
                        return left % right;
                    case SyntaxKind.LeftShiftExpression:
                        return left << right;
                    case SyntaxKind.RightShiftExpression:
                        return left >> right;
                    case SyntaxKind.BitwiseAndExpression:
                        return left & right;
                    case SyntaxKind.BitwiseOrExpression:
                        return left | right;
                    case SyntaxKind.ExclusiveOrExpression:
                        return left ^ right;
                    case SyntaxKind.EqualsExpression:
                        return left == right;
                    case SyntaxKind.NotEqualsExpression:
                        return left != right;
                    case SyntaxKind.LessThanExpression:
                        return left < right;
                    case SyntaxKind.LessThanOrEqualExpression:
                        return left <= right;
                    case SyntaxKind.GreaterThanExpression:
                        return left > right;
                    case SyntaxKind.GreaterThanOrEqualExpression:
                        return left >= right;
                    default:
                        return null;
                }
            }

            private object EvaluateLongBinary(SyntaxKind kind, long left, long right)
            {
                switch (kind)
                {
                    case SyntaxKind.AddExpression:
                        return checked(left + right);
                    case SyntaxKind.SubtractExpression:
                        return checked(left - right);
                    case SyntaxKind.MultiplyExpression:
                        return checked(left * right);
                    case SyntaxKind.DivideExpression:
                        return left / right;
                    case SyntaxKind.ModuloExpression:
                        return left % right;
                    case SyntaxKind.LeftShiftExpression:
                        return left << (int)right;
                    case SyntaxKind.RightShiftExpression:
                        return left >> (int)right;
                    case SyntaxKind.BitwiseAndExpression:
                        return left & right;
                    case SyntaxKind.BitwiseOrExpression:
                        return left | right;
                    case SyntaxKind.ExclusiveOrExpression:
                        return left ^ right;
                    default:
                        return null;
                }
            }

            private object EvaluateFloatBinary(SyntaxKind kind, float left, float right)
            {
                switch (kind)
                {
                    case SyntaxKind.AddExpression:
                        return left + right;
                    case SyntaxKind.SubtractExpression:
                        return left - right;
                    case SyntaxKind.MultiplyExpression:
                        return left * right;
                    case SyntaxKind.DivideExpression:
                        return left / right;
                    case SyntaxKind.ModuloExpression:
                        return left % right;
                    case SyntaxKind.EqualsExpression:
                        return left == right;
                    case SyntaxKind.NotEqualsExpression:
                        return left != right;
                    case SyntaxKind.LessThanExpression:
                        return left < right;
                    case SyntaxKind.LessThanOrEqualExpression:
                        return left <= right;
                    case SyntaxKind.GreaterThanExpression:
                        return left > right;
                    case SyntaxKind.GreaterThanOrEqualExpression:
                        return left >= right;
                    default:
                        return null;
                }
            }

            private object EvaluateDoubleBinary(SyntaxKind kind, double left, double right)
            {
                switch (kind)
                {
                    case SyntaxKind.AddExpression:
                        return left + right;
                    case SyntaxKind.SubtractExpression:
                        return left - right;
                    case SyntaxKind.MultiplyExpression:
                        return left * right;
                    case SyntaxKind.DivideExpression:
                        return left / right;
                    case SyntaxKind.ModuloExpression:
                        return left % right;
                    default:
                        return null;
                }
            }

            private object EvaluateBoolBinary(SyntaxKind kind, bool left, bool right)
            {
                switch (kind)
                {
                    case SyntaxKind.LogicalAndExpression:
                        return left && right;
                    case SyntaxKind.LogicalOrExpression:
                        return left || right;
                    case SyntaxKind.EqualsExpression:
                        return left == right;
                    case SyntaxKind.NotEqualsExpression:
                        return left != right;
                    case SyntaxKind.BitwiseAndExpression:
                        return left & right;
                    case SyntaxKind.BitwiseOrExpression:
                        return left | right;
                    case SyntaxKind.ExclusiveOrExpression:
                        return left ^ right;
                    default:
                        return null;
                }
            }

            private LiteralExpressionSyntax CreateLiteral(object value)
            {
                switch (value)
                {
                    case int i:
                        return SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(i));

                    case long l:
                        return SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(l));

                    case float f:
                        return SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(f));

                    case double d:
                        return SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(d));

                    case bool b:
                        return SyntaxFactory.LiteralExpression(
                            b ? SyntaxKind.TrueLiteralExpression : SyntaxKind.FalseLiteralExpression);

                    default:
                        return null;
                }
            }
        }
    }
}

