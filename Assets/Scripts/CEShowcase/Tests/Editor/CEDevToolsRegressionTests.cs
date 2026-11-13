using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.DevTools;
using UdonSharp.CE.Editor.DevTools.NetworkSim;
using UdonSharp.CE.Editor.GraphBridge;
using UdonSharp.CE.Editor.Inspector;
using UdonSharp.CE.Editor.Optimizers;
using UdonSharp.CE.GraphBridge;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UnityEditor;
using UnityEngine;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Regression tests for the developer tooling: the runtime DevTools behaviours, the network and
    /// late-join simulators, the CE inspector, the optimization report window and the GraphBridge
    /// generators. The calls-before-Start case is in CEAuditDevToolsRegressionTests, in
    /// CEAuditEditorGameplayRegressionTests.cs.
    /// </summary>
    public class CEDevToolsRegressionTests
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private readonly List<GameObject> _createdObjects = new List<GameObject>();
        private readonly List<string> _createdFolders = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
            }

            _createdObjects.Clear();

            foreach (string folder in _createdFolders)
            {
                if (Directory.Exists(folder))
                    Directory.Delete(folder, true);
            }

            _createdFolders.Clear();
        }

        // ------------------------------------------------------------------------------------------
        // CEDebugConsole
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The ring index is taken modulo the buffer length, so a size of 0 typed into the inspector was a
        /// division by zero on the first log, which halts an Udon behaviour for the session.
        /// </summary>
        [Test]
        public void DebugConsole_ZeroCapacity_KeepsTheLatestEntry()
        {
            CEDebugConsole console = CreateComponent<CEDebugConsole>("Console capacity");
            SetField(console, "maxLogEntries", 0);
            SetField(console, "_logBuffer", null);

            console.AddLog("first", LogLevel.Info);
            console.AddLog("second", LogLevel.Info);

            Assert.AreEqual(1, GetField<LogEntry[]>(console, "_logBuffer").Length);
            Assert.AreEqual(1, GetField<int>(console, "_logCount"));
            Assert.AreEqual("second", GetField<LogEntry[]>(console, "_logBuffer")[GetField<int>(console, "_logHead")].Message);
        }

        /// <summary>
        /// The level filter used to drop entries as they arrived as well as when the text was built, so
        /// anything logged while it was raised could never be shown again.
        /// </summary>
        [Test]
        public void DebugConsole_LoweringTheFilter_ShowsEntriesLoggedWhileItWasRaised()
        {
            CEDebugConsole console = CreateComponent<CEDebugConsole>("Console filter");

            console.SetFilter(LogLevel.Warning);
            console.AddLog("quiet detail", LogLevel.Debug);
            console.AddLog("loud problem", LogLevel.Warning);

            Invoke(console, "RefreshDisplay");
            string raised = GetField<string>(console, "_cachedText");
            StringAssert.Contains("loud problem", raised);
            StringAssert.DoesNotContain("quiet detail", raised);

            console.SetFilter(LogLevel.Debug);
            Invoke(console, "RefreshDisplay");
            StringAssert.Contains("quiet detail", GetField<string>(console, "_cachedText"));
        }

        /// <summary>
        /// A uGUI control reaches an UdonSharpBehaviour through UdonBehaviour.SendCustomEvent, which carries
        /// no argument, and U# exports a method with parameters under a generated name, so a Dropdown or
        /// Toggle could never reach OnFilterChanged(int) or OnAutoScrollChanged(bool). The listeners below
        /// do what that wiring does: the control changes, then SendCustomEvent runs the handler by name. The
        /// proxy's SendCustomEvent, like the compiled behaviour, only finds public parameterless methods.
        /// </summary>
        [Test]
        public void DebugConsole_DropdownAndToggleWiredBySendCustomEvent_SetFilterAndAutoScroll()
        {
            CEDebugConsole console = CreateComponent<CEDebugConsole>("Console controls");
            var levels = new List<string> { "Trace", "Debug", "Info", "Warning", "Error" };

            var dropdown = CreateComponent<UnityEngine.UI.Dropdown>("Console filter dropdown");
            dropdown.AddOptions(levels);
            dropdown.onValueChanged.AddListener(_ => console.SendCustomEvent("OnFilterDropdownChanged"));
            SetField(console, "filterDropdown", dropdown);

            var toggle = CreateComponent<UnityEngine.UI.Toggle>("Console auto-scroll toggle");
            toggle.SetIsOnWithoutNotify(true);
            toggle.onValueChanged.AddListener(_ => console.SendCustomEvent("OnAutoScrollToggleChanged"));
            SetField(console, "autoScrollToggle", toggle);

            Assert.AreEqual(LogLevel.Debug, GetField<LogLevel>(console, "minDisplayLevel"), "inspector default");
            Assert.IsTrue(GetField<bool>(console, "autoScroll"), "inspector default");

            dropdown.value = (int)LogLevel.Warning;
            toggle.isOn = false;

            Assert.AreEqual(LogLevel.Warning, GetField<LogLevel>(console, "minDisplayLevel"));
            Assert.IsFalse(GetField<bool>(console, "autoScroll"));

            toggle.isOn = true;
            Assert.IsTrue(GetField<bool>(console, "autoScroll"));

            // With no legacy dropdown assigned, the TextMeshPro one is read.
            var tmpDropdown = CreateComponent<TMPro.TMP_Dropdown>("Console filter TMP dropdown");
            tmpDropdown.AddOptions(levels);
            tmpDropdown.onValueChanged.AddListener(_ => console.SendCustomEvent("OnFilterDropdownChanged"));
            SetField(console, "filterDropdown", null);
            SetField(console, "filterDropdownTMP", tmpDropdown);

            tmpDropdown.value = (int)LogLevel.Error;
            Assert.AreEqual(LogLevel.Error, GetField<LogLevel>(console, "minDisplayLevel"));
        }

        /// <summary>
        /// The controls are optional, so the handlers must be safe to wire, or call, with none assigned.
        /// </summary>
        [Test]
        public void DebugConsole_UiHandlersWithoutControls_LeaveTheSettingsAlone()
        {
            CEDebugConsole console = CreateComponent<CEDebugConsole>("Console without controls");
            console.SetFilter(LogLevel.Info);

            Invoke(console, "OnFilterDropdownChanged");
            Invoke(console, "OnAutoScrollToggleChanged");

            Assert.AreEqual(LogLevel.Info, GetField<LogLevel>(console, "minDisplayLevel"));
            Assert.IsTrue(GetField<bool>(console, "autoScroll"));
        }

        // ------------------------------------------------------------------------------------------
        // CEProfiler
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// One start time and one index were all the profiler kept, so a section begun inside another
        /// overwrote it and the outer section's EndSection found nothing to close.
        /// </summary>
        [Test]
        public void Profiler_NestedSections_CountBothLevels()
        {
            CEProfiler profiler = CreateComponent<CEProfiler>("Profiler nesting");

            profiler.BeginSection("outer");
            profiler.BeginSection("inner");
            profiler.EndSection();
            profiler.EndSection();

            // A section that cannot be timed still takes its slot, so the EndSection that matches it does
            // not close the section around it instead.
            profiler.BeginSection("outer");
            profiler.BeginSection(null);
            profiler.EndSection();
            profiler.EndSection();

            Assert.AreEqual(2, SectionCount(profiler, "outer"));
            Assert.AreEqual(1, SectionCount(profiler, "inner"));
        }

        [Test]
        public void Profiler_ZeroAndNegativeInspectorSizes_DoNotBreakTiming()
        {
            CEProfiler profiler = CreateComponent<CEProfiler>("Profiler sizes");
            SetField(profiler, "sampleCount", 0);
            SetField(profiler, "maxSections", -1);
            SetField(profiler, "_frameTimes", null);

            Assert.DoesNotThrow(() =>
            {
                profiler.BeginSection("anything");
                profiler.EndSection();
                SetField(profiler, "_isRunning", true);
                Invoke(profiler, "Update");
                Invoke(profiler, "Update");
                profiler.GetCurrentFrameTime();
            });

            Assert.AreEqual(1, GetField<float[]>(profiler, "_frameTimes").Length);
            Assert.AreEqual(0, profiler.GetSectionNames().Length);
        }

        // ------------------------------------------------------------------------------------------
        // PerformanceMonitor
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The running frame time started at zero, so the first reading came out high -- 62.7 at a steady
        /// 60 fps -- and Max FPS kept it. Before any reading, Min FPS reported float.MaxValue.
        /// </summary>
        [Test]
        public void PerformanceMonitor_SteadyFrameRate_ReadsAsItself()
        {
            PerformanceMonitor monitor = CreateComponent<PerformanceMonitor>("Monitor");

            Assert.AreEqual(0f, monitor.GetMinFPS(), "No reading has been taken yet.");

            for (int i = 0; i < 40; i++)
            {
                Invoke(monitor, "Sample", 1f / 60f);
            }

            Assert.AreEqual(60f, monitor.GetMaxFPS(), 0.01f);
            Assert.AreEqual(60f, monitor.GetMinFPS(), 0.01f);
            Assert.AreEqual(60f, monitor.GetAvgFPS(), 0.01f);
        }

        // ------------------------------------------------------------------------------------------
        // NetworkSimulator
        // ------------------------------------------------------------------------------------------

        [Test]
        public void NetworkSimulator_AverageLatency_CountsOnlyDeliveredPackets()
        {
            NetworkSimulator simulator = NetworkSimulator.CreateDetached(
                new NetworkConditions { Enabled = true, LatencyMin = 100f, LatencyMax = 100f, PacketLossPercent = 50f },
                7);
            simulator.ClockMs = () => 1000.0;

            for (int i = 0; i < 200; i++)
            {
                simulator.SimulatePacket("p" + i, 16);
            }

            Assert.That(simulator.Stats.DroppedPackets, Is.InRange(1, 199));
            Assert.AreEqual(100.0, simulator.Stats.AverageLatencyMs, 1e-6);
        }

        /// <summary>
        /// Each packet draws its own latency, so with out-of-order delivery switched off packets still
        /// arrived out of order while the out-of-order count read zero.
        /// </summary>
        [Test]
        public void NetworkSimulator_InOrderProfile_DeliversInSendOrder()
        {
            var delivered = RunSimulatedTraffic(false, out NetworkSimulator simulator);

            CollectionAssert.AreEqual(Enumerable.Range(0, 300).Select(i => "p" + i).ToList(), delivered.Select(e => e.PacketName).ToList());
            Assert.AreEqual(0, simulator.Stats.OutOfOrderPackets);
        }

        /// <summary>
        /// A quarter of packets used to be labelled out of order whether or not they overtook anything.
        /// The label now means the packet arrived ahead of one sent before it.
        /// </summary>
        [Test]
        public void NetworkSimulator_OutOfOrderProfile_LabelsOnlyPacketsThatOvertake()
        {
            var delivered = RunSimulatedTraffic(true, out NetworkSimulator simulator);
            var order = delivered.Select(e => int.Parse(e.PacketName.Substring(1))).ToList();
            var labelled = delivered.Where(e => e.EventType == PacketEventType.OutOfOrder).ToList();

            Assert.AreEqual(300, delivered.Count);
            Assert.That(labelled.Count, Is.GreaterThan(0));
            Assert.AreEqual(labelled.Count, simulator.Stats.OutOfOrderPackets);

            foreach (var packet in labelled)
            {
                int sent = int.Parse(packet.PacketName.Substring(1));
                int arrived = order.IndexOf(sent);

                Assert.That(order.Skip(arrived + 1).Any(later => later < sent),
                    $"{packet.PacketName} is labelled out of order but no packet sent before it arrived after it.");
            }
        }

        [Test]
        public void NetworkSimulator_LeavesUnityRandomAlone_AndReplaysFromItsSeed()
        {
            UnityEngine.Random.State saved = UnityEngine.Random.state;

            try
            {
                UnityEngine.Random.InitState(1234);
                float expected = UnityEngine.Random.value;
                UnityEngine.Random.InitState(1234);

                List<string> first = RunSeeded(42);
                List<string> second = RunSeeded(42);

                Assert.AreEqual(expected, UnityEngine.Random.value, "The simulator drew from UnityEngine.Random.");
                CollectionAssert.AreEqual(first, second);
            }
            finally
            {
                UnityEngine.Random.state = saved;
            }
        }

        /// <summary>
        /// Delivery used to be scheduled on (float)timeSinceStartup * 1000, which after ten hours of
        /// editor uptime only resolves 4 ms -- a 1.5 ms packet was due the moment it was sent.
        /// </summary>
        [Test]
        public void NetworkSimulator_AfterHoursOfUptime_StillSchedulesToTheMillisecond()
        {
            double now = 36000000.0;
            NetworkSimulator simulator = NetworkSimulator.CreateDetached(
                new NetworkConditions { Enabled = true, LatencyMin = 1.5f, LatencyMax = 1.5f }, 1);
            simulator.ClockMs = () => now;

            var delivered = new List<PacketEvent>();
            simulator.OnPacketProcessed += delivered.Add;

            simulator.SimulatePacket("late", 8);

            now += 1.0;
            simulator.ProcessDelayedPackets();
            Assert.AreEqual(0, delivered.Count, "Delivered 0.5 ms early.");

            now += 0.5;
            simulator.ProcessDelayedPackets();
            Assert.AreEqual(1, delivered.Count);
        }

        private static List<PacketEvent> RunSimulatedTraffic(bool outOfOrder, out NetworkSimulator simulator)
        {
            double now = 1000.0;
            simulator = NetworkSimulator.CreateDetached(
                new NetworkConditions
                {
                    Enabled = true,
                    LatencyMin = 10f,
                    LatencyMax = 500f,
                    Jitter = 50f,
                    SimulateOutOfOrder = outOfOrder
                },
                12345);
            simulator.ClockMs = () => now;

            var delivered = new List<PacketEvent>();
            simulator.OnPacketProcessed += delivered.Add;

            for (int i = 0; i < 300; i++)
            {
                now += 1.0;
                simulator.SimulatePacket("p" + i, 32);
            }

            now += 1000000.0;
            simulator.ProcessDelayedPackets();
            return delivered;
        }

        private static List<string> RunSeeded(int seed)
        {
            NetworkSimulator simulator = NetworkSimulator.CreateDetached(
                new NetworkConditions
                {
                    Enabled = true,
                    LatencyMin = 10f,
                    LatencyMax = 200f,
                    Jitter = 20f,
                    PacketLossPercent = 30f,
                    DuplicatePercent = 10f
                },
                seed);
            simulator.ClockMs = () => 0.0;

            var results = new List<string>();
            for (int i = 0; i < 100; i++)
            {
                PacketResult result = simulator.SimulatePacket("p" + i, 16);
                results.Add($"{result.Dropped}/{result.Duplicated}/{result.DelayMs:R}");
            }

            return results;
        }

        // ------------------------------------------------------------------------------------------
        // LateJoinSimulator
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// UdonSharpBehaviour declares OnDeserialization twice, so the name-only lookup threw
        /// AmbiguousMatchException for every behaviour; OnPlayerJoined, declared once, came back as the
        /// empty base virtual for behaviours that never wrote one.
        /// </summary>
        [Test]
        public void LateJoin_FindUserOverride_SeesOnlyTheBehavioursOwnHandlers()
        {
            Type collectible = typeof(CEShowcase.Station5_Persistence.CollectibleItem);

            MethodInfo own = LateJoinSimulator.FindUserOverride(collectible, "OnDeserialization", 0);
            Assert.IsNotNull(own);
            Assert.AreEqual(collectible, own.DeclaringType);

            Assert.IsNull(LateJoinSimulator.FindUserOverride(collectible, "OnPlayerJoined", 1));
            Assert.IsNull(LateJoinSimulator.FindUserOverride(collectible, "OnDeserialization", 1));

            Assert.IsNotNull(LateJoinSimulator.FindUserOverride(typeof(CEToolingResultHandlerFixture), "OnDeserialization", 1));
            Assert.IsNull(LateJoinSimulator.FindUserOverride(typeof(CEToolingResultHandlerFixture), "OnDeserialization", 0));

            MethodInfo inherited = LateJoinSimulator.FindUserOverride(typeof(CEToolingInheritedHandlerFixture), "OnDeserialization", 0);
            Assert.IsNotNull(inherited);
            Assert.AreEqual(typeof(CEToolingBaseHandlerFixture), inherited.DeclaringType);
        }

        [Test]
        public void LateJoin_AnalyzeScene_CountsHandlersAndSyncOnJoinFields()
        {
            var simulator = new LateJoinSimulator();
            LateJoinAnalysis before = simulator.AnalyzeScene();

            CreateComponent<CEToolingResultHandlerFixture>("Late join result handler");
            CreateComponent<CEToolingSyncOnJoinFixture>("Late join payload");

            LateJoinAnalysis after = simulator.AnalyzeScene();

            Assert.AreEqual(2, after.TotalBehaviours - before.TotalBehaviours);
            Assert.AreEqual(1, after.OnDeserializationHandlers - before.OnDeserializationHandlers);

            // [SyncOnJoin] sits on a plain field; it used to be looked for only on [UdonSynced] ones.
            Assert.AreEqual(1, after.SyncOnJoinFields - before.SyncOnJoinFields);
            Assert.AreEqual(1, after.TotalSyncedFields - before.TotalSyncedFields);
        }

        /// <summary>
        /// Run outside play mode, the simulation wrote default values into every synced field of every
        /// behaviour in the scene and never put them back.
        /// </summary>
        [Test]
        public void LateJoin_OutsidePlayMode_LeavesTheSceneUntouched()
        {
            var fixture = CreateComponent<CEToolingPrivateSyncedDerivedFixture>("Late join scene data");
            fixture.derivedSynced = 42;
            SetField(fixture, "_baseSynced", 7, typeof(CEToolingPrivateSyncedBaseFixture));

            var simulator = new LateJoinSimulator();
            simulator.CaptureCurrentState();
            LateJoinSimulationResult result = simulator.SimulateLateJoin();

            Assert.IsNull(result);
            Assert.AreEqual(0, simulator.CapturedSnapshots.Count);
            Assert.AreEqual(42, fixture.derivedSynced);
            Assert.AreEqual(7, GetField<int>(fixture, "_baseSynced", typeof(CEToolingPrivateSyncedBaseFixture)));
        }

        [Test]
        public void SyncedFieldScans_IncludePrivateFieldsDeclaredOnBaseClasses()
        {
            var lateJoinFields = LateJoinSimulator.GetInstanceFields(typeof(CEToolingPrivateSyncedDerivedFixture)).Select(f => f.Name).ToList();
            CollectionAssert.Contains(lateJoinFields, "_baseSynced");
            CollectionAssert.Contains(lateJoinFields, "derivedSynced");

            var inspectorFields = ((List<FieldInfo>)typeof(CEBehaviourEditor).GetMethod("GetInstanceFields", AnyStatic)
                .Invoke(null, new object[] { typeof(CEToolingPrivateSyncedDerivedFixture) })).Select(f => f.Name).ToList();
            CollectionAssert.Contains(inspectorFields, "_baseSynced");
            CollectionAssert.Contains(inspectorFields, "derivedSynced");
        }

        // ------------------------------------------------------------------------------------------
        // Bandwidth Analyzer window
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// A synced string with no length is counted as its framing only, and the window printed that as a
        /// plain "4 B" -- as if it were exact. Its rows, and every total that includes one, are marked ">=".
        /// </summary>
        [Test]
        public void BandwidthWindow_MarksSizesThatAreOnlyLowerBounds()
        {
            var analyzer = new UdonSharp.CE.Editor.DevTools.Analysis.BehaviourAnalyzer();
            var unbounded = analyzer.Analyze(typeof(CEToolingUnboundedPayloadFixture));
            var fixedSize = analyzer.Analyze(typeof(CEToolingFixedPayloadFixture));

            Assert.IsTrue(UdonSharp.CE.Editor.DevTools.BandwidthAnalyzerWindow.HasLowerBound(unbounded));
            Assert.IsFalse(UdonSharp.CE.Editor.DevTools.BandwidthAnalyzerWindow.HasLowerBound(fixedSize));

            var world = new UdonSharp.CE.Editor.DevTools.Analysis.WorldAnalysisResult();
            world.BehaviourResults.Add(fixedSize);
            Assert.IsFalse(UdonSharp.CE.Editor.DevTools.BandwidthAnalyzerWindow.HasLowerBound(world));

            world.BehaviourResults.Add(unbounded);
            Assert.IsTrue(UdonSharp.CE.Editor.DevTools.BandwidthAnalyzerWindow.HasLowerBound(world));

            Assert.AreEqual(">=", UdonSharp.CE.Editor.DevTools.BandwidthAnalyzerWindow.FloorMark(true));
            Assert.AreEqual("", UdonSharp.CE.Editor.DevTools.BandwidthAnalyzerWindow.FloorMark(false));
        }

        // ------------------------------------------------------------------------------------------
        // CE inspector
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// UdonSharp's host inspector hands a multi-object selection only to an editor that declares
        /// CanEditMultipleObjects; without it every multi-selection read "Multi-object editing not
        /// supported."
        /// </summary>
        [Test]
        public void CEInspector_AcceptsMultiObjectSelections()
        {
            Assert.IsTrue(typeof(CEBehaviourEditor).IsDefined(typeof(CanEditMultipleObjects), false));
        }

        /// <summary>
        /// ExitGUIException is Unity abandoning a GUI pass on purpose (an object or colour picker opening).
        /// Caught as a failure, it was reported as an inspector error and the fallback inspector was drawn
        /// over a half-built layout.
        /// </summary>
        [Test]
        public void CEInspector_LetsExitGuiPassThrough()
        {
            MethodBody body = typeof(CEBehaviourEditor).GetMethod("OnInspectorGUI", AnyInstance).GetMethodBody();

            Assert.IsTrue(body.ExceptionHandlingClauses.Any(clause =>
                clause.Flags == ExceptionHandlingClauseOptions.Clause && clause.CatchType == typeof(ExitGUIException)));
        }

        /// <summary>
        /// "Use CE Inspector" was an EditorPrefs value nothing read. Which inspector draws a U# behaviour is
        /// UdonSharp's Default Behaviour Editor project setting, and the property now reports that.
        /// </summary>
        [Test]
        public void CEInspector_UseCEInspector_ReportsUdonSharpsDefaultEditorSetting()
        {
            UdonSharpEditor.UdonSharpSettings settings = UdonSharpEditor.UdonSharpSettings.GetSettings();
            string original = settings.defaultBehaviourInterfaceType;

            try
            {
                settings.defaultBehaviourInterfaceType = "";
                Assert.IsFalse(CEInspectorBootstrap.UseCEInspector);

                settings.defaultBehaviourInterfaceType = typeof(CEBehaviourEditor).ToString();
                Assert.IsTrue(CEInspectorBootstrap.UseCEInspector);
            }
            finally
            {
                settings.defaultBehaviourInterfaceType = original;
            }
        }

        // ------------------------------------------------------------------------------------------
        // Optimization report window
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The window enumerated the live optimization context, which the U# compile fills from a worker
        /// thread, so a repaint during a compile threw "Collection was modified" part-way through a layout.
        /// It now draws from a copy.
        /// </summary>
        [Test]
        public void OptimizationReport_DrawsFromACopyOfTheContext()
        {
            var window = ScriptableObject.CreateInstance<OptimizationReportWindow>();

            try
            {
                var context = new OptimizationContext();
                context.RecordOptimization("CEOPT999", "first", null);

                typeof(OptimizationReportWindow).GetMethod("RefreshSnapshot", AnyInstance).Invoke(window, new object[] { context });
                var drawn = (List<OptimizationEntry>)typeof(OptimizationReportWindow).GetField("_entries", AnyInstance).GetValue(window);

                context.RecordOptimization("CEOPT999", "second", null);

                Assert.AreEqual(1, drawn.Count);
                Assert.AreNotSame(context.GetEntries(), drawn);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(window);
            }
        }

        // ------------------------------------------------------------------------------------------
        // GraphBridge
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The node scan ran from [InitializeOnLoadMethod] after every domain reload, walking nearly every
        /// type in the editor.
        /// </summary>
        [Test]
        public void GraphNodes_AreNotScannedOnDomainReload()
        {
            Type integration = typeof(UdonGraphIntegration);

            Assert.IsFalse(integration.IsDefined(typeof(InitializeOnLoadAttribute), false));
            Assert.IsFalse(integration.GetMethods(AnyStatic).Any(m => m.IsDefined(typeof(InitializeOnLoadMethodAttribute), false)));
        }

        /// <summary>
        /// Every way the generator used to write a wrapper that does not compile -- or that compiles and
        /// calls itself -- in one fixture. The global-namespace MathNodes is the documented example: its
        /// wrapper class used to be called MathNodes too, and its unqualified call bound to itself.
        /// </summary>
        [Test]
        public void GraphWrappers_CompileForEveryLegalSignature_AndNeverCallThemselves()
        {
            Assembly fixture = CompileGraphFixture(out byte[] image);
            List<NodeDefinition> nodes = BuildFixtureNodes(fixture);

            foreach (bool perNode in new[] { false, true })
            {
                var warnings = new List<string>();
                Dictionary<string, string> sources = perNode
                    ? GraphNodeCodeGenerator.BuildNodeWrapperSources(nodes, warnings)
                    : GraphNodeCodeGenerator.BuildCategoryWrapperSources(nodes, warnings);

                List<SyntaxTree> trees = sources
                    .Select(source => CSharpSyntaxTree.ParseText(source.Value, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9), source.Key))
                    .ToList();

                var references = new List<MetadataReference>(CompilationContext.GetMetadataReferences())
                {
                    MetadataReference.CreateFromImage(image)
                };

                CSharpCompilation compilation = CSharpCompilation.Create(
                    "CEToolingWrappers_" + Guid.NewGuid().ToString("N"),
                    trees,
                    references,
                    new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

                var errors = compilation.GetDiagnostics()
                    .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.ToString())
                    .ToList();
                Assert.That(errors, Is.Empty, string.Join("\n", errors));

                foreach (SyntaxTree tree in trees)
                {
                    SemanticModel model = compilation.GetSemanticModel(tree);

                    foreach (var invocation in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        ISymbol target = model.GetSymbolInfo(invocation).Symbol;
                        Assert.IsNotNull(target, invocation.ToString());
                        Assert.That(target.ContainingNamespace.ToDisplayString(), Does.Not.StartWith("UdonSharp.CE.Generated"),
                            $"{tree.FilePath} calls itself: {invocation}");
                    }
                }

                // The inherited Open() is one method however many types it is reached through, and the
                // unrelated Window.Open() of the same shape is kept apart by its type's name.
                string doors = sources.Where(source => source.Value.Contains("global::CEToolingGraphFixture.Door.Count")).Select(source => source.Value).FirstOrDefault();
                if (!perNode)
                {
                    Assert.IsNotNull(doors);
                    Assert.AreEqual(1, CountOccurrences(doors, " Open()"));
                    StringAssert.Contains("Window_Open()", doors);
                }

                Assert.That(warnings.Any(w => w.Contains("Constrained")), "A constrained generic method must be skipped with a warning.");
                Assert.That(warnings.Any(w => w.Contains("Echo")), "A method on an open generic type must be skipped with a warning.");
            }
        }

        /// <summary>
        /// Generated files are rewritten only when their text changes, deleted when nothing wants them,
        /// never written when they do not parse, and a file the generator did not write is left alone.
        /// </summary>
        [Test]
        public void GraphWrappers_SyncWritesOnlyChanges_RemovesStaleFiles_AndRefusesUnparsableText()
        {
            Assembly fixture = CompileGraphFixture(out _);
            Dictionary<string, string> sources = GraphNodeCodeGenerator.BuildCategoryWrapperSources(BuildFixtureNodes(fixture), null);
            Assert.That(sources.Count, Is.GreaterThanOrEqualTo(2));

            string folder = CreateTempFolder();
            string userFile = Path.Combine(folder, "Mine.cs");
            File.WriteAllText(userFile, "public static class Mine { }");

            var first = GraphNodeCodeGenerator.SyncGeneratedFiles(folder, sources, true);
            Assert.AreEqual(sources.Count, first.Written);

            var second = GraphNodeCodeGenerator.SyncGeneratedFiles(folder, sources, true);
            Assert.AreEqual(0, second.Written, "Unchanged text must not be rewritten.");
            Assert.AreEqual(sources.Count, second.Unchanged);

            var kept = sources.Take(1).ToDictionary(source => source.Key, source => source.Value);
            var third = GraphNodeCodeGenerator.SyncGeneratedFiles(folder, kept, true);
            Assert.AreEqual(sources.Count - 1, third.Removed.Count);
            Assert.IsTrue(File.Exists(Path.Combine(folder, kept.Keys.Single())));
            Assert.IsTrue(File.Exists(userFile), "A file the generator did not write must never be deleted.");

            string broken = kept.Values.Single().Replace("public static class", "public static class {");
            var refused = GraphNodeCodeGenerator.SyncGeneratedFiles(folder, new Dictionary<string, string> { { "Broken.cs", broken } }, false);
            Assert.AreEqual(1, refused.Refused.Count);
            Assert.IsFalse(File.Exists(Path.Combine(folder, "Broken.cs")));
        }

        /// <summary>
        /// Every [GraphProperty] makes ".../Get" and ".../Set" nodes and pages were named after the last
        /// segment, so a second property's pages overwrote the first's. User text went into MDX, YAML and
        /// JSON unescaped, where a brace or angle bracket breaks a Docusaurus build.
        /// </summary>
        [Test]
        public void GraphDocs_PropertyPagesAreDistinct_AndUserTextIsEscaped()
        {
            var nodes = new List<NodeDefinition>();
            nodes.AddRange(GraphNodeGenerator.CreatePropertyNodes(typeof(CEToolingDocFixture).GetProperty("Health"), new GraphPropertyAttribute("Player/Health"), null));
            nodes.AddRange(GraphNodeGenerator.CreatePropertyNodes(typeof(CEToolingDocFixture).GetProperty("Mana"), new GraphPropertyAttribute("Player/Mana"), null));
            nodes.Add(GraphNodeGenerator.CreateNodeDefinition(
                typeof(CEToolingDocFixture).GetMethod("Describe"),
                new GraphNodeAttribute("Text \"Tools\"/Describe {value}") { Tooltip = "Wraps <b>{value}</b> in \"quotes\"\nand a back\\slash" },
                null));

            string folder = CreateTempFolder();
            int pages = GraphNodeDocGenerator.WriteDocumentation(nodes, folder);

            Assert.AreEqual(5, pages);
            foreach (string page in new[] { "health-get.md", "health-set.md", "mana-get.md", "mana-set.md" })
            {
                Assert.IsTrue(File.Exists(Path.Combine(folder, "player", page)), page);
            }

            string describe = Directory.GetFiles(folder, "describe*.md", SearchOption.AllDirectories).Single();
            string text = File.ReadAllText(describe);

            StringAssert.Contains("# Describe \\{value\\}", text);
            StringAssert.Contains("Wraps \\<b\\>\\{value\\}\\</b\\> in \"quotes\" and a back\\\\slash", text);
            StringAssert.Contains("description: \"Wraps <b>{value}</b> in \\\"quotes\\\" and a back\\\\slash\"", text);

            string category = File.ReadAllText(Path.Combine(Path.GetDirectoryName(describe), "_category_.json"));
            StringAssert.Contains("\"label\": \"Text \\\"Tools\\\"\"", category);
        }

        private static readonly string GraphFixtureSource = @"
using UnityEngine;

public static class MathNodes
{
    public static Vector3 LerpVector3(Vector3 a, Vector3 b, float t) { return Vector3.Lerp(a, b, t); }
    public static float Clamp01(float value) { return value; }
    public static float Clamp01(int value) { return value; }
    public static void SinCos(float angle, out float sin, out float cos) { sin = angle; cos = angle; }
    public static void Swap(ref int a, ref int b) { int t = a; a = b; b = t; }
    public static int ReadOnly(in Vector3 v) { return (int)v.x; }
    public static int Keywords(int @class, int @event, string @string) { return @class; }
    public static T First<T>(T[] array) { return array[0]; }
    public static T Constrained<T>(T value) where T : struct { return value; }
    public static int[][,] Jagged(int[,][] input) { return null; }
    public static System.Collections.Generic.List<int> Generic(System.Collections.Generic.Dictionary<string, int[]> map) { return null; }
    public static int Defaulted(int a = 5, params int[] rest) { return a; }
    public static float Gain { get; set; }
}

// Named exactly as the generator now names the Math category's wrapper class. Only a call written from
// global:: reaches this type from inside UdonSharp.CE.Generated.MathGraphWrappers.
public static class MathGraphWrappers
{
    public static float Twice(float value) { return value * 2f; }
}

namespace CEToolingGraphFixture
{
    public class Door
    {
        public void Open() { }
        public static int Count(Door door) { return 0; }

        public class Hinge
        {
            public enum Side { Left, Right }
            public static Side Flip(Side side) { return side; }
        }
    }

    public class SlidingDoor : Door { }

    public class Window
    {
        public void Open() { }
    }

    public static class Gen<T>
    {
        public static T Echo(T value) { return value; }
    }
}
";

        private static Assembly CompileGraphFixture(out byte[] image)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(GraphFixtureSource, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9));
            CSharpCompilation compilation = CSharpCompilation.Create(
                "CEToolingGraphFixture_" + Guid.NewGuid().ToString("N"),
                new[] { tree },
                CompilationContext.GetMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var stream = new MemoryStream())
            {
                var emitted = compilation.Emit(stream);
                Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
                image = stream.ToArray();
            }

            return Assembly.Load(image);
        }

        /// <summary>
        /// The fixture carries no GraphBridge attributes -- nodes are built here directly -- so no node scan
        /// in the editor can pick its types up after the test has loaded them.
        /// </summary>
        private static List<NodeDefinition> BuildFixtureNodes(Assembly fixture)
        {
            var nodes = new List<NodeDefinition>();

            Type math = fixture.GetType("MathNodes", true);
            foreach (MethodInfo method in math.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName))
            {
                nodes.Add(GraphNodeGenerator.CreateNodeDefinition(method, new GraphNodeAttribute("Math/" + method.Name), null));
            }

            nodes.AddRange(GraphNodeGenerator.CreatePropertyNodes(math.GetProperty("Gain"), new GraphPropertyAttribute("Math/Gain"), null));
            nodes.Add(GraphNodeGenerator.CreateNodeDefinition(fixture.GetType("MathGraphWrappers", true).GetMethod("Twice"), new GraphNodeAttribute("Math/Twice"), null));

            Type door = fixture.GetType("CEToolingGraphFixture.Door", true);
            Type sliding = fixture.GetType("CEToolingGraphFixture.SlidingDoor", true);
            Type window = fixture.GetType("CEToolingGraphFixture.Window", true);
            Type hinge = fixture.GetType("CEToolingGraphFixture.Door+Hinge", true);
            Type gen = fixture.GetType("CEToolingGraphFixture.Gen`1", true);

            nodes.Add(GraphNodeGenerator.CreateNodeDefinition(door.GetMethod("Open"), new GraphNodeAttribute("Doors/Open"), null));
            nodes.Add(GraphNodeGenerator.CreateNodeDefinition(sliding.GetMethod("Open"), new GraphNodeAttribute("Doors/Open"), null));
            nodes.Add(GraphNodeGenerator.CreateNodeDefinition(
                window.GetMethod("Open"),
                new GraphNodeAttribute("Doors/Open") { Tooltip = "Line one\nline two </summary> & <b>bold</b>" },
                null));
            nodes.Add(GraphNodeGenerator.CreateNodeDefinition(door.GetMethod("Count"), new GraphNodeAttribute("Doors/Count"), null));
            nodes.Add(GraphNodeGenerator.CreateNodeDefinition(hinge.GetMethod("Flip"), new GraphNodeAttribute("Doors/Flip"), null));
            nodes.Add(GraphNodeGenerator.CreateNodeDefinition(gen.GetMethod("Echo"), new GraphNodeAttribute("Doors/Echo"), null));

            return nodes;
        }

        // ------------------------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------------------------

        private T CreateComponent<T>(string name) where T : Component
        {
            var go = new GameObject(name);
            _createdObjects.Add(go);
            return go.AddComponent<T>();
        }

        private string CreateTempFolder()
        {
            string folder = Path.Combine(Path.GetTempPath(), "CEToolingRegression_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            _createdFolders.Add(folder);
            return folder;
        }

        private static int SectionCount(CEProfiler profiler, string section)
        {
            int index = Array.IndexOf(profiler.GetSectionNames(), section);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), section);
            return GetField<int[]>(profiler, "_sectionCounts")[index];
        }

        private static int CountOccurrences(string text, string value)
        {
            int count = 0;
            for (int i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            {
                count++;
            }

            return count;
        }

        private static void Invoke(object instance, string methodName, params object[] arguments)
        {
            MethodInfo method = instance.GetType().GetMethod(methodName, AnyInstance);
            if (method == null)
                throw new MissingMethodException(instance.GetType().Name, methodName);

            try
            {
                method.Invoke(instance, arguments.Length == 0 ? null : arguments);
            }
            catch (TargetInvocationException failure) when (failure.InnerException != null)
            {
                throw failure.InnerException;
            }
        }

        private static T GetField<T>(object instance, string fieldName, Type declaringType = null)
        {
            FieldInfo field = (declaringType ?? instance.GetType()).GetField(fieldName, AnyInstance);
            if (field == null)
                throw new MissingFieldException(instance.GetType().Name, fieldName);

            return (T)field.GetValue(instance);
        }

        private static void SetField(object instance, string fieldName, object value, Type declaringType = null)
        {
            FieldInfo field = (declaringType ?? instance.GetType()).GetField(fieldName, AnyInstance);
            if (field == null)
                throw new MissingFieldException(instance.GetType().Name, fieldName);

            field.SetValue(instance, value);
        }
    }

    // Fixture behaviours for the late-join and inspector tests. They are only reflected over or added to
    // a scratch GameObject; UdonSharp never compiles this assembly, so none of them needs a program asset.

    public class CEToolingResultHandlerFixture : UdonSharpBehaviour
    {
        public override void OnDeserialization(VRC.Udon.Common.DeserializationResult result) { }
    }

    public class CEToolingBaseHandlerFixture : UdonSharpBehaviour
    {
        public override void OnDeserialization() { }
    }

    public class CEToolingInheritedHandlerFixture : CEToolingBaseHandlerFixture { }

    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class CEToolingSyncOnJoinFixture : UdonSharpBehaviour
    {
        [SyncOnJoin] public int savedForLateJoiners;
        [UdonSynced] public int synced;
    }

    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class CEToolingPrivateSyncedBaseFixture : UdonSharpBehaviour
    {
        [UdonSynced] private int _baseSynced = 0;

        public int BaseSynced => _baseSynced;
    }

    public class CEToolingPrivateSyncedDerivedFixture : CEToolingPrivateSyncedBaseFixture
    {
        [UdonSynced] public int derivedSynced;
    }

    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class CEToolingUnboundedPayloadFixture : UdonSharpBehaviour
    {
        [UdonSynced] public string label;
        [UdonSynced] public int score;
    }

    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class CEToolingFixedPayloadFixture : UdonSharpBehaviour
    {
        [UdonSynced] public int score;
        [UdonSynced] public Vector3 position;
    }

    public static class CEToolingDocFixture
    {
        public static int Health { get; set; }
        public static int Mana { get; set; }

        public static string Describe(string value) { return value; }
    }
}
