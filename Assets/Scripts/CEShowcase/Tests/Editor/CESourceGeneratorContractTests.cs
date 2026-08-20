using System;
using System.Linq;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.Compiler;
using UnityEngine.TestTools;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Guardrails for the CE source-generation contract.
    ///
    /// These exist because of a specific, shipped failure: CE's correctness-critical transforms
    /// (SyncOnJoin glue, quantization, async state machines, persistence converters) were registered in
    /// the same list as optional perf optimizers, and the compiler swallowed any failure from that list
    /// into a single <c>Debug.LogWarning</c> before continuing with the untransformed trees. The compile
    /// then *succeeded*, produced Udon programs missing all their generated glue, and marked every script
    /// up to date -- so nothing synced in game and no test or console message said why.
    /// </summary>
    public class CESourceGeneratorContractTests
    {
        private const string CEProviderId = "UdonSharp.CE";

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;

            // Leave the project in a compiled, error-free state regardless of what a test did.
            CompileSync();
        }

        [Test]
        public void ThrowingGenerator_FailsTheCompile_InsteadOfWarningAndContinuing()
        {
            // Errors are expected here; we assert on the diagnostics rather than the console.
            LogAssert.ignoreFailingMessages = true;

            using (SourceGeneratorRegistry.OverrideForTests(CEProviderId, 0, _ =>
                       throw new InvalidOperationException("harness-induced generator failure")))
            {
                CompileSync();

                string[] errors = ErrorMessages();

                Assert.That(errors, Is.Not.Empty,
                    "A source generator that throws must fail the compile. If this is empty the failure was " +
                    "downgraded to a warning again, which means a broken generator can ship a program with no glue.");

                Assert.That(string.Join("\n", errors), Does.Contain("harness-induced generator failure"),
                    "The generator's own exception text must reach the compile diagnostics so the cause is visible.");
            }
        }

        [Test]
        public void SuppressedGenerator_TripsTheTripwire()
        {
            LogAssert.ignoreFailingMessages = true;

            // Simulate the real failure shape: the provider is present and returns successfully, but did
            // not actually run any generator. A presence check alone cannot see this.
            using (SourceGeneratorRegistry.OverrideForTests(CEProviderId, 0,
                       trees => new SourceGenerationResult(
                           trees,
                           tripwires: new[] { new SourceGeneratorTripwire("CEGEN001", "USCE0001", "SyncOnJoin") })))
            {
                CompileSync();

                string joined = string.Join("\n", ErrorMessages());

                Assert.That(joined, Does.Contain("USCE0001"),
                    "A generator that never ran, while sources still use its attribute, must be a compile error.");
                Assert.That(joined, Does.Contain("CEGEN001"));
            }
        }

        [Test]
        public void CEGeneratorProvider_IsRegistered()
        {
            // Registration is forced by the compiler running the provider's static constructor, so it no
            // longer depends on which [InitializeOnLoad] Unity happened to run first.
            CompileSync();

            Assert.That(SourceGeneratorRegistry.RegisteredProviderIds, Does.Contain(CEProviderId),
                "If the CE provider is not registered, none of CE's correctness-critical codegen runs.");
        }

        [Test]
        public void RegisteredTransformIds_AreUnique()
        {
            var generatorIds = UdonSharp.CE.Editor.CodeGen.CEGeneratorRunner.GetGeneratorInfo()
                .Select(g => g.Id);
            var optimizerIds = UdonSharp.CE.Editor.Optimizers.CEOptimizerRunner.GetOptimizerInfo()
                .Select(o => o.Id);

            string[] allIds = generatorIds.Concat(optimizerIds).ToArray();

            // CEOPT009 was once claimed by both the SyncOnJoin glue emitter and common subexpression
            // elimination, and CEOPT010 by both the quantize emitter and loop-invariant code motion. Since
            // the "contract-critical" escalation set was keyed by id, a failure in either perf pass was
            // escalated as though a sync generator had failed.
            CollectionAssert.AllItemsAreUnique(allIds,
                "Transform ids key the optimization report and any per-pass toggle; duplicates silently " +
                "conflate a correctness generator with a perf optimizer.");
        }

        [Test]
        public void CorrectnessGenerators_AreNotGatedByTheOptimizationToggle()
        {
            // Generators live in their own registry precisely so that turning optimizations off cannot
            // turn sync codegen off.
            var optimizerIds = UdonSharp.CE.Editor.Optimizers.CEOptimizerRunner.GetOptimizerInfo()
                .Select(o => o.Id)
                .ToArray();

            Assert.That(optimizerIds, Has.No.Member("CEGEN001"));
            Assert.That(optimizerIds, Has.No.Member("CEGEN002"));
            Assert.That(optimizerIds, Has.No.Member("CEASYNC001"));
        }

        private static void CompileSync()
        {
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
        }

        private static string[] ErrorMessages()
        {
            var diagnostics = UdonSharpEditorCache.Instance.LastCompileDiagnostics;

            if (diagnostics == null)
                return Array.Empty<string>();

            return diagnostics
                .Where(d => d.severity == DiagnosticSeverity.Error)
                .Select(d => d.message)
                .ToArray();
        }
    }
}
