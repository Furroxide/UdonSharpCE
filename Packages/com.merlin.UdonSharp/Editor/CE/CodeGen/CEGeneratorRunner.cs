using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using UdonSharp.CE.Editor.Async;
using UdonSharp.CE.Editor.Optimizers;
using UdonSharp.CE.Editor.Persistence;
using UdonSharp.Compiler;
using UnityEngine;

// Lets the compiler discover CEGeneratorRunner without a full type scan of every loaded assembly.
[assembly: UdonSharpSourceGeneratorAssembly]

// Lets the CE test suites assert on generator/optimizer registration directly.
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("CEShowcase.Tests")]

namespace UdonSharp.CE.Editor.CodeGen
{
    /// <summary>
    /// Runs CE's correctness-critical source generators.
    ///
    /// These are deliberately separated from <see cref="CEOptimizerRunner"/>. A generator emits code the
    /// program is *incorrect* without -- synced-variable glue, async state machines, quantization,
    /// persistence converters, Action-to-callback and CELogger rewrites. None of that is an optimization,
    /// so none of it may be skipped, disabled, or downgraded to a warning.
    ///
    /// Registration happens via the compiler force-running this static constructor
    /// (<see cref="UdonSharpSourceGeneratorProviderAttribute"/>), so it no longer depends on
    /// <c>[InitializeOnLoad]</c> ordering between CE's registrars.
    /// </summary>
    [UdonSharpSourceGeneratorProvider(ProviderId, order: 0)]
    internal static class CEGeneratorRunner
    {
        internal const string ProviderId = "UdonSharp.CE";

        /// <summary>
        /// Id of the persistence auto-registration generator. It operates on the whole tree set rather than
        /// per-tree, so it has no <see cref="ICompileTimeOptimizer"/> instance to source an id from and is
        /// the one id in this file that has to be a literal.
        /// </summary>
        private const string PersistenceGeneratorId = "CEGEN004";

        /// <summary>A generator plus the source tokens that prove it was required.</summary>
        private sealed class GeneratorEntry
        {
            public ICompileTimeOptimizer Transform;
            public int Order;

            /// <summary>
            /// Literals whose presence in real code (not a comment, not a string) makes this generator's
            /// output MANDATORY -- if one of these is written and the generator transformed nothing, that
            /// is a silent codegen failure and USCE0001 fails the compile.
            ///
            /// That obligation is the whole bar. A generator with any legitimate no-op path -- an opt-out
            /// attribute, an author-supplied declaration it defers to, a token that can appear without
            /// obliging it -- declares NO tokens and states why at its entry below, because a tripwire that
            /// can fire on a correct project turns a working compile into a fatal error for everyone.
            /// </summary>
            public string[] TriggerTokens;

            /// <summary>Single-sourced from the transform so an id can never drift between the two.</summary>
            public string GeneratorId => Transform.OptimizerId;
        }

        private static readonly GeneratorEntry[] Generators;

        static CEGeneratorRunner()
        {
            Generators = new[]
            {
                // Async must run before anything else touches method bodies.
                new GeneratorEntry
                {
                    Transform = new AsyncMethodTransformOptimizer(),   // CEASYNC001
                    Order = 1,
                    // No tripwire, and none is needed: RejectUnloweredAwaits already throws CE0024 on any
                    // await this generator failed to consume, so a silent miss is impossible. "UdonTask" is
                    // also not an obligation -- a behaviour may name the type in a field or a Delay() call
                    // in a file with no async method to lower.
                    TriggerTokens = Array.Empty<string>(),
                },
                new GeneratorEntry
                {
                    Transform = new LambdaInliningOptimizer(),         // CEOPT011
                    Order = 2,
                    // No tripwire: neither token is an obligation on THIS pass. "(() =>" as an argument
                    // (Foo(() => Bar())) is deliberately left standing here and consumed by
                    // ActionToCallbackTransformer at order 5, and ".ForEach(" on a receiver that is not a
                    // resolvable CE collection is correctly left alone. Either would fail a working compile.
                    TriggerTokens = Array.Empty<string>(),
                },
                new GeneratorEntry
                {
                    Transform = new ActionToCallbackTransformer(),      // CEOPT007
                    Order = 5,
                    // No tripwire: [CEPreserveAction] is a supported opt-out, so a project can legitimately
                    // declare Actions this pass must not touch and still report zero transforms.
                    TriggerTokens = Array.Empty<string>(),
                },
                new GeneratorEntry
                {
                    Transform = new CELoggerTransformOptimizer(),       // CEOPT006
                    Order = 6,
                    // No tripwire: the rewriter only rewrites CELogger *invocations*, and "CELogger." also
                    // covers the documented property surface (CELogger.MinLevel = ...), which is a
                    // legitimate zero-transform use of the API.
                    TriggerTokens = Array.Empty<string>(),
                },
                // Sync-mode inference must run before the SyncOnJoin emitter so the injected
                // [UdonBehaviourSyncMode(Manual)] is already on the class when the synced payload appears.
                new GeneratorEntry
                {
                    Transform = new BehaviourSyncModeEmitter(),         // CEGEN008
                    Order = 8,
                    // No tripwire: this emitter defers to an explicit [UdonBehaviourSyncMode] on the class,
                    // so a project whose synced behaviours all declare their mode by hand is correct and
                    // transforms nothing.
                    TriggerTokens = Array.Empty<string>(),
                },
                new GeneratorEntry
                {
                    Transform = new SyncOnJoinGlueEmitter(),            // CEGEN001
                    Order = 10,
                    // Obligation: a written [SyncOnJoin] field has no meaning without the generated
                    // serialize/deserialize pair and the [UdonSynced] payload field. Emitting nothing leaves
                    // a behaviour that looks late-join-safe and sends a late joiner nothing at all.
                    TriggerTokens = new[] { "[SyncOnJoin" },
                },
                new GeneratorEntry
                {
                    Transform = new RpcEmitter(),                       // CEGEN009
                    Order = 11,
                    // Obligation: [Rpc] is inert without the injected [NetworkCallable] and the rewritten
                    // call site -- the method stays a plain local call that never reaches another client.
                    TriggerTokens = new[] { "[Rpc" },
                },
                new GeneratorEntry
                {
                    Transform = new QuantizePreSerializeEmitter(),      // CEGEN002
                    Order = 20,
                    // No tripwire: the trigger is the named argument in [Sync(Quantize = ...)], and no
                    // literal picks that out reliably -- "Quantize" alone matches ordinary identifiers
                    // (StateCompressor.QuantizeQuaternionComponent), while "Quantize =" also matches a
                    // plain property assignment. The emitter also legitimately skips a non-behaviour that
                    // carries an unrelated attribute named Sync.
                    TriggerTokens = Array.Empty<string>(),
                },
            }
            .OrderBy(g => g.Order)
            .ThenBy(g => g.GeneratorId, StringComparer.Ordinal)
            .ToArray();

            AssertUniqueTransformIds();

            SourceGeneratorRegistry.RegisterProvider(ProviderId, 0, Run);
        }

        /// <summary>
        /// Guards against two transforms sharing an id, across BOTH registries.
        ///
        /// The collision this exists to prevent -- CEOPT009 claimed by the SyncOnJoin glue emitter and by
        /// common subexpression elimination, CEOPT010 by the quantize emitter and loop-invariant code
        /// motion -- now spans the generator set and the optimizer set, so a check that walks only one of
        /// them cannot see it. Ids key the optimization report, the tripwire table and any future per-pass
        /// toggle, so a duplicate silently attributes sync glue emission to a perf pass.
        ///
        /// This throws instead of logging: the static constructor runs inside
        /// <see cref="SourceGeneratorRegistry.EnsureProvidersRegistered"/>, which turns the failure into a
        /// USCE0002 compile error. A Debug.LogError cannot fail a compile, so it would scroll past.
        /// </summary>
        private static void AssertUniqueTransformIds()
        {
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            var duplicates = new List<string>();

            Action<string, string> claim = (id, owner) =>
            {
                if (owners.TryGetValue(id, out string existingOwner))
                    duplicates.Add($"'{id}' is claimed by both '{existingOwner}' and '{owner}'");
                else
                    owners.Add(id, owner);
            };

            foreach (GeneratorEntry entry in Generators)
                claim(entry.GeneratorId, entry.Transform.OptimizerName);

            claim(PersistenceGeneratorId, "Persistence Auto-Registration");

            foreach ((string Id, string Name) optimizer in HarvestOptimizerIds())
                claim(optimizer.Id, optimizer.Name);

            if (duplicates.Count > 0)
            {
                throw new InvalidOperationException(
                    "[CE.CodeGen] Duplicate transform ids across the generator and optimizer registries: " +
                    string.Join("; ", duplicates) + ". Ids must be unique across both registries -- they " +
                    "key the optimization report, the generator tripwires and any per-pass toggle.");
            }
        }

        /// <summary>
        /// The optimizer-side half of the id harvest, isolated so a fault there cannot take the generator
        /// registration with it.
        ///
        /// <see cref="CEOptimizerRunner.GetRegisteredTransformIds"/> is the first touch of
        /// <c>CEOptimizerRunner</c>, so it forces that type's static constructor, which constructs all six
        /// perf optimizers. An exception in any one of those constructors would escape this cctor, and the
        /// CLR would then leave <see cref="CEGeneratorRunner"/> in the faulted state -- so the CORRECTNESS
        /// generator provider would never register, and sync glue, RPC rewriting and persistence codegen
        /// would all silently disappear because an OPTIMIZER broke. That inverts the entire reason the two
        /// registries are split. A missed duplicate-id check is a report-attribution bug; a missed generator
        /// is a shipped world that does not sync.
        /// </summary>
        private static (string Id, string Name)[] HarvestOptimizerIds()
        {
            try
            {
                return CEOptimizerRunner.GetRegisteredTransformIds();
            }
            catch (Exception e)
            {
                // Warning, not throw: the generator-set half of the assertion above still runs, and the
                // optimizer registry's own failure surfaces on its own when the optimizer phase runs.
                Debug.LogWarning(
                    "[CE.CodeGen] Could not read the optimizer registry's transform ids, so the id-uniqueness " +
                    "check covered the generator set only. A generator/optimizer id collision would go " +
                    $"undetected until it misattributes the optimization report: {e}");

                return Array.Empty<(string, string)>();
            }
        }

        /// <summary>
        /// Tripwires declared for this provider. The compiler uses these to detect a generator that was
        /// present and registered but silently produced nothing -- the failure shape that let a single
        /// codegen exception strip every CE transform behind one console warning.
        ///
        /// Only the three generators whose trigger is a hard obligation appear here; the rest say at their
        /// entry in <see cref="Generators"/> why they cannot be tripwired without failing correct projects.
        /// Nothing is lost on the "never ran" side by that: a generator can only fail to run at all when
        /// this whole provider failed to register, and the tripwires that remain report that just as well.
        /// </summary>
        private static IReadOnlyList<SourceGeneratorTripwire> BuildTripwires()
        {
            var tripwires = new List<SourceGeneratorTripwire>();

            foreach (GeneratorEntry entry in Generators)
            {
                // A generator with no tokens is intentionally untripwired -- see the comment at its entry
                // for the no-op path that makes a zero-transform compile legitimate for it. Any new entry
                // that cannot be tripwired must say why there rather than silently landing here.
                if (entry.TriggerTokens.Length > 0)
                    tripwires.Add(new SourceGeneratorTripwire(entry.GeneratorId, "USCE0001", entry.TriggerTokens));
            }

            // Obligation: a [PlayerData] model with no generated converter means CEPersistence.Save/Restore
            // compile and then do nothing at runtime -- player data silently never persists. The token is
            // the model declaration, not "CEPersistence.": calling the persistence API without declaring a
            // model is legitimate and generates nothing.
            tripwires.Add(new SourceGeneratorTripwire(PersistenceGeneratorId, "USCE0001", "[PlayerData"));

            return tripwires;
        }

        /// <summary>
        /// Runs every CE generator over the full tree set.
        ///
        /// Nothing here swallows a failure. A generator failure must reach the compiler so it becomes a
        /// compile error rather than a silently-unglued program; both catches below rethrow immediately and
        /// only attach the file the generator was working on.
        ///
        /// The per-generator <see cref="SourceGeneratorRunRecord"/> carries FilesTransformed, not just a
        /// status, because status alone is what the compiler's tripwire reads to decide whether a generator
        /// silently no-opped -- a Ran record with zero transforms and a written trigger token is the failure
        /// shape USCE0001 exists for.
        /// </summary>
        private static SourceGenerationResult Run((SyntaxTree tree, string filePath)[] syntaxTrees)
        {
            IReadOnlyList<SourceGeneratorTripwire> tripwires = BuildTripwires();

            if (syntaxTrees == null || syntaxTrees.Length == 0)
                return new SourceGenerationResult(syntaxTrees, tripwires: tripwires);

            OptimizationContext context = CEOptimizerRunner.GetContext();

            // Generators run first in the pipeline, so this is the single Clear() for the compile. The
            // optimizer runner deliberately does not clear, so the report window shows both sets of records.
            context.Clear();

            var runRecords = new List<SourceGeneratorRunRecord>();
            var result = new (SyntaxTree tree, string filePath)[syntaxTrees.Length];
            Array.Copy(syntaxTrees, result, syntaxTrees.Length);

            foreach (GeneratorEntry entry in Generators)
            {
                context.SetSemanticModelTrees(result.Select(item => item.tree));

                int transformed = 0;

                for (int i = 0; i < result.Length; i++)
                {
                    var (tree, filePath) = result[i];
                    context.CurrentFilePath = filePath;

                    SyntaxTree generatedTree;

                    try
                    {
                        generatedTree = entry.Transform.Optimize(tree, context);
                    }
                    catch (CodeGenerationException e) when (e.FilePath == null)
                    {
                        // Not a swallow -- rethrown immediately, only enriched. A generator throws before it
                        // knows which module it is in, and by this point in the chain its Location may point
                        // at a tree the compilation context has never seen, which prints "Unknown File(1,1)".
                        throw new CodeGenerationException(e.GeneratorId, e.Location, e.Message, e, filePath);
                    }

                    if (generatedTree == null)
                    {
                        // A null return would ride the result tuple all the way to Roslyn and blow up with no
                        // attribution. Fail here, where the generator and the file are both still known.
                        throw new CodeGenerationException(
                            entry.GeneratorId,
                            tree.GetRoot().GetLocation(),
                            $"'{entry.Transform.OptimizerName}' returned no syntax tree for '{filePath}'. A " +
                            "generator must return the original tree when it has nothing to emit, never null.",
                            null,
                            filePath);
                    }

                    if (!ReferenceEquals(generatedTree, tree))
                        transformed++;

                    result[i] = (generatedTree, filePath);
                }

                runRecords.Add(new SourceGeneratorRunRecord(entry.GeneratorId, SourceGeneratorRunStatus.Ran, result.Length, transformed));
            }

            // Persistence auto-registration operates on the whole tree set (it appends a generated tree), so
            // it does not fit the per-tree shape above. It runs last so it sees every other rewrite.
            var beforePersistence = result;
            (SyntaxTree tree, string filePath)[] persistenceTrees;

            try
            {
                persistenceTrees = CEPersistenceAutoRegistrationOptimizer.Generate(result);
            }
            catch (CodeGenerationException e) when (e.FilePath == null)
            {
                // Same enrichment as the per-tree loop above, and needed for the same reason: by this point
                // every tree has been rewritten, so the exception's Location belongs to a tree the
                // compilation context has never seen and the error prints "Unknown File(1,1)". This
                // generator has no "current file", so the path is recovered from the location's own tree.
                throw new CodeGenerationException(
                    e.GeneratorId, e.Location, e.Message, e, FindFilePath(result, e.Location));
            }

            result = persistenceTrees ?? beforePersistence;

            // Only a real run counts as Ran, and only an appended tree counts as a transform. Both halves
            // matter to the CEGEN004 tripwire: a null return would silently fall back to the untouched trees,
            // and a run that inspected every file and generated no converter is exactly the shape in which a
            // project declaring [PlayerData] ships with player data that never persists.
            runRecords.Add(new SourceGeneratorRunRecord(
                PersistenceGeneratorId,
                persistenceTrees != null ? SourceGeneratorRunStatus.Ran : SourceGeneratorRunStatus.Faulted,
                beforePersistence.Length,
                result.Length != beforePersistence.Length ? 1 : 0));

            return new SourceGenerationResult(result, runRecords: runRecords, tripwires: tripwires);
        }

        /// <summary>
        /// The file a whole-tree-set generator's location belongs to, or null when it belongs to none of
        /// them (a location on the generated tree, or no location at all). Null leaves the exception exactly
        /// as thrown, so this can only improve the reported file, never replace a good one with a wrong one.
        /// </summary>
        private static string FindFilePath((SyntaxTree tree, string filePath)[] trees, Location location)
        {
            SyntaxTree sourceTree = location?.SourceTree;
            if (sourceTree == null)
                return null;

            foreach (var (tree, filePath) in trees)
            {
                if (ReferenceEquals(tree, sourceTree))
                    return filePath;
            }

            return null;
        }

        /// <summary>Generator metadata for the CE optimization report window.</summary>
        internal static (string Id, string Name, string Description, int Order)[] GetGeneratorInfo()
        {
            var info = new (string, string, string, int)[Generators.Length + 1];

            for (int i = 0; i < Generators.Length; i++)
            {
                GeneratorEntry entry = Generators[i];
                info[i] = (entry.GeneratorId, entry.Transform.OptimizerName, entry.Transform.Description, entry.Order);
            }

            info[Generators.Length] = (
                PersistenceGeneratorId,
                "Persistence Auto-Registration",
                "Generates CEPersistence converters and non-generic overloads from [PlayerData] models.",
                30);

            return info;
        }
    }
}
