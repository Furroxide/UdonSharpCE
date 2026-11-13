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
    /// The two halves have different reach -- a call site is usually in a file that never spells the
    /// attribute, and a declaration can avoid spelling it too, through an alias or a derived attribute
    /// type -- which is why the per-file gate in <see cref="Optimize"/> is the set of [Rpc] method names
    /// rather than anything about how the attribute is written, and why both halves recognise an [Rpc]
    /// through the bound attribute type before falling back to the written name.
    ///
    /// A generated sender method is deliberately NOT emitted. CE transforms only rewrite the trees inside
    /// UdonSharp's private compilation while Unity compiles the original source, so a generated member
    /// would be invisible at the call site. Rewriting the call is the only shape that works. For the same
    /// reason the escape hatch for calls this pass refuses to rewrite -- <c>UdonSharp.CE.Net.CENet</c> --
    /// is hand-written in the runtime library rather than generated here.
    /// </summary>
    internal class RpcEmitter : ICompileTimeOptimizer
    {
        /// <summary>
        /// The one spelling of this emitter's id. The runner matches a discharged obligation to its
        /// generator by this string, so a second literal that drifted from <see cref="OptimizerId"/> would
        /// not fail anything visibly -- the discharge would simply never be counted and USCE0001 would
        /// start failing correct projects.
        /// </summary>
        private const string GeneratorId = "CEGEN009";

        /// <summary>
        /// The exact spelling of the [NetworkCallable] this emitter adds to an [Rpc] that has none.
        /// </summary>
        /// <remarks>
        /// RpcMethodAnalyzer runs over the rewritten trees and reports an [Rpc] whose RateLimit disagrees
        /// with a [NetworkCallable] the author wrote by hand. The one this emitter adds holds the rate it
        /// derived from RateLimit itself, so the analyzer has to be able to tell the two apart, and this
        /// spelling is how: one constant, so the injection and the check cannot drift apart.
        /// </remarks>
        internal const string InjectedNetworkCallableName = "global::VRC.SDK3.UdonNetworkCalling.NetworkCallable";

        public string OptimizerId => GeneratorId;
        public string OptimizerName => "Rpc Network Event Emitter";
        public string Description => "Marks [Rpc] methods [NetworkCallable] and rewrites call sites into network events.";
        public bool IsEnabledByDefault => true;
        public int Priority => 11;

        /// <summary>
        /// The [Rpc] method names declared anywhere in a compilation, keyed by that compilation.
        /// </summary>
        /// <remarks>
        /// This pass has to run on files that never mention "Rpc" -- every cross-file call site, and any
        /// declaration whose attribute is spelled through an alias or a derived type -- so it needs a gate
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

            // One gate decides both halves: the [Rpc] method names declared anywhere in this compilation.
            //
            // A CALL SITE is why the names exist at all: '[Rpc] public void StartGame()' lives in A.cs
            // while 'manager.StartGame()' lives in B.cs, and B.cs need not contain the substring "Rpc"
            // anywhere. Gating the whole pass on that substring skipped B.cs outright, so the receiver was
            // marked [NetworkCallable] while the call stayed a plain local call -- the cross-file case,
            // which is the normal one, silently did not send.
            //
            // A DECLARATION used to keep the substring test, on the reasoning that it is only ever in a
            // file that spells the attribute. It is not: 'using NetCall = UdonSharp.CE.Net.RpcAttribute;'
            // and an attribute type deriving from RpcAttribute both declare an [Rpc] without the letters
            // "Rpc" appearing at the method, or in the second case anywhere in the file. Such a file was
            // never walked, so the method got no [NetworkCallable], no owner guard and no error. A file
            // that declares an [Rpc] necessarily contains that method's own name, so the name table covers
            // declarations too and the substring test is gone.
            if (!MentionsRpcMethodName(semanticModel.Compilation, sourceText))
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
        /// Names rather than symbols are what the gate compares: it only has to decide whether a file is
        /// worth walking. A name that collides with an unrelated method costs that file one walk, where
        /// the per-invocation symbol check rejects it.
        ///
        /// Deciding which methods ARE [Rpc] declarations is a different question, and the written name
        /// alone gets it wrong. '[NetCall]' under 'using NetCall = UdonSharp.CE.Net.RpcAttribute;', or an
        /// attribute class deriving from RpcAttribute, is an [Rpc] to every call site -- those resolve the
        /// bound attribute type -- but was not one here. The declaration was then never marked
        /// [NetworkCallable] and never given its owner guard, while calls to it in the same file were
        /// still rewritten into sends to a receiver that could not accept them. So a method whose
        /// attributes do not spell an [Rpc] is asked through its symbol. Only methods that carry an
        /// attribute list at all get that far, which keeps the semantic model out of the common case.
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
                // Created on first use: most trees have no method that needs its symbol looked at.
                SemanticModel model = null;

                // Method bodies cannot hold an [Rpc] declaration, so they are not descended into. On a
                // project-sized tree set that is the difference between walking declarations and walking
                // every expression in the project once per compile.
                IEnumerable<SyntaxNode> declarations = candidate
                    .GetRoot()
                    .DescendantNodes(node => !(node is MethodDeclarationSyntax));

                foreach (SyntaxNode node in declarations)
                {
                    if (!(node is MethodDeclarationSyntax method) || method.AttributeLists.Count == 0)
                        continue;

                    if (FindRpcAttribute(method.AttributeLists, out _) != null)
                    {
                        names.Add(method.Identifier.Text);
                        continue;
                    }

                    if (model == null)
                        model = compilation.GetSemanticModel(candidate);

                    if (model.GetDeclaredSymbol(method) is IMethodSymbol symbol && HasBoundRpcAttribute(symbol))
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
        /// Whether a method symbol carries an attribute the compiler bound to RpcAttribute or a type
        /// deriving from it, however that attribute was spelled at the declaration.
        /// </summary>
        private static bool HasBoundRpcAttribute(IMethodSymbol method)
        {
            foreach (AttributeData attribute in method.GetAttributes())
            {
                if (IsRpcAttribute(attribute.AttributeClass, out _))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether this bound attribute class is an [Rpc], and whether it is the RpcOwnerOnly shorthand.
        /// </summary>
        /// <remarks>
        /// The shorthand sets OwnerOnly in its own constructor, so the value never reaches metadata and
        /// cannot be read from NamedArguments -- the class identity is the only evidence there is.
        ///
        /// An attribute the compiler could not bind is never a match, whatever the error symbol happens
        /// to be called: it carries no constructor or named arguments, so accepting it would read every
        /// option as its default -- OwnerOnly included -- instead of sending the caller on to the written
        /// tokens, which is the only place an unbound attribute's values still exist.
        /// </remarks>
        private static bool IsRpcAttribute(INamedTypeSymbol attributeClass, out bool ownerOnlyShorthand)
        {
            ownerOnlyShorthand = false;

            if (attributeClass == null || attributeClass.TypeKind == TypeKind.Error)
                return false;

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

        /// <summary>
        /// The [Rpc]-family attribute written on a declaration, if any, along with its short name as
        /// written. Purely syntactic: it answers "did the author ask for an RPC here", which has to be
        /// answerable even when the semantic model cannot bind the attribute type. It is the fallback, not
        /// the test -- an alias or a derived attribute type is invisible to it, so wherever a symbol is
        /// available the bound attribute class is asked first (<see cref="IsRpcAttribute"/>).
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

            /// <summary>
            /// The [Rpc] declaration whose body is being visited, or null outside one.
            /// </summary>
            /// <remarks>
            /// Recorded by the declaration visit rather than re-derived at each call site. Re-deriving it
            /// meant asking the enclosing method's written attribute names again, which is the purely
            /// syntactic test that cannot see an aliased or derived [Rpc] -- so a call made from inside
            /// such a method slipped past CE0045 and was rewritten into the re-broadcast that check
            /// exists to refuse. Methods do not nest, so one slot is enough.
            /// </remarks>
            private MethodDeclarationSyntax _enclosingRpc;

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

                // UdonSharp never exports a static method as an event and builds no network metadata for
                // one, so a static [Rpc] compiled cleanly and then did nothing at all: the call site was
                // still rewritten into a send naming an event no program has, VRChat logged that it does
                // not exist, and the body ran on no client -- including the caller's, which had run it
                // before the rewrite.
                foreach (SyntaxToken modifier in node.Modifiers)
                {
                    if (modifier.IsKind(SyntaxKind.StaticKeyword))
                    {
                        throw new CodeGenerationException(
                            "CE0011",
                            node.Identifier.GetLocation(),
                            "[Rpc] method '" + node.Identifier.Text + "' is static. A network event is " +
                            "delivered to a behaviour instance, and UdonSharp does not export static methods " +
                            "as events, so no client could ever receive this one: the send would name an " +
                            "event that does not exist and the method body would never run. Make it an " +
                            "instance method.");
                    }
                }

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

                MethodDeclarationSyntax outerRpc = _enclosingRpc;
                _enclosingRpc = node;

                var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node);

                _enclosingRpc = outerRpc;

                // Kept to tell, further down, whether THIS declaration needed anything added. ChangesMade
                // cannot answer that: it is per file, and a rewritten call elsewhere in the file sets it too.
                MethodDeclarationSyntax asWritten = visited;

                if (!HasAttribute(visited.AttributeLists, "NetworkCallable"))
                {
                    int maxEventsPerSecond = ResolveRateLimit(spec, node);

                    AttributeListSyntax networkCallable = SyntaxFactory.AttributeList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Attribute(
                                SyntaxFactory.ParseName(InjectedNetworkCallableName),
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

                // An [Rpc] the author already completed by hand -- [NetworkCallable] written next to it, no
                // owner guard owed or one already in the body -- is recognised here and correctly left
                // alone. A project whose every [Rpc] is like that, sent through CENet.Rpc or a hand-written
                // SendCustomNetworkEvent, changes no tree at all, and the USCE0001 tripwire reads "wrote
                // [Rpc], nothing transformed" as this emitter having silently stopped working. Saying so
                // here is what keeps that compile from failing. It is said only for a declaration this
                // method positively identified, never as a blanket "the pass ran".
                if (ReferenceEquals(visited, asWritten))
                    _context.RecordObligationDischarged(GeneratorId);

                _context.RecordOptimization(
                    optimizerId: GeneratorId,
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
                // 'this.gameObject', not a bare 'gameObject': the guard is spliced into the author's own
                // method body, so a parameter or local that happens to be called gameObject would capture
                // the bare name -- the ownership check then tests the wrong object, or the injected
                // statement stops compiling with an error pointing at code the author never wrote.
                string guard =
                    "{ global::VRC.SDKBase.VRCPlayerApi __ce_rpcCaller = " +
                    "global::VRC.SDK3.UdonNetworkCalling.NetworkCalling.CallingPlayer; " +
                    "if (__ce_rpcCaller != null && !global::VRC.SDKBase.Networking.IsOwner(__ce_rpcCaller, this.gameObject)) " +
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

                MethodDeclarationSyntax enclosing = _enclosingRpc;

                if (enclosing != null)
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
                    optimizerId: GeneratorId,
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
            ///
            /// <paramref name="argumentList"/> is the REWRITTEN list, because that is the text to forward;
            /// <paramref name="node"/> still carries the original one, which is the only one the semantic
            /// model can answer type questions about. Visiting never adds or removes an argument, so the
            /// two are read in step by position.
            /// </remarks>
            private List<string> BuildForwardedArguments(
                InvocationExpressionSyntax node,
                ArgumentListSyntax argumentList,
                IMethodSymbol target)
            {
                var forwarded = new string[target.Parameters.Length];
                bool reordered = false;
                int previousIndex = -1;

                SeparatedSyntaxList<ArgumentSyntax> writtenArguments = node.ArgumentList.Arguments;

                for (int argumentIndex = 0; argumentIndex < argumentList.Arguments.Count; argumentIndex++)
                {
                    ArgumentSyntax argument = argumentList.Arguments[argumentIndex];

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

                    ExpressionSyntax written = argumentIndex < writtenArguments.Count
                        ? writtenArguments[argumentIndex].Expression
                        : null;

                    forwarded[parameterIndex] = ForwardAsParameterType(
                        argument.Expression.ToString(),
                        written,
                        target.Parameters[parameterIndex]);

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

            /// <summary>
            /// The text to forward for one argument, converted to the declared parameter type whenever the
            /// written expression is not already of that type.
            /// </summary>
            /// <remarks>
            /// A direct call converts its arguments: 'SetVolume(1)' against 'void SetVolume(float v)' passes
            /// 1f. The send does not, because every SendCustomNetworkEvent parameter is 'object' -- the same
            /// text boxes an Int32. VRChat checks each argument's runtime type against the entry point's
            /// parameter and refuses the event when they differ, so an ordinary call relying on an implicit
            /// conversion (int to float, int to long, a constant to byte, Vector2 to Vector3) compiled
            /// cleanly and then never ran; under ClientSim the refusal is an exception inside the extern,
            /// which halts the calling behaviour as well. Spelling the conversion as a cast restores what
            /// the direct call did.
            ///
            /// An expression whose type comes from where it is written needs the same treatment for a
            /// different reason. 'default', 'new(1, 2, 3)' and a conditional with no type of its own
            /// ('flag ? default : default') are given the parameter's type by the direct call -- Roslyn
            /// even reports that type as theirs -- but moved into an 'object' slot they are retyped by it:
            /// 'default' becomes null, 'new()' becomes 'new object()', and 'new(1, 2, 3)' stops compiling,
            /// with the error pointing at code the author never wrote. So a target-typed argument is cast
            /// whatever type the model reports for it, which hands it back the target the call gave it.
            ///
            /// Nothing is added when the types already agree, for a 'null' literal (null is the same value
            /// in any slot, and the right thing to send for a reference parameter), or when the model
            /// could not bind the argument: an invented cast would turn a gap in the model into a compile
            /// error in code the author never wrote.
            /// </remarks>
            private string ForwardAsParameterType(string forwardedText, ExpressionSyntax written, IParameterSymbol parameter)
            {
                ITypeSymbol parameterType = parameter.Type;

                if (written == null || parameterType == null || parameterType.TypeKind == TypeKind.Error)
                    return forwardedText;

                ExpressionSyntax unwrapped = written;
                while (unwrapped is ParenthesizedExpressionSyntax parenthesized)
                    unwrapped = parenthesized.Expression;

                if (unwrapped.IsKind(SyntaxKind.NullLiteralExpression))
                    return forwardedText;

                string parameterTypeName = parameterType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

                if (unwrapped.IsKind(SyntaxKind.DefaultLiteralExpression))
                    return "default(" + parameterTypeName + ")";

                TypeInfo typeInfo = _semanticModel.GetTypeInfo(written);

                // The second arm is an expression with no natural type that the call converted to this
                // parameter's type -- 'flag ? default : default'.
                bool targetTyped =
                    unwrapped is ImplicitObjectCreationExpressionSyntax ||
                    (typeInfo.Type == null &&
                     SymbolEqualityComparer.Default.Equals(typeInfo.ConvertedType, parameterType));

                if (!targetTyped)
                {
                    ITypeSymbol argumentType = typeInfo.Type;

                    if (argumentType == null || argumentType.TypeKind == TypeKind.Error)
                        return forwardedText;

                    if (SymbolEqualityComparer.Default.Equals(argumentType, parameterType))
                        return forwardedText;
                }

                return "(" + parameterTypeName + ")(" + forwardedText + ")";
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
            /// The whole number of events per second to hand [NetworkCallable]: the declared rate held to
            /// 1..100, or the SDK's own default of 5 when none was declared.
            /// </summary>
            /// <remarks>
            /// [NetworkCallable] has no "unthrottled". The SDK validates every network-callable entry point
            /// when it builds the method's metadata and throws for a rate below 1 or above 100, with or
            /// without parameters. A parameterless [Rpc] with no RateLimit used to be emitted as
            /// [NetworkCallable(0)], on the belief that parameterless events are unthrottled -- true of a
            /// legacy event, which has no [NetworkCallable] at all, and never of one that carries the
            /// attribute -- so the most ordinary declaration there is, '[Rpc] public void StartGame()',
            /// failed the compile with "Rate limit cannot be lower than 1". No declared rate now means the
            /// same thing for every [Rpc]: what a bare [NetworkCallable] would get.
            /// </remarks>
            private int ResolveRateLimit(RpcSpec spec, MethodDeclarationSyntax node)
            {
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
                    optimizerId: GeneratorId,
                    description: "[Rpc] '" + node.Identifier.Text + "' rate limit " + declaredText +
                                 "/s is not expressible; VRChat will enforce " + applied + "/s.",
                    location: node.Identifier.GetLocation(),
                    originalCode: "RateLimit = " + declaredText,
                    optimizedCode: "NetworkCallable(" + applied + ")");
            }

            /// <summary>
            /// Whether an attribute with this short name is written on the declaration, with or without the
            /// 'Attribute' suffix.
            /// </summary>
            /// <remarks>
            /// Both spellings name the same type. Matching only the short one read
            /// '[NetworkCallableAttribute(10)]' as absent and added a second [NetworkCallable] beside it,
            /// which is CS0579 on a method that was already complete.
            /// </remarks>
            private static bool HasAttribute(SyntaxList<AttributeListSyntax> attributeLists, string shortName)
            {
                string longName = shortName + "Attribute";

                return attributeLists
                    .SelectMany(list => list.Attributes)
                    .Select(attribute => attribute.Name.ToString())
                    .Select(name => name.Substring(name.LastIndexOf('.') + 1))
                    .Any(name => name == shortName || name == longName);
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
            ///
            /// The symbol is also asked BEFORE the written name, not only after it matched. Asking the
            /// name first made this return "not an [Rpc]" for '[NetCall]' under
            /// 'using NetCall = UdonSharp.CE.Net.RpcAttribute;' and for any attribute type deriving from
            /// RpcAttribute, while the call-site half, which only ever had the symbol, went on treating the
            /// same method as one: calls were rewritten into sends, and the receiver was left without
            /// [NetworkCallable], without its rate limit and without its owner guard.
            /// </remarks>
            private RpcSpec ReadRpcSpec(MethodDeclarationSyntax node)
            {
                // No attribute list, no [Rpc] -- and no symbol lookup for the overwhelming majority of
                // methods this is asked about.
                if (node.AttributeLists.Count == 0)
                    return null;

                if (_semanticModel.GetDeclaredSymbol(node) is IMethodSymbol symbol)
                {
                    RpcSpec bound = ReadRpcSpec(symbol, node.Identifier.GetLocation(), node.Identifier.Text);
                    if (bound != null)
                        return bound;
                }

                AttributeSyntax written = FindRpcAttribute(node.AttributeLists, out string shortName);
                if (written == null)
                    return null;

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
