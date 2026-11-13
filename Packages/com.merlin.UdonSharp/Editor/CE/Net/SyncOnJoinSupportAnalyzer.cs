using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp.CE.Editor.Net;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Warns about every [SyncOnJoin] field the glue emitter leaves out of the payload: a field whose
    /// type requires a custom serializer, and a static or const field, which has no per-instance state.
    /// </summary>
    internal class SyncOnJoinSupportAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0032";
        public string AnalyzerName => "SyncOnJoin Type Support";
        public string Description => "Warns on SyncOnJoin field types that need a custom serializer.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            if (type.FieldSymbols.IsDefaultOrEmpty)
                yield break;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                if (field.GetAttribute<SyncOnJoinAttribute>() == null)
                    continue;

                // Checked before [SyncSerializer], because the emitter drops these before it ever reads the
                // serializer: a static or const field holds no per-instance state to send. Without this the
                // field was left out of the payload with nothing anywhere to say so -- its type is usually
                // a perfectly supported one, so the type check below never fired for it.
                if (field.IsStatic || field.IsConst)
                {
                    yield return AnalyzerDiagnostic.Warning(
                        GetFieldLocation(field),
                        $"[SyncOnJoin] field '{field.Name}' is {(field.IsConst ? "const" : "static")}, so it holds no per-instance state and will be omitted from generated late-join glue.",
                        AnalyzerId,
                        AnalyzerId,
                        "Remove [SyncOnJoin] from this field, or make it an instance field."
                    );
                    continue;
                }

                if (field.GetAttribute<SyncSerializerAttribute>() != null)
                    continue;

                if (!SyncOnJoinTypeSupport.IsManualOnlyRoot(field.Type?.RoslynSymbol, out string reason))
                    continue;

                yield return AnalyzerDiagnostic.Warning(
                    GetFieldLocation(field),
                    $"[SyncOnJoin] field '{field.Name}' of type '{field.Type?.Name ?? "<unknown>"}' requires a custom serializer and will be omitted from generated late-join glue.",
                    AnalyzerId,
                    AnalyzerId,
                    $"Add [SyncSerializer(nameof(SerializeMethod), nameof(DeserializeMethod))] to this field. Reason: {reason}."
                );
            }
        }

        private static Location GetFieldLocation(FieldSymbol field)
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
