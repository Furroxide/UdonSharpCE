using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.CE.Persistence;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;

namespace UdonSharp.CE.Editor.Analyzers
{
    /// <summary>
    /// Warns when a class marked [PlayerData] has a worst-case JSON size near or over the 100KB quota.
    ///
    /// VRChat limits PlayerData storage to 100KB per player, and CEPersistence saves each model as one
    /// JSON string under that limit. A model that cannot fit fails at Save, at runtime, on the players
    /// with the most progress -- which is the last place anyone wants to find out.
    ///
    /// This check is driven by the compilation, not by the type it is handed. Analyzers are invoked once
    /// per UdonSharpBehaviour, and a [PlayerData] model is a plain class, never a behaviour, so a check
    /// that waited to be handed a model was never handed one: it had been registered, documented and
    /// silent since it was written. It now finds every model in the compilation the first time it is
    /// called for that compilation and reports on all of them then.
    ///
    /// Three limits on what the figure means, each of them stated in the diagnostic rather than left to
    /// be assumed:
    ///
    /// <list type="bullet">
    /// <item><description>It is a worst case. Every number is counted at its longest spelling ('-2147483648'
    /// for an int), because the value that will be stored is not known here.</description></item>
    /// <item><description>A field whose length cannot be read is left out, and named. An array is sized by
    /// its [MaxLength] or by a constant length in its initializer; a string only by [MaxLength]. Anything
    /// else -- an array created at runtime, an unbounded string, a DataList -- used to be counted as
    /// sixteen elements or twenty characters, and a guess reported as a measurement is worse than no
    /// number.</description></item>
    /// <item><description>It never fails a build. The estimate can be over the quota for a model whose
    /// real payload is a tenth of it, so the most it may do is warn.</description></item>
    /// </list>
    /// </summary>
    /// <example>
    /// // Warning - about 120,000 bytes of JSON in the worst case, over the 102,400 byte quota
    /// [PlayerData("large_save")]
    /// public class LargeSaveData
    /// {
    ///     [PersistKey("items")] public int[] inventory = new int[10000]; // CE0030
    /// }
    ///
    /// // Not counted, and the report says so - the length is only known at runtime
    /// [PlayerData("log")]
    /// public class LogData
    /// {
    ///     [PersistKey("lines")] public string[] lines;
    /// }
    /// </example>
    internal class PlayerDataSizeAnalyzer : ICompileTimeAnalyzer
    {
        public string AnalyzerId => "CE0030";
        public string AnalyzerName => "PlayerData Size Estimation";
        public string Description => "Warns when a [PlayerData] model's worst-case JSON size approaches the 100KB quota.";
        public bool IsEnabledByDefault => true;

        private const string PlayerDataAttributeName = "PlayerDataAttribute";
        private const string PersistKeyAttributeName = "PersistKeyAttribute";
        private const string PersistIgnoreAttributeName = "PersistIgnoreAttribute";
        private const string MaxLengthAttributeName = "MaxLengthAttribute";

        private static readonly object ClaimLock = new object();
        private static readonly object ClaimMarker = new object();

        /// <summary>
        /// Compilations whose models have already been reported on. Weak, so a finished compile is not
        /// kept alive by the fact that it was once analyzed.
        /// </summary>
        private static readonly ConditionalWeakTable<CSharpCompilation, object> AnalyzedCompilations =
            new ConditionalWeakTable<CSharpCompilation, object>();

        public IEnumerable<AnalyzerDiagnostic> Analyze(
            TypeSymbol type,
            BindContext context,
            CompilationContext compilationContext)
        {
            var diagnostics = new List<AnalyzerDiagnostic>();

            CSharpCompilation compilation = compilationContext?.RoslynCompilation;

            // The models belong to the compilation, not to the behaviour this call happens to be about.
            // Reporting them once per behaviour would repeat every warning as many times as the project
            // has behaviours.
            if (compilation == null || !TryClaim(compilation))
                return diagnostics;

            var models = new List<INamedTypeSymbol>();
            CollectPlayerDataModels(compilation.Assembly.GlobalNamespace, models);

            foreach (INamedTypeSymbol model in models)
            {
                AnalyzerDiagnostic diagnostic = AnalyzeModel(model, compilationContext);

                if (diagnostic != null)
                    diagnostics.Add(diagnostic);
            }

            return diagnostics;
        }

        private static bool TryClaim(CSharpCompilation compilation)
        {
            lock (ClaimLock)
            {
                if (AnalyzedCompilations.TryGetValue(compilation, out _))
                    return false;

                AnalyzedCompilations.Add(compilation, ClaimMarker);
                return true;
            }
        }

        private static void CollectPlayerDataModels(INamespaceSymbol namespaceSymbol, List<INamedTypeSymbol> models)
        {
            foreach (INamedTypeSymbol type in namespaceSymbol.GetTypeMembers())
                CollectPlayerDataModels(type, models);

            foreach (INamespaceSymbol child in namespaceSymbol.GetNamespaceMembers())
                CollectPlayerDataModels(child, models);
        }

        private static void CollectPlayerDataModels(INamedTypeSymbol type, List<INamedTypeSymbol> models)
        {
            if (FindAttribute(type, PlayerDataAttributeName) != null)
                models.Add(type);

            foreach (INamedTypeSymbol nested in type.GetTypeMembers())
                CollectPlayerDataModels(nested, models);
        }

        /// <summary>
        /// Sizes one model, or returns null when it is comfortably inside the quota or nothing in it
        /// could be sized.
        /// </summary>
        private AnalyzerDiagnostic AnalyzeModel(INamedTypeSymbol model, CompilationContext compilationContext)
        {
            long fieldBytes = 0;
            var sizedFields = new List<KeyValuePair<string, long>>();
            var unsizedFields = new List<string>();

            foreach (ISymbol member in model.GetMembers())
            {
                if (!(member is IFieldSymbol field) || field.IsStatic || field.IsConst || field.IsImplicitlyDeclared)
                    continue;

                if (FindAttribute(field, PersistIgnoreAttributeName) != null)
                    continue;

                string persistKey = GetPersistKey(field);
                if (string.IsNullOrEmpty(persistKey))
                    continue;

                long valueBytes = EstimateValueBytes(field, compilationContext);

                if (valueBytes < 0)
                {
                    unsizedFields.Add(field.Name);
                    continue;
                }

                long bytes = PlayerDataSizeEstimate.GetFieldBytes(persistKey, valueBytes);

                fieldBytes += bytes;
                sizedFields.Add(new KeyValuePair<string, long>(persistKey, bytes));
            }

            if (sizedFields.Count == 0)
                return null;

            long totalBytes = PlayerDataSizeEstimate.GetModelBytes(fieldBytes);

            if (!PlayerDataSizeEstimate.IsNearQuota(totalBytes))
                return null;

            return AnalyzerDiagnostic.Warning(
                GetTypeLocation(model),
                PlayerDataSizeEstimate.DescribeVerdict(model.Name, totalBytes, sizedFields.Count, unsizedFields) +
                " Largest fields: " + GetFieldBreakdown(sizedFields) + ".",
                AnalyzerId,
                AnalyzerId,
                "Store less per player: shorten the largest arrays, or lower their [MaxLength] if the declared " +
                "length is only a ceiling the data never reaches.");
        }

        /// <summary>
        /// The worst-case JSON size of one field's value, or -1 when its length is not known here.
        /// </summary>
        internal static long EstimateValueBytes(IFieldSymbol field, CompilationContext compilationContext)
        {
            ITypeSymbol fieldType = field.Type;
            int? maxLength = GetMaxLength(field);

            if (fieldType is IArrayTypeSymbol arrayType)
            {
                if (arrayType.Rank != 1)
                    return -1;

                int elementBytes = GetScalarBytes(arrayType.ElementType);
                if (elementBytes < 0)
                    return -1;

                // [MaxLength] is the ceiling CEPersistence validates against, so it wins over whatever
                // length the field happens to be declared with.
                int? length = maxLength ?? GetDeclaredArrayLength(field, compilationContext);

                return length.HasValue
                    ? PlayerDataSizeEstimate.GetArrayBytes(elementBytes, length.Value)
                    : -1;
            }

            if (fieldType.SpecialType == SpecialType.System_String)
                return maxLength.HasValue ? PlayerDataSizeEstimate.GetStringBytes(maxLength.Value) : -1;

            return GetScalarBytes(fieldType);
        }

        private static int GetScalarBytes(ITypeSymbol type)
        {
            if (type == null)
                return -1;

            // An enum is stored as its underlying number.
            if (type.TypeKind == TypeKind.Enum &&
                type is INamedTypeSymbol enumType &&
                enumType.EnumUnderlyingType != null)
            {
                type = enumType.EnumUnderlyingType;
            }

            // The CLR name, not ToString(): ToString() renders 'int' and 'float', which never matched a
            // table keyed "System.Int32" and sent every primitive to the unknown-type guess.
            return PlayerDataSizeEstimate.GetWorstCaseValueBytes(TypeSymbol.GetFullTypeName(type));
        }

        /// <summary>
        /// The array length a field's initializer fixes, or null when it fixes none.
        ///
        /// The size expression is asked of the semantic model as a constant, so 'new int[MaxSlots]' with
        /// 'const int MaxSlots = 64' is read as 64. Reading only a literal turned that spelling -- the
        /// usual one -- into "unknown", and unknown used to mean sixteen.
        /// </summary>
        private static int? GetDeclaredArrayLength(IFieldSymbol field, CompilationContext compilationContext)
        {
            var declarator = field.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as VariableDeclaratorSyntax;
            ExpressionSyntax initializer = declarator?.Initializer?.Value;

            if (initializer == null)
                return null;

            return PlayerDataSizeEstimate.ReadArrayLength(
                initializer,
                CENetworkAnalysis.GetSemanticModel(compilationContext, declarator));
        }

        private static string GetPersistKey(IFieldSymbol field)
        {
            AttributeData attribute = FindAttribute(field, PersistKeyAttributeName);

            if (attribute == null || attribute.ConstructorArguments.Length == 0)
                return null;

            return attribute.ConstructorArguments[0].Value as string;
        }

        private static int? GetMaxLength(IFieldSymbol field)
        {
            AttributeData attribute = FindAttribute(field, MaxLengthAttributeName);

            if (attribute == null || attribute.ConstructorArguments.Length == 0)
                return null;

            return attribute.ConstructorArguments[0].Value is int length && length >= 0
                ? length
                : (int?)null;
        }

        private static AttributeData FindAttribute(ISymbol symbol, string attributeTypeName)
        {
            foreach (AttributeData attribute in symbol.GetAttributes())
            {
                if (string.Equals(attribute.AttributeClass?.Name, attributeTypeName, StringComparison.Ordinal))
                    return attribute;
            }

            return null;
        }

        private static Location GetTypeLocation(INamedTypeSymbol type)
        {
            if (type.DeclaringSyntaxReferences.Length > 0)
                return type.DeclaringSyntaxReferences[0].GetSyntax().GetLocation();

            return type.Locations.Length > 0 ? type.Locations[0] : Location.None;
        }

        private static string GetFieldBreakdown(List<KeyValuePair<string, long>> fields)
        {
            var parts = new List<string>();

            foreach (KeyValuePair<string, long> field in fields.OrderByDescending(f => f.Value).Take(5))
                parts.Add($"{field.Key} ({field.Value:N0} B)");

            if (fields.Count > 5)
                parts.Add($"and {fields.Count - 5} more");

            return string.Join(", ", parts);
        }
    }

    /// <summary>
    /// The arithmetic behind CE0030 and the World Validator's Persistence Size check, in one place.
    ///
    /// The two used to carry a table each and disagreed about almost every entry -- a Vector3 was 55
    /// bytes at compile time and 70 in the validator, an unsized array sixteen elements in one and a
    /// hundred in the other -- so the same model could be fine in the console and failing in the window.
    /// The per-type sizes here are the ones CEPersistence's own runtime SizeEstimator uses, except that a
    /// long or ulong is counted as the quoted string the generated converter writes it as.
    /// </summary>
    internal static class PlayerDataSizeEstimate
    {
        /// <summary>VRChat's PlayerData quota in bytes, shared by every model a player saves.</summary>
        public const int QuotaBytes = CEPersistence.PLAYER_DATA_QUOTA;

        /// <summary>The share of the quota past which a model is worth a warning.</summary>
        public const float WarningThreshold = 0.8f;

        private const int ObjectOverheadBytes = 2;     // {}
        private const int KeyOverheadBytes = 4;        // "":,
        private const int ArrayOverheadBytes = 2;      // []
        private const int StringOverheadBytes = 2;     // ""

        /// <summary>The version entry CEPersistence writes next to the model's own fields.</summary>
        private const int ModelBookkeepingBytes = 50;

        private static readonly Dictionary<string, int> WorstCaseValueBytes = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            { "System.Boolean", 5 },           // false
            { "System.Byte", 4 },
            { "System.SByte", 4 },             // -128
            { "System.Int16", 6 },             // -32768
            { "System.UInt16", 6 },
            { "System.Int32", 11 },            // -2147483648
            { "System.UInt32", 11 },
            { "System.Int64", 22 },            // "-9223372036854775808" -- stored as a string to keep all 64 bits
            { "System.UInt64", 22 },           // "18446744073709551615"
            { "System.Single", 15 },           // -3.402823E+38
            { "System.Double", 24 },           // -1.7976931348623157E+308
            { "UnityEngine.Vector2", 50 },
            { "UnityEngine.Vector3", 70 },
            { "UnityEngine.Vector4", 90 },
            { "UnityEngine.Quaternion", 90 },
            { "UnityEngine.Color", 90 },
            { "UnityEngine.Color32", 40 },
        };

        /// <summary>
        /// The longest JSON spelling of one value of the type, or -1 when the type has no fixed size.
        /// </summary>
        public static int GetWorstCaseValueBytes(string clrTypeName)
        {
            return clrTypeName != null && WorstCaseValueBytes.TryGetValue(clrTypeName, out int bytes)
                ? bytes
                : -1;
        }

        public static long GetFieldBytes(string persistKey, long valueBytes)
        {
            return (persistKey?.Length ?? 0) + KeyOverheadBytes + valueBytes;
        }

        public static long GetArrayBytes(int elementBytes, int length)
        {
            // Elements, plus the commas between them.
            return ArrayOverheadBytes + (long)elementBytes * length + Math.Max(0, length - 1);
        }

        public static long GetStringBytes(int maxCharacters)
        {
            return StringOverheadBytes + (long)maxCharacters;
        }

        public static long GetModelBytes(long fieldBytes)
        {
            return ObjectOverheadBytes + ModelBookkeepingBytes + fieldBytes;
        }

        public static bool IsOverQuota(long totalBytes)
        {
            return totalBytes > QuotaBytes;
        }

        public static bool IsNearQuota(long totalBytes)
        {
            return totalBytes > QuotaBytes * WarningThreshold;
        }

        /// <summary>
        /// The sentence both reports open with. It says what was counted and what was not, because the
        /// number is only as good as the fields that went into it.
        /// </summary>
        public static string DescribeVerdict(string modelName, long totalBytes, int sizedFieldCount, IList<string> unsizedFields)
        {
            string verdict = IsOverQuota(totalBytes)
                ? $"which is over the {QuotaBytes:N0}-byte PlayerData quota"
                : $"{(double)totalBytes / QuotaBytes:P0} of the {QuotaBytes:N0}-byte PlayerData quota";

            string message =
                $"[PlayerData] model '{modelName}' has a worst-case JSON size of about {totalBytes:N0} bytes, {verdict}. " +
                $"That counts {sizedFieldCount} [PersistKey] field(s), with every number at its longest spelling, arrays at " +
                "their [MaxLength] or declared length, and strings at their [MaxLength].";

            if (unsizedFields != null && unsizedFields.Count > 0)
            {
                message +=
                    $" {unsizedFields.Count} field(s) have no length that can be read ahead of time " +
                    $"({string.Join(", ", unsizedFields)}) and are not counted, so the real payload is larger.";
            }

            return message + " The quota is shared by everything a player saves in this world.";
        }

        /// <summary>
        /// The length an array initializer fixes: 'new T[N]' with a constant N, 'new T[] { ... }',
        /// 'new[] { ... }' or a bare '{ ... }'. Null for anything else.
        /// </summary>
        public static int? ReadArrayLength(ExpressionSyntax initializer, SemanticModel model)
        {
            switch (initializer)
            {
                case ArrayCreationExpressionSyntax creation:
                    if (creation.Initializer != null)
                        return creation.Initializer.Expressions.Count;

                    if (creation.Type.RankSpecifiers.Count != 1 || creation.Type.RankSpecifiers[0].Sizes.Count != 1)
                        return null;

                    return ReadConstantLength(creation.Type.RankSpecifiers[0].Sizes[0], model);

                case ImplicitArrayCreationExpressionSyntax implicitCreation:
                    return implicitCreation.Initializer?.Expressions.Count;

                case InitializerExpressionSyntax bareInitializer:
                    return bareInitializer.Expressions.Count;

                default:
                    return null;
            }
        }

        private static int? ReadConstantLength(ExpressionSyntax size, SemanticModel model)
        {
            if (size == null || size is OmittedArraySizeExpressionSyntax)
                return null;

            object value = null;

            if (model != null)
            {
                Optional<object> constant = model.GetConstantValue(size);

                if (constant.HasValue)
                    value = constant.Value;
            }
            else if (size is LiteralExpressionSyntax literal)
            {
                value = literal.Token.Value;
            }

            return value is int length && length >= 0 ? length : (int?)null;
        }
    }
}
