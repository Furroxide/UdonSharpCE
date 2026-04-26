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
    /// Compile-time optimizer that lowers async UdonTask methods into event-driven state machines.
    /// </summary>
    internal class AsyncMethodTransformOptimizer : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEASYNC001";
        public string OptimizerName => "Async Method Transformer";
        public string Description => "Transforms async UdonTask methods into state machines using SendCustomEventDelayed*.";
        public bool IsEnabledByDefault => true;
        public int Priority => 1;

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            var root = tree.GetRoot();
            var rewriter = new AsyncMethodRewriter(context);
            var newRoot = rewriter.Visit(root);

            return rewriter.ChangesMade
                ? tree.WithRootAndOptions(newRoot.NormalizeWhitespace(), tree.Options)
                : tree;
        }

        private sealed class AsyncMethodRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly Stack<HashSet<string>> _asyncMethodNames = new Stack<HashSet<string>>();
            private readonly List<MemberDeclarationSyntax> _membersToAdd = new List<MemberDeclarationSyntax>();

            public bool ChangesMade { get; private set; }

            public AsyncMethodRewriter(OptimizationContext context)
            {
                _context = context;
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                HashSet<string> asyncNames = new HashSet<string>(
                    node.Members
                        .OfType<MethodDeclarationSyntax>()
                        .Where(IsTransformableAsyncMethod)
                        .Select(method => method.Identifier.ValueText),
                    StringComparer.Ordinal);

                _asyncMethodNames.Push(asyncNames);
                _membersToAdd.Clear();

                var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);

                if (_membersToAdd.Count > 0)
                {
                    visited = visited.AddMembers(_membersToAdd.ToArray());
                    _membersToAdd.Clear();
                }

                _asyncMethodNames.Pop();
                return visited;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                if (!IsTransformableAsyncMethod(node))
                {
                    return base.VisitMethodDeclaration(node);
                }

                var generator = new MethodGenerator(
                    node,
                    _asyncMethodNames.Count > 0 ? _asyncMethodNames.Peek() : new HashSet<string>(),
                    _context);

                MethodDeclarationSyntax entryMethod = generator.GenerateEntryMethod();
                _membersToAdd.AddRange(generator.GenerateSupportMembers());

                ChangesMade = true;
                _context.RecordOptimization(
                    "CEASYNC001",
                    $"Transforming async method '{node.Identifier.ValueText}' with {generator.AwaitCount} await(s)",
                    node.GetLocation(),
                    node.Identifier.ValueText,
                    "");

                return entryMethod;
            }

            private static bool IsTransformableAsyncMethod(MethodDeclarationSyntax method)
            {
                if (method == null || method.Body == null)
                    return false;

                string returnType = method.ReturnType.ToString();
                if (!returnType.Contains("UdonTask"))
                    return false;

                return method.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();
            }
        }

        private sealed class MethodGenerator
        {
            private readonly MethodDeclarationSyntax _method;
            private readonly HashSet<string> _asyncMethodNames;
            private readonly Dictionary<string, string> _identifierReplacements = new Dictionary<string, string>(StringComparer.Ordinal);
            private readonly List<HoistedVariable> _hoistedVariables = new List<HoistedVariable>();
            private readonly List<string> _fieldDeclarations = new List<string>();
            private readonly List<string> _switchSections = new List<string>();
            private readonly string _methodName;
            private readonly string _stateFieldName;
            private readonly string _continuationFieldName;
            private readonly string _startMethodName;
            private readonly string _moveNextMethodName;
            private readonly string _completeMethodName;
            private int _nextState = 1;

            public int AwaitCount { get; }

            public MethodGenerator(
                MethodDeclarationSyntax method,
                HashSet<string> asyncMethodNames,
                OptimizationContext context)
            {
                _method = method;
                _asyncMethodNames = asyncMethodNames;
                _methodName = method.Identifier.ValueText;
                _stateFieldName = $"__{_methodName}_state";
                _continuationFieldName = $"__{_methodName}_continuation";
                _startMethodName = $"__{_methodName}_Start";
                _moveNextMethodName = $"__{_methodName}_MoveNext";
                _completeMethodName = $"__{_methodName}_Complete";
                AwaitCount = method.DescendantNodes().OfType<AwaitExpressionSyntax>().Count();

                CollectHoistedVariables();
                BuildFieldDeclarations();
            }

            public MethodDeclarationSyntax GenerateEntryMethod()
            {
                string modifiers = ModifiersWithoutAsync(_method.Modifiers);
                string returnType = _method.ReturnType.ToString();
                string parameterList = _method.ParameterList.ToString();
                string arguments = string.Join(", ", _method.ParameterList.Parameters.Select(p => p.Identifier.ValueText));
                if (!string.IsNullOrEmpty(arguments))
                    arguments += ", ";

                string source =
                    $"{modifiers} {returnType} {_methodName}{parameterList}\n" +
                    "{\n" +
                    $"    {_startMethodName}({arguments}\"\");\n" +
                    "    return UdonTask.CompletedTask;\n" +
                    "}";

                return (MethodDeclarationSyntax)SyntaxFactory.ParseMemberDeclaration(source)
                    .WithLeadingTrivia(_method.GetLeadingTrivia())
                    .WithTrailingTrivia(_method.GetTrailingTrivia());
            }

            public IEnumerable<MemberDeclarationSyntax> GenerateSupportMembers()
            {
                var members = new List<MemberDeclarationSyntax>();

                foreach (string field in _fieldDeclarations)
                    members.Add(ParseMember(field));

                members.Add(ParseMember(GenerateStartMethodSource()));
                members.Add(ParseMember(GenerateMoveNextMethodSource()));
                members.Add(ParseMember(GenerateCompleteMethodSource()));

                return members;
            }

            private void CollectHoistedVariables()
            {
                foreach (var parameter in _method.ParameterList.Parameters)
                {
                    string name = parameter.Identifier.ValueText;
                    if (string.IsNullOrEmpty(name))
                        continue;

                    string fieldName = $"__{_methodName}_{name}";
                    _identifierReplacements[name] = fieldName;
                    _hoistedVariables.Add(new HoistedVariable(name, fieldName, parameter.Type?.ToString() ?? "object"));
                }

                foreach (var local in _method.Body.DescendantNodes().OfType<LocalDeclarationStatementSyntax>())
                {
                    string typeName = local.Declaration.Type.ToString();
                    if (string.Equals(typeName, "var", StringComparison.Ordinal))
                        continue;

                    foreach (var variable in local.Declaration.Variables)
                    {
                        string name = variable.Identifier.ValueText;
                        if (string.IsNullOrEmpty(name) || _identifierReplacements.ContainsKey(name))
                            continue;

                        string fieldName = $"__{_methodName}_{name}";
                        _identifierReplacements[name] = fieldName;
                        _hoistedVariables.Add(new HoistedVariable(name, fieldName, typeName));
                    }
                }
            }

            private void BuildFieldDeclarations()
            {
                _fieldDeclarations.Add($"private int {_stateFieldName};");
                _fieldDeclarations.Add($"private string {_continuationFieldName};");

                foreach (var variable in _hoistedVariables)
                    _fieldDeclarations.Add($"private {variable.TypeName} {variable.FieldName};");
            }

            private string GenerateStartMethodSource()
            {
                var sb = new StringBuilder();
                string parameters = StartParameterList();

                sb.AppendLine($"private void {_startMethodName}({parameters})");
                sb.AppendLine("{");
                sb.AppendLine($"    {_stateFieldName} = 0;");
                sb.AppendLine($"    {_continuationFieldName} = continuation;");

                foreach (var parameter in _method.ParameterList.Parameters)
                {
                    string name = parameter.Identifier.ValueText;
                    if (_identifierReplacements.TryGetValue(name, out string fieldName))
                        sb.AppendLine($"    {fieldName} = {name};");
                }

                sb.AppendLine($"    {_moveNextMethodName}();");
                sb.AppendLine("}");
                return sb.ToString();
            }

            private string GenerateMoveNextMethodSource()
            {
                _switchSections.Clear();
                EmitSequence(0, _method.Body.Statements.ToList());

                var sb = new StringBuilder();
                sb.AppendLine($"public void {_moveNextMethodName}()");
                sb.AppendLine("{");
                sb.AppendLine($"    switch ({_stateFieldName})");
                sb.AppendLine("    {");
                foreach (string section in _switchSections)
                    sb.Append(section);
                sb.AppendLine("    }");
                sb.AppendLine("}");
                return sb.ToString();
            }

            private string GenerateCompleteMethodSource()
            {
                return
                    $"private void {_completeMethodName}()\n" +
                    "{\n" +
                    $"    if (!string.IsNullOrEmpty({_continuationFieldName}))\n" +
                    "    {\n" +
                    $"        string continuation = {_continuationFieldName};\n" +
                    $"        {_continuationFieldName} = \"\";\n" +
                    "        SendCustomEvent(continuation);\n" +
                    "    }\n" +
                    "}";
            }

            private void EmitSequence(int stateId, List<StatementSyntax> statements)
            {
                var lines = new List<string>();

                for (int i = 0; i < statements.Count; i++)
                {
                    StatementSyntax statement = statements[i];

                    if (TryEmitEarlyReturnIfAwait(stateId, lines, statement))
                        continue;

                    if (statement is WhileStatementSyntax whileStatement &&
                        whileStatement.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                    {
                        int afterLoopState = AllocateState();
                        int afterAwaitState = AllocateState();

                        EmitWhileState(stateId, lines, whileStatement, afterLoopState, afterAwaitState);
                        stateId = afterLoopState;
                        lines = new List<string>();
                        continue;
                    }

                    if (TryGetSimpleAwait(statement, out AwaitExpressionSyntax awaitExpression))
                    {
                        int nextState = AllocateState();
                        lines.Add($"{_stateFieldName} = {nextState};");
                        lines.Add(CreateAwaitSchedule(awaitExpression, nextState));
                        lines.Add("return;");
                        AddCase(stateId, lines);
                        stateId = nextState;
                        lines = new List<string>();
                        continue;
                    }

                    if (statement is ReturnStatementSyntax)
                    {
                        lines.Add($"{_completeMethodName}();");
                        lines.Add("return;");
                        AddCase(stateId, lines);
                        return;
                    }

                    if (statement.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                    {
                        throw new NotSupportedException($"Unsupported await pattern in '{_methodName}': {statement.Kind()}");
                    }

                    lines.AddRange(TransformStatement(statement));
                }

                lines.Add($"{_completeMethodName}();");
                lines.Add("return;");
                AddCase(stateId, lines);
            }

            private void EmitWhileState(
                int loopState,
                List<string> prefixLines,
                WhileStatementSyntax whileStatement,
                int afterLoopState,
                int afterAwaitState)
            {
                var condition = ReplaceIdentifiers(whileStatement.Condition).ToString();
                var bodyStatements = GetBlockStatements(whileStatement.Statement);
                AwaitExpressionSyntax awaitExpression = bodyStatements
                    .SelectMany(s => s.DescendantNodes().OfType<AwaitExpressionSyntax>())
                    .FirstOrDefault();

                if (awaitExpression == null)
                    throw new NotSupportedException($"Unsupported await pattern in '{_methodName}': while loop without await");

                int awaitStatementIndex = bodyStatements.FindIndex(s => s.DescendantNodes().OfType<AwaitExpressionSyntax>().Contains(awaitExpression));
                if (awaitStatementIndex < 0 || !TryGetSimpleAwait(bodyStatements[awaitStatementIndex], out awaitExpression))
                    throw new NotSupportedException($"Unsupported await pattern in '{_methodName}': while loop await must be a standalone statement");

                var loopLines = new List<string>(prefixLines);
                loopLines.Add($"if (!({condition}))");
                loopLines.Add("{");
                loopLines.Add($"    {_stateFieldName} = {afterLoopState};");
                loopLines.Add($"    {_moveNextMethodName}();");
                loopLines.Add("    return;");
                loopLines.Add("}");

                for (int i = 0; i < awaitStatementIndex; i++)
                    loopLines.AddRange(TransformStatement(bodyStatements[i]));

                loopLines.Add($"{_stateFieldName} = {afterAwaitState};");
                loopLines.Add(CreateAwaitSchedule(awaitExpression, afterAwaitState));
                loopLines.Add("return;");
                AddCase(loopState, loopLines);

                var afterAwaitLines = new List<string>();
                for (int i = awaitStatementIndex + 1; i < bodyStatements.Count; i++)
                    afterAwaitLines.AddRange(TransformStatement(bodyStatements[i]));

                afterAwaitLines.Add($"{_stateFieldName} = {loopState};");
                afterAwaitLines.Add($"{_moveNextMethodName}();");
                afterAwaitLines.Add("return;");
                AddCase(afterAwaitState, afterAwaitLines);
            }

            private bool TryEmitEarlyReturnIfAwait(
                int stateId,
                List<string> lines,
                StatementSyntax statement)
            {
                if (!(statement is IfStatementSyntax ifStatement))
                    return false;

                if (!ifStatement.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                    return false;

                if (ifStatement.Else != null)
                    throw new NotSupportedException($"Unsupported await pattern in '{_methodName}': if/else with await");

                var thenStatements = GetBlockStatements(ifStatement.Statement);
                if (thenStatements.Count != 2 ||
                    !TryGetSimpleAwait(thenStatements[0], out AwaitExpressionSyntax awaitExpression) ||
                    !(thenStatements[1] is ReturnStatementSyntax))
                {
                    throw new NotSupportedException($"Unsupported await pattern in '{_methodName}': if with await must be 'await ...; return;'");
                }

                int completionState = AllocateState();
                string condition = ReplaceIdentifiers(ifStatement.Condition).ToString();

                lines.Add($"if ({condition})");
                lines.Add("{");
                lines.Add($"    {_stateFieldName} = {completionState};");
                foreach (string scheduleLine in CreateAwaitSchedule(awaitExpression, completionState).Split('\n'))
                    lines.Add("    " + scheduleLine.TrimEnd());
                lines.Add("    return;");
                lines.Add("}");

                var completionLines = new List<string>
                {
                    $"{_completeMethodName}();",
                    "return;"
                };
                AddCase(completionState, completionLines);
                return true;
            }

            private string CreateAwaitSchedule(AwaitExpressionSyntax awaitExpression, int nextState)
            {
                ExpressionSyntax expression = ReplaceIdentifiers(awaitExpression.Expression);

                if (expression is InvocationExpressionSyntax invocation)
                {
                    string methodName = GetInvocationMethodName(invocation);
                    if (IsUdonTaskStaticCall(invocation, "Delay"))
                    {
                        string delay = invocation.ArgumentList.Arguments.Count > 0
                            ? invocation.ArgumentList.Arguments[0].Expression.ToString()
                            : "0f";
                        return $"SendCustomEventDelayedSeconds(\"{_moveNextMethodName}\", {delay});";
                    }

                    if (IsUdonTaskStaticCall(invocation, "DelayFrames"))
                    {
                        string frames = invocation.ArgumentList.Arguments.Count > 0
                            ? invocation.ArgumentList.Arguments[0].Expression.ToString()
                            : "1";
                        return $"SendCustomEventDelayedFrames(\"{_moveNextMethodName}\", {frames});";
                    }

                    if (IsUdonTaskStaticCall(invocation, "Yield"))
                    {
                        return $"SendCustomEventDelayedFrames(\"{_moveNextMethodName}\", 1);";
                    }

                    if (_asyncMethodNames.Contains(methodName))
                    {
                        string args = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => a.Expression.ToString()));
                        if (!string.IsNullOrEmpty(args))
                            args += ", ";

                        return $"__{methodName}_Start({args}\"{_moveNextMethodName}\");";
                    }
                }

                throw new NotSupportedException($"Unsupported await target in '{_methodName}': {awaitExpression.Expression}");
            }

            private IEnumerable<string> TransformStatement(StatementSyntax statement)
            {
                if (statement is LocalDeclarationStatementSyntax localDeclaration)
                    return TransformLocalDeclaration(localDeclaration);

                StatementSyntax replaced = (StatementSyntax)new IdentifierReplacer(_identifierReplacements).Visit(statement);
                return new[] { replaced.ToString() };
            }

            private IEnumerable<string> TransformLocalDeclaration(LocalDeclarationStatementSyntax localDeclaration)
            {
                var lines = new List<string>();

                foreach (var variable in localDeclaration.Declaration.Variables)
                {
                    string name = variable.Identifier.ValueText;
                    if (!_identifierReplacements.TryGetValue(name, out string fieldName))
                    {
                        lines.Add(localDeclaration.ToString());
                        continue;
                    }

                    if (variable.Initializer != null)
                    {
                        ExpressionSyntax initializer = ReplaceIdentifiers(variable.Initializer.Value);
                        lines.Add($"{fieldName} = {initializer};");
                    }
                }

                return lines;
            }

            private ExpressionSyntax ReplaceIdentifiers(ExpressionSyntax expression)
            {
                return (ExpressionSyntax)new IdentifierReplacer(_identifierReplacements).Visit(expression);
            }

            private static List<StatementSyntax> GetBlockStatements(StatementSyntax statement)
            {
                if (statement is BlockSyntax block)
                    return block.Statements.ToList();

                return new List<StatementSyntax> { statement };
            }

            private static bool TryGetSimpleAwait(StatementSyntax statement, out AwaitExpressionSyntax awaitExpression)
            {
                awaitExpression = null;
                if (statement is ExpressionStatementSyntax expressionStatement &&
                    expressionStatement.Expression is AwaitExpressionSyntax await)
                {
                    awaitExpression = await;
                    return true;
                }

                return false;
            }

            private static bool IsUdonTaskStaticCall(InvocationExpressionSyntax invocation, string methodName)
            {
                if (!(invocation.Expression is MemberAccessExpressionSyntax memberAccess))
                    return false;

                return memberAccess.Expression.ToString().EndsWith("UdonTask", StringComparison.Ordinal) &&
                       string.Equals(memberAccess.Name.Identifier.ValueText, methodName, StringComparison.Ordinal);
            }

            private static string GetInvocationMethodName(InvocationExpressionSyntax invocation)
            {
                switch (invocation.Expression)
                {
                    case IdentifierNameSyntax identifier:
                        return identifier.Identifier.ValueText;
                    case MemberAccessExpressionSyntax memberAccess:
                        return memberAccess.Name.Identifier.ValueText;
                    default:
                        return string.Empty;
                }
            }

            private void AddCase(int stateId, IEnumerable<string> lines)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"        case {stateId}:");
                foreach (string line in lines)
                    sb.AppendLine($"            {line}");
                _switchSections.Add(sb.ToString());
            }

            private int AllocateState()
            {
                return _nextState++;
            }

            private string StartParameterList()
            {
                var parameters = _method.ParameterList.Parameters
                    .Select(parameter => $"{parameter.Type} {parameter.Identifier.ValueText}")
                    .ToList();
                parameters.Add("string continuation");
                return string.Join(", ", parameters);
            }

            private static string ModifiersWithoutAsync(SyntaxTokenList modifiers)
            {
                return string.Join(" ", modifiers.Where(m => !m.IsKind(SyntaxKind.AsyncKeyword)).Select(m => m.Text));
            }

            private static MemberDeclarationSyntax ParseMember(string source)
            {
                return SyntaxFactory.ParseMemberDeclaration(source);
            }
        }

        private sealed class HoistedVariable
        {
            public HoistedVariable(string originalName, string fieldName, string typeName)
            {
                OriginalName = originalName;
                FieldName = fieldName;
                TypeName = typeName;
            }

            public string OriginalName { get; }
            public string FieldName { get; }
            public string TypeName { get; }
        }

        private sealed class IdentifierReplacer : CSharpSyntaxRewriter
        {
            private readonly Dictionary<string, string> _replacements;

            public IdentifierReplacer(Dictionary<string, string> replacements)
            {
                _replacements = replacements;
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                string identifier = node.Identifier.ValueText;
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
