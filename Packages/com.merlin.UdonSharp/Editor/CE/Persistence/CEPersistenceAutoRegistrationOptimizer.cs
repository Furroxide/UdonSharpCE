using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.Compiler;
using UnityEditor;
using UnityEngine;

namespace UdonSharp.CE.Editor.Persistence
{
    /// <summary>
    /// Auto-registers CEPersistence models by generating converters from attributes.
    ///
    /// This is a correctness-critical source generator, not an optimization: without the generated
    /// converters and non-generic overloads, <c>CEPersistence.Save</c>/<c>Restore</c> compile but do
    /// nothing at runtime. It is invoked by <c>CEGeneratorRunner</c> rather than registering itself, so
    /// its position in the transform order is declared rather than decided by which
    /// <c>[InitializeOnLoad]</c> static constructor Unity happened to run first.
    /// </summary>
    internal static class CEPersistenceAutoRegistrationOptimizer
    {
        private const string GeneratedFileName = "CEPersistence.AutoRegister.g.cs";

        internal static (SyntaxTree tree, string filePath)[] Generate((SyntaxTree tree, string filePath)[] syntaxTrees)
        {
            if (syntaxTrees == null || syntaxTrees.Length == 0)
                return syntaxTrees;

            var modelCollector = new PersistenceModelCollector();
            var models = modelCollector.CollectModels(syntaxTrees);
            if (models.Count == 0)
                return syntaxTrees;

            var updatedTrees = new List<(SyntaxTree tree, string filePath)>(syntaxTrees.Length + 1);

            foreach (var (tree, filePath) in syntaxTrees)
            {
                var root = (CompilationUnitSyntax)tree.GetRoot();
                var rewriter = new CEPersistenceUsageRewriter(root, models);
                var newRoot = rewriter.Visit(root);

                if (rewriter.DidModify && newRoot != null)
                    updatedTrees.Add((tree.WithRootAndOptions(newRoot, tree.Options), filePath));
                else
                    updatedTrees.Add((tree, filePath));
            }

            string generatedSource = PersistenceAutoRegistrationGenerator.Generate(models);
            var parseOptions = syntaxTrees[0].tree.Options as CSharpParseOptions ?? CSharpParseOptions.Default;
            var generatedTree = CSharpSyntaxTree.ParseText(generatedSource, parseOptions, GeneratedFileName);
            updatedTrees.Add((generatedTree, GeneratedFileName));

            return updatedTrees.ToArray();
        }

        private sealed class CEPersistenceUsageRewriter : CSharpSyntaxRewriter
        {
            private static readonly HashSet<string> MethodsWithAutoOverloads = new HashSet<string>(StringComparer.Ordinal)
            {
                "Save",
                "Restore",
                "ToData",
                "FromData",
                "ToJson",
                "FromJson",
                "Validate",
                "EstimateSize",
            };

            private static readonly HashSet<string> MethodsWithInlineConstants = new HashSet<string>(StringComparer.Ordinal)
            {
                "IsRegistered",
                "GetKey",
                "GetVersion",
            };

            private readonly bool _hasStaticUsingCEPersistence;
            private readonly HashSet<string> _typeAliases;
            private readonly Dictionary<string, PersistenceModelInfo> _modelsByFullName;
            private readonly Dictionary<string, List<PersistenceModelInfo>> _modelsByShortName;

            public bool DidModify { get; private set; }

            public CEPersistenceUsageRewriter(CompilationUnitSyntax root, IReadOnlyList<PersistenceModelInfo> models)
            {
                CollectUsingContext(root, out _hasStaticUsingCEPersistence, out _typeAliases);
                BuildModelLookups(models, out _modelsByFullName, out _modelsByShortName);
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node)
            {
                if (node == null)
                    return null;

                var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node);

                if (!TryGetTargetMethod(visited, out string methodName, out GenericNameSyntax genericName))
                    return visited;

                if (genericName.TypeArgumentList.Arguments.Count != 1)
                    return visited;

                if (!TryResolveModel(genericName.TypeArgumentList.Arguments[0].ToString(), node, out var model))
                    return visited;

                if (MethodsWithAutoOverloads.Contains(methodName))
                {
                    var updated = DropGenericTypeArguments(visited, methodName);
                    if (!ReferenceEquals(updated, visited))
                        DidModify = true;
                    return updated;
                }

                if (MethodsWithInlineConstants.Contains(methodName))
                {
                    var replacement = CreateInlineReplacement(methodName, model);
                    if (replacement != null)
                    {
                        DidModify = true;
                        return replacement;
                    }
                }

                return visited;
            }

            private bool TryGetTargetMethod(
                InvocationExpressionSyntax invocation,
                out string methodName,
                out GenericNameSyntax genericName)
            {
                methodName = null;
                genericName = null;

                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    if (!IsCEPersistenceTarget(memberAccess.Expression.ToString()))
                        return false;

                    if (memberAccess.Name is GenericNameSyntax memberGeneric)
                    {
                        methodName = memberGeneric.Identifier.ValueText;
                        genericName = memberGeneric;
                        return true;
                    }

                    return false;
                }

                if (_hasStaticUsingCEPersistence && invocation.Expression is GenericNameSyntax staticGeneric)
                {
                    methodName = staticGeneric.Identifier.ValueText;
                    genericName = staticGeneric;
                    return true;
                }

                return false;
            }

            private InvocationExpressionSyntax DropGenericTypeArguments(InvocationExpressionSyntax invocation, string methodName)
            {
                if (invocation.Expression is MemberAccessExpressionSyntax memberAccess)
                {
                    var updatedMemberAccess = memberAccess.WithName(SyntaxFactory.IdentifierName(methodName));
                    return invocation.WithExpression(updatedMemberAccess);
                }

                if (invocation.Expression is GenericNameSyntax)
                {
                    return invocation.WithExpression(SyntaxFactory.IdentifierName(methodName));
                }

                return invocation;
            }

            private static ExpressionSyntax CreateInlineReplacement(string methodName, PersistenceModelInfo model)
            {
                switch (methodName)
                {
                    case "IsRegistered":
                        return SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);
                    case "GetKey":
                        return SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal(model.Key ?? string.Empty));
                    case "GetVersion":
                        return SyntaxFactory.LiteralExpression(
                            SyntaxKind.NumericLiteralExpression,
                            SyntaxFactory.Literal(model.Version));
                    default:
                        return null;
                }
            }

            private bool IsCEPersistenceTarget(string target)
            {
                if (string.IsNullOrEmpty(target))
                    return false;

                if (IsCEPersistenceName(target))
                    return true;

                // 'using CEP = UdonSharp.CE.Persistence.CEPersistence;' hides the type name from a textual
                // match, so without this the call keeps its type argument and binds to the generic overload -
                // which is a compile error under Udon - instead of the generated one.
                return _typeAliases.Contains(target);
            }

            private bool TryResolveModel(string typeName, SyntaxNode context, out PersistenceModelInfo model)
            {
                model = null;
                if (string.IsNullOrWhiteSpace(typeName))
                    return false;

                string normalized = NormalizeTypeName(typeName);
                if (_modelsByFullName.TryGetValue(normalized, out model))
                    return true;

                if (TryResolveByQualifiedSuffix(normalized, out model))
                    return true;

                if (!_modelsByShortName.TryGetValue(normalized, out var candidates) || candidates.Count == 0)
                    return false;

                if (candidates.Count == 1)
                {
                    model = candidates[0];
                    return true;
                }

                // Two [PlayerData] models share a short name. Guessing would persist under the wrong key, so
                // only accept the candidate whose namespace this call site actually has in scope; if that is
                // still ambiguous, leave the call alone and let the obsolete generic overload report it.
                var scope = GetNamespaceScope(context);
                PersistenceModelInfo match = null;
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (!scope.Contains(candidates[i].Namespace))
                        continue;

                    if (match != null)
                    {
                        match = null;
                        break;
                    }

                    match = candidates[i];
                }

                if (match != null)
                {
                    model = match;
                    return true;
                }

                Debug.LogWarning($"[CE.Persistence] '{normalized}' matches {candidates.Count} [PlayerData] models ({DescribeCandidates(candidates)}). Qualify the type argument with its namespace.");
                model = null;
                return false;
            }

            private bool TryResolveByQualifiedSuffix(string normalized, out PersistenceModelInfo model)
            {
                model = null;
                if (normalized.IndexOf('.') < 0)
                    return false;

                string suffix = "." + normalized;
                foreach (var pair in _modelsByFullName)
                {
                    if (!pair.Key.EndsWith(suffix, StringComparison.Ordinal))
                        continue;

                    if (model != null)
                    {
                        model = null;
                        return false;
                    }

                    model = pair.Value;
                }

                return model != null;
            }

            private static string DescribeCandidates(List<PersistenceModelInfo> candidates)
            {
                var names = new List<string>(candidates.Count);
                foreach (var candidate in candidates)
                    names.Add(candidate.QualifiedName);
                return string.Join(", ", names);
            }

            private static HashSet<string> GetNamespaceScope(SyntaxNode node)
            {
                var scope = new HashSet<string>(StringComparer.Ordinal) { string.Empty };

                SyntaxNode current = node;
                while (current != null)
                {
                    if (current is NamespaceDeclarationSyntax namespaceDeclaration)
                    {
                        AddNamespaceWithPrefixes(scope, namespaceDeclaration.Name?.ToString());
                        AddUsingNamespaces(scope, namespaceDeclaration.Usings);
                    }
                    else if (current is CompilationUnitSyntax compilationUnit)
                    {
                        AddUsingNamespaces(scope, compilationUnit.Usings);
                    }

                    current = current.Parent;
                }

                return scope;
            }

            private static void AddNamespaceWithPrefixes(HashSet<string> scope, string namespaceName)
            {
                if (string.IsNullOrEmpty(namespaceName))
                    return;

                string remaining = namespaceName;
                while (!string.IsNullOrEmpty(remaining))
                {
                    scope.Add(remaining);
                    int lastDot = remaining.LastIndexOf('.');
                    if (lastDot < 0)
                        break;
                    remaining = remaining.Substring(0, lastDot);
                }
            }

            private static void AddUsingNamespaces(HashSet<string> scope, SyntaxList<UsingDirectiveSyntax> usings)
            {
                foreach (var usingDirective in usings)
                {
                    if (usingDirective.Alias != null)
                        continue;

                    if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                        continue;

                    string name = usingDirective.Name?.ToString();
                    if (!string.IsNullOrEmpty(name))
                        scope.Add(NormalizeTypeName(name));
                }
            }

            private static string NormalizeTypeName(string typeName)
            {
                string normalized = typeName.Trim();
                if (normalized.StartsWith("global::", StringComparison.Ordinal))
                    normalized = normalized.Substring("global::".Length);
                return normalized;
            }

            private static void BuildModelLookups(
                IReadOnlyList<PersistenceModelInfo> models,
                out Dictionary<string, PersistenceModelInfo> byFullName,
                out Dictionary<string, List<PersistenceModelInfo>> byShortName)
            {
                byFullName = new Dictionary<string, PersistenceModelInfo>(StringComparer.Ordinal);
                byShortName = new Dictionary<string, List<PersistenceModelInfo>>(StringComparer.Ordinal);

                foreach (var model in models)
                {
                    string fullName = NormalizeTypeName(model.QualifiedName);
                    if (!string.IsNullOrEmpty(fullName))
                        byFullName[fullName] = model;

                    string shortName = model.DisplayName;
                    if (string.IsNullOrEmpty(shortName))
                        continue;

                    // Keep every colliding candidate instead of dropping the name: a call site that only has
                    // one of them in scope is still resolvable, and one that is genuinely ambiguous deserves a
                    // warning naming both rather than silence.
                    if (!byShortName.TryGetValue(shortName, out var candidates))
                    {
                        candidates = new List<PersistenceModelInfo>();
                        byShortName[shortName] = candidates;
                    }

                    candidates.Add(model);
                }
            }

            private static bool IsCEPersistenceName(string name)
            {
                return string.Equals(name, "CEPersistence", StringComparison.Ordinal) ||
                       name.EndsWith(".CEPersistence", StringComparison.Ordinal);
            }

            private static void CollectUsingContext(
                CompilationUnitSyntax root,
                out bool hasStaticUsing,
                out HashSet<string> typeAliases)
            {
                hasStaticUsing = false;
                typeAliases = new HashSet<string>(StringComparer.Ordinal);

                // Usings can also sit inside a namespace declaration; reading only the compilation unit's list
                // silently skipped those files.
                CollectUsingContext(root.Usings, ref hasStaticUsing, typeAliases);

                foreach (var namespaceDeclaration in root.DescendantNodes().OfType<NamespaceDeclarationSyntax>())
                {
                    CollectUsingContext(namespaceDeclaration.Usings, ref hasStaticUsing, typeAliases);
                }
            }

            private static void CollectUsingContext(
                SyntaxList<UsingDirectiveSyntax> usings,
                ref bool hasStaticUsing,
                HashSet<string> typeAliases)
            {
                foreach (var usingDirective in usings)
                {
                    string name = NormalizeTypeName(usingDirective.Name?.ToString() ?? string.Empty);
                    if (string.IsNullOrEmpty(name) || !IsCEPersistenceName(name))
                        continue;

                    if (usingDirective.Alias != null)
                    {
                        typeAliases.Add(usingDirective.Alias.Name.Identifier.ValueText);
                        continue;
                    }

                    if (usingDirective.StaticKeyword.IsKind(SyntaxKind.StaticKeyword))
                        hasStaticUsing = true;
                }
            }
        }

        private sealed class PersistenceModelCollector
        {
            public List<PersistenceModelInfo> CollectModels((SyntaxTree tree, string filePath)[] syntaxTrees)
            {
                var models = new Dictionary<string, PersistenceModelInfo>(StringComparer.Ordinal);

                foreach (var (tree, filePath) in syntaxTrees)
                {
                    var root = tree.GetRoot();
                    foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    {
                        if (!TryGetPlayerDataAttribute(classDeclaration, out var attribute))
                            continue;

                        string modelName = GetQualifiedModelName(classDeclaration);
                        if (string.IsNullOrEmpty(modelName))
                            continue;

                        if (!models.TryGetValue(modelName, out var modelInfo))
                        {
                            modelInfo = new PersistenceModelInfo
                            {
                                QualifiedName = modelName,
                                DisplayName = classDeclaration.Identifier.ValueText,
                                Namespace = GetNamespaceName(classDeclaration) ?? string.Empty,
                                DeclaredFile = filePath
                            };
                            models[modelName] = modelInfo;
                        }

                        if (TryGetStringLiteral(attribute, out string key))
                            modelInfo.Key = key;

                        if (TryGetNamedIntArgument(attribute, "Version", out int version))
                            modelInfo.Version = version;
                    }
                }

                if (models.Count == 0)
                    return new List<PersistenceModelInfo>();

                foreach (var (tree, _) in syntaxTrees)
                {
                    var root = tree.GetRoot();
                    foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    {
                        string modelName = GetQualifiedModelName(classDeclaration);
                        if (string.IsNullOrEmpty(modelName))
                            continue;

                        if (!models.TryGetValue(modelName, out var modelInfo))
                            continue;

                        CollectFields(modelInfo, classDeclaration);
                    }
                }

                var result = new List<PersistenceModelInfo>();
                foreach (var model in models.Values)
                {
                    if (string.IsNullOrEmpty(model.Key))
                    {
                        Debug.LogWarning($"[CE.Persistence] Auto-registration skipped for '{model.DisplayName}': missing [PlayerData(\"key\")].");
                        continue;
                    }

                    if (model.Version == 0)
                        model.Version = 1;

                    if (!model.HasPersistFields && !model.HasRawDataDictionary)
                    {
                        Debug.LogWarning($"[CE.Persistence] Auto-registration skipped for '{model.DisplayName}': no [PersistKey] fields or raw DataDictionary field found.");
                        continue;
                    }

                    // Manual Register<T>() is a compile error under Udon, so a Register call site must never
                    // suppress generation here - doing that used to leave the model with no overloads at all.
                    if (model.HasUnsupportedFields)
                    {
                        Debug.LogWarning($"[CE.Persistence] Auto-registration skipped for '{model.DisplayName}': unsupported field types detected. Change the field to a supported type, or hold the payload in a single DataDictionary field and build it yourself.");
                        continue;
                    }

                    result.Add(model);
                }

                return result;
            }

            private static void CollectFields(PersistenceModelInfo model, ClassDeclarationSyntax classDeclaration)
            {
                foreach (var fieldDeclaration in classDeclaration.Members.OfType<FieldDeclarationSyntax>())
                {
                    if (fieldDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                        continue;

                    bool isPrivate = fieldDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword));

                    var attributes = fieldDeclaration.AttributeLists.SelectMany(list => list.Attributes).ToList();
                    bool ignore = HasAttribute(attributes, "PersistIgnore");
                    if (ignore)
                        continue;

                    var persistAttribute = GetAttribute(attributes, "PersistKey");
                    var fieldType = fieldDeclaration.Declaration.Type;

                    foreach (var variable in fieldDeclaration.Declaration.Variables)
                    {
                        string fieldName = variable.Identifier.ValueText;

                        if (persistAttribute == null)
                        {
                            if (TryMarkRawDataDictionary(model, fieldType, fieldName))
                                continue;

                            continue;
                        }

                        if (isPrivate)
                        {
                            Debug.LogWarning($"[CE.Persistence] Auto-registration skipped field '{fieldName}' in '{model.DisplayName}': field must not be private.");
                            model.HasUnsupportedFields = true;
                            continue;
                        }

                        if (!TryGetStringLiteral(persistAttribute, out string persistKey))
                        {
                            Debug.LogWarning($"[CE.Persistence] Auto-registration skipped field '{fieldName}' in '{model.DisplayName}': [PersistKey] must use a string literal.");
                            model.HasUnsupportedFields = true;
                            continue;
                        }

                        if (!TryClassifyFieldType(fieldType, out var typeInfo))
                        {
                            Debug.LogWarning($"[CE.Persistence] Auto-registration skipped field '{fieldName}' in '{model.DisplayName}': unsupported type '{fieldType}'.");
                            model.HasUnsupportedFields = true;
                            continue;
                        }

                        var fieldInfo = new PersistenceFieldInfo
                        {
                            Name = fieldName,
                            PersistKey = persistKey,
                            Type = typeInfo
                        };

                        if (TryGetNamedBoolArgument(persistAttribute, "Optional", out bool optional))
                            fieldInfo.Optional = optional;

                        if (TryGetNamedArgumentExpression(persistAttribute, "DefaultValue", out string defaultValue))
                            fieldInfo.DefaultValueExpression = defaultValue;

                        var rangeAttribute = GetAttribute(attributes, "Range");
                        if (rangeAttribute != null && TryGetRange(rangeAttribute, out double min, out double max))
                        {
                            fieldInfo.RangeMin = min;
                            fieldInfo.RangeMax = max;
                        }

                        var maxLengthAttribute = GetAttribute(attributes, "MaxLength");
                        if (maxLengthAttribute != null && TryGetMaxLength(maxLengthAttribute, out int maxLength))
                            fieldInfo.MaxLength = maxLength;

                        if (HasAttribute(attributes, "Required"))
                            fieldInfo.Required = true;

                        model.AddField(fieldInfo);
                    }
                }
            }

            private static bool TryMarkRawDataDictionary(PersistenceModelInfo model, TypeSyntax typeSyntax, string fieldName)
            {
                if (model.HasPersistFields)
                    return false;

                string typeName = typeSyntax.ToString();
                if (!IsDataDictionaryType(typeName))
                    return false;

                if (model.HasRawDataDictionary && !string.Equals(model.RawDataFieldName, fieldName, StringComparison.Ordinal))
                    return false;

                model.HasRawDataDictionary = true;
                model.RawDataFieldName = fieldName;
                return true;
            }

            private static bool TryGetPlayerDataAttribute(ClassDeclarationSyntax classDeclaration, out AttributeSyntax attribute)
            {
                attribute = null;
                foreach (var attrList in classDeclaration.AttributeLists)
                {
                    foreach (var attr in attrList.Attributes)
                    {
                        if (IsAttribute(attr, "PlayerData"))
                        {
                            attribute = attr;
                            return true;
                        }
                    }
                }

                return false;
            }

            private static bool HasAttribute(IEnumerable<AttributeSyntax> attributes, string name)
            {
                foreach (var attribute in attributes)
                {
                    if (IsAttribute(attribute, name))
                        return true;
                }

                return false;
            }

            private static AttributeSyntax GetAttribute(IEnumerable<AttributeSyntax> attributes, string name)
            {
                foreach (var attribute in attributes)
                {
                    if (IsAttribute(attribute, name))
                        return attribute;
                }

                return null;
            }

            private static bool IsAttribute(AttributeSyntax attribute, string name)
            {
                string attrName = attribute.Name?.ToString() ?? string.Empty;
                return attrName.EndsWith(name, StringComparison.Ordinal) ||
                       attrName.EndsWith(name + "Attribute", StringComparison.Ordinal);
            }

            private static bool TryGetStringLiteral(AttributeSyntax attribute, out string value)
            {
                value = null;
                if (attribute.ArgumentList == null || attribute.ArgumentList.Arguments.Count == 0)
                    return false;

                var arg = attribute.ArgumentList.Arguments[0];
                if (arg.Expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.StringLiteralExpression))
                {
                    value = literal.Token.ValueText;
                    return true;
                }

                return false;
            }

            private static bool TryGetNamedBoolArgument(AttributeSyntax attribute, string name, out bool value)
            {
                value = false;
                if (attribute.ArgumentList == null)
                    return false;

                foreach (var arg in attribute.ArgumentList.Arguments)
                {
                    if (arg.NameEquals == null)
                        continue;

                    if (!string.Equals(arg.NameEquals.Name.Identifier.ValueText, name, StringComparison.Ordinal))
                        continue;

                    if (arg.Expression is LiteralExpressionSyntax literal &&
                        literal.IsKind(SyntaxKind.TrueLiteralExpression))
                    {
                        value = true;
                        return true;
                    }

                    if (arg.Expression is LiteralExpressionSyntax literalFalse &&
                        literalFalse.IsKind(SyntaxKind.FalseLiteralExpression))
                    {
                        value = false;
                        return true;
                    }
                }

                return false;
            }

            private static bool TryGetNamedIntArgument(AttributeSyntax attribute, string name, out int value)
            {
                value = 0;
                if (attribute.ArgumentList == null)
                    return false;

                foreach (var arg in attribute.ArgumentList.Arguments)
                {
                    if (arg.NameEquals == null)
                        continue;

                    if (!string.Equals(arg.NameEquals.Name.Identifier.ValueText, name, StringComparison.Ordinal))
                        continue;

                    if (arg.Expression is LiteralExpressionSyntax literal &&
                        literal.IsKind(SyntaxKind.NumericLiteralExpression))
                    {
                        if (int.TryParse(literal.Token.ValueText, out value))
                            return true;
                    }
                }

                return false;
            }

            private static bool TryGetNamedArgumentExpression(AttributeSyntax attribute, string name, out string expression)
            {
                expression = null;
                if (attribute.ArgumentList == null)
                    return false;

                foreach (var arg in attribute.ArgumentList.Arguments)
                {
                    if (arg.NameEquals == null)
                        continue;

                    if (!string.Equals(arg.NameEquals.Name.Identifier.ValueText, name, StringComparison.Ordinal))
                        continue;

                    if (arg.Expression is LiteralExpressionSyntax literal)
                    {
                        expression = literal.ToString();
                        return true;
                    }

                    if (arg.Expression.IsKind(SyntaxKind.DefaultLiteralExpression))
                    {
                        expression = "default";
                        return true;
                    }
                }

                return false;
            }

            private static bool TryGetRange(AttributeSyntax attribute, out double min, out double max)
            {
                min = 0;
                max = 0;
                if (attribute.ArgumentList == null || attribute.ArgumentList.Arguments.Count < 2)
                    return false;

                if (!TryGetNumericLiteral(attribute.ArgumentList.Arguments[0].Expression, out min))
                    return false;

                if (!TryGetNumericLiteral(attribute.ArgumentList.Arguments[1].Expression, out max))
                    return false;

                return true;
            }

            private static bool TryGetMaxLength(AttributeSyntax attribute, out int maxLength)
            {
                maxLength = 0;
                if (attribute.ArgumentList == null || attribute.ArgumentList.Arguments.Count == 0)
                    return false;

                if (attribute.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.NumericLiteralExpression))
                {
                    return int.TryParse(literal.Token.ValueText, out maxLength);
                }

                return false;
            }

            private static bool TryGetNumericLiteral(ExpressionSyntax expression, out double value)
            {
                value = 0;
                if (expression is LiteralExpressionSyntax literal &&
                    literal.IsKind(SyntaxKind.NumericLiteralExpression))
                {
                    if (double.TryParse(literal.Token.ValueText, out value))
                        return true;
                }

                return false;
            }

            private static string GetQualifiedModelName(ClassDeclarationSyntax classDeclaration)
            {
                if (classDeclaration == null)
                    return null;

                var nameParts = new Stack<string>();
                nameParts.Push(classDeclaration.Identifier.ValueText);

                SyntaxNode current = classDeclaration.Parent;
                while (current is ClassDeclarationSyntax parentClass)
                {
                    nameParts.Push(parentClass.Identifier.ValueText);
                    current = parentClass.Parent;
                }

                string namespaceName = GetNamespaceName(current);
                string name = string.Join(".", nameParts);
                return string.IsNullOrEmpty(namespaceName)
                    ? name
                    : $"{namespaceName}.{name}";
            }

            private static string GetNamespaceName(SyntaxNode node)
            {
                while (node != null)
                {
                    if (node is NamespaceDeclarationSyntax namespaceDecl)
                        return namespaceDecl.Name.ToString();
                    node = node.Parent;
                }

                return null;
            }

            private static bool TryClassifyFieldType(TypeSyntax typeSyntax, out PersistenceFieldTypeInfo info)
            {
                info = null;
                if (typeSyntax == null)
                    return false;

                if (typeSyntax is ArrayTypeSyntax arrayType)
                {
                    if (!TryClassifyFieldType(arrayType.ElementType, out var elementInfo))
                        return false;

                    info = new PersistenceFieldTypeInfo
                    {
                        Kind = PersistenceFieldKind.Array,
                        TypeName = arrayType.ToString(),
                        ElementType = elementInfo
                    };
                    return true;
                }

                if (typeSyntax is PredefinedTypeSyntax predefined)
                {
                    string keyword = predefined.Keyword.ValueText;
                    switch (keyword)
                    {
                        case "bool":
                        case "byte":
                        case "sbyte":
                        case "short":
                        case "ushort":
                        case "int":
                        case "uint":
                        case "long":
                        case "ulong":
                        case "float":
                        case "double":
                            info = PersistenceFieldTypeInfo.Primitive(keyword);
                            return true;
                        case "string":
                            info = PersistenceFieldTypeInfo.String();
                            return true;
                    }
                }

                string typeName = typeSyntax.ToString();
                if (IsDataDictionaryType(typeName))
                {
                    info = PersistenceFieldTypeInfo.DataDictionary();
                    return true;
                }

                if (IsDataListType(typeName))
                {
                    info = PersistenceFieldTypeInfo.DataList();
                    return true;
                }

                return false;
            }

            private static bool IsDataDictionaryType(string typeName)
            {
                return string.Equals(typeName, "DataDictionary", StringComparison.Ordinal) ||
                       string.Equals(typeName, "VRC.SDK3.Data.DataDictionary", StringComparison.Ordinal) ||
                       typeName.EndsWith(".DataDictionary", StringComparison.Ordinal);
            }

            private static bool IsDataListType(string typeName)
            {
                return string.Equals(typeName, "DataList", StringComparison.Ordinal) ||
                       string.Equals(typeName, "VRC.SDK3.Data.DataList", StringComparison.Ordinal) ||
                       typeName.EndsWith(".DataList", StringComparison.Ordinal);
            }
        }

        private static class PersistenceAutoRegistrationGenerator
        {
            public static string Generate(List<PersistenceModelInfo> models)
            {
                var sb = new StringBuilder(8192);
                sb.AppendLine("using System.Collections.Generic;");
                sb.AppendLine("using UnityEngine;");
                sb.AppendLine("using VRC.SDK3.Data;");
                sb.AppendLine("using VRC.SDK3.Persistence;");
                sb.AppendLine("using VRC.SDKBase;");
                sb.AppendLine();
                sb.AppendLine("namespace UdonSharp.CE.Persistence");
                sb.AppendLine("{");
                sb.AppendLine("    public static partial class CEPersistence");
                sb.AppendLine("    {");

                foreach (var model in models)
                {
                    sb.AppendLine();
                    sb.Append(GenerateModelMethods(model));
                }

                sb.AppendLine("    }");
                sb.AppendLine("}");
                return sb.ToString();
            }

            private static string GenerateModelMethods(PersistenceModelInfo model)
            {
                var sb = new StringBuilder(2048);
                string typeName = model.QualifiedName;
                string safeName = model.SafeName;
                string displayName = EscapeString(model.DisplayName);
                string keyLiteral = EscapeString(model.Key);

                sb.AppendLine($"        // Auto-generated persistence for {displayName}");
                sb.AppendLine($"        public static SaveResult Save({typeName} model)");
                sb.AppendLine("        {");
                sb.AppendLine($"            const string key = \"{keyLiteral}\";");
                sb.AppendLine($"            const int version = {model.Version};");
                sb.AppendLine("            if (model == null)");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] Save<{displayName}>: model cannot be null\");");
                sb.AppendLine("                PersistenceLifecycle.RecordSaveResult(SaveResult.ValidationFailed, key);");
                sb.AppendLine("                return SaveResult.ValidationFailed;");
                sb.AppendLine("            }");
                sb.AppendLine($"            List<ValidationError> validationErrors = Validate__{safeName}(model);");
                sb.AppendLine("            if (validationErrors != null && validationErrors.Count > 0)");
                sb.AppendLine("            {");
                sb.AppendLine("                foreach (var error in validationErrors)");
                sb.AppendLine("                {");
                sb.AppendLine("                    Debug.LogError(\"[CE.Persistence] Validation error: \" + error);");
                sb.AppendLine("                }");
                sb.AppendLine("                PersistenceLifecycle.RecordSaveResult(SaveResult.ValidationFailed, key);");
                sb.AppendLine("                return SaveResult.ValidationFailed;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine($"            DataDictionary data = ToData__{safeName}(model);");
                sb.AppendLine("            if (data == null)");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] Save<{displayName}>: converter returned null DataDictionary\");");
                sb.AppendLine("                data = new DataDictionary();");
                sb.AppendLine("            }");
                sb.AppendLine("            data[VERSION_KEY] = version;");
                sb.AppendLine("            data[MODEL_KEY] = key;");
                sb.AppendLine();
                // PlayerData is one account-wide budget shared by every model, so the enforced comparison is
                // against what is LEFT of the quota. Comparing one model against the whole limit passed several
                // models that each "fit" while together they overran the account. GetWritableQuota credits back
                // the bytes this key already holds, because the save replaces them.
                sb.AppendLine("            int quotaBudget = GetWritableQuota(key);");
                sb.AppendLine("            int estimatedSize = SizeEstimator.EstimateDataDictionarySize(data);");
                sb.AppendLine("            if (estimatedSize > quotaBudget)");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] Save<{displayName}>: estimated size (\" + estimatedSize + \" bytes) exceeds remaining quota (\" + quotaBudget + \" of \" + GetQuotaLimit() + \" bytes)\");");
                sb.AppendLine("                PersistenceLifecycle.RecordSaveResult(SaveResult.QuotaExceeded, key);");
                sb.AppendLine("                return SaveResult.QuotaExceeded;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            if (estimatedSize > quotaBudget * 0.8f)");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogWarning(\"[CE.Persistence] Save<{displayName}>: estimated size (\" + estimatedSize + \" bytes) is approaching the remaining quota (\" + quotaBudget + \" bytes)\");");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            VRCPlayerApi localPlayer = Networking.LocalPlayer;");
                sb.AppendLine("            if (localPlayer == null || !localPlayer.IsValid())");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] Save<{displayName}>: local player not available\");");
                sb.AppendLine("                PersistenceLifecycle.RecordSaveResult(SaveResult.NotAllowed, key);");
                sb.AppendLine("                return SaveResult.NotAllowed;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            if (!VRCJson.TrySerializeToJson(data, JsonExportType.Minify, out DataToken jsonToken))");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] Save<{displayName}>: JSON serialization failed\");");
                sb.AppendLine("                PersistenceLifecycle.RecordSaveResult(SaveResult.ValidationFailed, key);");
                sb.AppendLine("                return SaveResult.ValidationFailed;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            PlayerData.SetString(key, jsonToken.String);");
                sb.AppendLine($"            Debug.Log(\"[CE.Persistence] Saved {displayName} to key '\" + key + \"' (\" + estimatedSize + \" bytes)\");");
                sb.AppendLine("            PersistenceLifecycle.RecordSaveResult(SaveResult.Success, key);");
                sb.AppendLine("            return SaveResult.Success;");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        public static DataDictionary ToData({typeName} model)");
                sb.AppendLine("        {");
                sb.AppendLine($"            const string key = \"{keyLiteral}\";");
                sb.AppendLine($"            const int version = {model.Version};");
                sb.AppendLine("            if (model == null)");
                sb.AppendLine("            {");
                sb.AppendLine("                return new DataDictionary();");
                sb.AppendLine("            }");
                sb.AppendLine($"            DataDictionary data = ToData__{safeName}(model);");
                sb.AppendLine("            if (data == null)");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] ToData<{displayName}>: converter returned null DataDictionary\");");
                sb.AppendLine("                return new DataDictionary();");
                sb.AppendLine("            }");
                sb.AppendLine("            data[VERSION_KEY] = version;");
                sb.AppendLine("            data[MODEL_KEY] = key;");
                sb.AppendLine("            return data;");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        public static string ToJson({typeName} model, bool beautify = false)");
                sb.AppendLine("        {");
                sb.AppendLine($"            DataDictionary data = ToData(model);");
                sb.AppendLine("            JsonExportType exportType = beautify ? JsonExportType.Beautify : JsonExportType.Minify;");
                sb.AppendLine();
                sb.AppendLine("            if (VRCJson.TrySerializeToJson(data, exportType, out DataToken jsonToken))");
                sb.AppendLine("            {");
                sb.AppendLine("                return jsonToken.String;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine($"            Debug.LogError(\"[CE.Persistence] ToJson<{displayName}>: serialization failed\");");
                sb.AppendLine("            return \"{}\";");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        public static RestoreResult Restore(out {typeName} model)");
                sb.AppendLine("        {");
                sb.AppendLine("            model = default;");
                sb.AppendLine($"            const string key = \"{keyLiteral}\";");
                sb.AppendLine();
                sb.AppendLine("            VRCPlayerApi localPlayer = Networking.LocalPlayer;");
                sb.AppendLine("            if (localPlayer == null || !localPlayer.IsValid())");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogWarning(\"[CE.Persistence] Restore<{displayName}>: local player not available\");");
                sb.AppendLine("                PersistenceLifecycle.RecordRestoreResult(RestoreResult.NotReady, key);");
                sb.AppendLine("                return RestoreResult.NotReady;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            string json;");
                sb.AppendLine("            if (!PlayerData.TryGetString(localPlayer, key, out json))");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.Log(\"[CE.Persistence] Restore<{displayName}>: no existing data for key '\" + key + \"'\");");
                sb.AppendLine("                PersistenceLifecycle.RecordRestoreResult(RestoreResult.NoData, key);");
                sb.AppendLine("                return RestoreResult.NoData;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            RestoreResult result = FromJson(json, out model);");
                sb.AppendLine("            PersistenceLifecycle.RecordRestoreResult(result, key);");
                sb.AppendLine("            return result;");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        public static RestoreResult FromData(DataDictionary data, out {typeName} model)");
                sb.AppendLine("        {");
                sb.AppendLine("            model = default;");
                sb.AppendLine($"            const string key = \"{keyLiteral}\";");
                sb.AppendLine($"            const int version = {model.Version};");
                sb.AppendLine();
                sb.AppendLine("            if (data == null)");
                sb.AppendLine("            {");
                sb.AppendLine("                return RestoreResult.NoData;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            if (data.TryGetValue(MODEL_KEY, out DataToken modelToken))");
                sb.AppendLine("            {");
                sb.AppendLine("                if (modelToken.TokenType != TokenType.String)");
                sb.AppendLine("                {");
                sb.AppendLine("                    string details = \"Model key token type \" + modelToken.TokenType + \" is invalid\";");
                sb.AppendLine($"                    Debug.LogError(\"[CE.Persistence] FromData<{displayName}>: \" + details);");
                sb.AppendLine("                    PersistenceLifecycle.RecordCorruption(new CorruptionInfo(key, MODEL_KEY, CorruptionType.InvalidType, details));");
                sb.AppendLine("                    return RestoreResult.ParseError;");
                sb.AppendLine("                }");
                sb.AppendLine();
                sb.AppendLine("                string storedKey = modelToken.String;");
                sb.AppendLine("                if (!string.IsNullOrEmpty(key) && storedKey != key)");
                sb.AppendLine("                {");
                sb.AppendLine("                    string details = \"Stored key '\" + storedKey + \"' does not match expected '\" + key + \"'\";");
                sb.AppendLine($"                    Debug.LogError(\"[CE.Persistence] FromData<{displayName}>: \" + details);");
                sb.AppendLine("                    PersistenceLifecycle.RecordCorruption(new CorruptionInfo(key, MODEL_KEY, CorruptionType.SchemaViolation, details));");
                sb.AppendLine("                    return RestoreResult.ParseError;");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            if (data.TryGetValue(VERSION_KEY, out DataToken versionToken))");
                sb.AppendLine("            {");
                sb.AppendLine("                if (!IsNumericToken(versionToken.TokenType))");
                sb.AppendLine("                {");
                sb.AppendLine("                    string details = \"Version token type \" + versionToken.TokenType + \" is invalid\";");
                sb.AppendLine($"                    Debug.LogError(\"[CE.Persistence] FromData<{displayName}>: \" + details);");
                sb.AppendLine("                    PersistenceLifecycle.RecordCorruption(new CorruptionInfo(key, VERSION_KEY, CorruptionType.InvalidType, details));");
                sb.AppendLine("                    return RestoreResult.ParseError;");
                sb.AppendLine("                }");
                sb.AppendLine();
                sb.AppendLine("                int storedVersion = (int)versionToken.Double;");
                sb.AppendLine("                if (storedVersion != version)");
                sb.AppendLine("                {");
                sb.AppendLine($"                    Debug.LogWarning(\"[CE.Persistence] FromData<{displayName}>: version mismatch (stored: \" + storedVersion + \", expected: \" + version + \")\");");
                sb.AppendLine("                    return RestoreResult.VersionMismatch;");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine($"            model = FromData__{safeName}(data);");
                sb.AppendLine("            return RestoreResult.Success;");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        public static RestoreResult FromJson(string json, out {typeName} model)");
                sb.AppendLine("        {");
                sb.AppendLine("            model = default;");
                sb.AppendLine($"            const string key = \"{keyLiteral}\";");
                sb.AppendLine();
                sb.AppendLine("            if (string.IsNullOrEmpty(json))");
                sb.AppendLine("            {");
                sb.AppendLine("                return RestoreResult.NoData;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            if (!VRCJson.TryDeserializeFromJson(json, out DataToken token))");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] FromJson<{displayName}>: JSON parse failed\");");
                sb.AppendLine("                PersistenceLifecycle.RecordCorruption(new CorruptionInfo(key, null, CorruptionType.MalformedJson, \"JSON parse failed\"));");
                sb.AppendLine("                return RestoreResult.ParseError;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            if (token.TokenType != TokenType.DataDictionary)");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] FromJson<{displayName}>: expected object, got \" + token.TokenType);");
                sb.AppendLine("                PersistenceLifecycle.RecordCorruption(new CorruptionInfo(key, null, CorruptionType.SchemaViolation, \"Expected object, got \" + token.TokenType));");
                sb.AppendLine("                return RestoreResult.ParseError;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            return FromData(token.DataDictionary, out model);");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        public static bool Validate({typeName} model, out List<ValidationError> errors)");
                sb.AppendLine("        {");
                sb.AppendLine("            errors = new List<ValidationError>();");
                sb.AppendLine();
                sb.AppendLine("            if (model == null)");
                sb.AppendLine("            {");
                sb.AppendLine("                errors.Add(new ValidationError(\"_model\", \"_model\", \"Model cannot be null\"));");
                sb.AppendLine("                return false;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine($"            List<ValidationError> validationErrors = Validate__{safeName}(model);");
                sb.AppendLine("            if (validationErrors != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                errors = validationErrors;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine("            return errors.Count == 0;");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        public static int EstimateSize({typeName} model)");
                sb.AppendLine("        {");
                sb.AppendLine($"            const string key = \"{keyLiteral}\";");
                sb.AppendLine($"            const int version = {model.Version};");
                sb.AppendLine();
                sb.AppendLine("            if (model == null)");
                sb.AppendLine("            {");
                sb.AppendLine("                return 0;");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine($"            DataDictionary data = ToData__{safeName}(model);");
                sb.AppendLine("            if (data == null)");
                sb.AppendLine("            {");
                sb.AppendLine($"                Debug.LogError(\"[CE.Persistence] EstimateSize<{displayName}>: converter returned null DataDictionary\");");
                sb.AppendLine("                return -1;");
                sb.AppendLine("            }");
                sb.AppendLine("            data[VERSION_KEY] = version;");
                sb.AppendLine("            data[MODEL_KEY] = key;");
                sb.AppendLine("            return SizeEstimator.EstimateDataDictionarySize(data);");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        private static DataDictionary ToData__{safeName}({typeName} model)");
                sb.AppendLine("        {");
                if (model.HasRawDataDictionary)
                {
                    sb.AppendLine($"            return model != null && model.{model.RawDataFieldName} != null ? model.{model.RawDataFieldName} : new DataDictionary();");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                    sb.AppendLine($"        private static {typeName} FromData__{safeName}(DataDictionary data)");
                    sb.AppendLine("        {");
                    sb.AppendLine($"            return new {typeName} {{ {model.RawDataFieldName} = data }};");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                    sb.AppendLine($"        private static List<ValidationError> Validate__{safeName}({typeName} model)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (model == null)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var errors = new List<ValidationError>();");
                    sb.AppendLine("                errors.Add(new ValidationError(\"_model\", \"_model\", \"Model cannot be null\"));");
                    sb.AppendLine("                return errors;");
                    sb.AppendLine("            }");
                    sb.AppendLine();
                    sb.AppendLine("            return null;");
                    sb.AppendLine("        }");
                    return sb.ToString();
                }

                sb.AppendLine("            var dict = new DataDictionary();");
                sb.AppendLine("            if (model == null) return dict;");
                sb.AppendLine();

                foreach (var field in model.Fields)
                {
                    AppendToDataField(sb, field);
                }

                sb.AppendLine("            return dict;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine($"        private static {typeName} FromData__{safeName}(DataDictionary data)");
                sb.AppendLine("        {");
                sb.AppendLine($"            var model = new {typeName}();");
                sb.AppendLine("            if (data == null) return model;");
                sb.AppendLine("            DataToken token;");
                sb.AppendLine();

                foreach (var field in model.Fields)
                {
                    AppendFromDataField(sb, field);
                }

                sb.AppendLine("            return model;");
                sb.AppendLine("        }");
                sb.AppendLine();
                sb.AppendLine($"        private static List<ValidationError> Validate__{safeName}({typeName} model)");
                sb.AppendLine("        {");
                sb.AppendLine("            if (model == null)");
                sb.AppendLine("            {");
                sb.AppendLine("                var errors = new List<ValidationError>();");
                sb.AppendLine("                errors.Add(new ValidationError(\"_model\", \"_model\", \"Model cannot be null\"));");
                sb.AppendLine("                return errors;");
                sb.AppendLine("            }");

                if (!model.HasValidation)
                {
                    sb.AppendLine();
                    sb.AppendLine("            return null;");
                    sb.AppendLine("        }");
                    return sb.ToString();
                }

                sb.AppendLine();
                sb.AppendLine("            List<ValidationError> errors = null;");
                sb.AppendLine();

                foreach (var field in model.Fields)
                {
                    AppendValidation(sb, field);
                }

                sb.AppendLine("            return errors;");
                sb.AppendLine("        }");

                return sb.ToString();
            }

            private static void AppendToDataField(StringBuilder sb, PersistenceFieldInfo field)
            {
                string fieldAccess = $"model.{field.Name}";
                string keyLiteral = $"\"{EscapeString(field.PersistKey)}\"";

                if (field.Type.Kind == PersistenceFieldKind.Array)
                {
                    sb.AppendLine($"            if ({fieldAccess} != null)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var list = new DataList();");
                    sb.AppendLine($"                for (int i = 0; i < {fieldAccess}.Length; i++)");
                    sb.AppendLine("                {");
                    sb.AppendLine($"                    list.Add({fieldAccess}[i]);");
                    sb.AppendLine("                }");
                    sb.AppendLine($"                dict[{keyLiteral}] = list;");
                    sb.AppendLine("            }");
                    sb.AppendLine("            else");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                dict[{keyLiteral}] = null;");
                    sb.AppendLine("            }");
                    return;
                }

                sb.AppendLine($"            dict[{keyLiteral}] = {fieldAccess};");
            }

            private static void AppendFromDataField(StringBuilder sb, PersistenceFieldInfo field)
            {
                string keyLiteral = $"\"{EscapeString(field.PersistKey)}\"";
                string fieldAccess = $"model.{field.Name}";

                sb.AppendLine($"            if (data.TryGetValue({keyLiteral}, out token))");
                sb.AppendLine("            {");

                if (field.Type.Kind == PersistenceFieldKind.Array)
                {
                    sb.AppendLine("                if (token.TokenType == TokenType.DataList)");
                    sb.AppendLine("                {");
                    sb.AppendLine("                    var list = token.DataList;");
                    sb.AppendLine($"                    var array = new {field.Type.ElementType.TypeName}[list.Count];");
                    sb.AppendLine("                    DataToken itemToken;");
                    sb.AppendLine("                    for (int i = 0; i < list.Count; i++)");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        if (list.TryGetValue(i, out itemToken))");
                    sb.AppendLine("                        {");
                    sb.AppendLine($"                            array[i] = {field.Type.ElementType.GetReadExpression("itemToken")};");
                    sb.AppendLine("                        }");
                    sb.AppendLine("                    }");
                    sb.AppendLine($"                    {fieldAccess} = array;");
                    sb.AppendLine("                }");
                }
                else
                {
                    sb.AppendLine($"                {fieldAccess} = {field.Type.GetReadExpression("token")};");
                }

                sb.AppendLine("            }");

                if (field.Optional && !string.IsNullOrEmpty(field.DefaultValueExpression))
                {
                    sb.AppendLine("            else");
                    sb.AppendLine("            {");
                    sb.AppendLine($"                {fieldAccess} = {field.DefaultValueExpression};");
                    sb.AppendLine("            }");
                }
            }

            private static void AppendValidation(StringBuilder sb, PersistenceFieldInfo field)
            {
                string fieldAccess = $"model.{field.Name}";
                string persistKeyLiteral = $"\"{EscapeString(field.PersistKey)}\"";

                if (field.Required)
                {
                    if (field.Type.Kind == PersistenceFieldKind.String)
                    {
                        sb.AppendLine($"            if (string.IsNullOrEmpty({fieldAccess}))");
                        AppendValidationError(sb, field, persistKeyLiteral, "\"Required string is null or empty\"", fieldAccess);
                    }
                    else if (field.Type.Kind == PersistenceFieldKind.Array)
                    {
                        sb.AppendLine($"            if ({fieldAccess} == null || {fieldAccess}.Length == 0)");
                        AppendValidationError(sb, field, persistKeyLiteral, "\"Required array is null or empty\"", fieldAccess);
                    }
                    else if (field.Type.Kind == PersistenceFieldKind.DataList)
                    {
                        sb.AppendLine($"            if ({fieldAccess} == null || {fieldAccess}.Count == 0)");
                        AppendValidationError(sb, field, persistKeyLiteral, "\"Required list is null or empty\"", fieldAccess);
                    }
                    else if (field.Type.Kind == PersistenceFieldKind.DataDictionary)
                    {
                        sb.AppendLine($"            if ({fieldAccess} == null || {fieldAccess}.Count == 0)");
                        AppendValidationError(sb, field, persistKeyLiteral, "\"Required dictionary is null or empty\"", fieldAccess);
                    }
                }

                if (field.RangeMin.HasValue && field.RangeMax.HasValue)
                {
                    if (field.Type.Kind == PersistenceFieldKind.Primitive && !string.Equals(field.Type.TypeName, "bool", StringComparison.Ordinal))
                    {
                        sb.AppendLine($"            if ({fieldAccess} < {field.RangeMin.Value} || {fieldAccess} > {field.RangeMax.Value})");
                        AppendValidationError(sb, field, persistKeyLiteral, $"\"Value must be between {field.RangeMin.Value} and {field.RangeMax.Value}\"", fieldAccess);
                    }
                }

                if (field.MaxLength.HasValue)
                {
                    if (field.Type.Kind == PersistenceFieldKind.String)
                    {
                        sb.AppendLine($"            if ({fieldAccess} != null && {fieldAccess}.Length > {field.MaxLength.Value})");
                        AppendValidationError(sb, field, persistKeyLiteral, $"\"String length exceeds {field.MaxLength.Value}\"", fieldAccess);
                    }
                    else if (field.Type.Kind == PersistenceFieldKind.Array)
                    {
                        sb.AppendLine($"            if ({fieldAccess} != null && {fieldAccess}.Length > {field.MaxLength.Value})");
                        AppendValidationError(sb, field, persistKeyLiteral, $"\"Array length exceeds {field.MaxLength.Value}\"", fieldAccess);
                    }
                }
            }

            private static void AppendValidationError(StringBuilder sb, PersistenceFieldInfo field, string persistKeyLiteral, string messageLiteral, string actualValue)
            {
                sb.AppendLine("            {");
                sb.AppendLine("                if (errors == null) errors = new List<ValidationError>();");
                sb.AppendLine($"                errors.Add(new ValidationError(\"{field.Name}\", {persistKeyLiteral}, {messageLiteral}, {actualValue}));");
                sb.AppendLine("            }");
            }

            private static string EscapeString(string value)
            {
                return value?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? string.Empty;
            }
        }

        private sealed class PersistenceModelInfo
        {
            public string QualifiedName { get; set; }
            public string DisplayName { get; set; }
            public string Namespace { get; set; } = string.Empty;
            public string DeclaredFile { get; set; }
            public string Key { get; set; }
            public int Version { get; set; } = 1;
            public bool HasRawDataDictionary { get; set; }
            public string RawDataFieldName { get; set; }
            public bool HasPersistFields => Fields.Count > 0;
            public bool HasUnsupportedFields { get; set; }
            public List<PersistenceFieldInfo> Fields { get; } = new List<PersistenceFieldInfo>();
            public string SafeName => SanitizeTypeName(string.IsNullOrEmpty(QualifiedName) ? DisplayName : QualifiedName);

            public bool HasValidation => Fields.Any(field => field.HasValidation);

            public void AddField(PersistenceFieldInfo field)
            {
                if (Fields.Any(existing => string.Equals(existing.Name, field.Name, StringComparison.Ordinal)))
                    return;

                Fields.Add(field);
                if (HasRawDataDictionary)
                {
                    HasRawDataDictionary = false;
                    RawDataFieldName = null;
                }
            }

            private static string SanitizeTypeName(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return "Model";
                var sb = new StringBuilder(name.Length);
                foreach (char c in name)
                {
                    if (char.IsLetterOrDigit(c) || c == '_')
                        sb.Append(c);
                    else
                        sb.Append('_');
                }
                return sb.ToString();
            }
        }

        private sealed class PersistenceFieldInfo
        {
            public string Name { get; set; }
            public string PersistKey { get; set; }
            public PersistenceFieldTypeInfo Type { get; set; }
            public bool Optional { get; set; }
            public string DefaultValueExpression { get; set; }
            public bool Required { get; set; }
            public double? RangeMin { get; set; }
            public double? RangeMax { get; set; }
            public int? MaxLength { get; set; }

            public bool HasValidation =>
                Required || RangeMin.HasValue || RangeMax.HasValue || MaxLength.HasValue;
        }

        private enum PersistenceFieldKind
        {
            Primitive,
            String,
            DataDictionary,
            DataList,
            Array
        }

        private sealed class PersistenceFieldTypeInfo
        {
            public PersistenceFieldKind Kind { get; set; }
            public string TypeName { get; set; }
            public PersistenceFieldTypeInfo ElementType { get; set; }

            public static PersistenceFieldTypeInfo Primitive(string typeName)
            {
                return new PersistenceFieldTypeInfo
                {
                    Kind = PersistenceFieldKind.Primitive,
                    TypeName = typeName
                };
            }

            public static PersistenceFieldTypeInfo String()
            {
                return new PersistenceFieldTypeInfo
                {
                    Kind = PersistenceFieldKind.String,
                    TypeName = "string"
                };
            }

            public static PersistenceFieldTypeInfo DataDictionary()
            {
                return new PersistenceFieldTypeInfo
                {
                    Kind = PersistenceFieldKind.DataDictionary,
                    TypeName = "DataDictionary"
                };
            }

            public static PersistenceFieldTypeInfo DataList()
            {
                return new PersistenceFieldTypeInfo
                {
                    Kind = PersistenceFieldKind.DataList,
                    TypeName = "DataList"
                };
            }

            public string GetReadExpression(string tokenName)
            {
                switch (Kind)
                {
                    case PersistenceFieldKind.Primitive:
                        return GetPrimitiveReadExpression(tokenName);
                    case PersistenceFieldKind.String:
                        return $"{tokenName}.String";
                    case PersistenceFieldKind.DataDictionary:
                        return $"{tokenName}.DataDictionary";
                    case PersistenceFieldKind.DataList:
                        return $"{tokenName}.DataList";
                    default:
                        return $"{tokenName}.String";
                }
            }

            private string GetPrimitiveReadExpression(string tokenName)
            {
                switch (TypeName)
                {
                    case "bool":
                        return $"{tokenName}.Boolean";
                    case "float":
                        return $"(float){tokenName}.Double";
                    case "double":
                        return $"{tokenName}.Double";
                    case "byte":
                        return $"(byte){tokenName}.Double";
                    case "sbyte":
                        return $"(sbyte){tokenName}.Double";
                    case "short":
                        return $"(short){tokenName}.Double";
                    case "ushort":
                        return $"(ushort){tokenName}.Double";
                    case "int":
                        return $"(int){tokenName}.Double";
                    case "uint":
                        return $"(uint){tokenName}.Double";
                    case "long":
                        return $"(long){tokenName}.Double";
                    case "ulong":
                        return $"(ulong){tokenName}.Double";
                    default:
                        return $"{tokenName}.Double";
                }
            }
        }
    }
}
