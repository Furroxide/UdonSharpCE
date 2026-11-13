using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp;
using UdonSharp.CE.Editor.CodeGen;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using VRC.SDK3.Network;
using VRC.SDK3.UdonNetworkCalling;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Validates the CE.Net attribute surface on a behaviour: [Rpc] method signatures, and the placement
    /// of [MergeStrategy].
    ///
    /// Checks for:
    /// - Static methods, which UdonSharp never exports as events
    /// - Methods on behaviours with incompatible sync modes
    /// - Parameter count exceeding VRChat's limit (8)
    /// - Parameter lists whose argument count is not fixed (optional or 'params' parameters), and
    ///   by-reference parameters
    /// - Non-void return types
    /// - Parameter types VRChat cannot put on the wire
    /// - No declared RateLimit (info: the default of 5 events per second applies)
    /// - A declared RateLimit that a hand-written [NetworkCallable] on the same method overrides
    /// - OwnerOnly together with Target=Owner, which only the owner calling itself can satisfy
    /// - [MergeStrategy] on a field CE never writes
    /// </summary>
    /// <example>
    /// // Error - too many parameters
    /// [Rpc]
    /// public void TooManyParams(int a, int b, int c, int d, int e, int f, int g, int h, int i)
    /// { }  // CE0011: RPCs limited to 8 parameters
    ///
    /// // Info - no declared rate
    /// [Rpc]
    /// public void NoRateLimit(int value)
    /// { }  // CE0011: default of 5 events per second applies
    ///
    /// // Error - invalid sync mode
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    /// public class MyBehaviour : UdonSharpBehaviour
    /// {
    ///     [Rpc]
    ///     public void MyRpc() { }  // CE0011: RPCs don't work with BehaviourSyncMode.None
    /// }
    /// </example>
    internal class RpcMethodAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0011";
        public string AnalyzerName => "CE.Net Attribute Validation";
        public string Description => "Validates [Rpc] method signatures and [MergeStrategy] placement.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// Diagnostic code for a [MergeStrategy] that can never run. It gets its own code rather than
        /// riding <see cref="AnalyzerId"/> so it is searchable independently of the RPC checks.
        /// </summary>
        private const string MergeStrategyMisplacedCode = "CE0046";

        /// <summary>
        /// Diagnostic code for a by-reference [Rpc] parameter, shared with the emitter so the two report
        /// the same failure under the same code.
        /// </summary>
        private const string RpcByRefParameterCode = "CE0047";

        /// <summary>
        /// Diagnostic code for an [Rpc] parameter list whose argument count is not fixed -- an optional or
        /// a 'params' parameter. Shared with the emitter for the same reason.
        /// </summary>
        private const string RpcVariableArityCode = "CE0050";

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            if (type == null || context == null)
                return diagnostics;

            AnalyzeRpcMethods(type, context, diagnostics);
            AnalyzeMergeStrategyFields(type, diagnostics);

            return diagnostics;
        }

        private void AnalyzeRpcMethods(TypeSymbol type, BindContext context, List<AnalyzerDiagnostic> diagnostics)
        {
            // Check behaviour sync mode
            var behaviourSyncAttr = type.GetAttribute<UdonBehaviourSyncModeAttribute>();
            bool networkingDisabled = behaviourSyncAttr?.behaviourSyncMode == BehaviourSyncMode.None;

            // Get all methods in the type
            var methods = type.GetMembers<MethodSymbol>(context);
            if (methods == null)
                return;

            foreach (MethodSymbol method in methods)
            {
                if (method == null)
                    continue;

                var rpcAttr = method.GetAttribute<RpcAttribute>();
                if (rpcAttr == null)
                    continue;

                Location location = GetMethodLocation(method);

                // Check: The method is an instance method.
                //
                // UdonSharp never exports a static method as an event and builds no network metadata for
                // one, so there is nothing on any client for a send to reach. The emitter refuses the
                // declaration during codegen under this same code; this is the same finding for a compile
                // that reaches analysis without having gone through it.
                if (method.IsStatic)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        location,
                        $"[Rpc] method '{method.Name}' is static. A network event is delivered to a behaviour " +
                        "instance, and UdonSharp does not export static methods as events, so no client " +
                        "could ever receive this one and the method body would never run.",
                        AnalyzerId,
                        AnalyzerId,
                        "Make it an instance method."
                    ));
                }

                // Check: Behaviour sync mode compatibility
                if (networkingDisabled)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        location,
                        $"[Rpc] method '{method.Name}' is on a behaviour with BehaviourSyncMode.None. " +
                        "SendCustomNetworkEvent won't work on this behaviour.",
                        AnalyzerId,
                        AnalyzerId,
                        "Change sync mode to Any, NoVariableSync, Continuous, or Manual."
                    ));
                }

                // Check: Parameter count
                if (method.Parameters.Length > NetworkLimits.MaxRpcParameters)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        location,
                        $"[Rpc] method '{method.Name}' has {method.Parameters.Length} parameters. " +
                        $"VRChat limits RPCs to {NetworkLimits.MaxRpcParameters} parameters.",
                        AnalyzerId,
                        AnalyzerId,
                        "Reduce parameter count or use synced variables instead."
                    ));
                }

                // Check: The argument count is fixed, and every argument travels by value.
                //
                // CE rewrites a call into SendCustomNetworkEvent, which forwards exactly the arguments
                // written at the call site. VRChat matches an incoming event to its entry point by argument
                // count and type and drops it when they disagree, without logging anything on either side.
                // So '[Rpc] void Play(int id, float volume = 1f)' called as 'Play(3)' sent one argument to a
                // two-parameter entry point and simply never ran, on any client -- no compile warning, no
                // runtime error, nothing to notice. The same holds for 'params': an expanded call sends one
                // argument per element to a single-parameter entry point.
                foreach (var param in method.Parameters)
                {
                    IParameterSymbol roslynParameter = param?.RoslynSymbol;
                    if (roslynParameter == null)
                        continue;

                    if (roslynParameter.IsOptional)
                    {
                        diagnostics.Add(AnalyzerDiagnostic.Error(
                            location,
                            $"[Rpc] method '{method.Name}' gives parameter '{param.Name}' a default value. " +
                            "CE forwards exactly the arguments written at the call site, and VRChat drops a " +
                            "network event whose argument count does not match the entry point, so a call " +
                            "that omits it would silently never run on any client.",
                            AnalyzerId,
                            RpcVariableArityCode,
                            "Remove the default and pass the value at every call site."
                        ));
                    }
                    else if (roslynParameter.IsParams)
                    {
                        diagnostics.Add(AnalyzerDiagnostic.Error(
                            location,
                            $"[Rpc] method '{method.Name}' declares 'params {param.Name}'. An expanded call " +
                            "would send one argument per element to a single-parameter entry point, and " +
                            "VRChat drops the mismatched event without a runtime error.",
                            AnalyzerId,
                            RpcVariableArityCode,
                            "Declare the array parameter without 'params' and pass the array explicitly."
                        ));
                    }

                    if (roslynParameter.RefKind != RefKind.None)
                    {
                        diagnostics.Add(AnalyzerDiagnostic.Error(
                            location,
                            $"[Rpc] method '{method.Name}' declares parameter '{param.Name}' as " +
                            $"'{roslynParameter.RefKind.ToString().ToLowerInvariant()}'. A network event " +
                            "carries values one way and has no reply channel, so nothing can be written back " +
                            "to the caller and VRChat's dispatch cannot bind the parameter.",
                            AnalyzerId,
                            RpcByRefParameterCode,
                            "Pass the value in, and send any result back with a second [Rpc]."
                        ));
                    }
                }

                // Check: Return type. A network event has no reply channel, so a returned value reaches
                // exactly one client -- the sender, if it happened to be a local call -- and is discarded
                // everywhere else.
                if (method.ReturnType != null)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        location,
                        $"[Rpc] method '{method.Name}' returns '{method.ReturnType.Name}'. " +
                        "Network events discard return values, so remote callers never see it.",
                        AnalyzerId,
                        AnalyzerId,
                        "Make the method void and send the result back with a second [Rpc] if a caller needs it."
                    ));
                }

                // Check: Parameter types are serializable. Validation is unconditional -- an opt-out here
                // only ever hid errors the emit phase raises anyway, a few seconds later and with less
                // context.
                foreach (var param in method.Parameters)
                {
                    if (IsNetworkSerializable(param.Type))
                        continue;

                    diagnostics.Add(AnalyzerDiagnostic.Error(
                        location,
                        $"[Rpc] parameter '{param.Name}' of type '{param.Type?.Name ?? "<unknown>"}' " +
                        "cannot be serialized over the network.",
                        AnalyzerId,
                        AnalyzerId,
                        "Use a type VRChat can encode: the numeric primitives, bool, char, string, VRCUrl, " +
                        "Vector2/3/4, Quaternion, Color, Color32, an enum, or a single-dimension array of " +
                        "those. Behaviour and VRCPlayerApi references cannot cross the wire -- send an " +
                        "index or a player id instead."
                    ));
                }

                // Check: No declared rate (info level). The method is not unlimited -- every [NetworkCallable]
                // entry point has a rate, and the emitter gives an [Rpc] that declares none the SDK default
                // of 5 events per second -- so the note says which limit is in effect. It used to say there
                // was none, which is the one thing that is never true of an [Rpc].
                if (rpcAttr.RateLimit <= 0f)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Info(
                        location,
                        $"[Rpc] method '{method.Name}' declares no RateLimit, so it is limited to the " +
                        "[NetworkCallable] default of 5 events per second unless the method carries its own " +
                        "[NetworkCallable(n)]. Set RateLimit if it needs a different rate.",
                        AnalyzerId,
                        AnalyzerId
                    ));
                }

                // Check: a declared RateLimit beside a hand-written [NetworkCallable] that says otherwise.
                //
                // The emitter does not add a second [NetworkCallable] to an [Rpc] that already carries one, so
                // the rate VRChat enforces is that attribute's value and RateLimit is read by nothing at all.
                // When the two disagree the declaration states one rate while another runs, and nothing else
                // said so.
                // No RateLimit, or one that matches, is not reported: there is nothing to contradict.
                if (rpcAttr.RateLimit > 0f &&
                    TryGetHandWrittenNetworkCallableRate(method, out int enforcedRate) &&
                    !UnityEngine.Mathf.Approximately(enforcedRate, rpcAttr.RateLimit))
                {
                    string declaredRate = rpcAttr.RateLimit.ToString("0.###", CultureInfo.InvariantCulture);

                    diagnostics.Add(AnalyzerDiagnostic.Warning(
                        location,
                        $"[Rpc] method '{method.Name}' declares RateLimit = {declaredRate}, but it also carries " +
                        $"[NetworkCallable({enforcedRate})], written by hand. CE leaves a [NetworkCallable] the " +
                        $"author wrote untouched, so VRChat enforces {enforcedRate} events per second for this " +
                        $"method and RateLimit = {declaredRate} is not in effect.",
                        AnalyzerId,
                        AnalyzerId,
                        "Remove the hand-written [NetworkCallable] and let RateLimit set the rate, or make the " +
                        "two values agree."
                    ));
                }

                // Check: OwnerOnly on Target.Owner, the one combination that can only ever talk to itself.
                //
                // Target decides who RECEIVES the event and OwnerOnly decides whose SEND the receiver
                // honours, so the two are independent and most pairings are meaningful. Target=Owner with
                // OwnerOnly is the exception: the event is delivered to the owner alone, and the owner
                // then drops it unless the owner sent it.
                //
                // This used to warn on OwnerOnly with Target=Others instead, calling it contradictory and
                // recommending Target=Owner. That had it backwards on both counts: the owner announcing
                // something to every other player is an ordinary, working shape -- each receiver sees the
                // owner as the caller and the guard passes -- and the recommended replacement was this one.
                if (rpcAttr.OwnerOnly && rpcAttr.Target == RpcTarget.Owner)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Warning(
                        location,
                        $"[Rpc] method '{method.Name}' has OwnerOnly=true and Target=Owner. The event is " +
                        "delivered only to the owner, and the owner-only check then drops it unless the " +
                        "owner itself sent it, so the only call that ever runs is the owner calling its own " +
                        "copy. A call from any other player reaches the owner and is discarded there.",
                        AnalyzerId,
                        AnalyzerId,
                        "If other players are meant to ask the owner to do something, remove OwnerOnly. If " +
                        "the owner is announcing something, use Target=All or Target=Others."
                    ));
                }
            }
        }

        /// <summary>
        /// The rate on a [NetworkCallable] the author wrote on this method, or false when it carries none.
        /// </summary>
        /// <remarks>
        /// Analysis runs over the rewritten trees, where every [Rpc] that had no [NetworkCallable] has been
        /// given one by the emitter. That one holds the rate the emitter derived from RateLimit, and where
        /// RateLimit could not be expressed the emitter has already said so, so it is recognised by the
        /// spelling the emitter writes (<see cref="RpcEmitter.InjectedNetworkCallableName"/>) and skipped.
        /// Reporting it would tell the author they wrote an attribute they never wrote.
        /// </remarks>
        private static bool TryGetHandWrittenNetworkCallableRate(MethodSymbol method, out int rate)
        {
            rate = 0;

            NetworkCallableAttribute networkCallable = method.GetAttribute<NetworkCallableAttribute>();
            if (networkCallable == null || method.RoslynSymbol == null)
                return false;

            foreach (AttributeData attribute in method.RoslynSymbol.GetAttributes())
            {
                if (attribute.AttributeClass?.Name != nameof(NetworkCallableAttribute))
                    continue;

                if (!(attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax written) ||
                    written.Name.ToString() == RpcEmitter.InjectedNetworkCallableName)
                {
                    return false;
                }

                rate = networkCallable.MaxEventsPerSecond;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Rejects [MergeStrategy] on any field CE does not write itself.
        /// </summary>
        /// <remarks>
        /// A merge needs both values in hand. CE only ever holds both for a [SyncOnJoin] field, where the
        /// generated glue performs the write. On an [UdonSynced] field Udon has already overwritten the
        /// heap slot by the time OnDeserialization runs, so the local value the strategy would merge
        /// against no longer exists -- the attribute would silently resolve every conflict to
        /// LastWriteWins while claiming to do something else. An unimplementable annotation that compiles
        /// is worse than one that does not.
        /// </remarks>
        private void AnalyzeMergeStrategyFields(TypeSymbol type, List<AnalyzerDiagnostic> diagnostics)
        {
            if (type.FieldSymbols.IsDefaultOrEmpty)
                return;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                if (field == null || field.GetAttribute<MergeStrategyAttribute>() == null)
                    continue;

                if (field.GetAttribute<SyncOnJoinAttribute>() != null)
                    continue;

                diagnostics.Add(AnalyzerDiagnostic.Error(
                    GetFieldLocation(field),
                    $"[MergeStrategy] on field '{field.Name}' cannot be honoured. CE can only merge a value " +
                    "it writes itself, which is only true for [SyncOnJoin] fields; for a [UdonSynced] field " +
                    "Udon overwrites the value before any CE code runs.",
                    AnalyzerId,
                    MergeStrategyMisplacedCode,
                    "Move the field to [SyncOnJoin], or drop [MergeStrategy] and merge explicitly in " +
                    "OnDeserialization using ConflictResolver.Resolve*."
                ));
            }
        }

        /// <summary>
        /// Asks the compiler's own oracle whether a parameter type can cross the wire.
        /// </summary>
        /// <remarks>
        /// This used to be a hand-written type-name set that green-lit VRCPlayerApi and behaviour
        /// references, neither of which VRChat can encode -- so the analyzer passed and the emit phase
        /// then failed with a less specific message. Going through <c>Type.UdonType.SystemType</c> and
        /// <see cref="VRCUdonSyncTypeConverter"/> is the exact expression
        /// <c>UdonSharpCompilerV1.BuildNetworkMetadata</c> uses, so the two can no longer disagree.
        /// </remarks>
        private bool IsNetworkSerializable(TypeSymbol type)
        {
            Type udonSystemType = type?.UdonType?.SystemType;

            if (udonSystemType == null)
                return false;

            return VRCUdonSyncTypeConverter.TypeToUdonType(udonSystemType) != VRCUdonSyncType.NONE;
        }

        /// <summary>
        /// Gets the Roslyn Location for a method symbol.
        /// </summary>
        private Location GetMethodLocation(MethodSymbol method)
        {
            var declaringSyntaxRefs = method.RoslynSymbol?.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs != null && declaringSyntaxRefs.Value.Length > 0)
            {
                return declaringSyntaxRefs.Value.First().GetSyntax().GetLocation();
            }

            var locations = method.RoslynSymbol?.Locations;
            if (locations != null && locations.Value.Length > 0)
            {
                return locations.Value.First();
            }

            return Location.None;
        }

        /// <summary>
        /// Gets the Roslyn Location for a field symbol.
        /// </summary>
        private Location GetFieldLocation(FieldSymbol field)
        {
            var declaringSyntaxRefs = field.RoslynSymbol?.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs != null && declaringSyntaxRefs.Value.Length > 0)
            {
                return declaringSyntaxRefs.Value.First().GetSyntax().GetLocation();
            }

            var locations = field.RoslynSymbol?.Locations;
            if (locations != null && locations.Value.Length > 0)
            {
                return locations.Value.First();
            }

            return Location.None;
        }
    }
}
