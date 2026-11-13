using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UdonSharp.CE.Editor.Analyzers;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// CE0003 and CE0010 read a behaviour's sync mode -- and CE0003 its synced payload -- from the whole
    /// class chain, not from the analysed class alone.
    ///
    /// UdonSharp applies a mode declared on a base class to every subclass, and emits each base class's
    /// synced fields into the subclass's program. Both analyzers used to read only the attribute on the
    /// type they were handed, so a Continuous payload over the limit, or interpolation on a Manual
    /// behaviour, went unreported whenever the mode was declared one class up; and CE0003 summed only the
    /// subclass's own fields even when the mode was its own.
    /// </summary>
    public class CEInheritedSyncModeAnalyzerTests
    {
        private const string PayloadSizeAnalyzer = "UdonSharp.CE.Editor.Analyzers.SyncPayloadSizeAnalyzer";
        private const string SyncFieldAnalyzer = "UdonSharp.CE.Editor.Analyzers.SyncFieldAnalyzer";

        [TearDown]
        public void TearDown()
        {
            new CENetCodegenRegressionTests().UnregisterFixtureAssemblies();
        }

        [Test]
        public void PayloadSize_ContinuousDeclaredOnTheBase_IsCheckedOnTheSubclass()
        {
            // 15 doubles in each class: 240 bytes on the wire for the subclass, over the 200-byte limit.
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = CENetCodegenRegressionTests.RunAnalyzer(PayloadSizeAnalyzer, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class InheritedPayloadBase : UdonSharpBehaviour
{
" + Doubles("a", 15) + @"
}

public class InheritedPayloadSubclass : InheritedPayloadBase
{
" + Doubles("b", 15) + @"
}", "InheritedPayloadSubclass");

            Assert.That(diagnostics.Any(d => d.Code == "CE0003"), Is.True,
                "The subclass is Continuous through its base and carries 240 bytes, but CE0003 said nothing.\n" +
                Render(diagnostics));
        }

        [Test]
        public void PayloadSize_SubclassPayload_IncludesTheBasesSyncedFields()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = CENetCodegenRegressionTests.RunAnalyzer(PayloadSizeAnalyzer, @"
using UdonSharp;

public abstract class SummedPayloadBase : UdonSharpBehaviour
{
" + Doubles("a", 15) + @"
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class SummedPayloadSubclass : SummedPayloadBase
{
" + Doubles("b", 15) + @"
}", "SummedPayloadSubclass");

            Assert.That(diagnostics.Any(d => d.Code == "CE0003"), Is.True,
                "The base's 120 bytes are serialized with the subclass's 120; counting only the subclass's " +
                "own fields under-reports the payload.\n" + Render(diagnostics));
        }

        [Test]
        public void PayloadSize_SmallInheritedPayload_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = CENetCodegenRegressionTests.RunAnalyzer(PayloadSizeAnalyzer, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class SmallPayloadBase : UdonSharpBehaviour
{
" + Doubles("a", 5) + @"
}

public class SmallPayloadSubclass : SmallPayloadBase
{
" + Doubles("b", 5) + @"
}", "SmallPayloadSubclass");

            Assert.That(diagnostics.Where(d => d.Code == "CE0003"), Is.Empty,
                "80 bytes is well under the Continuous limit.\n" + Render(diagnostics));
        }

        [Test]
        public void Interpolation_OnASubclassWhoseBaseIsManual_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = CENetCodegenRegressionTests.RunAnalyzer(SyncFieldAnalyzer, @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ManualModeBase : UdonSharpBehaviour
{
}

public class ManualModeSubclass : ManualModeBase
{
    [Sync(Authority.Owner, InterpolationMode.Linear)] public Vector3 position;
}", "ManualModeSubclass");

            Assert.That(diagnostics.Any(d => d.Code == "CE0010" && d.Message.Contains("Interpolation")), Is.True,
                "The subclass is Manual through its base, where interpolation does nothing.\n" + Render(diagnostics));
        }

        [Test]
        public void Interpolation_OnASubclassWhoseBaseIsContinuous_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = CENetCodegenRegressionTests.RunAnalyzer(SyncFieldAnalyzer, @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class ContinuousModeBase : UdonSharpBehaviour
{
}

public class ContinuousModeSubclass : ContinuousModeBase
{
    [Sync(Authority.Owner, InterpolationMode.Linear)] public Vector3 position;
}", "ContinuousModeSubclass");

            Assert.That(diagnostics.Where(d => d.Code == "CE0010" && d.Message.Contains("Interpolation")), Is.Empty,
                "Interpolation is what Continuous sync is for.\n" + Render(diagnostics));
        }

        private static string Doubles(string prefix, int count)
        {
            return string.Join("\n", Enumerable.Range(0, count).Select(i => $"    [UdonSynced] public double {prefix}{i};"));
        }

        private static string Render(IReadOnlyList<AnalyzerDiagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
                return "Diagnostics: (none)";

            return "Diagnostics:\n" + string.Join("\n",
                diagnostics.Select(diagnostic => $"  [{diagnostic.Severity}] {diagnostic.GetFormattedMessage()}"));
        }
    }
}
