using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using UnityEngine;
using VRC.Udon;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Warns when a behaviour with Continuous sync mode exceeds 200 bytes.
    ///
    /// VRChat limits continuous sync payloads to approximately 200 bytes.
    /// Exceeding this limit causes sync issues and potential data loss.
    /// Consider using Manual sync mode for larger data payloads.
    ///
    /// In a normal compile the only payloads this sees are made of fixed-size fields. A Continuous
    /// behaviour that declares a synced array or string never gets here: BehaviourSyncModeEmitter raises
    /// CE0034 for it during Phase A codegen, as an error, before Bind. The array and string estimates
    /// below are kept as a backstop for the day that gate is narrowed, not because they are reachable --
    /// which is why the example is thirteen quaternions rather than one array.
    /// </summary>
    /// <example>
    /// // Warning - approximately 208 bytes of sync data (13 x 16)
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
    /// public class RagdollPose : UdonSharpBehaviour   // CE0003
    /// {
    ///     [UdonSynced] private Quaternion _hips;
    ///     [UdonSynced] private Quaternion _spine;
    ///     // ... eleven more joints
    /// }
    ///
    /// // Better - use Manual sync for large payloads
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class RagdollPose : UdonSharpBehaviour
    /// {
    ///     [UdonSynced] private Quaternion[] _joints = new Quaternion[13];
    /// }
    /// </example>
    internal class SyncPayloadSizeAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0003";
        public string AnalyzerName => "Sync Payload Size";
        public string Description => "Warns when continuous sync payload exceeds the recommended limit.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// Maximum recommended size for continuous sync in bytes.
        /// </summary>
        private const int CONTINUOUS_SYNC_LIMIT = NetworkLimits.ContinuousSyncMaxBytes;

        /// <summary>
        /// Warning threshold (percentage of limit).
        /// </summary>
        private const float WARNING_THRESHOLD = NetworkLimits.WarningThreshold;

        /// <summary>
        /// What a synced string is counted as when nothing bounds its length: the same default every other
        /// CE estimate uses, so this report and NetworkLimits cannot quote two different sizes for one
        /// field.
        /// </summary>
        private const int ESTIMATED_STRING_BYTES =
            NetworkLimits.DefaultEstimatedSyncedStringLength * NetworkLimits.SyncedStringBytesPerCharacter;

        /// <summary>
        /// Type sizes for common types in bytes, keyed by CLR type name.
        /// </summary>
        private static readonly Dictionary<string, int> TypeSizes = new Dictionary<string, int>
        {
            { "System.Boolean", 1 },
            { "System.Byte", 1 },
            { "System.SByte", 1 },
            { "System.Int16", 2 },
            { "System.UInt16", 2 },
            { "System.Int32", 4 },
            { "System.UInt32", 4 },
            { "System.Int64", 8 },
            { "System.UInt64", 8 },
            { "System.Single", 4 },
            { "System.Double", 8 },
            { "System.Char", 2 },
            { "UnityEngine.Vector2", 8 },
            { "UnityEngine.Vector3", 12 },
            { "UnityEngine.Vector4", 16 },
            { "UnityEngine.Quaternion", 16 },
            { "UnityEngine.Color", 16 },
            { "UnityEngine.Color32", 4 },
        };

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Both the mode and the payload come from the whole class chain. A mode declared on a base class
            // applies to every subclass, and UdonSharp emits each base class's synced fields into the
            // subclass's program. Reading only this type's own attribute and fields skipped a Continuous
            // behaviour whose mode sits on its base, and under-counted a subclass by everything its base syncs.
            if (CENetworkAnalysis.GetDeclaredSyncMode(type) != BehaviourSyncMode.Continuous)
                return diagnostics;

            // Calculate total sync payload size
            int totalBytes = 0;
            var syncedFields = new List<(string name, int size)>();

            if (!type.FieldSymbols.IsDefaultOrEmpty)
            {
                foreach (FieldSymbol field in type.FieldSymbols)
                {
                    if (!field.IsSynced)
                        continue;

                    int fieldSize = EstimateFieldSize(field, context);
                    if (fieldSize > 0)
                    {
                        syncedFields.Add((field.Name, fieldSize));
                        totalBytes += fieldSize;
                    }
                }
            }

            // Inherited synced fields come from the Roslyn symbols, not the bases' FieldSymbols: those only
            // exist once a base has itself been bound, and an unbound base read that way contributes nothing.
            foreach (IFieldSymbol inherited in CENetworkAnalysis.GetInheritedSyncedFields(type))
            {
                int fieldSize = EstimateInheritedFieldSize(inherited);
                if (fieldSize > 0)
                {
                    syncedFields.Add((inherited.Name, fieldSize));
                    totalBytes += fieldSize;
                }
            }

            // Check if over limit
            if (totalBytes > CONTINUOUS_SYNC_LIMIT)
            {
                Location location = GetTypeLocation(type);

                string fieldBreakdown = GetFieldBreakdown(syncedFields);

                diagnostics.Add(AnalyzerDiagnostic.Warning(
                    location,
                    $"Behaviour '{type.Name}' has approximately {totalBytes} bytes of synced data. " +
                    $"Continuous sync mode is limited to ~{CONTINUOUS_SYNC_LIMIT} bytes. " +
                    $"Synced fields: {fieldBreakdown}",
                    AnalyzerId,
                    AnalyzerId,
                    "Consider using Manual sync mode ([UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]) or reducing synced fields."
                ));
            }
            else if (totalBytes > CONTINUOUS_SYNC_LIMIT * WARNING_THRESHOLD)
            {
                // Near the limit - log a softer warning
                Location location = GetTypeLocation(type);

                diagnostics.Add(AnalyzerDiagnostic.Warning(
                    location,
                    $"Behaviour '{type.Name}' has approximately {totalBytes} bytes of synced data, " +
                    $"approaching the ~{CONTINUOUS_SYNC_LIMIT} byte continuous sync limit. " +
                    "Consider using Manual sync mode if you need to add more synced fields.",
                    AnalyzerId,
                    AnalyzerId
                ));
            }

            return diagnostics;
        }

        /// <summary>
        /// Estimates the byte size of a field.
        /// </summary>
        private int EstimateFieldSize(FieldSymbol field, BindContext context)
        {
            TypeSymbol fieldType = field.Type;

            // Handle arrays
            if (fieldType.IsArray)
            {
                TypeSymbol elementType = fieldType.ElementType;
                int elementSize = GetTypeSize(elementType);

                // Try to get array size from initializer
                if (field.InitializerSyntax != null)
                {
                    int arrayLength = TryGetArrayLength(field.InitializerSyntax.ToString());
                    if (arrayLength > 0)
                    {
                        return elementSize * arrayLength;
                    }
                }

                // Default to a conservative estimate for unknown array sizes
                return elementSize * 16; // Assume 16 elements as a rough estimate
            }

            // Handle strings (variable length, estimate)
            if (fieldType.RoslynSymbol.SpecialType == SpecialType.System_String)
            {
                return ESTIMATED_STRING_BYTES;
            }

            return GetTypeSize(fieldType);
        }

        /// <summary>
        /// The same estimate as <see cref="EstimateFieldSize"/>, for a field declared on a base class and so
        /// known here only as a Roslyn symbol.
        /// </summary>
        private int EstimateInheritedFieldSize(IFieldSymbol field)
        {
            if (field.Type is IArrayTypeSymbol arrayType)
            {
                int elementSize = GetTypeSize(arrayType.ElementType);

                var declarator = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax()
                    as Microsoft.CodeAnalysis.CSharp.Syntax.VariableDeclaratorSyntax;
                string initializer = declarator?.Initializer?.Value?.ToString();
                int arrayLength = initializer != null ? TryGetArrayLength(initializer) : -1;

                return elementSize * (arrayLength > 0 ? arrayLength : 16);
            }

            if (field.Type.SpecialType == SpecialType.System_String)
                return ESTIMATED_STRING_BYTES;

            return GetTypeSize(field.Type);
        }

        /// <summary>
        /// Gets the size of a type in bytes.
        /// </summary>
        private int GetTypeSize(TypeSymbol type)
        {
            return GetTypeSize(type.RoslynSymbol);
        }

        private int GetTypeSize(ITypeSymbol type)
        {
            // The CLR name, not ToString(). ToString() renders C# keywords -- 'int', 'bool', 'double' -- so
            // against a table keyed "System.Int32" it never matched a primitive, and every one of them fell
            // through to the 4-byte guess at the bottom: a bool counted four times over, a long and a
            // double at half their size.
            string typeName = TypeSymbol.GetFullTypeName(type);

            if (TypeSizes.TryGetValue(typeName, out int size))
            {
                return size;
            }

            // Check for special types
            if (type.SpecialType == SpecialType.System_String)
            {
                return ESTIMATED_STRING_BYTES;
            }

            // Unknown type - make a conservative estimate
            return 4;
        }

        /// <summary>
        /// Tries to parse array length from an initializer expression.
        /// </summary>
        private int TryGetArrayLength(string initializer)
        {
            // Try to match patterns like "new int[16]" or "new int[] { 1, 2, 3 }"
            initializer = initializer.Trim();

            // Pattern: new Type[N]
            int bracketStart = initializer.IndexOf('[');
            int bracketEnd = initializer.IndexOf(']');

            if (bracketStart >= 0 && bracketEnd > bracketStart)
            {
                string sizeStr = initializer.Substring(bracketStart + 1, bracketEnd - bracketStart - 1).Trim();

                if (int.TryParse(sizeStr, out int size))
                {
                    return size;
                }
            }

            // Pattern: new Type[] { elem, elem, ... }
            int braceStart = initializer.IndexOf('{');
            int braceEnd = initializer.LastIndexOf('}');

            if (braceStart >= 0 && braceEnd > braceStart)
            {
                string elementsStr = initializer.Substring(braceStart + 1, braceEnd - braceStart - 1);
                if (string.IsNullOrWhiteSpace(elementsStr))
                    return 0;

                // Count comma-separated elements (rough estimate)
                return elementsStr.Split(',').Length;
            }

            return -1; // Unknown
        }

        /// <summary>
        /// Gets the Roslyn Location for a type symbol.
        /// </summary>
        private Location GetTypeLocation(TypeSymbol type)
        {
            var declaringSyntaxRefs = type.RoslynSymbol.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs.Length > 0)
            {
                return declaringSyntaxRefs.First().GetSyntax().GetLocation();
            }

            var locations = type.RoslynSymbol.Locations;
            if (locations.Length > 0)
            {
                return locations.First();
            }

            return Location.None;
        }

        /// <summary>
        /// Creates a breakdown string of synced fields and their sizes.
        /// </summary>
        private string GetFieldBreakdown(List<(string name, int size)> fields)
        {
            var parts = new List<string>();

            foreach (var (name, size) in fields.OrderByDescending(f => f.size).Take(5))
            {
                parts.Add($"{name}({size}B)");
            }

            if (fields.Count > 5)
            {
                parts.Add($"... and {fields.Count - 5} more");
            }

            return string.Join(", ", parts);
        }
    }
}
