using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.CE.Editor.Net;
using UdonSharp.Compiler;

namespace UdonSharp.CE.Editor.Optimizers
{
    /// <summary>
    /// Emits per-behaviour SyncOnJoin serialize/deserialize glue into UdonSharp behaviours.
    /// </summary>
    internal class SyncOnJoinGlueEmitter : ICompileTimeOptimizer
    {
        private const string GeneratorId = "CEGEN001";

        // Base names for the generated transport. A behaviour emits one COMPLETE set of these per
        // distinct SyncOnJoinAttribute.Group, each suffixed with that group's sanitised identifier, so
        // changing a field in one group re-sends only that group's payload. The default (unnamed) group
        // keeps the bare names, which is what a behaviour that never mentions Group has always emitted --
        // and what SendCustomEvent("_CE_SyncOnJoin_RequestCapture") and existing tests address.
        private const string PayloadFieldName = "_CE_SyncOnJoinPayload";
        private const string AppliedFieldName = "_CE_SyncOnJoinApplied";
        private const string SerializeMethodName = "_CE_SyncOnJoin_Serialize";
        private const string DeserializeMethodName = "_CE_SyncOnJoin_Deserialize_Trigger";
        private const string ApplyMethodName = "_CE_SyncOnJoin_Apply";

        /// <summary>
        /// The one capture entry point, whatever the group count. It is not per-group because
        /// RequestSerialization() is per BEHAVIOUR: splitting it would make N calls where one does, while
        /// each group's own change check still decides whether anything is sent at all.
        /// </summary>
        private const string RequestCaptureMethodName = "_CE_SyncOnJoin_RequestCapture";

        private const string ToDataMethodName = "_CE_SyncOnJoin_ToData";
        private const string FromDataMethodName = "_CE_SyncOnJoin_FromData";

        /// <summary>Matches <c>SyncOnJoinAttribute.Priority</c>'s own default.</summary>
        private const int DefaultPriority = 100;

        // JSON has no representation for a non-finite number, so they ride as these exact strings rather
        // than through ToString(), whose infinity symbol is culture dependent.
        private const string NaNSentinel = "NaN";
        private const string PositiveInfinitySentinel = "Infinity";
        private const string NegativeInfinitySentinel = "-Infinity";

        public string OptimizerId => GeneratorId;
        public string OptimizerName => "SyncOnJoin Glue Emitter";
        public string Description => "Generates SyncOnJoin serialization glue and payload fields for late-join sync.";
        public bool IsEnabledByDefault => true;
        public int Priority => 10;

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

                // Query the ORIGINAL node against the semantic model: children of the rewritten node are not
                // in the model's syntax tree, and GetDeclaredSymbol/GetTypeInfo throw on them once any nested
                // type has been rewritten.
                INamedTypeSymbol declaredType = _semanticModel?.GetDeclaredSymbol(node);
                if (declaredType == null)
                    return visited;

                bool isCanonicalPart = IsCanonicalDeclarationPart(declaredType, node);
                var membersToAdd = new List<MemberDeclarationSyntax>();

                if (TryCreateCustomHelperSpec(declaredType, out SyncOnJoinTypeSpec helperSpec) &&
                    isCanonicalPart &&
                    !DeclaresMethod(declaredType, ToDataMethodName) &&
                    !DeclaresMethod(declaredType, FromDataMethodName))
                {
                    Location helperLocation = node.Identifier.GetLocation();
                    membersToAdd.Add(ParseMember(BuildToDataMethod(helperSpec, helperLocation)));
                    membersToAdd.Add(ParseMember(BuildFromDataMethod(helperSpec, declaredType.Name, helperLocation)));
                }

                bool isCarrier = false;
                bool ownsAnyGroup = false;
                IReadOnlyList<SyncGroup> groups = EmptyGroups;

                if (IsUdonSharpBehaviour(declaredType))
                    isCarrier = DeclaresSyncOnJoinFields(declaredType);

                if (isCarrier)
                {
                    groups = CollectSyncOnJoinGroups(declaredType);

                    // Nothing survived type support (every declared field was rejected, each already reported
                    // as CE0032). Emitting the transport anyway would give this behaviour a synced payload
                    // that can only ever carry "{}", which reads as working and delivers nothing.
                    if (groups.Count == 0)
                        isCarrier = false;
                }

                if (isCarrier)
                {
                    // A group's transport lives on the base-most behaviour of the hierarchy that declares a
                    // field in THAT group. A derived behaviour adding fields to a group the base already
                    // owns only overrides the serialize/deserialize pair: a second [UdonSynced] payload
                    // field would be a duplicate synced variable. A derived behaviour introducing a group
                    // of its own does own that group, and carries its transport itself.
                    ownsAnyGroup = groups.Any(group => group.IsOwnedBy(declaredType));

                    if (isCanonicalPart)
                    {
                        Location transportLocation = node.Identifier.GetLocation();

                        foreach (SyncGroup group in groups)
                        {
                            bool ownsGroup = group.IsOwnedBy(declaredType);

                            if (ownsGroup)
                            {
                                if (!DeclaresField(declaredType, group.PayloadField))
                                    membersToAdd.Add(ParseMember(BuildPayloadField(group)));

                                if (!DeclaresField(declaredType, group.AppliedField))
                                    membersToAdd.Add(ParseMember(BuildAppliedField(group)));

                                if (!DeclaresMethod(declaredType, group.ApplyMethod))
                                    membersToAdd.Add(ParseMember(BuildApplyMethod(group)));
                            }

                            // Overriding a group this type contributes nothing to would emit an override
                            // identical to the base's, so the base's own pair already covers every field
                            // the group has.
                            if (!ownsGroup && !group.DeclaredByVisitedType)
                                continue;

                            if (!DeclaresMethod(declaredType, group.SerializeMethod))
                                membersToAdd.Add(ParseMember(BuildSerializeMethod(declaredType.Name, group, !ownsGroup, transportLocation)));

                            if (!DeclaresMethod(declaredType, group.DeserializeMethod))
                                membersToAdd.Add(ParseMember(BuildDeserializeMethod(declaredType.Name, group, !ownsGroup, transportLocation)));
                        }

                        // Capture covers every group in the hierarchy, so a type that introduces one
                        // overrides the base's rather than adding a second entry point the author would
                        // have to know to call.
                        if (ownsAnyGroup && !DeclaresMethod(declaredType, RequestCaptureMethodName))
                        {
                            bool overridesCapture = groups.Any(group => !group.IsOwnedBy(declaredType));
                            membersToAdd.Add(ParseMember(
                                BuildRequestCaptureMethod(groups, overridesCapture, node.Identifier.GetLocation())));
                        }
                    }
                }

                ClassDeclarationSyntax result = visited;

                if (membersToAdd.Count > 0)
                    result = result.AddMembers(membersToAdd.ToArray());

                if (isCarrier)
                    result = WeaveSerializationEvents(result, declaredType, groups, isCanonicalPart && ownsAnyGroup);

                if (ReferenceEquals(result, visited))
                    return visited;

                ChangesMade = true;
                _context.RecordOptimization(
                    optimizerId: GeneratorId,
                    description: $"Generated SyncOnJoin glue for '{node.Identifier.Text}'",
                    location: node.Identifier.GetLocation(),
                    originalCode: node.Identifier.Text,
                    optimizedCode: membersToAdd.Count > 0
                        ? string.Join(", ", membersToAdd.Select(GetMemberName))
                        : "serialization events");

                return result;
            }

            public override SyntaxNode VisitStructDeclaration(StructDeclarationSyntax node)
            {
                StructDeclarationSyntax visited = (StructDeclarationSyntax)base.VisitStructDeclaration(node);
                INamedTypeSymbol declaredType = _semanticModel?.GetDeclaredSymbol(node);

                if (!TryCreateCustomHelperSpec(declaredType, out SyncOnJoinTypeSpec helperSpec) ||
                    !IsCanonicalDeclarationPart(declaredType, node) ||
                    DeclaresMethod(declaredType, ToDataMethodName) ||
                    DeclaresMethod(declaredType, FromDataMethodName))
                {
                    return visited;
                }

                ChangesMade = true;
                Location helperLocation = node.Identifier.GetLocation();
                return visited.AddMembers(
                    ParseMember(BuildToDataMethod(helperSpec, helperLocation)),
                    ParseMember(BuildFromDataMethod(helperSpec, declaredType.Name, helperLocation)));
            }

            private bool TryCreateCustomHelperSpec(INamedTypeSymbol declaredType, out SyncOnJoinTypeSpec helperSpec)
            {
                helperSpec = null;
                if (declaredType == null || IsUdonSharpBehaviour(declaredType))
                    return false;

                if (!SyncOnJoinTypeSupport.TryCreateSpec(declaredType, out SyncOnJoinTypeSpec spec, out _) ||
                    spec.Kind != SyncOnJoinValueKind.CustomObject)
                {
                    return false;
                }

                ApplyRenamedMemberKeys(declaredType, spec);

                helperSpec = spec;
                return true;
            }

            /// <summary>
            /// Restores the <c>[DataField("...")]</c> / <c>[PersistKey("...")]</c> rename on a serialized
            /// model's members.
            /// </summary>
            /// <remarks>
            /// Type support derives the key from the attribute's constructor arguments, which the compiler
            /// only folds once the attribute type binds. UdonSharp compiles the CE runtime library as
            /// source, so CompilationContext.GetMetadataReferences deliberately leaves UdonSharp.CE.Runtime
            /// out of the optimizer's reference set -- and in any pass whose tree set does not happen to
            /// include DataFieldAttribute.cs, [DataField] resolves to an error symbol carrying no
            /// arguments. The key then silently fell back to the field name.
            ///
            /// Silently is the problem. Both halves are generated from this one spec, so writer and reader
            /// agreed on the wrong key and nothing failed until the payload met something that expects the
            /// documented one -- a hand-written reader, a persisted snapshot, another CE build. Reading the
            /// literal off the attribute syntax is the same last-resort ReadSyncSerializerNamesFromSyntax
            /// takes, for the same reason.
            ///
            /// The spec is normalised in one place so BuildToDataMethod and BuildFromDataMethod cannot
            /// disagree: whatever key the serializer writes is the key the deserializer reads.
            /// </remarks>
            private static void ApplyRenamedMemberKeys(INamedTypeSymbol declaredType, SyncOnJoinTypeSpec spec)
            {
                if (spec.Members == null)
                    return;

                foreach (SyncOnJoinMemberSpec member in spec.Members)
                {
                    IFieldSymbol field = FindSerializedField(declaredType, member.FieldName);
                    if (field == null)
                        continue;

                    string renamed = ReadKeyArgument(field, "UdonSharp.CE.Data.DataFieldAttribute") ??
                                     ReadKeyArgument(field, "UdonSharp.CE.Persistence.PersistKeyAttribute");

                    if (!string.IsNullOrEmpty(renamed))
                        member.Key = renamed;
                }
            }

            /// <summary>
            /// The field a member spec came from. Type support collects inherited fields too, so the whole
            /// hierarchy is searched rather than just the declared type.
            /// </summary>
            private static IFieldSymbol FindSerializedField(INamedTypeSymbol declaredType, string fieldName)
            {
                for (INamedTypeSymbol current = declaredType;
                     current != null && current.SpecialType != SpecialType.System_Object;
                     current = current.BaseType)
                {
                    IFieldSymbol field = current.GetMembers(fieldName).OfType<IFieldSymbol>().FirstOrDefault();
                    if (field != null)
                        return field;
                }

                return null;
            }

            /// <summary>
            /// The key an attribute's first positional argument names, read through the semantic model where
            /// it bound and off the written literal where it did not. Returns null for anything else, so an
            /// unreadable rename keeps the field-name key rather than failing the compile.
            /// </summary>
            private static string ReadKeyArgument(IFieldSymbol field, string attributeTypeName)
            {
                AttributeData attribute = SyncOnJoinTypeSupport.FindAttribute(field, attributeTypeName);
                if (attribute == null)
                    return null;

                ImmutableArray<TypedConstant> arguments = attribute.ConstructorArguments;
                if (arguments.Length > 0 && arguments[0].Value is string bound && bound.Length > 0)
                    return bound;

                if (!(attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax syntax))
                    return null;

                SeparatedSyntaxList<AttributeArgumentSyntax>? argumentList = syntax.ArgumentList?.Arguments;
                if (argumentList == null)
                    return null;

                foreach (AttributeArgumentSyntax argument in argumentList.Value)
                {
                    // Optional/DefaultValue are named; the key is the one positional argument.
                    if (argument.NameEquals != null || argument.NameColon != null)
                        continue;

                    if (argument.Expression is LiteralExpressionSyntax literal &&
                        literal.IsKind(SyntaxKind.StringLiteralExpression))
                    {
                        return literal.Token.ValueText;
                    }

                    return null;
                }

                return null;
            }
            /// <summary>
            /// Collects the payload's fields from the whole hierarchy, base-most first, buckets them by
            /// <c>SyncOnJoinAttribute.Group</c>, and orders each bucket by <c>SyncOnJoinAttribute.Priority</c>.
            ///
            /// Group decides which synced payload a field rides in. Each group gets its own
            /// <c>[UdonSynced]</c> string, its own applied-guard, and its own serialize/apply pair, so a
            /// behaviour that mixes a large cold snapshot with a small hot field stops re-sending the cold
            /// one every time the hot one moves. Fields that name no group share the default payload.
            ///
            /// Priority controls the order fields are written into their group's payload *and* the order
            /// decoded values are committed back, so a field that depends on another can be given a higher
            /// number and is guaranteed to be written second. It orders within one group only: separate
            /// groups are separate synced variables and VRChat may deliver them in either order, so a
            /// dependency that has to hold must live in one group. Cross-behaviour ordering is not
            /// recoverable here either -- each behaviour owns its own payloads.
            ///
            /// Groups come back in first-declaration order, base-most first, so the emitted member order is
            /// stable from one compile to the next.
            /// </summary>
            private IReadOnlyList<SyncGroup> CollectSyncOnJoinGroups(INamedTypeSymbol owner)
            {
                var order = new List<string>();
                var fieldsById = new Dictionary<string, List<FieldSpec>>(StringComparer.Ordinal);
                var namesById = new Dictionary<string, string>(StringComparer.Ordinal);
                var ownersById = new Dictionary<string, INamedTypeSymbol>(StringComparer.Ordinal);
                var declaredByOwner = new HashSet<string>(StringComparer.Ordinal);
                var keys = new Dictionary<string, FieldSpec>(StringComparer.Ordinal);

                foreach (INamedTypeSymbol declaringType in GetBehaviourHierarchy(owner))
                {
                    bool isOwnType = SymbolEqualityComparer.Default.Equals(declaringType, owner);

                    foreach (IFieldSymbol field in declaringType.GetMembers().OfType<IFieldSymbol>())
                    {
                        if (field.IsImplicitlyDeclared)
                            continue;

                        AttributeData syncOnJoin = SyncOnJoinTypeSupport.FindAttribute(field, "SyncOnJoinAttribute");
                        if (syncOnJoin == null)
                            continue;

                        Location fieldLocation = GetSymbolLocation(field, owner);

                        // A static or const field holds no per-instance state to send. Every field dropped
                        // here is reported on its own as CE0032 by SyncOnJoinSupportAnalyzer, not from the
                        // emitter.
                        if (field.IsStatic || field.IsConst)
                            continue;

                        if (!isOwnType && field.DeclaredAccessibility == Accessibility.Private)
                        {
                            throw new CodeGenerationException(
                                GeneratorId,
                                fieldLocation,
                                $"'{owner.Name}' inherits the private [SyncOnJoin] field '{declaringType.Name}.{field.Name}'. " +
                                "The late-join payload for a derived behaviour is written by generated code on the derived " +
                                "type, which cannot read a private base field, so that state would silently never be sent. " +
                                "Fix: make the field protected or public, or move every [SyncOnJoin] field onto one type.");
                        }

                        FieldSpec spec = CreateFieldSpec(field, syncOnJoin, fieldLocation, out _);
                        if (spec == null)
                            continue;

                        if (keys.TryGetValue(spec.Key, out FieldSpec existing))
                        {
                            throw new CodeGenerationException(
                                GeneratorId,
                                fieldLocation,
                                $"'{owner.Name}' has two [SyncOnJoin] fields that both map to the payload key " +
                                $"'{spec.Key}' ('{existing.DeclaringTypeName}.{existing.FieldName}' and " +
                                $"'{spec.DeclaringTypeName}.{spec.FieldName}'). One would overwrite the other in the " +
                                "snapshot. Fix: rename one of the fields so the hierarchy has no duplicate name.");
                        }

                        keys.Add(spec.Key, spec);

                        string groupName = NormalizeGroupName(GetGroupName(syncOnJoin));
                        string groupId = SanitizeGroupId(groupName);

                        if (namesById.TryGetValue(groupId, out string claimedName))
                        {
                            // Two group names that differ only where the sanitiser rewrites would share one
                            // payload while reading as two -- exactly the silent bundling Group exists to
                            // remove.
                            if (!string.Equals(claimedName, groupName, StringComparison.Ordinal))
                            {
                                throw new CodeGenerationException(
                                    GeneratorId,
                                    fieldLocation,
                                    $"'{owner.Name}' has two [SyncOnJoin] groups, '{claimedName}' and '{groupName}', that " +
                                    $"both reduce to the generated identifier '{groupId}'. Generated members have to be valid " +
                                    "C# names, so every character outside a-z, A-Z, 0-9 and _ becomes an underscore -- which " +
                                    "would give these two groups one shared payload while the source reads as two. " +
                                    "Fix: rename one group so the two differ in more than punctuation.");
                            }
                        }
                        else
                        {
                            namesById.Add(groupId, groupName);
                            fieldsById.Add(groupId, new List<FieldSpec>());

                            // First declaration wins the transport. The hierarchy is walked base-most first,
                            // so this is the base-most type that carries a field of this group.
                            ownersById.Add(groupId, declaringType);
                            order.Add(groupId);
                        }

                        fieldsById[groupId].Add(spec);

                        if (isOwnType)
                            declaredByOwner.Add(groupId);
                    }
                }

                // When every field this behaviour declared was rejected, the collection keeps whatever the
                // base hierarchy contributed and drops only the unsupported ones. If that leaves nothing at
                // all, the caller emits no payload and no glue rather than a behaviour that syncs an empty
                // string.
                //
                // This is deliberately NOT a thrown CodeGenerationException. Generator failures are fatal for
                // the whole project by design, while this case is already reported per field as CE0032 by
                // SyncOnJoinSupportAnalyzer -- so it is not a silent failure, and escalating it would let one
                // behaviour using an unsupported [SyncOnJoin] type stop every other script in the project from
                // compiling. The repository's own CE0032 fixture has exactly this shape.

                var groups = new List<SyncGroup>(order.Count);

                foreach (string groupId in order)
                {
                    groups.Add(new SyncGroup(
                        namesById[groupId],
                        groupId,
                        ownersById[groupId],
                        fieldsById[groupId].OrderBy(field => field.Priority).ToList(),
                        declaredByOwner.Contains(groupId)));
                }

                return groups;
            }

            private FieldSpec CreateFieldSpec(
                IFieldSymbol field,
                AttributeData syncOnJoin,
                Location fieldLocation,
                out string reason)
            {
                reason = null;
                int priority = GetPriority(syncOnJoin);

                CustomSerializerSpec customSerializer = TryGetCustomSerializer(field, fieldLocation);
                if (customSerializer != null)
                    return new FieldSpec(field, priority, null, customSerializer);

                if (!SyncOnJoinTypeSupport.TryCreateSpec(field.Type, out SyncOnJoinTypeSpec typeSpec, out reason))
                    return null;

                return new FieldSpec(field, priority, typeSpec, null);
            }

            private static int GetPriority(AttributeData syncOnJoin)
            {
                foreach (KeyValuePair<string, TypedConstant> argument in syncOnJoin.NamedArguments)
                {
                    if (argument.Key == "Priority" && argument.Value.Value is int priority)
                        return priority;
                }

                if (AttributeResolved(syncOnJoin))
                    return DefaultPriority;

                ExpressionSyntax written = FindNamedArgumentSyntax(syncOnJoin, "Priority");

                if (written is LiteralExpressionSyntax literal && literal.Token.Value is int writtenPriority)
                    return writtenPriority;

                // A negative literal is a unary minus applied to the literal, not a literal.
                if (written is PrefixUnaryExpressionSyntax unary &&
                    unary.IsKind(SyntaxKind.UnaryMinusExpression) &&
                    unary.Operand is LiteralExpressionSyntax negated &&
                    negated.Token.Value is int negatedPriority)
                {
                    return -negatedPriority;
                }

                return DefaultPriority;
            }

            /// <summary>
            /// Reads <c>SyncOnJoinAttribute.Group</c>, the payload a field rides in. Null for the default
            /// group, which is what a field that names no group gets.
            /// </summary>
            /// <remarks>
            /// The syntax fallback is the same last resort <see cref="ReadKeyArgument"/> takes, for the same
            /// reason: UdonSharp compiles the CE runtime library as source, so in any pass whose tree set
            /// does not happen to include SyncOnJoinAttribute.cs the attribute binds to an error symbol
            /// carrying no arguments at all.
            ///
            /// Falling through to "no group" there is not harmless. Both halves of the transport are
            /// generated from this one reading, so writer and reader would still agree -- they would simply
            /// agree on putting every field back in one payload, which is the bundling Group exists to
            /// undo, with nothing anywhere to say it happened.
            /// </remarks>
            private static string GetGroupName(AttributeData syncOnJoin)
            {
                foreach (KeyValuePair<string, TypedConstant> argument in syncOnJoin.NamedArguments)
                {
                    if (argument.Key == "Group")
                        return argument.Value.Value as string;
                }

                if (AttributeResolved(syncOnJoin))
                    return null;

                return FindNamedArgumentSyntax(syncOnJoin, "Group") is LiteralExpressionSyntax literal &&
                       literal.IsKind(SyntaxKind.StringLiteralExpression)
                    ? literal.Token.ValueText
                    : null;
            }

            /// <summary>
            /// Whether the attribute type itself bound. An unresolved attribute reports no constructor or
            /// named arguments, so "not set" and "could not be read" are indistinguishable without this.
            /// </summary>
            private static bool AttributeResolved(AttributeData attribute)
            {
                return attribute.AttributeClass != null && attribute.AttributeClass.TypeKind != TypeKind.Error;
            }

            /// <summary>The expression written for a named attribute argument, or null when it is absent.</summary>
            private static ExpressionSyntax FindNamedArgumentSyntax(AttributeData attribute, string argumentName)
            {
                if (!(attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax syntax))
                    return null;

                SeparatedSyntaxList<AttributeArgumentSyntax>? arguments = syntax.ArgumentList?.Arguments;
                if (arguments == null)
                    return null;

                foreach (AttributeArgumentSyntax argument in arguments.Value)
                {
                    if (argument.NameEquals?.Name.Identifier.Text == argumentName)
                        return argument.Expression;
                }

                return null;
            }

            /// <summary>
            /// The canonical form of a written group name. Null, empty and whitespace all mean the default
            /// group, and surrounding whitespace is not a distinction worth having two payloads over.
            /// </summary>
            private static string NormalizeGroupName(string groupName)
            {
                return string.IsNullOrWhiteSpace(groupName) ? string.Empty : groupName.Trim();
            }

            /// <summary>
            /// Turns a group name into the identifier every one of that group's generated members is
            /// suffixed with. The default group's is empty, so it keeps the unsuffixed names.
            /// </summary>
            /// <remarks>
            /// Anything outside a-z, A-Z, 0-9 and _ becomes an underscore, and a leading digit gains one,
            /// because these end up in C# member names and then in Udon symbol names. Two names that
            /// collide after this are rejected where they are collected rather than quietly merged.
            /// </remarks>
            private static string SanitizeGroupId(string normalizedGroupName)
            {
                if (normalizedGroupName.Length == 0)
                    return string.Empty;

                var builder = new System.Text.StringBuilder(normalizedGroupName.Length + 1);

                foreach (char character in normalizedGroupName)
                {
                    bool isIdentifierCharacter = (character >= 'a' && character <= 'z') ||
                                                 (character >= 'A' && character <= 'Z') ||
                                                 (character >= '0' && character <= '9') ||
                                                 character == '_';

                    builder.Append(isIdentifierCharacter ? character : '_');
                }

                if (builder[0] >= '0' && builder[0] <= '9')
                    builder.Insert(0, '_');

                return builder.ToString();
            }

            /// <summary>
            /// Reads <c>[SyncSerializer]</c> off the field symbol.
            ///
            /// A malformed attribute is a hard error rather than a null return: returning null for both
            /// "no attribute" and "unusable attribute" is how a field the author explicitly wired up gets
            /// dropped from the payload without a single diagnostic.
            /// </summary>
            private static CustomSerializerSpec TryGetCustomSerializer(IFieldSymbol field, Location fieldLocation)
            {
                AttributeData attribute = SyncOnJoinTypeSupport.FindAttribute(field, "SyncSerializerAttribute");
                if (attribute == null)
                    return null;

                // When the attribute class itself did not resolve, its constructor arguments are unresolved
                // too, and "no method names" is indistinguishable from "we could not read them". Throwing
                // there would turn a reference problem into a fatal, and misleading, codegen failure --
                // so fall back to reading the names out of the syntax instead.
                bool attributeResolved = attribute.AttributeClass != null &&
                                         attribute.AttributeClass.TypeKind != TypeKind.Error;

                ImmutableArray<TypedConstant> arguments = attribute.ConstructorArguments;
                string serializeMethod = arguments.Length > 0 ? arguments[0].Value as string : null;
                string deserializeMethod = arguments.Length > 1 ? arguments[1].Value as string : null;

                if (!attributeResolved && (string.IsNullOrEmpty(serializeMethod) || string.IsNullOrEmpty(deserializeMethod)))
                {
                    ReadSyncSerializerNamesFromSyntax(attribute, ref serializeMethod, ref deserializeMethod);
                }

                if (string.IsNullOrEmpty(serializeMethod) || string.IsNullOrEmpty(deserializeMethod))
                {
                    throw new CodeGenerationException(
                        GeneratorId,
                        fieldLocation,
                        $"[SyncSerializer] on '{field.ContainingType?.Name}.{field.Name}' does not name both methods. " +
                        "CE cannot generate late-join glue for this field. " +
                        "Fix: write [SyncSerializer(nameof(SerializeMethod), nameof(DeserializeMethod))] with a " +
                        "'string SerializeMethod()' and a 'void DeserializeMethod(string data)' on this behaviour.");
                }

                return new CustomSerializerSpec(serializeMethod, deserializeMethod);
            }

            /// <summary>
            /// Last-resort read of the two <c>[SyncSerializer]</c> method names straight from the attribute
            /// syntax, used only when the attribute type failed to resolve so its constant arguments are
            /// unavailable. Handles both a string literal and <c>nameof(Method)</c>.
            /// </summary>
            private static void ReadSyncSerializerNamesFromSyntax(
                AttributeData attribute,
                ref string serializeMethod,
                ref string deserializeMethod)
            {
                if (!(attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax syntax))
                    return;

                SeparatedSyntaxList<AttributeArgumentSyntax>? argumentList = syntax.ArgumentList?.Arguments;
                if (argumentList == null)
                    return;

                var names = new List<string>();
                foreach (AttributeArgumentSyntax argument in argumentList.Value)
                {
                    if (argument.NameEquals != null)
                        continue;

                    names.Add(ExtractMethodName(argument.Expression));
                }

                if (names.Count > 0 && string.IsNullOrEmpty(serializeMethod)) serializeMethod = names[0];
                if (names.Count > 1 && string.IsNullOrEmpty(deserializeMethod)) deserializeMethod = names[1];
            }

            private static string ExtractMethodName(ExpressionSyntax expression)
            {
                if (expression is LiteralExpressionSyntax literal)
                    return literal.Token.ValueText;

                // nameof(Method) parses as an ordinary invocation.
                if (expression is InvocationExpressionSyntax invocation &&
                    invocation.Expression is IdentifierNameSyntax identifier &&
                    identifier.Identifier.Text == "nameof" &&
                    invocation.ArgumentList.Arguments.Count == 1)
                {
                    ExpressionSyntax target = invocation.ArgumentList.Arguments[0].Expression;

                    if (target is IdentifierNameSyntax name)
                        return name.Identifier.Text;

                    if (target is MemberAccessExpressionSyntax member)
                        return member.Name.Identifier.Text;
                }

                return null;
            }

            private static bool DeclaresSyncOnJoinFields(INamedTypeSymbol type)
            {
                return type.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Any(field => !field.IsImplicitlyDeclared &&
                                  SyncOnJoinTypeSupport.FindAttribute(field, "SyncOnJoinAttribute") != null);
            }

            /// <summary>Base-most user behaviour first, so base fields keep a stable position in the payload.</summary>
            private static IEnumerable<INamedTypeSymbol> GetBehaviourHierarchy(INamedTypeSymbol type)
            {
                var stack = new Stack<INamedTypeSymbol>();

                for (INamedTypeSymbol current = type;
                     current != null && current.Name != "UdonSharpBehaviour";
                     current = current.BaseType)
                {
                    stack.Push(current);
                }

                while (stack.Count > 0)
                    yield return stack.Pop();
            }

            /// <summary>
            /// One group's late-join transport.
            ///
            /// This is a real <c>[UdonSynced]</c> variable on the behaviour itself, so VRChat delivers it
            /// to late joiners automatically and authority follows normal object ownership. The previous
            /// design used an unsynced public string moved by a hidden master-owned coordinator, which
            /// meant the snapshot only ever captured the *master's* local view -- state authored by any
            /// other client never reached late joiners at all.
            ///
            /// One per group rather than one per behaviour is the entire point of
            /// <c>SyncOnJoinAttribute.Group</c>: a Manual payload may be
            /// <c>NetworkLimits.ManualSyncMaxBytes</c> while the whole instance shares
            /// <c>NetworkLimits.NetworkBudgetBytesPerSecond</c> per second, and VRChat's rate
            /// limiting is size-proportional -- so a behaviour that bundles a large cold snapshot with a
            /// small hot field starves the instance AND throttles the hot field, precisely because the two
            /// travel together.
            ///
            /// It is protected rather than private so a derived behaviour that adds fields to this group
            /// writes the same single synced variable instead of shadowing it.
            /// </summary>
            private static string BuildPayloadField(SyncGroup group)
            {
                return $@"
[global::UdonSharp.CE.Net.SyncOnJoinGenerated]
[global::UnityEngine.HideInInspector]
[global::UdonSharp.UdonSynced]
protected string {group.PayloadField} = """";
";
            }

            /// <summary>
            /// Guards against re-applying an unchanged payload. OnDeserialization fires for any synced
            /// variable change on the behaviour, not just this one -- with groups that now includes every
            /// OTHER group's payload -- and the same snapshot is delivered again whenever the owner
            /// re-serializes for an unrelated reason.
            /// </summary>
            private static string BuildAppliedField(SyncGroup group)
            {
                return $@"
[global::UdonSharp.CE.Net.SyncOnJoinGenerated]
[global::UnityEngine.HideInInspector]
protected string {group.AppliedField} = null;
";
            }

            /// <summary>
            /// The receive path for one group.
            ///
            /// Safe to run before Start: it reads nothing that Start initialises, and the initial late-join
            /// delivery routinely arrives before the behaviour's own Start. Re-delivering the same payload is
            /// a no-op because the guard is compared before decoding, and decoding twice is idempotent anyway.
            ///
            /// Each group carries its own guard, so a delivery that changed only group A does not re-decode
            /// group B.
            /// </summary>
            private static string BuildApplyMethod(SyncGroup group)
            {
                return $@"
[global::UdonSharp.CE.Net.SyncOnJoinGenerated]
public void {group.ApplyMethod}()
{{
    if ({group.PayloadField} == null || {group.PayloadField}.Length == 0)
        return;

    if ({group.PayloadField} == {group.AppliedField})
        return;

    {group.DeserializeMethod}();
}}
";
            }

            /// <summary>
            /// The send path, and the reason any of this reaches the network at all.
            ///
            /// [SyncOnJoin] forces Manual sync (see BehaviourSyncModeEmitter), and Manual sync only transmits
            /// when something calls RequestSerialization(). Writing the payload in OnPreSerialization alone
            /// means a behaviour whose only synced state is [SyncOnJoin] serializes NEVER and every late joiner
            /// gets the empty initial string.
            ///
            /// So the capture is driven from OnPlayerJoined, which fires on the owner for the local player at
            /// world load (the initial snapshot) and again for every subsequent joiner -- exactly the moments a
            /// late-join snapshot has to be current, and no cost whatsoever in between. There is no Update and
            /// no polling loop. RequestSerialization is only called when SOME group's freshly serialized JSON
            /// differs from what is already in that group's synced variable, so an unchanged world never spends
            /// bandwidth, and a world where only one group moved re-sends only that group's string.
            ///
            /// Authors who mutate [SyncOnJoin] state and want it published immediately can drive the same path
            /// with <c>SendCustomEvent("_CE_SyncOnJoin_RequestCapture")</c> -- generated members are invisible
            /// to Unity-compiled user C#, so string dispatch is the only callable surface they have. That is
            /// why there is one capture method covering every group rather than one per group: the name an
            /// author has to type stays the same however their state is bucketed.
            /// </summary>
            private static string BuildRequestCaptureMethod(
                IReadOnlyList<SyncGroup> groups,
                bool isOverride,
                Location location)
            {
                var code = new CodeWriter(location);
                code.Line("[global::UdonSharp.CE.Net.SyncOnJoinGenerated]");
                code.Line($"public {(isOverride ? "override" : "virtual")} void {RequestCaptureMethodName}()");
                code.OpenBlock();
                code.Line("if (!global::VRC.SDKBase.Networking.IsOwner(global::VRC.SDKBase.Networking.LocalPlayer, gameObject))");
                code.OpenBlock();
                code.Line("return;");
                code.CloseBlock();
                code.Line();
                code.Line("bool __ce_changed = false;");

                // A derived behaviour that introduces a group overrides this instead of chaining to base:
                // the override lists every group in the hierarchy, so a base call would only re-serialize
                // the base's groups a second time.
                foreach (SyncGroup group in groups)
                {
                    string previous = code.Temp("__ce_previousPayload");
                    code.Line();
                    code.Line($"string {previous} = {group.PayloadField};");
                    code.Line($"{group.SerializeMethod}();");
                    code.Line($"if ({group.PayloadField} != {previous})");
                    code.OpenBlock();
                    code.Line("__ce_changed = true;");
                    code.CloseBlock();
                }

                code.Line();
                code.Line("if (__ce_changed)");
                code.OpenBlock();
                code.Line("RequestSerialization();");
                code.CloseBlock();
                code.CloseBlock();
                return code.ToString();
            }

            /// <summary>
            /// Wires the generated glue into Udon's events, preserving any handler the author already wrote.
            ///
            /// Capture runs LAST in OnPreSerialization so a user who mutates [SyncOnJoin] fields in their own
            /// handler still gets captured. Apply runs FIRST in OnDeserialization so user code observes
            /// already-restored state. Capture also runs in OnPlayerJoined, which is what actually makes the
            /// owner call RequestSerialization.
            ///
            /// Note both OnDeserialization overloads map to the same Udon event, so declaring both would
            /// collide -- only ever inject into whichever exists, and synthesize just one when neither does.
            /// A handler declared in another partial part, or inherited from a base behaviour that already
            /// carries the glue, must not be duplicated here either: that is CS0111 for partials and a lost
            /// base handler for inheritance.
            ///
            /// Which groups get woven in depends on how the handler is reached. A handler the author wrote
            /// gets EVERY group in the hierarchy, because that handler may never call base and would
            /// otherwise strand the base's groups. A synthesized handler always opens with the base call, so
            /// it only needs the groups this type owns -- listing the rest would serialize them twice per
            /// event for the whole depth of the hierarchy.
            /// </summary>
            private static ClassDeclarationSyntax WeaveSerializationEvents(
                ClassDeclarationSyntax node,
                INamedTypeSymbol type,
                IReadOnlyList<SyncGroup> groups,
                bool synthesizeMissingHandlers)
            {
                IReadOnlyList<SyncGroup> owned = groups.Where(group => group.IsOwnedBy(type)).ToList();

                node = WeaveEvent(
                    node,
                    type,
                    "OnPreSerialization",
                    CallsTo(groups, group => group.SerializeMethod),
                    CallsTo(owned, group => group.SerializeMethod),
                    prepend: false,
                    synthesizeMissingHandlers: synthesizeMissingHandlers,
                    synthesizedSignature: "public override void OnPreSerialization()",
                    baseCall: "base.OnPreSerialization();");

                node = WeaveEvent(
                    node,
                    type,
                    "OnDeserialization",
                    CallsTo(groups, group => group.ApplyMethod),
                    CallsTo(owned, group => group.ApplyMethod),
                    prepend: true,
                    synthesizeMissingHandlers: synthesizeMissingHandlers,
                    synthesizedSignature: "public override void OnDeserialization()",
                    baseCall: "base.OnDeserialization();");

                string[] capture = { $"{RequestCaptureMethodName}();" };

                // Capture is one virtual method covering every group, so a behaviour whose base already
                // carries the glue inherits a handler that dispatches to this type's override. Synthesizing
                // a second one would re-serialize every group a second time on every join.
                bool baseCarriesCapture = groups.Count != owned.Count;

                node = WeaveEvent(
                    node,
                    type,
                    "OnPlayerJoined",
                    capture,
                    baseCarriesCapture ? EmptyStatements : capture,
                    prepend: false,
                    synthesizeMissingHandlers: synthesizeMissingHandlers,
                    synthesizedSignature: "public override void OnPlayerJoined(global::VRC.SDKBase.VRCPlayerApi player)",
                    baseCall: "base.OnPlayerJoined(player);");

                return node;
            }

            private static IReadOnlyList<string> CallsTo(IReadOnlyList<SyncGroup> groups, Func<SyncGroup, string> methodName)
            {
                return groups.Select(group => methodName(group) + "();").ToList();
            }

            private static ClassDeclarationSyntax WeaveEvent(
                ClassDeclarationSyntax node,
                INamedTypeSymbol type,
                string eventName,
                IReadOnlyList<string> injectedStatements,
                IReadOnlyList<string> synthesizedStatements,
                bool prepend,
                bool synthesizeMissingHandlers,
                string synthesizedSignature,
                string baseCall)
            {
                MethodDeclarationSyntax existing = node.Members
                    .OfType<MethodDeclarationSyntax>()
                    .FirstOrDefault(method => method.Identifier.Text == eventName);

                if (existing != null)
                    return InjectIntoHandler(node, existing, injectedStatements, prepend);

                // Another partial part of this same type owns the handler and will get the calls injected when
                // that part is visited. Adding a second declaration here is CS0111.
                if (!synthesizeMissingHandlers || synthesizedStatements.Count == 0 || DeclaresMethod(type, eventName))
                    return node;

                // The base call runs first so a handler the author wrote on a base behaviour is not silently
                // replaced by the synthesized override, and so a base group's glue still runs when this type
                // only introduced a group of its own. Base calls into UdonSharpBehaviour's own events compile
                // to a no-op, so this is safe even when nothing overrides it.
                var body = new System.Text.StringBuilder();
                body.Append("    ").AppendLine(baseCall);

                foreach (string statement in synthesizedStatements)
                    body.Append("    ").AppendLine(statement);

                return node.AddMembers(ParseMember($@"
[global::UdonSharp.CE.Net.SyncOnJoinGenerated]
{synthesizedSignature}
{{
{body}}}
"));
            }

            /// <summary>
            /// Adds the glue calls to a handler the author already wrote.
            /// </summary>
            /// <remarks>
            /// Everything is staged against the ORIGINAL <paramref name="existing"/> node and committed with
            /// a single ReplaceNode -- one commit for every group's call, not one per group. Converting the
            /// expression body in its own ReplaceNode first, and then replacing the converted node, could
            /// never work: ReplaceNode returns a new tree in which the replacement is a different instance,
            /// so the second call found nothing to replace and returned the class unchanged -- silently,
            /// because ReplaceNode does not report a node it did not match.
            ///
            /// The result was the worst outcome this weaving has: an expression-bodied
            /// '[SyncOnJoin] ... public override void OnDeserialization() =&gt; Refresh();' was DETECTED as
            /// the handler, so nothing was synthesized and nothing failed to compile, and the author's body
            /// still ran -- while the late-join payload was never applied on any client. The whole feature
            /// was inert on that behaviour with no diagnostic anywhere.
            /// </remarks>
            private static ClassDeclarationSyntax InjectIntoHandler(
                ClassDeclarationSyntax node,
                MethodDeclarationSyntax existing,
                IReadOnlyList<string> callStatements,
                bool prepend)
            {
                MethodDeclarationSyntax rewritten = existing;
                BlockSyntax body = existing.Body;

                if (body == null)
                {
                    // Nothing to weave into: a partial or extern declaration with no implementation here.
                    if (existing.ExpressionBody == null)
                        return node;

                    // Expression-bodied handler: convert to a block so the glue call can be added. The line
                    // break after the method lives on the semicolon this shape drops, so it moves to the
                    // closing brace -- otherwise every later diagnostic in the file points one line too high.
                    body = SyntaxFactory.Block(
                        SyntaxFactory.ExpressionStatement(existing.ExpressionBody.Expression));

                    body = body.WithCloseBraceToken(
                        body.CloseBraceToken.WithTrailingTrivia(existing.SemicolonToken.TrailingTrivia));

                    rewritten = existing
                        .WithExpressionBody(null)
                        .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.None));
                }

                var calls = new List<StatementSyntax>();

                foreach (string callStatement in callStatements)
                {
                    if (body.Statements.Any(statement => statement.ToString().Contains(callStatement)))
                        continue;

                    calls.Add(LayOutLikeExistingStatements(body, SyntaxFactory.ParseStatement(callStatement)));
                }

                if (calls.Count == 0)
                    return node;

                BlockSyntax newBody = body.WithStatements(
                    prepend ? body.Statements.InsertRange(0, calls) : body.Statements.AddRange(calls));

                return node.ReplaceNode(existing, rewritten.WithBody(newBody));
            }

            /// <summary>
            /// Gives the injected call the indentation and line break the body's own statements use.
            /// </summary>
            /// <remarks>
            /// A parsed statement carries no trivia, so the call was emitted hard against the author's
            /// first statement -- '_CE_SyncOnJoin_Apply();            ApplySeed(_worldSeed);' on one line.
            /// It compiles and runs, but a reader looking at the generated source cannot see that the glue
            /// is there at all, which is exactly the question the dump is opened to answer.
            /// </remarks>
            private static StatementSyntax LayOutLikeExistingStatements(BlockSyntax body, StatementSyntax call)
            {
                SyntaxTriviaList neighbour = body.Statements.Count > 0
                    ? body.Statements[0].GetLeadingTrivia()
                    : body.CloseBraceToken.LeadingTrivia;

                return call
                    .WithLeadingTrivia(IndentationOf(neighbour))
                    .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);
            }

            /// <summary>
            /// Just the indentation at the end of a leading-trivia list.
            /// </summary>
            /// <remarks>
            /// Copying the whole list would copy the doc comment or '#region' that belongs to the member it
            /// precedes, so the author's comment would appear twice with the generated line wedged between.
            /// </remarks>
            private static SyntaxTriviaList IndentationOf(SyntaxTriviaList leadingTrivia)
            {
                if (leadingTrivia.Count == 0)
                    return SyntaxFactory.TriviaList();

                SyntaxTrivia last = leadingTrivia[leadingTrivia.Count - 1];

                return last.IsKind(SyntaxKind.WhitespaceTrivia)
                    ? SyntaxFactory.TriviaList(last)
                    : SyntaxFactory.TriviaList();
            }

            private static string BuildSerializeMethod(
                string className,
                SyncGroup group,
                bool isOverride,
                Location location)
            {
                IReadOnlyList<FieldSpec> fields = group.Fields;
                string subject = DescribeSubject(className, group);
                var code = new CodeWriter(location);
                code.Line("[global::UdonSharp.CE.Net.SyncOnJoinGenerated]");
                code.Line($"public {(isOverride ? "override" : "virtual")} void {group.SerializeMethod}()");
                code.OpenBlock();
                code.Line("global::VRC.SDK3.Data.DataDictionary __ce_dict = new global::VRC.SDK3.Data.DataDictionary();");

                foreach (FieldSpec field in fields)
                {
                    string tokenName = code.Temp("__ce_token");
                    code.Line($"global::VRC.SDK3.Data.DataToken {tokenName};");
                    if (field.CustomSerializer != null)
                    {
                        code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({field.CustomSerializer.SerializeMethod}());");
                    }
                    else
                    {
                        EmitSerializeToExistingToken(code, field.Type, field.FieldName, tokenName);
                    }

                    code.Line($"__ce_dict.Add(new global::VRC.SDK3.Data.DataToken(\"{EscapeString(field.Key)}\"), {tokenName});");
                }

                code.Line("global::VRC.SDK3.Data.DataToken __ce_jsonToken;");
                code.Line("if (!global::VRC.SDK3.Data.VRCJson.TrySerializeToJson(new global::VRC.SDK3.Data.DataToken(__ce_dict), global::VRC.SDK3.Data.JsonExportType.Minify, out __ce_jsonToken))");
                code.OpenBlock();
                code.Line($"global::UnityEngine.Debug.LogError(\"[SyncOnJoin] Failed to serialize {subject}. Keeping the last good payload.\");");
                // Deliberately does not blank the payload: publishing an empty snapshot would wipe every
                // late joiner's state because of one transient serialization failure.
                code.Line("return;");
                code.CloseBlock();
                code.Line();
                code.Line($"{group.PayloadField} = __ce_jsonToken.String;");
                code.CloseBlock();
                return code.ToString();
            }

            /// <summary>
            /// Decodes one group's payload into locals and only writes the behaviour's fields once every
            /// field in that group has decoded, so a truncated, stale, or hand-edited payload leaves the
            /// behaviour on its previous state instead of half-applying the snapshot. The group's
            /// applied-guard is set last for the same reason: a failed decode must not mark the payload as
            /// consumed. A group that fails to decode leaves every other group untouched, since each has
            /// its own payload, its own decode, and its own guard.
            /// </summary>
            private static string BuildDeserializeMethod(
                string className,
                SyncGroup group,
                bool isOverride,
                Location location)
            {
                IReadOnlyList<FieldSpec> fields = group.Fields;
                string subject = DescribeSubject(className, group);
                var code = new CodeWriter(location);
                code.Line("[global::UdonSharp.CE.Net.SyncOnJoinGenerated]");
                code.Line($"public {(isOverride ? "override" : "virtual")} void {group.DeserializeMethod}()");
                code.OpenBlock();
                code.Line("global::VRC.SDK3.Data.DataToken __ce_root;");
                code.Line($"if (!global::VRC.SDK3.Data.VRCJson.TryDeserializeFromJson({group.PayloadField}, out __ce_root))");
                code.OpenBlock();
                code.Line($"global::UnityEngine.Debug.LogError(\"[SyncOnJoin] {subject} received a payload that is not valid JSON. Nothing was applied.\");");
                code.Line("return;");
                code.CloseBlock();
                code.Line();
                code.Line("if (__ce_root.TokenType != global::VRC.SDK3.Data.TokenType.DataDictionary)");
                code.OpenBlock();
                code.Line($"global::UnityEngine.Debug.LogError(\"[SyncOnJoin] {subject} received a payload whose root is not a JSON object. Nothing was applied.\");");
                code.Line("return;");
                code.CloseBlock();
                code.Line();
                code.Line("global::VRC.SDK3.Data.DataDictionary __ce_dict = __ce_root.DataDictionary;");
                code.Line("global::VRC.SDK3.Data.DataToken __ce_value;");
                code.Line("bool __ce_ok = true;");

                var locals = new string[fields.Count];

                for (int i = 0; i < fields.Count; i++)
                {
                    FieldSpec field = fields[i];
                    locals[i] = code.Temp("__ce_decoded");
                    string localTypeName = field.CustomSerializer != null ? "string" : field.Type.TypeName;

                    code.Line();
                    code.Line($"{localTypeName} {locals[i]} = default({localTypeName});");
                    code.Line($"if (!__ce_dict.TryGetValue(new global::VRC.SDK3.Data.DataToken(\"{EscapeString(field.Key)}\"), out __ce_value))");
                    code.OpenBlock();
                    code.Line($"global::UnityEngine.Debug.LogError(\"[SyncOnJoin] {subject} payload has no entry for field '{EscapeString(field.FieldName)}'. Nothing was applied.\");");
                    code.Line("return;");
                    code.CloseBlock();

                    if (field.CustomSerializer != null)
                        EmitDeserializeStringToken(code, locals[i], "__ce_value", "__ce_ok");
                    else
                        EmitDeserializeFromToken(code, field.Type, locals[i], "__ce_value", "__ce_ok");

                    code.Line("if (!__ce_ok)");
                    code.OpenBlock();
                    code.Line($"global::UnityEngine.Debug.LogError(\"[SyncOnJoin] {subject} could not decode field '{EscapeString(field.FieldName)}' from its payload. Nothing was applied.\");");
                    code.Line("return;");
                    code.CloseBlock();
                }

                code.Line();

                for (int i = 0; i < fields.Count; i++)
                {
                    FieldSpec field = fields[i];
                    if (field.CustomSerializer != null)
                        code.Line($"{field.CustomSerializer.DeserializeMethod}({locals[i]});");
                    else
                        code.Line($"{field.FieldName} = {locals[i]};");
                }

                code.Line($"{group.AppliedField} = {group.PayloadField};");
                code.CloseBlock();
                return code.ToString();
            }

            /// <summary>
            /// What a generated log line calls the thing that failed. A behaviour with groups has several
            /// payloads, so naming only the behaviour would leave the reader guessing which one.
            /// </summary>
            private static string DescribeSubject(string className, SyncGroup group)
            {
                return group.Id.Length == 0
                    ? $"'{EscapeString(className)}'"
                    : $"'{EscapeString(className)}' group '{EscapeString(group.Name)}'";
            }

            private static string BuildToDataMethod(SyncOnJoinTypeSpec spec, Location location)
            {
                var code = new CodeWriter(location);
                code.Line("[global::UdonSharp.CE.Net.SyncOnJoinGenerated]");
                code.Line($"public global::VRC.SDK3.Data.DataDictionary {ToDataMethodName}()");
                code.OpenBlock();
                code.Line("global::VRC.SDK3.Data.DataDictionary __ce_dict = new global::VRC.SDK3.Data.DataDictionary();");

                foreach (SyncOnJoinMemberSpec member in spec.Members)
                {
                    string tokenName = code.Temp("__ce_token");
                    code.Line($"global::VRC.SDK3.Data.DataToken {tokenName};");
                    EmitSerializeToExistingToken(code, member.Type, member.FieldName, tokenName);
                    code.Line($"__ce_dict.Add(new global::VRC.SDK3.Data.DataToken(\"{EscapeString(member.Key)}\"), {tokenName});");
                }

                code.Line("return __ce_dict;");
                code.CloseBlock();
                return code.ToString();
            }

            /// <summary>
            /// Returns false when any member is missing or has the wrong shape, so the behaviour-level decode
            /// can abort before it commits anything. A nested model that silently keeps half of an old
            /// snapshot is the same corruption the root guards against.
            /// </summary>
            private static string BuildFromDataMethod(SyncOnJoinTypeSpec spec, string typeName, Location location)
            {
                var code = new CodeWriter(location);
                code.Line("[global::UdonSharp.CE.Net.SyncOnJoinGenerated]");
                code.Line($"public bool {FromDataMethodName}(global::VRC.SDK3.Data.DataDictionary __ce_dict)");
                code.OpenBlock();
                code.Line("if (__ce_dict == null)");
                code.OpenBlock();
                code.Line("return false;");
                code.CloseBlock();
                code.Line("global::VRC.SDK3.Data.DataToken __ce_value;");
                code.Line("bool __ce_ok = true;");

                foreach (SyncOnJoinMemberSpec member in spec.Members)
                {
                    code.Line($"if (__ce_dict.TryGetValue(new global::VRC.SDK3.Data.DataToken(\"{EscapeString(member.Key)}\"), out __ce_value))");
                    code.OpenBlock();
                    EmitDeserializeFromToken(code, member.Type, member.FieldName, "__ce_value", "__ce_ok");
                    code.CloseBlock();
                    code.Line("else");
                    code.OpenBlock();
                    code.Line($"global::UnityEngine.Debug.LogError(\"[SyncOnJoin] '{EscapeString(typeName)}' payload has no entry for '{EscapeString(member.Key)}'.\");");
                    code.Line("__ce_ok = false;");
                    code.CloseBlock();
                }

                code.Line("return __ce_ok;");
                code.CloseBlock();
                return code.ToString();
            }

            private static void EmitSerializeToExistingToken(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string valueExpression,
                string tokenName)
            {
                switch (spec.Kind)
                {
                    case SyncOnJoinValueKind.Primitive:
                        EmitSerializePrimitive(code, spec, valueExpression, tokenName);
                        return;
                    case SyncOnJoinValueKind.Enum:
                        code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken((long){valueExpression});");
                        return;
                    case SyncOnJoinValueKind.String:
                        code.Line($"{tokenName} = {valueExpression} != null ? new global::VRC.SDK3.Data.DataToken({valueExpression}) : new global::VRC.SDK3.Data.DataToken();");
                        return;
                    case SyncOnJoinValueKind.DataDictionary:
                    case SyncOnJoinValueKind.DataList:
                        code.Line($"{tokenName} = {valueExpression} != null ? new global::VRC.SDK3.Data.DataToken({valueExpression}) : new global::VRC.SDK3.Data.DataToken();");
                        return;
                    case SyncOnJoinValueKind.UnityStruct:
                        EmitSerializeUnityStruct(code, spec, valueExpression, tokenName);
                        return;
                    case SyncOnJoinValueKind.Array:
                        EmitSerializeArray(code, spec, valueExpression, tokenName);
                        return;
                    case SyncOnJoinValueKind.List:
                        EmitSerializeList(code, spec, valueExpression, tokenName);
                        return;
                    case SyncOnJoinValueKind.CustomObject:
                        EmitSerializeCustomObject(code, spec, valueExpression, tokenName);
                        return;
                    default:
                        throw new CodeGenerationException(
                            GeneratorId,
                            code.Location,
                            $"CE cannot serialize the SyncOnJoin value kind '{spec.Kind}' for '{spec.TypeName}'. " +
                            "This is a CE bug: report the field type. " +
                            "Workaround: add [SyncSerializer(nameof(Serialize), nameof(Deserialize))] to the field.");
                }
            }

            /// <summary>
            /// long/ulong ride as strings and non-finite floats ride as sentinels because of what JSON can
            /// carry: a JSON number is an IEEE double, so anything past 2^53 comes back silently rounded, and
            /// <c>TrySerializeToJson</c> refuses NaN/Infinity outright -- which would blank the ENTIRE payload
            /// over one bad component rather than just that field.
            /// </summary>
            private static void EmitSerializePrimitive(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string valueExpression,
                string tokenName)
            {
                switch (spec.PrimitiveEncoding)
                {
                    case SyncOnJoinPrimitiveEncoding.BigInteger:
                        code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({valueExpression}.ToString());");
                        return;
                    case SyncOnJoinPrimitiveEncoding.Floating:
                        code.Line($"if ({spec.TypeName}.IsNaN({valueExpression}))");
                        code.OpenBlock();
                        code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken(\"{NaNSentinel}\");");
                        code.CloseBlock();
                        code.Line($"else if ({spec.TypeName}.IsPositiveInfinity({valueExpression}))");
                        code.OpenBlock();
                        code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken(\"{PositiveInfinitySentinel}\");");
                        code.CloseBlock();
                        code.Line($"else if ({spec.TypeName}.IsNegativeInfinity({valueExpression}))");
                        code.OpenBlock();
                        code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken(\"{NegativeInfinitySentinel}\");");
                        code.CloseBlock();
                        code.Line("else");
                        code.OpenBlock();
                        code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({valueExpression});");
                        code.CloseBlock();
                        return;
                    default:
                        code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({valueExpression});");
                        return;
                }
            }

            private static void EmitSerializeUnityStruct(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string valueExpression,
                string tokenName)
            {
                string listName = code.Temp("__ce_list");
                code.Line($"global::VRC.SDK3.Data.DataList {listName} = new global::VRC.SDK3.Data.DataList();");
                for (int i = 0; i < spec.ComponentNames.Length; i++)
                {
                    string itemToken = code.Temp("__ce_component");
                    code.Line($"global::VRC.SDK3.Data.DataToken {itemToken};");
                    EmitSerializeToExistingToken(code, spec.ComponentTypes[i], $"{valueExpression}.{spec.ComponentNames[i]}", itemToken);
                    code.Line($"{listName}.Add({itemToken});");
                }
                code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({listName});");
            }

            private static void EmitSerializeArray(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string valueExpression,
                string tokenName)
            {
                code.Line($"if ({valueExpression} == null)");
                code.OpenBlock();
                code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken();");
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                string listName = code.Temp("__ce_list");
                string indexName = code.Temp("__ce_i");
                code.Line($"global::VRC.SDK3.Data.DataList {listName} = new global::VRC.SDK3.Data.DataList();");
                code.Line($"for (int {indexName} = 0; {indexName} < {valueExpression}.Length; {indexName}++)");
                code.OpenBlock();
                string itemToken = code.Temp("__ce_item");
                code.Line($"global::VRC.SDK3.Data.DataToken {itemToken};");
                EmitSerializeToExistingToken(code, spec.ElementType, $"{valueExpression}[{indexName}]", itemToken);
                code.Line($"{listName}.Add({itemToken});");
                code.CloseBlock();
                code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({listName});");
                code.CloseBlock();
            }

            private static void EmitSerializeList(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string valueExpression,
                string tokenName)
            {
                code.Line($"if ({valueExpression} == null)");
                code.OpenBlock();
                code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken();");
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                string listName = code.Temp("__ce_list");
                string indexName = code.Temp("__ce_i");
                code.Line($"global::VRC.SDK3.Data.DataList {listName} = new global::VRC.SDK3.Data.DataList();");
                code.Line($"for (int {indexName} = 0; {indexName} < {valueExpression}.Count; {indexName}++)");
                code.OpenBlock();
                string itemToken = code.Temp("__ce_item");
                code.Line($"global::VRC.SDK3.Data.DataToken {itemToken};");
                EmitSerializeToExistingToken(code, spec.ElementType, $"{valueExpression}[{indexName}]", itemToken);
                code.Line($"{listName}.Add({itemToken});");
                code.CloseBlock();
                code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({listName});");
                code.CloseBlock();
            }

            private static void EmitSerializeCustomObject(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string valueExpression,
                string tokenName)
            {
                if (spec.IsReferenceType)
                {
                    code.Line($"if ({valueExpression} == null)");
                    code.OpenBlock();
                    code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken();");
                    code.CloseBlock();
                    code.Line("else");
                    code.OpenBlock();
                    code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({valueExpression}.{ToDataMethodName}());");
                    code.CloseBlock();
                    return;
                }

                code.Line($"{tokenName} = new global::VRC.SDK3.Data.DataToken({valueExpression}.{ToDataMethodName}());");
            }

            /// <summary>
            /// Every read is guarded on the token's TokenType before it is dereferenced. An unguarded read of
            /// the wrong token type throws inside OnDeserialization, which in Udon stops the behaviour dead
            /// with whatever it had already written still applied.
            /// </summary>
            private static void EmitDeserializeFromToken(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string targetExpression,
                string tokenName,
                string okFlag)
            {
                switch (spec.Kind)
                {
                    case SyncOnJoinValueKind.Primitive:
                        EmitDeserializePrimitive(code, spec, targetExpression, tokenName, okFlag);
                        return;
                    case SyncOnJoinValueKind.Enum:
                        code.Line($"if ({tokenName}.IsNumber)");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = ({spec.TypeName})(long){tokenName}.Number;");
                        code.CloseBlock();
                        code.Line("else");
                        code.OpenBlock();
                        code.Line($"{okFlag} = false;");
                        code.CloseBlock();
                        return;
                    case SyncOnJoinValueKind.String:
                        EmitDeserializeStringToken(code, targetExpression, tokenName, okFlag);
                        return;
                    case SyncOnJoinValueKind.DataDictionary:
                        EmitDeserializeNullableToken(code, targetExpression, tokenName, okFlag, "DataDictionary", "DataDictionary");
                        return;
                    case SyncOnJoinValueKind.DataList:
                        EmitDeserializeNullableToken(code, targetExpression, tokenName, okFlag, "DataList", "DataList");
                        return;
                    case SyncOnJoinValueKind.UnityStruct:
                        EmitDeserializeUnityStruct(code, spec, targetExpression, tokenName, okFlag);
                        return;
                    case SyncOnJoinValueKind.Array:
                        EmitDeserializeArray(code, spec, targetExpression, tokenName, okFlag);
                        return;
                    case SyncOnJoinValueKind.List:
                        EmitDeserializeList(code, spec, targetExpression, tokenName, okFlag);
                        return;
                    case SyncOnJoinValueKind.CustomObject:
                        EmitDeserializeCustomObject(code, spec, targetExpression, tokenName, okFlag);
                        return;
                    default:
                        throw new CodeGenerationException(
                            GeneratorId,
                            code.Location,
                            $"CE cannot deserialize the SyncOnJoin value kind '{spec.Kind}' for '{spec.TypeName}'. " +
                            "This is a CE bug: report the field type. " +
                            "Workaround: add [SyncSerializer(nameof(Serialize), nameof(Deserialize))] to the field.");
                }
            }

            private static void EmitDeserializePrimitive(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string targetExpression,
                string tokenName,
                string okFlag)
            {
                switch (spec.PrimitiveEncoding)
                {
                    case SyncOnJoinPrimitiveEncoding.Boolean:
                        code.Line($"if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.Boolean)");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = {tokenName}.Boolean;");
                        code.CloseBlock();
                        code.Line("else");
                        code.OpenBlock();
                        code.Line($"{okFlag} = false;");
                        code.CloseBlock();
                        return;

                    case SyncOnJoinPrimitiveEncoding.BigInteger:
                        string parsed = code.Temp("__ce_parsed");
                        code.Line($"{spec.TypeName} {parsed} = default({spec.TypeName});");
                        code.Line($"if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.String && {spec.TypeName}.TryParse({tokenName}.String, out {parsed}))");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = {parsed};");
                        code.CloseBlock();
                        // Numbers are still accepted so a payload written before the string encoding, or by
                        // hand, still loads for values inside the double-exact range.
                        code.Line($"else if ({tokenName}.IsNumber)");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = ({spec.TypeName}){tokenName}.Number;");
                        code.CloseBlock();
                        code.Line("else");
                        code.OpenBlock();
                        code.Line($"{okFlag} = false;");
                        code.CloseBlock();
                        return;

                    case SyncOnJoinPrimitiveEncoding.Floating:
                        code.Line($"if ({tokenName}.IsNumber)");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = {string.Format(spec.PrimitiveReadExpression, tokenName)};");
                        code.CloseBlock();
                        code.Line($"else if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.String && {tokenName}.String == \"{NaNSentinel}\")");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = {spec.TypeName}.NaN;");
                        code.CloseBlock();
                        code.Line($"else if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.String && {tokenName}.String == \"{PositiveInfinitySentinel}\")");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = {spec.TypeName}.PositiveInfinity;");
                        code.CloseBlock();
                        code.Line($"else if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.String && {tokenName}.String == \"{NegativeInfinitySentinel}\")");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = {spec.TypeName}.NegativeInfinity;");
                        code.CloseBlock();
                        code.Line("else");
                        code.OpenBlock();
                        code.Line($"{okFlag} = false;");
                        code.CloseBlock();
                        return;

                    default:
                        code.Line($"if ({tokenName}.IsNumber)");
                        code.OpenBlock();
                        code.Line($"{targetExpression} = {string.Format(spec.PrimitiveReadExpression, tokenName)};");
                        code.CloseBlock();
                        code.Line("else");
                        code.OpenBlock();
                        code.Line($"{okFlag} = false;");
                        code.CloseBlock();
                        return;
                }
            }

            private static void EmitDeserializeStringToken(
                CodeWriter code,
                string targetExpression,
                string tokenName,
                string okFlag)
            {
                EmitDeserializeNullableToken(code, targetExpression, tokenName, okFlag, "String", "String");
            }

            private static void EmitDeserializeNullableToken(
                CodeWriter code,
                string targetExpression,
                string tokenName,
                string okFlag,
                string tokenTypeName,
                string accessorName)
            {
                code.Line($"if ({tokenName}.IsNull)");
                code.OpenBlock();
                code.Line($"{targetExpression} = null;");
                code.CloseBlock();
                code.Line($"else if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.{tokenTypeName})");
                code.OpenBlock();
                code.Line($"{targetExpression} = {tokenName}.{accessorName};");
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                code.Line($"{okFlag} = false;");
                code.CloseBlock();
            }

            private static void EmitDeserializeUnityStruct(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string targetExpression,
                string tokenName,
                string okFlag)
            {
                code.Line($"if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.DataList)");
                code.OpenBlock();
                string listName = code.Temp("__ce_list");
                string tempName = code.Temp("__ce_tmp");
                string componentToken = code.Temp("__ce_component");
                code.Line($"global::VRC.SDK3.Data.DataList {listName} = {tokenName}.DataList;");
                code.Line($"{spec.TypeName} {tempName} = new {spec.TypeName}();");
                code.Line($"global::VRC.SDK3.Data.DataToken {componentToken};");
                for (int i = 0; i < spec.ComponentNames.Length; i++)
                {
                    code.Line($"if ({listName}.TryGetValue({i}, out {componentToken}))");
                    code.OpenBlock();
                    EmitDeserializeFromToken(code, spec.ComponentTypes[i], $"{tempName}.{spec.ComponentNames[i]}", componentToken, okFlag);
                    code.CloseBlock();
                    code.Line("else");
                    code.OpenBlock();
                    code.Line($"{okFlag} = false;");
                    code.CloseBlock();
                }
                code.Line($"{targetExpression} = {tempName};");
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                code.Line($"{okFlag} = false;");
                code.CloseBlock();
            }

            private static void EmitDeserializeArray(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string targetExpression,
                string tokenName,
                string okFlag)
            {
                code.Line($"if ({tokenName}.IsNull)");
                code.OpenBlock();
                code.Line($"{targetExpression} = null;");
                code.CloseBlock();
                code.Line($"else if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.DataList)");
                code.OpenBlock();
                string listName = code.Temp("__ce_list");
                string arrayName = code.Temp("__ce_array");
                string indexName = code.Temp("__ce_i");
                string itemToken = code.Temp("__ce_item");
                code.Line($"global::VRC.SDK3.Data.DataList {listName} = {tokenName}.DataList;");
                code.Line($"{GetArrayElementTypeName(spec.ElementType)}[] {arrayName} = new {GetArrayElementTypeName(spec.ElementType)}[{listName}.Count];");
                code.Line($"global::VRC.SDK3.Data.DataToken {itemToken};");
                code.Line($"for (int {indexName} = 0; {indexName} < {listName}.Count; {indexName}++)");
                code.OpenBlock();
                code.Line($"if ({listName}.TryGetValue({indexName}, out {itemToken}))");
                code.OpenBlock();
                EmitDeserializeFromToken(code, spec.ElementType, $"{arrayName}[{indexName}]", itemToken, okFlag);
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                code.Line($"{okFlag} = false;");
                code.CloseBlock();
                code.CloseBlock();
                code.Line($"{targetExpression} = {arrayName};");
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                code.Line($"{okFlag} = false;");
                code.CloseBlock();
            }

            private static void EmitDeserializeList(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string targetExpression,
                string tokenName,
                string okFlag)
            {
                code.Line($"if ({tokenName}.IsNull)");
                code.OpenBlock();
                code.Line($"{targetExpression} = null;");
                code.CloseBlock();
                code.Line($"else if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.DataList)");
                code.OpenBlock();
                string listName = code.Temp("__ce_list");
                string targetList = code.Temp("__ce_targetList");
                string indexName = code.Temp("__ce_i");
                string itemToken = code.Temp("__ce_item");
                string itemValue = code.Temp("__ce_itemValue");
                string elementTypeName = GetArrayElementTypeName(spec.ElementType);
                code.Line($"global::VRC.SDK3.Data.DataList {listName} = {tokenName}.DataList;");
                code.Line($"global::System.Collections.Generic.List<{elementTypeName}> {targetList} = new global::System.Collections.Generic.List<{elementTypeName}>();");
                code.Line($"global::VRC.SDK3.Data.DataToken {itemToken};");
                code.Line($"for (int {indexName} = 0; {indexName} < {listName}.Count; {indexName}++)");
                code.OpenBlock();
                code.Line($"if ({listName}.TryGetValue({indexName}, out {itemToken}))");
                code.OpenBlock();
                code.Line($"{elementTypeName} {itemValue} = default({elementTypeName});");
                EmitDeserializeFromToken(code, spec.ElementType, itemValue, itemToken, okFlag);
                code.Line($"{targetList}.Add({itemValue});");
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                code.Line($"{okFlag} = false;");
                code.CloseBlock();
                code.CloseBlock();
                code.Line($"{targetExpression} = {targetList};");
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                code.Line($"{okFlag} = false;");
                code.CloseBlock();
            }

            private static void EmitDeserializeCustomObject(
                CodeWriter code,
                SyncOnJoinTypeSpec spec,
                string targetExpression,
                string tokenName,
                string okFlag)
            {
                if (spec.IsReferenceType)
                {
                    code.Line($"if ({tokenName}.IsNull)");
                    code.OpenBlock();
                    code.Line($"{targetExpression} = null;");
                    code.CloseBlock();
                    code.Line($"else if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.DataDictionary)");
                    code.OpenBlock();
                    code.Line($"if ({targetExpression} == null)");
                    code.OpenBlock();
                    code.Line($"{targetExpression} = new {spec.TypeName}();");
                    code.CloseBlock();
                    code.Line($"if (!{targetExpression}.{FromDataMethodName}({tokenName}.DataDictionary))");
                    code.OpenBlock();
                    code.Line($"{okFlag} = false;");
                    code.CloseBlock();
                    code.CloseBlock();
                    code.Line("else");
                    code.OpenBlock();
                    code.Line($"{okFlag} = false;");
                    code.CloseBlock();
                    return;
                }

                code.Line($"if ({tokenName}.TokenType == global::VRC.SDK3.Data.TokenType.DataDictionary)");
                code.OpenBlock();
                string tempName = code.Temp("__ce_tmp");
                code.Line($"{spec.TypeName} {tempName} = {targetExpression};");
                code.Line($"if (!{tempName}.{FromDataMethodName}({tokenName}.DataDictionary))");
                code.OpenBlock();
                code.Line($"{okFlag} = false;");
                code.CloseBlock();
                code.Line($"{targetExpression} = {tempName};");
                code.CloseBlock();
                code.Line("else");
                code.OpenBlock();
                code.Line($"{okFlag} = false;");
                code.CloseBlock();
            }

            private static string GetArrayElementTypeName(SyncOnJoinTypeSpec spec)
            {
                return spec.TypeName;
            }

            /// <summary>
            /// A partial type has one symbol and several declaration parts. Emitting into every part gives two
            /// payload fields and two 'public override void OnPreSerialization()' declarations (CS0102/CS0111),
            /// so exactly one part -- the first by file path then position -- carries the generated members.
            /// The choice only depends on the symbol, so it is identical no matter which tree is visited first.
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

            private static bool DeclaresField(INamedTypeSymbol type, string fieldName)
            {
                return type.GetMembers(fieldName).OfType<IFieldSymbol>().Any();
            }

            private static bool DeclaresMethod(INamedTypeSymbol type, string methodName)
            {
                return type != null && type.GetMembers(methodName)
                    .OfType<IMethodSymbol>()
                    .Any(method => !method.IsImplicitlyDeclared);
            }

            /// <summary>
            /// Diagnostic locations must come from an INPUT tree, otherwise the compiler cannot match the
            /// location back to a module and reports 'Unknown File(1,1)'. Symbols declared in the compiled
            /// source always have one; the owning behaviour is the fallback for anything that does not.
            /// </summary>
            private static Location GetSymbolLocation(ISymbol symbol, INamedTypeSymbol fallbackOwner)
            {
                ImmutableArray<SyntaxReference> references = symbol.DeclaringSyntaxReferences;
                if (references.Length > 0)
                    return references[0].GetSyntax().GetLocation();

                if (fallbackOwner != null && !ReferenceEquals(symbol, fallbackOwner))
                    return GetSymbolLocation(fallbackOwner, null);

                return Location.None;
            }

            private static MemberDeclarationSyntax ParseMember(string memberSource)
            {
                CompilationUnitSyntax unit = SyntaxFactory.ParseCompilationUnit("class __CEGeneratedHost__ {" + memberSource + "}");
                return ((ClassDeclarationSyntax)unit.Members[0]).Members[0];
            }

            private static string GetMemberName(MemberDeclarationSyntax member)
            {
                switch (member)
                {
                    case FieldDeclarationSyntax field:
                        return string.Join(", ", field.Declaration.Variables.Select(v => v.Identifier.Text));
                    case MethodDeclarationSyntax method:
                        return method.Identifier.Text;
                    default:
                        return member.Kind().ToString();
                }
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

            private static string EscapeString(string value)
            {
                return value?.Replace("\\", "\\\\").Replace("\"", "\\\"") ?? string.Empty;
            }
        }

        private static readonly IReadOnlyList<SyncGroup> EmptyGroups = new SyncGroup[0];
        private static readonly IReadOnlyList<string> EmptyStatements = new string[0];

        /// <summary>
        /// One <c>[UdonSynced]</c> payload and the fields that ride in it.
        /// </summary>
        /// <remarks>
        /// Every generated member of a group is the corresponding base name suffixed with
        /// <see cref="Id"/>, and the default group's id is empty -- so a behaviour that never mentions
        /// <c>SyncOnJoinAttribute.Group</c> emits exactly the members it always did, under exactly the
        /// names <c>SendCustomEvent</c> callers and existing snapshots already use.
        /// </remarks>
        private sealed class SyncGroup
        {
            public SyncGroup(
                string name,
                string id,
                INamedTypeSymbol ownerType,
                IReadOnlyList<FieldSpec> fields,
                bool declaredByVisitedType)
            {
                Name = name;
                Id = id;
                OwnerType = ownerType;
                Fields = fields;
                DeclaredByVisitedType = declaredByVisitedType;
            }

            /// <summary>The group name as written, trimmed. Empty for the default group.</summary>
            public string Name { get; }

            /// <summary>Identifier suffix every generated member of this group carries. Empty for the default group.</summary>
            public string Id { get; }

            /// <summary>
            /// The base-most behaviour in the hierarchy that declares a field of this group, and therefore
            /// the one type that carries the group's payload, applied-guard and apply method. A derived
            /// behaviour adding fields to the same group only overrides the serialize/deserialize pair.
            /// </summary>
            public INamedTypeSymbol OwnerType { get; }

            /// <summary>Fields in this group, ordered by <c>SyncOnJoinAttribute.Priority</c>.</summary>
            public IReadOnlyList<FieldSpec> Fields { get; }

            /// <summary>
            /// Whether the type this group collection was built for declares a field of this group itself,
            /// as opposed to only inheriting it. A type that contributes no field needs no override.
            /// </summary>
            public bool DeclaredByVisitedType { get; }

            public string PayloadField => Suffixed(PayloadFieldName);
            public string AppliedField => Suffixed(AppliedFieldName);
            public string SerializeMethod => Suffixed(SerializeMethodName);
            public string DeserializeMethod => Suffixed(DeserializeMethodName);
            public string ApplyMethod => Suffixed(ApplyMethodName);

            public bool IsOwnedBy(INamedTypeSymbol type)
            {
                return SymbolEqualityComparer.Default.Equals(OwnerType, type);
            }

            private string Suffixed(string baseName)
            {
                return Id.Length == 0 ? baseName : baseName + "_" + Id;
            }
        }
        private sealed class FieldSpec
        {
            public FieldSpec(
                IFieldSymbol field,
                int priority,
                SyncOnJoinTypeSpec type,
                CustomSerializerSpec customSerializer)
            {
                FieldName = field.Name;
                Key = field.Name;
                DeclaringTypeName = field.ContainingType?.Name;
                Priority = priority;
                Type = type;
                CustomSerializer = customSerializer;
            }

            public string FieldName { get; }

            /// <summary>Payload key. Kept equal to the field name so a snapshot stays readable in logs.</summary>
            public string Key { get; }

            public string DeclaringTypeName { get; }
            public int Priority { get; }
            public SyncOnJoinTypeSpec Type { get; }
            public CustomSerializerSpec CustomSerializer { get; }
        }

        private sealed class CustomSerializerSpec
        {
            public CustomSerializerSpec(string serializeMethod, string deserializeMethod)
            {
                SerializeMethod = serializeMethod;
                DeserializeMethod = deserializeMethod;
            }

            public string SerializeMethod { get; }
            public string DeserializeMethod { get; }
        }

        private sealed class CodeWriter
        {
            private readonly System.Text.StringBuilder _builder = new System.Text.StringBuilder();
            private int _indent;
            private int _tempCounter;

            public CodeWriter(Location location)
            {
                Location = location ?? Location.None;
            }

            /// <summary>Input-tree location the member being written belongs to, for located generator errors.</summary>
            public Location Location { get; }

            public void Line(string line = "")
            {
                if (line.Length > 0)
                    _builder.Append(new string(' ', _indent * 4));
                _builder.AppendLine(line);
            }

            public void OpenBlock()
            {
                Line("{");
                _indent++;
            }

            public void CloseBlock()
            {
                _indent--;
                Line("}");
            }

            public string Temp(string prefix)
            {
                return $"{prefix}_{_tempCounter++}";
            }

            public override string ToString()
            {
                return _builder.ToString();
            }
        }
    }
}
