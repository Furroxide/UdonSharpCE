using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.CE.Editor.Optimizers;
using UdonSharp.Compiler;

namespace UdonSharp.CE.Editor.CodeGen
{
    /// <summary>
    /// Makes [Rpc] actually send.
    ///
    /// Before this, [Rpc] was documentation: nothing read Target, RateLimit or OwnerOnly, and authors
    /// still hand-wrote SendCustomNetworkEvent next to the attribute that claimed to do it for them.
    ///
    /// Two transforms:
    ///  1. Mark the receiver [NetworkCallable(maxEventsPerSecond)] so VRChat itself enforces the declared
    ///     rate limit, and prepend a real owner check when OwnerOnly is set. NetworkCalling.CallingPlayer
    ///     is a genuine Udon extern, so this is enforcement rather than a comment; a null caller means a
    ///     local call and is allowed.
    ///  2. Rewrite call sites into SendCustomNetworkEvent with the declared target.
    ///
    /// The two halves have different reach, which is why the per-file gate in <see cref="Optimize"/> is
    /// two separate tests: a declaration is always in a file that spells the attribute, a call site
    /// usually is not.
    ///
    /// A generated sender method is deliberately NOT emitted. CE transforms only rewrite the trees inside
    /// UdonSharp's private compilation while Unity compiles the original source, so a generated member
    /// would be invisible at the call site. Rewriting the call is the only shape that works. For the same
    /// reason the escape hatch for calls this pass refuses to rewrite -- <c>UdonSharp.CE.Net.CENet</c> --
    /// is hand-written in the runtime library rather than generated here.
    /// </summary>
    internal class RpcEmitter : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEGEN009";
        public string OptimizerName => "Rpc Network Event Emitter";
        public string Description => "Marks [Rpc] methods [NetworkCallable] and rewrites call sites into network events.";
        public bool IsEnabledByDefault => true;
        public int Priority => 11;

        /// <summary>
        /// The [Rpc] method names declared anywhere in a compilation, keyed by that compilation.
        /// </summary>
        /// <remarks>
        /// The call-site half of this pass has to run on files that never mention "Rpc", so it needs a gate
        /// the file's own text cannot provide. Collecting the declared names once per compilation keeps
        /// that gate a substring test rather than a symbol lookup on every invocation in the project.
        ///
        /// The table is weak so a finished compile's trees are not pinned alive by this static; the
        /// generator instance itself lives for the whole editor session.
        /// </remarks>
        private static readonly ConditionalWeakTable<Compilation, string[]> RpcMethodNamesByCompilation =
            new ConditionalWeakTable<Compilation, string[]>();

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            SemanticModel semanticModel = context.GetSemanticModel(tree);
            if (semanticModel == null)
                return tree;

            SyntaxNode root = tree.GetRoot();
            string sourceText = root.ToFullString();

            // A DECLARATION is only ever in a file that spells the attribute, so the substring still
            // decides that half.
            bool mayDeclare = sourceText.IndexOf("Rpc", StringComparison.Ordinal) >= 0;

            // A CALL SITE normally is not: '[Rpc] public void StartGame()' lives in A.cs while
            // 'manager.StartGame()' lives in B.cs, and B.cs need not contain the substring "Rpc" anywhere.
            // Gating the whole pass on that substring skipped B.cs outright, so the receiver was marked
            // [NetworkCallable] while the call stayed a plain local call -- the cross-file case, which is
            // the normal one, silently did not send. Gate the call rewrite on the [Rpc] method names
            // instead, collected once for the whole compilation.
            bool mayCall = MentionsRpcMethodName(semanticModel.Compilation, sourceText);

            if (!mayDeclare && !mayCall)
                return tree;

            var rewriter = new Rewriter(semanticModel, context);
            SyntaxNode newRoot = rewriter.Visit(root);

            if (!rewriter.ChangesMade || newRoot == null)
                return tree;

            return tree.WithRootAndOptions(newRoot, tree.Options);
        }

        /// <summary>
        /// Whether this file's text mentions any name declared as an [Rpc] in this compilation.
        /// </summary>
        private static bool MentionsRpcMethodName(Compilation compilation, string sourceText)
        {
            if (compilation == null)
                return false;

            string[] names = RpcMethodNamesByCompilation.GetValue(compilation, CollectRpcMethodNames);

            for (int i = 0; i < names.Length; i++)
            {
                if (sourceText.IndexOf(names[i], StringComparison.Ordinal) >= 0)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Every method name in the compilation that carries an [Rpc]-family attribute.
        /// </summary>
        /// <remarks>
        /// Names rather than symbols: this only has to decide whether a file is worth walking, and a name
        /// comparison needs no semantic model. A name that collides with an unrelated method costs that
        /// file one walk, where the per-invocation symbol check rejects it.
        ///
        /// Only source declarations are collected, which is the whole reachable set: CE marks a receiver
        /// [NetworkCallable] by rewriting its tree, so an [Rpc] that is not in this compilation was never
        /// made network callable and must not have its call sites rewritten either.
        /// </remarks>
        private static string[] CollectRpcMethodNames(Compilation compilation)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (SyntaxTree candidate in compilation.SyntaxTrees)
            {
                // Method bodies cannot hold an [Rpc] declaration, so they are not descended into. On a
                // project-sized tree set that is the difference between walking declarations and walking
                // every expression in the project once per compile.
                IEnumerable<SyntaxNode> declarations = candidate
                    .GetRoot()
                    .DescendantNodes(node => !(node is MethodDeclarationSyntax));

                foreach (SyntaxNode node in declarations)
                {
                    if (!(node is MethodDeclarationSyntax method))
                        continue;

                    if (FindRpcAttribute(method.AttributeLists, out _) != null)
                        names.Add(method.Identifier.Text);
                }
            }

            if (names.Count == 0)
                return Array.Empty<string>();

            var result = new string[names.Count];
            names.CopyTo(result);
            return result;
        }

        /// <summary>
        /// The [Rpc]-family attribute written on a declaration, if any, along with its short name as
        /// written. Purely syntactic: it answers "did the author ask for an RPC here", which has to be
        /// answerable even when the semantic model cannot bind the attribute type.
        /// </summary>
        private static AttributeSyntax FindRpcAttribute(SyntaxList<AttributeListSyntax> attributeLists, out string shortName)
        {
            foreach (AttributeListSyntax list in attributeLists)
            {
                foreach (AttributeSyntax attribute in list.Attributes)
                {
                    string written = attribute.Name.ToString();
                    string candidate = written.Substring(written.LastIndexOf('.') + 1);

                    if (candidate == "Rpc" || candidate == "RpcAttribute" ||
                        candidate == "RpcOwnerOnly" || candidate == "RpcOwnerOnlyAttribute")
                    {
                        shortName = candidate;
                        return attribute;
                    }
                }
            }

            shortName = null;
            return null;
        }

        private sealed class RpcSpec
        {
            public string Target = "All";
            public float RateLimit;
            public bool OwnerOnly;
        }

        private sealed class Rewriter : CSharpSyntaxRewriter
        {
            private readonly SemanticModel _semanticModel;
            private readonly OptimizationContext _context;

            public bool ChangesMade { get; private set; }

            public Rewriter(SemanticModel semanticModel, OptimizationContext context)
            {
                _semanticModel = semanticModel;
                _context = context;
            }

            public override SyntaxNode VisitMethodDeclaration(MethodDeclarationSyntax node)
            {
                RpcSpec spec = ReadRpcSpec(node);
                if (spec == null)
                    return base.VisitMethodDeclaration(node);

                if (!node.ReturnType.ToString().Equals("void", StringComparison.Ordinal))
                {
                    throw new CodeGenerationException(
                        "CE0043",
                        node.Identifier.GetLocation(),
                        "[Rpc] method '" + node.Identifier.Text + "' must return void. Network events discard " +
                        "return values, so a non-void [Rpc] silently loses its result on every remote client.");
                }

                if (node.ParameterList.Parameters.Count > 8)
                {
                    throw new CodeGenerationException(
                        "CE0011",
                        node.Identifier.GetLocation(),
                        "[Rpc] method '" + node.Identifier.Text + "' has " + node.ParameterList.Parameters.Count +
                        " parameters. VRChat supports at most 8 parameters on a network event.");
                }

                ValidateParameterList(node);

                var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node);

                if (!HasAttribute(visited.AttributeLists, "NetworkCallable"))
                {
                    int maxEventsPerSecond = ResolveRateLimit(spec, visited.ParameterList.Parameters.Count, node);

                    AttributeListSyntax networkCallable = SyntaxFactory.AttributeList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Attribute(
                                SyntaxFactory.ParseName("global::VRC.SDK3.UdonNetworkCalling.NetworkCallable"),
                                SyntaxFactory.AttributeArgumentList(
                                    SyntaxFactory.SingletonSeparatedList(
                                        SyntaxFactory.AttributeArgument(
                                            SyntaxFactory.ParseExpression(
                                                maxEventsPerSecond.ToString(CultureInfo.InvariantCulture))))))));

                    visited = visited.AddAttributeLists(LayOutLikeWrittenAttributes(visited, networkCallable));

                    ChangesMade = true;
                }

                if (spec.OwnerOnly)
                {
                    // An expression-bodied RPC has no statement list to prepend the guard to. Skipping it
                    // shipped [NetworkCallable] with no owner check at all, so the method stayed callable by
                    // any player while its attribute claimed otherwise -- a silently unprotected method is
                    // worse than either an error or this conversion. The declaration is guaranteed void
                    // above, so the expression is always a valid statement.
                    if (visited.Body == null && visited.ExpressionBody != null)
                    {
                        BlockSyntax block = SyntaxFactory.Block(
                            SyntaxFactory.ExpressionStatement(visited.ExpressionBody.Expression));

                        // The line break after a method lives on its semicolon, which this shape drops. Every
                        // later diagnostic in the file would then point one line too high.
                        block = block.WithCloseBraceToken(
                            block.CloseBraceToken.WithTrailingTrivia(visited.SemicolonToken.TrailingTrivia));

                        visited = visited
                            .WithBody(block)
                            .WithExpressionBody(null)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));

                        ChangesMade = true;
                    }

                    if (visited.Body != null && !visited.Body.ToString().Contains("__ce_rpcCaller"))
                    {
                        visited = visited.WithBody(
                            visited.Body.WithStatements(
                                visited.Body.Statements.Insert(0, BuildOwnerGuard(visited.Identifier.Text))));

                        ChangesMade = true;
                    }
                }

                _context.RecordOptimization(
                    optimizerId: "CEGEN009",
                    description: "[Rpc] '" + node.Identifier.Text + "' is now a real network event (target " + spec.Target + ").",
                    location: node.GetLocation(),
                    originalCode: node.Identifier.Text,
                    optimizedCode: "NetworkCallable");

                return visited;
            }

            /// <summary>
            /// Rejects parameter shapes VRChat's event dispatch cannot bind.
            /// </summary>
            /// <remarks>
            /// A call site becomes SendCustomNetworkEvent, which forwards exactly the arguments that were
            /// written. VRChat matches an incoming event to its entry point by argument count and type and
            /// drops it when they disagree -- with no compile-time signal and nothing logged on the sender.
            /// So '[Rpc] public void Play(int id, float volume = 1f)' called as 'Play(3)' sent one argument
            /// to a two-parameter entry point and simply never ran, on any client.
            ///
            /// Materialising the default at the call site was the alternative and was rejected: a default
            /// is not always expressible as an argument of the right runtime type ('= default' on a struct
            /// has no constant, an enum default folds to its underlying integer), and an argument of the
            /// wrong runtime type is dropped by the same silent path. Refusing the declaration is the only
            /// shape that cannot fail quietly. 'params' fails identically -- an expanded call sends N
            /// arguments to a one-parameter entry point.
            ///
            /// A by-reference parameter fails for a related reason: a network event carries values one way
            /// and has no reply channel, so nothing could ever be written back to the caller.
            /// </remarks>
            private static void ValidateParameterList(MethodDeclarationSyntax node)
            {
                foreach (ParameterSyntax parameter in node.ParameterList.Parameters)
                {
                    foreach (SyntaxToken modifier in parameter.Modifiers)
                    {
                        if (modifier.IsKind(SyntaxKind.RefKeyword) ||
                            modifier.IsKind(SyntaxKind.OutKeyword) ||
                            modifier.IsKind(SyntaxKind.InKeyword))
                        {
                            throw new CodeGenerationException(
                                "CE0047",
                                parameter.GetLocation(),
                                "[Rpc] method '" + node.Identifier.Text + "' declares parameter '" +
                                parameter.Identifier.Text + "' as '" + modifier.Text + "'. A network event " +
                                "carries values one way and has no reply channel, so a by-reference " +
                                "parameter can never be written back to the caller and VRChat's dispatch " +
                                "cannot bind it. Pass the value in and send any result back with a second " +
                                "[Rpc].");
                        }

                        if (modifier.IsKind(SyntaxKind.ParamsKeyword))
                        {
                            throw new CodeGenerationException(
                                "CE0050",
                                parameter.GetLocation(),
                                "[Rpc] method '" + node.Identifier.Text + "' declares 'params " +
                                parameter.Identifier.Text + "'. CE forwards exactly the arguments written " +
                                "at the call site, so an expanded call would send one argument per element " +
                                "to a single-parameter entry point; VRChat drops the mismatched event " +
                                "without a runtime error, so the call would silently never run. Declare the " +
                                "array parameter without 'params' and pass the array explicitly.");
                        }
                    }

                    if (parameter.Default != null)
                    {
                        throw new CodeGenerationException(
                            "CE0050",
                            parameter.GetLocation(),
                            "[Rpc] method '" + node.Identifier.Text + "' gives parameter '" +
                            parameter.Identifier.Text + "' a default value. CE forwards exactly the " +
                            "arguments written at the call site, so a call that omits it would send fewer " +
                            "arguments than the entry point declares; VRChat drops the mismatched event " +
                            "without a runtime error, so that call would silently never run on any client. " +
                            "Remove the default and pass the value at every call site.");
                    }
                }
            }

            /// <summary>
            /// Gives an injected attribute list the layout of the [Rpc] it is emitted beside.
            /// </summary>
            /// <remarks>
            /// AddAttributeLists splices the new list in carrying no trivia at all, so it landed hard
            /// against the modifier that used to follow the last written attribute:
            /// '[Rpc(...)]\n[global::...NetworkCallable(2)]    public void PlaySound'. The tree still
            /// compiles, but every surface that shows the rewritten source -- the generated-source dump and
            /// any diagnostic quoting the member -- showed a mangled declaration.
            ///
            /// Both halves are copied from what is already there rather than synthesized, so a declaration
            /// written on one line ('[Rpc] public void X()') stays on one line and the file's own line
            /// ending is preserved.
            /// </remarks>
            private static AttributeListSyntax LayOutLikeWrittenAttributes(
                MethodDeclarationSyntax declaration,
                AttributeListSyntax attributeList)
            {
                if (declaration.AttributeLists.Count == 0)
                    return attributeList;

                // Whatever token follows the written attributes carries the member's indentation.
                SyntaxToken anchor = declaration.Modifiers.Count > 0
                    ? declaration.Modifiers[0]
                    : declaration.ReturnType.GetFirstToken();

                SyntaxTriviaList leading = anchor.LeadingTrivia;

                // Only the indentation, never a comment sitting between the attribute and the modifier:
                // copying that would print the author's comment twice.
                SyntaxTriviaList indentation =
                    leading.Count > 0 && leading[leading.Count - 1].IsKind(SyntaxKind.WhitespaceTrivia)
                        ? SyntaxFactory.TriviaList(leading[leading.Count - 1])
                        : SyntaxFactory.TriviaList();

                return attributeList
                    .WithLeadingTrivia(indentation)
                    .WithTrailingTrivia(declaration.AttributeLists[declaration.AttributeLists.Count - 1].GetTrailingTrivia());
            }

            private static StatementSyntax BuildOwnerGuard(string methodName)
            {
                string guard =
                    "{ global::VRC.SDKBase.VRCPlayerApi __ce_rpcCaller = " +
                    "global::VRC.SDK3.UdonNetworkCalling.NetworkCalling.CallingPlayer; " +
                    "if (__ce_rpcCaller != null && !global::VRC.SDKBase.Networking.IsOwner(__ce_rpcCaller, gameObject)) " +
                    "{ global::UnityEngine.Debug.LogWarning(\"[CE.Rpc] Dropped a non-owner call to " + methodName + ".\"); " +
                    "return; } }";

                return SyntaxFactory.ParseStatement(guard);
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                // 'nameof(MyRpc)' is an InvocationExpression like any other and its argument resolves to the
                // same method symbol a call would, so without this it becomes a send and the string the
                // author wanted vanishes. Returning before the base visit also covers the argument subtree;
                // nameof cannot contain a real invocation, so nothing is lost by not descending.
                if (IsNameOf(node))
                    return node;

                // A call that already goes through CENet is the author saying "send from here, on purpose".
                // Rewriting anything inside it would either double-send or re-broadcast the very hop the
                // escape hatch exists to make explicit. [Rpc] methods are void, so no [Rpc] call can appear
                // as an argument here and skipping the subtree cannot swallow a real call site.
                if (IsCENetInvocation(node))
                    return node;

                var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);

                // Resolve against the ORIGINAL node; the rewritten one is not in the model's tree.
                if (!(_semanticModel.GetSymbolInfo(node).Symbol is IMethodSymbol target))
                    return visited;

                RpcSpec spec = ReadRpcSpec(target, node.GetLocation(), target.Name);
                if (spec == null || spec.Target == "Self")
                    return visited;

                // Only rewrite when the [Rpc] method is the thing actually being invoked. A method-group
                // conversion ('Register(MyRpc)') hands the same symbol to the model, and turning "pass this
                // method" into "call this method over the network" would be a silent behaviour change. An
                // unresolvable expression is left to the rewrite below rather than skipped, so a gap in the
                // optimizer's semantic model can never quietly un-send a call that used to send.
                ISymbol invoked = _semanticModel.GetSymbolInfo(node.Expression).Symbol;
                if (invoked != null && !SymbolEqualityComparer.Default.Equals(invoked, target))
                    return visited;

                MethodDeclarationSyntax enclosing = node.FirstAncestorOrSelf<MethodDeclarationSyntax>();
                bool insideRpc = enclosing != null && FindRpcAttribute(enclosing.AttributeLists, out _) != null;

                if (insideRpc)
                {
                    // Direct recursion on this same behaviour is left as a plain local call. The receiving
                    // client is already running the body, so rewriting it would make that client re-broadcast
                    // to everyone and the recursion would fan out across the instance instead of unwinding
                    // locally. A recursive [Rpc] was a hard CE0045 before this, which made the shape
                    // inexpressible rather than merely dangerous.
                    if (IsSelfCall(node, target, enclosing))
                        return visited;

                    // Everything else genuinely multiplies traffic: each receiver would send again.
                    throw new CodeGenerationException(
                        "CE0045",
                        node.GetLocation(),
                        "[Rpc] method '" + target.Name + "' is called from inside [Rpc] method '" +
                        enclosing.Identifier.Text + "'. Because CE rewrites [Rpc] call sites into network " +
                        "events, every receiving client would re-broadcast. Extract the shared logic into a " +
                        "plain private method and call that from both, or -- if the second hop is what you " +
                        "want -- send it explicitly with CENet.Rpc(target, RpcTarget." + spec.Target +
                        ", nameof(" + target.Name + "), ...), which is not rewritten.");
                }

                string receiver = visited.Expression is MemberAccessExpressionSyntax memberAccess
                    ? memberAccess.Expression.ToString()
                    : "this";

                var arguments = new List<string>
                {
                    "global::VRC.Udon.Common.Interfaces.NetworkEventTarget." + spec.Target,
                    "\"" + target.Name + "\"",
                };
                arguments.AddRange(BuildForwardedArguments(node, visited.ArgumentList, target));

                ExpressionSyntax rewritten = SyntaxFactory
                    .ParseExpression(receiver + ".SendCustomNetworkEvent(" + string.Join(", ", arguments) + ")")
                    .WithTriviaFrom(visited);

                ChangesMade = true;

                // Without this the report only listed declarations, so it could say which methods became
                // network callable but not which of the author's calls now cost bandwidth -- which is the
                // question the report exists to answer.
                _context.RecordOptimization(
                    optimizerId: "CEGEN009",
                    description: "Call to [Rpc] '" + target.Name + "' now sends a network event to " + spec.Target + ".",
                    location: node.GetLocation(),
                    originalCode: node.ToString(),
                    optimizedCode: rewritten.ToString());

                return rewritten;
            }

            /// <summary>
            /// The argument expressions to forward, in the receiver's parameter order.
            /// </summary>
            /// <remarks>
            /// Copying each argument's full text carried its name colon into the send, so an ordinary
            /// 'PlaySound(soundId: 3)' became 'SendCustomNetworkEvent(target, "PlaySound", soundId: 3)' --
            /// which does not compile, meaning a legal C# call broke the build of the whole project.
            /// SendCustomNetworkEvent takes a 'params object[]', so only the expression may be forwarded,
            /// and it has to be forwarded in the receiver's parameter order rather than the written order.
            /// </remarks>
            private static List<string> BuildForwardedArguments(
                InvocationExpressionSyntax node,
                ArgumentListSyntax argumentList,
                IMethodSymbol target)
            {
                var forwarded = new string[target.Parameters.Length];
                bool reordered = false;
                int previousIndex = -1;

                foreach (ArgumentSyntax argument in argumentList.Arguments)
                {
                    // Unreachable through a declaration this pass has seen -- ValidateParameterList refuses
                    // by-reference parameters -- but an [Rpc] resolved from outside this compilation has
                    // been through no such check, and 'ref x' inside a params object[] does not compile.
                    if (!argument.RefKindKeyword.IsKind(SyntaxKind.None))
                    {
                        throw new CodeGenerationException(
                            "CE0047",
                            argument.GetLocation(),
                            "Call to [Rpc] '" + target.Name + "' passes an argument by reference. A network " +
                            "event carries values one way, so there is nothing to write back and the send " +
                            "cannot be emitted.");
                    }

                    int parameterIndex = previousIndex + 1;

                    if (argument.NameColon != null)
                    {
                        parameterIndex = IndexOfParameter(target, argument.NameColon.Name.Identifier.Text);

                        if (parameterIndex < 0)
                        {
                            throw new CodeGenerationException(
                                "CE0048",
                                argument.GetLocation(),
                                "Call to [Rpc] '" + target.Name + "' names an argument '" +
                                argument.NameColon.Name.Identifier.Text + "' that is not a parameter of it, " +
                                "so CE cannot tell which position to send it in.");
                        }
                    }

                    if (parameterIndex >= forwarded.Length || forwarded[parameterIndex] != null)
                    {
                        throw new CodeGenerationException(
                            "CE0050",
                            node.GetLocation(),
                            "Call to [Rpc] '" + target.Name + "' passes " + argumentList.Arguments.Count +
                            " arguments to a method declaring " + forwarded.Length + ". A network event is " +
                            "dispatched by argument count, so CE will not emit a send it knows VRChat would " +
                            "drop.");
                    }

                    if (parameterIndex < previousIndex)
                        reordered = true;

                    forwarded[parameterIndex] = argument.Expression.ToString();
                    previousIndex = parameterIndex;
                }

                for (int i = 0; i < forwarded.Length; i++)
                {
                    if (forwarded[i] != null)
                        continue;

                    // Only reachable for an [Rpc] whose declaration this pass never validated (optional or
                    // 'params' parameters are refused at the declaration). Sending fewer arguments than the
                    // entry point declares is dropped by VRChat with no runtime error at all.
                    throw new CodeGenerationException(
                        "CE0050",
                        node.GetLocation(),
                        "Call to [Rpc] '" + target.Name + "' does not pass parameter '" +
                        target.Parameters[i].Name + "'. A network event is dispatched by argument count, so " +
                        "the receiver would never run. Pass every parameter explicitly.");
                }

                if (reordered)
                {
                    foreach (ArgumentSyntax argument in argumentList.Arguments)
                    {
                        if (IsOrderIndependent(argument.Expression))
                            continue;

                        throw new CodeGenerationException(
                            "CE0048",
                            node.GetLocation(),
                            "Call to [Rpc] '" + target.Name + "' passes named arguments out of parameter " +
                            "order, and at least one of them can change state when it runs ('" +
                            argument.Expression + "'). The send has to list arguments in parameter order, " +
                            "which would evaluate yours in a different order than C# does. Write the " +
                            "arguments in parameter order.");
                    }
                }

                return forwarded.ToList();
            }

            private static int IndexOfParameter(IMethodSymbol target, string name)
            {
                for (int i = 0; i < target.Parameters.Length; i++)
                {
                    if (string.Equals(target.Parameters[i].Name, name, StringComparison.Ordinal))
                        return i;
                }

                return -1;
            }

            /// <summary>
            /// Whether an argument expression can be moved past the others without changing what runs.
            /// </summary>
            /// <remarks>
            /// C# evaluates arguments in written order, so putting named arguments back into parameter
            /// order moves their evaluation. That is invisible for a literal or a field read, and
            /// observable for anything that can write state -- which is why
            /// 'Fire(damage: Roll(), id: NextId())' is reported instead of having its two calls quietly
            /// swapped.
            /// </remarks>
            private static bool IsOrderIndependent(ExpressionSyntax expression)
            {
                if (expression is LiteralExpressionSyntax ||
                    expression is ThisExpressionSyntax ||
                    expression is IdentifierNameSyntax ||
                    expression is PredefinedTypeSyntax)
                {
                    return true;
                }

                if (expression is ParenthesizedExpressionSyntax parenthesized)
                    return IsOrderIndependent(parenthesized.Expression);

                if (expression is MemberAccessExpressionSyntax memberAccess)
                    return IsOrderIndependent(memberAccess.Expression);

                if (expression is CastExpressionSyntax cast)
                    return IsOrderIndependent(cast.Expression);

                if (expression is PrefixUnaryExpressionSyntax prefix)
                {
                    return (prefix.IsKind(SyntaxKind.UnaryMinusExpression) ||
                            prefix.IsKind(SyntaxKind.UnaryPlusExpression) ||
                            prefix.IsKind(SyntaxKind.LogicalNotExpression) ||
                            prefix.IsKind(SyntaxKind.BitwiseNotExpression)) &&
                           IsOrderIndependent(prefix.Operand);
                }

                if (expression is BinaryExpressionSyntax binary)
                {
                    // Assignment is an AssignmentExpressionSyntax, not a BinaryExpressionSyntax, so every
                    // shape reaching here is a pure operator over its two operands.
                    return IsOrderIndependent(binary.Left) && IsOrderIndependent(binary.Right);
                }

                if (expression is ElementAccessExpressionSyntax elementAccess)
                {
                    if (!IsOrderIndependent(elementAccess.Expression))
                        return false;

                    foreach (ArgumentSyntax index in elementAccess.ArgumentList.Arguments)
                    {
                        if (!IsOrderIndependent(index.Expression))
                            return false;
                    }

                    return true;
                }

                return false;
            }

            /// <summary>
            /// Whether this invocation is a 'nameof(...)'. There is no symbol for nameof itself, so it has
            /// to be matched on the written name.
            /// </summary>
            private static bool IsNameOf(InvocationExpressionSyntax node)
            {
                return node.Expression is IdentifierNameSyntax identifier &&
                       identifier.Identifier.Text == "nameof";
            }

            private bool IsCENetInvocation(InvocationExpressionSyntax node)
            {
                // Syntactic gate first: this runs on every invocation in the file, and a symbol lookup per
                // node is far more expensive than a receiver name comparison. A miss here is safe -- the
                // call then falls through to the normal path, where CENet.Rpc carries no [Rpc] attribute
                // and is left alone anyway.
                if (!(node.Expression is MemberAccessExpressionSyntax memberAccess) ||
                    !memberAccess.Expression.ToString().EndsWith("CENet", StringComparison.Ordinal))
                {
                    return false;
                }

                if (!(_semanticModel.GetSymbolInfo(node).Symbol is IMethodSymbol method))
                    return false;

                return method.ContainingType != null &&
                       method.ContainingType.Name == "CENet" &&
                       method.ContainingType.ContainingNamespace?.ToDisplayString() == "UdonSharp.CE.Net";
            }

            /// <summary>
            /// Whether the call targets the enclosing method on this same behaviour, rather than the same
            /// method on some other instance -- 'other.Tick()' from inside 'Tick()' is a real second hop and
            /// must not be mistaken for recursion.
            /// </summary>
            private bool IsSelfCall(InvocationExpressionSyntax node, IMethodSymbol target, MethodDeclarationSyntax enclosing)
            {
                if (!SymbolEqualityComparer.Default.Equals(_semanticModel.GetDeclaredSymbol(enclosing), target))
                    return false;

                if (node.Expression is IdentifierNameSyntax)
                    return true;

                return node.Expression is MemberAccessExpressionSyntax memberAccess &&
                       memberAccess.Expression is ThisExpressionSyntax;
            }

            /// <summary>
            /// VRChat clamps a parameterized network event to 1..100 events/second and defaults to 5.
            /// Parameterless events are unthrottled (0).
            /// </summary>
            private int ResolveRateLimit(RpcSpec spec, int parameterCount, MethodDeclarationSyntax node)
            {
                if (parameterCount == 0 && spec.RateLimit <= 0f)
                    return 0;

                if (spec.RateLimit <= 0f)
                    return 5;

                int applied = UnityEngine.Mathf.Clamp(UnityEngine.Mathf.CeilToInt(spec.RateLimit), 1, 100);

                if (!UnityEngine.Mathf.Approximately(applied, spec.RateLimit))
                    WarnRateLimitAdjusted(node, spec.RateLimit, applied);

                return applied;
            }

            /// <summary>
            /// Reports a declared rate that [NetworkCallable] cannot express.
            /// </summary>
            /// <remarks>
            /// Nothing said anything before this: RateLimit = 500 became 100 and RateLimit = 0.2f became 1,
            /// both silently. The attribute then read as documentation for a limit that was never in effect,
            /// which is the exact failure [Rpc] was meant to stop -- and the direction matters, because a
            /// silently *raised* limit lets a method spam the shared 11 KB/s budget the author thought they
            /// had capped.
            /// </remarks>
            private void WarnRateLimitAdjusted(MethodDeclarationSyntax node, float declared, int applied)
            {
                string declaredText = declared.ToString("0.###", CultureInfo.InvariantCulture);

                string message =
                    "[CE.Rpc] '" + node.Identifier.Text + "' declares RateLimit = " + declaredText +
                    ", but [NetworkCallable] takes a whole number of events per second between 1 and 100, so " +
                    "VRChat will enforce " + applied + " instead. Set RateLimit = " + applied +
                    " so the declaration matches what runs.";

                if (!string.IsNullOrEmpty(_context.CurrentFilePath))
                {
                    int line = node.Identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                    message += " (" + _context.CurrentFilePath + ":" + line + ")";
                }

                UnityEngine.Debug.LogWarning(message);

                _context.RecordOptimization(
                    optimizerId: "CEGEN009",
                    description: "[Rpc] '" + node.Identifier.Text + "' rate limit " + declaredText +
                                 "/s is not expressible; VRChat will enforce " + applied + "/s.",
                    location: node.Identifier.GetLocation(),
                    originalCode: "RateLimit = " + declaredText,
                    optimizedCode: "NetworkCallable(" + applied + ")");
            }

            private static bool HasAttribute(SyntaxList<AttributeListSyntax> attributeLists, string shortName)
            {
                return attributeLists
                    .SelectMany(list => list.Attributes)
                    .Select(attribute => attribute.Name.ToString())
                    .Any(name => name.Substring(name.LastIndexOf('.') + 1) == shortName);
            }

            /// <summary>
            /// Reads the [Rpc] on a declaration in this file, through the semantic model wherever it binds.
            /// </summary>
            /// <remarks>
            /// The values used to be read straight off the written tokens, so anything that was not the
            /// literal token 'true' -- a const, a constant expression, 'True' -- silently produced
            /// OwnerOnly = false and the method shipped with no owner guard at all while its author
            /// believed it was protected. AttributeData folds constants, which is the only way to see
            /// through '[Rpc(OwnerOnly = Config.OwnerOnly)]'.
            /// </remarks>
            private RpcSpec ReadRpcSpec(MethodDeclarationSyntax node)
            {
                AttributeSyntax written = FindRpcAttribute(node.AttributeLists, out string shortName);
                if (written == null)
                    return null;

                if (_semanticModel.GetDeclaredSymbol(node) is IMethodSymbol symbol)
                {
                    RpcSpec bound = ReadRpcSpec(symbol, node.Identifier.GetLocation(), node.Identifier.Text);
                    if (bound != null)
                        return bound;
                }

                // The attribute is written but the model did not bind it -- an unresolved reference
                // somewhere in the file, or an alias this pass's compilation cannot see. The declaration
                // still has to be honoured, so values are read from the written tokens, and anything that
                // is not a literal is an error rather than a default. Defaulting here is what shipped
                // unprotected methods.
                return ReadRpcSpecFromSyntax(written, shortName, node.Identifier.GetLocation(), node.Identifier.Text);
            }

            /// <summary>
            /// Reads the [Rpc] on a resolved method symbol, which is the only path available for a call
            /// site: the declaration is usually in another file.
            /// </summary>
            private static RpcSpec ReadRpcSpec(IMethodSymbol method, Location location, string memberName)
            {
                foreach (AttributeData attribute in method.GetAttributes())
                {
                    if (!IsRpcAttribute(attribute.AttributeClass, out bool ownerOnlyShorthand))
                        continue;

                    var spec = new RpcSpec { OwnerOnly = ownerOnlyShorthand };

                    if (attribute.ConstructorArguments.Length > 0)
                        spec.Target = ReadTarget(attribute.ConstructorArguments[0], "Target", location, memberName);

                    foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
                    {
                        if (named.Key == "Target")
                        {
                            spec.Target = ReadTarget(named.Value, "Target", location, memberName);
                        }
                        else if (named.Key == "RateLimit")
                        {
                            spec.RateLimit = Convert.ToSingle(
                                RequireConstant(named.Value, "RateLimit", location, memberName),
                                CultureInfo.InvariantCulture);
                        }
                        else if (named.Key == "OwnerOnly")
                        {
                            spec.OwnerOnly = Convert.ToBoolean(
                                RequireConstant(named.Value, "OwnerOnly", location, memberName),
                                CultureInfo.InvariantCulture);
                        }
                    }

                    return spec;
                }

                return ReadRpcSpecFromDeclaration(method, location, memberName);
            }

            /// <summary>
            /// Reads the [Rpc] off the method's own declaration when the attribute type did not bind.
            /// </summary>
            /// <remarks>
            /// This is what makes a CALL SITE work at all. UdonSharp compiles the CE runtime library as
            /// source, so CompilationContext.GetMetadataReferences deliberately leaves UdonSharp.CE.Runtime
            /// out of the reference set the optimizer's semantic compilation is built from. Whenever
            /// RpcAttribute.cs is not itself one of the trees in the pass, [Rpc] binds to an error type:
            /// AttributeData then carries neither the class identity nor any folded argument, so the symbol
            /// read above finds nothing.
            ///
            /// The declaration half already survived that -- it falls back to the written tokens -- so the
            /// receiver went on being marked [NetworkCallable] while every call to it stayed a plain local
            /// call. That is the worst shape available: the method looks network callable, nothing sends to
            /// it, and no error is reported anywhere.
            ///
            /// DeclaringSyntaxReferences reaches the declaration wherever it is in the compilation, so the
            /// cross-file call site this pass exists to rewrite keeps working. An [Rpc] that came from
            /// metadata has no declaration here, and is correctly left alone: CE only makes a receiver
            /// network callable by rewriting its tree, so one it never saw was never made callable either.
            /// </remarks>
            private static RpcSpec ReadRpcSpecFromDeclaration(IMethodSymbol method, Location location, string memberName)
            {
                foreach (SyntaxReference reference in method.OriginalDefinition.DeclaringSyntaxReferences)
                {
                    if (!(reference.GetSyntax() is MethodDeclarationSyntax declaration))
                        continue;

                    AttributeSyntax written = FindRpcAttribute(declaration.AttributeLists, out string shortName);
                    if (written == null)
                        continue;

                    return ReadRpcSpecFromSyntax(written, shortName, location, memberName);
                }

                return null;
            }

            /// <summary>
            /// Whether this attribute is an [Rpc], and whether it is the RpcOwnerOnly shorthand.
            /// </summary>
            /// <remarks>
            /// The shorthand sets OwnerOnly in its own constructor, so the value never reaches metadata and
            /// cannot be read from NamedArguments -- the class identity is the only evidence there is.
            /// </remarks>
            private static bool IsRpcAttribute(INamedTypeSymbol attributeClass, out bool ownerOnlyShorthand)
            {
                ownerOnlyShorthand = false;

                for (INamedTypeSymbol type = attributeClass; type != null; type = type.BaseType)
                {
                    if (type.Name == "RpcOwnerOnlyAttribute")
                        ownerOnlyShorthand = true;

                    if (type.Name == "RpcAttribute")
                        return true;
                }

                ownerOnlyShorthand = false;
                return false;
            }

            private static string ReadTarget(TypedConstant constant, string argumentName, Location location, string memberName)
            {
                object value = RequireConstant(constant, argumentName, location, memberName);
                return RpcTargetName(Convert.ToInt32(value, CultureInfo.InvariantCulture));
            }

            /// <summary>
            /// The folded value of an attribute argument, or a CE error when the compiler could not fold it.
            /// </summary>
            /// <remarks>
            /// Defaulting instead is the dangerous option: an unresolved OwnerOnly reads as false, and a
            /// method with no owner guard whose attribute claims one is the worst outcome available --
            /// nothing in the editor or at runtime reports the missing check.
            /// </remarks>
            private static object RequireConstant(TypedConstant constant, string argumentName, Location location, string memberName)
            {
                if (constant.Kind != TypedConstantKind.Error && constant.Value != null)
                    return constant.Value;

                throw new CodeGenerationException(
                    "CE0049",
                    location,
                    "[Rpc] on '" + memberName + "' has a '" + argumentName + "' the compiler cannot fold to " +
                    "a constant. CE reads these at compile time to emit the owner guard, the rate limit and " +
                    "the send target, and it will not guess: an unresolved OwnerOnly would ship a method any " +
                    "player can call. Use a literal, or a const the compiler can resolve.");
            }

            /// <summary>
            /// Fallback for a written [Rpc] the semantic model could not bind. Literals only: a value that
            /// cannot be read here is an error, never a default.
            /// </summary>
            private static RpcSpec ReadRpcSpecFromSyntax(
                AttributeSyntax attribute,
                string shortName,
                Location location,
                string memberName)
            {
                var spec = new RpcSpec { OwnerOnly = shortName.StartsWith("RpcOwnerOnly", StringComparison.Ordinal) };

                if (attribute.ArgumentList == null)
                    return spec;

                foreach (AttributeArgumentSyntax argument in attribute.ArgumentList.Arguments)
                {
                    string name = argument.NameEquals?.Name.Identifier.Text ??
                                  argument.NameColon?.Name.Identifier.Text;

                    if (name == null || name == "Target" || name == "target")
                    {
                        string targetName = ReadTargetFromSyntax(argument.Expression);

                        if (targetName == null)
                            throw UnreadableArgument("Target", argument, location, memberName);

                        spec.Target = targetName;
                    }
                    else if (name == "RateLimit")
                    {
                        if (!TryReadSingleLiteral(argument.Expression, out spec.RateLimit))
                            throw UnreadableArgument("RateLimit", argument, location, memberName);
                    }
                    else if (name == "OwnerOnly")
                    {
                        if (!TryReadBooleanLiteral(argument.Expression, out spec.OwnerOnly))
                            throw UnreadableArgument("OwnerOnly", argument, location, memberName);
                    }
                }

                return spec;
            }

            private static CodeGenerationException UnreadableArgument(
                string argumentName,
                AttributeArgumentSyntax argument,
                Location location,
                string memberName)
            {
                return new CodeGenerationException(
                    "CE0049",
                    location,
                    "[Rpc] on '" + memberName + "' declares " + argumentName + " = '" + argument.Expression +
                    "', which CE cannot resolve here because the attribute did not bind (usually another " +
                    "compile error in the same file). CE will not fall back to a default for it: an " +
                    "OwnerOnly read as false would ship a method any player can call. Fix the other errors " +
                    "in this file, or write the value as a literal.");
            }

            private static string ReadTargetFromSyntax(ExpressionSyntax expression)
            {
                string written = expression is MemberAccessExpressionSyntax memberAccess
                    ? memberAccess.Name.Identifier.Text
                    : (expression as IdentifierNameSyntax)?.Identifier.Text;

                switch (written)
                {
                    case "All":
                    case "Owner":
                    case "Others":
                    case "Self":
                        return written;
                    default:
                        return null;
                }
            }

            /// <summary>
            /// Reads a numeric literal without going through the current culture.
            /// </summary>
            /// <remarks>
            /// float.TryParse(text) uses CultureInfo.CurrentCulture, so on any comma-decimal locale --
            /// de-DE, fr-FR, pt-BR -- '[Rpc(RateLimit = 2.5f)]' failed to parse, left RateLimit at 0 and
            /// shipped the default rate while the attribute said otherwise. Roslyn has already parsed the
            /// token, so its value is used directly; the invariant parse is only a fallback.
            /// </remarks>
            private static bool TryReadSingleLiteral(ExpressionSyntax expression, out float value)
            {
                value = 0f;
                float sign = 1f;

                while (expression is PrefixUnaryExpressionSyntax prefix &&
                       (prefix.IsKind(SyntaxKind.UnaryMinusExpression) || prefix.IsKind(SyntaxKind.UnaryPlusExpression)))
                {
                    if (prefix.IsKind(SyntaxKind.UnaryMinusExpression))
                        sign = -sign;

                    expression = prefix.Operand;
                }

                if (!(expression is LiteralExpressionSyntax literal) ||
                    !literal.IsKind(SyntaxKind.NumericLiteralExpression))
                {
                    return false;
                }

                object parsed = literal.Token.Value;

                if (parsed is float single)
                {
                    value = sign * single;
                    return true;
                }

                if (parsed is double dbl)
                {
                    value = sign * (float)dbl;
                    return true;
                }

                if (parsed is decimal dec)
                {
                    value = sign * (float)dec;
                    return true;
                }

                if (parsed is IConvertible convertible)
                {
                    value = sign * convertible.ToSingle(CultureInfo.InvariantCulture);
                    return true;
                }

                string text = literal.Token.Text.TrimEnd('f', 'F', 'd', 'D', 'm', 'M');

                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float fallback))
                {
                    value = sign * fallback;
                    return true;
                }

                return false;
            }

            private static bool TryReadBooleanLiteral(ExpressionSyntax expression, out bool value)
            {
                if (expression.IsKind(SyntaxKind.TrueLiteralExpression))
                {
                    value = true;
                    return true;
                }

                if (expression.IsKind(SyntaxKind.FalseLiteralExpression))
                {
                    value = false;
                    return true;
                }

                value = false;
                return false;
            }

            private static string RpcTargetName(int value)
            {
                switch (value)
                {
                    case 1: return "Owner";
                    case 2: return "Others";
                    case 3: return "Self";
                    default: return "All";
                }
            }
        }
    }
}
