using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.Compiler;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Injects quantization logic into OnPreSerialization for fields using [Sync(Quantize = ...)].
    /// </summary>
    internal class QuantizePreSerializeEmitter : ICompileTimeOptimizer
    {
        private const string HelperMethodName = "_CE_ApplyQuantizePreSerialization";

        public string OptimizerId => "CEGEN002";
        public string OptimizerName => "Quantize Pre-Serialization Emitter";
        public string Description => "Rounds quantized Sync fields before serialization.";
        public bool IsEnabledByDefault => true;
        public int Priority => 12;

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            SyntaxNode root = tree.GetRoot();
            SemanticModel semanticModel = context.GetSemanticModel(tree);
            var rewriter = new Rewriter(context, semanticModel);
            SyntaxNode newRoot = rewriter.Visit(root);

            if (rewriter.ChangesMade)
                return tree.WithRootAndOptions(newRoot, tree.Options);

            return tree;
        }

        private sealed class Rewriter : CSharpSyntaxRewriter
        {
            private readonly OptimizationContext _context;
            private readonly SemanticModel _semanticModel;

            public bool ChangesMade { get; private set; }

            public Rewriter(OptimizationContext context, SemanticModel semanticModel)
            {
                _context = context;
                _semanticModel = semanticModel;
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                ClassDeclarationSyntax visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);

                // Query the ORIGINAL node: children of the rewritten node are not in the semantic model's
                // syntax tree, and GetDeclaredSymbol/GetTypeInfo throw ArgumentException on them the moment
                // base.VisitClassDeclaration rewrote anything -- a nested class is enough. That throw is
                // now a fatal generator failure, so it would abort the whole compile.
                INamedTypeSymbol declaredType = _semanticModel?.GetDeclaredSymbol(node);
                if (declaredType == null)
                {
                    // Without a resolved symbol there is no way to prove this type inherits
                    // OnPreSerialization, and emitting the override onto one that does not is a bare
                    // CS0115 naming neither CE nor the attribute. Skip rather than guess.
                    return visited;
                }

                List<QuantizeFieldSpec> fields = CollectQuantizedFields(declaredType);
                bool isBehaviour = IsUdonSharpBehaviour(declaredType);

                // A behaviour with no quantized field of its own still inherits the rounding its bases
                // declare -- through an OnPreSerialization that is generated, so nothing in the source
                // says an override of it ought to call base. One that did not simply stopped the base's
                // fields being rounded for every instance of the derived behaviour.
                List<string> inheritedHelpers = isBehaviour
                    ? CollectInheritedHelperNames(declaredType)
                    : new List<string>();

                if (fields.Count == 0 && inheritedHelpers.Count == 0)
                    return visited;

                if (!isBehaviour)
                {
                    // A plain class cannot carry [Sync] at all: UdonSynced exists only on behaviours, so the
                    // field never leaves the local client and rounding it accomplishes nothing. Only a
                    // confirmed CE [Sync] is worth failing over -- an unrelated user attribute that happens
                    // to be named Sync resolves to a different symbol and is left alone.
                    if (HasConfirmedSyncQuantizeAttribute(node))
                    {
                        throw new CodeGenerationException(
                            "CE0060",
                            node.Identifier.GetLocation(),
                            $"'{declaredType.Name}' declares [Sync(Quantize = ...)] but is not an " +
                            "UdonSharpBehaviour. [Sync] inherits [UdonSynced], which VRChat only applies to " +
                            "behaviours, so the field is never synchronized and quantizing it before " +
                            "serialization is meaningless. " +
                            "Fix: move the field onto an UdonSharpBehaviour, or drop the [Sync] attribute.");
                    }

                    return visited;
                }

                // A partial type has one symbol and many declaration parts, and 'visited' is only the part
                // being visited right now. Deciding "does this already exist?" from the part emitted the
                // helper and the OnPreSerialization override once per part -- CS0111 twice over -- whenever
                // an author split a behaviour's quantized fields across files. Every once-per-type question
                // below is therefore asked of the symbol, and the generated members land in exactly one part.
                bool isCanonicalPart = IsCanonicalDeclarationPart(declaredType, node);
                string helperName = GetHelperMethodName(declaredType);

                bool classChanged = false;
                if (fields.Count > 0 && isCanonicalPart && !DeclaresMethod(declaredType, helperName))
                {
                    visited = visited.AddMembers(ParseMember(BuildHelperMethod(helperName, fields, declaredType.IsSealed)));
                    ChangesMade = true;
                    classChanged = true;
                }

                MethodDeclarationSyntax onPreSerialization = visited.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(method => method.Identifier.Text == "OnPreSerialization" && method.ParameterList.Parameters.Count == 0);

                if (onPreSerialization == null)
                {
                    // Another part of this same type owns the handler and gets the helper call injected when
                    // that part is visited; adding a second declaration here is CS0111.
                    //
                    // A type with no quantized field of its own needs no handler either: the one it
                    // inherits already rounds every field the hierarchy has.
                    if (fields.Count > 0 && isCanonicalPart && !DeclaresMethod(declaredType, "OnPreSerialization"))
                    {
                        visited = visited.AddMembers(ParseMember(BuildOnPreSerializationMethod(helperName)));
                        ChangesMade = true;
                        classChanged = true;
                    }
                }
                else
                {
                    // A handler the author wrote may never call base, so it is handed every quantized base's
                    // helper as well as this type's own -- including one that does call base, because the
                    // call can sit behind an early return ('if (skip) return; base.OnPreSerialization();')
                    // and those paths still serialize. On the paths that do reach base the fields are rounded
                    // twice, which changes nothing: a value already on the grid rounds to itself.
                    var missingHelpers = new List<string>();

                    if (inheritedHelpers.Count > 0)
                    {
                        foreach (string inheritedHelper in inheritedHelpers)
                        {
                            if (!CallsHelper(onPreSerialization, inheritedHelper))
                                missingHelpers.Add(inheritedHelper);
                        }
                    }

                    if (fields.Count > 0 && !CallsHelper(onPreSerialization, helperName))
                        missingHelpers.Add(helperName);

                    if (missingHelpers.Count > 0)
                    {
                        MethodDeclarationSyntax updatedMethod = InjectHelperCalls(onPreSerialization, missingHelpers);
                        if (!ReferenceEquals(updatedMethod, onPreSerialization))
                        {
                            visited = visited.ReplaceNode(onPreSerialization, updatedMethod);
                            ChangesMade = true;
                            classChanged = true;
                        }
                    }
                }

                if (classChanged)
                {
                    // The location comes from the original node. 'visited' is detached from the input tree,
                    // so its location prints as "Unknown File(1,1)" in the optimization report.
                    _context.RecordOptimization(
                        optimizerId: "CEGEN002",
                        description: $"Injected quantization into '{node.Identifier.Text}.OnPreSerialization'",
                        location: node.GetLocation(),
                        originalCode: node.Identifier.Text,
                        optimizedCode: helperName);
                }

                return visited;
            }

            /// <summary>
            /// The helper's name on one behaviour. It carries the declaring type's name because UdonSharp
            /// rejects any method that repeats a base behaviour's name and parameters without overriding it
            /// ("does not yet support hiding base methods"), private ones included. Under one shared name a
            /// base and a derived behaviour that both quantize fields could not be compiled at all -- while
            /// the same generated source built cleanly as plain C#, which is all the tests then checked.
            /// </summary>
            private static string GetHelperMethodName(INamedTypeSymbol type)
            {
                string name = HelperMethodName + "_" + type.Name;

                // Two behaviours in one hierarchy can share a simple name across namespaces, and they
                // would collide again. Counting the same-named bases above keeps each one distinct.
                int sameNamedBases = 0;

                for (INamedTypeSymbol current = type.BaseType; current != null; current = current.BaseType)
                {
                    if (current.Name == type.Name)
                        sameNamedBases++;
                }

                return sameNamedBases == 0
                    ? name
                    : name + "_" + sameNamedBases.ToString(CultureInfo.InvariantCulture);
            }

            /// <summary>
            /// The helpers of every base behaviour that declares quantized fields, base-most first. Each of
            /// those behaviours emits its own when it is visited, so the name is derived here the same way
            /// rather than looked up: this pass's output is not in the semantic model yet.
            /// </summary>
            private List<string> CollectInheritedHelperNames(INamedTypeSymbol declaredType)
            {
                var names = new List<string>();

                for (INamedTypeSymbol current = declaredType.BaseType;
                     current != null && current.ToDisplayString() != "UdonSharp.UdonSharpBehaviour";
                     current = current.BaseType)
                {
                    // Only a behaviour compiled from source gets a helper generated onto it.
                    if (current.DeclaringSyntaxReferences.Length == 0)
                        continue;

                    if (CollectQuantizedFields(current).Count > 0)
                        names.Insert(0, GetHelperMethodName(current));
                }

                return names;
            }

            /// <summary>
            /// Collects every quantized field the type declares, read off the merged type symbol so that a
            /// partial behaviour contributes all of its parts' fields to the one generated helper. Walking
            /// the visited declaration part instead produced a helper per part, each covering only the fields
            /// that part happened to declare.
            /// </summary>
            private List<QuantizeFieldSpec> CollectQuantizedFields(INamedTypeSymbol declaredType)
            {
                var fields = new List<QuantizeFieldSpec>();

                foreach (IFieldSymbol field in declaredType.GetMembers().OfType<IFieldSymbol>())
                {
                    // A const cannot be assigned at all (CS0131) and a readonly cannot be assigned outside a
                    // constructor (CS0191), so quantizing either one is a fatal generator-authored compile
                    // error on source a user could reasonably write. A static holds no per-instance state and
                    // [UdonSynced] never applies to one, so rounding it before serialization does nothing.
                    if (field.IsImplicitlyDeclared || field.IsStatic || field.IsConst || field.IsReadOnly)
                        continue;

                    if (!TryGetQuantizeExpression(field, out string quantizeExpression))
                        continue;

                    if (!TryClassifyType(field.Type, out QuantizeKind kind, out bool isArray))
                        continue;

                    fields.Add(new QuantizeFieldSpec(field.Name, quantizeExpression, kind, isArray));
                }

                return fields;
            }

            /// <summary>
            /// Reads the <c>Quantize</c> value off a field.
            ///
            /// The bound attribute is preferred because its value is already a constant: the generated helper
            /// lands in a single declaration part, and a partial behaviour's other parts can live in files
            /// with entirely different <c>using</c> directives, so re-emitting the expression text a field
            /// wrote in another file can fail to resolve. The written syntax is still the fallback, so a
            /// [Sync] that does not bind never silently drops quantization from a behaviour that asked for it.
            /// </summary>
            private static bool TryGetQuantizeExpression(IFieldSymbol field, out string quantizeExpression)
            {
                quantizeExpression = null;

                foreach (AttributeData attribute in field.GetAttributes())
                {
                    if (!IsCeSyncAttributeType(attribute.AttributeClass))
                        continue;

                    foreach (KeyValuePair<string, TypedConstant> argument in attribute.NamedArguments)
                    {
                        if (argument.Key != "Quantize" || !(argument.Value.Value is float quantizeValue))
                            continue;

                        // A non-finite step never passes the generated 'step > 0f' guard, so the field is
                        // unquantized either way -- but 'float.NaN' has no literal form and emitting one
                        // would not compile.
                        if (float.IsNaN(quantizeValue) || float.IsInfinity(quantizeValue))
                            return false;

                        quantizeExpression = FormatQuantizeLiteral(quantizeValue);
                        return true;
                    }
                }

                foreach (SyntaxReference reference in field.DeclaringSyntaxReferences)
                {
                    FieldDeclarationSyntax declaration = reference.GetSyntax().FirstAncestorOrSelf<FieldDeclarationSyntax>();
                    if (declaration != null &&
                        TryGetQuantizeExpression(declaration.AttributeLists, out quantizeExpression))
                    {
                        return true;
                    }
                }

                return false;
            }

            /// <summary>Round-trip ("R") so the emitted literal is the exact float the author wrote.</summary>
            private static string FormatQuantizeLiteral(float quantizeValue)
            {
                return quantizeValue.ToString("R", CultureInfo.InvariantCulture) + "f";
            }

            /// <summary>
            /// Confirms through the semantic model that a field really carries CE's
            /// <c>[Sync(Quantize = ...)]</c> and not some unrelated attribute that shares the short name.
            /// Only used to decide whether a non-behaviour is worth aborting the compile over -- emission
            /// itself stays on the syntactic match so an unresolvable attribute never silently drops
            /// quantization from a behaviour that needs it.
            /// </summary>
            private bool HasConfirmedSyncQuantizeAttribute(ClassDeclarationSyntax node)
            {
                foreach (FieldDeclarationSyntax fieldDeclaration in node.Members.OfType<FieldDeclarationSyntax>())
                {
                    foreach (AttributeListSyntax list in fieldDeclaration.AttributeLists)
                    {
                        foreach (AttributeSyntax attribute in list.Attributes)
                        {
                            if (attribute.ArgumentList == null)
                                continue;

                            if (!attribute.ArgumentList.Arguments.Any(argument => argument.NameEquals?.Name.Identifier.Text == "Quantize"))
                                continue;

                            if (IsCeSyncAttributeType(_semanticModel?.GetTypeInfo(attribute).Type))
                                return true;
                        }
                    }
                }

                return false;
            }

            private static bool IsCeSyncAttributeType(ITypeSymbol attributeType)
            {
                for (INamedTypeSymbol current = attributeType as INamedTypeSymbol; current != null; current = current.BaseType)
                {
                    if (current.ToDisplayString() == "UdonSharp.CE.Net.SyncAttribute")
                        return true;
                }

                return false;
            }

            private static bool IsUdonSharpBehaviour(INamedTypeSymbol typeSymbol)
            {
                for (INamedTypeSymbol current = typeSymbol; current != null; current = current.BaseType)
                {
                    if (current.ToDisplayString() == "UdonSharp.UdonSharpBehaviour")
                        return true;
                }

                return false;
            }

            private static bool TryGetQuantizeExpression(SyntaxList<AttributeListSyntax> attributeLists, out string quantizeExpression)
            {
                quantizeExpression = null;

                foreach (AttributeListSyntax list in attributeLists)
                {
                    foreach (AttributeSyntax attribute in list.Attributes)
                    {
                        string name = attribute.Name.ToString().Split('.').Last();
                        if (name != "Sync" && name != "SyncAttribute")
                            continue;

                        if (attribute.ArgumentList == null)
                            continue;

                        foreach (AttributeArgumentSyntax argument in attribute.ArgumentList.Arguments)
                        {
                            if (argument.NameEquals?.Name.Identifier.Text == "Quantize")
                            {
                                quantizeExpression = argument.Expression.ToString();
                                return true;
                            }
                        }
                    }
                }

                return false;
            }

            private static bool TryClassifyType(ITypeSymbol typeSymbol, out QuantizeKind kind, out bool isArray)
            {
                kind = QuantizeKind.Unsupported;
                isArray = false;

                if (typeSymbol is IArrayTypeSymbol arrayType)
                {
                    if (arrayType.Rank != 1)
                        return false;

                    isArray = true;
                    typeSymbol = arrayType.ElementType;
                }

                if (typeSymbol == null)
                    return false;

                switch (typeSymbol.SpecialType)
                {
                    case SpecialType.System_Single:
                        kind = QuantizeKind.Float;
                        return true;
                    case SpecialType.System_Double:
                        kind = QuantizeKind.Double;
                        return true;
                }

                string typeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat).Replace("global::", string.Empty);
                switch (typeName)
                {
                    case "UnityEngine.Vector2":
                        kind = QuantizeKind.Vector2;
                        return true;
                    case "UnityEngine.Vector3":
                        kind = QuantizeKind.Vector3;
                        return true;
                    case "UnityEngine.Vector4":
                        kind = QuantizeKind.Vector4;
                        return true;
                    case "UnityEngine.Quaternion":
                        kind = QuantizeKind.Quaternion;
                        return true;
                    case "UnityEngine.Color":
                        kind = QuantizeKind.Color;
                        return true;
                    default:
                        return false;
                }
            }

            /// <summary>
            /// Protected rather than private so a derived behaviour's own OnPreSerialization can be handed
            /// the call when it never chains to base. A sealed behaviour has nothing deriving from it, and
            /// a protected member there only earns CS0628.
            /// </summary>
            private static string BuildHelperMethod(string helperName, IReadOnlyList<QuantizeFieldSpec> fields, bool isSealed)
            {
                var builder = new StringBuilder();
                builder.AppendLine($"{(isSealed ? "private" : "protected")} void {helperName}()");
                builder.AppendLine("{");

                foreach (QuantizeFieldSpec field in fields)
                {
                    AppendIndentedLines(builder, BuildQuantizeStatements(field), 1);
                }

                builder.AppendLine("}");
                return builder.ToString();
            }

            private static IEnumerable<string> BuildQuantizeStatements(QuantizeFieldSpec field)
            {
                // The field as it has to be written in an expression. A symbol's name carries no '@', so a
                // field declared as '@float' would otherwise be emitted as the keyword and not parse. The
                // generated locals below only use the name as a suffix, where a keyword is harmless.
                string fieldAccess = EscapeIdentifier(field.FieldName);

                string quantizeVar = $"__ce_quantize_{field.FieldName}";
                yield return "{";
                yield return $"    float {quantizeVar} = {field.QuantizeExpression};";
                yield return $"    if ({quantizeVar} > 0f)";
                yield return "    {";

                if (!field.IsArray)
                {
                    foreach (string line in BuildSingleValueQuantizeLines(field, fieldAccess, quantizeVar, "        "))
                        yield return line;
                }
                else
                {
                    string indexName = $"__ce_i_{field.FieldName}";
                    yield return $"        if ({fieldAccess} != null)";
                    yield return "        {";
                    yield return $"            for (int {indexName} = 0; {indexName} < {fieldAccess}.Length; {indexName}++)";
                    yield return "            {";
                    foreach (string line in BuildSingleValueQuantizeLines(field, $"{fieldAccess}[{indexName}]", quantizeVar, "                "))
                        yield return line;
                    yield return "            }";
                    yield return "        }";
                }

                yield return "    }";
                yield return "}";
            }

            /// <summary>
            /// The base call is not optional. Each behaviour in a hierarchy that declares quantized fields
            /// gets its own helper plus its own OnPreSerialization override, and an override hides the base
            /// one -- so without this call a derived behaviour's override simply stopped the base class's
            /// quantized fields from ever being rounded, silently and only over the network. Calling into
            /// UdonSharpBehaviour's own OnPreSerialization is a no-op, so this is safe at the base-most type
            /// too.
            /// </summary>
            private static string BuildOnPreSerializationMethod(string helperName)
            {
                return $@"
public override void OnPreSerialization()
{{
    base.OnPreSerialization();
    {helperName}();
}}
";
            }

            private static string EscapeIdentifier(string name)
            {
                if (string.IsNullOrEmpty(name))
                    return name;

                return SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;
            }

            /// <summary>
            /// A partial type has one symbol and several declaration parts. Emitting into every part gives
            /// two '_CE_ApplyQuantizePreSerialization' bodies and two 'public override void
            /// OnPreSerialization()' declarations (CS0111), so exactly one part -- the first by file path
            /// then position -- carries the generated members. The choice only depends on the symbol, so it
            /// is identical no matter which tree is visited first.
            /// </summary>
            private static bool IsCanonicalDeclarationPart(INamedTypeSymbol type, TypeDeclarationSyntax node)
            {
                ImmutableArray<SyntaxReference> references = type.DeclaringSyntaxReferences;
                if (references.Length <= 1)
                    return true;

                SyntaxReference canonical = references
                    .OrderBy(reference => reference.SyntaxTree.FilePath, StringComparer.Ordinal)
                    .ThenBy(reference => reference.Span.Start)
                    .First();

                return canonical.SyntaxTree == node.SyntaxTree && canonical.Span == node.Span;
            }

            /// <summary>
            /// Whether the type itself declares a parameterless method with this name, in any partial part.
            /// Inherited members are deliberately not counted: an inherited OnPreSerialization is exactly the
            /// case this generator overrides.
            /// </summary>
            private static bool DeclaresMethod(INamedTypeSymbol type, string methodName)
            {
                return type != null && type.GetMembers(methodName)
                    .OfType<IMethodSymbol>()
                    .Any(method => !method.IsImplicitlyDeclared && method.Parameters.Length == 0);
            }

            private static bool CallsHelper(MethodDeclarationSyntax method, string helperName)
            {
                return method.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>()
                    .Any(invocation => invocation.Expression is IdentifierNameSyntax identifier && identifier.Identifier.Text == helperName);
            }

            private static MethodDeclarationSyntax InjectHelperCalls(MethodDeclarationSyntax method, IReadOnlyList<string> helperNames)
            {
                StatementSyntax[] helperCalls = helperNames
                    .Select(helperName => SyntaxFactory.ParseStatement($"{helperName}();"))
                    .ToArray();

                if (method.ExpressionBody != null)
                {
                    var statements = new List<StatementSyntax>
                    {
                        SyntaxFactory.ExpressionStatement(method.ExpressionBody.Expression)
                    };

                    statements.AddRange(helperCalls);

                    return method
                        .WithExpressionBody(null)
                        .WithSemicolonToken(default)
                        .WithBody(SyntaxFactory.Block(statements));
                }

                if (method.Body == null)
                    return method;

                // Decide on the ORIGINAL body. The injector replaces each return with a block containing the
                // helper call, so afterwards the last statement is a BlockSyntax and a check run on the
                // result appended a second helper call sitting unreachable behind the return it had just
                // guarded.
                bool alreadyExits = EndsWithExit(method.Body);

                var injector = new ReturnInjector(helperCalls);
                BlockSyntax updatedBody = (BlockSyntax)injector.Visit(method.Body);
                if (!alreadyExits)
                {
                    updatedBody = updatedBody.AddStatements(helperCalls);
                }

                return method.WithBody(updatedBody);
            }

            private static bool EndsWithExit(BlockSyntax body)
            {
                StatementSyntax last = body.Statements.LastOrDefault();

                if (last is BlockSyntax nested)
                    return EndsWithExit(nested);

                return last is ReturnStatementSyntax || last is ThrowStatementSyntax;
            }

            private static void AppendIndentedLines(StringBuilder builder, IEnumerable<string> lines, int indentLevel)
            {
                string indent = new string(' ', indentLevel * 4);
                foreach (string line in lines)
                {
                    if (line.Length == 0)
                    {
                        builder.AppendLine();
                    }
                    else
                    {
                        builder.Append(indent);
                        builder.AppendLine(line);
                    }
                }
            }

            private static MemberDeclarationSyntax ParseMember(string memberSource)
            {
                CompilationUnitSyntax unit = SyntaxFactory.ParseCompilationUnit("class __CEQuantizeHost__ {" + memberSource + "}");
                return ((ClassDeclarationSyntax)unit.Members[0]).Members[0];
            }

            private static IEnumerable<string> BuildSingleValueQuantizeLines(
                QuantizeFieldSpec field,
                string valueExpression,
                string quantizeVar,
                string indent)
            {
                switch (field.Kind)
                {
                    case QuantizeKind.Float:
                        yield return $"{indent}{valueExpression} = global::UnityEngine.Mathf.Round({valueExpression} / {quantizeVar}) * {quantizeVar};";
                        yield break;
                    case QuantizeKind.Double:
                        yield return $"{indent}{valueExpression} = global::System.Math.Round({valueExpression} / (double){quantizeVar}) * (double){quantizeVar};";
                        yield break;
                    case QuantizeKind.Vector2:
                    case QuantizeKind.Vector3:
                    case QuantizeKind.Vector4:
                    case QuantizeKind.Quaternion:
                    case QuantizeKind.Color:
                        string tempVar = $"{indent}global::{GetStructTypeName(field.Kind)} __ce_tmp_{field.FieldName} = {valueExpression};";
                        yield return tempVar;
                        foreach (string component in GetComponents(field.Kind))
                        {
                            yield return $"{indent}__ce_tmp_{field.FieldName}.{component} = global::UnityEngine.Mathf.Round(__ce_tmp_{field.FieldName}.{component} / {quantizeVar}) * {quantizeVar};";
                        }
                        yield return $"{indent}{valueExpression} = __ce_tmp_{field.FieldName};";
                        yield break;
                    default:
                        yield break;
                }
            }

            private static string GetStructTypeName(QuantizeKind kind)
            {
                switch (kind)
                {
                    case QuantizeKind.Vector2:
                        return "UnityEngine.Vector2";
                    case QuantizeKind.Vector3:
                        return "UnityEngine.Vector3";
                    case QuantizeKind.Vector4:
                        return "UnityEngine.Vector4";
                    case QuantizeKind.Quaternion:
                        return "UnityEngine.Quaternion";
                    case QuantizeKind.Color:
                        return "UnityEngine.Color";
                    default:
                        throw new InvalidOperationException($"Kind '{kind}' does not map to a struct type.");
                }
            }

            private static string[] GetComponents(QuantizeKind kind)
            {
                switch (kind)
                {
                    case QuantizeKind.Vector2:
                        return new[] { "x", "y" };
                    case QuantizeKind.Vector3:
                        return new[] { "x", "y", "z" };
                    case QuantizeKind.Vector4:
                    case QuantizeKind.Quaternion:
                        return new[] { "x", "y", "z", "w" };
                    case QuantizeKind.Color:
                        return new[] { "r", "g", "b", "a" };
                    default:
                        throw new InvalidOperationException($"Kind '{kind}' does not expose quantizable components.");
                }
            }

            private sealed class ReturnInjector : CSharpSyntaxRewriter
            {
                private readonly StatementSyntax[] _helperCalls;

                public ReturnInjector(StatementSyntax[] helperCalls)
                {
                    _helperCalls = helperCalls;
                }

                public override SyntaxNode VisitReturnStatement(ReturnStatementSyntax node)
                {
                    var statements = new List<StatementSyntax>(_helperCalls);
                    statements.Add(node);
                    return SyntaxFactory.Block(statements);
                }

                // A return inside a lambda, an anonymous method or a local function exits that body, not
                // OnPreSerialization. Rewriting those ran the quantize helper every time the callback fired
                // and left the serialization path itself unquantized.
                public override SyntaxNode VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node) => node;

                public override SyntaxNode VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node) => node;

                public override SyntaxNode VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node) => node;

                public override SyntaxNode VisitLocalFunctionStatement(LocalFunctionStatementSyntax node) => node;
            }
        }

        private sealed class QuantizeFieldSpec
        {
            public QuantizeFieldSpec(string fieldName, string quantizeExpression, QuantizeKind kind, bool isArray)
            {
                FieldName = fieldName;
                QuantizeExpression = quantizeExpression;
                Kind = kind;
                IsArray = isArray;
            }

            public string FieldName { get; }
            public string QuantizeExpression { get; }
            public QuantizeKind Kind { get; }
            public bool IsArray { get; }
        }

        private enum QuantizeKind
        {
            Unsupported,
            Float,
            Double,
            Vector2,
            Vector3,
            Vector4,
            Quaternion,
            Color
        }
    }
}
