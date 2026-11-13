using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using UdonSharp.CE.Editor.Optimizers;
using UdonSharp.Compiler;

namespace UdonSharp.CE.Editor.CodeGen
{
    /// <summary>
    /// Infers and injects <c>[UdonBehaviourSyncMode]</c> so authors never have to reason about VRChat's
    /// sync modes by hand.
    ///
    /// This works because CE's transforms run before Roslyn: an injected attribute is seen by
    /// <c>UdonSharpCompilerV1</c> when it sets <c>programAsset.behaviourSyncMode</c>, which
    /// <c>UdonSharpEditorManager.UpdateSyncModes</c> then applies to every scene UdonBehaviour, and which
    /// <c>UdonSharpGUI</c> shows as a locked dropdown. No new plumbing is required.
    ///
    /// Rules:
    /// <list type="bullet">
    /// <item>No synced state of its own -> leave the behaviour alone. Injecting a mode here would force a
    /// sync method onto the GameObject and can fight a sibling behaviour's declared mode; a class that
    /// only inherits synced state already resolves to whatever its base declares or was given.</item>
    /// <item>Synced state, no declared mode -> inject Manual when something requires it (arrays,
    /// <c>[SyncOnJoin]</c> payloads) or prefers it (a synced string, on a behaviour with no interpolated
    /// field), otherwise Continuous. This removes the invisible default where an undeclared behaviour
    /// silently resolves to Continuous at runtime.</item>
    /// <item>Declared mode that cannot carry the state -> hard compile error. A declared intent is never
    /// silently overridden.</item>
    /// </list>
    ///
    /// "The state" is the behaviour's whole synced state: every partial declaration of the class and
    /// every base class up to <c>UdonSharpBehaviour</c>. The compiler emits inherited fields into the
    /// derived program and gives that program exactly one sync mode, so a decision made from one
    /// declaration's own fields is a decision about a program that does not exist.
    ///
    /// "Requires" and "prefers" are kept apart on purpose. Continuous sync cannot carry an array at all,
    /// and a <c>[SyncOnJoin]</c> payload needs Manual's explicit serialization; those are requirements,
    /// and a declared Continuous that contradicts one is CE0034. A string is different: Continuous syncs
    /// it, it merely shares the ~200 byte budget with everything else on the behaviour. Treating that as
    /// a requirement rejected working behaviours outright, and left one shape with no mode that compiled
    /// at all -- a smoothed field beside a string was pushed to Manual by the string, refused under
    /// Manual by the compiler because of the interpolation, and refused under a declared Continuous by
    /// CE0034 because of the string.
    /// </summary>
    internal class BehaviourSyncModeEmitter : ICompileTimeOptimizer
    {
        public string OptimizerId => "CEGEN008";
        public string OptimizerName => "Behaviour Sync Mode Inference";
        public string Description => "Infers [UdonBehaviourSyncMode] from the behaviour's synced fields.";
        public bool IsEnabledByDefault => true;
        public int Priority => 8;

        private const string SyncModeAttributeName = "UdonBehaviourSyncModeAttribute";

        public SyntaxTree Optimize(SyntaxTree tree, OptimizationContext context)
        {
            SemanticModel semanticModel = context.GetSemanticModel(tree);
            if (semanticModel == null)
                return tree;

            var rewriter = new Rewriter(semanticModel, context);
            SyntaxNode root = tree.GetRoot();
            SyntaxNode newRoot = rewriter.Visit(root);

            if (!rewriter.ChangesMade || newRoot == null)
                return tree;

            return tree.WithRootAndOptions(newRoot, tree.Options);
        }

        private sealed class Rewriter : CSharpSyntaxRewriter
        {
            private readonly SemanticModel _semanticModel;
            private readonly OptimizationContext _context;

            public bool ChangesMade { get; private set; }

            public Rewriter(SemanticModel semanticModel, OptimizationContext context)
            {
                _semanticModel = semanticModel;
                _context = context;
            }

            public override SyntaxNode VisitClassDeclaration(ClassDeclarationSyntax node)
            {
                // Query the ORIGINAL node against the semantic model. Children of a rewritten node are not
                // in the model's syntax tree and GetDeclaredSymbol/GetTypeInfo throw on them.
                INamedTypeSymbol classSymbol = _semanticModel.GetDeclaredSymbol(node);

                var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node);

                if (classSymbol == null || !IsUdonSharpBehaviour(classSymbol))
                    return visited;

                // A partial behaviour is one type with several declarations, and every one of them is
                // visited. Deciding per declaration gave each part that held a synced field its own
                // injected attribute -- CS0579, a duplicate attribute on a class whose author wrote none --
                // and let two parts infer two different modes. One part decides for the whole type.
                if (!IsCanonicalDeclarationPart(classSymbol, node))
                    return visited;

                SyncRequirement requirement = AnalyzeSyncedFields(classSymbol);

                if (!requirement.DeclaresSyncedState)
                    return visited;

                string declaredMode = GetDeclaredSyncMode(classSymbol);

                if (declaredMode != null)
                {
                    ValidateDeclaredMode(node, classSymbol, declaredMode, requirement);
                    return visited;
                }

                // A string only asks for Manual. Honouring that on a behaviour that also interpolates a
                // field would pick the one mode the compiler then rejects the interpolation under.
                bool manualForString = requirement.PrefersManual && !requirement.HasInterpolatedField;
                string inferredMode = (requirement.RequiresManual || manualForString) ? "Manual" : "Continuous";

                AttributeListSyntax attributeList = SyntaxFactory.AttributeList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Attribute(
                            SyntaxFactory.ParseName("global::UdonSharp.UdonBehaviourSyncMode"),
                            SyntaxFactory.AttributeArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.AttributeArgument(
                                        SyntaxFactory.ParseExpression(
                                            $"global::UdonSharp.BehaviourSyncMode.{inferredMode}")))))));

                // The injected list goes on the same line as the token it is put in front of, and TAKES
                // that token's leading trivia instead of copying the declaration's. Copying it printed
                // every comment and blank line above the class a second time, so the rewritten file ran
                // that many lines longer than the author's and each later diagnostic in it -- reported by
                // line in the rewritten tree -- pointed that far below the code it was about.
                SyntaxToken anchor = visited.Modifiers.Count > 0 ? visited.Modifiers[0] : visited.Keyword;

                attributeList = attributeList
                    .WithLeadingTrivia(anchor.LeadingTrivia)
                    .WithTrailingTrivia(SyntaxFactory.Space);

                ClassDeclarationSyntax withAttribute = visited
                    .ReplaceToken(anchor, anchor.WithLeadingTrivia(SyntaxFactory.TriviaList()))
                    .AddAttributeLists(attributeList);

                ChangesMade = true;

                _context.RecordOptimization(
                    optimizerId: "CEGEN008",
                    description:
                    $"CE set '{node.Identifier.Text}' to {inferredMode} sync because it declares synced state" +
                    (requirement.RequiresManual
                        ? $" that requires Manual sync ({requirement.ManualReason})."
                        : manualForString
                            ? " that includes a synced string, which can exceed the Continuous sync budget."
                            : " and no [UdonBehaviourSyncMode] was present."),
                    location: node.GetLocation(),
                    originalCode: node.Identifier.Text,
                    optimizedCode: inferredMode);

                return withAttribute;
            }

            /// <summary>
            /// Whether this declaration is the one part of a (possibly partial) type that decides its mode:
            /// the first by file path, then by position. The choice depends only on the symbol, so it is the
            /// same whichever tree is visited first, and it is the same rule SyncOnJoinGlueEmitter uses to
            /// place its generated members -- the attribute and the payload land on the same part.
            /// </summary>
            private static bool IsCanonicalDeclarationPart(INamedTypeSymbol type, ClassDeclarationSyntax node)
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

            private void ValidateDeclaredMode(
                ClassDeclarationSyntax node,
                INamedTypeSymbol classSymbol,
                string declaredMode,
                SyncRequirement requirement)
            {
                if (declaredMode == "None" || declaredMode == "NoVariableSync")
                {
                    throw new CodeGenerationException(
                        "CE0033",
                        node.Identifier.GetLocation(),
                        $"'{classSymbol.Name}' declares BehaviourSyncMode.{declaredMode} but has synced state " +
                        $"({requirement.FieldSummary}). Nothing on this behaviour is synchronized" +
                        (declaredMode == "None" ? " and SendCustomNetworkEvent does not work on it" : "") +
                        ", so that state can never reach other players. " +
                        "Fix: change the attribute to [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)], or " +
                        "remove the synced attributes and drive this state from a separate synced behaviour.");
                }

                if (declaredMode == "Continuous" && requirement.RequiresManual)
                {
                    throw new CodeGenerationException(
                        "CE0034",
                        node.Identifier.GetLocation(),
                        $"'{classSymbol.Name}' declares BehaviourSyncMode.Continuous but has synced state that " +
                        $"requires Manual sync ({requirement.ManualReason}). Continuous sync is capped near " +
                        "200 bytes per serialization and does not support arrays at all. " +
                        "Fix: change the attribute to [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)], or move " +
                        "that state onto a separate Manual-sync behaviour on a different GameObject.");
                }
            }

            /// <summary>
            /// Resolves the sync mode the author declared, searching this class and then its base types.
            ///
            /// The base-type walk is not optional. <c>UdonSharpCompilerV1</c> resolves the mode by climbing
            /// from the emitted type to <c>UdonSharpBehaviour</c> and stopping at the first attribute it
            /// finds, so a mode declared on a base class is authoritative for every derived class. Scanning
            /// only this declaration's own attribute lists made an inherited declaration invisible here, and
            /// this emitter would then inject a mode of its own onto the derived class -- which the compiler
            /// finds first. A behaviour the author declared <c>None</c> silently became <c>Continuous</c>,
            /// the exact opposite of the promise above that a declared mode is never overridden.
            ///
            /// Resolution is symbol-based rather than textual so a <c>using</c> alias cannot hide the
            /// attribute from this emitter while the compiler still sees it.
            /// </summary>
            private static string GetDeclaredSyncMode(INamedTypeSymbol classSymbol)
            {
                for (INamedTypeSymbol current = classSymbol; current != null; current = current.BaseType)
                {
                    if (current.Name == "UdonSharpBehaviour")
                        break;

                    foreach (AttributeData attribute in current.GetAttributes())
                    {
                        if (attribute.AttributeClass == null ||
                            attribute.AttributeClass.Name != SyncModeAttributeName)
                        {
                            continue;
                        }

                        if (attribute.ConstructorArguments.Length == 0)
                            return "Any";

                        TypedConstant argument = attribute.ConstructorArguments[0];
                        if (argument.Value == null)
                            return "Any";

                        // BehaviourSyncMode is an enum, so the constant is its underlying integer. Map it
                        // back through the declaring enum's own members rather than hardcoding the order.
                        string name = EnumMemberName(argument);
                        if (name != null)
                            return name;
                    }
                }

                return null;
            }

            private static string EnumMemberName(TypedConstant argument)
            {
                if (argument.Type == null)
                    return argument.Value?.ToString();

                foreach (ISymbol member in argument.Type.GetMembers())
                {
                    if (member is IFieldSymbol field &&
                        field.HasConstantValue &&
                        Equals(field.ConstantValue, argument.Value))
                    {
                        return field.Name;
                    }
                }

                return argument.Value?.ToString();
            }


            /// <summary>
            /// Works out what the behaviour's synced state needs, from its SYMBOL: every partial part of the
            /// class, and every base class up to <c>UdonSharpBehaviour</c>.
            /// </summary>
            /// <remarks>
            /// This used to read the fields of the one declaration being visited, which was wrong twice.
            ///
            /// A derived behaviour's program carries its base's synced fields as well as its own, and the
            /// compiler takes the first <c>[UdonBehaviourSyncMode]</c> it meets climbing from the emitted
            /// type. So 'class Base { [SyncOnJoin] int phase; }' was given Manual, 'class Derived : Base
            /// { [UdonSynced] float speed; }' looked only at 'speed' and was given Continuous, and Derived
            /// ran Continuous while carrying the inherited late-join payload -- with nothing reported,
            /// because a synced string is legal under Continuous. With an inherited array instead, the same
            /// shape failed to compile on a class that had declared nothing. Inherited state is state.
            ///
            /// The other half is the partial case, handled together with
            /// <see cref="IsCanonicalDeclarationPart"/>: the part that decides has to see the fields the
            /// other parts hold, or two parts with different fields infer two different modes.
            ///
            /// Symbols are used rather than syntax because a base class is usually in another file, and
            /// this pass's semantic model can only be asked about nodes in its own tree.
            /// </remarks>
            private static SyncRequirement AnalyzeSyncedFields(INamedTypeSymbol classSymbol)
            {
                var requirement = new SyncRequirement();
                var syncedFieldNames = new List<string>();

                for (INamedTypeSymbol current = classSymbol; current != null; current = current.BaseType)
                {
                    if (current.Name == "UdonSharpBehaviour")
                        break;

                    bool inherited = !SymbolEqualityComparer.Default.Equals(current, classSymbol);

                    foreach (ISymbol member in current.GetMembers())
                    {
                        if (!(member is IFieldSymbol field))
                            continue;

                        SyncKind kind = ClassifySyncAttributes(field, out bool interpolated);
                        if (kind == SyncKind.None)
                            continue;

                        if (!inherited)
                            requirement.DeclaresSyncedState = true;

                        if (interpolated)
                            requirement.HasInterpolatedField = true;

                        syncedFieldNames.Add(inherited ? current.Name + "." + field.Name : field.Name);

                        // The walk starts at the class itself, so its own fields are met before any
                        // inherited one and get to explain the requirement first.
                        string manualReason = null;

                        if (kind == SyncKind.SyncOnJoin)
                        {
                            // [SyncOnJoin] is transported as a synced JSON string on this behaviour, and the
                            // generated glue is written for Manual sync.
                            manualReason = "[SyncOnJoin] state is transported as a synced string";
                        }
                        else if (field.Type is IArrayTypeSymbol)
                        {
                            manualReason = "synced arrays are not supported by Continuous sync";
                        }
                        else if (field.Type != null && field.Type.SpecialType == SpecialType.System_String)
                        {
                            // A preference, not a requirement: Continuous does sync a string, it only shares
                            // the ~200 byte budget with the rest of the behaviour.
                            requirement.PrefersManual = true;
                        }

                        if (manualReason == null)
                            continue;

                        requirement.RequiresManual = true;

                        if (requirement.ManualReason.Length == 0)
                        {
                            requirement.ManualReason = inherited
                                ? $"{manualReason}; '{field.Name}' is inherited from '{current.Name}'"
                                : manualReason;
                        }
                    }
                }

                requirement.FieldSummary = syncedFieldNames.Count > 0
                    ? "'" + string.Join("', '", syncedFieldNames) + "'"
                    : "synced fields";

                return requirement;
            }

            /// <summary>
            /// Classifies a field through its bound attribute classes so <c>[Sync]</c> (which derives from
            /// <c>UdonSyncedAttribute</c>) and aliased usings are both recognised. Matching on the written
            /// text alone lets an alias slip past the generator while the analyzer still sees the field.
            /// </summary>
            private static SyncKind ClassifySyncAttributes(IFieldSymbol field, out bool interpolated)
            {
                SyncKind result = SyncKind.None;
                interpolated = false;

                foreach (AttributeData attribute in field.GetAttributes())
                {
                    INamedTypeSymbol attributeType = attribute.AttributeClass;

                    if (attributeType == null || attributeType.TypeKind == TypeKind.Error)
                    {
                        // Fall back to the written name when the model cannot resolve the attribute.
                        if (!(attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax written))
                            continue;

                        string writtenName = written.Name.ToString();
                        string shortName = writtenName.Substring(writtenName.LastIndexOf('.') + 1);

                        if (shortName == "SyncOnJoin" || shortName == "SyncOnJoinAttribute")
                            return SyncKind.SyncOnJoin;

                        if (shortName == "UdonSynced" || shortName == "UdonSyncedAttribute" ||
                            shortName == "Sync" || shortName == "SyncAttribute")
                        {
                            result = SyncKind.Synced;

                            if (WritesInterpolation(written))
                                interpolated = true;
                        }

                        continue;
                    }

                    if (IsOrDerivesFrom(attributeType, "SyncOnJoinAttribute"))
                        return SyncKind.SyncOnJoin;

                    if (IsOrDerivesFrom(attributeType, "UdonSyncedAttribute"))
                    {
                        result = SyncKind.Synced;

                        if (DeclaresInterpolation(attribute))
                            interpolated = true;
                    }
                }

                return result;
            }

            /// <summary>
            /// Whether a bound synced attribute asks for Linear or Smooth interpolation, in either spelling:
            /// <c>[UdonSynced(UdonSyncMode.Smooth)]</c>, or <c>[Sync(...)]</c> with an
            /// <c>InterpolationMode</c> given positionally or as <c>Interpolation = ...</c>.
            /// </summary>
            /// <remarks>
            /// The argument is found by its enum type rather than by position or name, so the two attribute
            /// shapes and both of [Sync]'s spellings are one test. Only Linear and Smooth count; None and
            /// NotSynced interpolate nothing.
            /// </remarks>
            private static bool DeclaresInterpolation(AttributeData attribute)
            {
                foreach (TypedConstant argument in attribute.ConstructorArguments)
                {
                    if (IsInterpolatingArgument(argument))
                        return true;
                }

                foreach (KeyValuePair<string, TypedConstant> named in attribute.NamedArguments)
                {
                    if (IsInterpolatingArgument(named.Value))
                        return true;
                }

                return false;
            }

            private static bool IsInterpolatingArgument(TypedConstant argument)
            {
                if (argument.Kind != TypedConstantKind.Enum || argument.Type == null)
                    return false;

                if (argument.Type.Name != "UdonSyncMode" && argument.Type.Name != "InterpolationMode")
                    return false;

                string name = EnumMemberName(argument);
                return name == "Linear" || name == "Smooth";
            }

            /// <summary>
            /// The same question for an attribute the model could not bind, answered from the written
            /// arguments. Anything it cannot read is taken as "no interpolation", which leaves the inference
            /// where it was before interpolation was considered at all.
            /// </summary>
            private static bool WritesInterpolation(AttributeSyntax attribute)
            {
                if (attribute.ArgumentList == null)
                    return false;

                foreach (AttributeArgumentSyntax argument in attribute.ArgumentList.Arguments)
                {
                    string written = argument.Expression is MemberAccessExpressionSyntax memberAccess
                        ? memberAccess.Name.Identifier.Text
                        : (argument.Expression as IdentifierNameSyntax)?.Identifier.Text;

                    if (written == "Linear" || written == "Smooth")
                        return true;
                }

                return false;
            }

            private static bool IsOrDerivesFrom(ITypeSymbol type, string baseTypeName)
            {
                for (ITypeSymbol current = type; current != null; current = current.BaseType)
                {
                    if (current.Name == baseTypeName)
                        return true;
                }

                return false;
            }

            private static bool IsUdonSharpBehaviour(INamedTypeSymbol classSymbol)
            {
                for (INamedTypeSymbol current = classSymbol.BaseType; current != null; current = current.BaseType)
                {
                    if (current.Name == "UdonSharpBehaviour")
                        return true;
                }

                return false;
            }
        }

        private enum SyncKind
        {
            None,
            Synced,
            SyncOnJoin,
        }

        private sealed class SyncRequirement
        {
            /// <summary>
            /// The class itself (any of its partial parts) declares a synced field. Inherited state alone
            /// does not set this: such a class is left to resolve to its base's mode.
            /// </summary>
            public bool DeclaresSyncedState;

            /// <summary>Something in the whole synced state cannot work under Continuous at all.</summary>
            public bool RequiresManual;

            /// <summary>Something in the whole synced state is better served by Manual, but syncs either way.</summary>
            public bool PrefersManual;

            /// <summary>Some synced field asks for Linear or Smooth interpolation, which only Continuous provides.</summary>
            public bool HasInterpolatedField;

            public string ManualReason = "";
            public string FieldSummary = "";
        }
    }
}
