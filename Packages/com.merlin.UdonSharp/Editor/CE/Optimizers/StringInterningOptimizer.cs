using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Deduplicates identical string literals across files.
    /// 
    /// This optimizer collects all string literals used multiple times and generates
    /// shared const fields for them. This reduces memory usage by avoiding duplicate
    /// string allocations.
    /// 
    /// Note: This optimizer runs in two passes:
    /// 1. First pass collects all string literals and counts occurrences
    /// 2. Second pass (if needed) would add const field declarations
    /// 
    /// For simplicity, this implementation just reports strings that could be interned
    /// and leaves the actual interning to the developer's discretion or future enhancement.
    /// </summary>
    internal class StringInterningOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT005";
        public string OptimizerName => "String Interning";
        public string Description => "Identifies duplicate string literals for potential interning to reduce memory.";
        public bool IsEnabledByDefault => true;
        public int Priority => 200; // Late optimization - runs after others

        private const int MinOccurrencesForInterning = 2;
        private const int MinStringLengthForInterning = 3;

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            // Collect all string literals from this tree
            var collector = new StringLiteralCollector(context);
            collector.Visit(tree.GetRoot());

            // String interning analysis is done across files in the context
            // The actual transformation would be more complex (requiring cross-file coordination)
            // For now, we just record statistics

            return tree; // No transformation in this phase
        }

        private class StringLiteralCollector : CSharpSyntaxWalker
        {
            private readonly OptimizationContext _context;
            private int _internableCount = 0;

            public StringLiteralCollector(OptimizationContext context)
            {
                _context = context;
            }

            public override void VisitLiteralExpression(LiteralExpressionSyntax node)
            {
                if (node.Kind() == SyntaxKind.StringLiteralExpression)
                {
                    var stringValue = node.Token.ValueText;

                    // Skip empty or very short strings
                    if (string.IsNullOrEmpty(stringValue) || stringValue.Length < MinStringLengthForInterning)
                    {
                        base.VisitLiteralExpression(node);
                        return;
                    }

                    // Skip interpolated strings (they're handled differently)
                    if (node.Token.IsKind(SyntaxKind.InterpolatedStringTextToken))
                    {
                        base.VisitLiteralExpression(node);
                        return;
                    }

                    // Track in context
                    if (_context.InternedStrings.TryGetValue(stringValue, out var info))
                    {
                        info.OccurrenceCount++;

                        // Record optimization when we hit the threshold
                        if (info.OccurrenceCount == MinOccurrencesForInterning)
                        {
                            _context.RecordOptimization(
                                "CEOPT005",
                                $"String \"{TruncateString(stringValue)}\" appears {info.OccurrenceCount}+ times - candidate for interning",
                                node.GetLocation(),
                                $"\"{TruncateString(stringValue)}\"");
                        }
                    }
                    else
                    {
                        var fieldName = GenerateFieldName(stringValue);
                        _context.InternedStrings[stringValue] = new StringLiteralInfo
                        {
                            Content = stringValue,
                            FieldName = fieldName,
                            OccurrenceCount = 1,
                            FirstOccurrenceFile = _context.CurrentFilePath
                        };
                    }
                }

                base.VisitLiteralExpression(node);
            }

            private string GenerateFieldName(string content)
            {
                // Generate a deterministic field name from content
                var hash = content.GetHashCode() & 0x7FFFFFFF;
                return $"_ce_str_{hash:X8}";
            }

            private string TruncateString(string s, int maxLength = 30)
            {
                if (s.Length <= maxLength)
                    return s;
                return s.Substring(0, maxLength - 3) + "...";
            }
        }
    }
}

