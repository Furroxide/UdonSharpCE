using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp;
using UdonSharp.CE.Editor.DevTools.Analysis;

namespace UdonSharp.CE.Editor.DevTools.Validation.Validators
{
    /// <summary>
    /// Validates sync mode appropriateness.
    /// Detects continuous sync on rarely-changed data that should use manual sync.
    /// </summary>
    public class SyncModeValidator : IValidator
    {
        public string Name => "Sync Mode Appropriateness";
        public string Category => "Performance";
        public string Description => "Detects inefficient sync mode usage for rarely-changed data.";
        public bool IsEnabledByDefault => true;

        public IEnumerable<ValidationIssue> Validate(ValidationContext context)
        {
            var issues = new List<ValidationIssue>();
            var candidates = new List<KeyValuePair<Type, List<FieldInfo>>>();

            foreach (var type in context.BehaviourTypes)
            {
                string syncMode = GetSyncMode(type);
                if (syncMode != "Continuous")
                    continue;

                // Get synced fields
                var syncedFields = GetSyncedFields(type);
                if (syncedFields.Count == 0)
                    continue;

                candidates.Add(new KeyValuePair<Type, List<FieldInfo>>(type, syncedFields));
            }

            if (candidates.Count == 0)
                return issues;

            // One parse and one compilation for the whole run rather than one per type; the analyzer is
            // built from the candidate set up front for that reason.
            var writeAnalyzer = new FieldWriteAnalyzer(candidates.Select(candidate => candidate.Key));

            foreach (var candidate in candidates)
            {
                Type type = candidate.Key;
                List<FieldInfo> syncedFields = candidate.Value;

                string modeSource = BehaviourAnalyzer.DescribeSyncModeSource(type);
                string modeNote = modeSource == null
                    ? ""
                    : $" This behaviour is Continuous by default ({modeSource}), so the cost is easy to miss.";

                Dictionary<string, List<string>> writesByField = writeAnalyzer.AnalyzeWrites(type, syncedFields);

                // Analyze each field for write patterns
                foreach (var field in syncedFields)
                {
                    var writeLocations = writesByField[field.Name];

                    // If field is only written in Start/Awake/constructor
                    if (writeLocations.Count > 0 && writeLocations.All(w => IsInitializationMethod(w)))
                    {
                        issues.Add(new ValidationIssue
                        {
                            Severity = ValidationSeverity.Warning,
                            ValidatorName = Name,
                            Message = $"Field '{field.Name}' only set during initialization but uses Continuous sync",
                            Details = $"This field is only written in {string.Join(", ", writeLocations)} but the behaviour uses Continuous sync mode. " +
                                     "Consider using Manual sync mode ([UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]) " +
                                     "and call RequestSerialization() when data changes." + modeNote,
                            RelatedType = type,
                            RelatedField = field,
                            FilePath = context.GetSourceFile(type)
                        });
                    }
                }

                // Check for too many synced fields in continuous mode
                if (syncedFields.Count > 5)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = ValidationSeverity.Info,
                        ValidatorName = Name,
                        Message = $"Behaviour has {syncedFields.Count} synced fields with Continuous sync",
                        Details = "Many fields in Continuous sync can waste bandwidth. " +
                                 "Consider grouping related data or using Manual sync with delta updates." + modeNote,
                        RelatedType = type,
                        FilePath = context.GetSourceFile(type)
                    });
                }
            }

            return issues;
        }

        private string GetSyncMode(Type type)
        {
            // Shared with the bandwidth analyzer so the two tools cannot disagree about what an absent
            // [UdonBehaviourSyncMode] means. It means Any, which resolves to Continuous -- reading it as
            // "None" made this validator skip precisely the behaviours it exists to catch.
            return BehaviourAnalyzer.ResolveSyncMode(type);
        }

        private List<FieldInfo> GetSyncedFields(Type type)
        {
            var fields = new List<FieldInfo>();
            var allFields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            foreach (var field in allFields)
            {
                if (field.GetCustomAttribute<UdonSyncedAttribute>(true) != null)
                    fields.Add(field);
            }

            return fields;
        }

        private bool IsInitializationMethod(string methodName)
        {
            return methodName == "Start" || methodName == "Awake" || methodName == ".ctor" || methodName == "OnEnable";
        }

        /// <summary>
        /// Finds every member that writes a behaviour's synced fields.
        ///
        /// This used to regex the raw .cs text for a hardcoded list of method names, which meant it reported
        /// nothing at all for expression-bodied members, property accessors, local functions, or any handler
        /// outside that list -- and "no writes found" reads exactly like "this field is fine", so the
        /// validator was quietly silent on most real behaviours. A Roslyn walk sees every member, and the
        /// semantic model stops a local variable that happens to share a field's name from being reported as
        /// a write to that field.
        /// </summary>
        private class FieldWriteAnalyzer
        {
#if UNITY_2022_3_OR_NEWER
            private const LanguageVersion SourceLanguageVersion = LanguageVersion.CSharp9;
#else
            private const LanguageVersion SourceLanguageVersion = LanguageVersion.CSharp7_3;
#endif

            private readonly Dictionary<Type, SyntaxTree> _treesByType = new Dictionary<Type, SyntaxTree>();
            private CSharpCompilation _compilation;

            public FieldWriteAnalyzer(IEnumerable<Type> types)
            {
                Dictionary<string, string[]> definesByAssembly = GetDefinesByAssembly();

                foreach (Type type in types)
                {
                    if (_treesByType.ContainsKey(type))
                        continue;

                    string sourceFile = FindSourceFile(type);
                    if (string.IsNullOrEmpty(sourceFile) || !File.Exists(sourceFile))
                        continue;

                    definesByAssembly.TryGetValue(type.Assembly.GetName().Name, out string[] defines);

                    // Parse with the defines Unity actually compiled this assembly with, and with the
                    // language version UdonSharp itself parses these files at. Without the defines every
                    // #if UNITY_EDITOR region is disabled text, and the writes inside it are invisible --
                    // which is how a field written in a gated block looked like a field never written.
                    CSharpParseOptions parseOptions = CSharpParseOptions.Default
                        .WithDocumentationMode(DocumentationMode.None)
                        .WithLanguageVersion(SourceLanguageVersion);

                    if (defines != null)
                        parseOptions = parseOptions.WithPreprocessorSymbols(defines);

                    _treesByType[type] = CSharpSyntaxTree.ParseText(
                        File.ReadAllText(sourceFile),
                        parseOptions,
                        sourceFile);
                }
            }

            public Dictionary<string, List<string>> AnalyzeWrites(Type type, List<FieldInfo> fields)
            {
                var writes = new Dictionary<string, List<string>>();
                foreach (FieldInfo field in fields)
                    writes[field.Name] = new List<string>();

                if (!_treesByType.TryGetValue(type, out SyntaxTree tree))
                    return writes;

                ClassDeclarationSyntax classDeclaration = tree.GetRoot()
                    .DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .FirstOrDefault(declaration => declaration.Identifier.ValueText == type.Name);

                if (classDeclaration == null)
                    return writes;

                SemanticModel model = GetSemanticModel(tree);

                foreach (SyntaxNode node in classDeclaration.DescendantNodes())
                {
                    ExpressionSyntax target = GetWriteTarget(node);
                    if (target == null)
                        continue;

                    string fieldName = ResolveFieldName(target, model, type, writes);
                    if (fieldName == null)
                        continue;

                    Record(writes, fieldName, GetContainingMemberName(node));
                }

                // Field initializers are not assignment expressions, so they need their own pass. They count
                // as ".ctor" writes because they run before Start, which is what makes a field "only set
                // during initialization".
                foreach (VariableDeclaratorSyntax declarator in classDeclaration
                             .DescendantNodes()
                             .OfType<VariableDeclaratorSyntax>())
                {
                    if (declarator.Initializer == null)
                        continue;

                    if (!(declarator.Parent?.Parent is FieldDeclarationSyntax))
                        continue;

                    Record(writes, declarator.Identifier.ValueText, ".ctor");
                }

                return writes;
            }

            private static void Record(Dictionary<string, List<string>> writes, string fieldName, string member)
            {
                if (member == null)
                    return;

                if (writes.TryGetValue(fieldName, out List<string> locations) && !locations.Contains(member))
                    locations.Add(member);
            }

            /// <summary>
            /// The expression a node writes to, or null if the node is not a write.
            /// </summary>
            private static ExpressionSyntax GetWriteTarget(SyntaxNode node)
            {
                if (node is AssignmentExpressionSyntax assignment)
                    return assignment.Left;

                if (node is PrefixUnaryExpressionSyntax prefix &&
                    (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)))
                {
                    return prefix.Operand;
                }

                if (node is PostfixUnaryExpressionSyntax postfix &&
                    (postfix.IsKind(SyntaxKind.PostIncrementExpression) || postfix.IsKind(SyntaxKind.PostDecrementExpression)))
                {
                    return postfix.Operand;
                }

                return null;
            }

            private static string ResolveFieldName(
                ExpressionSyntax target,
                SemanticModel model,
                Type declaringType,
                Dictionary<string, List<string>> tracked)
            {
                while (target is ParenthesizedExpressionSyntax parenthesized)
                    target = parenthesized.Expression;

                if (model != null && model.GetSymbolInfo(target).Symbol is IFieldSymbol fieldSymbol)
                {
                    if (fieldSymbol.ContainingType?.Name != declaringType.Name)
                        return null;

                    return tracked.ContainsKey(fieldSymbol.Name) ? fieldSymbol.Name : null;
                }

                // No semantic model, or an unresolvable expression. Fall back to matching the written name
                // syntactically; over-reporting a shadowed local is better than the previous behaviour of
                // reporting nothing at all.
                string name = null;

                if (target is IdentifierNameSyntax identifier)
                    name = identifier.Identifier.ValueText;
                else if (target is MemberAccessExpressionSyntax member && member.Expression is ThisExpressionSyntax)
                    name = member.Name.Identifier.ValueText;

                return name != null && tracked.ContainsKey(name) ? name : null;
            }

            /// <summary>
            /// The name a write is reported under. A write inside a lambda gets its enclosing member's name
            /// with a suffix, never the bare name: "assigned by a callback created in Start" must not be
            /// mistaken for "assigned during initialization", which is the whole judgement this validator
            /// makes.
            /// </summary>
            private static string GetContainingMemberName(SyntaxNode node)
            {
                bool insideCallback = false;

                for (SyntaxNode current = node; current != null; current = current.Parent)
                {
                    if (current is AnonymousFunctionExpressionSyntax)
                    {
                        insideCallback = true;
                        continue;
                    }

                    string member = GetMemberName(current);
                    if (member == null)
                        continue;

                    return insideCallback ? $"{member} (callback)" : member;
                }

                return null;
            }

            private static string GetMemberName(SyntaxNode node)
            {
                switch (node)
                {
                    case MethodDeclarationSyntax method:
                        return method.Identifier.ValueText;
                    case LocalFunctionStatementSyntax localFunction:
                        return localFunction.Identifier.ValueText;
                    case ConstructorDeclarationSyntax _:
                        return ".ctor";
                    case AccessorDeclarationSyntax accessor:
                        return $"{GetPropertyName(accessor)}.{accessor.Keyword.ValueText}";
                    case PropertyDeclarationSyntax property:
                        return property.Identifier.ValueText;
                    case IndexerDeclarationSyntax _:
                        return "this[]";
                    case FieldDeclarationSyntax _:
                        return ".ctor";
                    default:
                        return null;
                }
            }

            private static string GetPropertyName(SyntaxNode accessor)
            {
                for (SyntaxNode current = accessor; current != null; current = current.Parent)
                {
                    if (current is PropertyDeclarationSyntax property)
                        return property.Identifier.ValueText;

                    if (current is IndexerDeclarationSyntax)
                        return "this[]";
                }

                return "property";
            }

            private SemanticModel GetSemanticModel(SyntaxTree tree)
            {
                if (_compilation == null)
                {
                    _compilation = CSharpCompilation.Create(
                        "UdonSharpCESyncModeValidator",
                        _treesByType.Values.Distinct(),
                        UdonSharp.Compiler.CompilationContext.GetMetadataReferences(),
                        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
                }

                if (!_compilation.ContainsSyntaxTree(tree))
                    return null;

                return _compilation.GetSemanticModel(tree, ignoreAccessibility: true);
            }

            private static Dictionary<string, string[]> GetDefinesByAssembly()
            {
                var defines = new Dictionary<string, string[]>();

                foreach (UnityEditor.Compilation.Assembly assembly in
                         UnityEditor.Compilation.CompilationPipeline.GetAssemblies(
                             UnityEditor.Compilation.AssembliesType.Editor))
                    defines[assembly.name] = assembly.defines;

                return defines;
            }

            private static string FindSourceFile(Type type)
            {
                var scripts = UnityEditor.MonoImporter.GetAllRuntimeMonoScripts();
                foreach (var script in scripts)
                {
                    if (script.GetClass() == type)
                    {
                        string assetPath = UnityEditor.AssetDatabase.GetAssetPath(script);
                        if (string.IsNullOrEmpty(assetPath))
                            return null;

                        return Path.GetFullPath(assetPath);
                    }
                }

                return null;
            }
        }
    }
}
