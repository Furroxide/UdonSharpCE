using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Perf;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEngine;
using VRC.SDK3.UdonNetworkCalling;

namespace CEShowcase.Tests.Editor
{
    public class CEAuditAnalyzerRegressionTests
    {
        [UdonSharp.CE.Persistence.PlayerData("validator_fixture")]
        public class PersistenceQuotaFixture
        {
            [UdonSharp.CE.Persistence.PersistKey("value"), UdonSharp.CE.Persistence.MaxLength(int.MaxValue)]
            public string value;
        }

        [TearDown]
        public void RestoreFixtureAssemblies() => new CENetCodegenRegressionTests().UnregisterFixtureAssemblies();

        [TestCase(10, false)]
        [TestCase(60, true)]
        public void PayloadSize_CountsPrimitiveBytes(int fields, bool shouldWarn)
        {
            string declarations = string.Join("\n", Enumerable.Range(0, fields).Select(i => "[UdonSynced] public int value" + i + ";"));
            var diagnostics = CENetCodegenRegressionTests.RunAnalyzer("UdonSharp.CE.Editor.Analyzers.SyncPayloadSizeAnalyzer",
                "using UdonSharp; [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)] public class PayloadFixture:UdonSharpBehaviour {" + declarations + "}", "PayloadFixture");
            Assert.That(diagnostics.Any(d => d.Code == "CE0003"), Is.EqualTo(shouldWarn));
        }

        [TestCase(true)]
        [TestCase(false)]
        public void SerializationRate_FollowsChangeGateAcrossHelperCalls(bool gated)
        {
            string body = gated ? "if(dirty){ Send(); dirty=false; }" : "Send();";
            var diagnostics = CENetCodegenRegressionTests.RunAnalyzer("UdonSharp.CE.Editor.Analyzers.RequestSerializationRateAnalyzer",
                "using UdonSharp; [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)] public class RateFixture:UdonSharpBehaviour { bool dirty; [UdonSynced] public int value; void Update(){" + body + "} void Send(){RequestSerialization();} }", "RateFixture");
            Assert.That(diagnostics.Any(d => d.Code == "CE0017"), Is.EqualTo(!gated));
        }

        [Test]
        public void PersistenceValidator_DiscoversModelsOutsideTheScene_WithoutSizeOverflow()
        {
            var assemblies = typeof(UdonSharp.Compiler.Udon.CompilerUdonInterface)
                .GetField("_udonSharpAssemblies", BindingFlags.NonPublic | BindingFlags.Static);
            var prior = UdonSharp.Compiler.Udon.CompilerUdonInterface.UdonSharpAssemblies;
            try
            {
                assemblies.SetValue(null, prior.Add(typeof(PersistenceQuotaFixture).Assembly));
                var issues = new UdonSharp.CE.Editor.DevTools.Validation.Validators.PersistenceSizeValidator()
                    .Validate(new UdonSharp.CE.Editor.DevTools.Validation.ValidationContext()).ToList();
                Assert.That(issues.Any(issue => issue.RelatedType == typeof(PersistenceQuotaFixture) &&
                    issue.Severity == UdonSharp.CE.Editor.DevTools.Validation.ValidationSeverity.Warning), Is.True);
            }
            finally { assemblies.SetValue(null, prior); }
        }

        [Test]
        public void PersistenceAnalyzer_FindsUnusedPlainModels()
        {
            var diagnostics = CENetCodegenRegressionTests.RunAnalyzer("UdonSharp.CE.Editor.Analyzers.PlayerDataSizeAnalyzer", @"
                using UdonSharp; using UdonSharp.CE.Persistence;
                [PlayerData(""big"")] public class LargeModel { [PersistKey(""values"")] public int[] values=new int[10000]; }
                public class PersistenceHost:UdonSharpBehaviour {}", "PersistenceHost");
            Assert.That(diagnostics.Any(d => d.Code == "CE0030"), Is.True);
        }
    }

    public class CEAuditDevToolsRegressionTests
    {
        private enum ByteEnum : byte { Value = 1 }
        private enum LongEnum : long { Value = 1 }

        [Test]
        public void UserEnums_MapToTheirNumericStorage()
        {
            Assert.That(UdonSharpUtils.UserTypeToUdonType(typeof(CESyncRuntimeTests.AuditByteEnum)), Is.EqualTo(typeof(byte)));
            Assert.That(UdonSharpUtils.UserTypeToUdonType(typeof(CESyncRuntimeTests.AuditLongEnum)), Is.EqualTo(typeof(long)));
        }

        [Test]
        public void ProfilerAndConsole_PublicApiWorksBeforeStart()
        {
            var host = new GameObject("DevTools fixture");
            try
            {
                var console = host.AddComponent<CEDebugConsole>();
                Assert.DoesNotThrow(() => { console.AddLog("before Start", LogLevel.Info); console.Clear(); });
                var profiler = host.AddComponent<CEProfiler>();
                Assert.DoesNotThrow(() => { profiler.StartProfiling(); profiler.BeginSection("outer"); profiler.BeginSection("inner"); profiler.EndSection(); profiler.EndSection(); profiler.StopProfiling(); profiler.Reset(); });
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void ProgramAsset_RefreshMetadataHook_PreservesNetworkEntries()
        {
            var asset = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            try
            {
                var entries = new[] { new NetworkCallingEntrypointMetadata("Entry", new NetworkCallableAttribute(5)) };
                asset.SetEntrypointMetadata(entries);
                var hook = typeof(UdonSharpProgramAsset).GetMethod("GetLastNetworkCallingMetadata", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(hook.Invoke(asset, null), Is.SameAs(entries));
            }
            finally { UnityEngine.Object.DestroyImmediate(asset); }
        }

        [Test]
        public void LateJoinAnalysis_HandlesBothDeserializationOverloads()
        {
            var simulator = new UdonSharp.CE.Editor.DevTools.NetworkSim.LateJoinSimulator();
            Assert.DoesNotThrow(() => simulator.AnalyzeScene());
        }

        [Test]
        public void GraphWrapper_CallsTheLibraryMethod_WithoutRecursing()
        {
            Type generator = typeof(UdonSharp.CE.Editor.GraphBridge.GraphNodeCodeGenerator);
            var node = new UdonSharp.CE.Editor.GraphBridge.NodeDefinition
            {
                MenuPath = "Noise/Perlin2D",
                ContainingType = typeof(UdonSharp.CE.Procgen.CENoise),
                Method = typeof(UdonSharp.CE.Procgen.CENoise).GetMethod("Perlin2D")
            };
            var sources = (Dictionary<string, string>)generator.GetMethod("BuildNodeWrapperSources", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { new[] { node }, new List<string>() });
            string source = sources.Values.Single();
            var trees = CEAuditCompilerFixture.Sources(source);
            var compilation = CEAuditCompilerFixture.Compile(trees);
            var call = trees[0].GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>().Single();
            var target = (Microsoft.CodeAnalysis.IMethodSymbol)compilation.GetSemanticModel(trees[0]).GetSymbolInfo(call).Symbol;
            Assert.That(target.ContainingType.ToDisplayString(), Is.EqualTo("UdonSharp.CE.Procgen.CENoise"));
        }
    }

    public class CEAuditShowcaseGameplayRegressionTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        private static void Set(object target, string name, object value) => target.GetType().GetField(name, PrivateInstance).SetValue(target, value);
        private static object Get(object target, string name) => target.GetType().GetField(name, PrivateInstance).GetValue(target);
        private static void Call(object target, string name) => target.GetType().GetMethod(name, PrivateInstance).Invoke(target, null);

        [Test]
        public void FlockingBoundary_ReflectsOnlyOutwardVelocity()
        {
            var host = new GameObject("Flocking fixture");
            try
            {
                var flock = host.AddComponent<CEShowcase.Station2_Flocking.FlockingDemo>();
                var world = new CEWorld(2); int id = world.CreateEntity();
                var positions = new Vector3[2]; positions[id] = new Vector3(-9, 0, 0);
                var velocities = new Vector3[2]; velocities[id] = Vector3.right;
                Set(flock, "_world", world); Set(flock, "_positions", positions); Set(flock, "_velocities", velocities);
                Set(flock, "_arenaMin", new Vector3(-10, -10, -10)); Set(flock, "_arenaMax", new Vector3(10, 10, 10));
                Call(flock, "UpdateBoundsSystem"); Assert.That(velocities[id].x, Is.EqualTo(1));
                velocities[id] = Vector3.left;
                Call(flock, "UpdateBoundsSystem"); Assert.That(velocities[id].x, Is.EqualTo(0.5f));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void LeaderboardStressTest_PreservesTheLiveRoster()
        {
            var host = new GameObject("Leaderboard fixture");
            try
            {
                var board = host.AddComponent<CEShowcase.Station3_Leaderboard.LeaderboardManager>();
                Call(board, "InitializeCollections");
                var scores = Get(board, "_playerScores");
                board.GetType().GetMethod("AddPlayer", PrivateInstance).Invoke(board, new object[] { 42, "Visitor" });
                board.OnStressTest();
                Assert.That(Get(board, "_playerScores"), Is.SameAs(scores));
                Assert.That(board.GetPlayerScore(42), Is.EqualTo(0));
                Assert.That(Get(board, "_playerCount"), Is.EqualTo(1));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }

        [Test]
        public void UnusedBulletPool_DoesNotAllocateOnStart()
        {
            var host = new GameObject("Bullet pool fixture");
            try
            {
                var pool = host.AddComponent<CEShowcase.Station1_BulletHell.BulletPool>();
                pool.GetType().GetMethod("Start", PrivateInstance)?.Invoke(pool, null);
                Assert.That(Get(pool, "_bulletTransforms"), Is.Null);
                Assert.That(host.transform.childCount, Is.EqualTo(0));
            }
            finally { UnityEngine.Object.DestroyImmediate(host); }
        }
    }
}
