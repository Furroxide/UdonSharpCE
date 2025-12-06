using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Validates [Rpc] method signatures and rate limiting configuration.
    ///
    /// Checks for:
    /// - Parameter count exceeding VRChat's limit (8)
    /// - Non-serializable parameter types
    /// - Missing rate limits on public RPCs
    /// - Methods on behaviours with incompatible sync modes
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
        public string AnalyzerName => "RPC Method Validation";
        public string Description => "Validates [Rpc] method signatures and rate limiting.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// Types that can be serialized over the network.
        /// </summary>
        private static readonly HashSet<string> SerializableTypes = new HashSet<string>
        {
            // Primitives
            "System.Boolean",
            "System.Byte",
            "System.SByte",
            "System.Int16",
            "System.UInt16",
            "System.Int32",
            "System.UInt32",
            "System.Int64",
            "System.UInt64",
            "System.Single",
            "System.Double",
            "System.Char",
            "System.String",
            // Unity types
            "UnityEngine.Vector2",
            "UnityEngine.Vector3",
            "UnityEngine.Vector4",
            "UnityEngine.Quaternion",
            "UnityEngine.Color",
            "UnityEngine.Color32",
            // VRChat types
            "VRC.SDKBase.VRCPlayerApi"
        };

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Check behaviour sync mode
            var behaviourSyncAttr = type.GetAttribute<UdonBehaviourSyncModeAttribute>();
            bool networkingDisabled = behaviourSyncAttr?.behaviourSyncMode == BehaviourSyncMode.None;

            // Get all methods in the type
            var methods = type.GetMembers<MethodSymbol>(context);

            foreach (MethodSymbol method in methods)
            {
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

                // Check: Parameter types are serializable
                if (rpcAttr.ValidateParameters)
                {
                    foreach (var param in method.Parameters)
                    {
                        if (!IsSerializableType(param.Type, context))
                        {
                            diagnostics.Add(AnalyzerDiagnostic.Error(
                                location,
                                $"[Rpc] parameter '{param.Name}' of type '{param.Type.Name}' " +
                                "cannot be serialized over the network.",
                                AnalyzerId,
                                AnalyzerId,
                                "Use a serializable type (primitives, Vector3, VRCPlayerApi, etc.)."
                            ));
                        }
                    }
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

            return diagnostics;
        }

        /// <summary>
        /// Checks if a type can be serialized over the network.
        /// </summary>
        private bool IsSerializableType(TypeSymbol type, BindContext context)
        {
            // Handle arrays
            if (type.IsArray)
            {
                return IsSerializableType(type.ElementType, context);
            }

            string typeName = type.RoslynSymbol.ToString();

            // Check known serializable types
            if (SerializableTypes.Contains(typeName))
                return true;

            // UdonSharpBehaviours can be passed as references
            if (type.IsUdonSharpBehaviour)
                return true;

            // Enums are serializable as their underlying type
            if (type.RoslynSymbol.TypeKind == TypeKind.Enum)
                return true;

            return false;
        }

        /// <summary>
        /// Gets the Roslyn Location for a method symbol.
        /// </summary>
        private Location GetMethodLocation(MethodSymbol method)
        {
            var declaringSyntaxRefs = method.RoslynSymbol?.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs != null && declaringSyntaxRefs.Length > 0)
            {
                return declaringSyntaxRefs.First().GetSyntax().GetLocation();
            }

            var locations = method.RoslynSymbol?.Locations;
            if (locations != null && locations.Length > 0)
            {
                return locations.First();
            }

            return Location.None;
        }
    }
}
