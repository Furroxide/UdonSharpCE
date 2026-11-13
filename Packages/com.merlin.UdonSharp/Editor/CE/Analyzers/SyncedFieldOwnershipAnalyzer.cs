using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Detects writes to synced fields on code paths that never check ownership.
    ///
    /// VRChat discards a synced-variable write from a non-owner. It does not throw, it does not log, and
    /// the local value still changes -- so the author sees the behaviour "work" on their own client and
    /// only ever hears that it is broken for everyone else. That silence is the entire reason this check
    /// exists.
    ///
    /// The check is deliberately hard to trip. A guard counts whether it sits in the writing method, in a
    /// helper the writer calls (the EnsureOwnership() shape), or in every call site the writer has inside
    /// the behaviour. Local-initialization and ownership-callback entry points and owner-scoped [Rpc]
    /// methods are exempt outright, and a handler this behaviour only ever sends to the owner with
    /// SendCustomNetworkEvent counts that event as a covered way in. A call site counts whether it is a
    /// direct call or one of Udon's string-addressed self dispatches (SendCustomEvent and its delayed
    /// forms), because a handler reached by name is still reached. The guard itself does not have to be
    /// a literal Networking.IsOwner call: a flag or property whose value comes from one counts, because
    /// that is how ownership is actually written.
    ///
    /// What is left after all of that is the shape with no answer anywhere on the path -- a write reached
    /// from an entry point the behaviour itself never calls -- which is the shape that silently loses
    /// data.
    ///
    /// All of that rests on one assumption the check cannot see: that every synced field is authoritative
    /// state only its owner is entitled to write. That is false for predict-and-reconcile netcode, where
    /// each client advances the value from its own simulation and the owner's periodic packet is what
    /// pulls the guesses back together. There the unguarded write is not the bug, it is the design, and
    /// CE0014 had no way to know -- which is why the fork's own NetVehicle produced five reports that were
    /// all correct code. CE's <c>[Sync(Authority...)]</c> lets the field say which it is, so the check
    /// splits in two:
    ///
    /// <list type="bullet">
    /// <item><description><c>Authority.Predicted</c>: the author has declared the unguarded write
    /// deliberate, so CE0014 says nothing about that field -- and nothing else picks the field up. Say
    /// that plainly rather than letting an author assume a second check is holding the other end:
    /// declaring Predicted asserts that the owner reconciles the field, and for any Manual or Continuous
    /// behaviour that assertion is taken on trust. CE0063 catches only the degenerate case where the
    /// declaring behaviour's sync mode (BehaviourSyncMode.None or NoVariableSync) replicates nothing at
    /// all, so no owner value could arrive whatever the rest of the project does. It does not examine
    /// Manual or Continuous behaviours, because reconciliation happens whenever RequestSerialization()
    /// runs <em>on the owner</em>, and that can be an ungated call in Update (which runs on the owner
    /// too), a base-class method, another behaviour entirely, or an inspector-wired SendCustomEvent --
    /// none of which are statically provable. Predicted turns a check off; it does not trade it for
    /// another one.</description></item>
    /// <item><description><c>Authority.Owner</c>: today's check, unchanged. The author has asserted the
    /// field is authoritative, which is exactly the claim CE0014 verifies.</description></item>
    /// <item><description>Bare <c>[UdonSynced]</c>: today's check, unchanged, because there is no
    /// declaration to read and the inference path is all there is. Only the fix suggestion differs, and
    /// only to name declaring the field predicted as the second way out.</description></item>
    /// </list>
    ///
    /// The split is per field rather than per write, so declaring a field predicted also stops CE0014
    /// looking at the genuinely authoritative writes to that same field. That is a real cost, and it is
    /// why <see cref="Authority.Owner"/> is the zero value and the fallback for anything unreadable.
    /// </summary>
    /// <example>
    /// // Warning - Interact() runs on whoever pressed it, which is usually not the owner
    /// public override void Interact()
    /// {
    ///     score++;  // CE0014: silently discarded on every non-owner
    /// }
    ///
    /// // Good - take ownership first
    /// public override void Interact()
    /// {
    ///     Networking.SetOwner(Networking.LocalPlayer, gameObject);
    ///     score++;
    /// }
    ///
    /// // Also good - the write is prediction, and the declaration says so. It is also an assertion
    /// // nothing checks: the owner has to actually publish _boostRemaining for the guesses to meet.
    /// [Sync(Authority.Predicted)] private float _boostRemaining;
    ///
    /// private void ApplyBoost(float dt)
    /// {
    ///     _boostRemaining -= dt;  // no CE0014: the declaration says every client is meant to write this
    /// }
    /// </example>
    internal class SyncedFieldOwnershipAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0014";
        public string AnalyzerName => "Synced Write Without Ownership";
        public string Description => "Detects synced field writes on code paths with no ownership check.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// Entry points where an unguarded synced write is not the bug this analyzer is looking for.
        /// </summary>
        private static readonly HashSet<string> ExemptMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            // Local initialization. These run on every client before ownership means anything, and seeding
            // defaults here is the normal pattern rather than a lost network write.
            "Awake",
            "Start",
            "OnEnable",
            "OnDisable",
            "OnDestroy",

            // The serialization callbacks. Writes here operate on data that has already arrived or is
            // about to be sent by the owner, so an ownership check would be redundant.
            "OnDeserialization",
            "OnPreSerialization",
            "OnPostSerialization",

            // Ownership callbacks are ownership-aware by construction.
            "OnOwnershipTransferred",
            "OnOwnershipRequest"
        };

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            if (type == null || compilationContext == null)
                return diagnostics;

            Dictionary<string, FieldSymbol> syncedFields = CENetworkAnalysis.GetSyncedFields(type);
            if (syncedFields.Count == 0)
                return diagnostics;

            // The ownership graph, not the plain one: a handler reached through SendCustomEvent or one of
            // its delayed forms has a caller, and the plain graph cannot see it.
            CENetworkAnalysis.BehaviourMethodGraph graph =
                CENetworkAnalysis.BuildOwnershipMethodGraph(type, compilationContext);

            if (graph.Methods.Count == 0)
                return diagnostics;

            List<CENetworkAnalysis.SyncedFieldWrite> writes =
                CENetworkAnalysis.FindSyncedFieldWrites(type, graph.Methods, compilationContext);

            if (writes.Count == 0)
                return diagnostics;

            // Resolved once per field rather than once per write, because the tier is a property of the
            // declaration: it decides both whether the field is checked at all and which ways out the
            // suggestion is allowed to name.
            var declaredAuthority = new Dictionary<string, Authority?>(StringComparer.Ordinal);

            foreach (KeyValuePair<string, FieldSymbol> syncedField in syncedFields)
                declaredAuthority[syncedField.Key] = CENetworkAnalysis.GetDeclaredAuthority(syncedField.Value);

            var exemptMethods = new Dictionary<MethodDeclarationSyntax, bool>();
            var reported = new HashSet<string>(StringComparer.Ordinal);

            HashSet<string> ownerTargetedEvents =
                CENetworkAnalysis.CollectOwnerTargetedEventNames(type, compilationContext);

            var walker = new GuardedPathWalker(
                graph,
                compilationContext,
                CENetworkAnalysis.CollectOwnershipDerivedNames(type, compilationContext),
                entryPoint => IsExemptEntryPoint(entryPoint, exemptMethods, compilationContext),
                entryPoint => IsOwnerScopedRpc(entryPoint, compilationContext),
                handler => IsOwnerTargetedEventHandler(handler, ownerTargetedEvents));

            foreach (CENetworkAnalysis.SyncedFieldWrite write in writes)
            {
                MethodDeclarationSyntax method = write.Method;
                if (method == null)
                    continue;

                declaredAuthority.TryGetValue(write.Field.Name, out Authority? authority);

                // A predicted field's unguarded write is the prediction, so there is nothing here to
                // report. Dropping it before the guard walk instead of filtering the finished report
                // keeps the two tiers from sharing state where one could start quietly deciding the
                // other: everything below this line sees only owner-authoritative fields.
                if (authority == Authority.Predicted)
                    continue;

                if (walker.IsGuarded(method))
                    continue;

                // One report per field per method. A loop that writes the same field ten times is one bug.
                string reportKey = method.Identifier.Text + "::" + write.Field.Name;
                if (!reported.Add(reportKey))
                    continue;

                string fixSuggestion =
                    $"Guard the write with 'if (!Networking.IsOwner(Networking.LocalPlayer, gameObject)) return;', or take ownership first " +
                    $"with 'Networking.SetOwner(Networking.LocalPlayer, gameObject);'. Putting it in '{method.Identifier.Text}' covers every " +
                    "caller; putting it in a caller instead only clears this warning once every call site in the behaviour has one. A cached " +
                    "flag or property whose value comes from Networking.IsOwner counts as the check.";

                // The third way out is offered only to a field that has not already answered the
                // question. Declaring [Sync(Authority.Owner)] is an assertion that the field is
                // authoritative -- which is the claim this report is testing -- so pointing its author at
                // Authority.Predicted would be telling them to rewrite the contract until the check
                // agrees, rather than to fix the write.
                //
                // Absent is the only state where "add [Sync(Authority.Predicted)]" is true advice.
                // Unreadable also arrives here as a null authority -- it routes to the strict check, which
                // is right -- but the field already carries a CE [Sync], so telling its author to add one
                // describes code that does not exist.
                //
                // The wording of that third way out is load-bearing. An earlier version sold it as a
                // trade -- "moves the field into CE0063, which verifies the other half of the bargain" --
                // and that promise is not kept: CE0063 was narrowed to the one shape it can prove, a
                // behaviour whose sync mode replicates nothing, so on any Manual or Continuous behaviour
                // the field leaves this check and lands in no check at all. Selling a check that will not
                // run pushes the author toward the unsafe declaration on a guarantee that does not exist,
                // so the suggestion has to say that Predicted switches the check off.
                if (CENetworkAnalysis.GetAuthorityDeclaration(write.Field, out _) ==
                    CENetworkAnalysis.SyncAuthorityDeclaration.Absent)
                {
                    fixSuggestion +=
                        $" If the write to '{write.Field.Name}' is deliberate local prediction that the owner's next packet is meant to " +
                        "overwrite, say so at the declaration with '[Sync(Authority.Predicted)]' instead -- but know what that does. It " +
                        "turns this check off for the field; it does not hand it to another one. Declaring Predicted asserts that the " +
                        "owner publishes a value to reconcile against, and that assertion is taken on trust: no analyzer verifies it. " +
                        "CE0063 reports only a predicted field on a behaviour whose sync mode replicates no variables at all " +
                        "(BehaviourSyncMode.None or NoVariableSync). Use Predicted only when you know the owner really does send.";
                }

                diagnostics.Add(AnalyzerDiagnostic.Warning(
                    write.Node?.GetLocation() ?? CENetworkAnalysis.GetFieldLocation(write.Field),
                    $"Synced field '{write.Field.Name}' is written in '{method.Identifier.Text}' with no ownership check on the path. " +
                    "VRChat silently discards synced-variable writes from non-owners, so on every client that does not own " +
                    $"this object the write to '{write.Field.Name}' is lost with no error and the local value diverges.",
                    AnalyzerId,
                    AnalyzerId,
                    fixSuggestion
                ));
            }

            return diagnostics;
        }

        /// <summary>
        /// Whether the method is itself an entry point where an unguarded synced write is expected.
        ///
        /// This is a property of the entry point and nothing wider. It used to be asked of the whole
        /// caller closure -- "is any method that can reach this write exempt" -- and one exempt caller
        /// then excused every other way in:
        /// 'void Start() { ResetScore(); }  public override void Interact() { ResetScore(); }' reported
        /// nothing for the write in ResetScore, although Interact runs on whoever clicked. Deleting the
        /// call from Start brought the report back, which is a check answering to something other than
        /// the code it is reading. A helper that only Start calls is still not a bug: the walker gets
        /// there by asking whether every way into the helper is covered, and Start is.
        ///
        /// An owner-scoped [Rpc] is exempt here because its body only ever runs on the owner: RpcEmitter
        /// turns every call to it, this behaviour's own included, into a network send to the owner. A
        /// plain handler of an owner-targeted SendCustomNetworkEvent is deliberately not, because nothing
        /// rewrites a direct call to it -- see <see cref="IsOwnerTargetedEventHandler"/>.
        /// </summary>
        private static bool IsExemptEntryPoint(
            MethodDeclarationSyntax method,
            Dictionary<MethodDeclarationSyntax, bool> cache,
            CompilationContext compilationContext)
        {
            if (cache.TryGetValue(method, out bool cached))
                return cached;

            bool exempt = ExemptMethods.Contains(method.Identifier.Text) ||
                          IsOwnerScopedRpc(method, compilationContext);

            cache[method] = exempt;
            return exempt;
        }

        /// <summary>
        /// Whether the method is what 'SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(Method))'
        /// runs, and nothing in the behaviour sends the same event to anyone else.
        ///
        /// That makes the network event a covered way in -- it only ever delivers the call to the owner --
        /// and nothing more. It is not an exemption for the method, because a direct call is not a network
        /// event: 'Interact() { ToggleOnOwner(); }' next to the owner-targeted send runs the write on
        /// whoever clicked. So the walker counts the event as covering the method's outside entry, and
        /// still asks every caller inside the behaviour to vouch for itself.
        ///
        /// The name alone is not enough. Udon only delivers a network event to a public method whose name
        /// does not start with an underscore, so a send naming anything else is a call that never arrives,
        /// and a method the event can never reach gets no cover from it.
        /// </summary>
        private static bool IsOwnerTargetedEventHandler(MethodDeclarationSyntax method, HashSet<string> ownerTargetedEvents)
        {
            string name = method.Identifier.Text;

            if (ownerTargetedEvents == null || !ownerTargetedEvents.Contains(name))
                return false;

            return !name.StartsWith("_", StringComparison.Ordinal) && method.Modifiers.Any(SyntaxKind.PublicKeyword);
        }

        /// <summary>
        /// Answers "is every way into this method ownership gated" for the methods of one behaviour.
        ///
        /// Two shapes count, and the difference between them is what keeps the check honest. A guard in
        /// the method itself or in a helper it calls (the EnsureOwnership() shape) gates the write
        /// wherever the method is reached from. A guard in a caller only gates the write if it gates
        /// <em>every</em> caller -- one gated call site says nothing about the ungated one next to it,
        /// and a method with no call sites at all is an entry point VRChat can invoke directly, which is
        /// the case CE0014 exists for.
        /// </summary>
        private sealed class GuardedPathWalker
        {
            private readonly CENetworkAnalysis.BehaviourMethodGraph _graph;
            private readonly CompilationContext _compilationContext;
            private readonly HashSet<string> _ownershipDerivedNames;
            private readonly Func<MethodDeclarationSyntax, bool> _isExemptEntryPoint;
            private readonly Func<MethodDeclarationSyntax, bool> _runsOnlyOnOwner;
            private readonly Func<MethodDeclarationSyntax, bool> _isOwnerTargetedHandler;

            private readonly Dictionary<MethodDeclarationSyntax, bool> _guarded =
                new Dictionary<MethodDeclarationSyntax, bool>();

            private readonly Dictionary<MethodDeclarationSyntax, bool> _guardsOwnPath =
                new Dictionary<MethodDeclarationSyntax, bool>();

            private readonly HashSet<MethodDeclarationSyntax> _inProgress =
                new HashSet<MethodDeclarationSyntax>();

            private int _cycleAssumptions;

            public GuardedPathWalker(
                CENetworkAnalysis.BehaviourMethodGraph graph,
                CompilationContext compilationContext,
                HashSet<string> ownershipDerivedNames,
                Func<MethodDeclarationSyntax, bool> isExemptEntryPoint,
                Func<MethodDeclarationSyntax, bool> runsOnlyOnOwner,
                Func<MethodDeclarationSyntax, bool> isOwnerTargetedHandler)
            {
                _graph = graph;
                _compilationContext = compilationContext;
                _ownershipDerivedNames = ownershipDerivedNames;
                _isExemptEntryPoint = isExemptEntryPoint;
                _runsOnlyOnOwner = runsOnlyOnOwner;
                _isOwnerTargetedHandler = isOwnerTargetedHandler;
            }

            /// <summary>
            /// Whether a synced write in this method is covered: the method is an exempt entry point, or
            /// an ownership check stands on every way to the write.
            /// </summary>
            public bool IsGuarded(MethodDeclarationSyntax method)
            {
                if (method == null || _graph == null)
                    return false;

                if (_isExemptEntryPoint(method))
                    return true;

                return IsGuardedByOwnershipCheck(method);
            }

            /// <summary>
            /// The same question with the entry-point exemption left out, for the one caller that must not
            /// take it: see <see cref="Vouches"/>.
            /// </summary>
            private bool IsGuardedByOwnershipCheck(MethodDeclarationSyntax method)
            {
                if (_guarded.TryGetValue(method, out bool cached))
                    return cached;

                // Cycles are handled by collapsing them in EveryCallerGuards, so the walk should only ever
                // ascend to methods outside the one it is evaluating. This is the backstop for a graph
                // shape that manages it anyway: re-entry defers to the evaluation already in flight rather
                // than recursing until the stack gives out, because an analyzer that dies stops checking
                // every behaviour after it.
                if (!_inProgress.Add(method))
                {
                    _cycleAssumptions++;
                    return true;
                }

                int assumptionsBefore = _cycleAssumptions;

                bool guarded = GuardsOwnPath(method) || EveryCallerGuards(method);

                _inProgress.Remove(method);

                // A result that leaned on the cycle answer above is only true relative to the walk that
                // produced it, so it must not be reused for a different starting method.
                if (_cycleAssumptions == assumptionsBefore)
                    _guarded[method] = guarded;

                return guarded;
            }

            /// <summary>
            /// Whether the method or one of the helpers it runs checks ownership itself. Helpers it only
            /// schedules with a delay are not searched: their check runs after the method has returned.
            /// </summary>
            private bool GuardsOwnPath(MethodDeclarationSyntax method)
            {
                if (_guardsOwnPath.TryGetValue(method, out bool cached))
                    return cached;

                bool guarded = false;

                foreach (MethodDeclarationSyntax callee in _graph.ImmediateCallees(method))
                {
                    SemanticModel model = CENetworkAnalysis.GetSemanticModel(_compilationContext, callee);

                    if (CENetworkAnalysis.ContainsOwnershipGuard(callee, model, _ownershipDerivedNames))
                    {
                        guarded = true;
                        break;
                    }
                }

                _guardsOwnPath[method] = guarded;
                return guarded;
            }

            /// <summary>
            /// Whether the method has call sites in this behaviour and all of them are gated.
            ///
            /// No call sites is deliberately not "vacuously gated": a method nothing in the behaviour
            /// calls is reached from outside it -- an Interact override, a UI event, an RPC entry point,
            /// another behaviour's SendCustomEvent -- and that is the risky case, not the safe one. The one
            /// outside way in that is covered is the owner-targeted network event this behaviour sends,
            /// which only ever runs the handler on the owner; a handler reached that way and from nowhere
            /// else in the behaviour is guarded by the send itself.
            ///
            /// Recursion is judged as one unit for that reason. A call from inside a cycle is not a way
            /// *into* the cycle, so counting it as a call site would let 'void Tick() { synced++; Tick(); }'
            /// vouch for itself, and a mutually recursive pair nothing else calls vouch for each other --
            /// turning the entry points most likely to be reached ungated into the ones the check trusts
            /// most.
            /// </summary>
            private bool EveryCallerGuards(MethodDeclarationSyntax method)
            {
                // Everything the method can reach that can reach it back, which is the method itself for
                // the ordinary non-recursive case.
                HashSet<MethodDeclarationSyntax> cycle = _graph.Callees(method);
                cycle.IntersectWith(_graph.Callers(method));

                bool hasEntryPoint = false;

                foreach (MethodDeclarationSyntax member in cycle)
                {
                    foreach (MethodDeclarationSyntax caller in _graph.DirectCallers(member))
                    {
                        if (cycle.Contains(caller))
                            continue;

                        hasEntryPoint = true;

                        if (!Vouches(caller, member))
                            return false;
                    }
                }

                if (hasEntryPoint)
                    return true;

                foreach (MethodDeclarationSyntax member in cycle)
                {
                    if (_isOwnerTargetedHandler(member))
                        return true;
                }

                return false;
            }

            /// <summary>
            /// Whether this one call site covers the method it calls.
            ///
            /// An ordinary call is covered when its caller is: guarded, or an exempt entry point. A call
            /// that only happens later -- SendCustomEventDelayedSeconds / SendCustomEventDelayedFrames --
            /// does not inherit an exemption that was about the moment the caller runs. Start is exempt
            /// because seeding defaults before ownership means anything is not a lost write; a timer that
            /// Start arms on every client and that writes synced state a second later is one, on every
            /// client but the owner. So across a delay the caller has to carry a real ownership check, be
            /// covered only by the owner-targeted event that runs it, or be an owner-scoped [Rpc], whose
            /// body only ever runs on the owner in the first place.
            /// </summary>
            private bool Vouches(MethodDeclarationSyntax caller, MethodDeclarationSyntax callee)
            {
                if (!_graph.IsDeferredOnlyCall(caller, callee))
                    return IsGuarded(caller);

                return _runsOnlyOnOwner(caller) || IsGuardedByOwnershipCheck(caller);
            }
        }

        /// <summary>
        /// Whether the method body only ever runs on the owner, which is true of exactly one [Rpc] shape:
        /// an effective Target of <see cref="RpcTarget.Owner"/>. Writing synced state there is the correct
        /// pattern, not a missing guard.
        ///
        /// OwnerOnly deliberately does not count, and neither does [RpcOwnerOnly]. Both restrict who may
        /// <em>call</em> the RPC -- the check runs receiver-side against NetworkCalling.CallingPlayer --
        /// while Target still defaults to RpcTarget.All (RpcOwnerOnlyAttribute chains ': base(RpcTarget.All)').
        /// The body therefore runs on every receiver, and on each non-owner the synced write is discarded
        /// exactly as CE0014 describes. Exempting those methods hid the bug in the one place authors are
        /// most confident they are safe.
        /// </summary>
        private static bool IsOwnerScopedRpc(MethodDeclarationSyntax method, CompilationContext compilationContext)
        {
            SemanticModel model = CENetworkAnalysis.GetSemanticModel(compilationContext, method);
            if (model == null)
                return false;

            // Bound through the base SemanticModel so the language-specific extension overload is not
            // required here; an unexpected symbol kind just means we cannot prove the method is owner
            // scoped, which is the safe answer.
            if (!(model.GetDeclaredSymbol(method) is IMethodSymbol methodSymbol))
                return false;

            foreach (AttributeData attribute in methodSymbol.GetAttributes())
            {
                if (!IsRpcAttribute(attribute.AttributeClass))
                    continue;

                if (HasOwnerTarget(attribute))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Whether the attribute is [Rpc] or anything deriving from it, so shorthand subclasses such as
        /// [RpcOwnerOnly] are read through the same Target rules rather than trusted on their name.
        /// </summary>
        private static bool IsRpcAttribute(INamedTypeSymbol attributeClass)
        {
            for (INamedTypeSymbol current = attributeClass; current != null; current = current.BaseType)
            {
                if (string.Equals(current.Name, nameof(RpcAttribute), StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The attribute's effective Target, resolved the way the attribute itself resolves it: the
        /// constructor runs first, then any object initializer, so a named Target wins over the positional
        /// one. Absent both, RpcAttribute's default is RpcTarget.All -- not the owner.
        /// </summary>
        private static bool HasOwnerTarget(AttributeData attribute)
        {
            foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
            {
                if (string.Equals(named.Key, "Target", StringComparison.Ordinal))
                    return IsOwnerTarget(named.Value.Value);
            }

            return attribute.ConstructorArguments.Length > 0 &&
                   IsOwnerTarget(attribute.ConstructorArguments[0].Value);
        }

        /// <summary>
        /// Attribute arguments arrive as the enum's underlying value, so compare against that.
        /// </summary>
        private static bool IsOwnerTarget(object value)
        {
            return value is int target && target == (int)RpcTarget.Owner;
        }
    }
}
