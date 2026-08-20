using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine.TestTools;
using UdonSharp;
using UdonSharp.Compiler;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Verifies sync as Udon: against the emitted program, its sync metadata, its UASM, and a real
    /// owner-to-remote round trip across two independent Udon heaps.
    ///
    /// Every assertion here is on ground truth taken from the compiled program rather than from source.
    /// That distinction is the whole point: a field can look synced in C# and be absent from the program,
    /// which is exactly what happened when a failed code generator silently stripped CE's glue and the
    /// compile still reported success.
    /// </summary>
    public class UdonSyncVerificationTests
    {
        private UdonSyncHarness.RuntimeBootstrap _bootstrap;

        [SetUp]
        public void SetUp()
        {
            _bootstrap = new UdonSyncHarness.RuntimeBootstrap();
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            _bootstrap?.Dispose();
            _bootstrap = null;
        }

        // -----------------------------------------------------------------------------------------
        // The premise the whole harness rests on
        // -----------------------------------------------------------------------------------------

        [Test]
        public void TwoBehavioursOnOneProgramAsset_GetIndependentHeaps()
        {
            UdonSharpProgramAsset asset = FirstAssetWithSyncedState();

            using (var pair = UdonSyncHarness.CreatePair(asset))
            {
                var ownerProgram = pair.ProgramOf(pair.Owner);
                var remoteProgram = pair.ProgramOf(pair.Remote);

                Assert.That(ownerProgram, Is.Not.Null, "owner program failed to load");
                Assert.That(remoteProgram, Is.Not.Null, "remote program failed to load");

                Assert.That(ReferenceEquals(ownerProgram.Heap, remoteProgram.Heap), Is.False,
                    "Owner and remote share one Udon heap. Every round-trip assertion in this file would " +
                    "then be a tautology that passes even when nothing is transmitted. This breaks if the " +
                    "SDK starts caching deserialized programs outside VRC_CLIENT.");
            }
        }

        [Test]
        public void GetRealProgram_IsShared_AndMustNotBeUsedForAPair()
        {
            UdonSharpProgramAsset asset = FirstAssetWithSyncedState();

            Assert.That(ReferenceEquals(asset.GetRealProgram(), asset.GetRealProgram()), Is.True,
                "GetRealProgram() is documented as the shared read-only instance. If it ever returns " +
                "fresh instances the harness guidance in UdonSyncHarness should be revisited.");
        }

        // -----------------------------------------------------------------------------------------
        // Sync metadata is the authoritative statement of what the program will sync
        // -----------------------------------------------------------------------------------------

        [Test]
        public void EveryCompiledProgram_HasSelfConsistentSyncMetadata()
        {
            // The cheapest broad win available: one sweep over every program in the project. This single
            // test would have caught the project-wide loss of CE codegen, because a stripped [SyncOnJoin]
            // payload disappears from the sync metadata while the compile still reports success.
            CompileAll();

            var problems = new List<string>();

            foreach (UdonSharpProgramAsset asset in UdonSharpProgramAsset.GetAllUdonSharpPrograms())
            {
                if (asset == null || asset.SerializedProgramAsset == null)
                    continue;

                var program = asset.SerializedProgramAsset.RetrieveProgram();
                if (program?.SyncMetadataTable == null)
                    continue;

                List<string> synced = program.SyncMetadataTable.GetAllSyncMetadata().Select(m => m.Name).ToList();
                Dictionary<string, string> uasmTags =
                    UdonSyncHarness.ParseSyncTags(UdonSharpEditorCache.Instance.GetUASMStr(asset));

                // (h) The UASM .sync tags and the sync metadata table are two independent renderings of
                // the same fact and must agree exactly.
                var onlyInMetadata = synced.Except(uasmTags.Keys).ToList();
                var onlyInUasm = uasmTags.Keys.Except(synced).ToList();

                if (onlyInMetadata.Count > 0)
                    problems.Add($"{asset.name}: in sync metadata but not in UASM .sync tags: {string.Join(", ", onlyInMetadata)}");
                if (onlyInUasm.Count > 0)
                    problems.Add($"{asset.name}: has UASM .sync tags absent from sync metadata: {string.Join(", ", onlyInUasm)}");

                // A behaviour that declares Manual sync and syncs nothing cannot transmit anything, no
                // matter how many times it calls RequestSerialization.
                if (asset.behaviourSyncMode == BehaviourSyncMode.Manual && synced.Count == 0)
                    problems.Add($"{asset.name}: BehaviourSyncMode.Manual but zero synced variables.");

                // Continuous sync has no array support at all, so a synced array there never syncs.
                if (asset.behaviourSyncMode == BehaviourSyncMode.Continuous || asset.behaviourSyncMode == BehaviourSyncMode.Any)
                {
                    foreach (string name in synced)
                    {
                        object value = ReadHeap(program, name);
                        if (value is Array)
                            problems.Add($"{asset.name}: synced array '{name}' on a {asset.behaviourSyncMode} behaviour never syncs.");
                    }
                }
            }

            Assert.That(problems, Is.Empty, "Sync metadata inconsistencies:\n" + string.Join("\n", problems));
        }

        [Test]
        public void SyncOnJoinBehaviour_IsManual_AndItsPayloadIsAnUdonSyncedVariable()
        {
            CompileAll();

            var withPayload = UdonSharpProgramAsset.GetAllUdonSharpPrograms()
                .Where(a => a != null && a.SerializedProgramAsset != null)
                .Select(a => new { Asset = a, Program = a.SerializedProgramAsset.RetrieveProgram() })
                .Where(x => x.Program?.SyncMetadataTable != null)
                .Where(x => x.Program.SyncMetadataTable.GetAllSyncMetadata()
                    .Any(m => m.Name.StartsWith("_CE_SyncOnJoinPayload", StringComparison.Ordinal)))
                .ToList();

            Assert.That(withPayload, Is.Not.Empty,
                "No compiled program carries a _CE_SyncOnJoinPayload synced variable. Either the showcase " +
                "stopped using [SyncOnJoin], or the glue emitter silently produced nothing -- which is the " +
                "exact shipped failure this suite exists to catch.");

            foreach (var entry in withPayload)
            {
                Assert.That(entry.Asset.behaviourSyncMode, Is.EqualTo(BehaviourSyncMode.Manual),
                    $"{entry.Asset.name} carries a [SyncOnJoin] payload but resolved to " +
                    $"{entry.Asset.behaviourSyncMode}. The payload is a synced string, which Continuous " +
                    "sync cannot carry within its ~200 byte budget.");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Owner -> remote round trip, under both transports
        // -----------------------------------------------------------------------------------------

        [Test]
        public void OwnerToRemote_RoundTripsEverySyncedVariable_UnderBothTransports()
        {
            CompileAll();

            var transports = new List<UdonSyncHarness.ITransport> { new UdonSyncHarness.HeapCopyTransport() };
            if (UdonSyncHarness.ClientSimCodecTransport.IsAvailable)
                transports.Add(new UdonSyncHarness.ClientSimCodecTransport());

            UdonSharpProgramAsset asset = FirstAssetWithSyncedState();
            var failures = new List<string>();

            // The pair is a bare program with no scene wiring: its serialized object references are all
            // null, so a behaviour whose handler touches them logs "OnDeserialization event failed". That
            // is the behaviour's own scene dependency, not a transport fault, and this test is about
            // transport -- the assertions below read program variables directly. Letting Udon's event
            // logging fail the test would make the suite untrustworthy for the thing it does measure.
            LogAssert.ignoreFailingMessages = true;

            foreach (UdonSyncHarness.ITransport transport in transports)
            {
                using (var pair = UdonSyncHarness.CreatePair(asset))
                {
                    IReadOnlyList<string> synced = pair.SyncedVariableNames();
                    Assert.That(synced, Is.Not.Empty, $"{asset.name} reported no synced variables.");

                    // (d) Author state on the owner, then run the real pre-serialization event.
                    foreach (string name in synced)
                        MutateOwnerValue(pair, name);

                    pair.Owner.OnPreSerialization();

                    int bytes = transport.Send(pair);

                    // (e) Deliver, then run the real deserialization event on the remote.
                    pair.Remote.OnDeserialization(new DeserializationResult());

                    int budget = UdonSyncHarness.BudgetFor(pair.Owner.SyncMethod);
                    Assert.That(bytes, Is.LessThanOrEqualTo(budget),
                        $"{asset.name} encodes ~{bytes} bytes but its {pair.Owner.SyncMethod} budget is {budget}.");

                    foreach (string name in synced)
                    {
                        object ownerValue = pair.Owner.GetProgramVariable(name);
                        object remoteValue = pair.Remote.GetProgramVariable(name);

                        if (!ValuesMatch(ownerValue, remoteValue))
                            failures.Add($"[{transport.Name}] {asset.name}.{name}: owner={Describe(ownerValue)} remote={Describe(remoteValue)}");
                    }
                }
            }

            // A codec-only failure is a simulator limitation; a heap-copy failure is a CE defect. Keeping
            // them distinguishable is what stops this suite accumulating tolerated failures.
            var strict = failures.Where(f => f.StartsWith("[heap-copy]", StringComparison.Ordinal)).ToList();
            Assert.That(strict, Is.Empty, "Round trip lost state:\n" + string.Join("\n", strict));

            var codecOnly = failures.Except(strict).ToList();
            if (codecOnly.Count > 0)
                Assert.Ignore("ClientSim codec limitations only (CE itself round-tripped cleanly):\n" +
                              string.Join("\n", codecOnly) + "\n\nKnown codec losses:\n" + UdonSyncHarness.KnownCodecLimitations);
        }

        [Test]
        public void ManualSyncBehaviour_SignalsThroughRequestSerializationHook()
        {
            // (f) A Manual behaviour transmits nothing until RequestSerialization() is called, so a missing
            // call is a silent failure. The hook makes the call observable from an editor test.
            CompileAll();

            UdonSharpProgramAsset asset = UdonSharpProgramAsset.GetAllUdonSharpPrograms()
                .FirstOrDefault(a => a != null
                                     && a.SerializedProgramAsset != null
                                     && a.behaviourSyncMode == BehaviourSyncMode.Manual);

            if (asset == null)
                Assert.Ignore("No Manual-sync program asset in the project.");

            var requested = new List<UdonBehaviour>();
            Action<UdonBehaviour> hook = b => requested.Add(b);

            UdonBehaviour.RequestSerializationHook += hook;
            try
            {
                using (var pair = UdonSyncHarness.CreatePair(asset))
                {
                    pair.Owner.RequestSerialization();

                    Assert.That(requested, Does.Contain(pair.Owner),
                        "RequestSerializationHook did not observe the call. Without this hook a missing " +
                        "RequestSerialization() -- the defect that makes a Manual behaviour never transmit -- " +
                        "cannot be detected from the editor.");
                }
            }
            finally
            {
                UdonBehaviour.RequestSerializationHook -= hook;
            }
        }

        [Test]
        public void CompiledUasm_UsesRealSyncTags_NotAnIncidentalSubstring()
        {
            CompileAll();

            UdonSharpProgramAsset asset = FirstAssetWithSyncedState();
            string uasm = UdonSharpEditorCache.Instance.GetUASMStr(asset);

            Assert.That(uasm, Is.Not.Null.And.Not.Empty, $"No UASM cached for {asset.name}.");

            Dictionary<string, string> tags = UdonSyncHarness.ParseSyncTags(uasm);

            Assert.That(tags, Is.Not.Empty,
                $"{asset.name} has synced variables but no parseable '.sync <symbol>, <mode>' tags. An " +
                "assertion like Does.Contain(\"sync\") would have passed here on any program with an " +
                "OnDeserialization entry point.");

            var expected = asset.SerializedProgramAsset.RetrieveProgram()
                .SyncMetadataTable.GetAllSyncMetadata().Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal);

            CollectionAssert.AreEqual(expected, tags.Keys.OrderBy(n => n, StringComparer.Ordinal),
                "The UASM .sync tags and the sync metadata table disagree about what this program syncs.");

            foreach (var tag in tags)
            {
                if (asset.behaviourSyncMode != BehaviourSyncMode.Manual)
                    continue;

                Assert.That(tag.Value, Is.EqualTo("none"),
                    $"{asset.name}.{tag.Key} is tagged '{tag.Value}' on a Manual behaviour. VRChat only " +
                    "supports linear/smooth interpolation under Continuous sync.");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// A <c>[SyncOnJoin]</c> payload must actually be APPLIED when it arrives through the real
        /// <c>OnDeserialization</c> event -- not merely be applicable when the generated trigger is called
        /// by hand.
        ///
        /// This exists because that distinction hid a total failure of the feature. Serialization produced
        /// a perfectly correct payload, and invoking <c>_CE_SyncOnJoin_Deserialize_Trigger</c> directly
        /// applied it correctly, so every check short of this one passed -- while delivery through
        /// <c>OnDeserialization</c> did nothing at all, because the glue call was never injected into the
        /// handler the author had already written. Every behaviour in the showcase that carries a payload
        /// declares its own handler, so the feature was inert in exactly the common case.
        ///
        /// The round trip below therefore goes through the EVENT, never the trigger.
        ///
        /// Every group is checked, not just the first payload found. A behaviour that splits its state with
        /// <c>SyncOnJoin(Group = ...)</c> has one payload, one applied-guard and one apply call per group,
        /// and a group left out of the handler is inert in exactly the same invisible way.
        /// </summary>
        [Test]
        public void SyncOnJoinPayload_IsAppliedThroughTheRealDeserializationEvent()
        {
            CompileAll();

            var carriers = UdonSharpProgramAsset.GetAllUdonSharpPrograms()
                .Where(a => a != null && a.SerializedProgramAsset != null)
                .Select(a => new { Asset = a, Program = a.SerializedProgramAsset.RetrieveProgram() })
                .Where(x => x.Program?.SyncMetadataTable != null)
                .Where(x => x.Program.SyncMetadataTable.GetAllSyncMetadata()
                    .Any(m => m.Name.StartsWith("_CE_SyncOnJoinPayload", StringComparison.Ordinal)))
                .Select(x => x.Asset)
                .ToList();

            Assert.That(carriers, Is.Not.Empty, "No compiled program carries a [SyncOnJoin] payload.");

            LogAssert.ignoreFailingMessages = true;
            var failures = new List<string>();

            foreach (UdonSharpProgramAsset asset in carriers)
            {
                using (var pair = UdonSyncHarness.CreatePair(asset))
                {
                    // One entry per group. The suffix is empty for the default group, and every generated
                    // member of a group carries the same one.
                    List<string> groupSuffixes = pair.SyncedVariableNames()
                        .Where(n => n.StartsWith(PayloadPrefix, StringComparison.Ordinal))
                        .Select(n => n.Substring(PayloadPrefix.Length))
                        .ToList();

                    var delivered = new Dictionary<string, string>(StringComparer.Ordinal);

                    foreach (string suffix in groupSuffixes)
                    {
                        // Capture on the owner using the generated capture event for that group.
                        pair.Owner.SendCustomEvent("_CE_SyncOnJoin_Serialize" + suffix);
                        string payload = pair.Owner.GetProgramVariable(PayloadPrefix + suffix) as string;

                        if (string.IsNullOrEmpty(payload) || payload == "{}")
                        {
                            failures.Add($"{asset.name}{DescribeGroup(suffix)}: capture produced an empty payload ('{payload}'), so there is nothing to deliver.");
                            continue;
                        }

                        pair.Remote.SetProgramVariable(PayloadPrefix + suffix, payload);
                        delivered[suffix] = payload;
                    }

                    // Apply the way VRChat does: through the event, not the trigger. One delivery carries
                    // whatever changed, so every group's apply call has to be in the same handler.
                    pair.Remote.OnDeserialization(new DeserializationResult());

                    foreach (KeyValuePair<string, string> group in delivered)
                    {
                        object appliedGuard = pair.Remote.GetProgramVariable("_CE_SyncOnJoinApplied" + group.Key);

                        if (!group.Value.Equals(appliedGuard as string, StringComparison.Ordinal))
                        {
                            failures.Add(
                                $"{asset.name}{DescribeGroup(group.Key)}: OnDeserialization did not apply the payload. " +
                                $"Sent '{group.Value}', applied-guard is '{appliedGuard ?? "<null>"}'. The generated " +
                                "apply call is missing from the deserialization handler, so late joiners receive nothing.");
                        }
                    }
                }
            }

            Assert.That(failures, Is.Empty, string.Join("\n", failures));
        }

        /// <summary>Base name every generated [SyncOnJoin] payload starts with; a group appends its id.</summary>
        private const string PayloadPrefix = "_CE_SyncOnJoinPayload";

        private static string DescribeGroup(string groupSuffix)
        {
            return groupSuffix.Length == 0 ? "" : $" (group '{groupSuffix.Substring(1)}')";
        }

        private static void CompileAll()
        {
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });

            var errors = UdonSharpEditorCache.Instance.LastCompileDiagnostics?
                .Where(d => d.severity == DiagnosticSeverity.Error)
                .Select(d => d.message)
                .ToArray() ?? Array.Empty<string>();

            Assert.That(errors, Is.Empty, "Project failed to compile:\n" + string.Join("\n", errors));
        }

        private static UdonSharpProgramAsset FirstAssetWithSyncedState()
        {
            UdonSharpProgramAsset asset = UdonSharpProgramAsset.GetAllUdonSharpPrograms()
                .Where(a => a != null && a.SerializedProgramAsset != null)
                .FirstOrDefault(a =>
                {
                    var program = a.SerializedProgramAsset.RetrieveProgram();
                    return program?.SyncMetadataTable != null && program.SyncMetadataTable.GetAllSyncMetadata().Any();
                });

            if (asset == null)
                Assert.Ignore("No compiled program in this project has synced state.");

            return asset;
        }

        private static object ReadHeap(VRC.Udon.Common.Interfaces.IUdonProgram program, string symbol)
        {
            try { return program.Heap.GetHeapVariable(program.SymbolTable.GetAddressFromSymbol(symbol)); }
            catch { return null; }
        }

        /// <summary>Writes a value distinguishable from the compiled default, so a no-op transport fails.</summary>
        private static void MutateOwnerValue(UdonSyncHarness.BehaviourPair pair, string name)
        {
            object current = pair.Owner.GetProgramVariable(name);

            switch (current)
            {
                case bool b: pair.Owner.SetProgramVariable(name, !b); return;
                case int i: pair.Owner.SetProgramVariable(name, i + 4242); return;
                case float f: pair.Owner.SetProgramVariable(name, f + 13.5f); return;
                case double d: pair.Owner.SetProgramVariable(name, d + 13.5d); return;
                case string s: pair.Owner.SetProgramVariable(name, (s ?? string.Empty) + "ce-harness"); return;
                case bool[] ba: pair.Owner.SetProgramVariable(name, ba.Select(v => !v).ToArray()); return;
                case int[] ia: pair.Owner.SetProgramVariable(name, ia.Select(v => v + 7).ToArray()); return;
                case string[] sa: pair.Owner.SetProgramVariable(name, sa.Select((v, ix) => "ce" + ix).ToArray()); return;
            }

            // Unknown type: leave it alone rather than write something the program cannot hold.
        }

        private static bool ValuesMatch(object a, object b)
        {
            if (a == null || b == null)
                return ReferenceEquals(a, b);

            if (a is Array arrayA && b is Array arrayB)
            {
                if (arrayA.Length != arrayB.Length)
                    return false;

                for (int i = 0; i < arrayA.Length; i++)
                    if (!ValuesMatch(arrayA.GetValue(i), arrayB.GetValue(i)))
                        return false;

                return true;
            }

            return Equals(a, b);
        }

        private static string Describe(object value)
        {
            if (value == null) return "null";
            if (value is Array array) return $"{value.GetType().Name}[{array.Length}]";
            return value.ToString();
        }
    }
}
