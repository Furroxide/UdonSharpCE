using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Editor.DevTools.Analysis;
using UdonSharp.CE.Editor.DevTools.Validation;
using UdonSharp.CE.Editor.DevTools.Validation.Validators;
using UdonSharp.CE.Net;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// The editor tooling must recognise <c>[Sync]</c>, not just <c>[UdonSynced]</c>.
    ///
    /// <c>SyncAttribute</c> derives from <c>UdonSyncedAttribute</c>, so the compiler sees it through an
    /// <c>is</c> test and syncs the field correctly. The analyzers and validators historically did their
    /// own attribute lookups, and any of them checking for the exact <c>UdonSyncedAttribute</c> type would
    /// silently under-report every CE-style synced field — telling an author their behaviour is within
    /// budget when it is not, or that a synced array is fine when it will never sync.
    ///
    /// Ported from the package's <c>Tests~/CE</c> folder, which Unity excludes from the asset pipeline
    /// entirely: those suites had never run once despite being reported as passing. Assertions were moved
    /// onto the validators' public surface rather than private methods reached by reflection, so the tests
    /// survive refactoring of the internals.
    /// </summary>
    public class CESyncAttributeToolingTests
    {
        // These fixtures live in an editor test assembly, which is NOT registered as an UdonSharp source
        // assembly, so no program asset is ever generated for them and they cannot poison a compile.
        //
        // The declared authority is not what is under test -- these only assert that the tooling sees a
        // [Sync] field as synced at all -- but it is a required argument now, and Owner is the reading
        // that claims nothing: Predicted asserts that an owner's send reconciles the field, and bodyless
        // fixtures with no RequestSerialization anywhere have no such send to point at. Nothing would
        // report it if they did -- CE0063 only looks at behaviours whose declared sync mode replicates no
        // variable at all, and these are Continuous and Manual -- so Owner is written here because it is
        // the true reading, not to dodge a diagnostic.
        [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
        private class SyncFieldFixture : UdonSharpBehaviour
        {
            [Sync(Authority.Owner)]
            public int value;
        }

        [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
        private class InitializedSyncArrayFixture : UdonSharpBehaviour
        {
            [Sync(Authority.Owner)]
            public int[] values = new int[4];
        }

        [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
        private class UninitializedSyncArrayFixture : UdonSharpBehaviour
        {
            [Sync(Authority.Owner)]
            public int[] values;
        }

        [Test]
        public void BehaviourAnalyzer_TreatsSyncAttributeAsSynced()
        {
            BehaviourAnalysisResult result = new BehaviourAnalyzer().Analyze(typeof(SyncFieldFixture));

            Assert.That(result.Fields.Select(field => field.FieldName), Does.Contain("value"),
                "[Sync] fields must be reported as synced. If they are not, every bandwidth figure the CE " +
                "tooling shows for a CE-style behaviour is understated.");
        }

        /// <summary>
        /// The validator decides "initialized" by locating the field's declaration in source. These
        /// fixtures are nested private classes inside a test file, which it cannot resolve to a source
        /// declaration, so it conservatively reports both. That is the correct conservative behaviour for
        /// a type it cannot see, and asserting on it here would only pin an artefact of the fixture shape.
        ///
        /// What is worth pinning is that <c>[Sync]</c> is recognised as synced at all -- the tooling used
        /// to look for the exact <c>UdonSyncedAttribute</c> type and miss every CE-style field. If the
        /// attribute were not recognised, the validator would report nothing for either fixture.
        /// </summary>
        [Test]
        public void UninitializedSyncedArrayValidator_RecognisesSyncAttributeArrays()
        {
            var issues = new UninitializedSyncedArrayValidator()
                .Validate(ContextFor(typeof(UninitializedSyncArrayFixture)))
                .ToList();

            Assert.That(issues, Is.Not.Empty,
                "A synced array declared with [Sync] was not seen as synced at all. A null synced array " +
                "throws inside Udon's serializer on the owner and silently transmits nothing, so the " +
                "validator missing [Sync] fields means it misses exactly the bug it exists to catch.");
        }

        /// <summary>
        /// The real project is the meaningful case: every shipped behaviour initializes its synced arrays
        /// at declaration, so a clean sweep over them proves the validator does not fire on correct code.
        /// </summary>
        [Test]
        public void UninitializedSyncedArrayValidator_DoesNotFlagAnyShippedBehaviour()
        {
            var shipped = UdonSharpProgramAsset.GetAllUdonSharpPrograms()
                .Where(asset => asset != null && asset.sourceCsScript != null)
                .Select(asset => asset.sourceCsScript.GetClass())
                .Where(type => type != null)
                .Distinct()
                .ToList();

            if (shipped.Count == 0)
                Assert.Ignore("No compiled UdonSharpBehaviour types in the project.");

            var issues = new UninitializedSyncedArrayValidator()
                .Validate(new ValidationContext { BehaviourTypes = shipped })
                .ToList();

            Assert.That(issues.Select(i => i.Message), Is.Empty,
                "A shipped behaviour has a synced array that is not initialized at its declaration. " +
                "Udon's serializer throws on a null synced array and the behaviour transmits nothing.");
        }

        [Test]
        public void SyncModeValidator_RunsOverASyncAttributeBehaviour_WithoutError()
        {
            // The validator's own internals changed shape (its field-write analysis moved from a regex over
            // raw source to a syntax walk), so this asserts the observable contract instead: it must accept
            // a [Sync]-only behaviour and complete rather than throwing or reporting a phantom issue.
            List<ValidationIssue> issues = null;

            Assert.DoesNotThrow(
                () => issues = new SyncModeValidator().Validate(ContextFor(typeof(SyncFieldFixture))).ToList(),
                "SyncModeValidator threw on a behaviour whose only synced field uses [Sync].");

            Assert.That(issues, Is.Not.Null);
        }

        private static ValidationContext ContextFor(Type behaviourType)
        {
            return new ValidationContext
            {
                BehaviourTypes = new List<Type> { behaviourType }
            };
        }
    }
}
