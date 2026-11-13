using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.Compiler;
using UdonSharp.CE.Editor.Optimizers;
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
                    var arguments = updated.ArgumentList.Arguments;
                    for (int i = 0; i < arguments.Count; i++)
                    {
                        var argument = arguments[i];
                        if ((methodName != "Restore" && methodName != "FromData" && methodName != "FromJson") || !argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)) continue;
                        if (argument.Expression is DeclarationExpressionSyntax declaration)
                            arguments = arguments.Replace(argument, argument.WithExpression(declaration.WithType(SyntaxFactory.ParseTypeName("global::" + model.QualifiedName).WithTrailingTrivia(SyntaxFactory.Space))));
                        else if (argument.Expression is IdentifierNameSyntax identifier && identifier.Identifier.ValueText == "_")
                            arguments = arguments.Replace(argument, argument.WithExpression(SyntaxFactory.DeclarationExpression(SyntaxFactory.ParseTypeName("global::" + model.QualifiedName).WithTrailingTrivia(SyntaxFactory.Space), SyntaxFactory.DiscardDesignation())));
                    }
                    updated = updated.WithArgumentList(updated.ArgumentList.WithArguments(arguments));
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
                        AddNamespaceWithPrefixes(scope, FullNamespaceName(namespaceDeclaration));
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

            /// <summary>
            /// The namespace a declaration block really declares. Nested 'namespace A { namespace B { } }'
            /// blocks are namespace A.B, and a model records that full name, so the scope has to as well:
            /// adding only "B" left a call site inside the nested block unable to choose between two models
            /// that share a short name.
            /// </summary>
            private static string FullNamespaceName(NamespaceDeclarationSyntax declaration)
            {
                var parts = new Stack<string>();
                for (SyntaxNode node = declaration; node != null; node = node.Parent)
                {
                    if (node is NamespaceDeclarationSyntax namespaceDeclaration)
                        parts.Push(namespaceDeclaration.Name.ToString());
                }

                return string.Join(".", parts);
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
            private readonly OptimizationContext _constants = new OptimizationContext();

            private object Constant(ExpressionSyntax expression)
            {
                var value = _constants.GetSemanticModel(expression.SyntaxTree).GetConstantValue(expression);
                if (!value.HasValue)
                    throw new CodeGenerationException("CEPERSIST001", expression.GetLocation(), "Persistence attribute argument must be a supported compile-time constant: " + expression);
                return value.Value;
            }

            private static CodeGenerationException Invalid(SyntaxNode node, string message)
                => new CodeGenerationException("CEPERSIST001", node.GetLocation(), message);

            public List<PersistenceModelInfo> CollectModels((SyntaxTree tree, string filePath)[] syntaxTrees)
            {
                _constants.SetSemanticModelTrees(syntaxTrees.Select(pair => pair.tree));
                var models = new Dictionary<string, PersistenceModelInfo>(StringComparer.Ordinal);

                foreach (var (tree, filePath) in syntaxTrees)
                {
                    var root = tree.GetRoot();
                    foreach (var classDeclaration in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
                    {
                        if (!TryGetPlayerDataAttribute(classDeclaration, out var attribute))
                            continue;

                        if (classDeclaration.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().Any(c => c.TypeParameterList != null))
                            throw Invalid(classDeclaration, "Generic persistence models are not supported.");
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
                            ValidateModelType(classDeclaration, modelInfo);
                        }

                        if (TryGetStringLiteral(attribute, out string key))
                        {
                            modelInfo.Key = key;
                            modelInfo.KeyLocation = attribute.GetLocation();
                        }

                        if (TryGetNamedIntArgument(attribute, "Version", out int version))
                            modelInfo.Version = version;
                    }
                }

                if (models.Count == 0)
                    return new List<PersistenceModelInfo>();

                foreach (var model in models.Values)
                    model.HasDeclaredPersistFields = syntaxTrees.SelectMany(pair => pair.tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>())
                        .Where(declaration => GetQualifiedModelName(declaration) == model.QualifiedName)
                        .SelectMany(declaration => declaration.Members.OfType<FieldDeclarationSyntax>())
                        .Any(field => !field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => IsAttribute(attribute, "PersistIgnore")) && field.AttributeLists.SelectMany(list => list.Attributes).Any(attribute => IsAttribute(attribute, "PersistKey")));

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

                        if (classDeclaration.BaseList != null)
                        {
                            var symbol = _constants.GetSemanticModel(tree).GetDeclaredSymbol(classDeclaration);
                            for (var baseType = symbol?.BaseType; baseType != null; baseType = baseType.BaseType)
                                if (baseType.GetMembers().OfType<IFieldSymbol>().Any(f => f.GetAttributes().Any(a => a.AttributeClass?.Name == "PersistKeyAttribute")))
                                    throw Invalid(classDeclaration, "Inherited [PersistKey] fields are not supported; declare persisted fields on the model itself.");
                        }
                        CollectFields(modelInfo, classDeclaration);
                    }
                }

                var modelsByKey = new Dictionary<string, PersistenceModelInfo>(StringComparer.Ordinal);
                foreach (var model in models.Values)
                {
                    if (model.Key == null)
                        continue;

                    if (model.Key.StartsWith("__ce_", StringComparison.Ordinal))
                        throw new CodeGenerationException("CEPERSIST001", model.KeyLocation ?? Location.None,
                            $"[PlayerData] key '{model.Key}' on '{model.QualifiedName}' is reserved: keys starting with __ce_ belong to CE.");

                    // Two models under one key overwrite each other's save, and neither notices: the model key
                    // written into the JSON is that same shared key, so each Restore accepts the other's data.
                    if (modelsByKey.TryGetValue(model.Key, out var other))
                        throw new CodeGenerationException("CEPERSIST001", model.KeyLocation ?? Location.None,
                            $"[PlayerData] key '{model.Key}' is used by both '{other.QualifiedName}' and '{model.QualifiedName}'. Give each model its own key; sharing one makes them overwrite each other's save.");

                    modelsByKey.Add(model.Key, model);
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

                    // Manual Register<T>() is a compile error under Udon, so a Register call site must never
                    // suppress generation here - doing that used to leave the model with no overloads at all.
                    // Checked before the no-fields case: a model whose only [PersistKey] field was rejected has
                    // no fields left, and being told it has none hid the warning that said why.
                    if (model.HasUnsupportedFields)
                    {
                        Debug.LogWarning($"[CE.Persistence] Auto-registration skipped for '{model.DisplayName}': it has [PersistKey] fields CE cannot generate a converter for (see the warnings above). Change each to a supported, writable public or internal field, or hold the payload in a single DataDictionary field and build it yourself.");
                        continue;
                    }

                    if (!model.HasPersistFields && !model.HasRawDataDictionary)
                    {
                        // The model's only DataDictionary is one the generated converter could not read or
                        // assign from outside the class. Saying so beats a warning about a field that exists.
                        if (model.InaccessibleRawCandidate != null)
                            throw Invalid(model.InaccessibleRawCandidate,
                                $"'{model.QualifiedName}' would persist its DataDictionary field, but that field is private, protected, readonly or const. The generated converter reads and assigns it from outside the class, so make it a writable public or internal field.");

                        Debug.LogWarning($"[CE.Persistence] Auto-registration skipped for '{model.DisplayName}': no [PersistKey] fields or raw DataDictionary field found.");
                        continue;
                    }

                    result.Add(model);
                }

                return result;
            }

            private void CollectFields(PersistenceModelInfo model, ClassDeclarationSyntax classDeclaration)
            {
                foreach (var fieldDeclaration in classDeclaration.Members.OfType<FieldDeclarationSyntax>())
                {
                    if (fieldDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword)))
                        continue;

                    bool isPrivate = !fieldDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword) || m.IsKind(SyntaxKind.InternalKeyword)) ||
                        fieldDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword) || m.IsKind(SyntaxKind.ConstKeyword));

                    var attributes = fieldDeclaration.AttributeLists.SelectMany(list => list.Attributes).ToList();
                    bool ignore = HasAttribute(attributes, "PersistIgnore");
                    if (ignore)
                        continue;

                    var persistAttribute = GetAttribute(attributes, "PersistKey");
                    var fieldType = fieldDeclaration.Declaration.Type;

                    foreach (var variable in fieldDeclaration.Declaration.Variables)
                    {
                        string fieldName = variable.Identifier.Text;

                        if (persistAttribute == null)
                        {
                            // Only a writable public or internal DataDictionary can be the raw payload: the
                            // generated converter reads and assigns it from outside the class. Any other
                            // DataDictionary is the model's own scratch state, and becomes an error only if the
                            // model turns out to have nothing else it could persist.
                            if (isPrivate)
                            {
                                if (model.InaccessibleRawCandidate == null && !model.HasDeclaredPersistFields && IsDataDictionaryType(fieldType.ToString()))
                                    model.InaccessibleRawCandidate = variable;
                                continue;
                            }

                            TryMarkRawDataDictionary(model, fieldType, fieldName);
                            continue;
                        }

                        if (isPrivate)
                        {
                            Debug.LogWarning($"[CE.Persistence] Auto-registration skipped field '{fieldName}' in '{model.DisplayName}': a [PersistKey] field must be public or internal and writable (not readonly or const), because the generated converter reads and assigns it from outside the class.");
                            model.HasUnsupportedFields = true;
                            continue;
                        }

                        if (!TryGetStringLiteral(persistAttribute, out string persistKey))
                        {
                            Debug.LogWarning($"[CE.Persistence] Auto-registration skipped field '{fieldName}' in '{model.DisplayName}': [PersistKey] needs a constant string key.");
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
                            Type = typeInfo,
                            Location = variable.GetLocation()
                        };

                        if (TryGetNamedBoolArgument(persistAttribute, "Optional", out bool optional))
                            fieldInfo.Optional = optional;

                        ExpressionSyntax defaultArgument = NamedArgument(persistAttribute, "DefaultValue");
                        if (defaultArgument != null)
                            fieldInfo.DefaultValueExpression = DefaultValueExpression(defaultArgument, typeInfo, fieldName, model);

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
                if (model.HasDeclaredPersistFields)
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

            private bool TryGetStringLiteral(AttributeSyntax attribute, out string value)
            {
                value = null;
                if (attribute.ArgumentList == null || attribute.ArgumentList.Arguments.Count == 0) return false;
                var expression = attribute.ArgumentList.Arguments[0].Expression;
                value = Constant(expression) as string;
                if (value == null) throw Invalid(expression, "Persistence keys must be constant strings.");
                return true;
            }

            private ExpressionSyntax NamedArgument(AttributeSyntax attribute, string name)
                => attribute.ArgumentList?.Arguments.FirstOrDefault(a => a.NameEquals?.Name.Identifier.ValueText == name)?.Expression;

            private bool TryGetNamedBoolArgument(AttributeSyntax attribute, string name, out bool value)
            {
                value = false;
                var expression = NamedArgument(attribute, name);
                if (expression == null) return false;
                if (!(Constant(expression) is bool result)) throw Invalid(expression, name + " must be a constant bool.");
                value = result;
                return true;
            }

            private bool TryGetNamedIntArgument(AttributeSyntax attribute, string name, out int value)
            {
                value = 0;
                var expression = NamedArgument(attribute, name);
                if (expression == null) return false;
                if (!(Constant(expression) is int result)) throw Invalid(expression, name + " must be a constant int.");
                value = result;
                return true;
            }

            /// <summary>
            /// The C# expression the generated FromData assigns when an Optional field's key is missing.
            /// </summary>
            /// <remarks>
            /// DefaultValue is typed object, so nothing at the attribute stops a constant the field cannot hold.
            /// Emitted as written, such a value turned into a compile error inside the generated file -- '(int)(null)'
            /// for DefaultValue = default, '(byte)(300)', a string on a number -- pointing at code the author
            /// never wrote. Each value is checked against the field here and either emitted in a form that
            /// compiles or rejected naming the argument. A non-integral value for an integer field is rejected
            /// rather than truncated, since a default that silently changes is worse than one that fails.
            /// </remarks>
            private string DefaultValueExpression(ExpressionSyntax argument, PersistenceFieldTypeInfo type, string fieldName, PersistenceModelInfo model)
            {
                object value = Constant(argument);

                if (value == null)
                    return type.Kind == PersistenceFieldKind.Primitive ? "default(" + type.TypeName + ")" : "null";

                string literal = null;
                switch (type.Kind)
                {
                    case PersistenceFieldKind.String:
                        if (value is string text)
                            literal = SyntaxFactory.Literal(text).ToString();
                        break;

                    case PersistenceFieldKind.Primitive:
                        if (type.TypeName == "bool")
                        {
                            if (value is bool flag)
                                literal = flag ? "true" : "false";
                        }
                        else if (type.TypeName == "float" || type.TypeName == "double")
                        {
                            literal = FloatingDefaultLiteral(value, type.TypeName);
                        }
                        else
                        {
                            literal = IntegerDefaultLiteral(value, type.TypeName);
                        }
                        break;
                }

                if (literal == null)
                    throw Invalid(argument,
                        $"DefaultValue = {argument} on '{model.DisplayName}.{fieldName}' cannot be stored in its {type.TypeName} field. " +
                        "Use a constant of the field's type, or null for the type's default.");

                return literal;
            }

            private static bool IsNumericConstant(object value)
            {
                return value is sbyte || value is byte || value is short || value is ushort || value is int ||
                       value is uint || value is long || value is ulong || value is float || value is double;
            }

            private static string FloatingDefaultLiteral(object value, string typeName)
            {
                if (!IsNumericConstant(value))
                    return null;

                // A float constant widens exactly, so the double literal below narrows back to the same float.
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number))
                    return null;

                if (typeName == "float" && Math.Abs(number) > float.MaxValue)
                    return null;

                return number.ToString("R", CultureInfo.InvariantCulture) + "d";
            }

            private static string IntegerDefaultLiteral(object value, string typeName)
            {
                if (!IsNumericConstant(value))
                    return null;

                decimal number;
                if (value is float || value is double)
                {
                    double floating = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    if (double.IsNaN(floating) || double.IsInfinity(floating) || Math.Abs(floating) > 1e20)
                        return null;

                    number = (decimal)floating;
                }
                else
                {
                    number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                }

                if (number != decimal.Truncate(number))
                    return null;

                decimal min, max;
                switch (typeName)
                {
                    case "byte": min = byte.MinValue; max = byte.MaxValue; break;
                    case "sbyte": min = sbyte.MinValue; max = sbyte.MaxValue; break;
                    case "short": min = short.MinValue; max = short.MaxValue; break;
                    case "ushort": min = ushort.MinValue; max = ushort.MaxValue; break;
                    case "int": min = int.MinValue; max = int.MaxValue; break;
                    case "uint": min = uint.MinValue; max = uint.MaxValue; break;
                    case "long": min = long.MinValue; max = long.MaxValue; break;
                    case "ulong": min = ulong.MinValue; max = ulong.MaxValue; break;
                    default: return null;
                }

                if (number < min || number > max)
                    return null;

                return number.ToString(CultureInfo.InvariantCulture);
            }

            /// <summary>
            /// Rejects a model class the generated overloads could not reach or construct, and records whether
            /// they have to be internal.
            /// </summary>
            /// <remarks>
            /// The overloads live in CEPersistence, outside the model, and FromData builds the model with
            /// 'new T()'. A model that is private or protected (or nested in such a class), nested in a struct,
            /// abstract, static, or without an accessible parameterless constructor therefore produced code that
            /// failed to compile -- and since every [PlayerData] model shares one generated file, it failed the
            /// whole project's compile from a file the author never wrote. An internal model (a top-level class
            /// with no modifier is one) compiled no better: a public overload taking an internal type is CS0051,
            /// so its overloads are emitted internal instead.
            /// </remarks>
            private void ValidateModelType(ClassDeclarationSyntax declaration, PersistenceModelInfo model)
            {
                if (!(_constants.GetSemanticModel(declaration.SyntaxTree).GetDeclaredSymbol(declaration) is INamedTypeSymbol symbol))
                    return;

                string name = model.QualifiedName;

                if (symbol.IsAbstract || symbol.IsStatic)
                    throw Invalid(declaration,
                        $"[PlayerData] model '{name}' is {(symbol.IsStatic ? "static" : "abstract")}. The generated FromData constructs the model, so it has to be a concrete, non-static class.");

                bool internalOnly = false;
                for (INamedTypeSymbol type = symbol; type != null; type = type.ContainingType)
                {
                    if (type.TypeKind != TypeKind.Class)
                        throw Invalid(declaration,
                            $"[PlayerData] model '{name}' is nested in {type.TypeKind.ToString().ToLowerInvariant()} '{type.Name}'. Declare it at namespace level or nested only in classes.");

                    switch (type.DeclaredAccessibility)
                    {
                        case Accessibility.Public:
                            break;
                        case Accessibility.Internal:
                        case Accessibility.ProtectedOrInternal:
                            internalOnly = true;
                            break;
                        default:
                            throw Invalid(declaration,
                                $"[PlayerData] model '{name}' cannot be reached by its generated overloads: '{type.Name}' is {DescribeAccessibility(type.DeclaredAccessibility)}. " +
                                "Make the model, and every class it is nested in, public or internal.");
                    }
                }

                bool constructible = false;
                foreach (IMethodSymbol constructor in symbol.InstanceConstructors)
                {
                    bool reachable = constructor.DeclaredAccessibility == Accessibility.Public ||
                                     constructor.DeclaredAccessibility == Accessibility.Internal ||
                                     constructor.DeclaredAccessibility == Accessibility.ProtectedOrInternal;

                    if (reachable && constructor.Parameters.All(parameter => parameter.IsOptional || parameter.IsParams))
                    {
                        constructible = true;
                        break;
                    }
                }

                if (!constructible)
                    throw Invalid(declaration,
                        $"[PlayerData] model '{name}' has no public or internal parameterless constructor. The generated FromData constructs the model with 'new {symbol.Name}()', so add one.");

                model.IsInternal = internalOnly;
            }

            private static string DescribeAccessibility(Accessibility accessibility)
            {
                switch (accessibility)
                {
                    case Accessibility.Private: return "private";
                    case Accessibility.Protected: return "protected";
                    case Accessibility.ProtectedAndInternal: return "private protected";
                    default: return accessibility.ToString().ToLowerInvariant();
                }
            }

            private bool TryGetRange(AttributeSyntax attribute, out double min, out double max)
            {
                min = max = 0;
                if (attribute.ArgumentList == null || attribute.ArgumentList.Arguments.Count < 2)
                    throw Invalid(attribute, "Range requires two constant numeric arguments.");
                min = Convert.ToDouble(Constant(attribute.ArgumentList.Arguments[0].Expression), CultureInfo.InvariantCulture);
                max = Convert.ToDouble(Constant(attribute.ArgumentList.Arguments[1].Expression), CultureInfo.InvariantCulture);
                if (double.IsNaN(min) || double.IsInfinity(min) || double.IsNaN(max) || double.IsInfinity(max) || min > max)
                    throw Invalid(attribute, "Range bounds must be finite and ordered.");
                return true;
            }

            private bool TryGetMaxLength(AttributeSyntax attribute, out int maxLength)
            {
                maxLength = 0;
                if (attribute.ArgumentList == null || attribute.ArgumentList.Arguments.Count == 0 || !(Constant(attribute.ArgumentList.Arguments[0].Expression) is int length) || length < 0)
                    throw Invalid(attribute, "MaxLength requires a non-negative constant int.");
                maxLength = length;
                return true;
            }

            private static string GetQualifiedModelName(ClassDeclarationSyntax classDeclaration)
            {
                if (classDeclaration == null)
                    return null;

                var nameParts = new Stack<string>();
                nameParts.Push(classDeclaration.Identifier.Text);

                SyntaxNode current = classDeclaration.Parent;
                while (current is ClassDeclarationSyntax parentClass)
                {
                    nameParts.Push(parentClass.Identifier.Text);
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
                var parts = new Stack<string>();
                while (node != null)
                {
                    if (node is NamespaceDeclarationSyntax ns) parts.Push(ns.Name.ToString());
                    node = node.Parent;
                }
                return string.Join(".", parts);
            }

            private static bool TryClassifyFieldType(TypeSyntax typeSyntax, out PersistenceFieldTypeInfo info)
            {
                info = null;
                if (typeSyntax == null)
                    return false;

                if (typeSyntax is ArrayTypeSyntax arrayType)
                {
                    if (arrayType.RankSpecifiers.Count != 1 || arrayType.RankSpecifiers[0].Rank != 1 || arrayType.ElementType is ArrayTypeSyntax)
                        return false;
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
                string typeName = "global::" + model.QualifiedName;
                string safeName = model.SafeName;
                string displayName = EscapeString(model.DisplayName);
                string keyLiteral = EscapeString(model.Key);

                // A public method taking an internal model is CS0051, so an internal model gets internal
                // overloads. Every behaviour can still call them: the U# compile is one compilation.
                string access = model.IsInternal ? "internal" : "public";

                sb.AppendLine($"        // Auto-generated persistence for {displayName}");
                sb.AppendLine($"        {access} static SaveResult Save({typeName} model)");
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

                sb.AppendLine($"        {access} static DataDictionary ToData({typeName} model)");
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

                sb.AppendLine($"        {access} static string ToJson({typeName} model, bool beautify = false)");
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

                sb.AppendLine($"        {access} static RestoreResult Restore(out {typeName} model)");
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

                sb.AppendLine($"        {access} static RestoreResult FromData(DataDictionary data, out {typeName} model)");
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
                sb.AppendLine("                double versionNumber = versionToken.Number;");
                sb.AppendLine("                if (double.IsNaN(versionNumber) || versionNumber < int.MinValue || versionNumber > int.MaxValue || versionNumber != System.Math.Truncate(versionNumber)) return RestoreResult.ParseError;");
                sb.AppendLine("                int storedVersion = (int)versionNumber;");
                sb.AppendLine("                if (storedVersion != version)");
                sb.AppendLine("                {");
                sb.AppendLine($"                    Debug.LogWarning(\"[CE.Persistence] FromData<{displayName}>: version mismatch (stored: \" + storedVersion + \", expected: \" + version + \")\");");
                sb.AppendLine("                    return RestoreResult.VersionMismatch;");
                sb.AppendLine("                }");
                sb.AppendLine("            }");
                sb.AppendLine();
                sb.AppendLine($"            model = FromData__{safeName}(data, out string invalidField);");
                sb.AppendLine("            if (invalidField != null)");
                sb.AppendLine("            {");
                sb.AppendLine("                PersistenceLifecycle.RecordCorruption(new CorruptionInfo(key, invalidField, CorruptionType.InvalidType, \"Invalid persisted field value\"));");
                sb.AppendLine("                return RestoreResult.ParseError;");
                sb.AppendLine("            }");
                sb.AppendLine("            return RestoreResult.Success;");
                sb.AppendLine("        }");
                sb.AppendLine();

                sb.AppendLine($"        {access} static RestoreResult FromJson(string json, out {typeName} model)");
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

                sb.AppendLine($"        {access} static bool Validate({typeName} model, out List<ValidationError> errors)");
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

                sb.AppendLine($"        {access} static int EstimateSize({typeName} model)");
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
                    sb.AppendLine($"        private static {typeName} FromData__{safeName}(DataDictionary data, out string invalidField)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            invalidField = null;");
                    sb.AppendLine($"            return new {typeName} {{ {model.RawDataFieldName} = data }};");
                    sb.AppendLine("        }");
                    sb.AppendLine();
                    sb.AppendLine($"        private static List<ValidationError> Validate__{safeName}({typeName} model)");
                    sb.AppendLine("        {");
                    sb.AppendLine("            if (model == null)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var nullErrors = new List<ValidationError>();");
                    sb.AppendLine("                nullErrors.Add(new ValidationError(\"_model\", \"_model\", \"Model cannot be null\"));");
                    sb.AppendLine("                return nullErrors;");
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
                sb.AppendLine($"        private static {typeName} FromData__{safeName}(DataDictionary data, out string invalidField)");
                sb.AppendLine("        {");
                sb.AppendLine("            invalidField = null;");
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
                sb.AppendLine("                var nullErrors = new List<ValidationError>();");
                sb.AppendLine("                nullErrors.Add(new ValidationError(\"_model\", \"_model\", \"Model cannot be null\"));");
                sb.AppendLine("                return nullErrors;");
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

            private static string WriteValue(PersistenceFieldTypeInfo type, string expression)
                => type.TypeName == "long" || type.TypeName == "ulong"
                    ? expression + ".ToString(System.Globalization.CultureInfo.InvariantCulture)"
                    : expression;

            private static bool IsReference(PersistenceFieldTypeInfo type)
                => type.Kind != PersistenceFieldKind.Primitive;

            private static void AppendToDataField(StringBuilder sb, PersistenceFieldInfo field)
            {
                string access = "model." + field.Name;
                string key = SyntaxFactory.Literal(field.PersistKey).ToString();
                if (field.Type.Kind == PersistenceFieldKind.Array)
                {
                    sb.AppendLine($"            if ({access} != null)");
                    sb.AppendLine("            {");
                    sb.AppendLine("                var list = new DataList();");
                    sb.AppendLine($"                for (int i = 0; i < {access}.Length; i++)");
                    sb.AppendLine("                {");
                    string item = access + "[i]";
                    if (IsReference(field.Type.ElementType))
                        sb.AppendLine($"                    if ({item} == null) list.Add(new DataToken()); else list.Add({WriteValue(field.Type.ElementType, item)});");
                    else
                        sb.AppendLine($"                    list.Add({WriteValue(field.Type.ElementType, item)});");
                    sb.AppendLine("                }");
                    sb.AppendLine($"                dict[{key}] = list;");
                    sb.AppendLine("            }");
                }
                else
                {
                    string guard = IsReference(field.Type) ? $"if ({access} != null) " : "";
                    sb.AppendLine($"            {guard}dict[{key}] = {WriteValue(field.Type, access)};");
                }
            }

            private static void AppendRead(StringBuilder sb, PersistenceFieldTypeInfo type, string token, string target, string key)
            {
                string fail = "{ invalidField = " + key + "; return null; }";
                if (type.Kind == PersistenceFieldKind.Primitive && (type.TypeName == "long" || type.TypeName == "ulong"))
                {
                    string primitive = type.TypeName;
                    sb.AppendLine($"                    if ({token}.TokenType == TokenType.String)");
                    sb.AppendLine("                    {");
                    sb.AppendLine($"                        if (!{primitive}.TryParse({token}.String, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out {primitive} parsed)) {fail}");
                    sb.AppendLine($"                        {target} = parsed;");
                    sb.AppendLine("                    }");
                    sb.AppendLine($"                    else if ({token}.TokenType == TokenType.{(primitive == "long" ? "Long" : "ULong")}) {target} = {token}.{(primitive == "long" ? "Long" : "ULong")};");
                    sb.AppendLine("                    else");
                    sb.AppendLine("                    {");
                    string min = primitive == "long" ? "-9223372036854775808d" : "0d";
                    string max = primitive == "long" ? "9223372036854775808d" : "18446744073709551616d";
                    sb.AppendLine($"                        if (!IsNumericToken({token}.TokenType)) {fail}");
                    sb.AppendLine($"                        double number = {token}.Number;");
                    sb.AppendLine($"                        if (double.IsNaN(number) || number < {min} || number >= {max} || number != System.Math.Truncate(number)) {fail}");
                    sb.AppendLine($"                        {target} = ({primitive})number;");
                    sb.AppendLine("                    }");
                    return;
                }

                string valid;
                switch (type.Kind)
                {
                    case PersistenceFieldKind.String: valid = token + ".TokenType == TokenType.String"; break;
                    case PersistenceFieldKind.DataDictionary: valid = token + ".TokenType == TokenType.DataDictionary"; break;
                    case PersistenceFieldKind.DataList: valid = token + ".TokenType == TokenType.DataList"; break;
                    default:
                        if (type.TypeName == "bool") valid = token + ".TokenType == TokenType.Boolean";
                        else
                        {
                            valid = $"IsNumericToken({token}.TokenType) && !double.IsNaN({token}.Number) && !double.IsInfinity({token}.Number)";
                            if (type.TypeName != "double") valid += $" && {token}.Number >= {type.TypeName}.MinValue && {token}.Number <= {type.TypeName}.MaxValue";
                            if (type.TypeName != "float" && type.TypeName != "double") valid += $" && {token}.Number == System.Math.Truncate({token}.Number)";
                        }
                        break;
                }
                if (IsReference(type)) sb.AppendLine($"                    if ({token}.TokenType == TokenType.Null) {target} = null; else");
                sb.AppendLine($"                    if ({valid}) {target} = {type.GetReadExpression(token)}; else {fail}");
            }

            private static void AppendFromDataField(StringBuilder sb, PersistenceFieldInfo field)
            {
                string key = SyntaxFactory.Literal(field.PersistKey).ToString();
                string target = "model." + field.Name;
                sb.AppendLine($"            if (data.TryGetValue({key}, out token))");
                sb.AppendLine("            {");
                if (field.Type.Kind == PersistenceFieldKind.Array)
                {
                    sb.AppendLine($"                if (token.TokenType == TokenType.Null) {target} = null; else");
                    sb.AppendLine("                if (token.TokenType == TokenType.DataList)");
                    sb.AppendLine("                {");
                    sb.AppendLine("                    var list = token.DataList;");
                    sb.AppendLine($"                    var array = new {field.Type.ElementType.TypeName}[list.Count];");
                    sb.AppendLine("                    for (int i = 0; i < list.Count; i++)");
                    sb.AppendLine("                    {");
                    sb.AppendLine("                        DataToken itemToken = list[i];");
                    AppendRead(sb, field.Type.ElementType, "itemToken", "array[i]", key);
                    sb.AppendLine("                    }");
                    sb.AppendLine($"                    {target} = array;");
                    sb.AppendLine("                }");
                    sb.AppendLine($"                else {{ invalidField = {key}; return null; }}");
                }
                else AppendRead(sb, field.Type, "token", target, key);
                sb.AppendLine("            }");
                if (field.Optional && !string.IsNullOrEmpty(field.DefaultValueExpression))
                    sb.AppendLine($"            else {target} = ({field.Type.TypeName})({field.DefaultValueExpression});");
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
                        string min = field.RangeMin.Value.ToString("R", CultureInfo.InvariantCulture);
                        string max = field.RangeMax.Value.ToString("R", CultureInfo.InvariantCulture);

                        // The bounds are emitted as double literals. "R" prints an integral bound without a
                        // decimal point, and a bare negative int literal cannot be compared with a ulong
                        // (CS0034), so Range(-1, 10) on a ulong field failed the compile; against a double every
                        // field type compares, exactly as RangeAttribute.IsValid compares them.
                        sb.AppendLine($"            if ({fieldAccess} < {min}d || {fieldAccess} > {max}d)");
                        AppendValidationError(sb, field, persistKeyLiteral, $"\"Value must be between {min} and {max}\"", fieldAccess);
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
                string literal = SyntaxFactory.Literal(value ?? string.Empty).ToString();
                return literal.Substring(1, literal.Length - 2);
            }
        }

        private sealed class PersistenceModelInfo
        {
            public string QualifiedName { get; set; }
            public string DisplayName { get; set; }
            public string Namespace { get; set; } = string.Empty;
            public string DeclaredFile { get; set; }
            public string Key { get; set; }
            public Location KeyLocation { get; set; }
            public int Version { get; set; } = 1;
            public bool HasRawDataDictionary { get; set; }
            public string RawDataFieldName { get; set; }
            public bool HasDeclaredPersistFields { get; set; }
            public bool HasPersistFields => Fields.Count > 0;
            public bool HasUnsupportedFields { get; set; }

            /// <summary>True when the model is only internally visible, so its overloads must be internal too.</summary>
            public bool IsInternal { get; set; }

            /// <summary>First DataDictionary field that could not be the raw payload, kept to explain a skip.</summary>
            public SyntaxNode InaccessibleRawCandidate { get; set; }

            public List<PersistenceFieldInfo> Fields { get; } = new List<PersistenceFieldInfo>();
            public string SafeName => SanitizeTypeName(string.IsNullOrEmpty(QualifiedName) ? DisplayName : QualifiedName);

            public bool HasValidation => Fields.Any(field => field.HasValidation);

            public void AddField(PersistenceFieldInfo field)
            {
                if (Fields.Any(existing => string.Equals(existing.Name, field.Name, StringComparison.Ordinal)))
                    return;

                // __ce_version and __ce_model are written into the same dictionary after the fields, so a field
                // under either name was silently overwritten on every save.
                if (field.PersistKey.StartsWith("__ce_", StringComparison.Ordinal))
                    throw new CodeGenerationException("CEPERSIST001", field.Location ?? Location.None,
                        $"[PersistKey(\"{field.PersistKey}\")] on '{DisplayName}.{field.Name}' is reserved: keys starting with __ce_ belong to CE.");

                // Two fields under one key write over each other on save and both read the survivor back,
                // through two converters that disagree about its type.
                PersistenceFieldInfo clash = Fields.FirstOrDefault(existing => existing.PersistKey == field.PersistKey);
                if (clash != null)
                    throw new CodeGenerationException("CEPERSIST001", field.Location ?? Location.None,
                        $"[PersistKey(\"{field.PersistKey}\")] is used by both '{DisplayName}.{clash.Name}' and '{DisplayName}.{field.Name}'. Give each field its own key.");

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
                uint hash = 2166136261;
                foreach (char c in name) hash = unchecked((hash ^ c) * 16777619);
                return sb.ToString() + "_" + hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        private sealed class PersistenceFieldInfo
        {
            public string Name { get; set; }
            public string PersistKey { get; set; }
            public PersistenceFieldTypeInfo Type { get; set; }
            public Location Location { get; set; }
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
                        return $"(float){tokenName}.Number";
                    case "double":
                        return $"{tokenName}.Number";
                    case "byte":
                        return $"(byte){tokenName}.Number";
                    case "sbyte":
                        return $"(sbyte){tokenName}.Number";
                    case "short":
                        return $"(short){tokenName}.Number";
                    case "ushort":
                        return $"(ushort){tokenName}.Number";
                    case "int":
                        return $"(int){tokenName}.Number";
                    case "uint":
                        return $"(uint){tokenName}.Number";
                    case "long":
                        return $"(long){tokenName}.Number";
                    case "ulong":
                        return $"(ulong){tokenName}.Number";
                    default:
                        return $"{tokenName}.Number";
                }
            }
        }
    }
}
