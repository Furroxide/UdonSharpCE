using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.CE.Editor.Optimizers;

namespace UdonSharp.CE.Editor.Async
{
    /// <summary>
    /// Compile-time optimizer that transforms async UdonTask methods into state machines.
    /// 
    /// This optimizer runs very early (priority 1) to transform await expressions before
    /// UdonSharp's compiler sees them, since UdonSharp doesn't support AwaitExpression nodes.
    /// 
    /// Transforms:
    /// <code>
    /// public async UdonTask PlayCutscene(float duration)
    /// {
    ///     await UdonTask.Delay(1f);
    ///     DoSomething(duration);
    ///     await UdonTask.Delay(2f);
    /// }
    /// </code>
    /// 
    /// Into:
    /// <code>
    /// private int __PlayCutscene_state;
    /// private float __PlayCutscene_duration; // Hoisted parameter
    /// 
    /// public UdonTask PlayCutscene(float duration)
    /// {
    ///     __PlayCutscene_state = 0;
    ///     __PlayCutscene_duration = duration; // Copy parameter to field
    ///     __PlayCutscene_MoveNext();
    ///     return UdonTask.CompletedTask;
    /// }
    /// 
    /// public void __PlayCutscene_MoveNext()
    /// {
    ///     switch (__PlayCutscene_state)
    ///     {
    ///         case 0:
    ///             __PlayCutscene_state = 1;
    ///             SendCustomEventDelayedSeconds("__PlayCutscene_MoveNext", 1f);
    ///             return;
    ///         case 1:
    ///             DoSomething(__PlayCutscene_duration); // Use hoisted field
    ///             __PlayCutscene_state = 2;
    ///             SendCustomEventDelayedSeconds("__PlayCutscene_MoveNext", 2f);
    ///             return;
    ///         case 2:
    ///             return;
    ///     }
    /// }
    /// </code>
    /// </summary>
    internal class AsyncMethodTransformOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEOPT008";
        public string OptimizerName => "Async Method Transformer";
        public string Description => "Transforms async UdonTask methods into state machines using SendCustomEventDelayed*.";
        public bool IsEnabledByDefault => true;
        public int Priority => 1; // Very early - must run before other transforms see await expressions

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var root = tree.GetRoot();
            var rewriter = new AsyncMethodRewriter(context);
            var newRoot = rewriter.Visit(root);

            if (rewriter.ChangesMade)
            {
                return tree.WithRootAndOptions(newRoot, tree.Options);
            }

            return tree;
        }

        /// <summary>
        /// Represents a variable (parameter or local) that needs to be hoisted to a field.
        /// </summary>
        private class HoistedVariable
        {
            public string OriginalName { get; set; }
            public string FieldName { get; set; }
            public TypeSyntax Type { get; set; }
            public bool IsParameter { get; set; }
        }

        private class AsyncMethodRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly List<MemberDeclarationSyntax> _membersToAdd = new List<MemberDeclarationSyntax>();
            public bool ChangesMade { get; private set; }

            public AsyncMethodRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                _membersToAdd.Clear();
                
                // First, visit all members to transform methods and collect new members
                var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);

                // If we have members to add, add them to the class
                if (_membersToAdd.Count > 0)
                {
                    visited = visited.AddMembers(_membersToAdd.ToArray());
                    _membersToAdd.Clear();
                }

                return visited;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                // Check if this method returns UdonTask or UdonTask<T>
                string returnType = node.ReturnType.ToString();
                if (!returnType.Contains("UdonTask"))
                {
                    return base.VisitMethodDeclaration(node);
                }

                // Check if it has any await expressions
                var awaitExpressions = node.DescendantNodes()
                    .OfType<AwaitExpressionSyntax>()
                    .ToList();

                if (awaitExpressions.Count == 0)
                {
                    return base.VisitMethodDeclaration(node);
                }

                // Remove 'async' modifier if present (UdonTask methods shouldn't have it)
                var modifiers = node.Modifiers;
                var asyncModifier = modifiers.FirstOrDefault(m => m.IsKind(SyntaxKind.AsyncKeyword));
                if (asyncModifier != default)
                {
                    modifiers = modifiers.Remove(asyncModifier);
                }

                // Transform this method
                ChangesMade = true;
                _context.RecordOptimization(
                    "CEOPT008",
                    $"Transforming async method '{node.Identifier.Text}' with {awaitExpressions.Count} await(s)",
                    node.GetLocation(),
                    node.Identifier.Text,
                    "");

                return TransformAsyncMethod(node, modifiers, awaitExpressions);
            }

            private SyntaxNode TransformAsyncMethod(
                MethodDeclarationSyntax method,
                SyntaxTokenList modifiers,
                List<AwaitExpressionSyntax> awaitExpressions)
            {
                string methodName = method.Identifier.Text;
                string stateFieldName = $"__{methodName}_state";
                string moveNextName = $"__{methodName}_MoveNext";
                string returnType = method.ReturnType.ToString();

                // Collect variables that need to be hoisted
                var hoistedVariables = CollectHoistedVariables(method, methodName, awaitExpressions);

                // Generate the state field
                var stateField = SyntaxFactory.FieldDeclaration(
                    SyntaxFactory.VariableDeclaration(
                        SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)))
                    .WithVariables(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.VariableDeclarator(stateFieldName))))
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

                _membersToAdd.Add(stateField);

                // Generate fields for hoisted variables (parameters and locals)
                foreach (var hoisted in hoistedVariables)
                {
                    var field = SyntaxFactory.FieldDeclaration(
                        SyntaxFactory.VariableDeclaration(hoisted.Type)
                        .WithVariables(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.VariableDeclarator(hoisted.FieldName))))
                        .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

                    _membersToAdd.Add(field);
                }

                // Generate the MoveNext method
                var moveNextMethod = GenerateMoveNextMethod(method, stateFieldName, moveNextName, awaitExpressions, hoistedVariables);
                _membersToAdd.Add(moveNextMethod);

                // Generate the entry method (replaces the original)
                var entryMethod = GenerateEntryMethod(method, modifiers, stateFieldName, moveNextName, returnType, hoistedVariables);

                return entryMethod;
            }

            /// <summary>
            /// Collects all variables (parameters and locals) that need to be hoisted to fields
            /// because they are used across await points.
            /// </summary>
            private List<HoistedVariable> CollectHoistedVariables(
                MethodDeclarationSyntax method,
                string methodName,
                List<AwaitExpressionSyntax> awaitExpressions)
            {
                var hoistedVariables = new List<HoistedVariable>();

                if (method.Body == null || awaitExpressions.Count == 0)
                    return hoistedVariables;

                // Get await positions for cross-reference checking
                var awaitPositions = awaitExpressions.Select(a => a.SpanStart).ToList();

                // Hoist ALL parameters - they may be used after any await
                foreach (var parameter in method.ParameterList.Parameters)
                {
                    string paramName = parameter.Identifier.Text;
                    
                    // Check if parameter is used anywhere in the method body
                    var usages = method.Body.DescendantNodes()
                        .OfType<IdentifierNameSyntax>()
                        .Where(id => id.Identifier.Text == paramName);

                    if (usages.Any())
                    {
                        hoistedVariables.Add(new HoistedVariable
                        {
                            OriginalName = paramName,
                            FieldName = $"__{methodName}_{paramName}",
                            Type = parameter.Type,
                            IsParameter = true
                        });
                    }
                }

                // Find local variables that need to be hoisted (used across await points)
                var localDeclarations = method.Body
                    .DescendantNodes()
                    .OfType<LocalDeclarationStatementSyntax>();

                foreach (var localDecl in localDeclarations)
                {
                    foreach (var variable in localDecl.Declaration.Variables)
                    {
                        string varName = variable.Identifier.Text;
                        int declPosition = variable.SpanStart;

                        // Check if this variable is used after any await point that comes after its declaration
                        bool crossesAwait = false;

                        foreach (var awaitPos in awaitPositions)
                        {
                            if (awaitPos > declPosition)
                            {
                                // Check if the variable is used after this await
                                var usagesAfterAwait = method.Body
                                    .DescendantNodes()
                                    .OfType<IdentifierNameSyntax>()
                                    .Where(id => id.Identifier.Text == varName && id.SpanStart > awaitPos);

                                if (usagesAfterAwait.Any())
                                {
                                    crossesAwait = true;
                                    break;
                                }
                            }
                        }

                        if (crossesAwait)
                        {
                            hoistedVariables.Add(new HoistedVariable
                            {
                                OriginalName = varName,
                                FieldName = $"__{methodName}_{varName}",
                                Type = localDecl.Declaration.Type,
                                IsParameter = false
                            });
                        }
                    }
                }

                return hoistedVariables;
            }

            private MethodDeclarationSyntax GenerateEntryMethod(
                MethodDeclarationSyntax original,
                SyntaxTokenList modifiers,
                string stateFieldName,
                string moveNextName,
                string returnType,
                List<HoistedVariable> hoistedVariables)
            {
                var statements = new List<StatementSyntax>();

                // __Method_state = 0;
                statements.Add(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SyntaxFactory.IdentifierName(stateFieldName),
                            SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression, 
                                SyntaxFactory.Literal(0)))));

                // Copy parameters to hoisted fields: __Method_param = param;
                foreach (var hoisted in hoistedVariables.Where(h => h.IsParameter))
                {
                    statements.Add(
                        SyntaxFactory.ExpressionStatement(
                            SyntaxFactory.AssignmentExpression(
                                SyntaxKind.SimpleAssignmentExpression,
                                SyntaxFactory.IdentifierName(hoisted.FieldName),
                                SyntaxFactory.IdentifierName(hoisted.OriginalName))));
                }

                // __Method_MoveNext();
                statements.Add(
                    SyntaxFactory.ExpressionStatement(
                        SyntaxFactory.InvocationExpression(
                            SyntaxFactory.IdentifierName(moveNextName))));

                // return UdonTask.CompletedTask; or return new UdonTask();
                statements.Add(
                    SyntaxFactory.ReturnStatement(
                        returnType.Contains("<")
                            ? (ExpressionSyntax)SyntaxFactory.ObjectCreationExpression(
                                SyntaxFactory.ParseTypeName(returnType))
                                .WithArgumentList(SyntaxFactory.ArgumentList())
                            : SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("UdonTask"),
                                SyntaxFactory.IdentifierName("CompletedTask"))));

                return SyntaxFactory.MethodDeclaration(original.ReturnType, original.Identifier)
                    .WithModifiers(modifiers)
                    .WithParameterList(original.ParameterList)
                    .WithBody(SyntaxFactory.Block(statements))
                    .WithLeadingTrivia(original.GetLeadingTrivia())
                    .WithTrailingTrivia(original.GetTrailingTrivia());
            }

            private MethodDeclarationSyntax GenerateMoveNextMethod(
                MethodDeclarationSyntax original,
                string stateFieldName,
                string moveNextName,
                List<AwaitExpressionSyntax> awaitExpressions,
                List<HoistedVariable> hoistedVariables)
            {
                // Build the switch statement with cases for each state
                var switchSections = new List<SwitchSectionSyntax>();

                // Get the method body
                if (original.Body == null)
                {
                    // Expression-bodied method - not supported
                    return CreateFallbackMoveNext(moveNextName);
                }

                // Create identifier replacer for hoisted variables
                var identifierReplacements = hoistedVariables.ToDictionary(
                    h => h.OriginalName,
                    h => h.FieldName);

                // Parse the body into segments separated by await expressions
                var segments = SplitByAwaitExpressions(original.Body, awaitExpressions, hoistedVariables);

                for (int i = 0; i < segments.Count; i++)
                {
                    var segment = segments[i];
                    var caseStatements = new List<StatementSyntax>();

                    // Add the code segment (with replaced identifiers)
                    foreach (var stmt in segment.Statements)
                    {
                        var replacedStmt = ReplaceIdentifiers(stmt, identifierReplacements);
                        caseStatements.Add(replacedStmt);
                    }

                    if (i < awaitExpressions.Count)
                    {
                        // This segment ends with an await - add state transition and delay
                        var awaitExpr = awaitExpressions[i];
                        var delayInfo = ExtractDelayInfo(awaitExpr, identifierReplacements);

                        // __Method_state = nextState;
                        caseStatements.Add(
                            SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    SyntaxFactory.IdentifierName(stateFieldName),
                                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                                        SyntaxFactory.Literal(i + 1)))));

                        // SendCustomEventDelayedSeconds/Frames("__Method_MoveNext", delay);
                        // Note: Using string literal instead of nameof for better compatibility
                        caseStatements.Add(
                            SyntaxFactory.ExpressionStatement(
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.IdentifierName(delayInfo.MethodName))
                                .WithArgumentList(
                                    SyntaxFactory.ArgumentList(
                                        SyntaxFactory.SeparatedList(new[]
                                        {
                                            SyntaxFactory.Argument(
                                                SyntaxFactory.LiteralExpression(
                                                    SyntaxKind.StringLiteralExpression,
                                                    SyntaxFactory.Literal(moveNextName))),
                                            SyntaxFactory.Argument(delayInfo.DelayExpression)
                                        })))));

                        // return;
                        caseStatements.Add(SyntaxFactory.ReturnStatement());
                    }
                    else
                    {
                        // Final segment - just return
                        caseStatements.Add(SyntaxFactory.ReturnStatement());
                    }

                    // Create the switch section
                    var section = SyntaxFactory.SwitchSection()
                        .WithLabels(
                            SyntaxFactory.SingletonList<SwitchLabelSyntax>(
                                SyntaxFactory.CaseSwitchLabel(
                                    SyntaxFactory.LiteralExpression(SyntaxKind.NumericLiteralExpression,
                                        SyntaxFactory.Literal(i)))))
                        .WithStatements(SyntaxFactory.List(caseStatements));

                    switchSections.Add(section);
                }

                // Create the switch statement
                var switchStatement = SyntaxFactory.SwitchStatement(
                    SyntaxFactory.IdentifierName(stateFieldName))
                    .WithSections(SyntaxFactory.List(switchSections));

                return SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    moveNextName)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithBody(SyntaxFactory.Block(switchStatement));
            }

            /// <summary>
            /// Replaces identifiers in a statement with their hoisted field names.
            /// </summary>
            private StatementSyntax ReplaceIdentifiers(
                StatementSyntax statement,
                Dictionary<string, string> replacements)
            {
                var replacer = new IdentifierReplacer(replacements);
                return (StatementSyntax)replacer.Visit(statement);
            }

            /// <summary>
            /// Replaces identifiers in an expression with their hoisted field names.
            /// </summary>
            private ExpressionSyntax ReplaceIdentifiers(
                ExpressionSyntax expression,
                Dictionary<string, string> replacements)
            {
                var replacer = new IdentifierReplacer(replacements);
                return (ExpressionSyntax)replacer.Visit(expression);
            }

            private MethodDeclarationSyntax CreateFallbackMoveNext(string moveNextName)
            {
                // Create a simple fallback for expression-bodied methods
                return SyntaxFactory.MethodDeclaration(
                    SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
                    moveNextName)
                    .WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                    .WithBody(SyntaxFactory.Block());
            }

            private class CodeSegment
            {
                public List<StatementSyntax> Statements { get; } = new List<StatementSyntax>();
            }

            private List<CodeSegment> SplitByAwaitExpressions(
                BlockSyntax body,
                List<AwaitExpressionSyntax> awaitExpressions,
                List<HoistedVariable> hoistedVariables)
            {
                var segments = new List<CodeSegment>();
                var currentSegment = new CodeSegment();
                var awaitSet = new HashSet<AwaitExpressionSyntax>(awaitExpressions);
                var hoistedLocalNames = new HashSet<string>(
                    hoistedVariables.Where(h => !h.IsParameter).Select(h => h.OriginalName));

                foreach (var statement in body.Statements)
                {
                    // Check if this is a local declaration for a hoisted variable
                    // If so, we need to transform it to an assignment instead
                    if (statement is LocalDeclarationStatementSyntax localDecl)
                    {
                        bool isHoisted = localDecl.Declaration.Variables
                            .Any(v => hoistedLocalNames.Contains(v.Identifier.Text));

                        if (isHoisted)
                        {
                            // Transform local declarations of hoisted variables to assignments
                            foreach (var variable in localDecl.Declaration.Variables)
                            {
                                if (hoistedLocalNames.Contains(variable.Identifier.Text) && 
                                    variable.Initializer != null)
                                {
                                    // Find the hoisted variable to get the field name
                                    var hoisted = hoistedVariables.First(h => h.OriginalName == variable.Identifier.Text);
                                    
                                    // Create assignment: __Method_varName = initializer;
                                    var assignment = SyntaxFactory.ExpressionStatement(
                                        SyntaxFactory.AssignmentExpression(
                                            SyntaxKind.SimpleAssignmentExpression,
                                            SyntaxFactory.IdentifierName(hoisted.FieldName),
                                            variable.Initializer.Value));

                                    currentSegment.Statements.Add(assignment);
                                }
                            }
                            continue;
                        }
                    }

                    // Check if this statement contains an await expression
                    var awaitInStatement = statement.DescendantNodes()
                        .OfType<AwaitExpressionSyntax>()
                        .FirstOrDefault(a => awaitSet.Contains(a));

                    if (awaitInStatement != null)
                    {
                        // Handle statement with await
                        // For simple "await X;" statements, just start new segment
                        // For complex statements like "var x = await Y;", we need special handling
                        
                        if (statement is ExpressionStatementSyntax exprStmt &&
                            exprStmt.Expression is AwaitExpressionSyntax)
                        {
                            // Simple "await X;" - just end current segment
                            segments.Add(currentSegment);
                            currentSegment = new CodeSegment();
                        }
                        else
                        {
                            // Complex statement - remove the await and add to current segment
                            var transformedStatement = RemoveAwaitFromStatement(statement, awaitInStatement);
                            if (transformedStatement != null)
                            {
                                // Add any pre-await code to current segment
                                segments.Add(currentSegment);
                                currentSegment = new CodeSegment();
                                // Add the transformed statement to the next segment
                                currentSegment.Statements.Add(transformedStatement);
                            }
                            else
                            {
                                segments.Add(currentSegment);
                                currentSegment = new CodeSegment();
                            }
                        }
                    }
                    else
                    {
                        // No await - add to current segment
                        currentSegment.Statements.Add(statement);
                    }
                }

                // Add final segment
                segments.Add(currentSegment);

                return segments;
            }

            private StatementSyntax RemoveAwaitFromStatement(
                StatementSyntax statement,
                AwaitExpressionSyntax awaitExpr)
            {
                // For "var x = await Y;", we need to transform to handle the result
                // For now, just remove the statement (simplified)
                // TODO: Proper handling of await result assignment
                return null;
            }

            private (string MethodName, ExpressionSyntax DelayExpression) ExtractDelayInfo(
                AwaitExpressionSyntax awaitExpr,
                Dictionary<string, string> identifierReplacements)
            {
                var expression = awaitExpr.Expression;

                // Check for UdonTask.Delay(seconds)
                if (expression is InvocationExpressionSyntax invocation)
                {
                    var memberAccess = invocation.Expression as MemberAccessExpressionSyntax;
                    if (memberAccess != null)
                    {
                        string methodName = memberAccess.Name.Identifier.Text;

                        if (methodName == "Delay")
                        {
                            var arg = invocation.ArgumentList.Arguments.FirstOrDefault();
                            var delayExpr = arg?.Expression ?? SyntaxFactory.LiteralExpression(
                                SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(0f));
                            
                            // Replace identifiers in delay expression
                            delayExpr = ReplaceIdentifiers(delayExpr, identifierReplacements);
                            
                            return ("SendCustomEventDelayedSeconds", delayExpr);
                        }
                        else if (methodName == "DelayFrames")
                        {
                            var arg = invocation.ArgumentList.Arguments.FirstOrDefault();
                            var delayExpr = arg?.Expression ?? SyntaxFactory.LiteralExpression(
                                SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1));
                            
                            // Replace identifiers in delay expression
                            delayExpr = ReplaceIdentifiers(delayExpr, identifierReplacements);
                            
                            return ("SendCustomEventDelayedFrames", delayExpr);
                        }
                        else if (methodName == "Yield")
                        {
                            return ("SendCustomEventDelayedFrames",
                                SyntaxFactory.LiteralExpression(
                                    SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));
                        }
                    }
                }

                // Default: yield next frame
                return ("SendCustomEventDelayedFrames",
                    SyntaxFactory.LiteralExpression(
                        SyntaxKind.NumericLiteralExpression, SyntaxFactory.Literal(1)));
            }

            /// <summary>
            /// Syntax rewriter that replaces identifier names with their hoisted field names.
            /// </summary>
            private class IdentifierReplacer : CSharpSyntaxRewriter
            {
                private readonly Dictionary<string, string> _replacements;

                public IdentifierReplacer(Dictionary<string, string> replacements)
                {
                    _replacements = replacements;
                }

                public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
                {
                    string identifier = node.Identifier.Text;

                    if (_replacements.TryGetValue(identifier, out string replacement))
                    {
                        return SyntaxFactory.IdentifierName(replacement)
                            .WithLeadingTrivia(node.GetLeadingTrivia())
                            .WithTrailingTrivia(node.GetTrailingTrivia());
                    }

                    return base.VisitIdentifierName(node);
                }
            }
        }
    }
}
