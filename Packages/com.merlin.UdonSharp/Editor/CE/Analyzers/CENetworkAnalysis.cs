using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Shared syntax and semantic-model helpers for the network-correctness analyzers (CE0014-CE0018,
    /// CE0063).
    ///
    /// Those analyzers all answer variations of the same three questions -- "does this write a synced
    /// field", "can an ownership check reach this code", "is RequestSerialization reachable" -- and every
    /// one of them describes a bug that produces no error at runtime. Keeping the lookups in one place is
    /// what stops five copies of the same call-graph walk from drifting apart and disagreeing about which
    /// writes count.
    ///
    /// Every entry point tolerates a null semantic model, a symbol with no source, and unexpected node
    /// kinds. An analyzer that throws stops checking, and a check that stops silently is exactly the
    /// failure mode these analyzers exist to catch.
    ///
    /// <para>
    /// Three entry points here read the base chain, and each of them owes the same debt: widening a search
    /// may only ever <em>suppress</em> a report, never create one, move one, or change which one is
    /// emitted. <see cref="BuildMethodGraph"/> pays it by admitting base declarations as callees only,
    /// never as report sites and never as callers, and by dropping any base declaration a nearer type
    /// shadows. <see cref="CollectOwnershipDerivedNames"/> pays it by returning a strict superset of the
    /// own-only name set, which can only make more code look guarded.
    /// <see cref="FindFirstRequestSerializationIncludingBases"/> pays it by being opt-in: the plain
    /// <see cref="FindFirstRequestSerialization"/> stays own-only, so a caller that <em>branches</em> on
    /// the answer instead of gating on it cannot be widened by accident. Anything added here that reads a
    /// base has to answer this question before it ships, because every one of these checks is a check
    /// against a bug that produces no runtime error -- a report this file loses is a report nobody else
    /// makes.
    /// </para>
    /// <para>
    /// Known limitation -- detection is own-declarations only while guards and publishes read the base
    /// chain. <see cref="GetSyncedFields"/> reads <c>TypeSymbol.FieldSymbols</c>, which is this type's own
    /// members and nothing above it, so a project that hoists synced state into a shared entity base
    /// ('[UdonSynced] protected float _health;' on NetEntity, written by every subclass) gets no CE0014,
    /// CE0015 or CE0063 coverage on those fields at all. The concrete subclass is the type that gets
    /// analyzed and it sees zero synced fields, so all three return at their first gate; the base that
    /// declares them is never analyzed on its own pass, because UdonSharpCompilerV1 collects only
    /// concrete, non-generic root types (UdonSharpCompilerV1.cs:616). Nothing is wrong with the behaviour
    /// -- the emitter walks the base chain when it collects userFields (EmitContext.cs:149-166), so those
    /// fields really are synced -- the hole is entirely in the checking.
    /// </para>
    /// <para>
    /// That is recorded rather than fixed because it is not the same kind of change as widening the guard
    /// and publish walks was. Those walks can only ever <em>suppress</em> a report, so admitting the base
    /// chain there removes false positives and risks a missed report at worst. GetSyncedFields feeds the
    /// detection side: making it base-aware manufactures diagnostics on code that compiles clean today,
    /// one per concrete subclass of every shared base, each landing on a field declaration in a file the
    /// analyzed type does not own. Where that report goes, how it is de-duplicated across five subclasses
    /// of one base, and what an author is supposed to do about a warning on someone else's file are all
    /// open questions, and they need their own design pass and their own regression run rather than a
    /// one-line widening attached to this one.
    /// </para>
    /// </summary>
    internal static class CENetworkAnalysis
    {
        /// <summary>
        /// Per-frame entry points. RequestSerialization() from any of these is a bandwidth problem unless
        /// it is rate gated.
        /// </summary>
        internal static readonly HashSet<string> UpdateMethodNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "Update",
            "FixedUpdate",
            "LateUpdate",
            "PostLateUpdate"
        };

        private const string NetworkingTypeName = "VRC.SDKBase.Networking";
        private const string PlayerApiTypeName = "VRC.SDKBase.VRCPlayerApi";
        private const string UnityTimeTypeName = "UnityEngine.Time";
        private const string UdonSharpBehaviourTypeName = "UdonSharpBehaviour";
        private const string CeSyncAttributeTypeName = "UdonSharp.CE.Net.SyncAttribute";

        /// <summary>
        /// The local RpcEmitter injects into an OwnerOnly [Rpc] to hold NetworkCalling.CallingPlayer.
        /// </summary>
        private const string RpcCallerLocalName = "__ce_rpcCaller";

        /// <summary>
        /// Members whose only reason to appear in a behaviour is deciding who is allowed to write.
        /// </summary>
        private static readonly HashSet<string> OwnershipMemberNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "IsOwner",
            "SetOwner",
            "GetOwner",
            "IsMaster",
            "isMaster",
            "IsInstanceOwner",
            "isInstanceOwner"
        };

        /// <summary>
        /// Members that make a guard time-based rather than per-frame. The delta members are in here
        /// because the accumulator shape ('_timer += Time.deltaTime; if (_timer > interval)') is just as
        /// much a rate gate as comparing absolute times.
        /// </summary>
        private static readonly HashSet<string> TimeMemberNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "time",
            "timeSinceLevelLoad",
            "unscaledTime",
            "fixedTime",
            "fixedUnscaledTime",
            "realtimeSinceStartup",
            "frameCount",
            "deltaTime",
            "fixedDeltaTime",
            "unscaledDeltaTime",
            "smoothDeltaTime",
            "GetServerTimeInSeconds",
            "GetServerTimeInMilliseconds"
        };

        /// <summary>A synced-field write, tied back to the field symbol and the method it happens in.</summary>
        internal sealed class SyncedFieldWrite
        {
            public FieldSymbol Field { get; }
            public SyntaxNode Node { get; }
            public MethodDeclarationSyntax Method { get; }

            public string MethodName => Method?.Identifier.Text ?? "<unknown>";

            public SyncedFieldWrite(FieldSymbol field, SyntaxNode node, MethodDeclarationSyntax method)
            {
                Field = field;
                Node = node;
                Method = method;
            }
        }

        /// <summary>
        /// The call graph over one behaviour and the user-written bases it inherits from, in both
        /// directions.
        ///
        /// Direction matters for ownership, and the two directions carry different weight. A guard in a
        /// helper the writer calls (EnsureOwnership()) gates the writer wherever it is reached from, so
        /// the transitive callee walk is the right question there. A guard in a caller only gates the
        /// writer when every call site has one, so that direction is asked one edge at a time through
        /// <see cref="DirectCallers"/>: the transitive caller walk cannot tell a gated call site apart
        /// from an ungated one three frames further up.
        ///
        /// That difference is also why the base classes are admitted asymmetrically -- as callees only,
        /// never as callers, and never into <see cref="Methods"/>. See <see cref="AddInheritedMethod"/>
        /// and <see cref="AddEdge"/>.
        /// </summary>
        internal sealed class BehaviourMethodGraph
        {
            private readonly Dictionary<MethodDeclarationSyntax, HashSet<MethodDeclarationSyntax>> _callees =
                new Dictionary<MethodDeclarationSyntax, HashSet<MethodDeclarationSyntax>>();

            private readonly Dictionary<MethodDeclarationSyntax, HashSet<MethodDeclarationSyntax>> _callers =
                new Dictionary<MethodDeclarationSyntax, HashSet<MethodDeclarationSyntax>>();

            /// <summary>
            /// Nodes that came from a user-written base class rather than from this behaviour's own
            /// declarations. Membership here is what makes a node reachable but never reportable.
            /// </summary>
            private readonly HashSet<MethodDeclarationSyntax> _inherited =
                new HashSet<MethodDeclarationSyntax>();

            /// <summary>
            /// The behaviour's <em>own</em> method declarations, and only those.
            ///
            /// Every consumer reads this as the set of places it is allowed to report from: CE0014 and
            /// CE0015 scan it for synced writes, CE0017 scans it for per-frame entry points and builds its
            /// per-frame path by filtering it. Inherited methods are deliberately excluded, because
            /// analyzers run once per *root* type -- UdonSharpCompilerV1 skips abstract and generic classes
            /// when it collects them -- so one base method admitted here becomes one report per concrete
            /// subclass, every one of them landing on the same line of a file the subclass does not own.
            /// That manufactures reports rather than removing false ones, which is the opposite of what
            /// pulling the base chain in is for. Inherited methods still take part in the callee walks
            /// through <see cref="Callees"/>, which is the one direction they are wanted in.
            ///
            /// The cost is real and is accepted deliberately: a bug that lives only in an abstract base and
            /// is never reached from a subclass goes unreported, because nothing ever analyzes that base on
            /// its own. Trading a silent miss there for N duplicate reports here is the wrong trade -- a
            /// report on every subclass of a shared base is exactly the noise that teaches an author to
            /// skim past these codes.
            /// </summary>
            public List<MethodDeclarationSyntax> Methods { get; } = new List<MethodDeclarationSyntax>();

            internal void AddMethod(MethodDeclarationSyntax method)
            {
                if (AddNode(method))
                    Methods.Add(method);
            }

            /// <summary>
            /// Adds a base-declared method as a node that can be reached but never reported on.
            ///
            /// The shape this exists for is the ownership helper on a shared entity base --
            /// 'protected void EnsureOwnership()' in NetEntity, called from 'Interact()' in the Door that
            /// derives from it. With the base method absent from the graph, GuardsOwnPath walked a callee
            /// set of exactly {Interact}, found no ownership call, and CE0014 reported a write that was
            /// correctly gated. CollectOwnershipDerivedNames could not rescue it either: the helper is
            /// void, and ReturnsOwnership only recognises a member whose returned <em>value</em> is an
            /// ownership answer. Both this file and SyncedFieldOwnershipAnalyzer advertise that shape as
            /// counting, so the check was contradicting its own documented contract on correct code.
            /// </summary>
            internal void AddInheritedMethod(MethodDeclarationSyntax method)
            {
                if (AddNode(method))
                    _inherited.Add(method);
            }

            /// <summary>
            /// Registers the node's edge slots, or returns false when it is already registered. First
            /// registration wins, which is what makes an own declaration stay own if it were somehow
            /// offered twice.
            /// </summary>
            private bool AddNode(MethodDeclarationSyntax method)
            {
                if (method == null || _callees.ContainsKey(method))
                    return false;

                _callees[method] = new HashSet<MethodDeclarationSyntax>();
                _callers[method] = new HashSet<MethodDeclarationSyntax>();
                return true;
            }

            internal void AddEdge(MethodDeclarationSyntax caller, MethodDeclarationSyntax callee)
            {
                if (caller == null || callee == null)
                    return;

                if (!_callees.ContainsKey(caller) || !_callers.ContainsKey(callee))
                    return;

                // An inherited method is never recorded as a caller of one of this behaviour's own methods,
                // and that asymmetry is the whole reason admitting the base chain can only ever suppress.
                // Edges are matched on the invoked *name*, so a base method calling its own virtual
                // 'ApplyState()' also matches the subclass override that shares the name. Every consumer of
                // the caller direction asks "is every way into this method gated" -- EveryCallerGuards
                // fails the moment one entry point is not -- so handing an override a new ungated caller
                // out of the base would turn writes CE0014 accepts today back into reports. The callee
                // direction carries no such risk: more callees only ever means more bodies to find a guard
                // in. Skipping the edge in both maps also keeps Callees(ownMethod) from ever reaching back
                // into own methods through a base node, which is what leaves EveryCallerGuards' cycle
                // detection reading exactly the graph it read before.
                //
                // This guard cannot cover the mirror-image hazard, an own method reaching the *shadowed*
                // base version of a method it overrides: there the caller is own and the callee inherited,
                // which is the direction that has to stay open for the EnsureOwnership() shape to work at
                // all. That one is handled before the edge exists, by BuildMethodGraph refusing to admit a
                // base declaration whose signature a nearer type has claimed.
                if (_inherited.Contains(caller) && !_inherited.Contains(callee))
                    return;

                _callees[caller].Add(callee);
                _callers[callee].Add(caller);
            }

            /// <summary>
            /// The method plus everything it can transitively call inside this behaviour or its bases.
            /// </summary>
            public HashSet<MethodDeclarationSyntax> Callees(MethodDeclarationSyntax root)
            {
                return Walk(root, _callees);
            }

            /// <summary>The method plus everything inside this behaviour that can transitively call it.</summary>
            public HashSet<MethodDeclarationSyntax> Callers(MethodDeclarationSyntax root)
            {
                return Walk(root, _callers);
            }

            /// <summary>
            /// Only the methods that call this one directly.
            ///
            /// Asking "is every way into this method gated" needs the immediate call sites, not the
            /// transitive closure: with the closure, one guarded caller somewhere up the chain vouches
            /// for every other way in, including the ungated ones.
            /// </summary>
            public HashSet<MethodDeclarationSyntax> DirectCallers(MethodDeclarationSyntax method)
            {
                if (method != null && _callers.TryGetValue(method, out HashSet<MethodDeclarationSyntax> callers))
                    return new HashSet<MethodDeclarationSyntax>(callers);

                return new HashSet<MethodDeclarationSyntax>();
            }

            private static HashSet<MethodDeclarationSyntax> Walk(
                MethodDeclarationSyntax root,
                Dictionary<MethodDeclarationSyntax, HashSet<MethodDeclarationSyntax>> edges)
            {
                var visited = new HashSet<MethodDeclarationSyntax>();

                if (root == null)
                    return visited;

                var pending = new Stack<MethodDeclarationSyntax>();
                pending.Push(root);

                while (pending.Count > 0)
                {
                    MethodDeclarationSyntax current = pending.Pop();

                    if (!visited.Add(current))
                        continue;

                    if (!edges.TryGetValue(current, out HashSet<MethodDeclarationSyntax> next))
                        continue;

                    foreach (MethodDeclarationSyntax neighbour in next)
                        pending.Push(neighbour);
                }

                return visited;
            }
        }

        /// <summary>
        /// Every class declaration that makes up this behaviour. Partial behaviours are legal in U#, so a
        /// scan that only looked at the first declaration would miss half the methods.
        /// </summary>
        internal static List<ClassDeclarationSyntax> GetClassDeclarations(TypeSymbol type)
        {
            var declarations = new List<ClassDeclarationSyntax>();

            var syntaxRefs = type?.RoslynSymbol?.DeclaringSyntaxReferences;
            if (syntaxRefs == null)
                return declarations;

            foreach (SyntaxReference syntaxRef in syntaxRefs.Value)
            {
                if (syntaxRef.GetSyntax() is ClassDeclarationSyntax classDeclaration)
                    declarations.Add(classDeclaration);
            }

            return declarations;
        }

        /// <summary>
        /// Every class declaration that makes up this behaviour <em>and</em> each of its user-written
        /// bases, nearest first, stopping before UdonSharpBehaviour.
        ///
        /// The stop is written as a break *before* the declarations are added, and that ordering is the
        /// point rather than a style choice. UdonSharpBehaviour declares OnPreSerialization,
        /// RequestSerialization, OnDeserialization and the rest of the callback surface, so any helper
        /// built on this list would read those declarations as user code and answer yes for every
        /// behaviour in the project. Today that cannot happen for an unrelated reason --
        /// UdonSharp.Runtime.asmdef has no UdonSharpAssemblyDefinition asset, so UdonSharpBehaviour binds
        /// from metadata and <see cref="GetClassDeclarations"/> returns nothing for it -- which means
        /// adding that asset would silently disarm those helpers with no error and no failing test.
        /// Excluding it here makes the protection structural instead of incidental. Intermediate bases are
        /// real user types and stay in, which is the entire reason to walk the chain.
        ///
        /// Passing <c>type.BaseType</c> instead of <c>type</c> gives the inherited-only view, with the same
        /// stop applied, which is how a caller splits own declarations from base ones without ever
        /// comparing two syntax nodes for identity. <see cref="BuildMethodGraph"/> needs the chain kept in
        /// tiers rather than flattened and takes <see cref="GetDeclarationTiers"/> for that reason; every
        /// other caller wants the flat list.
        ///
        /// The order is nearest first, so a caller that stops at its first hit -- as
        /// <see cref="FindFirstRequestSerializationIncludingBases"/> does -- reports the behaviour's own
        /// answer whenever it has one.
        ///
        /// A base bound from metadata contributes nothing rather than throwing: TypeSymbol leaves BaseType
        /// null on extern types, and <see cref="GetClassDeclarations"/> returns an empty list for a symbol
        /// with no DeclaringSyntaxReferences.
        /// </summary>
        internal static List<ClassDeclarationSyntax> GetClassDeclarationsWithBases(TypeSymbol type)
        {
            var declarations = new List<ClassDeclarationSyntax>();

            foreach (List<ClassDeclarationSyntax> tier in GetDeclarationTiers(type))
                declarations.AddRange(tier);

            return declarations;
        }

        /// <summary>
        /// The same walk as <see cref="GetClassDeclarationsWithBases"/>, kept split by type: one entry per
        /// type in the chain, nearest first, each holding that one type's own (possibly partial) class
        /// declarations. Types that contribute no syntax -- a base bound from metadata -- contribute no
        /// entry.
        ///
        /// The tier boundaries are load bearing, not presentation. <see cref="BuildMethodGraph"/> has to
        /// decide whether a base declaration is shadowed by a nearer one, and "nearer" stops existing the
        /// moment the chain is flattened. Grouping also keeps two overloads declared side by side in the
        /// <em>same</em> type from being read as one shadowing the other, which is a distinction a flat
        /// list cannot express and which matters because the shadow key is deliberately coarse. See
        /// <see cref="GetShadowSignature"/>.
        /// </summary>
        private static List<List<ClassDeclarationSyntax>> GetDeclarationTiers(TypeSymbol type)
        {
            var tiers = new List<List<ClassDeclarationSyntax>>();
            int depth = 0;

            // Bounded rather than tracked with a visited set. A base chain the C# compiler accepted cannot
            // cycle, but this walk now runs from BuildMethodGraph for every behaviour on every compile, and
            // a chain rebuilt from error symbols carries no such promise. A HashSet<TypeSymbol> would be
            // the obvious cycle guard and is the wrong tool here: Symbol.GetHashCode dereferences
            // RoslynSymbol unconditionally, which is the exact throw the null conditional below exists to
            // avoid. A counter cannot throw, and a hang is worse than a throw anyway -- it takes the whole
            // compile with it and leaves no diagnostic explaining why. A chain that does loop back on
            // itself is harmless downstream as well: the repeated type's signatures are already claimed by
            // the earlier pass over it, so its declarations are dropped as shadowed rather than doubled.
            for (TypeSymbol current = type;
                 current != null && depth < MaxBaseChainDepth;
                 current = current.BaseType, depth++)
            {
                // Reads RoslynSymbol directly because Symbol.Name dereferences it unconditionally, and an
                // analyzer that throws stops checking every behaviour after it.
                if (string.Equals(current.RoslynSymbol?.Name, UdonSharpBehaviourTypeName, StringComparison.Ordinal))
                    break;

                List<ClassDeclarationSyntax> declarations = GetClassDeclarations(current);

                if (declarations.Count > 0)
                    tiers.Add(declarations);
            }

            return tiers;
        }

        /// <summary>
        /// Ceiling on the base walk above. Far past any real behaviour hierarchy, so hitting it means the
        /// chain is malformed and stopping early is the only useful answer.
        /// </summary>
        private const int MaxBaseChainDepth = 64;

        private static readonly object ModelCacheLock = new object();
        private static readonly Dictionary<SyntaxTree, SemanticModel> ModelCache = new Dictionary<SyntaxTree, SemanticModel>();
        private static CSharpCompilation _modelCacheCompilation;

        /// <summary>
        /// Gets the semantic model for a node's tree, or null when the tree is not part of the compilation.
        /// Callers must degrade to syntax-only matching rather than skipping the check entirely.
        ///
        /// Models are cached per compilation because Roslyn rebinds the whole tree on every
        /// <c>GetSemanticModel</c> call, and these analyzers ask for the same handful of trees once per
        /// method. The cache is dropped as soon as a different compilation asks, so it cannot serve a model
        /// built from stale, pre-generator syntax.
        /// </summary>
        internal static SemanticModel GetSemanticModel(CompilationContext compilationContext, SyntaxNode node)
        {
            SyntaxTree tree = node?.SyntaxTree;
            CSharpCompilation compilation = compilationContext?.RoslynCompilation;

            if (tree == null || compilation == null)
                return null;

            lock (ModelCacheLock)
            {
                if (!ReferenceEquals(_modelCacheCompilation, compilation))
                {
                    ModelCache.Clear();
                    _modelCacheCompilation = compilation;
                }

                if (ModelCache.TryGetValue(tree, out SemanticModel cached))
                    return cached;

                SemanticModel model;

                try
                {
                    model = compilationContext.GetSemanticModel(tree);
                }
                catch (ArgumentException)
                {
                    // Roslyn throws this when the tree is not in the compilation, which can happen for a
                    // tree a generator replaced. There is nothing to recover, and no model just means we
                    // fall back to name matching. Cached as null so the throw does not repeat per method.
                    model = null;
                }

                ModelCache[tree] = model;
                return model;
            }
        }

        /// <summary>
        /// The declared (or generator-inferred) sync mode, or null when neither the behaviour nor any of
        /// its bases carries the attribute.
        ///
        /// The base walk is not optional. UdonSharpCompilerV1 climbs the base chain to UdonSharpBehaviour
        /// when it sets <c>programAsset.behaviourSyncMode</c>, so a derived behaviour really is built with
        /// the mode its base declares. Reading only this type's own attribute made every check built on
        /// this helper (CE0015-CE0018) disagree with the program the compiler emits, and disagree by
        /// reporting nothing -- the silent miss these analyzers exist to remove.
        /// </summary>
        internal static BehaviourSyncMode? GetDeclaredSyncMode(TypeSymbol type)
        {
            for (TypeSymbol current = type; current != null; current = current.BaseType)
            {
                BehaviourSyncMode? declared = current.GetAttribute<UdonBehaviourSyncModeAttribute>()?.behaviourSyncMode;

                if (declared != null)
                    return declared;

                // UdonSharpBehaviour carries no mode itself, and it binds as an extern type whose BaseType
                // is null, so the walk already stops there. The name check makes that termination explicit
                // rather than dependent on how the base type happened to be bound. It reads RoslynSymbol
                // directly because Symbol.Name dereferences it unconditionally, and an analyzer that throws
                // stops checking every behaviour after it.
                if (string.Equals(current.RoslynSymbol?.Name, UdonSharpBehaviourTypeName, StringComparison.Ordinal))
                    break;
            }

            return null;
        }

        /// <summary>
        /// Synced fields keyed by name. <c>[Sync]</c> derives from <c>UdonSyncedAttribute</c>, so
        /// <see cref="FieldSymbol.IsSynced"/> already covers both spellings.
        /// </summary>
        internal static Dictionary<string, FieldSymbol> GetSyncedFields(TypeSymbol type)
        {
            var syncedFields = new Dictionary<string, FieldSymbol>(StringComparer.Ordinal);

            if (type == null || type.FieldSymbols.IsDefaultOrEmpty)
                return syncedFields;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                if (field == null || !field.IsSynced || string.IsNullOrEmpty(field.Name))
                    continue;

                syncedFields[field.Name] = field;
            }

            return syncedFields;
        }

        /// <summary>
        /// Whether a field carries CE's <c>[Sync]</c> at all, which is a different question from what
        /// authority it declares and must not be answered by the same value.
        ///
        /// Collapsing the two is how CE0014's fix text started giving advice that contradicted the code it
        /// was pointing at: a single nullable Authority says null both for "there is no CE attribute here"
        /// and for "there is one and its argument would not decode", and the second of those was being
        /// told to "say so at the declaration with [Sync(Authority.Predicted)] instead" on a field whose
        /// declaration already said <c>[Sync(...)]</c>. Routing the *check* on a value that cannot be read
        /// is safe -- strict is the direction that reports rather than hides -- but routing the
        /// *suggestion* on it is not, because a suggestion is a claim about what the author wrote.
        /// </summary>
        internal enum SyncAuthorityDeclaration
        {
            /// <summary>
            /// No CE <c>[Sync]</c> on the field: bare <c>[UdonSynced]</c>, or not synced at all. This is
            /// the only state where offering <c>[Sync(Authority.Predicted)]</c> as a resolution is true.
            /// </summary>
            Absent,

            /// <summary>CE <c>[Sync]</c> is present and its authority argument was decoded.</summary>
            Declared,

            /// <summary>
            /// CE <c>[Sync]</c> is present but its authority could not be read off the use site -- an
            /// attribute Roslyn only half bound, or a subclass that fixes authority in its own constructor
            /// (<c>class PredictedSync : SyncAttribute { public PredictedSync() : base(Authority.Predicted) {} }</c>).
            /// The field is checked as strictly as an undeclared one, but it is not undeclared, so nothing
            /// may tell its author to add the attribute they already added.
            /// </summary>
            Unreadable
        }

        /// <summary>
        /// Reads CE's <c>[Sync(Authority...)]</c> off a field, separating "no CE attribute" from "CE
        /// attribute whose authority would not decode". <paramref name="authority"/> is meaningful only
        /// for <see cref="SyncAuthorityDeclaration.Declared"/> and is
        /// <see cref="Authority.Owner"/> otherwise, matching the enum's own choice of the strict reading
        /// as its zero value.
        ///
        /// The two tiers cannot be told apart by asking whether the field is synced, and they cannot be
        /// told apart by the attribute's spelling either. <c>[Sync]</c> derives from
        /// <c>UdonSyncedAttribute</c>, so <see cref="GetSyncedFields"/> already answers yes for both, and
        /// matching the short name "Sync" would accept any unrelated attribute that happens to share it.
        /// The attribute type is therefore resolved through the Roslyn symbol and up its base chain. That
        /// base walk decides <em>recognition</em> only: the authority itself is read from the arguments at
        /// the use site, so a subclass that supplies it through <c>: base(Authority.Predicted)</c> is
        /// recognised as a CE sync attribute and reported Unreadable rather than Predicted. Following the
        /// subclass constructor is deliberately not attempted -- it would only ever be a syntax read, only
        /// ever work for a subclass declared in source, and the direction it moves a field is out of
        /// CE0014, which is the direction that hides a silent desync.
        ///
        /// Read here rather than through <c>Symbol.GetAttribute&lt;SyncAttribute&gt;()</c> because that
        /// route reflectively constructs a real SyncAttribute against the runtime assembly the editor has
        /// loaded. A stale DLL still exposing the pre-Authority constructor -- what a mid-migration domain
        /// reload looks like -- makes the construction throw, and a throw there drops the attribute
        /// entirely, which reads as "this field is not synced" rather than as an error.
        /// </summary>
        internal static SyncAuthorityDeclaration GetAuthorityDeclaration(FieldSymbol field, out Authority authority)
        {
            authority = Authority.Owner;

            IFieldSymbol roslynField = field?.RoslynSymbol;
            if (roslynField == null)
                return SyncAuthorityDeclaration.Absent;

            foreach (AttributeData attribute in roslynField.GetAttributes())
            {
                if (!IsCeSyncAttribute(attribute.AttributeClass))
                    continue;

                // Authority is [Sync]'s required positional parameter, so it is always the first
                // constructor argument, and Roslyn hands enum arguments over as their underlying value.
                // Roslyn also fills optional parameters in, so the length check is about a malformed or
                // half-bound attribute rather than about the one-argument spelling.
                if (attribute.ConstructorArguments.Length > 0 &&
                    attribute.ConstructorArguments[0].Value is int declared)
                {
                    authority = (Authority)declared;
                    return SyncAuthorityDeclaration.Declared;
                }

                return SyncAuthorityDeclaration.Unreadable;
            }

            return SyncAuthorityDeclaration.Absent;
        }

        /// <summary>
        /// The authority a field declares, or null when none could be read.
        ///
        /// This is the tier question and nothing else: null means Absent <em>or</em> Unreadable, and both
        /// route the field to the strict CE0014 check and out of CE0063, which is the reporting direction
        /// either way. Anything that needs to know which of the two it is -- any wording that describes
        /// what the author wrote -- must call <see cref="GetAuthorityDeclaration"/> instead, because this
        /// answer cannot tell them apart and will not start being able to.
        /// </summary>
        internal static Authority? GetDeclaredAuthority(FieldSymbol field)
        {
            return GetAuthorityDeclaration(field, out Authority authority) == SyncAuthorityDeclaration.Declared
                ? authority
                : (Authority?)null;
        }

        /// <summary>
        /// Whether the attribute is CE's <c>[Sync]</c> or anything deriving from it, matched on the full
        /// type name because the short name is not distinctive enough to bet a suppressed check on.
        /// </summary>
        private static bool IsCeSyncAttribute(INamedTypeSymbol attributeClass)
        {
            for (INamedTypeSymbol current = attributeClass; current != null; current = current.BaseType)
            {
                if (string.Equals(current.ToDisplayString(), CeSyncAttributeTypeName, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Builds the intra-type call graph over the behaviour's own method declarations, plus the
        /// declarations of its user-written bases as reachable-but-unreportable nodes.
        /// Nested types are excluded so their methods cannot be mistaken for behaviour methods.
        ///
        /// The base half exists for the ownership helper that lives one type up:
        /// 'protected void EnsureOwnership()' on a shared NetEntity base, called from 'Interact()' on the
        /// Door that derives from it. With the base method missing from the graph, Callees(Interact) was
        /// {Interact}, the guard was invisible to GuardsOwnPath, and CE0014 reported a correctly gated
        /// write -- on the shape this package documents as the way to do it.
        ///
        /// Base declarations enter as inherited nodes rather than into
        /// <see cref="BehaviourMethodGraph.Methods"/>, and that is what keeps the widening
        /// one-directional. Methods is every consumer's report-site list, so leaving it alone leaves the
        /// writes CE0014/CE0015 find and the per-frame path CE0017 walks identical to what they were;
        /// <see cref="BehaviourMethodGraph.AddEdge"/> supplies the other half by refusing to record an
        /// inherited method as a caller of an own one, which leaves the caller direction -- Callers,
        /// DirectCallers, and so EveryCallerGuards and IsReachedFromExemptEntryPoint -- untouched as well.
        /// What is left is that GuardsOwnPath has more callee bodies to find a guard in, which can only
        /// silence a report.
        ///
        /// Shadowed base declarations are dropped, and that is not an optimisation -- it is what keeps the
        /// widening from opening a hole. Edges are matched on the invoked name, so an override and the base
        /// declaration it replaces both answer to the same call. Admitting both let
        /// 'override void ApplyState() { _score++; }' be vouched for by the 'if (!Networking.IsOwner(...))
        /// return;' standing in the base body it replaced -- code that never runs for this type -- and
        /// CE0014 went silent on a genuinely unguarded write. A missed CE0014 is a permanently invisible
        /// desync, which is strictly worse than the false positive the base chain was admitted to fix, so
        /// a base declaration survives only while no nearer type in the chain claims its signature.
        ///
        /// What is deliberately <em>not</em> resolved is the explicit 'base.ApplyState()' call: the base
        /// body is gone from the graph, so its guard is invisible even to the override that really does run
        /// it. That is the pre-widening answer restored, and on the shape that matters it is also the
        /// correct one -- an 'if (!IsOwner) return;' inside base.ApplyState() returns from the base method
        /// and gates nothing that follows the call in the override.
        /// </summary>
        internal static BehaviourMethodGraph BuildMethodGraph(TypeSymbol type)
        {
            var graph = new BehaviourMethodGraph();
            var byName = new Dictionary<string, List<MethodDeclarationSyntax>>(StringComparer.Ordinal);

            // Every node, own declarations first. Kept local instead of exposed on the graph so nothing
            // outside this method can mistake it for the report-site list that Methods is.
            var allMethods = new List<MethodDeclarationSyntax>();

            // Signatures a nearer declaration has already claimed. Seeded by the behaviour's own tier and
            // extended one base at a time, so a base declaration is admitted only when nothing between it
            // and the behaviour redeclares it. Walking tier by tier rather than over one flat list is what
            // makes "nearer" a question that can be asked at all.
            var claimedSignatures = new HashSet<string>(StringComparer.Ordinal);

            // Starting the base walk at BaseType rather than at type is what makes the two tiers disjoint
            // without comparing syntax nodes for identity. SyntaxReference.GetSyntax() is not contractually
            // required to hand back the same red node on a second call, and a misclassified own declaration
            // would drop out of Methods -- silently switching CE0014, CE0015 and CE0017 off for the whole
            // behaviour, which is a worse failure than the false positive this is fixing.
            CollectGraphMethods(GetClassDeclarations(type), graph, byName, allMethods, claimedSignatures, inherited: false);

            foreach (List<ClassDeclarationSyntax> tier in GetDeclarationTiers(type?.BaseType))
                CollectGraphMethods(tier, graph, byName, allMethods, claimedSignatures, inherited: true);

            foreach (MethodDeclarationSyntax method in allMethods)
            {
                foreach (InvocationExpressionSyntax invocation in method.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (!IsSelfInvocation(invocation))
                        continue;

                    string invokedName = GetInvokedName(invocation);
                    if (invokedName == null || !byName.TryGetValue(invokedName, out List<MethodDeclarationSyntax> targets))
                        continue;

                    // Overloads all get an edge. Picking the wrong one would only ever make the graph
                    // smaller, and a smaller graph means missed guards means false positives.
                    foreach (MethodDeclarationSyntax target in targets)
                        graph.AddEdge(method, target);
                }
            }

            return graph;
        }

        /// <summary>
        /// Adds one tier -- one type's declarations -- to the graph as nodes and to the name index as call
        /// targets, skipping inherited declarations whose signature a nearer tier already claimed.
        ///
        /// Own declarations must be collected before inherited ones. The two tiers are disjoint by
        /// construction, but if a malformed base chain ever offered the same declaration twice, whichever
        /// arrives first decides which tier it lands in, and the behaviour's own declaration is the one
        /// that has to stay reportable.
        ///
        /// The shadow test never applies to the behaviour's own tier. Own declarations are the report sites
        /// every consumer reads out of <see cref="BehaviourMethodGraph.Methods"/>, so dropping one there
        /// would not narrow a guard walk, it would switch CE0014, CE0015 and CE0017 off for whatever it
        /// dropped -- and the coarse signature key would drop exactly the sibling overloads a type is
        /// entitled to declare.
        /// </summary>
        private static void CollectGraphMethods(
            List<ClassDeclarationSyntax> declarations,
            BehaviourMethodGraph graph,
            Dictionary<string, List<MethodDeclarationSyntax>> byName,
            List<MethodDeclarationSyntax> allMethods,
            HashSet<string> claimedSignatures,
            bool inherited)
        {
            // Collected across the whole tier and applied only at the end, because two declarations in the
            // same type are siblings rather than shadows: 'Fire(int)' and 'Fire(string)' share this key and
            // both have to stay in the graph. Claiming as we went would let the first silently delete the
            // second.
            var tierSignatures = new List<string>();

            foreach (ClassDeclarationSyntax classDeclaration in declarations)
            {
                foreach (MemberDeclarationSyntax member in classDeclaration.Members)
                {
                    if (!(member is MethodDeclarationSyntax method))
                        continue;

                    string signature = GetShadowSignature(method);

                    // A nearer type in the chain declares this signature, so this body is unreachable for
                    // the type being analyzed -- an override replaced it, or a 'new' member hid it. Leaving
                    // it in would hand every call that matches the name a second body to find a guard in,
                    // and that body does not run.
                    if (inherited && claimedSignatures.Contains(signature))
                        continue;

                    if (inherited)
                        graph.AddInheritedMethod(method);
                    else
                        graph.AddMethod(method);

                    allMethods.Add(method);
                    tierSignatures.Add(signature);

                    string name = method.Identifier.Text;
                    if (!byName.TryGetValue(name, out List<MethodDeclarationSyntax> overloads))
                    {
                        overloads = new List<MethodDeclarationSyntax>();
                        byName[name] = overloads;
                    }

                    overloads.Add(method);
                }
            }

            claimedSignatures.UnionWith(tierSignatures);
        }

        /// <summary>
        /// The key a declaration is shadow-matched on: the name the call graph keys its edges on, plus the
        /// declared parameter count.
        ///
        /// Parameter <em>types</em> are deliberately not read. Deciding them from syntax means resolving
        /// aliases, using directives and type parameters, and a wrong answer in the permissive direction is
        /// the exact failure this test exists to prevent. Arity is enough for the case that matters -- an
        /// override's signature is identical to the base's, so its parameter count always matches -- and it
        /// still keeps a base 'Fire(int, int)' alive alongside a subclass 'Fire(string)', which really are
        /// two methods that both run for this type.
        ///
        /// Where arity alone cannot decide -- base 'Fire(int)', subclass 'Fire(string)' -- the base
        /// declaration is dropped, and that direction is chosen on purpose. Dropping costs at most one
        /// callee body the guard walk cannot see, which is the graph that existed before the base chain was
        /// admitted at all and at worst restores a report that was already being made. Keeping costs a body
        /// that does not run for this type vouching for one that does, and the report it silences is a
        /// synced write VRChat discards without a word.
        /// </summary>
        private static string GetShadowSignature(MethodDeclarationSyntax method)
        {
            int parameterCount = method.ParameterList?.Parameters.Count ?? 0;

            return method.Identifier.Text + "/" + parameterCount;
        }

        /// <summary>
        /// Finds every write to one of this behaviour's own synced fields inside the given methods.
        /// Array element writes count -- <c>scores[i] = x</c> is just as lost as <c>score = x</c>.
        /// </summary>
        internal static List<SyncedFieldWrite> FindSyncedFieldWrites(
            TypeSymbol type,
            IEnumerable<MethodDeclarationSyntax> methods,
            CompilationContext compilationContext)
        {
            var writes = new List<SyncedFieldWrite>();

            Dictionary<string, FieldSymbol> syncedFields = GetSyncedFields(type);
            if (syncedFields.Count == 0 || methods == null)
                return writes;

            foreach (MethodDeclarationSyntax method in methods)
            {
                if (method == null)
                    continue;

                SemanticModel model = GetSemanticModel(compilationContext, method);

                foreach (SyntaxNode node in method.DescendantNodes())
                {
                    ExpressionSyntax target = GetWriteTarget(node);
                    if (target == null)
                        continue;

                    FieldSymbol field = ResolveOwnSyncedField(target, type, syncedFields, model);
                    if (field == null)
                        continue;

                    writes.Add(new SyncedFieldWrite(field, node, method));
                }
            }

            return writes;
        }

        /// <summary>
        /// Whether an ownership check appears anywhere in the given subtree.
        /// </summary>
        internal static bool ContainsOwnershipGuard(SyntaxNode node, SemanticModel model)
        {
            return ContainsOwnershipGuard(node, model, null);
        }

        /// <summary>
        /// Whether an ownership check appears anywhere in the given subtree, counting the behaviour's own
        /// ownership-derived members as checks.
        ///
        /// Pass the set from <see cref="CollectOwnershipDerivedNames"/> to recognise the cached-flag shape
        /// ('if (!_isOwner) return;'). Pass null -- or use the two-argument overload -- to ask only about
        /// literal ownership calls, which is what keeps the derivation one level deep.
        /// </summary>
        internal static bool ContainsOwnershipGuard(SyntaxNode node, SemanticModel model, HashSet<string> ownershipDerivedNames)
        {
            if (node == null)
                return false;

            // DescendantNodesAndSelf, because callers hand this bare expressions ('_isOwner',
            // 'Networking.IsOwner(...)') as well as whole method bodies, and a bare identifier is not a
            // descendant of itself.
            foreach (SimpleNameSyntax name in node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            {
                string identifier = name.Identifier.Text;

                if (ownershipDerivedNames != null && ownershipDerivedNames.Contains(identifier))
                    return true;

                if (!OwnershipMemberNames.Contains(identifier))
                    continue;

                // The guard RpcEmitter injects into an OwnerOnly [Rpc] tests the *sender*:
                // 'if (__ce_rpcCaller != null && !Networking.IsOwner(__ce_rpcCaller, gameObject)) return;'.
                // OwnerOnly leaves Target at RpcTarget.All, so the body still runs on every receiver, and
                // on a receiver that does not own the object a synced write is discarded exactly as it
                // would be with no guard at all. Counting the generated call reopened that hole for every
                // OwnerOnly RPC -- the one place authors are most confident they are safe.
                if (IsGeneratedRpcCallerCheck(name))
                    continue;

                ISymbol symbol = model?.GetSymbolInfo(name).Symbol;

                // With no model the name is all we have, and treating it as a guard only ever suppresses a
                // report. Under-reporting a guard is what produces noise on correct code.
                if (symbol == null)
                    return true;

                if (IsDeclaredOn(symbol, NetworkingTypeName) || IsDeclaredOn(symbol, PlayerApiTypeName))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether this ownership call is the generated OwnerOnly [Rpc] guard, which answers "was the
        /// caller the owner" rather than "am I the owner".
        /// </summary>
        private static bool IsGeneratedRpcCallerCheck(SimpleNameSyntax name)
        {
            SyntaxNode current = name;

            // Climb past the receiver chain of 'global::VRC.SDKBase.Networking.IsOwner' to the call itself.
            while (current is SimpleNameSyntax || current is MemberAccessExpressionSyntax)
                current = current.Parent;

            if (!(current is InvocationExpressionSyntax invocation) || invocation.ArgumentList == null)
                return false;

            foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
            {
                if (argument.Expression is IdentifierNameSyntax identifier &&
                    string.Equals(identifier.Identifier.Text, RpcCallerLocalName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Names in this behaviour whose value answers "do I own this object".
        ///
        /// The scan above only ever recognised a literal Networking.IsOwner call, so the shape authors
        /// actually write -- a flag refreshed once from Networking.IsOwner and then tested
        /// ('if (!_isOwner) return;'), or a 'bool IsAuthority => Networking.IsOwner(gameObject)' property
        /// -- read as no guard at all. Reporting correct code is not a harmless extra: it is what teaches
        /// an author to skim past CE0014, and the write it exists to catch fails silently.
        ///
        /// One level of indirection is followed, and only one. A property or method qualifies when every
        /// value it returns comes from an ownership call; a field qualifies when its initializer or an
        /// assignment to it reaches an ownership call or one of those members. A flag copied from another
        /// flag is not tracked. Cheap and predictable beats clever here, and the cost of stopping a level
        /// short is one report the author can silence by checking ownership where the write happens.
        ///
        /// Locals need no entry. A local ownership flag is declared in the same body as the write that
        /// uses it, so the direct scan already sees the Networking.IsOwner call that produced it, and
        /// keeping locals out of a type-wide set stops a 'var owner = ...' in one method from vouching
        /// for an unrelated identifier in another.
        ///
        /// The base chain is included because the ownership helper and the write that uses it are
        /// routinely declared in different types. Both halves of that already exist here: NetPhysicsWorld
        /// writes the helper ('public bool IsAuthority => Networking.IsOwner(gameObject)'), and
        /// NetPhysicsEntity/NetVehicle is the base-plus-subclass layering the NetPhysics library is built
        /// on. Lift one into the other -- the obvious refactor, and what any user entity base looks like
        /// -- and a scan of the derived type's own declarations sees no guard at all, so every write
        /// behind it is reported as unguarded. That is a false positive on the shape this package
        /// documents as correct, which is the same disease CE0014's five reports on NetVehicle were.
        /// </summary>
        internal static HashSet<string> CollectOwnershipDerivedNames(TypeSymbol type, CompilationContext compilationContext)
        {
            List<ClassDeclarationSyntax> declarations = GetClassDeclarationsWithBases(type);

            var fieldNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (ClassDeclarationSyntax classDeclaration in declarations)
            {
                foreach (MemberDeclarationSyntax member in classDeclaration.Members)
                {
                    if (!(member is FieldDeclarationSyntax field) || field.Declaration == null)
                        continue;

                    foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                        fieldNames.Add(variable.Identifier.Text);
                }
            }

            // Members that answer the ownership question themselves: 'bool IsAuthority =>
            // Networking.IsOwner(gameObject)' and the method form of the same thing.
            var ownershipValuedMembers = new HashSet<string>(StringComparer.Ordinal);

            foreach (ClassDeclarationSyntax classDeclaration in declarations)
            {
                SemanticModel model = GetSemanticModel(compilationContext, classDeclaration);

                foreach (MemberDeclarationSyntax member in classDeclaration.Members)
                {
                    switch (member)
                    {
                        case PropertyDeclarationSyntax property:
                            if (ReturnsOwnership(GetGetterBody(property), model))
                                ownershipValuedMembers.Add(property.Identifier.Text);

                            break;

                        case MethodDeclarationSyntax method:
                            if (ReturnsOwnership((SyntaxNode)method.ExpressionBody ?? method.Body, model))
                                ownershipValuedMembers.Add(method.Identifier.Text);

                            break;
                    }
                }
            }

            var derived = new HashSet<string>(ownershipValuedMembers, StringComparer.Ordinal);

            foreach (ClassDeclarationSyntax classDeclaration in declarations)
            {
                SemanticModel model = GetSemanticModel(compilationContext, classDeclaration);

                foreach (MemberDeclarationSyntax member in classDeclaration.Members)
                {
                    if (!(member is FieldDeclarationSyntax field) || field.Declaration == null)
                        continue;

                    foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                    {
                        if (variable.Initializer != null &&
                            ContainsOwnershipGuard(variable.Initializer.Value, model, ownershipValuedMembers))
                        {
                            derived.Add(variable.Identifier.Text);
                        }
                    }
                }

                // An ownership flag is normally refreshed somewhere other than where it is declared -- an
                // OnOwnershipTransferred handler, a per-frame poll -- so the assignments matter as much as
                // the declaration. Only assignments to this behaviour's own fields count, so a local named
                // 'owner' in one method cannot vouch for an unrelated identifier in another.
                //
                // The right-hand side is read against the members found above rather than against the set
                // being built, so '_isOwner = IsLocalOwner();' resolves while '_a = _b;' still does not:
                // the depth stays fixed at one member instead of depending on declaration order.
                foreach (AssignmentExpressionSyntax assignment in
                         classDeclaration.DescendantNodes().OfType<AssignmentExpressionSyntax>())
                {
                    string assignedField = GetAssignedFieldName(assignment.Left, fieldNames);

                    if (assignedField != null &&
                        ContainsOwnershipGuard(assignment.Right, model, ownershipValuedMembers))
                    {
                        derived.Add(assignedField);
                    }
                }
            }

            return derived;
        }

        /// <summary>
        /// The body that produces a property's value, or null when it has no readable one.
        /// </summary>
        private static SyntaxNode GetGetterBody(PropertyDeclarationSyntax property)
        {
            if (property.ExpressionBody != null)
                return property.ExpressionBody;

            if (property.AccessorList == null)
                return null;

            foreach (AccessorDeclarationSyntax accessor in property.AccessorList.Accessors)
            {
                if (accessor.IsKind(SyntaxKind.GetAccessorDeclaration))
                    return (SyntaxNode)accessor.ExpressionBody ?? accessor.Body;
            }

            return null;
        }

        /// <summary>
        /// Whether every value this body can return is derived from an ownership call.
        ///
        /// Every return has to qualify. One that does not means the member answers some other question on
        /// at least one path, and a guard written against it would not be an ownership guard there.
        /// Polarity is not read -- 'return !Networking.IsOwner(...)' still counts -- because nothing else
        /// in this file reads polarity either, and a guard shaped like an ownership test is the case this
        /// check is deliberately quiet about.
        ///
        /// A void helper -- 'void EnsureOwnership() { if (!IsOwner) SetOwner(...); }' -- deliberately does
        /// not qualify and must not be made to. This set feeds <c>ContainsOwnershipGuard</c>, where
        /// membership means a bare mention of the name anywhere counts as a guard, so admitting a member
        /// that merely <em>performs</em> an ownership action rather than answering the question would let
        /// any call to it vouch for code it does not gate. The EnsureOwnership() shape is covered instead
        /// by <see cref="BuildMethodGraph"/> walking into the helper's body, which reads the real guard
        /// where it actually stands.
        /// </summary>
        private static bool ReturnsOwnership(SyntaxNode body, SemanticModel model)
        {
            if (body == null)
                return false;

            if (body is ArrowExpressionClauseSyntax arrow)
                return ContainsOwnershipGuard(arrow.Expression, model);

            bool sawReturn = false;

            foreach (ReturnStatementSyntax statement in body.DescendantNodes().OfType<ReturnStatementSyntax>())
            {
                if (statement.Expression == null)
                    return false;

                sawReturn = true;

                if (!ContainsOwnershipGuard(statement.Expression, model))
                    return false;
            }

            return sawReturn;
        }

        /// <summary>
        /// The name of this behaviour's own field being assigned to, or null when the target is anything
        /// else -- a local, a parameter, or a member of some other object.
        /// </summary>
        private static string GetAssignedFieldName(ExpressionSyntax left, HashSet<string> fieldNames)
        {
            string name;

            switch (left)
            {
                case IdentifierNameSyntax identifier:
                    name = identifier.Identifier.Text;
                    break;

                case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression is ThisExpressionSyntax:
                    name = memberAccess.Name.Identifier.Text;
                    break;

                default:
                    return null;
            }

            return fieldNames.Contains(name) ? name : null;
        }

        /// <summary>
        /// Whether a clock is read anywhere in the given subtree.
        ///
        /// This answers "is a clock read here", not "is this rate gated". Callers must decide the second
        /// question themselves by asking this only about guard positions: the previous whole-body scan
        /// counted the <c>Time.deltaTime</c> in '_pos += _speed * Time.deltaTime;' as a rate gate, which
        /// suppressed CE0017 on the exact ungated-Update shape the check exists to catch.
        /// </summary>
        internal static bool ContainsTimeMember(SyntaxNode node, SemanticModel model)
        {
            if (node == null)
                return false;

            foreach (SimpleNameSyntax name in node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            {
                if (!TimeMemberNames.Contains(name.Identifier.Text))
                    continue;

                ISymbol symbol = model?.GetSymbolInfo(name).Symbol;

                // With no model the name is all we have, and treating it as a clock only ever suppresses a
                // report, which is the safe direction for a check tuned against false positives.
                if (symbol == null)
                    return true;

                if (IsDeclaredOn(symbol, UnityTimeTypeName) || IsDeclaredOn(symbol, NetworkingTypeName))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether this invocation is a RequestSerialization() call on <em>this</em> behaviour.
        ///
        /// The receiver check is what makes the answer usable. '_display.RequestSerialization()'
        /// serializes the other behaviour and does nothing for this one, so counting it here let a
        /// Manual-sync behaviour that never serializes itself suppress CE0015 and CE0016 -- the analyzer
        /// would take a call that sends someone else's payload as proof that this one is sent.
        /// </summary>
        internal static bool IsRequestSerializationCall(InvocationExpressionSyntax invocation, SemanticModel model)
        {
            if (!string.Equals(GetInvokedName(invocation), "RequestSerialization", StringComparison.Ordinal))
                return false;

            if (!IsSelfInvocation(invocation))
                return false;

            ISymbol symbol = model?.GetSymbolInfo(invocation).Symbol;

            // No model, or a name we cannot resolve: the name is unambiguous enough in a behaviour.
            return symbol == null || symbol.Kind == SymbolKind.Method;
        }

        /// <summary>
        /// Finds the first RequestSerialization() call the behaviour makes on itself in its <em>own</em>
        /// declarations, or null when it makes none. Manual sync sends nothing without one, so its absence
        /// is load bearing.
        ///
        /// Own-only is the default and the base-aware search is the opt-in
        /// (<see cref="FindFirstRequestSerializationIncludingBases"/>), because the two consumers of this
        /// answer put it to incompatible uses and only one of them can absorb a base-declared call. A gate
        /// -- a caller that stops reporting the moment it finds one -- can only lose reports when the
        /// search widens. A branch selector cannot: widening changes which report it emits, where that
        /// report points, and how many times it is emitted. Defaulting to the narrow answer means a caller
        /// gets base-awareness by asking for it, rather than by being on the wrong end of a shared default
        /// that was widened for somebody else's analyzer.
        ///
        /// CE0016 is that branch selector and is why this shape matters. It fires on
        /// Manual-with-no-synced-fields either way and only chooses between a warning
        /// ('RequestSerialization() serializes an empty payload') at the call site and an info ('never
        /// calls RequestSerialization()') at the type. Handing it a base-declared call would flip the
        /// severity, move the location into a file the analyzed type does not own, repeat that report once
        /// per concrete subclass -- analyzers run per concrete root type, abstract ones being skipped at
        /// UdonSharpCompilerV1.cs:616 -- and, worst of the four, assert an empty payload on precisely the
        /// shape where the payload is full: a base that carries both the send and the synced fields, which
        /// <see cref="GetSyncedFields"/> cannot see but the emitter does collect (EmitContext.cs:149-166).
        /// </summary>
        internal static InvocationExpressionSyntax FindFirstRequestSerialization(
            TypeSymbol type,
            CompilationContext compilationContext)
        {
            return FindFirstRequestSerializationIn(GetClassDeclarations(type), compilationContext);
        }

        /// <summary>
        /// The same search widened to the behaviour's user-written bases, nearest first, for a caller that
        /// uses the answer as a gate rather than as a branch. See
        /// <see cref="FindFirstRequestSerialization"/> for why that distinction decides which entry point
        /// is correct, and do not reach for this one to "improve" a caller that branches.
        ///
        /// CE0015 is the caller this exists for. It pairs the answer with <see cref="GetDeclaredSyncMode"/>,
        /// which already walks bases, so a Manual base declaring
        /// 'protected void Publish() { RequestSerialization(); }' gave every subclass a sync mode read
        /// through the base chain and a send that an own-declarations-only search could not see. CE0015
        /// then told each of them "this behaviour never calls RequestSerialization()" about a behaviour
        /// that inherits exactly that call. Because CE0015 returns with no diagnostics as soon as a call is
        /// found, the widened answer can only remove that report; it can never move one or add one.
        ///
        /// Nothing else was covering for it, either. CE0063 does not defer the Manual case to CE0015 or to
        /// anything else -- it returns at its own mode gate (PredictedFieldReconciliationAnalyzer.cs:119-120)
        /// for every mode except None and NoVariableSync, without consulting another check -- and CE0015
        /// reaches only the last of the Manual shapes CE0063's type comment lists, a synced field written
        /// on a behaviour whose source contains no send at all. The gap between those is a gap: on a
        /// base-declared send there was no second opinion, just one check that was wrong about it and
        /// another that had already left.
        ///
        /// Own declarations are searched first, so a behaviour that has its own call still reports the same
        /// call site it reported before. This only ever supplies an answer where there was none.
        /// </summary>
        internal static InvocationExpressionSyntax FindFirstRequestSerializationIncludingBases(
            TypeSymbol type,
            CompilationContext compilationContext)
        {
            return FindFirstRequestSerializationIn(GetClassDeclarationsWithBases(type), compilationContext);
        }

        private static InvocationExpressionSyntax FindFirstRequestSerializationIn(
            List<ClassDeclarationSyntax> declarations,
            CompilationContext compilationContext)
        {
            foreach (ClassDeclarationSyntax classDeclaration in declarations)
            {
                SemanticModel model = GetSemanticModel(compilationContext, classDeclaration);

                foreach (InvocationExpressionSyntax invocation in classDeclaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
                {
                    if (IsRequestSerializationCall(invocation, model))
                        return invocation;
                }
            }

            return null;
        }

        /// <summary>
        /// The simple name being invoked, ignoring the receiver.
        /// </summary>
        internal static string GetInvokedName(InvocationExpressionSyntax invocation)
        {
            switch (invocation?.Expression)
            {
                case IdentifierNameSyntax identifier:
                    return identifier.Identifier.Text;

                case GenericNameSyntax genericName:
                    return genericName.Identifier.Text;

                case MemberAccessExpressionSyntax memberAccess:
                    return memberAccess.Name.Identifier.Text;

                case MemberBindingExpressionSyntax memberBinding:
                    return memberBinding.Name.Identifier.Text;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Whether the call targets this behaviour instance rather than some other object. A call through
        /// another reference is that object's problem, and reporting it here would blame the wrong type.
        ///
        /// 'base.' counts. UdonSharpBehaviour's RequestSerialization and SendCustomNetworkEvent are the
        /// only implementations there are, so 'base.RequestSerialization()' serializes this behaviour and
        /// 'base.SendCustomNetworkEvent(...)' sends through this behaviour's networking -- the same call
        /// with a different receiver spelling. Reading it as some other object's business let a behaviour
        /// that only ever serialized itself through 'base.' look like one that never serializes at all.
        /// </summary>
        internal static bool IsSelfInvocation(InvocationExpressionSyntax invocation)
        {
            switch (invocation?.Expression)
            {
                case IdentifierNameSyntax _:
                case GenericNameSyntax _:
                    return true;

                case MemberAccessExpressionSyntax memberAccess:
                    return memberAccess.Expression is ThisExpressionSyntax ||
                           memberAccess.Expression is BaseExpressionSyntax;

                default:
                    return false;
            }
        }

        /// <summary>The name of the method a node sits in, for messages that have to be actionable.</summary>
        internal static string GetEnclosingMethodName(SyntaxNode node)
        {
            return node?.FirstAncestorOrSelf<MethodDeclarationSyntax>()?.Identifier.Text ?? "<unknown>";
        }

        /// <summary>Gets the Roslyn Location for a field symbol.</summary>
        internal static Location GetFieldLocation(FieldSymbol field)
        {
            return GetSymbolLocation(field?.RoslynSymbol);
        }

        /// <summary>Gets the Roslyn Location for a type symbol.</summary>
        internal static Location GetTypeLocation(TypeSymbol type)
        {
            return GetSymbolLocation(type?.RoslynSymbol);
        }

        private static Location GetSymbolLocation(ISymbol symbol)
        {
            var declaringSyntaxRefs = symbol?.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs != null && declaringSyntaxRefs.Value.Length > 0)
            {
                return declaringSyntaxRefs.Value.First().GetSyntax().GetLocation();
            }

            var locations = symbol?.Locations;
            if (locations != null && locations.Value.Length > 0)
            {
                return locations.Value.First();
            }

            return Location.None;
        }

        private static bool IsDeclaredOn(ISymbol symbol, string containingTypeName)
        {
            INamedTypeSymbol containingType = symbol?.ContainingType;

            return containingType != null &&
                   string.Equals(containingType.ToDisplayString(), containingTypeName, StringComparison.Ordinal);
        }

        /// <summary>
        /// The expression a node writes to, or null when the node is not a write.
        /// </summary>
        private static ExpressionSyntax GetWriteTarget(SyntaxNode node)
        {
            switch (node)
            {
                case AssignmentExpressionSyntax assignment:
                    return assignment.Left;

                case PrefixUnaryExpressionSyntax prefix:
                    return prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)
                        ? prefix.Operand
                        : null;

                case PostfixUnaryExpressionSyntax postfix:
                    return postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)
                        ? postfix.Operand
                        : null;

                default:
                    return null;
            }
        }

        /// <summary>
        /// Resolves a write target to one of this behaviour's own synced fields, or null.
        /// </summary>
        private static FieldSymbol ResolveOwnSyncedField(
            ExpressionSyntax target,
            TypeSymbol type,
            Dictionary<string, FieldSymbol> syncedFields,
            SemanticModel model)
        {
            ExpressionSyntax current = target;

            // Peel the indexing and parentheses so 'scores[i] = x' resolves to 'scores'.
            while (true)
            {
                if (current is ParenthesizedExpressionSyntax parenthesized)
                {
                    current = parenthesized.Expression;
                    continue;
                }

                if (current is ElementAccessExpressionSyntax elementAccess)
                {
                    current = elementAccess.Expression;
                    continue;
                }

                break;
            }

            string name;
            switch (current)
            {
                case IdentifierNameSyntax identifier:
                    name = identifier.Identifier.Text;
                    break;

                case MemberAccessExpressionSyntax memberAccess when memberAccess.Expression is ThisExpressionSyntax:
                    name = memberAccess.Name.Identifier.Text;
                    break;

                default:
                    // A write through another behaviour reference belongs to that behaviour.
                    return null;
            }

            if (!syncedFields.TryGetValue(name, out FieldSymbol field))
                return null;

            ISymbol symbol = model?.GetSymbolInfo(current).Symbol;
            if (symbol != null)
            {
                // With a model, make sure a local or parameter shadowing the field is not counted as one.
                if (!(symbol is IFieldSymbol fieldSymbol))
                    return null;

                if (type?.RoslynSymbol != null &&
                    !SymbolEqualityComparer.Default.Equals(fieldSymbol.ContainingType, type.RoslynSymbol))
                {
                    return null;
                }
            }

            return field;
        }
    }
}
