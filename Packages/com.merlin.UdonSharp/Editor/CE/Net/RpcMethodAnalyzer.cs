using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using VRC.SDK3.Network;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Validates the CE.Net attribute surface on a behaviour: [Rpc] method signatures, and the placement
    /// of [MergeStrategy].
    ///
    /// Checks for:
    /// - Methods on behaviours with incompatible sync modes
    /// - Parameter count exceeding VRChat's limit (8)
    /// - Parameter lists whose argument count is not fixed (optional or 'params' parameters), and
    ///   by-reference parameters
    /// - Non-void return types
    /// - Parameter types VRChat cannot put on the wire
    /// - Missing rate limits on public RPCs
    /// - [MergeStrategy] on a field CE never writes
    /// </summary>
    /// <example>
    /// // Error - too many parameters
    /// [Rpc]
    /// public void TooManyParams(int a, int b, int c, int d, int e, int f, int g, int h, int i)
    /// { }  // CE0011: RPCs limited to 8 parameters
    ///
    /// // Info - no rate limit
    /// [Rpc]
    /// public void NoRateLimit(int value)
    /// { }  // CE0011: Consider adding rate limit
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

                // Check: Missing rate limit (info level)
                if (rpcAttr.RateLimit <= 0f)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Info(
                        location,
                        $"[Rpc] method '{method.Name}' has no rate limit. " +
                        "Consider adding RateLimit to prevent network spam.",
                        AnalyzerId,
                        AnalyzerId
                    ));
                }

                // Check: OwnerOnly on Target.Others (contradictory)
                if (rpcAttr.OwnerOnly && rpcAttr.Target == RpcTarget.Others)
                {
                    diagnostics.Add(AnalyzerDiagnostic.Warning(
                        location,
                        $"[Rpc] method '{method.Name}' has OwnerOnly=true but Target=Others. " +
                        "This combination is contradictory.",
                        AnalyzerId,
                        AnalyzerId,
                        "Use Target=All or Target=Owner for owner-only RPCs."
                    ));
                }
            }
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
