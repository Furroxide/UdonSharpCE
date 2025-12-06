using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp.CE.Persistence;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using UnityEngine;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Warns when classes marked with [PlayerData] approach or exceed the 100KB quota.
    ///
    /// VRChat limits PlayerData storage to 100KB per player. This analyzer estimates
    /// the serialized size of data models at compile time to catch quota issues early.
    /// </summary>
    /// <example>
    /// // Warning - model approaches 100KB limit
    /// [PlayerData("large_save")]
    /// public class LargeSaveData
    /// {
    ///     [PersistKey("items")] public int[] inventory = new int[10000]; // CE0030
    /// }
    ///
    /// // Error - model exceeds 100KB limit
    /// [PlayerData("huge_save")]
    /// public class HugeSaveData
    /// {
    ///     [PersistKey("data")] public int[] data = new int[30000]; // CE0030
    /// }
    /// </example>
    internal class PlayerDataSizeAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0030";
        public string AnalyzerName => "PlayerData Size Estimation";
        public string Description => "Warns when [PlayerData] models approach the 100KB quota.";
        public bool IsEnabledByDefault => true;

        /// <summary>
        /// VRChat PlayerData quota limit in bytes (100KB).
        /// </summary>
        private const int PLAYER_DATA_QUOTA = 102400;

        /// <summary>
        /// Warning threshold (80% of quota).
        /// </summary>
        private const float WARNING_THRESHOLD = 0.8f;

        /// <summary>
        /// Error threshold (95% of quota - likely to fail in practice).
        /// </summary>
        private const float ERROR_THRESHOLD = 0.95f;

        /// <summary>
        /// JSON overhead estimate for object structure.
        /// </summary>
        private const int JSON_OBJECT_OVERHEAD = 2; // {}

        /// <summary>
        /// JSON overhead estimate for each key-value pair.
        /// </summary>
        private const int JSON_KEY_OVERHEAD = 4; // "":,

        /// <summary>
        /// Type sizes for common types in bytes (JSON serialized estimate).
        /// </summary>
        private static readonly Dictionary<string, int> TypeSizes = new Dictionary<string, int>
        {
            { "System.Boolean", 5 },      // "true" or "false"
            { "System.Byte", 3 },         // 0-255
            { "System.SByte", 4 },        // -128-127
            { "System.Int16", 6 },        // -32768-32767
            { "System.UInt16", 5 },       // 0-65535
            { "System.Int32", 11 },       // -2147483648 max
            { "System.UInt32", 10 },      // 4294967295 max
            { "System.Int64", 20 },       // 64-bit max
            { "System.UInt64", 20 },      // 64-bit max
            { "System.Single", 15 },      // Float with decimal
            { "System.Double", 24 },      // Double with decimal
            { "System.Char", 4 },         // "c" with quotes
            { "UnityEngine.Vector2", 40 },     // {"x":0.0,"y":0.0}
            { "UnityEngine.Vector3", 55 },     // {"x":0.0,"y":0.0,"z":0.0}
            { "UnityEngine.Vector4", 70 },     // {"x":0.0,"y":0.0,"z":0.0,"w":0.0}
            { "UnityEngine.Quaternion", 70 },  // {"x":0.0,"y":0.0,"z":0.0,"w":0.0}
            { "UnityEngine.Color", 70 },       // {"r":0.0,"g":0.0,"b":0.0,"a":0.0}
            { "UnityEngine.Color32", 40 },     // {"r":0,"g":0,"b":0,"a":0}
            { "UnityEngine.Vector2Int", 30 },  // {"x":0,"y":0}
            { "UnityEngine.Vector3Int", 40 },  // {"x":0,"y":0,"z":0}
        };

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            // Check if the type has [PlayerData] attribute
            if (!HasPlayerDataAttribute(type))
            {
                return diagnostics;
            }

            // Calculate estimated serialized size
            int totalBytes = JSON_OBJECT_OVERHEAD; // Base object overhead
            var fieldEstimates = new List<(FieldSymbol field, string key, int size)>();

            if (type.FieldSymbols == null)
                return diagnostics;

            foreach (FieldSymbol field in type.FieldSymbols)
            {
                // Check for [PersistKey] attribute
                string persistKey = GetPersistKey(field);
                if (string.IsNullOrEmpty(persistKey))
                    continue;

                int fieldSize = EstimateFieldSize(field, context);
                int keySize = persistKey.Length + JSON_KEY_OVERHEAD; // Key name + overhead

                fieldEstimates.Add((field, persistKey, fieldSize + keySize));
                totalBytes += fieldSize + keySize;
            }

            // Add version and model key metadata overhead (~50 bytes estimate)
            totalBytes += 50;

            // Check thresholds and report diagnostics
            Location location = GetTypeLocation(type);
            string modelKey = GetPlayerDataKey(type);

            if (totalBytes > PLAYER_DATA_QUOTA * ERROR_THRESHOLD)
            {
                string breakdown = GetFieldBreakdown(fieldEstimates);

                diagnostics.Add(AnalyzerDiagnostic.Error(
                    location,
                    $"[PlayerData(\"{modelKey}\")] model '{type.Name}' has estimated size of ~{totalBytes:N0} bytes, " +
                    $"which exceeds the 100KB ({PLAYER_DATA_QUOTA:N0} bytes) PlayerData limit. " +
                    $"Largest fields: {breakdown}",
                    AnalyzerId,
                    AnalyzerId,
                    "Reduce the amount of data being stored. Consider removing large arrays or storing data more efficiently."
                ));
            }
            else if (totalBytes > PLAYER_DATA_QUOTA * WARNING_THRESHOLD)
            {
                string breakdown = GetFieldBreakdown(fieldEstimates);

                diagnostics.Add(AnalyzerDiagnostic.Warning(
                    location,
                    $"[PlayerData(\"{modelKey}\")] model '{type.Name}' has estimated size of ~{totalBytes:N0} bytes " +
                    $"({(float)totalBytes / PLAYER_DATA_QUOTA:P0} of 100KB limit). " +
                    $"Largest fields: {breakdown}",
                    AnalyzerId,
                    AnalyzerId,
                    "Consider reducing data size to allow room for growth."
                ));
            }
            else if (fieldEstimates.Count > 0)
            {
                // Info-level message for valid models
                diagnostics.Add(AnalyzerDiagnostic.Info(
                    location,
                    $"[PlayerData(\"{modelKey}\")] model '{type.Name}' has estimated size of ~{totalBytes:N0} bytes " +
                    $"({(float)totalBytes / PLAYER_DATA_QUOTA:P0} of 100KB limit).",
                    AnalyzerId,
                    AnalyzerId
                ));
            }

            return diagnostics;
        }

        /// <summary>
        /// Checks if a type has the [PlayerData] attribute.
        /// </summary>
        private bool HasPlayerDataAttribute(TypeSymbol type)
        {
            if (type.RoslynSymbol == null)
                return false;

            foreach (var attr in type.RoslynSymbol.GetAttributes())
            {
                string attrName = attr.AttributeClass?.Name ?? "";
                if (attrName == "PlayerDataAttribute" || attrName == "PlayerData")
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gets the key from the [PlayerData] attribute.
        /// </summary>
        private string GetPlayerDataKey(TypeSymbol type)
        {
            if (type.RoslynSymbol == null)
                return type.Name;

            foreach (var attr in type.RoslynSymbol.GetAttributes())
            {
                string attrName = attr.AttributeClass?.Name ?? "";
                if (attrName == "PlayerDataAttribute" || attrName == "PlayerData")
                {
                    if (attr.ConstructorArguments.Length > 0)
                    {
                        return attr.ConstructorArguments[0].Value?.ToString() ?? type.Name;
                    }
                }
            }

            return type.Name;
        }

        /// <summary>
        /// Gets the persist key from the [PersistKey] attribute on a field.
        /// </summary>
        private string GetPersistKey(FieldSymbol field)
        {
            if (field.RoslynSymbol == null)
                return null;

            foreach (var attr in field.RoslynSymbol.GetAttributes())
            {
                string attrName = attr.AttributeClass?.Name ?? "";
                if (attrName == "PersistKeyAttribute" || attrName == "PersistKey")
                {
                    if (attr.ConstructorArguments.Length > 0)
                    {
                        return attr.ConstructorArguments[0].Value?.ToString();
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Estimates the serialized size of a field in bytes.
        /// </summary>
        private int EstimateFieldSize(FieldSymbol field, BindContext context)
        {
            TypeSymbol fieldType = field.Type;

            // Handle arrays
            if (fieldType.IsArray)
            {
                TypeSymbol elementType = fieldType.ElementType;
                int elementSize = GetTypeSize(elementType);

                // Add JSON array overhead: [, ], and commas
                int arrayOverhead = 2; // []

                // Try to get array size from initializer
                if (field.InitializerSyntax != null)
                {
                    int arrayLength = TryGetArrayLength(field.InitializerSyntax.ToString());
                    if (arrayLength > 0)
                    {
                        // Add comma separators (length - 1 commas)
                        return arrayOverhead + (elementSize * arrayLength) + Math.Max(0, arrayLength - 1);
                    }
                }

                // Default to a conservative estimate for unknown array sizes
                int defaultLength = 16;
                return arrayOverhead + (elementSize * defaultLength) + Math.Max(0, defaultLength - 1);
            }

            // Handle strings (variable length, estimate based on common usage)
            if (fieldType.RoslynSymbol?.SpecialType == SpecialType.System_String)
            {
                // Estimate: average string length of 20 chars + quotes
                return 22;
            }

            return GetTypeSize(fieldType);
        }

        /// <summary>
        /// Gets the estimated JSON serialized size of a type in bytes.
        /// </summary>
        private int GetTypeSize(TypeSymbol type)
        {
            if (type?.RoslynSymbol == null)
                return 4;

            string typeName = type.RoslynSymbol.ToString();

            if (TypeSizes.TryGetValue(typeName, out int size))
            {
                return size;
            }

            // Check for special types
            if (type.RoslynSymbol.SpecialType == SpecialType.System_String)
            {
                return 22; // Average string estimate
            }

            // Enums serialize as numbers
            if (type.RoslynSymbol.TypeKind == TypeKind.Enum)
            {
                return 5; // Small integer
            }

            // Unknown type - make a conservative estimate
            return 10;
        }

        /// <summary>
        /// Tries to parse array length from an initializer expression.
        /// </summary>
        private int TryGetArrayLength(string initializer)
        {
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

                return elementsStr.Split(',').Length;
            }

            return -1;
        }

        /// <summary>
        /// Gets the Roslyn Location for a type symbol.
        /// </summary>
        private Location GetTypeLocation(TypeSymbol type)
        {
            var declaringSyntaxRefs = type.RoslynSymbol?.DeclaringSyntaxReferences;
            if (declaringSyntaxRefs != null && declaringSyntaxRefs.Length > 0)
            {
                return declaringSyntaxRefs.First().GetSyntax().GetLocation();
            }

            var locations = type.RoslynSymbol?.Locations;
            if (locations != null && locations.Length > 0)
            {
                return locations.First();
            }

            return Location.None;
        }

        /// <summary>
        /// Creates a breakdown string of fields and their sizes.
        /// </summary>
        private string GetFieldBreakdown(List<(FieldSymbol field, string key, int size)> fields)
        {
            var parts = new List<string>();

            foreach (var (field, key, size) in fields.OrderByDescending(f => f.size).Take(5))
            {
                parts.Add($"{key}({size:N0}B)");
            }

            if (fields.Count > 5)
            {
                parts.Add($"... and {fields.Count - 5} more");
            }

            return string.Join(", ", parts);
        }
    }
}
