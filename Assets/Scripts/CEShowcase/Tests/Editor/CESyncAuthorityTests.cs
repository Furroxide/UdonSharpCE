using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using CESyncRuntimeTests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Editor.Analyzers;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using UdonSharp.Compiler.Udon;
using CompilerSeverity = UdonSharp.Compiler.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Covers the declared-authority rework: <c>[Sync(Authority...)]</c>, the two-tier CE0014, and CE0063.
    ///
    /// The change exists because CE0014 assumed every synced field is authoritative state that only the
    /// owner may write. That is false for predict-and-reconcile netcode, where every client simulates the
    /// value locally and the owner's packet corrects it -- CE's own NetVehicle is exactly that shape and
    /// produced five warnings describing its design as a bug. The fix declares the role at the field: an
    /// Owner field still gets the strict ownership check, and a Predicted field is bought out of it.
    ///
    /// CE0063 is what the Predicted field gets instead, and it is deliberately much smaller than that
    /// framing suggests. It reports one thing -- a prediction on a behaviour whose declared sync mode
    /// transmits no variable to anyone, ever -- and that report needs no guess about what other code does.
    /// Everything else is left alone, because the first draft asked whether a Manual behaviour publishes
    /// and could not answer it soundly: it read a base-class guard, an externally driven publish, a
    /// quantized field and a plain ungated <c>RequestSerialization</c> as divergences, all four wrongly.
    /// A check written to remove five false positives may not ship four more, so those four shapes are
    /// pinned below as regression tests, along with the true positive CE0063 no longer catches, so that
    /// the cost of the scope is written down rather than rediscovered.
    ///
    /// That makes two opposite regressions possible, and both are silent. Declaring Predicted too widely
    /// switches CE0014 off for real desyncs; disturbing the shared analysis while adding the tier switch
    /// turns it off for plain <c>[UdonSynced]</c> fields, which is the majority of the world. Every
    /// assertion below pins one of those directions, and the bare-<c>[UdonSynced]</c> pair is the
    /// load-bearing one -- until this file there was no test of CE0014 anywhere, so the inference path it
    /// must not disturb had no safety net at all.
    ///
    /// HOW THE ANALYZERS ARE DRIVEN. CE analyzers are not Roslyn DiagnosticAnalyzers and cannot be run
    /// over a bare snippet: <c>ICompileTimeAnalyzer.Analyze</c> wants a bound <c>TypeSymbol</c>, a
    /// <c>BindContext</c> and a <c>CompilationContext</c>. The alternative -- fixture behaviours in a U#
    /// source assembly plus a whole-project <c>CompileSync</c> -- reports diagnostics for the entire
    /// project at once, so "no CE0014 here" would really assert that nothing anywhere in the project emits
    /// CE0014: unsatisfiable while NetVehicle exists, and meaningless afterwards.
    /// <see cref="BindFixture(string, string)"/> therefore runs the compiler's real Bind phase over one fixture in its own
    /// compilation, the same way UdonSharpCompilerV1 does, and hands the result to the real analyzer
    /// instance. Every diagnostic a test sees provably came from the fixture in front of it.
    ///
    /// THE BASE CHAIN. Several of the scans these analyzers share were later widened from the behaviour's
    /// own declarations to its user-written base chain, to fix a real CE0014 false positive on a derived
    /// type guarded by a base-declared ownership helper -- a shape both analyzers document as supported.
    /// That widening was justified by one invariant: it may only ever suppress a report, never create one,
    /// move one, or change what one says. The invariant does not hold by itself, so the section titled
    /// "Base chain" pins the three places it has to be enforced, and does it across two files per fixture
    /// so that "this report landed in the base's file" is assertable rather than merely arguable. Two of
    /// the consumers involved, CE0016 and CE0017, had no test anywhere in this repo before that section.
    /// </summary>
    public class CESyncAuthorityTests
    {
        private const string CE0014 = "CE0014";
        private const string CE0015 = "CE0015";
        private const string CE0016 = "CE0016";
        private const string CE0017 = "CE0017";
        private const string CE0063 = "CE0063";

        private const string OwnershipAnalyzerTypeName =
            "UdonSharp.CE.Editor.Analyzers.SyncedFieldOwnershipAnalyzer";

        private const string ReconciliationAnalyzerTypeName =
            "UdonSharp.CE.Editor.Analyzers.PredictedFieldReconciliationAnalyzer";

        // CE0015 is not part of the authority rework, but it reads the same base-chain scans CE0014 does,
        // so a base-declared helper that goes invisible breaks both at once and in the same way.
        private const string SerializationAnalyzerTypeName =
            "UdonSharp.CE.Editor.Analyzers.ManualSyncWithoutSerializationAnalyzer";

        // CE0016 and CE0017 are the other two consumers of the scans the base walk widened, and neither
        // had a test anywhere in this repo before the tests below -- this file was the only analyzer suite
        // and it drove CE0014, CE0015 and CE0063 only. CE0016 matters most of the four: it is the one
        // consumer whose behaviour the widening changed in *direction* rather than merely narrowing, and
        // an untested branch selector is exactly where a change like that lands unnoticed.
        private const string SyncedFieldsAnalyzerTypeName =
            "UdonSharp.CE.Editor.Analyzers.ManualSyncWithoutSyncedFieldsAnalyzer";

        private const string SerializationRateAnalyzerTypeName =
            "UdonSharp.CE.Editor.Analyzers.RequestSerializationRateAnalyzer";

        // Two-file fixtures are parsed under distinct paths so "which file did this report land in" is a
        // question the assertions can ask at all. It is not cosmetic: analyzers run once per *concrete*
        // root type (UdonSharpCompilerV1 skips abstract classes when it collects them), so a report
        // anchored into a shared base is one report per subclass, every one of them on a line the
        // reported type does not own and its author cannot fix.
        private const string BaseFixturePath = "BaseFixture.cs";
        private const string DerivedFixturePath = "DerivedFixture.cs";

        // ------------------------------------------------------------------------------------------
        // [Sync] surface
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Authority is required, and required *positionally*. The break is the point of the design: a
        /// reader of a synced field is never asked to infer from the surrounding code whether a write on a
        /// non-owner is a mistake or the design, because that inference is the thing that fails silently.
        /// </summary>
        [Test]
        public void SyncAttribute_WithNoAuthority_IsACompileError()
        {
            string[] errors = CompileErrors(@"
using UdonSharp;
using UdonSharp.CE.Net;

public class BareSyncSpelling : UdonSharpBehaviour
{
    [Sync] public int score;
}
");

            Assert.That(errors, Is.Not.Empty,
                "Bare [Sync] must not compile. If it does, authority picked up a default value, and a " +
                "defaulted authority is exactly the unstated assumption this change exists to remove.");

            Assert.That(string.Join("\n", errors), Does.Contain("CS7036").Or.Contain("CS1729"),
                "The failure must be the missing constructor argument rather than some unrelated error, " +
                "otherwise this fixture has stopped testing what it claims to test. Errors:\n" +
                string.Join("\n", errors));
        }

        /// <summary>
        /// The one call-site shape that cannot be rewritten mechanically. Authority and InterpolationMode
        /// are distinct enums on purpose: an old interpolation-first call is a type error at the call site
        /// instead of a silent bind to Authority.Owner, which is what a shared underlying type would have
        /// produced -- quietly relabelling every field the migration touched.
        /// </summary>
        [Test]
        public void SyncAttribute_WithInterpolationInThePositionalSlot_IsACompileError()
        {
            string[] errors = CompileErrors(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

public class InterpolationFirstSpelling : UdonSharpBehaviour
{
    [Sync(InterpolationMode.Linear)] public Vector3 position;
}
");

            Assert.That(string.Join("\n", errors), Does.Contain("CS1503"),
                "An interpolation-first [Sync] must fail as a conversion error. Anything else -- or " +
                "nothing -- means the two enums are no longer distinct types, and a pre-migration call " +
                "site can bind to an authority it never asked for.");
        }

        /// <summary>
        /// Every spelling the design contract promises keeps working. They are asserted together because
        /// the failure they guard is one-sided: a mistake in the constructor breaks the named forms while
        /// leaving the positional ones fine.
        /// </summary>
        [Test]
        public void SyncAttribute_AcceptsEverySupportedSpelling()
        {
            string[] errors = CompileErrors(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

public class SupportedSyncSpellings : UdonSharpBehaviour
{
    [Sync(Authority.Owner)] public int a;
    [Sync(Authority.Predicted)] public float b;
    [Sync(Authority.Owner, InterpolationMode.Smooth)] public Vector3 c;
    [Sync(Authority.Owner, Interpolation = InterpolationMode.Linear)] public Vector3 d;
    [Sync(Authority.Owner, Quantize = 0.01f)] public float e;
}
");

            Assert.That(errors, Is.Empty,
                "All five documented [Sync] spellings must compile. Errors:\n" + string.Join("\n", errors));
        }

        /// <summary>
        /// The constructor's shape, read off the type rather than off a call site.
        ///
        /// Owner must be 0 so a zero-initialized or defaulted Authority is the *strict* reading. The
        /// direction is what matters: a defaulted field is then checked as if it were authoritative, which
        /// reports a bug instead of hiding one.
        /// </summary>
        [Test]
        public void SyncAttribute_ExposesAuthorityAsItsOnlyRequiredParameter()
        {
            ConstructorInfo[] constructors = typeof(SyncAttribute).GetConstructors();

            Assert.That(constructors.Length, Is.EqualTo(1),
                "[Sync] must have exactly one public constructor. A second overload reintroduces a way to " +
                "declare a synced field without stating its authority.");

            ParameterInfo[] parameters = constructors[0].GetParameters();

            Assert.That(parameters.Length, Is.EqualTo(2),
                "[Sync] takes authority plus optional interpolation; everything else is a named property.");

            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(Authority)),
                "Authority must be the first constructor parameter.");

            Assert.That(parameters[0].IsOptional, Is.False,
                "Authority must not be optional. Wrong interpolation looks janky and is visible on screen; " +
                "wrong authority is a silent desync, so authority is the one thing that may not default.");

            Assert.That(parameters[1].ParameterType, Is.EqualTo(typeof(InterpolationMode)),
                "Interpolation stays on the constructor, demoted to the second slot.");

            Assert.That(parameters[1].IsOptional, Is.True,
                "Interpolation must remain optional so [Sync(Authority.Owner)] stays a legal call.");

            Assert.That(typeof(SyncAttribute).GetProperty("Authority").GetSetMethod(), Is.Null,
                "Authority must be read-only. A settable Authority lets a named argument disagree with the " +
                "positional one, and the analyzers read only the positional value.");

            Assert.That((int)Authority.Owner, Is.EqualTo(0),
                "Owner must be 0 so the defaulted value is the strict one.");

            Assert.That(default(Authority), Is.EqualTo(Authority.Owner),
                "default(Authority) must be Owner. Anything else makes an unset authority silently opt a " +
                "field out of CE0014.");
        }

        /// <summary>
        /// The compiler does not read attributes off the syntax; it reflectively *constructs* a real
        /// SyncAttribute with <c>Activator.CreateInstance</c> over Roslyn's constructor arguments, handing
        /// enum values across as boxed integers via <c>Enum.ToObject</c>. If that construction throws,
        /// <c>Symbol.TryCreateAttributeInstance</c> returns null and the attribute is dropped -- and
        /// because <c>FieldSymbol.IsSynced</c> is "does a UdonSyncedAttribute exist", the field then reads
        /// as not synced at all. The compile still succeeds and nothing replicates. This is the direct
        /// probe of that path.
        /// </summary>
        [Test]
        public void SyncAttribute_ConstructsThroughTheCompilersReflectionPath()
        {
            object instance = Activator.CreateInstance(
                typeof(SyncAttribute),
                new[]
                {
                    Enum.ToObject(typeof(Authority), (int)Authority.Predicted),
                    Enum.ToObject(typeof(InterpolationMode), (int)InterpolationMode.Smooth)
                });

            var attribute = (SyncAttribute)instance;

            Assert.That(attribute.Authority, Is.EqualTo(Authority.Predicted));

            Assert.That(attribute.NetworkSyncType, Is.EqualTo(UdonSyncMode.Smooth),
                "The constructor must drive the inherited NetworkSyncType, because that is the value the " +
                "upstream synced-variable pipeline actually serializes with.");
        }

        /// <summary>
        /// The named-interpolation form. The compiler applies named arguments after the constructor has
        /// run, so this only works while the constructor assigns through the <c>Interpolation</c> property.
        /// Assigning the backing field instead leaves Interpolation and NetworkSyncType disagreeing --
        /// coherent enough to compile, wrong on the wire, and it silently disarms CE0010's Manual-mode
        /// interpolation check, which reads the property while the pipeline reads NetworkSyncType.
        /// </summary>
        [Test]
        public void SyncAttribute_NamedInterpolationStillDrivesNetworkSyncType()
        {
            var attribute = new SyncAttribute(Authority.Owner)
            {
                Interpolation = InterpolationMode.Linear,
                Quantize = 0.01f
            };

            Assert.That(attribute.Interpolation, Is.EqualTo(InterpolationMode.Linear));

            Assert.That(attribute.NetworkSyncType, Is.EqualTo(UdonSyncMode.Linear),
                "Setting Interpolation must keep NetworkSyncType in step.");

            Assert.That(attribute.Quantize, Is.EqualTo(0.01f),
                "Quantize must still be settable now that the constructor takes a required argument.");
        }

        /// <summary>
        /// The end-to-end version of the two tests above: the same spellings, read back out of the *bound
        /// compiler symbols* rather than out of a hand-constructed attribute.
        ///
        /// This is the one that catches the worst outcome of the rework. If the editor's loaded
        /// UdonSharp.CE.Runtime and the Roslyn view of SyncAttribute ever disagree about the constructor --
        /// which is what a mid-migration domain reload looks like -- construction throws, the attribute is
        /// dropped, and every [Sync] field in the project stops being synced behind nothing louder than a
        /// USCE0006 warning. IsSynced is therefore asserted per field rather than assumed.
        /// </summary>
        [Test]
        public void SyncedFieldMetadata_SurvivesTheAuthorityRework()
        {
            BoundFixture fixture = BindFixture(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class SyncAuthorityMetadataFixture : UdonSharpBehaviour
{
    [Sync(Authority.Owner)] public int ownerPlain;
    [Sync(Authority.Predicted)] public float predictedPlain;
    [Sync(Authority.Owner, InterpolationMode.Smooth)] public Vector3 ownerSmooth;
    [Sync(Authority.Owner, Interpolation = InterpolationMode.Linear)] public Vector3 ownerLinearNamed;
    [Sync(Authority.Owner, Quantize = 0.01f)] public float ownerQuantized;
}
", "SyncAuthorityMetadataFixture");

            string[] fieldNames =
            {
                "ownerPlain", "predictedPlain", "ownerSmooth", "ownerLinearNamed", "ownerQuantized"
            };

            foreach (string fieldName in fieldNames)
            {
                Assert.That(Field(fixture, fieldName).IsSynced, Is.True,
                    $"'{fieldName}' stopped being a synced field. That is the silent-outage shape: the " +
                    "[Sync] attribute failed to construct, the compiler no longer sees a " +
                    "UdonSyncedAttribute on the field, and the program compiles clean while nothing " +
                    "replicates.");
            }

            Assert.That(AuthorityOf(fixture, "ownerPlain"), Is.EqualTo(Authority.Owner));

            Assert.That(AuthorityOf(fixture, "predictedPlain"), Is.EqualTo(Authority.Predicted),
                "Authority must round-trip through the compiler, not merely through a hand-built " +
                "instance. This is the value both CE0014 and CE0063 branch on.");

            Assert.That(Field(fixture, "ownerPlain").SyncMode, Is.EqualTo(UdonSyncMode.None));

            Assert.That(Field(fixture, "ownerSmooth").SyncMode, Is.EqualTo(UdonSyncMode.Smooth),
                "Positional interpolation must still reach NetworkSyncType now that it sits in the second " +
                "constructor slot.");

            Assert.That(Field(fixture, "ownerLinearNamed").SyncMode, Is.EqualTo(UdonSyncMode.Linear),
                "The named form is applied after the constructor runs, so it only works while the " +
                "constructor assigns interpolation through the property. This is the spelling's only " +
                "trip through the compiler's own attribute reconstruction.");

            Assert.That(SyncAttributeOf(fixture, "ownerQuantized").Quantize, Is.EqualTo(0.01f),
                "Quantize must survive the new positional argument, through the compiler's own attribute " +
                "reconstruction rather than through a direct constructor call.");
        }

        /// <summary>
        /// The named-interpolation spelling on a real, shipped, imported call site rather than on a string
        /// literal.
        ///
        /// Every other test of this spelling in the project builds its own throwaway compilation, so all
        /// of them would keep passing if the spelling stopped being used anywhere a normal build compiles.
        /// It nearly was: until EditorSyncNamedInterpolationIntegrationTestBehaviour the only occurrence
        /// outside a literal lived under Tests~/, a directory Unity does not import and therefore never
        /// compiles. A form nothing compiles is a form nothing protects.
        ///
        /// Reflecting the attribute back off the compiled field re-runs the path that matters: the CLR
        /// materializes an attribute by invoking the constructor with the positional arguments and then
        /// assigning the named ones through their property setters, which is the same two-step
        /// UdonSharp's Symbol.TryCreateAttributeInstance performs. NetworkSyncType is the assertion rather
        /// than Interpolation because NetworkSyncType is what the upstream synced-variable pipeline reads;
        /// the two disagreeing is the failure that compiles clean and syncs wrong.
        ///
        /// What this does not reach: UdonSharp only binds and emits a behaviour that a program asset
        /// points at, so the mirror's trip through the U# compiler proper -- ending in a field definition
        /// whose sync mode is Linear in the emitted program -- needs the program-asset harness that lives
        /// in UdonSharpCESyncPipelineTests, next to the identical assertion for the positional spelling.
        /// This test covers the C# side of the same fixture and fails if it is deleted or rewritten into
        /// the positional form.
        /// </summary>
        [Test]
        public void SyncAttribute_NamedInterpolationHasACompiledCallSite()
        {
            FieldInfo field = typeof(EditorSyncNamedInterpolationIntegrationTestBehaviour)
                .GetField("pos", BindingFlags.Instance | BindingFlags.Public);

            Assert.That(field, Is.Not.Null,
                "EditorSyncNamedInterpolationIntegrationTestBehaviour.pos is gone. It exists to be the " +
                "one place in an imported assembly that spells [Sync] with a named interpolation; without " +
                "it that spelling is only ever exercised by string literals in tests.");

            var attribute = field.GetCustomAttribute<SyncAttribute>();

            Assert.That(attribute, Is.Not.Null, "The fixture field must carry CE's [Sync].");

            Assert.That(attribute.Authority, Is.EqualTo(Authority.Owner),
                "The positional argument must still be read when a named one follows it.");

            Assert.That(attribute.Interpolation, Is.EqualTo(InterpolationMode.Linear),
                "The named argument must reach the property it names.");

            Assert.That(attribute.NetworkSyncType, Is.EqualTo(UdonSyncMode.Linear),
                "Interpolation and NetworkSyncType must agree. If Interpolation reads Linear while " +
                "NetworkSyncType reads None, the field serializes uninterpolated and CE0010's Manual-mode " +
                "check -- which reads the property, not NetworkSyncType -- silently stops matching the " +
                "program the compiler emits.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0014 tier A -- declared Predicted
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The warning the whole change exists to remove. NetPhysicsWorld runs ApplyInput for every
        /// vehicle on every client, so a boost meter draining locally is an unguarded synced write by
        /// construction -- and it is the design, not the bug.
        /// </summary>
        [Test]
        public void CE0014_PredictedFieldWrittenWithoutAGuard_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(
                UnguardedWriteFixture("Predicted"), "UnguardedWriteFixture");

            AssertNoDiagnostic(diagnostics, CE0014,
                "A field declared [Sync(Authority.Predicted)] must be exempt from CE0014 entirely. The " +
                "unguarded write CE0014 would report *is* the prediction, and reporting it is what teaches " +
                "an author to skim past the warning that also covers real desyncs.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0014 tier B -- declared Owner
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The paired half of the test above: the same fixture with one word changed. Run together they
        /// prove the suppression comes from the declared authority rather than from anything incidental in
        /// the fixture's shape.
        /// </summary>
        [Test]
        public void CE0014_OwnerFieldWrittenWithoutAGuard_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(
                UnguardedWriteFixture("Owner"), "UnguardedWriteFixture");

            AnalyzerDiagnostic diagnostic = AssertSingleDiagnostic(diagnostics, CE0014,
                "Declaring Authority.Owner must keep the strict check. Losing it here would mean the " +
                "rework bought CE0063 by giving up the check that catches the silent desync.");

            Assert.That(diagnostic.Message, Does.Contain("_boostRemaining"),
                "The report must name the field, or an author with several synced fields cannot tell " +
                "which write is being discarded.");

            Assert.That(diagnostic.Message, Does.Contain("AdvancePrediction"),
                "The report must name the writing method as well as the field.");

            Assert.That(diagnostic.Severity, Is.EqualTo(CompilerSeverity.Warning),
                "CE0014 stays a Warning: a lost synced write is a bug, but not a reason to fail a build.");
        }

        /// <summary>
        /// An owner-declared field written behind a literal ownership check is the pattern CE0014 asks for,
        /// and a diagnostic that fires on its own recommended fix is worse than no diagnostic.
        /// </summary>
        [Test]
        public void CE0014_OwnerFieldWrittenBehindAGuard_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class OwnerGuardedFixture : UdonSharpBehaviour
{
    [Sync(Authority.Owner)] public int score;

    public override void Interact()
    {
        if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
            return;

        score++;
        RequestSerialization();
    }
}
", "OwnerGuardedFixture");

            AssertNoDiagnostic(diagnostics, CE0014,
                "The guarded write is the shape CE0014 tells authors to write. Reporting it would make the " +
                "fix indistinguishable from the bug.");
        }

        /// <summary>
        /// The same guarded write, with the guard's definition one class up.
        ///
        /// CE0014 has always accepted a member whose value comes from Networking.IsOwner as a guard, but
        /// it only ever looked for that member's declaration in the behaviour's own class declarations.
        /// Hoisting it into a shared entity base -- which is what a project does the second time it needs
        /// one, and what NetPhysicsEntity/NetVehicle already is -- made the guard invisible and every write
        /// behind it a report. That is the same disease as the five NetVehicle warnings this whole rework
        /// exists to clear: correct code, reported, on the pattern the package documents.
        ///
        /// The fix is in CENetworkAnalysis.CollectOwnershipDerivedNames, which now walks the base chain
        /// (stopping before UdonSharpBehaviour so its own callback declarations cannot leak in), so this
        /// covers every check built on that scan rather than CE0014 alone. The fixture is shared with
        /// <see cref="CE0063_ManualPredictedGuardedByABaseClassProperty_IsNotReported"/>: one behaviour,
        /// each analyzer asked what it makes of it.
        /// </summary>
        [Test]
        public void CE0014_OwnerFieldGuardedByABaseClassProperty_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(
                BaseClassGuardFixture, "BaseClassGuardFixture");

            AssertNoDiagnostic(diagnostics, CE0014,
                "'if (!IsLocalAuthority) return;' is an ownership guard wherever IsLocalAuthority is " +
                "declared. If this reports, the ownership-name scan stopped at the derived type and every " +
                "project with a shared entity base now gets CE0014 on its correct writes.");
        }

        /// <summary>
        /// The other base-declared guard shape, and the one the property fixture above does not reach.
        ///
        /// <c>protected void EnsureOwnership()</c> takes ownership instead of reporting it, so it returns
        /// nothing and CollectOwnershipDerivedNames cannot see it: that scan asks whether every value a
        /// member returns comes from an ownership call, and a void method returns none. The shape is
        /// recognised through the call graph instead -- GuardsOwnPath walks the writer's callees and reads
        /// the Networking.SetOwner in the helper's body -- which is exactly why hoisting the helper into a
        /// base is what breaks it: BuildMethodGraph collected only the behaviour's own class declarations,
        /// so the helper was not a node, the call was not an edge, and the write behind it had no guard on
        /// any path the walker could reach.
        ///
        /// This is the same defect as
        /// <see cref="CE0014_OwnerFieldGuardedByABaseClassProperty_IsNotReported"/> arriving through a
        /// different scan, and it is worth its own fixture because fixing one does not fix the other: the
        /// property form is found by the ownership-name walk, the void form by the method graph. The
        /// analyzer's own type comment names "the EnsureOwnership() shape" as a supported guard, so a
        /// subclass getting CE0014 on it is the package contradicting its documentation.
        /// </summary>
        [Test]
        public void CE0014_OwnerFieldGuardedByABaseClassVoidHelper_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(
                BaseClassVoidGuardFixture, "BaseClassVoidGuardFixture");

            AssertNoDiagnostic(diagnostics, CE0014,
                "'EnsureOwnership();' before the write is the take-ownership resolution CE0014's own fix " +
                "text recommends, and it does not stop being one because it lives on the base class. If " +
                "this reports, the call graph stopped at the derived type and every shared entity base " +
                "with an ownership helper now warns on the code the analyzer told its author to write.");
        }

        /// <summary>
        /// CE0015's half of the same base walk: the publish helper is declared on the base.
        ///
        /// CE0015 reports a Manual behaviour that writes a synced field and never calls
        /// RequestSerialization, and it answers the second half by scanning the behaviour's own class
        /// declarations for the call. A shared entity base with 'protected void Publish() {
        /// RequestSerialization(); }' -- the same refactor that produced the two fixtures above -- puts
        /// that call one class up, so every subclass looks like a behaviour that never serializes.
        ///
        /// Included here rather than left to a CE0015 suite because there is no such suite, and because
        /// the two checks fail together: they read the same declaration scans, and a base walk added to
        /// one and not the other leaves this shape reported by whichever half was missed. The write is
        /// owner-guarded so the fixture is correct code by every reading, not merely by CE0015's.
        /// </summary>
        [Test]
        public void CE0015_ManualBehaviourPublishedByABaseClassHelper_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunSerializationAnalyzer(
                BaseClassPublishFixture, "BaseClassPublishFixture");

            AssertNoDiagnostic(diagnostics, CE0015,
                "'Publish()' calls RequestSerialization on this behaviour, so the write does reach the " +
                "network. If this reports, the RequestSerialization scan stopped at the derived type and " +
                "CE0015 now tells every subclass of a publishing base that its synced state never leaves " +
                "the owner -- the exact claim the fixture disproves.");
        }

        // ------------------------------------------------------------------------------------------
        // Base chain -- the invariant, and the three places it does not hold by itself
        //
        // Pulling the base chain into these scans was justified by one claim: it can only ever SUPPRESS a
        // report, never create one, move one, or change what one says. The three tests above are the
        // suppressions it was added for. These are the places that claim needed enforcing rather than
        // asserting, and every one of them fails silently:
        //
        //   1. CE0014 goes quiet on a real unguarded write, because the guard it found lives in a base
        //      body an override replaced. A missed CE0014 is a permanently invisible desync -- strictly
        //      worse than the false positive the widening was for.
        //   2. CE0016 branches on the same answer CE0015 gates on, so widening it does not narrow CE0016,
        //      it changes which report comes out, how severe it is, and which file it lands in.
        //   3. CE0017 reads gates off the per-frame path; a path that reaches into base bodies is a path
        //      that can be "gated" by a clock the per-frame code never consults.
        //
        // Every fixture here compiles as two files so that "did this land in the base" is a question the
        // assertions can ask. Before these tests, CE0016 and CE0017 had no coverage anywhere in the repo.
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The guard sits in the base body the override <em>replaced</em>, so it never runs for this type
        /// -- and CE0014 must still report the write.
        ///
        /// This is the hole the base walk opened, and it is the one that costs the most. BuildMethodGraph
        /// keys its edges on the invoked *name*, so 'ApplyState()' in Interact matched two declarations at
        /// once: the override that really runs, and the base declaration it shadows. GuardsOwnPath walks
        /// callee bodies looking for any ownership check and does not care which body it found the check
        /// in, so the 'if (!Networking.IsOwner(...)) return;' standing in the dead base version vouched for
        /// an override whose entire body is '_score++;'. CE0014 said nothing about a write VRChat discards
        /// on every non-owner.
        ///
        /// The direction matters. A false positive is noisy, and noise gets read and argued with; a missed
        /// CE0014 has no symptom until somebody opens the world with two people in it, and then the symptom
        /// is "it works on my client". Trading the first for the second is not a trade the widening was
        /// allowed to make, so a base declaration may only survive while no nearer type in the chain claims
        /// its signature.
        ///
        /// The base guards with an early return rather than by wrapping its body, because that is both the
        /// shape an author writes and the shape where the mistake is total: 'return' there returns from the
        /// base method, which is not on this type's call path at all -- it would not gate the override even
        /// if the override called it.
        /// </summary>
        [Test]
        public void CE0014_OwnerWriteInAnOverrideShadowingAGuardedBase_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzerOnSubclass(
                OwnershipAnalyzerTypeName,
                ShadowedGuardBaseFixture,
                ShadowedGuardFixture,
                "ShadowedGuardFixture");

            AnalyzerDiagnostic diagnostic = AssertSingleDiagnostic(diagnostics, CE0014,
                "The only ownership check in this fixture is in a method body that does not run for " +
                "ShadowedGuardFixture: the override replaced it. Silence here means the call graph handed " +
                "a shadowed base body to the guard walk, and CE0014 is now suppressible by writing the " +
                "guard in a base version and then overriding it away -- which is what a subclass that " +
                "forgets 'base.ApplyState()' does by accident.");

            Assert.That(diagnostic.Message, Does.Contain("_score"),
                "The report must name the field whose write is being discarded.");

            Assert.That(diagnostic.Message, Does.Contain("ApplyState"),
                "The report must name the overriding method, which is the body that actually runs. " +
                "Naming the base method would send the author to fix a body that is not on the path.");

            AssertAnchoredIn(diagnostic, DerivedFixturePath,
                "The write is in the subclass, so the report belongs in the subclass's file. A CE0014 " +
                "pointing into the base would be telling the wrong author about the wrong line.");
        }

        /// <summary>
        /// CE0016 on a Manual subclass with no synced fields whose only RequestSerialization is inherited:
        /// it stays a Log, and it stays on the subclass.
        ///
        /// CE0016 is the one consumer of the RequestSerialization scan that *branches* on the answer rather
        /// than gating on it. CE0015 stops reporting the moment it finds a call, so widening its search can
        /// only remove reports. CE0016 reports either way and only chooses which report: a Warning at the
        /// call site ('serializes an empty payload') or a Log at the type ('never calls
        /// RequestSerialization()'). Feeding it a base-declared call therefore does not suppress anything
        /// -- it promotes Log to Warning, and moves the anchor from the subclass's own declaration into a
        /// file the subclass does not own, once per concrete subclass of that base.
        ///
        /// So severity and location are what this pins, and not the message. The Log's text is the weaker
        /// half of the pre-existing behaviour -- this behaviour does inherit a send, so "never calls
        /// RequestSerialization()" is imprecise about a base-declared one -- and that is a wording problem
        /// with a wording fix. Severity and anchor are not: they decide whether an author sees a warning on
        /// a line they cannot edit, N times over, for a base class that is doing nothing wrong.
        ///
        /// The mode is declared on the base on purpose. GetDeclaredSyncMode walks the chain because
        /// UdonSharpCompilerV1 does, so a subclass really is built Manual by its base -- which is what puts
        /// this shape in front of CE0016 in the first place.
        /// </summary>
        [Test]
        public void CE0016_ManualSubclassPublishingThroughABaseHelper_StaysALogOnTheSubclass()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzerOnSubclass(
                SyncedFieldsAnalyzerTypeName,
                ManualPublishingBaseFixture,
                ManualNoSyncedFieldsFixture,
                "ManualNoSyncedFieldsFixture");

            AnalyzerDiagnostic diagnostic = AssertSingleDiagnostic(diagnostics, CE0016,
                "CE0016 fires on Manual-with-no-synced-fields either way, so no report at all means the " +
                "mode never resolved -- either GetDeclaredSyncMode stopped at the derived type, or the " +
                "base's [UdonBehaviourSyncMode] is no longer being read through the chain.");

            Assert.That(diagnostic.Severity, Is.EqualTo(CompilerSeverity.Log),
                "CE0016's own reasoning for the Log is that Manual with nothing to send is dead weight " +
                "rather than a live bug. A base-declared send does not make this subclass's payload any " +
                "less empty, so it must not be what promotes the report to a Warning.");

            Assert.That(diagnostic.Message, Does.Contain("ManualNoSyncedFieldsFixture"),
                "The report must name the type that has to gain synced fields, which is the subclass -- " +
                "not the base that merely lent it a publish helper.");

            AssertAnchoredIn(diagnostic, DerivedFixturePath,
                "The report must anchor on the analyzed type's own declaration. Anchoring it at the " +
                "inherited RequestSerialization puts a report on a line in a shared base once per " +
                "concrete subclass -- and the base is not the thing that needs changing.");
        }

        /// <summary>
        /// The CE0017 control: an ungated per-frame RequestSerialization in the behaviour's own body is
        /// reported. Nothing in this repo asserted that before.
        ///
        /// It exists so the two base-chain tests below mean something. Both of them assert silence, and
        /// silence is also what a check that stopped running produces -- this is the fixture that proves
        /// the check runs at all. The body is shared verbatim with the base fixture below, so the pair
        /// differs in exactly one thing: which class declares it.
        /// </summary>
        [Test]
        public void CE0017_UngatedPerFrameSerializationInOwnBody_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunSerializationRateAnalyzer(
                OwnPerFrameSerializationFixture, "OwnPerFrameSerializationFixture");

            AnalyzerDiagnostic diagnostic = AssertSingleDiagnostic(diagnostics, CE0017,
                "An Update that serializes every frame with no gate is CE0017's worked example. If this " +
                "is silent, the analyzer is not running and neither of the two tests below is testing " +
                "anything.");

            Assert.That(diagnostic.Message, Does.Contain("Update"),
                "The report must name the per-frame method, because the fix is a gate in that method.");

            AssertAnchoredIn(diagnostic, DerivedFixturePath,
                "A single-file fixture's report belongs in that file. This also pins the anchor the two " +
                "base-chain tests below compare against.");
        }

        /// <summary>
        /// The same per-frame serialization, declared on the base: still not reported, and specifically not
        /// reported into the base's file.
        ///
        /// This is a deliberate miss, documented on BehaviourMethodGraph.Methods, and it is the pre-existing
        /// verdict rather than something the base walk introduced: base declarations enter the graph as
        /// reachable-but-unreportable nodes, so CE0017's scan over Methods finds no per-frame entry point
        /// here. The cost is a real bandwidth bug that nothing reports, because analyzers never run on the
        /// abstract base on its own.
        ///
        /// It is pinned rather than fixed because the obvious fix is worse than the miss. Reporting from
        /// inherited nodes means one warning per concrete subclass on the same line of a file none of them
        /// owns, and CE0017's own advice -- "gate the call in 'Update'" -- is not something the subclass
        /// author can act on. Changing this verdict has to be a decision about that trade, which is why the
        /// assertion is here to make it a visible one.
        /// </summary>
        [Test]
        public void CE0017_PerFrameSerializationDeclaredOnTheBase_IsNotReportedOnTheSubclass()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzerOnSubclass(
                SerializationRateAnalyzerTypeName,
                PerFrameSerializingBaseFixture,
                PerFrameSerializingSubclassFixture,
                "PerFrameSerializingSubclassFixture");

            AssertNoDiagnostic(diagnostics, CE0017,
                "CE0017 reports from the behaviour's own declarations only. If this starts reporting, the " +
                "per-frame scan is reading inherited nodes, and every subclass of a base with an Update " +
                "now carries its own copy of the same warning.");

            AssertNothingAnchoredIn(diagnostics, BaseFixturePath,
                "Nothing may point into the base file. Analyzers run per concrete root type, so a report " +
                "anchored there is emitted once per subclass and lands on an author who did not write the " +
                "subclass and cannot see why it fired.");
        }

        /// <summary>
        /// The subclass's own per-frame method calls a base publish helper: still not reported.
        ///
        /// This is the shape the base walk actually changed, because the callee walk now reaches the base's
        /// Publish() body where before it stopped at the subclass. CE0017 is unaffected only because it
        /// builds its per-frame path by filtering Methods -- the report-site list -- rather than by taking
        /// the reachability set straight from the graph. Swapping one for the other looks like a tidy-up and
        /// is not: it would anchor this report inside the shared base, once per subclass, exactly as above.
        ///
        /// So the verdict is unchanged and the miss is the same documented one. The distinction worth
        /// keeping is that here the inherited body <em>is</em> genuinely on the per-frame path -- which is
        /// what makes the tidy-up tempting.
        /// </summary>
        [Test]
        public void CE0017_SubclassUpdateCallingABasePublishHelper_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzerOnSubclass(
                SerializationRateAnalyzerTypeName,
                PublishHelperBaseFixture,
                PublishHelperSubclassFixture,
                "PublishHelperSubclassFixture");

            AssertNoDiagnostic(diagnostics, CE0017,
                "The RequestSerialization is in an inherited body, and CE0017 reports only from the " +
                "behaviour's own declarations. A report here would be anchored in the base file.");

            AssertNothingAnchoredIn(diagnostics, BaseFixturePath,
                "Same reason as the fixture above: a per-frame report on a shared base repeats once per " +
                "concrete subclass, on a line the reported type does not own.");
        }

        /// <summary>
        /// The direction the widening must never take: a clock in an inherited body is not a rate gate for
        /// the subclass's own ungated call.
        ///
        /// CE0017 accepts a time check in another method on the per-frame path, because
        /// 'if (Time.time - _last &lt; Interval) return; Sync();' puts the gate in the caller and the call in
        /// the callee. That rule is only sound while the per-frame path is the code that actually runs
        /// before the call. Widen the path to inherited bodies and any base helper that happens to read a
        /// clock -- a poll, an animation timer, an interpolation step -- starts vouching for a subclass
        /// Update that serializes unconditionally.
        ///
        /// The fixture is built to be maximally tempting: PollClock() has a real, correct, dominating time
        /// gate, and the subclass's Update really does call it on the way to RequestSerialization. It gates
        /// nothing -- PollClock's early return returns from PollClock -- and CE0017 must keep saying so.
        /// This one passes today; it is here so that a future "the base is on the path, so read its gates
        /// too" cannot switch CE0017 off without a failing test.
        /// </summary>
        [Test]
        public void CE0017_SubclassUpdateWithAClockOnlyInTheBase_IsStillReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzerOnSubclass(
                SerializationRateAnalyzerTypeName,
                ClockHelperBaseFixture,
                ClockHelperSubclassFixture,
                "ClockHelperSubclassFixture");

            AnalyzerDiagnostic diagnostic = AssertSingleDiagnostic(diagnostics, CE0017,
                "PollClock()'s gate returns from PollClock. Nothing stands between the top of Update and " +
                "the RequestSerialization below it, so this behaviour serializes every frame. Silence here " +
                "means a base-declared clock is being read as a gate on code it does not gate -- a false " +
                "negative on the bug CE0017 exists for, and one whose only symptom is other people's " +
                "objects going laggy.");

            AssertAnchoredIn(diagnostic, DerivedFixturePath,
                "The ungated call is the subclass's own, so the report belongs on its line.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0014 tier C -- bare [UdonSynced], the regression that matters most
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// [UdonSynced] is upstream API and its behaviour may not move. Everything written before this
        /// change uses it, and the inference path that judges it had no test at all until this file -- so a
        /// tier switch that accidentally routed undeclared fields into the Predicted branch would have
        /// turned CE0014 off for the entire existing world, silently.
        /// </summary>
        [Test]
        public void CE0014_BareUdonSyncedWrittenWithoutAGuard_IsStillReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(@"
using UdonSharp;

public class BareSyncedUnguardedFixture : UdonSharpBehaviour
{
    [UdonSynced] public int score;

    public override void Interact()
    {
        score++;
    }
}
", "BareSyncedUnguardedFixture");

            AnalyzerDiagnostic diagnostic = AssertSingleDiagnostic(diagnostics, CE0014,
                "A field carrying only [UdonSynced] must keep running the original inference path. An " +
                "undeclared authority is not a claim of prediction -- it is no claim at all, and the strict " +
                "reading is the one that reports the desync instead of permitting it.");

            Assert.That(diagnostic.Message, Does.Contain("score"));
        }

        /// <summary>
        /// The documented one-level-of-indirection shape: a flag refreshed once from Networking.IsOwner and
        /// then tested. This is what authors actually write, and it is the part of the inference path most
        /// likely to be lost when the shared analysis is touched. Losing it does not silence CE0014 -- it
        /// makes CE0014 fire on correct code, which is how a warning gets trained out of its reader.
        /// </summary>
        [Test]
        public void CE0014_BareUdonSyncedBehindACachedOwnerFlag_IsStillNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(@"
using UdonSharp;
using VRC.SDKBase;

public class BareSyncedFlagGuardedFixture : UdonSharpBehaviour
{
    [UdonSynced] public int score;

    private bool _isOwner;

    private void Start()
    {
        _isOwner = Networking.IsOwner(Networking.LocalPlayer, gameObject);
    }

    public override void Interact()
    {
        if (!_isOwner)
            return;

        score++;
    }
}
", "BareSyncedFlagGuardedFixture");

            AssertNoDiagnostic(diagnostics, CE0014,
                "'_isOwner = Networking.IsOwner(...)' followed by 'if (!_isOwner) return;' is the " +
                "cached-flag guard the analyzer documents as supported. If this fires, the " +
                "ownership-derived-name scan was disturbed and CE0014 now reports correct code.");
        }

        /// <summary>
        /// The fix suggestion is tier-aware, and deliberately asymmetric.
        ///
        /// On an undeclared field the author has two honest resolutions and the diagnostic has to name
        /// both, because "guard the write" is wrong advice when the write is deliberate prediction -- that
        /// advice is what produced NetVehicle's five false positives. On a field already declared Owner the
        /// author has asserted that only the owner writes it, so offering Predicted there would be advising
        /// them to silence a true positive by rewriting the field's meaning.
        /// </summary>
        [Test]
        public void CE0014_OffersPredictedOnlyWhereAuthorityWasNeverDeclared()
        {
            AnalyzerDiagnostic undeclared = AssertSingleDiagnostic(
                RunOwnershipAnalyzer(@"
using UdonSharp;

public class BareSyncedSuggestionFixture : UdonSharpBehaviour
{
    [UdonSynced] public int score;

    public override void Interact()
    {
        score++;
    }
}
", "BareSyncedSuggestionFixture"),
                CE0014,
                "The fixture must still report, otherwise there is no suggestion to inspect.");

            Assert.That(undeclared.FixSuggestion, Does.Contain("Networking.IsOwner"),
                "The original resolution -- guard the write -- must survive.");

            Assert.That(undeclared.FixSuggestion, Does.Contain("Authority.Predicted"),
                "On an undeclared field the diagnostic must also name the second resolution. Without it " +
                "the only advice on offer is wrong for every predict-and-reconcile field.");

            AnalyzerDiagnostic declaredOwner = AssertSingleDiagnostic(
                RunOwnershipAnalyzer(UnguardedWriteFixture("Owner"), "UnguardedWriteFixture"),
                CE0014,
                "The fixture must still report, otherwise there is no suggestion to inspect.");

            Assert.That(declaredOwner.FixSuggestion, Does.Not.Contain("Authority.Predicted"),
                "The author already declared this field owner-authoritative. Suggesting Predicted here " +
                "recommends silencing a true positive by changing what the field means, which is the one " +
                "resolution that loses information.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0063 -- predicted state that can never converge
        //
        // CE0063 asks one question and only one: does this behaviour's declared sync mode transmit a
        // variable to anybody, ever? None and NoVariableSync answer no, and that answer is total -- no
        // other behaviour, no manager, no inspector wiring can rescue a prediction on a program that
        // replicates nothing. Every other mode leaves without a report.
        //
        // The first draft of the check went further and asked, on a Manual behaviour, whether an
        // ownership-gated RequestSerialization existed. That question has no sound answer from inside one
        // type, and four separate shapes of correct code came back as defects. Three are pinned below as
        // named regression tests; the fourth is the fixture immediately following, which the deleted test
        // asserted must report and which now asserts the opposite. CE0063 exists because CE0014 reported
        // five pieces of correct code; a replacement that invents its own false positives has moved the
        // noise rather than removed it.
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The inverted test. This fixture is what the removed Manual arm reported, and reporting it was
        /// the bug: Udon runs Update on every client, the owner included, so this behaviour serializes
        /// from the owner every single frame. The prediction converges -- aggressively. An ownership gate
        /// would narrow the send to the owner alone, which is still "the owner sends", so its absence was
        /// never evidence of anything.
        ///
        /// The shape is a real defect, just not this one: it is a per-frame Manual serialization, which is
        /// CE0017's (RequestSerializationRateAnalyzer) subject and its documented worked example. Reading
        /// a bandwidth bug as a divergence bug is how a diagnostic loses its meaning.
        /// </summary>
        [Test]
        public void CE0063_ManualPredictedWithUngatedSerialization_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class PredictedWithoutOwnerGateFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted)] private float _boostRemaining;

    private void Update()
    {
        _boostRemaining -= Time.deltaTime;
        RequestSerialization();
    }
}
", "PredictedWithoutOwnerGateFixture");

            AssertNoDiagnostic(diagnostics, CE0063,
                "An ungated RequestSerialization runs on every client, and the owner is one of them, so " +
                "this behaviour publishes the owner's value every frame. CE0063 must not report a " +
                "reconciliation as a failure to reconcile. The genuine defect here -- serializing every " +
                "frame -- belongs to CE0017.");
        }

        /// <summary>
        /// NetVehicle's shape reduced to the members the two analyzers read: Manual mode, an owner-gated
        /// FixedUpdate that calls RequestSerialization, an OnPreSerialization override, and predicted
        /// fields advanced from a method every client runs. This must pass clean.
        ///
        /// It is the most important CE0063 case there is. NetVehicle is the reason the rework exists, and a
        /// CE0063 that fires on it would have replaced five false positives on that file with a new one.
        ///
        /// Worth being precise about why it passes, because the honest reason is weaker than it looks and
        /// the flattering one invites someone to "improve" the analyzer back into the old bug. It passes
        /// at the mode gate: Manual replicates, so CE0063 stops there. It does *not* pass because the
        /// analyzer inspected the owner-gated FixedUpdate and approved of it -- nothing reads that path
        /// any more. The gate is kept in the fixture anyway, because NetVehicle really has one and this
        /// fixture's job is to stay a faithful reduction of it.
        /// </summary>
        [Test]
        public void CE0063_ManualPredictedWithOwnerGatedSerialization_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(
                NetVehicleShapedFixture("Predicted"), "NetVehicleShapedFixture");

            AssertNoDiagnostic(diagnostics, CE0063,
                "Manual sync replicates, so a predicted field on this behaviour has an owner value to " +
                "converge on and there is nothing CE0063 can prove against it. Firing here would report " +
                "the package's own flagship netcode as broken.");
        }

        /// <summary>
        /// The same NetVehicle shape run through CE0014. Nothing may fire: the predicted fields are exempt
        /// by declaration, the owner-authoritative one is written only from OnPreSerialization -- which
        /// runs on the serializing owner and has always been exempt -- and the driver-id writes are
        /// predicted too.
        /// </summary>
        [Test]
        public void CE0014_NetVehicleShape_ReportsNothing()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(
                NetVehicleShapedFixture("Predicted"), "NetVehicleShapedFixture");

            AssertNoDiagnostic(diagnostics, CE0014,
                "This is the shape that produced the five warnings the rework exists to clear. If it still " +
                "reports, the declared authority is not reaching the tier switch.");
        }

        /// <summary>
        /// The same shape with the driver id declared Owner instead: CE0014 is still silent, and that is
        /// the finding rather than a reassurance.
        ///
        /// _ownerPlayerId being Predicted is load bearing in two documents -- Authority.cs uses it as the
        /// worked example of "Predicted records who may write, not where the value came from", and
        /// agents/ce-runtime.md tells agents not to change it -- and this test is what establishes that no
        /// analyzer backs either of them up. LatchDriverId writes the field with no guard of its own, but
        /// each of its callers reaches PublishDriverId's ownership check, so EveryCallerGuards is satisfied
        /// and the strict tier produces nothing to report. Flipping the declaration is therefore invisible
        /// to CE0014 in both directions: it neither warns that the flip was wrong nor confirms that it was
        /// right.
        ///
        /// So the pin in <see cref="NetVehicle_DeclaresAuthorityOnEverySyncedField"/> is not belt and
        /// braces, it is the only brace there is. This assertion records why, and it is deliberately an
        /// assertion of silence: if a future CE0014 can prove this write is lost, this test failing is the
        /// right way to find out, and inverting it then is a decision rather than an accident.
        ///
        /// It is also the reason the declaration is where the answer lives. The graph can only see that
        /// every path to the write passes an ownership check; it cannot see that every client is *meant*
        /// to run that write, which is the thing Predicted says.
        /// </summary>
        [Test]
        public void CE0014_NetVehicleShapeWithTheDriverIdFlippedToOwner_IsStillNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunOwnershipAnalyzer(
                NetVehicleShapedFixture("Owner"), "NetVehicleShapedFixture");

            AssertNoDiagnostic(diagnostics, CE0014,
                "CE0014 cannot see this flip: LatchDriverId's write is unguarded, but every caller of " +
                "LatchDriverId reaches PublishDriverId's ownership check, so the caller rule accepts it. " +
                "A report appearing here means the guard walk stopped following callees into " +
                "PublishDriverId -- which would also mean CE0014 has started reporting the take-ownership " +
                "and check-in-a-helper shapes its own fix text recommends.");
        }

        /// <summary>
        /// Continuous sync auto-serializes from the owner, so the reconciliation CE0063 asks about is
        /// supplied by VRChat rather than by the author. Reporting here would fire on every correct
        /// Continuous behaviour that predicts anything.
        /// </summary>
        [Test]
        public void CE0063_ContinuousPredicted_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class PredictedContinuousFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted)] private float _boostRemaining;

    private void Update()
    {
        _boostRemaining -= Time.deltaTime;
    }
}
", "PredictedContinuousFixture");

            AssertNoDiagnostic(diagnostics, CE0063,
                "Continuous behaviours serialize from the owner without being asked, so the field is " +
                "reconciled and there is nothing to report.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0063 -- the three false positives, pinned so they cannot come back
        //
        // Each of these compiled clean and reported CE0063 under the first draft. They are not edge cases
        // dredged up to be difficult: shapes 1 and 2 are how this package's own NetPhysics library is
        // built, and shape 3 turned on a bandwidth knob and got a divergence warning, through an
        // OnPreSerialization the author never wrote and cannot see. A check that reports all three is not
        // stricter than the one that ships, it is wrong three more times.
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Counterexample 1: the ownership helper lives in a base class.
        ///
        /// 'protected bool IsLocalAuthority => Networking.IsOwner(gameObject);' on a shared entity base,
        /// used by every subclass, is the obvious refactor and the shape NetPhysicsEntity/NetVehicle
        /// already has. A scan of the derived type's own declarations finds no ownership call anywhere and
        /// concludes the send is ungated -- which was CE0063's report, and, more seriously, CE0014's on
        /// every write behind that guard.
        ///
        /// This is a mode-gate pass now (Manual leaves before any field is read) rather than a
        /// send-detection pass, so it can no longer regress by way of the guard being invisible. It stays
        /// because the guard being invisible was the actual defect, and the day someone re-adds a
        /// Manual-mode question this fixture is the first thing it has to survive.
        /// <see cref="CE0014_OwnerFieldGuardedByABaseClassProperty_IsNotReported"/> is the half of this
        /// pair that still depends on the base walk working.
        /// </summary>
        [Test]
        public void CE0063_ManualPredictedGuardedByABaseClassProperty_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(
                BaseClassGuardFixture, "BaseClassGuardFixture");

            AssertNoDiagnostic(diagnostics, CE0063,
                "The owner gate is 'if (!IsLocalAuthority) return;' and IsLocalAuthority is declared on " +
                "the base. An analyzer that reads only the derived type's declarations sees an ungated " +
                "send here and reports correct code.");
        }

        /// <summary>
        /// Counterexample 2: the owner-side publish is driven from another behaviour.
        ///
        /// Neither Step() nor Publish() has a caller inside this type, because both are invoked by a pool
        /// manager doing 'if (Networking.IsOwner(p.gameObject)) p.Publish();' -- which is precisely how
        /// NetPhysicsWorld drives its entities. From inside the projectile there is no gate, no caller and
        /// nothing to walk; from outside there is a perfectly ordinary owner-gated serialization.
        ///
        /// This is the counterexample that killed the Manual arm rather than narrowing it. It is
        /// indistinguishable, by anything visible in this type, from a behaviour that genuinely never
        /// publishes -- see <see cref="CE0063_ManualPredictedThatNeverPublishes_IsNotReported"/> for the
        /// other side of that coin and what it costs.
        /// </summary>
        [Test]
        public void CE0063_ManualPredictedPublishedByAnotherBehaviour_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class PooledProjectileFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted)] private float _lifeRemaining;

    public void Step()
    {
        _lifeRemaining -= 0.02f;
    }

    public void Publish()
    {
        RequestSerialization();
    }
}
", "PooledProjectileFixture");

            AssertNoDiagnostic(diagnostics, CE0063,
                "A method with no caller in its own behaviour is an entry point something outside drives, " +
                "not a dead end. Reporting here says 'I could not find the owner-side send' while claiming " +
                "'there is not one', and those are different statements.");
        }

        /// <summary>
        /// Counterexample 3: a quantized predicted field.
        ///
        /// Quantize makes QuantizePreSerializeEmitter synthesize an OnPreSerialization for the behaviour
        /// during codegen. The first draft consulted "does this type declare OnPreSerialization" as
        /// evidence about reconciliation, so turning on quantization -- a bandwidth knob that changes
        /// nothing about who sends -- moved CE0063 on and off. The corrected rule reads the sync mode and
        /// nothing else, so Manual leaves unreported whatever the field's tuning options say.
        ///
        /// The verdict asserted here is the one the analyzer actually produces, not the one the original
        /// CE0063 specification described. That specification's Manual arm no longer exists.
        /// </summary>
        [Test]
        public void CE0063_ManualQuantizedPredicted_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class QuantizedPredictedFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted, Quantize = 0.01f)] private float _boostRemaining;

    private void FixedUpdate()
    {
        if (!Networking.IsOwner(gameObject))
            return;

        RequestSerialization();
    }

    public void ApplyInput(float deltaTime)
    {
        _boostRemaining -= deltaTime;
    }
}
", "QuantizedPredictedFixture");

            AssertNoDiagnostic(diagnostics, CE0063,
                "Quantize is a payload knob. It must not decide whether a prediction is reconciled, in " +
                "either direction -- and the OnPreSerialization it makes the compiler synthesize is not " +
                "evidence about who sends.");
        }

        /// <summary>
        /// A behaviour that replicates nothing can never correct a prediction, so a predicted field on it
        /// diverges from the first frame.
        ///
        /// In a full compile this shape is intercepted earlier: BehaviourSyncModeEmitter raises CE0033
        /// during Phase A codegen for None plus synced state, before Roslyn ever binds. Driving the
        /// analyzer directly is what makes the branch reachable at all, which is exactly why it is worth
        /// pinning -- nothing else in the project can tell whether the branch was implemented or quietly
        /// skipped.
        /// </summary>
        [Test]
        public void CE0063_SyncModeNoneWithPredictedField_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class PredictedNoneFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted)] private float _boostRemaining;

    private void Update()
    {
        _boostRemaining -= Time.deltaTime;
    }
}
", "PredictedNoneFixture");

            AssertSingleDiagnostic(diagnostics, CE0063,
                "BehaviourSyncMode.None never replicates, so a field declared Predicted on it has no owner " +
                "value to converge on.");
        }

        /// <summary>
        /// The other non-replicating mode, asserted separately because it is a second enum value inside one
        /// condition and dropping it is a one-token edit with no other symptom.
        ///
        /// NoVariableSync is the mode authors reach for on an RPC-driven behaviour that deliberately syncs
        /// no variables, which makes it the likelier of the two to carry a synced field by accident -- the
        /// author was thinking about events, not state. If only None is checked, that behaviour predicts
        /// into a void and nothing says so.
        /// </summary>
        [Test]
        public void CE0063_SyncModeNoVariableSyncWithPredictedField_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class PredictedNoVariableSyncFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted)] private float _boostRemaining;

    private void Update()
    {
        _boostRemaining -= Time.deltaTime;
    }
}
", "PredictedNoVariableSyncFixture");

            AnalyzerDiagnostic diagnostic = AssertSingleDiagnostic(diagnostics, CE0063,
                "NoVariableSync transmits no variables, which is the same impossibility as None and must " +
                "produce the same report.");

            Assert.That(diagnostic.Message, Does.Contain("NoVariableSync"),
                "The report must name the mode it read, because 'None' in the text of a NoVariableSync " +
                "report sends the author looking at the wrong attribute.");
        }

        /// <summary>
        /// The other half of counterexample 3, and the one with teeth.
        ///
        /// The first draft treated an OnPreSerialization declaration as evidence that the owner publishes,
        /// which got the quantized case wrong in both directions at once. On Manual it silenced a report
        /// it should not have had; on None it silenced a report it should have kept -- an
        /// OnPreSerialization on a behaviour that transmits nothing is a callback that is never invoked,
        /// so it makes exactly no difference to whether the prediction converges. The declaration here is
        /// hand-written because that is the only way to put one in front of a directly-invoked analyzer,
        /// but it stands in for the one QuantizePreSerializeEmitter synthesizes from 'Quantize = 0.01f',
        /// which is how an author would trip this without writing a callback at all.
        /// </summary>
        [Test]
        public void CE0063_SyncModeNoneWithQuantizedPredictedField_IsStillReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class QuantizedPredictedNoneFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted, Quantize = 0.01f)] private float _boostRemaining;

    public override void OnPreSerialization()
    {
    }

    private void Update()
    {
        _boostRemaining -= Time.deltaTime;
    }
}
", "QuantizedPredictedNoneFixture");

            AnalyzerDiagnostic diagnostic = AssertSingleDiagnostic(diagnostics, CE0063,
                "Neither Quantize nor an OnPreSerialization makes a non-replicating behaviour replicate. " +
                "If either one silences this, a CE feature is switching off a true positive and the only " +
                "symptom is silence.");

            Assert.That(diagnostic.Message, Does.Contain("_boostRemaining"),
                "The report must name the predicted field that never converges.");

            Assert.That(diagnostic.Severity, Is.EqualTo(CompilerSeverity.Warning),
                "CE0063 is specified as a Warning.");
        }

        /// <summary>
        /// The accepted false negative, pinned deliberately.
        ///
        /// This behaviour predicts a field and has no RequestSerialization anywhere, so nothing on it ever
        /// publishes and the prediction really does diverge forever. CE0063 says nothing, and that is the
        /// shipped behaviour rather than an oversight: from inside the type this fixture is
        /// indistinguishable from
        /// <see cref="CE0063_ManualPredictedPublishedByAnotherBehaviour_IsNotReported"/>, where a pool
        /// manager calls 'entity.RequestSerialization()' from outside, and from a behaviour whose publish
        /// is wired to a SendCustomEvent in the inspector. Both of those are correct code this package
        /// ships and documents, so the evidence available here cannot separate them.
        ///
        /// The gap is partly covered elsewhere: CE0015 reports a Manual behaviour that writes a synced
        /// field with no RequestSerialization in its own source, at the write, and names the
        /// cross-behaviour call in its fix text. It does not cover a write made through a property setter.
        ///
        /// This test exists so that changing the verdict is a deliberate act. If a future revision can
        /// prove divergence for this shape without also reporting the pool-manager and base-class fixtures
        /// above, inverting this assertion is the right move -- but it has to be a decision, not a
        /// side effect.
        /// </summary>
        [Test]
        public void CE0063_ManualPredictedThatNeverPublishes_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class NeverPublishesFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted)] private float _boostRemaining;

    private void Update()
    {
        _boostRemaining -= Time.deltaTime;
    }
}
", "NeverPublishesFixture");

            AssertNoDiagnostic(diagnostics, CE0063,
                "CE0063 does not claim to verify that a Manual behaviour publishes -- see the type comment " +
                "on PredictedFieldReconciliationAnalyzer. This assertion records the cost of that scope, " +
                "so nobody has to rediscover it by finding a divergence in a live world.");
        }

        /// <summary>
        /// A behaviour that declares no sync mode has its effective mode chosen in the inspector, so
        /// whether it reconciles is unknowable at compile time. The design contract's tie-break is explicit
        /// -- prefer a false negative to a false positive -- and getting this wrong fires CE0063 on every
        /// mode-less behaviour in a project.
        /// </summary>
        [Test]
        public void CE0063_UndeclaredSyncMode_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunReconciliationAnalyzer(@"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

public class PredictedUndeclaredModeFixture : UdonSharpBehaviour
{
    [Sync(Authority.Predicted)] private float _boostRemaining;

    private void Update()
    {
        _boostRemaining -= Time.deltaTime;
    }
}
", "PredictedUndeclaredModeFixture");

            AssertNoDiagnostic(diagnostics, CE0063,
                "With no [UdonBehaviourSyncMode] the effective mode is an authoring-time inspector choice. " +
                "Reporting a divergence that cannot be proven is the false positive the contract forbids.");
        }

        /// <summary>
        /// An analyzer nobody registered is indistinguishable from an analyzer that found nothing. This
        /// repo already surfaces analyzer *crashes* as CE0019 for that reason, but a missing registration
        /// produces no failure to surface, so it needs its own assertion.
        /// </summary>
        [Test]
        public void CE0063_IsRegisteredWithTheAnalyzerRunner()
        {
            FieldInfo analyzersField = typeof(CEAnalyzerRunner)
                .GetField("Analyzers", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(analyzersField, Is.Not.Null,
                "CEAnalyzerRunner.Analyzers is gone, so the registration this test checks no longer exists " +
                "in the form it checks for.");

            var analyzers = (ICompileTimeAnalyzer[])analyzersField.GetValue(null);
            string[] ids = analyzers.Select(analyzer => analyzer.AnalyzerId).ToArray();

            Assert.That(ids, Does.Contain(CE0063),
                "PredictedFieldReconciliationAnalyzer must be in CEAnalyzerRunner.Analyzers. Writing the " +
                "analyzer and never registering it produces a compile that looks clean, for the same " +
                "reason an unregistered check always does: silence. Registered ids: " +
                string.Join(", ", ids));

            Assert.That(ids, Does.Contain(CE0014),
                "CE0014 must still be registered after the rework.");

            // Every other analyzer this file drives is instantiated by type name, which proves it behaves
            // but not that it runs. An analyzer dropped from this array produces exactly the silence a
            // clean compile produces, and the tests above would keep passing through it.
            foreach (string code in new[] { CE0015, CE0016, CE0017 })
            {
                Assert.That(ids, Does.Contain(code),
                    $"{code} is exercised by tests in this file that construct its analyzer directly, so " +
                    "removing it from the runner would take the check out of every compile while leaving " +
                    "those tests green. Registered ids: " + string.Join(", ", ids));
            }

            CollectionAssert.AllItemsAreUnique(ids,
                "Two analyzers sharing a diagnostic code make the code useless for filtering and " +
                "documentation. Registered ids: " + string.Join(", ", ids));
        }

        // ------------------------------------------------------------------------------------------
        // Migration
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// CE's flagship netcode library dogfoods CE's own attribute.
        ///
        /// Read through reflection rather than by grepping the source, so this asserts the metadata the
        /// compiler and the analyzers actually see. Three of the four fields have their authority pinned
        /// exactly; _airStateFlags is required only to declare *some* authority, because nothing outside
        /// NetVehicle's own comments depends on which one it carries.
        ///
        /// _ownerPlayerId is pinned because two documents now cite it by name -- Authority.cs's Predicted
        /// remarks use it as the canonical shipped example of "Predicted records who may write, not where
        /// the value came from", and agents/ce-runtime.md tells agents not to "fix" it back to Owner -- and
        /// nothing enforced either claim. It is also the one field where no analyzer can cover for the
        /// prose: flipping it to Owner produces no CE0014 at all, because LatchDriverId's callers every one
        /// reach PublishDriverId's ownership check and EveryCallerGuards is satisfied. So the declaration
        /// is the whole of the protection, and this assertion is the whole of the enforcement. See
        /// <see cref="CE0014_NetVehicleShapeWithTheDriverIdFlippedToOwner_IsStillNotReported"/>, which pins
        /// that gap from the analyzer's side.
        /// </summary>
        [Test]
        public void NetVehicle_DeclaresAuthorityOnEverySyncedField()
        {
            Type netVehicle = GetAssemblyByName("UdonSharp.CE.Runtime")
                .GetType("UdonSharp.CE.NetPhysics.NetVehicle", throwOnError: false);

            Assert.That(netVehicle, Is.Not.Null, "UdonSharp.CE.NetPhysics.NetVehicle was not found.");

            var expected = new Dictionary<string, Authority?>
            {
                { "_boostRemaining", Authority.Predicted },
                { "_jumpTimer", Authority.Predicted },
                { "_ownerPlayerId", Authority.Predicted },
                { "_airStateFlags", null }
            };

            var because = new Dictionary<string, string>
            {
                {
                    "_boostRemaining",
                    "_boostRemaining is drained by ApplyInput, which NetPhysicsWorld runs for every vehicle " +
                    "on every client. That write is prediction, and declaring it as such is what clears the " +
                    "CE0014 warnings honestly instead of suppressing them."
                },
                {
                    "_jumpTimer",
                    "_jumpTimer is advanced by ApplyInput on every client for the same reason as " +
                    "_boostRemaining: a dodge window that waited for the owner's packet would be visibly " +
                    "late on every remote car."
                },
                {
                    "_ownerPlayerId",
                    "_ownerPlayerId is Predicted because LatchDriverId writes it on every client that hears " +
                    "the AssignDriver broadcast, and Authority records who may write a field rather than " +
                    "where the value came from. Owner would be the intuitive reading -- a driver id looks " +
                    "like authoritative state -- and it is the wrong one: it reinstates CE0014 on correct " +
                    "code, and the analyzer cannot argue back, because PublishDriverId's guard covers every " +
                    "caller of LatchDriverId so no report is produced either way. Authority.cs cites this " +
                    "exact field as the worked example of the distinction, so changing it also makes the " +
                    "shipped documentation wrong."
                }
            };

            foreach (KeyValuePair<string, Authority?> entry in expected)
            {
                FieldInfo field = netVehicle.GetField(
                    entry.Key,
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                Assert.That(field, Is.Not.Null, $"NetVehicle.{entry.Key} is gone.");

                var attribute = field.GetCustomAttribute<SyncAttribute>();

                Assert.That(attribute, Is.Not.Null,
                    $"NetVehicle.{entry.Key} must declare its authority with CE's [Sync]. A bare " +
                    "[UdonSynced] here leaves the library that motivated the whole change unable to say " +
                    "what it needed to say.");

                if (entry.Value != null)
                {
                    Assert.That(attribute.Authority, Is.EqualTo(entry.Value.Value), because[entry.Key]);
                }
            }
        }

        // ------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// One fixture, one word different between the two tiers.
        ///
        /// AdvancePrediction has no caller inside the behaviour and no ownership check on its path, which
        /// is precisely the shape CE0014 reports, so the only thing that can change the verdict between the
        /// two spellings is the declared authority. The owner-gated FixedUpdate is present in both on
        /// purpose: it proves the Predicted verdict comes from the declaration rather than from a guard the
        /// path walker happened to find somewhere else in the type.
        /// </summary>
        private static string UnguardedWriteFixture(string authority)
        {
            return @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class UnguardedWriteFixture : UdonSharpBehaviour
{
    [Sync(Authority." + authority + @")] private float _boostRemaining;

    public void AdvancePrediction()
    {
        _boostRemaining -= 0.25f;
    }

    private void FixedUpdate()
    {
        if (!Networking.IsOwner(gameObject))
            return;

        RequestSerialization();
    }
}
";
        }

        /// <summary>
        /// NetVehicle reduced to the members the two analyzers read: the Manual mode, the owner-gated
        /// serialization path, the OnPreSerialization override, an owner-authoritative field written only
        /// from that override, predicted fields advanced from a method every client runs, and the driver-id
        /// latch/publish pair.
        ///
        /// The driver id is the part this fixture used to omit, and it is the part worth having. The other
        /// three fields are decided by their declarations alone -- two Predicted writes CE0014 drops before
        /// the guard walk ever runs, one Owner write that only OnPreSerialization makes -- so nothing about
        /// them can regress except the tier switch itself, which the UnguardedWriteFixture pair already
        /// covers. _ownerPlayerId is the exception: its write in LatchDriverId carries no guard of its own,
        /// and the only reason CE0014 would stay quiet on it as an Owner field is the callee walk finding
        /// PublishDriverId's ownership check from every caller. That makes it the one field in NetVehicle's
        /// shape whose verdict comes out of the call graph rather than off the declaration -- which is
        /// precisely the machinery the base-chain work keeps rebuilding.
        ///
        /// The authority is a parameter so the same shape can be asked the question both ways; see the two
        /// CE0014 tests that use it. The entry point is named AssignDriverLocally rather than NetVehicle's
        /// own SetOwner because 'SetOwner' is one of the names the ownership scan matches, and a fixture
        /// whose verdict depends on that scan declining to treat a same-named local method as a guard is a
        /// fixture that stops asserting anything the day it stops declining.
        /// </summary>
        private static string NetVehicleShapedFixture(string driverIdAuthority)
        {
            return @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class NetVehicleShapedFixture : UdonSharpBehaviour
{
    public float StateSendRate = 5f;

    [Sync(Authority.Predicted)] private float _boostRemaining;
    [Sync(Authority.Predicted)] private float _jumpTimer;
    [Sync(Authority." + driverIdAuthority + @")] private int _ownerPlayerId;
    [Sync(Authority.Owner)] private byte _airStateFlags;

    private int _pendingDriverId;
    private bool _hasPendingDriverId;

    private void FixedUpdate()
    {
        if (StateSendRate <= 0f || !Networking.IsOwner(gameObject))
            return;

        RequestSerialization();
    }

    public override void OnPreSerialization()
    {
        _airStateFlags = (byte)1;
    }

    public override void OnDeserialization()
    {
    }

    public void ApplyInput(float deltaTime)
    {
        _boostRemaining -= deltaTime;
        _jumpTimer -= deltaTime;
    }

    public void AssignDriverLocally(int driverId)
    {
        LatchDriverId(driverId);
        PublishDriverId();
    }

    public override void OnPlayerLeft(VRCPlayerApi player)
    {
        LatchDriverId(-1);
        PublishDriverId();
    }

    private void LatchDriverId(int driverId)
    {
        _pendingDriverId = driverId;
        _hasPendingDriverId = true;
        _ownerPlayerId = driverId;
    }

    public void PublishDriverId()
    {
        if (!_hasPendingDriverId || !Networking.IsOwner(gameObject))
            return;

        _hasPendingDriverId = false;
        _ownerPlayerId = _pendingDriverId;

        RequestSerialization();
    }
}
";
        }

        /// <summary>
        /// A shared entity base that owns the ownership helper, and one subclass that uses it. Both
        /// analyzers are asked about the subclass.
        ///
        /// Nothing in the subclass mentions Networking, so a scan of its own declarations finds no guard
        /// at all -- which is the whole point. The base is abstract and holds nothing but the property,
        /// because a bigger base would give the scan other reasons to succeed and stop this fixture from
        /// testing the base walk specifically.
        ///
        /// Both authorities appear on purpose. _score is Owner, so CE0014's strict tier really runs on it
        /// and the guard has to be found for the test to pass; _boostRemaining is Predicted, so CE0063 has
        /// something to look at rather than trivially finding no predicted fields.
        /// </summary>
        private const string BaseClassGuardFixture = @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDKBase;

public abstract class OwnedEntityBaseFixture : UdonSharpBehaviour
{
    protected bool IsLocalAuthority => Networking.IsOwner(gameObject);
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class BaseClassGuardFixture : OwnedEntityBaseFixture
{
    [Sync(Authority.Owner)] private int _score;
    [Sync(Authority.Predicted)] private float _boostRemaining;

    private void Update()
    {
        if (!IsLocalAuthority)
            return;

        _score++;
        _boostRemaining -= 0.02f;
        RequestSerialization();
    }
}
";

        /// <summary>
        /// The void ownership helper on a shared base, and one subclass that calls it before writing.
        ///
        /// Deliberately not a variant of <see cref="BaseClassGuardFixture"/>: that one carries a property
        /// whose value is an ownership call, which the ownership-name scan finds on its own, and a fixture
        /// holding both would pass while the graph walk that has to find this helper stayed broken. The
        /// base is abstract and holds nothing else for the same reason -- any other member gives the scans
        /// a second way to succeed and stops the fixture from testing the one route it exists for.
        ///
        /// The write is in Interact, which is not an exempt entry point and has no caller inside the
        /// behaviour, so nothing else on the path can supply the verdict: strip EnsureOwnership() out and
        /// this fixture is the textbook CE0014 report.
        /// </summary>
        private const string BaseClassVoidGuardFixture = @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDKBase;

public abstract class OwnedEntityVoidHelperBaseFixture : UdonSharpBehaviour
{
    protected void EnsureOwnership()
    {
        if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
    }
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class BaseClassVoidGuardFixture : OwnedEntityVoidHelperBaseFixture
{
    [Sync(Authority.Owner)] private int _score;

    public override void Interact()
    {
        EnsureOwnership();

        _score++;
        RequestSerialization();
    }
}
";

        /// <summary>
        /// The publish helper on a shared base, and one subclass that writes synced state through it.
        ///
        /// The base holds only Publish(), so the RequestSerialization CE0015 looks for exists exactly once
        /// and only in a place the derived type's own declarations do not contain. The subclass guards its
        /// write with a literal Networking.IsOwner rather than reusing the helper from
        /// <see cref="BaseClassVoidGuardFixture"/>, so this fixture asks about the serialization scan alone
        /// and cannot be rescued -- or broken -- by whatever the ownership scans make of it.
        /// </summary>
        private const string BaseClassPublishFixture = @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDKBase;

public abstract class PublishingEntityBaseFixture : UdonSharpBehaviour
{
    protected void Publish()
    {
        RequestSerialization();
    }
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class BaseClassPublishFixture : PublishingEntityBaseFixture
{
    [Sync(Authority.Owner)] private int _score;

    public override void Interact()
    {
        if (!Networking.IsOwner(Networking.LocalPlayer, gameObject))
            return;

        _score++;
        Publish();
    }
}
";

        // ------------------------------------------------------------------------------------------
        // Two-file fixtures. Each pair is passed to RunAnalyzerOnSubclass, which parses the base and the
        // subclass under different paths so an assertion can tell which one a report landed in.
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// A guarded virtual on the base. Nothing else lives here: any second member would give the guard
        /// walks another body to succeed in and stop the pair from testing shadowing specifically.
        /// </summary>
        private const string ShadowedGuardBaseFixture = @"
using UdonSharp;
using VRC.SDKBase;

public abstract class ShadowedGuardBaseFixture : UdonSharpBehaviour
{
    protected int _appliedTicks;

    protected virtual void ApplyState()
    {
        if (!Networking.IsOwner(gameObject))
            return;

        _appliedTicks++;
    }
}
";

        /// <summary>
        /// The subclass that overrides the guard away, and the only synced write in the pair.
        ///
        /// The subclass names Networking nowhere, which is the whole point: every ownership answer
        /// available to CE0014 here comes from a body that this type replaced. Interact is the entry point
        /// because it is not exempt and has no caller inside the behaviour, so the verdict cannot come from
        /// anywhere except the guard walk over ApplyState and its one call site.
        /// </summary>
        private const string ShadowedGuardFixture = @"
using UdonSharp;
using UdonSharp.CE.Net;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ShadowedGuardFixture : ShadowedGuardBaseFixture
{
    [Sync(Authority.Owner)] private int _score;

    protected override void ApplyState()
    {
        _score++;
    }

    public override void Interact()
    {
        ApplyState();
    }
}
";

        /// <summary>
        /// A Manual base whose only member is the publish helper. The sync mode is declared here rather
        /// than on the subclass because that is the shape that puts a subclass in front of CE0016 without
        /// its author writing anything about sync at all.
        /// </summary>
        private const string ManualPublishingBaseFixture = @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public abstract class ManualPublishingBaseFixture : UdonSharpBehaviour
{
    protected void Publish()
    {
        RequestSerialization();
    }
}
";

        /// <summary>
        /// The subclass with state to replicate, a publish call, and not one synced field to carry it --
        /// CE0016's subject exactly, arrived at through inheritance. '_score' is deliberately unsynced:
        /// this is the shipped LeaderboardManager shape that CE0016 was written for.
        /// </summary>
        private const string ManualNoSyncedFieldsFixture = @"
using UdonSharp;

public class ManualNoSyncedFieldsFixture : ManualPublishingBaseFixture
{
    private int _score;

    public override void Interact()
    {
        _score++;
        Publish();
    }
}
";

        /// <summary>
        /// The ungated per-frame serialization, held as one string so the own-declaration control and the
        /// base-declared fixture below are character-identical. Anything else and a later edit to one of
        /// them quietly turns the pair into two unrelated tests.
        /// </summary>
        private const string PerFrameSerializationBody = @"
    private int _tick;

    private void Update()
    {
        _tick++;
        RequestSerialization();
    }
";

        /// <summary>The control: the body in the behaviour's own declaration, where CE0017 reports it.</summary>
        private const string OwnPerFrameSerializationFixture = @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class OwnPerFrameSerializationFixture : UdonSharpBehaviour
{" + PerFrameSerializationBody + @"}
";

        /// <summary>The same body, one class up.</summary>
        private const string PerFrameSerializingBaseFixture = @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public abstract class PerFrameSerializingBaseFixture : UdonSharpBehaviour
{" + PerFrameSerializationBody + @"}
";

        /// <summary>
        /// The subclass that inherits it. It declares a method of its own so the graph is non-empty:
        /// CE0017 returns early on a behaviour with no own methods, and a test that passes because the
        /// analyzer left at the first line is not testing the per-frame scan.
        /// </summary>
        private const string PerFrameSerializingSubclassFixture = @"
using UdonSharp;

public class PerFrameSerializingSubclassFixture : PerFrameSerializingBaseFixture
{
    private int _pokes;

    public override void Interact()
    {
        _pokes++;
    }
}
";

        /// <summary>A Manual base whose publish helper is reached from the subclass's per-frame method.</summary>
        private const string PublishHelperBaseFixture = @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public abstract class PublishHelperBaseFixture : UdonSharpBehaviour
{
    protected void Publish()
    {
        RequestSerialization();
    }
}
";

        /// <summary>
        /// The subclass whose Update reaches the inherited send. Unlike the pair above, the inherited body
        /// really is on this behaviour's per-frame path -- the callee walk reaches it -- so this is the
        /// fixture that would start reporting into the base file if CE0017 built its path from graph
        /// reachability instead of from the report-site list.
        /// </summary>
        private const string PublishHelperSubclassFixture = @"
using UdonSharp;

public class PublishHelperSubclassFixture : PublishHelperBaseFixture
{
    private int _tick;

    private void Update()
    {
        _tick++;
        Publish();
    }
}
";

        /// <summary>
        /// A base with a correct, dominating rate gate that gates only its own body.
        /// </summary>
        private const string ClockHelperBaseFixture = @"
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public abstract class ClockHelperBaseFixture : UdonSharpBehaviour
{
    protected float _lastPollTime;

    protected void PollClock()
    {
        if (Time.time - _lastPollTime < 1f)
            return;

        _lastPollTime = Time.time;
    }
}
";

        /// <summary>
        /// The subclass that calls it on the way to an unconditional serialization. PollClock()'s early
        /// return returns from PollClock, so Update serializes every frame regardless of what the clock
        /// says -- which is what makes this the fixture a widened per-frame path would silence.
        /// </summary>
        private const string ClockHelperSubclassFixture = @"
using UdonSharp;

public class ClockHelperSubclassFixture : ClockHelperBaseFixture
{
    private int _tick;

    private void Update()
    {
        PollClock();

        _tick++;
        RequestSerialization();
    }
}
";

        // ------------------------------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------------------------------

        private sealed class BoundFixture
        {
            public TypeSymbol Type;
            public BindContext BindContext;
            public CompilationContext CompilationContext;
        }

        /// <summary>One fixture file: its source, and the path the tree is parsed under.</summary>
        private sealed class FixtureFile
        {
            public readonly string Path;
            public readonly string Source;

            public FixtureFile(string path, string source)
            {
                Path = path;
                Source = source;
            }
        }

        private static IReadOnlyList<AnalyzerDiagnostic> RunOwnershipAnalyzer(string source, string behaviourName)
        {
            return RunAnalyzer(OwnershipAnalyzerTypeName, source, behaviourName);
        }

        private static IReadOnlyList<AnalyzerDiagnostic> RunReconciliationAnalyzer(string source, string behaviourName)
        {
            return RunAnalyzer(ReconciliationAnalyzerTypeName, source, behaviourName);
        }

        private static IReadOnlyList<AnalyzerDiagnostic> RunSerializationAnalyzer(string source, string behaviourName)
        {
            return RunAnalyzer(SerializationAnalyzerTypeName, source, behaviourName);
        }

        private static IReadOnlyList<AnalyzerDiagnostic> RunSyncedFieldsAnalyzer(string source, string behaviourName)
        {
            return RunAnalyzer(SyncedFieldsAnalyzerTypeName, source, behaviourName);
        }

        private static IReadOnlyList<AnalyzerDiagnostic> RunSerializationRateAnalyzer(string source, string behaviourName)
        {
            return RunAnalyzer(SerializationRateAnalyzerTypeName, source, behaviourName);
        }

        /// <summary>
        /// Runs one analyzer over a subclass whose base is a separate file.
        ///
        /// The split is the assertion surface. Every fixture in this file could put both classes in one
        /// string -- the analyzers read syntax trees, not files -- but then every report has the same path
        /// and "this must not be anchored into the base" becomes unassertable, which is precisely the half
        /// of the base-chain change that has no other guard.
        /// </summary>
        private static IReadOnlyList<AnalyzerDiagnostic> RunAnalyzerOnSubclass(
            string analyzerTypeName,
            string baseSource,
            string derivedSource,
            string behaviourName)
        {
            return RunAnalyzer(
                analyzerTypeName,
                new[]
                {
                    new FixtureFile(BaseFixturePath, baseSource),
                    new FixtureFile(DerivedFixturePath, derivedSource)
                },
                behaviourName);
        }

        private static IReadOnlyList<AnalyzerDiagnostic> RunAnalyzer(
            string analyzerTypeName,
            string source,
            string behaviourName)
        {
            return RunAnalyzer(
                analyzerTypeName,
                new[] { new FixtureFile(DerivedFixturePath, source) },
                behaviourName);
        }

        private static IReadOnlyList<AnalyzerDiagnostic> RunAnalyzer(
            string analyzerTypeName,
            IReadOnlyList<FixtureFile> files,
            string behaviourName)
        {
            ICompileTimeAnalyzer analyzer = CreateAnalyzer(analyzerTypeName);
            BoundFixture fixture = BindFixture(files, behaviourName);

            IEnumerable<AnalyzerDiagnostic> diagnostics =
                analyzer.Analyze(fixture.Type, fixture.BindContext, fixture.CompilationContext);

            return (diagnostics ?? Enumerable.Empty<AnalyzerDiagnostic>())
                .Where(diagnostic => diagnostic != null)
                .ToList();
        }

        /// <summary>
        /// Instantiated by name so a missing analyzer fails one test with a readable message instead of
        /// failing to compile the test assembly. That distinction is load bearing here: a single C# error
        /// anywhere in this assembly takes the whole edit-mode suite down with it, and the resulting
        /// cascade of unrelated failures buries whatever actually broke.
        /// </summary>
        private static ICompileTimeAnalyzer CreateAnalyzer(string analyzerTypeName)
        {
            Type analyzerType = GetAssemblyByName("UdonSharp.CE.Editor")
                .GetType(analyzerTypeName, throwOnError: false);

            if (analyzerType == null)
            {
                Assert.Fail(
                    $"Analyzer type '{analyzerTypeName}' does not exist in UdonSharp.CE.Editor, so the " +
                    "check it owns cannot run. Its absence from a compile's diagnostics says nothing about " +
                    "whether the code is clean.");
            }

            return (ICompileTimeAnalyzer)Activator.CreateInstance(analyzerType, nonPublic: true);
        }

        /// <summary>
        /// Runs the compiler's real Bind phase over a single fixture in its own compilation, producing the
        /// three objects <c>ICompileTimeAnalyzer.Analyze</c> takes.
        ///
        /// This mirrors UdonSharpCompilerV1: one CSharpCompilation over the fixture with the same metadata
        /// references the compiler uses, handed to a CompilationContext, then the root type bound through a
        /// BindContext. The inheritance lookup the real pipeline builds alongside is skipped deliberately --
        /// nothing in the Bind phase reads it, and building it walks the base chain looking for an identity
        /// match against UdonSharpBehaviour, which is a loop this harness has no reason to enter.
        ///
        /// Source generators and optimizers are not run, which is the one respect in which this is not a
        /// full compile. For CE0063 that is a feature: BehaviourSyncMode.None plus synced state is rejected
        /// in Phase A codegen, so the analyzer's branch for it is reachable only from here.
        /// </summary>
        private static BoundFixture BindFixture(string source, string behaviourName)
        {
            return BindFixture(new[] { new FixtureFile(DerivedFixturePath, source) }, behaviourName);
        }

        // ------------------------------------------------------------------------------------------
        // Fixture assembly registration
        // ------------------------------------------------------------------------------------------

        private static readonly FieldInfo UdonSharpAssembliesField = typeof(CompilerUdonInterface)
            .GetField("_udonSharpAssemblies", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>The assembly set as it was before any fixture was appended to it.</summary>
        private static ImmutableArray<Assembly>? _pristineUdonSharpAssemblies;

        /// <summary>
        /// Emits the fixture into a loadable assembly and adds it to the set the binder resolves
        /// source-declared types against. See the call site for why binding cannot work without it.
        /// </summary>
        private static void RegisterFixtureAssembly(CSharpCompilation compilation, string behaviourName)
        {
            Assert.That(UdonSharpAssembliesField, Is.Not.Null,
                "CompilerUdonInterface._udonSharpAssemblies is gone, so fixtures can no longer be registered " +
                "and every test in this file would fail to bind. Find where the assembly set now lives.");

            // Read through the property first: its getter runs AssemblyCacheInit, so this is the populated
            // set. Writing the field before init would be silently overwritten by the first later read.
            ImmutableArray<Assembly> current = CompilerUdonInterface.UdonSharpAssemblies;

            if (!_pristineUdonSharpAssemblies.HasValue)
                _pristineUdonSharpAssemblies = current;

            byte[] image;

            using (var stream = new MemoryStream())
            {
                EmitResult emitResult = compilation.Emit(stream);

                Assert.That(emitResult.Success, Is.True,
                    $"Fixture '{behaviourName}' compiles but does not emit, so it cannot be registered.");

                image = stream.ToArray();
            }

            UdonSharpAssembliesField.SetValue(null, current.Add(Assembly.Load(image)));
        }

        /// <summary>
        /// Drops every fixture assembly the test that just ran registered. Fixture type names repeat across
        /// tests and GetSystemTypeInternal returns the first match it finds, so leaving them registered would
        /// let one test's fixture answer for another's.
        /// </summary>
        [TearDown]
        public void UnregisterFixtureAssemblies()
        {
            if (_pristineUdonSharpAssemblies.HasValue && UdonSharpAssembliesField != null)
                UdonSharpAssembliesField.SetValue(null, _pristineUdonSharpAssemblies.Value);
        }

        /// <summary>
        /// The same bind, over several files.
        ///
        /// One compilation, several trees, each parsed under its own path -- which is what a real project
        /// looks like and what lets an assertion distinguish a report on the analyzed behaviour from one
        /// that landed in a base class's file. Every fixture goes through this overload, so single-file
        /// fixtures carry a path too and the two shapes are read the same way.
        /// </summary>
        private static BoundFixture BindFixture(IReadOnlyList<FixtureFile> files, string behaviourName)
        {
            // Populates the Udon extern tables the binder resolves calls against, and the
            // UdonSharp-assembly name set GetMetadataReferences filters on.
            CompilerUdonInterface.CacheInit();

            SyntaxTree[] trees = files
                .Select(file => CSharpSyntaxTree.ParseText(
                    file.Source,
                    CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                    file.Path))
                .ToArray();

            CSharpCompilation compilation = CSharpCompilation.Create(
                "CESyncAuthorityFixture." + Guid.NewGuid().ToString("N"),
                trees,
                FixtureReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            string[] errors = ErrorsOf(compilation);

            Assert.That(errors, Is.Empty,
                $"Fixture '{behaviourName}' does not compile, so nothing below it is testing an analyzer. " +
                "Errors:\n" + string.Join("\n", errors));

            // The binder will only treat a source-declared class as an UdonSharpBehaviour if it can map it
            // back to a real System.Type: ExternResolverExtensions.IsUdonSharpBehaviour walks the base chain
            // through TypeSymbol.TryGetSystemType, and for a type with no extern assembly
            // GetSystemTypeInternal searches nothing but CompilerUdonInterface.UdonSharpAssemblies. A fixture
            // parsed from a string is in none of them, so IsUdonSharpBehaviour answered false, TypeSymbolFactory
            // fell through to ImportedUdonSharpTypeSymbol, and its Bind threw "Inheritance is not supported by
            // U# on non-UdonSharpBehaviour classes" for every fixture in this file -- the analyzers never ran
            // at all. Emitting the fixture into a real assembly and registering it makes that lookup succeed.
            // TearDown puts the original set back so one fixture's types can never answer for another's.
            RegisterFixtureAssembly(compilation, behaviourName);

            INamedTypeSymbol behaviourSymbol = null;

            foreach (SyntaxTree tree in trees)
            {
                SemanticModel model = compilation.GetSemanticModel(tree);

                behaviourSymbol = tree.GetRoot()
                    .DescendantNodes()
                    .OfType<ClassDeclarationSyntax>()
                    .Where(declaration => declaration.Identifier.Text == behaviourName)
                    .Select(declaration => model.GetDeclaredSymbol(declaration))
                    .FirstOrDefault();

                if (behaviourSymbol != null)
                    break;
            }

            Assert.That(behaviourSymbol, Is.Not.Null,
                $"Fixture source declares no class named '{behaviourName}'.");

            var compilationContext = new CompilationContext(new UdonSharpCompileOptions { IsEditorBuild = true })
            {
                RoslynCompilation = compilation
            };

            var bindContext = new BindContext(compilationContext, behaviourSymbol, Array.Empty<Symbol>());

            try
            {
                bindContext.Bind();
            }
            catch (Exception exception)
            {
                Assert.Fail(
                    $"Binding fixture '{behaviourName}' threw, so the analyzer never ran. Read this as a " +
                    "harness or fixture problem, not as the check under test passing or failing.\n" +
                    exception);
            }

            string[] bindErrors = compilationContext.Diagnostics
                .Where(diagnostic => diagnostic.Severity == CompilerSeverity.Error)
                .Select(diagnostic => diagnostic.Message)
                .ToArray();

            Assert.That(bindErrors, Is.Empty,
                $"Binding fixture '{behaviourName}' produced compiler errors:\n" +
                string.Join("\n", bindErrors));

            return new BoundFixture
            {
                Type = bindContext.GetTypeSymbol(behaviourSymbol),
                BindContext = bindContext,
                CompilationContext = compilationContext
            };
        }

        private static string[] CompileErrors(string source)
        {
            CompilerUdonInterface.CacheInit();

            SyntaxTree tree = CSharpSyntaxTree.ParseText(
                source,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

            CSharpCompilation compilation = CSharpCompilation.Create(
                "CESyncAuthoritySpelling." + Guid.NewGuid().ToString("N"),
                new[] { tree },
                FixtureReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            return ErrorsOf(compilation);
        }

        private static IReadOnlyList<MetadataReference> _fixtureReferences;

        /// <summary>
        /// The compiler's own reference set, plus the U# source assemblies it deliberately leaves out.
        ///
        /// <c>CompilationContext.GetMetadataReferences</c> drops every assembly that has an
        /// UdonSharpAssemblyDefinition -- UdonSharp.CE.Runtime among them -- because a real compile feeds
        /// those assemblies' *source files* into the same Roslyn compilation, so referencing the built DLL
        /// as well would define every type twice. A fixture-only compilation has no such source, so without
        /// this the [Sync] attribute simply would not resolve and every test in this file would fail as
        /// CS0246 rather than as an analyzer result.
        ///
        /// Referencing the built DLL is also the more faithful choice for what is being tested here:
        /// <c>Symbol.TryCreateAttributeInstance</c> reflectively constructs the attribute against exactly
        /// this loaded assembly, so a fixture that binds against it is bound against the same type the
        /// compiler will instantiate.
        /// </summary>
        private static IReadOnlyList<MetadataReference> FixtureReferences()
        {
            if (_fixtureReferences != null)
                return _fixtureReferences;

            var references = new List<MetadataReference>(CompilationContext.GetMetadataReferences());

            var locations = new HashSet<string>(
                references.OfType<PortableExecutableReference>()
                    .Select(reference => reference.FilePath)
                    .Where(path => !string.IsNullOrEmpty(path)),
                StringComparer.OrdinalIgnoreCase);

            foreach (string assemblyName in new[] { "UdonSharp.Runtime", "UdonSharp.CE.Runtime", "UdonSharp.Lib" })
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => candidate.GetName().Name == assemblyName);

                if (assembly == null || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                    continue;

                if (!locations.Add(assembly.Location))
                    continue;

                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }

            _fixtureReferences = references;
            return _fixtureReferences;
        }

        private static string[] ErrorsOf(CSharpCompilation compilation)
        {
            return compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == RoslynSeverity.Error)
                .Select(diagnostic => diagnostic.Id + ": " + diagnostic.GetMessage())
                .ToArray();
        }

        // ------------------------------------------------------------------------------------------
        // Assertions
        // ------------------------------------------------------------------------------------------

        private static AnalyzerDiagnostic AssertSingleDiagnostic(
            IReadOnlyList<AnalyzerDiagnostic> diagnostics,
            string code,
            string because)
        {
            AnalyzerDiagnostic[] matches = diagnostics
                .Where(diagnostic => string.Equals(diagnostic.Code, code, StringComparison.Ordinal))
                .ToArray();

            Assert.That(matches.Length, Is.EqualTo(1),
                because + "\nExpected exactly one " + code + ", got " + matches.Length +
                ".\nEvery diagnostic this fixture produced:\n" + Render(diagnostics));

            return matches[0];
        }

        private static void AssertNoDiagnostic(
            IReadOnlyList<AnalyzerDiagnostic> diagnostics,
            string code,
            string because)
        {
            string[] codes = diagnostics.Select(diagnostic => diagnostic.Code).ToArray();

            Assert.That(codes, Does.Not.Contain(code),
                because + "\nEvery diagnostic this fixture produced:\n" + Render(diagnostics));
        }

        /// <summary>
        /// Asserts that no diagnostic at all points into the given file.
        ///
        /// Deliberately blind to the code: the failure this guards is a report landing in a file the
        /// analyzed type does not own, and which analyzer id is stamped on it changes nothing about that.
        /// </summary>
        private static void AssertNothingAnchoredIn(
            IReadOnlyList<AnalyzerDiagnostic> diagnostics,
            string path,
            string because)
        {
            AnalyzerDiagnostic[] anchored = diagnostics
                .Where(diagnostic => string.Equals(FileOf(diagnostic), path, StringComparison.Ordinal))
                .ToArray();

            Assert.That(anchored, Is.Empty,
                because + $"\n{anchored.Length} diagnostic(s) point into '{path}'." +
                "\nEvery diagnostic this fixture produced:\n" + Render(diagnostics));
        }

        private static void AssertAnchoredIn(AnalyzerDiagnostic diagnostic, string path, string because)
        {
            Assert.That(FileOf(diagnostic), Is.EqualTo(path),
                because + "\nThe diagnostic was: " + Describe(diagnostic));
        }

        /// <summary>
        /// The file a diagnostic points into, or a readable stand-in. Location.None and a location with no
        /// tree both mean "this report cannot be navigated to", which is a distinct failure from pointing
        /// at the wrong file and has to stay distinguishable in the message.
        /// </summary>
        private static string FileOf(AnalyzerDiagnostic diagnostic)
        {
            SyntaxTree tree = diagnostic?.Location?.SourceTree;

            if (tree != null)
                return tree.FilePath;

            return diagnostic?.Location == null || diagnostic.Location == Location.None
                ? "<no location>"
                : diagnostic.Location.GetLineSpan().Path;
        }

        private static string Render(IReadOnlyList<AnalyzerDiagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
                return "  (none)";

            return string.Join("\n", diagnostics.Select(diagnostic => "  " + Describe(diagnostic)));
        }

        private static string Describe(AnalyzerDiagnostic diagnostic)
        {
            Location location = diagnostic.Location;

            string line = location != null && location != Location.None
                ? ":" + (location.GetLineSpan().StartLinePosition.Line + 1)
                : string.Empty;

            return $"[{diagnostic.Severity}] {FileOf(diagnostic)}{line} {diagnostic.GetFormattedMessage()}";
        }

        // ------------------------------------------------------------------------------------------
        // Symbol readers
        // ------------------------------------------------------------------------------------------

        private static FieldSymbol Field(BoundFixture fixture, string fieldName)
        {
            FieldSymbol field = fixture.Type.FieldSymbols
                .FirstOrDefault(candidate => candidate != null && candidate.Name == fieldName);

            Assert.That(field, Is.Not.Null, $"Bound behaviour has no field symbol named '{fieldName}'.");

            return field;
        }

        private static SyncAttribute SyncAttributeOf(BoundFixture fixture, string fieldName)
        {
            SyncAttribute attribute = Field(fixture, fieldName).GetAttribute<SyncAttribute>();

            Assert.That(attribute, Is.Not.Null,
                $"The compiler could not reconstruct the [Sync] attribute on '{fieldName}'. That is the " +
                "silent-outage failure: a construction that throws drops the attribute, and the field then " +
                "reads as not synced at all while the compile succeeds.");

            return attribute;
        }

        private static Authority AuthorityOf(BoundFixture fixture, string fieldName)
        {
            return SyncAttributeOf(fixture, fieldName).Authority;
        }

        private static Assembly GetAssemblyByName(string assemblyName)
        {
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(candidate => candidate.GetName().Name == assemblyName);

            if (assembly == null)
            {
                Assert.Fail($"Assembly '{assemblyName}' is not loaded.");
            }

            return assembly;
        }
    }
}
