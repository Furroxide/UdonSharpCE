using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using UdonSharp.CE.Editor.Optimizers;
using UdonSharp.Compiler;

namespace UdonSharp.CE.Editor.Async
{
    /// <summary>
    /// Lowers <c>async UdonTask</c> methods into event-driven state machines.
    ///
    /// The lowered shape is deliberately fire-and-forget. Udon has no way to park a call frame, so the
    /// public entry point starts the machine and returns immediately; the only thing that can observe the
    /// machine finishing is a continuation event name threaded through <c>__Name_Start</c>, which this
    /// generator wires up for <c>await</c> sites it lowers itself. Nothing else can await a
    /// <see cref="UdonSharp.CE.Async.UdonTask"/> and see completion, so any <c>await</c> this generator
    /// does not consume is a hard error rather than a silently-immediate resume.
    /// </summary>
    internal class AsyncMethodTransformOptimizer : ICompileTimeOptimizer
    {
        private const string OptimizerIdValue = "CEASYNC001";

        /// <summary>Author-facing diagnostic codes. Kept distinct so a fix guide can address each case.</summary>
        private const string DiagUdonTaskOfT = "CE0022";
        private const string DiagAsyncWithoutAwait = "CE0023";
        private const string DiagUnloweredAwait = "CE0024";
        private const string DiagUnsupportedPattern = "CE0025";
        private const string DiagUninferrableLocal = "CE0026";

        public string OptimizerId => OptimizerIdValue;
        public string OptimizerName => "Async Method Transformer";
        public string Description => "Transforms async UdonTask methods into state machines using SendCustomEventDelayed*.";
        public bool IsEnabledByDefault => true;
        public int Priority => 1;

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            SemanticModel semanticModel = context.GetSemanticModel(tree);
            if (semanticModel == null)
            {
                throw new CodeGenerationException(
                    OptimizerIdValue,
                    tree.GetRoot().GetLocation(),
                    "CE could not build a semantic model for this file, so async lowering cannot resolve " +
                    "local variables or infer 'var' types. Lowering without it silently hoists the wrong " +
                    "identifiers, so the compile is aborted instead.");
            }

            var root = tree.GetRoot();
            var rewriter = new AsyncMethodRewriter(context, semanticModel);
            var newRoot = rewriter.Visit(root);

            // Every await this generator understands has been consumed by now, so anything still standing
            // is in code the state machine never rewrote -- an expression-bodied member, a lambda, a second
            // await inside a loop body. Left alone it reaches Roslyn, which builds a real compiler-generated
            // state machine class that Udon cannot run, and the failure surfaces nowhere near the await.
            RejectUnloweredAwaits(newRoot);

            // Deliberately no NormalizeWhitespace(). It reformatted the whole file, which moved every
            // untouched line and pointed every later diagnostic and every debug-info line-map entry at the
            // wrong source line.
            return rewriter.ChangesMade
                ? tree.WithRootAndOptions(newRoot, tree.Options)
                : tree;
        }

        private static void RejectUnloweredAwaits(SyntaxNode root)
        {
            AwaitExpressionSyntax remaining = root
                .DescendantNodes()
                .OfType<AwaitExpressionSyntax>()
                .FirstOrDefault();

            if (remaining == null)
                return;

            throw new CodeGenerationException(
                DiagUnloweredAwait,
                remaining.GetLocation(),
                "CE could not lower this 'await'. UdonTask is fire-and-forget: it carries no continuation, " +
                "so code CE did not lower cannot await one and observe it completing. CE lowers only " +
                "'async UdonTask' methods with a braced body, and only the await forms documented in " +
                "ce-async.md. Fix: move this await into an 'async UdonTask' method with a braced body, or " +
                "call the async method without awaiting it.");
        }

        private sealed class AsyncMethodRewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly SemanticModel _semanticModel;
            private readonly Stack<HashSet<string>> _asyncMethodNames = new Stack<HashSet<string>>();

            /// <summary>
            /// One accumulator per class currently being visited. A single shared list cleared on entry to
            /// every class meant a nested class declared after an async method wiped the outer class's
            /// pending state-machine members, leaving the entry method calling a <c>__Name_Start</c> that
            /// was never emitted.
            /// </summary>
            private readonly Stack<List<MemberDeclarationSyntax>> _pendingMembers =
                new Stack<List<MemberDeclarationSyntax>>();

            public bool ChangesMade { get; private set; }

            public AsyncMethodRewriter(OptimizationContext context, SemanticModel semanticModel)
            {
                _context = context;
                _semanticModel = semanticModel;
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                _asyncMethodNames.Push(new HashSet<string>(
                    node.Members
                        .OfType<MethodDeclarationSyntax>()
                        .Where(IsTransformableAsyncMethod)
                        .Select(method => method.Identifier.ValueText),
                    StringComparer.Ordinal));
                _pendingMembers.Push(new List<MemberDeclarationSyntax>());

                var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);

                List<MemberDeclarationSyntax> members = _pendingMembers.Pop();
                _asyncMethodNames.Pop();

                if (members.Count > 0)
                    visited = visited.AddMembers(members.ToArray());

                return visited;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                ValidateAsyncDeclaration(node);

                if (!IsTransformableAsyncMethod(node))
                    return base.VisitMethodDeclaration(node);

                var generator = new MethodGenerator(
                    node,
                    _asyncMethodNames.Count > 0 ? _asyncMethodNames.Peek() : new HashSet<string>(),
                    _semanticModel);

                MethodDeclarationSyntax entryMethod = generator.GenerateEntryMethod();

                if (_pendingMembers.Count == 0)
                {
                    // Support members can only be attached to an enclosing class. A transformable async
                    // method outside one would emit an entry point calling methods that land nowhere.
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        node.Identifier.GetLocation(),
                        $"'{node.Identifier.ValueText}' is an async UdonTask method declared outside a class. " +
                        "CE emits the state machine as members of the declaring class, so the method must " +
                        "live in one.");
                }

                _pendingMembers.Peek().AddRange(generator.GenerateSupportMembers());

                ChangesMade = true;
                _context.RecordOptimization(
                    OptimizerIdValue,
                    $"Transforming async method '{node.Identifier.ValueText}' with {generator.AwaitCount} await(s)",
                    node.GetLocation(),
                    node.Identifier.ValueText,
                    "");

                return entryMethod;
            }

            /// <summary>
            /// Rejects the async shapes CE cannot lower, before any code is generated for them.
            /// </summary>
            private static void ValidateAsyncDeclaration(MethodDeclarationSyntax method)
            {
                if (method == null || !method.Modifiers.Any(SyntaxKind.AsyncKeyword))
                    return;

                if (!TryGetUdonTaskReturn(method.ReturnType, out bool isGeneric))
                    return;

                if (isGeneric)
                {
                    // The generated entry point has no result to hand back: the machine returns to the
                    // caller at the first await, long before the value exists. Lowering it anyway produced
                    // 'return UdonTask.CompletedTask;' against a UdonTask<T> signature -- a CS0029 inside
                    // generated code the author cannot open.
                    throw new CodeGenerationException(
                        DiagUdonTaskOfT,
                        method.ReturnType.GetLocation(),
                        $"'{method.Identifier.ValueText}' is declared 'async UdonTask<T>', which CE cannot " +
                        "lower. The state machine returns to its caller at the first await, so there is no " +
                        "point at which a result can be handed back. Fix: return plain 'UdonTask' and store " +
                        "the result in a field the caller reads once the continuation runs.");
                }

                if (!method.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                {
                    // Without an await there is nothing for CE to lower, so the method reaches Roslyn with
                    // its 'async' keyword intact and Roslyn builds a real state machine class for it, which
                    // Udon cannot execute.
                    throw new CodeGenerationException(
                        DiagAsyncWithoutAwait,
                        method.Identifier.GetLocation(),
                        $"'{method.Identifier.ValueText}' is declared async but contains no 'await', so CE " +
                        "has nothing to lower and the C# compiler would build a state machine Udon cannot " +
                        "run. Fix: drop the 'async' keyword and 'return UdonTask.CompletedTask;'.");
                }
            }

            /// <summary>
            /// Matching on the return type alone treated a plain <c>UdonTask</c> helper -- one that just
            /// returns <c>UdonTask.CompletedTask</c> -- as a state machine and rewrote it into an entry
            /// point calling generated members built from a body with no awaits. The <c>async</c> keyword
            /// is what actually marks a method as lowerable.
            /// </summary>
            private static bool IsTransformableAsyncMethod(MethodDeclarationSyntax method)
            {
                if (method == null || method.Body == null)
                    return false;

                if (!method.Modifiers.Any(SyntaxKind.AsyncKeyword))
                    return false;

                if (!TryGetUdonTaskReturn(method.ReturnType, out bool isGeneric) || isGeneric)
                    return false;

                return method.DescendantNodes().OfType<AwaitExpressionSyntax>().Any();
            }
        }

        /// <summary>
        /// Recognises <c>UdonTask</c> through qualifiers and aliases, so <c>global::UdonSharp.CE.Async.UdonTask</c>
        /// and a bare <c>UdonTask</c> classify the same and a user type named <c>MyUdonTaskQueue</c> does not
        /// classify at all -- which a substring match on the written return type could not tell apart.
        /// </summary>
        private static bool TryGetUdonTaskReturn(TypeSyntax type, out bool isGeneric)
        {
            isGeneric = false;
            if (type == null)
                return false;

            TypeSyntax core = type;
            while (true)
            {
                if (core is QualifiedNameSyntax qualified)
                {
                    core = qualified.Right;
                    continue;
                }

                if (core is AliasQualifiedNameSyntax aliasQualified)
                {
                    core = aliasQualified.Name;
                    continue;
                }

                break;
            }

            if (core is GenericNameSyntax generic)
            {
                isGeneric = true;
                return string.Equals(generic.Identifier.ValueText, "UdonTask", StringComparison.Ordinal);
            }

            return core is IdentifierNameSyntax identifier &&
                   string.Equals(identifier.Identifier.ValueText, "UdonTask", StringComparison.Ordinal);
        }

        private sealed class MethodGenerator
        {
            private readonly MethodDeclarationSyntax _method;
            private readonly HashSet<string> _asyncMethodNames;
            private readonly SemanticModel _semanticModel;

            /// <summary>
            /// Hoisted storage keyed by the declared symbol, not by name. Name keying gave one field to
            /// every local called <c>t</c> no matter which scope declared it, and substituted in member
            /// position too, so a local <c>x</c> next to a <c>vec.x</c> produced <c>vec.__Name_x</c>.
            /// </summary>
            private readonly Dictionary<ISymbol, string> _fieldsBySymbol =
                new Dictionary<ISymbol, string>(SymbolEqualityComparer.Default);

            /// <summary>Identifier occurrences to rewrite, keyed by source span so node identity is irrelevant.</summary>
            private readonly Dictionary<TextSpan, string> _identifierReplacements = new Dictionary<TextSpan, string>();

            /// <summary>Declarators whose storage moved to a field, keyed by the declared name's span.</summary>
            private readonly Dictionary<TextSpan, string> _declaratorFields = new Dictionary<TextSpan, string>();

            private readonly HashSet<string> _usedFieldNames = new HashSet<string>(StringComparer.Ordinal);
            private readonly List<HoistedVariable> _hoistedVariables = new List<HoistedVariable>();
            private readonly List<string> _fieldDeclarations = new List<string>();
            private readonly List<string> _switchSections = new List<string>();
            private readonly string _methodName;
            private readonly string _stateFieldName;
            private readonly string _continuationFieldName;
            private readonly string _runningFieldName;
            private readonly string _continuationParameterName;
            private readonly string _startMethodName;
            private readonly string _moveNextMethodName;
            private readonly string _completeMethodName;
            private int _nextState = 1;

            public int AwaitCount { get; }

            public MethodGenerator(
                MethodDeclarationSyntax method,
                HashSet<string> asyncMethodNames,
                SemanticModel semanticModel)
            {
                _method = method;
                _asyncMethodNames = asyncMethodNames;
                _semanticModel = semanticModel;
                _methodName = method.Identifier.ValueText;
                _stateFieldName = $"__{_methodName}_state";
                _continuationFieldName = $"__{_methodName}_continuation";
                _runningFieldName = $"__{_methodName}_running";
                _startMethodName = $"__{_methodName}_Start";
                _moveNextMethodName = $"__{_methodName}_MoveNext";
                _completeMethodName = $"__{_methodName}_Complete";
                AwaitCount = method.DescendantNodes().OfType<AwaitExpressionSyntax>().Count();

                // Seeded so a hoisted parameter named 'state', 'continuation' or 'running' cannot claim a
                // field the machine itself owns.
                _usedFieldNames.Add(_stateFieldName);
                _usedFieldNames.Add(_continuationFieldName);
                _usedFieldNames.Add(_runningFieldName);

                _continuationParameterName = MakeUniqueParameterName("continuation");

                CollectHoistedVariables();
                BuildIdentifierReplacements();
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

                // The returned struct is a snapshot, never a handle: UdonTask has no continuation to
                // register against, so it cannot be updated once the caller holds it. Reporting the state
                // the machine is actually in beats the old unconditional CompletedTask, which told every
                // caller a still-running sequence had already finished.
                string source =
                    $"{modifiers} {returnType} {_methodName}{parameterList}\n" +
                    "{\n" +
                    $"    {_startMethodName}({arguments}\"\");\n" +
                    $"    if ({_runningFieldName})\n" +
                    "        return global::UdonSharp.CE.Async.UdonTask.Started;\n" +
                    "    return global::UdonSharp.CE.Async.UdonTask.CompletedTask;\n" +
                    "}";

                return (MethodDeclarationSyntax)ParseMember(source, _method.Identifier.GetLocation())
                    .WithLeadingTrivia(_method.GetLeadingTrivia())
                    .WithTrailingTrivia(_method.GetTrailingTrivia());
            }

            public IEnumerable<MemberDeclarationSyntax> GenerateSupportMembers()
            {
                var members = new List<MemberDeclarationSyntax>();
                Location location = _method.Identifier.GetLocation();

                foreach (string field in _fieldDeclarations)
                    members.Add(ParseMember(field, location));

                members.Add(ParseMember(GenerateStartMethodSource(), location));
                members.Add(ParseMember(GenerateMoveNextMethodSource(), location));
                members.Add(ParseMember(GenerateCompleteMethodSource(), location));

                return members;
            }

            /// <summary>
            /// The statement lists the emitter flattens into <c>case</c> labels, and therefore the only
            /// declarations whose storage has to survive a return from <c>MoveNext</c>. Locals nested inside
            /// a block that is emitted whole keep their own braces and must be left alone -- hoisting those
            /// rewrote their uses to a field while leaving the declaration in place, so the loop counter in
            /// <c>if (c) { int i = 0; while (i &lt; n) ... }</c> read a field nothing ever assigned.
            /// </summary>
            private IEnumerable<StatementSyntax> FlattenedStatements()
            {
                foreach (StatementSyntax statement in _method.Body.Statements)
                {
                    yield return statement;

                    if (statement is WhileStatementSyntax whileStatement &&
                        whileStatement.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                    {
                        foreach (StatementSyntax bodyStatement in GetBlockStatements(whileStatement.Statement))
                            yield return bodyStatement;
                    }
                }
            }

            private void CollectHoistedVariables()
            {
                foreach (ParameterSyntax parameter in _method.ParameterList.Parameters)
                {
                    ISymbol symbol = _semanticModel.GetDeclaredSymbol(parameter);
                    Hoist(symbol, parameter.Identifier, parameter.Type?.ToString() ?? "object");
                }

                foreach (StatementSyntax statement in FlattenedStatements())
                {
                    if (!(statement is LocalDeclarationStatementSyntax local))
                        continue;

                    foreach (VariableDeclaratorSyntax variable in local.Declaration.Variables)
                    {
                        ISymbol symbol = _semanticModel.GetDeclaredSymbol(variable);
                        Hoist(symbol, variable.Identifier, ResolveHoistedType(local, variable, symbol));
                    }
                }
            }

            /// <summary>
            /// 'var' locals used to be skipped by the hoister but still emitted into a <c>case N:</c> label.
            /// Every case shares one switch scope, so a second 'var' of the same name was a CS0128 the
            /// author could not see, and any value it held was lost the moment the machine returned.
            /// </summary>
            private string ResolveHoistedType(
                LocalDeclarationStatementSyntax local,
                VariableDeclaratorSyntax variable,
                ISymbol symbol)
            {
                string writtenType = local.Declaration.Type.ToString();
                if (!string.Equals(writtenType, "var", StringComparison.Ordinal))
                    return writtenType;

                ITypeSymbol inferred = (symbol as ILocalSymbol)?.Type;
                if (inferred == null ||
                    inferred.TypeKind == TypeKind.Error ||
                    inferred.TypeKind == TypeKind.Pointer ||
                    inferred.IsAnonymousType)
                {
                    throw new CodeGenerationException(
                        DiagUninferrableLocal,
                        variable.GetLocation(),
                        $"CE cannot infer the type of 'var {variable.Identifier.ValueText}' in " +
                        $"'{_methodName}'. Lowering moves this local into a field, which needs a written " +
                        "type. Fix: replace 'var' with the explicit type.");
                }

                return inferred.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            }

            private void Hoist(ISymbol symbol, SyntaxToken identifier, string typeName)
            {
                if (symbol == null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        identifier.GetLocation(),
                        $"CE could not resolve '{identifier.ValueText}' in '{_methodName}' to a declaration, " +
                        "so it cannot tell which uses of that name belong to it. Lowering is aborted rather " +
                        "than guessing by name.");
                }

                if (_fieldsBySymbol.ContainsKey(symbol))
                    return;

                string fieldName = MakeUniqueFieldName(identifier.ValueText);
                _fieldsBySymbol[symbol] = fieldName;
                _declaratorFields[identifier.Span] = fieldName;
                _hoistedVariables.Add(new HoistedVariable(fieldName, typeName));
            }

            private void BuildIdentifierReplacements()
            {
                foreach (IdentifierNameSyntax identifier in _method.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    if (IsMemberName(identifier))
                        continue;

                    ISymbol symbol = _semanticModel.GetSymbolInfo(identifier).Symbol;
                    if (symbol == null)
                        continue;

                    if (_fieldsBySymbol.TryGetValue(symbol, out string fieldName))
                        _identifierReplacements[identifier.Span] = fieldName;
                }
            }

            /// <summary>
            /// True when the identifier names a member of something else rather than a variable in scope --
            /// the <c>x</c> of <c>vec.x</c>, of <c>Vector3.x</c>, or of a named argument.
            /// </summary>
            private static bool IsMemberName(IdentifierNameSyntax identifier)
            {
                switch (identifier.Parent)
                {
                    case MemberAccessExpressionSyntax memberAccess:
                        return memberAccess.Name == identifier;
                    case MemberBindingExpressionSyntax memberBinding:
                        return memberBinding.Name == identifier;
                    case QualifiedNameSyntax qualified:
                        return qualified.Right == identifier;
                    case NameColonSyntax _:
                    case NameEqualsSyntax _:
                        return true;
                    default:
                        return false;
                }
            }

            private void BuildFieldDeclarations()
            {
                _fieldDeclarations.Add($"private int {_stateFieldName};");
                _fieldDeclarations.Add($"private string {_continuationFieldName};");
                _fieldDeclarations.Add($"private bool {_runningFieldName};");

                foreach (var variable in _hoistedVariables)
                    _fieldDeclarations.Add($"private {variable.TypeName} {variable.FieldName};");
            }

            private string GenerateStartMethodSource()
            {
                var sb = new StringBuilder();

                sb.AppendLine($"private void {_startMethodName}({StartParameterList()})");
                sb.AppendLine("{");

                // Every local of this method lives in one set of instance fields, so a second call landing
                // while the first is still suspended would overwrite the first's state and then run both
                // machines against the mixture. There is no per-call storage to fall back on, so the second
                // call is refused loudly instead of corrupting the first. Its continuation still fires, so
                // an awaiting caller carries on rather than waiting on a sequence that will never start.
                sb.AppendLine($"    if ({_runningFieldName})");
                sb.AppendLine("    {");
                sb.AppendLine(
                    $"        global::UnityEngine.Debug.LogError(\"[CE.Async] '{_methodName}' was started " +
                    "again while the previous call was still running on this behaviour. CE stores that " +
                    "method's locals in shared fields, so the second call would corrupt the first; it was " +
                    "skipped and its caller resumed immediately. Guard the call, or move the work onto a " +
                    "second behaviour.\");");
                sb.AppendLine($"        if (!string.IsNullOrEmpty({_continuationParameterName}))");
                sb.AppendLine("        {");
                sb.AppendLine($"            SendCustomEvent({_continuationParameterName});");
                sb.AppendLine("        }");
                sb.AppendLine("        return;");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    {_runningFieldName} = true;");
                sb.AppendLine($"    {_stateFieldName} = 0;");
                sb.AppendLine($"    {_continuationFieldName} = {_continuationParameterName};");

                foreach (var parameter in _method.ParameterList.Parameters)
                {
                    if (_declaratorFields.TryGetValue(parameter.Identifier.Span, out string fieldName))
                        sb.AppendLine($"    {fieldName} = {parameter.Identifier.ValueText};");
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
                var sb = new StringBuilder();

                sb.AppendLine($"private void {_completeMethodName}()");
                sb.AppendLine("{");
                sb.AppendLine($"    {_runningFieldName} = false;");
                sb.AppendLine($"    if (!string.IsNullOrEmpty({_continuationFieldName}))");
                sb.AppendLine("    {");
                sb.AppendLine($"        string resume = {_continuationFieldName};");
                sb.AppendLine($"        {_continuationFieldName} = \"\";");
                sb.AppendLine("        SendCustomEvent(resume);");
                sb.AppendLine("    }");
                sb.AppendLine("}");
                return sb.ToString();
            }

            private void EmitSequence(int stateId, List<StatementSyntax> statements)
            {
                var lines = new List<string>();

                for (int i = 0; i < statements.Count; i++)
                {
                    StatementSyntax statement = statements[i];

                    if (TryEmitEarlyReturnIfAwait(lines, statement))
                        continue;

                    if (statement is WhileStatementSyntax whileStatement &&
                        whileStatement.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                    {
                        int loopState = AllocateState();
                        int afterLoopState = AllocateState();
                        int afterAwaitState = AllocateState();

                        // The loop head gets a state of its own. Folding it into the state that already
                        // carries the statements ahead of the loop replayed those statements on every
                        // iteration, so a 'float elapsed = 0f;' before the loop was reset each pass and the
                        // loop condition could never go false.
                        lines.Add($"{_stateFieldName} = {loopState};");
                        lines.Add($"{_moveNextMethodName}();");
                        lines.Add("return;");
                        AddCase(stateId, lines);

                        EmitWhileState(loopState, whileStatement, afterLoopState, afterAwaitState);
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
                        throw new CodeGenerationException(
                            DiagUnsupportedPattern,
                            statement.GetLocation(),
                            $"CE cannot lower this {statement.Kind()} in '{_methodName}' because it contains " +
                            "an await. Fix: lift the await out so it is a statement of its own in the method " +
                            "body or in a while loop body.");
                    }

                    lines.AddRange(TransformStatement(statement));
                }

                lines.Add($"{_completeMethodName}();");
                lines.Add("return;");
                AddCase(stateId, lines);
            }

            private void EmitWhileState(
                int loopState,
                WhileStatementSyntax whileStatement,
                int afterLoopState,
                int afterAwaitState)
            {
                string condition = ReplaceIdentifiersText(whileStatement.Condition);
                var bodyStatements = GetBlockStatements(whileStatement.Statement);
                AwaitExpressionSyntax awaitExpression = bodyStatements
                    .SelectMany(s => s.DescendantNodes().OfType<AwaitExpressionSyntax>())
                    .FirstOrDefault();

                if (awaitExpression == null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        whileStatement.GetLocation(),
                        $"CE cannot lower this while loop in '{_methodName}': its await is not in the loop " +
                        "body. Fix: put the await directly in the loop body.");
                }

                int awaitStatementIndex = bodyStatements.FindIndex(s => s.DescendantNodes().OfType<AwaitExpressionSyntax>().Contains(awaitExpression));
                if (awaitStatementIndex < 0 || !TryGetSimpleAwait(bodyStatements[awaitStatementIndex], out awaitExpression))
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        whileStatement.GetLocation(),
                        $"CE cannot lower this while loop in '{_methodName}': the await inside it must be a " +
                        "statement of its own directly in the loop body.");
                }

                var loopLines = new List<string>();
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
                List<string> lines,
                StatementSyntax statement)
            {
                if (!(statement is IfStatementSyntax ifStatement))
                    return false;

                if (!ifStatement.DescendantNodes().OfType<AwaitExpressionSyntax>().Any())
                    return false;

                if (ifStatement.Else != null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        ifStatement.GetLocation(),
                        $"CE cannot lower an if/else containing an await in '{_methodName}'. Fix: split the " +
                        "branches into separate async methods and await those.");
                }

                var thenStatements = GetBlockStatements(ifStatement.Statement);
                if (thenStatements.Count != 2 ||
                    !TryGetSimpleAwait(thenStatements[0], out AwaitExpressionSyntax awaitExpression) ||
                    !(thenStatements[1] is ReturnStatementSyntax))
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        ifStatement.GetLocation(),
                        $"CE cannot lower this if in '{_methodName}'. The only awaiting if it supports is an " +
                        "early exit of exactly 'await ...; return;'.");
                }

                int completionState = AllocateState();
                string condition = ReplaceIdentifiersText(ifStatement.Condition);

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
                        return $"SendCustomEventDelayedSeconds(\"{_moveNextMethodName}\", {delay}, global::VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);";
                    }

                    if (IsUdonTaskStaticCall(invocation, "DelayFrames"))
                    {
                        string frames = invocation.ArgumentList.Arguments.Count > 0
                            ? invocation.ArgumentList.Arguments[0].Expression.ToString()
                            : "1";
                        return $"SendCustomEventDelayedFrames(\"{_moveNextMethodName}\", {frames}, global::VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);";
                    }

                    if (IsUdonTaskStaticCall(invocation, "Yield"))
                    {
                        return $"SendCustomEventDelayedFrames(\"{_moveNextMethodName}\", 1, global::VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);";
                    }

                    // The continuation is delivered by SendCustomEvent on *this* behaviour, so only a call
                    // that targets this behaviour can be chained. Matching on the bare method name also
                    // matched 'other.Child()' and emitted a '__Child_Start' call this class does not have.
                    if (IsSelfInvocation(invocation) && _asyncMethodNames.Contains(methodName))
                    {
                        string args = string.Join(", ", invocation.ArgumentList.Arguments.Select(a => a.Expression.ToString()));
                        if (!string.IsNullOrEmpty(args))
                            args += ", ";

                        return $"__{methodName}_Start({args}\"{_moveNextMethodName}\");";
                    }
                }

                throw new CodeGenerationException(
                    DiagUnsupportedPattern,
                    awaitExpression.GetLocation(),
                    $"CE cannot lower 'await {awaitExpression.Expression}' in '{_methodName}'. Awaitable " +
                    "targets are UdonTask.Delay, UdonTask.DelayFrames, UdonTask.Yield, and an async UdonTask " +
                    "method on this same behaviour.");
            }

            private IEnumerable<string> TransformStatement(StatementSyntax statement)
            {
                if (statement is LocalDeclarationStatementSyntax localDeclaration)
                    return TransformLocalDeclaration(localDeclaration);

                StatementSyntax replaced = (StatementSyntax)new IdentifierReplacer(_identifierReplacements).Visit(statement);
                replaced = RewriteNestedReturns(replaced);
                return new[] { replaced.ToString() };
            }

            /// <summary>
            /// Statements emitted verbatim into a MoveNext case keep their own 'return', which exits
            /// MoveNext without running the completion. That would leave the running flag latched on
            /// forever and the behaviour would refuse every later start of this method, so each nested
            /// return has to tear the state machine down the way the top-level path in EmitSequence does.
            /// The shape CE's own CancellationToken documentation asks authors to write --
            /// 'if (token.IsCancellationRequested) return;' -- lands here.
            /// </summary>
            private StatementSyntax RewriteNestedReturns(StatementSyntax statement)
            {
                return (StatementSyntax)new NestedReturnRewriter(_completeMethodName).Visit(statement);
            }

            private IEnumerable<string> TransformLocalDeclaration(LocalDeclarationStatementSyntax localDeclaration)
            {
                bool allHoisted = localDeclaration.Declaration.Variables
                    .All(variable => _declaratorFields.ContainsKey(variable.Identifier.Span));

                if (!allHoisted)
                {
                    // Reached by a path that does not flatten this statement, so its braces still scope the
                    // local and the declaration has to survive intact.
                    StatementSyntax replaced = (StatementSyntax)new IdentifierReplacer(_identifierReplacements).Visit(localDeclaration);
                    return new[] { replaced.ToString() };
                }

                var lines = new List<string>();

                foreach (VariableDeclaratorSyntax variable in localDeclaration.Declaration.Variables)
                {
                    if (variable.Initializer == null)
                        continue;

                    string fieldName = _declaratorFields[variable.Identifier.Span];
                    lines.Add($"{fieldName} = {ReplaceIdentifiersText(variable.Initializer.Value)};");
                }

                return lines;
            }

            private ExpressionSyntax ReplaceIdentifiers(ExpressionSyntax expression)
            {
                return (ExpressionSyntax)new IdentifierReplacer(_identifierReplacements).Visit(expression);
            }

            private string ReplaceIdentifiersText(ExpressionSyntax expression)
            {
                return ReplaceIdentifiers(expression).ToString();
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

            private static bool IsSelfInvocation(InvocationExpressionSyntax invocation)
            {
                if (invocation.Expression is IdentifierNameSyntax)
                    return true;

                return invocation.Expression is MemberAccessExpressionSyntax memberAccess &&
                       memberAccess.Expression is ThisExpressionSyntax;
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
                parameters.Add($"string {_continuationParameterName}");
                return string.Join(", ", parameters);
            }

            private string MakeUniqueFieldName(string name)
            {
                string candidate = $"__{_methodName}_{name}";
                if (_usedFieldNames.Add(candidate))
                    return candidate;

                // Two scopes in one method can each declare 't'; both land in the same class, so the second
                // needs a name of its own or the class does not compile.
                for (int suffix = 1; ; suffix++)
                {
                    string scoped = $"{candidate}_{suffix}";
                    if (_usedFieldNames.Add(scoped))
                        return scoped;
                }
            }

            private string MakeUniqueParameterName(string name)
            {
                var taken = new HashSet<string>(
                    _method.ParameterList.Parameters.Select(parameter => parameter.Identifier.ValueText),
                    StringComparer.Ordinal);

                if (!taken.Contains(name))
                    return name;

                for (int suffix = 1; ; suffix++)
                {
                    string scoped = $"{name}{suffix}";
                    if (!taken.Contains(scoped))
                        return scoped;
                }
            }

            private static string ModifiersWithoutAsync(SyntaxTokenList modifiers)
            {
                return string.Join(" ", modifiers.Where(m => !m.IsKind(SyntaxKind.AsyncKeyword)).Select(m => m.Text));
            }

            /// <summary>
            /// Parses generated source and refuses anything malformed. A member that does not parse used to
            /// ride the tree into Roslyn and surface as compiler errors inside code the author has no file
            /// for; failing here names the method that produced it.
            /// </summary>
            private MemberDeclarationSyntax ParseMember(string source, Location location)
            {
                MemberDeclarationSyntax member = SyntaxFactory.ParseMemberDeclaration("\n" + source + "\n");

                if (member == null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"CE generated a member for '{_methodName}' that is not a valid declaration:\n{source}");
                }

                Diagnostic error = member.GetDiagnostics()
                    // Qualified: UdonSharp.Compiler also declares a DiagnosticSeverity, and this file sees
                    // both through usings with neither closer, so the bare name is ambiguous here.
                    .FirstOrDefault(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);

                if (error != null)
                {
                    throw new CodeGenerationException(
                        DiagUnsupportedPattern,
                        location,
                        $"CE generated a member for '{_methodName}' that does not parse ({error.GetMessage()}):\n{source}");
                }

                return member;
            }
        }

        private sealed class HoistedVariable
        {
            public HoistedVariable(string fieldName, string typeName)
            {
                FieldName = fieldName;
                TypeName = typeName;
            }

            public string FieldName { get; }
            public string TypeName { get; }
        }

        /// <summary>
        /// Replaces every return that belongs to the async method body with a completion followed by the
        /// return. Returns inside a lambda, anonymous method or local function belong to that body rather
        /// than to the async method, so those are left alone.
        /// </summary>
        private sealed class NestedReturnRewriter : CSharpSyntaxRewriter
        {
            private readonly string _completeMethodName;

            public NestedReturnRewriter(string completeMethodName)
            {
                _completeMethodName = completeMethodName;
            }

            public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node)
            {
                return SyntaxFactory
                    .ParseStatement("{ " + _completeMethodName + "(); return; }")
                    .WithTriviaFrom(node);
            }

            public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;

            public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;

            public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

            public override SyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
        }

        private sealed class IdentifierReplacer : CSharpSyntaxRewriter
        {
            private readonly Dictionary<TextSpan, string> _replacements;

            public IdentifierReplacer(Dictionary<TextSpan, string> replacements)
            {
                _replacements = replacements;
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node)
            {
                if (_replacements.TryGetValue(node.Span, out string replacement))
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
