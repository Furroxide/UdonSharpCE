using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp;
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
    /// </summary>
    /// <example>
    /// // Warning - approximately 256 bytes of sync data
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
    /// public class LargeSync : UdonSharpBehaviour
    /// {
    ///     [UdonSynced] private Vector3[] positions = new Vector3[20]; // CE0003
    /// }
    ///
    /// // Better - use Manual sync for large payloads
    /// [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    /// public class LargeSync : UdonSharpBehaviour
    /// {
    ///     [UdonSynced] private Vector3[] positions = new Vector3[20];
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
        private const int CONTINUOUS_SYNC_LIMIT = 200;

        /// <summary>
        /// Warning threshold (percentage of limit).
        /// </summary>
        private const float WARNING_THRESHOLD = 0.8f;

        /// <summary>
        /// Type sizes for common types in bytes.
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

            // Check if the type has Continuous sync mode
            var syncModeAttr = type.GetAttribute<UdonBehaviourSyncModeAttribute>();
            if (syncModeAttr == null || syncModeAttr.behaviourSyncMode != BehaviourSyncMode.Continuous)
            {
                // Not Continuous mode, skip analysis
                return diagnostics;
            }

            // Calculate total sync payload size
            int totalBytes = 0;
            var syncedFields = new List<(FieldSymbol field, int size)>();

            if (type.FieldSymbols.IsDefaultOrEmpty)
                return diagnostics;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                if (!field.IsSynced)
                    continue;

                int fieldSize = EstimateFieldSize(field, context);
                if (fieldSize > 0)
                {
                    syncedFields.Add((field, fieldSize));
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
                return 32; // Estimate average string length
            }

            return GetTypeSize(fieldType);
        }

        /// <summary>
        /// Gets the size of a type in bytes.
        /// </summary>
        private int GetTypeSize(TypeSymbol type)
        {
            string typeName = type.RoslynSymbol.ToString();

            if (TypeSizes.TryGetValue(typeName, out int size))
            {
                return size;
            }

            // Check for special types
            if (type.RoslynSymbol.SpecialType == SpecialType.System_String)
            {
                return 32; // Estimate
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
        private string GetFieldBreakdown(List<(FieldSymbol field, int size)> fields)
        {
            var parts = new List<string>();

            foreach (var (field, size) in fields.OrderByDescending(f => f.size).Take(5))
            {
                parts.Add($"{field.Name}({size}B)");
            }

            if (fields.Count > 5)
            {
                parts.Add($"... and {fields.Count - 5} more");
            }

            return string.Join(", ", parts);
        }
    }
}
