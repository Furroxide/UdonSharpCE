using System.Collections.Generic;
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
    /// <item>No synced state at all -> leave the behaviour alone. Injecting a mode here would force a
    /// sync method onto the GameObject and can fight a sibling behaviour's declared mode.</item>
    /// <item>Synced state, no declared mode -> inject Manual when something requires it (arrays,
    /// <c>[SyncOnJoin]</c> payloads), otherwise Continuous. This removes the invisible default where an
    /// undeclared behaviour silently resolves to Continuous at runtime.</item>
    /// <item>Declared mode that cannot carry the state -> hard compile error. A declared intent is never
    /// silently overridden.</item>
    /// </list>
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

                SyncRequirement requirement = AnalyzeSyncedFields(node, classSymbol);

                if (!requirement.HasSyncedState)
                    return visited;

                string declaredMode = GetDeclaredSyncMode(classSymbol);

                if (declaredMode != null)
                {
                    ValidateDeclaredMode(node, classSymbol, declaredMode, requirement);
                    return visited;
                }

                string inferredMode = requirement.RequiresManual ? "Manual" : "Continuous";

                ClassDeclarationSyntax withAttribute = visited.AddAttributeLists(
                    SyntaxFactory.AttributeList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Attribute(
                                    SyntaxFactory.ParseName("global::UdonSharp.UdonBehaviourSyncMode"),
                                    SyntaxFactory.AttributeArgumentList(
                                        SyntaxFactory.SingletonSeparatedList(
                                            SyntaxFactory.AttributeArgument(
                                                SyntaxFactory.ParseExpression(
                                                    $"global::UdonSharp.BehaviourSyncMode.{inferredMode}")))))))
                        .WithTriviaFrom(visited));

                ChangesMade = true;

                _context.RecordOptimization(
                    optimizerId: "CEGEN008",
                    description:
                    $"CE set '{node.Identifier.Text}' to {inferredMode} sync because it declares synced state" +
                    (requirement.RequiresManual
                        ? $" that requires Manual sync ({requirement.ManualReason})."
                        : " and no [UdonBehaviourSyncMode] was present."),
                    location: node.GetLocation(),
                    originalCode: node.Identifier.Text,
                    optimizedCode: inferredMode);

                return withAttribute;
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


            private SyncRequirement AnalyzeSyncedFields(ClassDeclarationSyntax node, INamedTypeSymbol classSymbol)
            {
                var requirement = new SyncRequirement();
                var syncedFieldNames = new List<string>();

                foreach (FieldDeclarationSyntax field in node.Members.OfType<FieldDeclarationSyntax>())
                {
                    SyncKind kind = ClassifySyncAttributes(field);
                    if (kind == SyncKind.None)
                        continue;

                    requirement.HasSyncedState = true;

                    foreach (VariableDeclaratorSyntax variable in field.Declaration.Variables)
                        syncedFieldNames.Add(variable.Identifier.Text);

                    if (kind == SyncKind.SyncOnJoin)
                    {
                        // [SyncOnJoin] is transported as a synced JSON string on this behaviour, which only
                        // Manual sync can carry.
                        requirement.RequiresManual = true;
                        requirement.ManualReason = "[SyncOnJoin] state is transported as a synced string";
                        continue;
                    }

                    ITypeSymbol fieldType = _semanticModel.GetTypeInfo(field.Declaration.Type).Type;
                    if (fieldType is IArrayTypeSymbol)
                    {
                        requirement.RequiresManual = true;
                        requirement.ManualReason = "synced arrays are not supported by Continuous sync";
                    }
                    else if (fieldType != null && fieldType.SpecialType == SpecialType.System_String)
                    {
                        requirement.RequiresManual = true;
                        requirement.ManualReason = "a synced string can exceed the Continuous sync budget";
                    }
                }

                requirement.FieldSummary = syncedFieldNames.Count > 0
                    ? "'" + string.Join("', '", syncedFieldNames) + "'"
                    : "synced fields";

                return requirement;
            }

            /// <summary>
            /// Resolves each attribute through the semantic model so <c>[Sync]</c> (which derives from
            /// <c>UdonSyncedAttribute</c>) and aliased usings are both recognised. Matching on the written
            /// text alone lets an alias slip past the generator while the analyzer still sees the field.
            /// </summary>
            private SyncKind ClassifySyncAttributes(FieldDeclarationSyntax field)
            {
                SyncKind result = SyncKind.None;

                foreach (AttributeListSyntax attributeList in field.AttributeLists)
                {
                    foreach (AttributeSyntax attribute in attributeList.Attributes)
                    {
                        ITypeSymbol attributeType = _semanticModel.GetTypeInfo(attribute).Type;

                        if (attributeType == null)
                        {
                            // Fall back to the written name when the model cannot resolve the attribute.
                            string written = attribute.Name.ToString();
                            string shortName = written.Substring(written.LastIndexOf('.') + 1);

                            if (shortName == "SyncOnJoin" || shortName == "SyncOnJoinAttribute")
                                return SyncKind.SyncOnJoin;

                            if (shortName == "UdonSynced" || shortName == "UdonSyncedAttribute" ||
                                shortName == "Sync" || shortName == "SyncAttribute")
                                result = SyncKind.Synced;

                            continue;
                        }

                        if (IsOrDerivesFrom(attributeType, "SyncOnJoinAttribute"))
                            return SyncKind.SyncOnJoin;

                        if (IsOrDerivesFrom(attributeType, "UdonSyncedAttribute"))
                            result = SyncKind.Synced;
                    }
                }

                return result;
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
            public bool HasSyncedState;
            public bool RequiresManual;
            public string ManualReason = "";
            public string FieldSummary = "";
        }
    }
}
