using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using CESyncRuntimeTests;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Net;
using UdonSharp.Serialization;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.Udon;
using VRC.Udon.Common;

namespace CEShowcase.Tests.Editor
{
    public class UdonSharpCESyncPipelineTests
    {
        private readonly List<string> _createdAssetPaths = new List<string>();
        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
            }

            _createdObjects.Clear();

            for (int i = _createdAssetPaths.Count - 1; i >= 0; i--)
            {
                string assetPath = _createdAssetPaths[i];
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                // The compiled program is a SEPARATE asset that ApplyProgram() wrote into
                // Assets/SerializedUdonPrograms, and deleting the program asset does not take it with it.
                // Left alone it accumulates one orphan per test run, forever -- 13 of them, 65 KB, were
                // already on disk before this cleanup existed.
                string serializedProgramPath = SerializedProgramPathOf(assetPath);

                AssetDatabase.DeleteAsset(assetPath);

                if (!string.IsNullOrEmpty(serializedProgramPath))
                    AssetDatabase.DeleteAsset(serializedProgramPath);
            }

            _createdAssetPaths.Clear();
            AssetDatabase.Refresh();
            CompileAllProgramsSync();
        }

        [Test]
        public void SyncAttribute_ProducesLinearSyncMetadataAndSyncAssemblyTag()
        {
            UnityEngine.Object asset = CreateProgramAssetForBehaviourType(typeof(EditorSyncLinearIntegrationTestBehaviour));

            CompileAllProgramsSync();

            Assert.That(GetAssemblyError(asset), Is.Null.Or.Empty);
            Assert.That(HasFieldDefinition(asset, "pos"), Is.True);
            Assert.That(GetFieldDefinitionSyncMode(asset, "pos"), Is.EqualTo(UdonSyncMode.Linear));
            Assert.That(GetUasm(asset), Does.Contain("sync").IgnoreCase);
        }

        /// <summary>
        /// The same assertion for the named-argument spelling,
        /// <c>[Sync(Authority.Owner, Interpolation = InterpolationMode.Linear)]</c>.
        ///
        /// EditorSyncNamedInterpolationIntegrationTestBehaviour has been compiled by Unity since it was
        /// added, which is not the same thing as being tested: UdonSharpCompilerV1 builds its root tree set
        /// out of the files program assets point at, so a fixture no program asset names is never bound and
        /// never emitted. Without the CreateProgramAssetForBehaviourType call below, the named spelling
        /// reaches an emitted Udon program in no test anywhere.
        ///
        /// The emitted field definition's sync mode is the assertion because that is the value the
        /// synced-variable pipeline serializes with. Roslyn stores a named argument in NamedArguments rather
        /// than ConstructorArguments, and Symbol.TryCreateAttributeInstance applies those by reflection
        /// *after* Activator.CreateInstance has already run the constructor -- so the interpolation only
        /// reaches the wire while SyncAttribute assigns through the Interpolation property, whose setter is
        /// what keeps the inherited NetworkSyncType in step. Assign the backing field instead and this field
        /// emits as UdonSyncMode.None while SyncAttribute.Interpolation still reads Linear, which silently
        /// disarms CE0010's Manual-mode interpolation check: that check reads the property, the pipeline
        /// reads NetworkSyncType, and the two would no longer describe the same program.
        /// </summary>
        [Test]
        public void SyncAttribute_NamedInterpolation_ProducesLinearSyncMetadataAndSyncAssemblyTag()
        {
            UnityEngine.Object asset = CreateProgramAssetForBehaviourType(
                typeof(EditorSyncNamedInterpolationIntegrationTestBehaviour));

            CompileAllProgramsSync();

            Assert.That(GetAssemblyError(asset), Is.Null.Or.Empty);
            Assert.That(HasFieldDefinition(asset, "pos"), Is.True);
            Assert.That(GetFieldDefinitionSyncMode(asset, "pos"), Is.EqualTo(UdonSyncMode.Linear),
                "The named interpolation argument did not reach the emitted program. Interpolation and " +
                "NetworkSyncType have come apart, so this field serializes uninterpolated while every " +
                "check that reads SyncAttribute.Interpolation -- CE0010's Manual-mode check among them -- " +
                "still believes it is Linear.");
            Assert.That(GetUasm(asset), Does.Contain("sync").IgnoreCase);
        }

        [Test]
        public void SyncAttribute_TriggersExistingManualModeCompatibilityError()
        {
            CreateProgramAssetForBehaviourType(typeof(EditorSyncManualLinearIntegrationTestBehaviour));

            LogAssert.Expect(
                LogType.Error,
                new Regex(".*linear interpolation sync mode.*Manual sync.*", RegexOptions.Singleline));
            CompileAllProgramsSync();

            string error = GetLastCompileDiagnosticsText(DiagnosticSeverity.Error);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(error, Does.Contain("Manual sync"));
        }

        [Test]
        public void SyncAttribute_TriggersExistingUnsupportedTypeError()
        {
            CreateProgramAssetForBehaviourType(typeof(EditorSyncInvalidTypeIntegrationTestBehaviour));

            LogAssert.Expect(
                LogType.Error,
                new Regex(".*not supported by Udon sync.*", RegexOptions.Singleline));
            CompileAllProgramsSync();

            string error = GetLastCompileDiagnosticsText(DiagnosticSeverity.Error);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
            Assert.That(error, Does.Contain("not supported by Udon sync"));
        }

        [Test]
        public void SyncOnJoinEmitter_GeneratesGlueAndRoundTripsState()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SyncOnJoinFixture : UdonSharpBehaviour
{
    [SyncOnJoin]
    public int gamePhase;

    [SyncOnJoin]
    public bool[] completed;
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Serialize"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Deserialize_Trigger"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinPayload"));
            Assert.That(transformed, Does.Contain("UdonSynced"), "the payload must be a real synced variable, not a plain field moved by a coordinator");

            Assembly assembly = CompileAssembly(transformed, "SyncOnJoinEmitterTests");
            Type type = assembly.GetType("SyncOnJoinFixture");

            GameObject sourceObject = new GameObject("SyncOnJoinSource");
            GameObject destinationObject = new GameObject("SyncOnJoinDestination");
            _createdObjects.Add(sourceObject);
            _createdObjects.Add(destinationObject);

            Component sourceInstance = sourceObject.AddComponent(type);
            type.GetField("gamePhase").SetValue(sourceInstance, 7);
            type.GetField("completed").SetValue(sourceInstance, new[] { true, false, true });
            type.GetMethod("_CE_SyncOnJoin_Serialize").Invoke(sourceInstance, null);

            string payload = (string)type.GetField("_CE_SyncOnJoinPayload", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sourceInstance);
            Assert.That(payload, Is.Not.Null.And.Not.Empty);

            Component destinationInstance = destinationObject.AddComponent(type);
            type.GetField("_CE_SyncOnJoinPayload", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(destinationInstance, payload);
            type.GetMethod("_CE_SyncOnJoin_Deserialize_Trigger").Invoke(destinationInstance, null);

            Assert.That((int)type.GetField("gamePhase").GetValue(destinationInstance), Is.EqualTo(7));
            CollectionAssert.AreEqual(
                new[] { true, false, true },
                (bool[])type.GetField("completed").GetValue(destinationInstance));
        }

        [Test]
        public void SyncOnJoinEmitter_DeepSerializesUnitySerializableModels()
        {
            const string source = @"
using System;
using System.Collections.Generic;
using UdonSharp;
using UdonSharp.CE.Data;
using UdonSharp.CE.Net;
using UdonSharp.CE.Persistence;
using UnityEngine;

public class DeepSyncOnJoinFixture : UdonSharpBehaviour
{
    [SyncOnJoin]
    public DeepRoundState state;
}

[Serializable]
public class DeepRoundState
{
    [DataField(""phase_id"")]
    public int phase;

    [SerializeField]
    private Vector3 spawn;

    [PersistKey(""players"")]
    public DeepPlayerState[] players;

    public List<DeepPlayerState> history;

    public void SetSpawn(Vector3 value)
    {
        spawn = value;
    }

    public Vector3 GetSpawn()
    {
        return spawn;
    }
}

[Serializable]
public class DeepPlayerState
{
    public string name;
    public int score;
    public Color color;
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_ToData"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_FromData"));
            Assert.That(transformed, Does.Contain("\"phase_id\""));
            Assert.That(transformed, Does.Contain("\"players\""));

            Assembly assembly = CompileAssembly(transformed, "DeepSyncOnJoinEmitterTests");
            Type fixtureType = assembly.GetType("DeepSyncOnJoinFixture");
            Type roundType = assembly.GetType("DeepRoundState");
            Type playerType = assembly.GetType("DeepPlayerState");

            object sourceState = Activator.CreateInstance(roundType);
            roundType.GetField("phase").SetValue(sourceState, 5);
            roundType.GetMethod("SetSpawn").Invoke(sourceState, new object[] { new Vector3(1.5f, 2.5f, 3.5f) });

            Array players = Array.CreateInstance(playerType, 2);
            players.SetValue(CreateDeepPlayer(playerType, "Ada", 10, Color.red), 0);
            players.SetValue(CreateDeepPlayer(playerType, "Ben", 20, Color.green), 1);
            roundType.GetField("players").SetValue(sourceState, players);

            Type historyType = typeof(List<>).MakeGenericType(playerType);
            object history = Activator.CreateInstance(historyType);
            historyType.GetMethod("Add").Invoke(history, new[] { CreateDeepPlayer(playerType, "Cy", 30, Color.blue) });
            roundType.GetField("history").SetValue(sourceState, history);

            GameObject sourceObject = new GameObject("DeepSyncOnJoinSource");
            GameObject destinationObject = new GameObject("DeepSyncOnJoinDestination");
            _createdObjects.Add(sourceObject);
            _createdObjects.Add(destinationObject);

            Component sourceInstance = sourceObject.AddComponent(fixtureType);
            fixtureType.GetField("state").SetValue(sourceInstance, sourceState);
            fixtureType.GetMethod("_CE_SyncOnJoin_Serialize").Invoke(sourceInstance, null);

            string payload = (string)fixtureType.GetField("_CE_SyncOnJoinPayload", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sourceInstance);
            Assert.That(payload, Does.Contain("phase_id"));
            Assert.That(payload, Does.Contain("players"));

            Component destinationInstance = destinationObject.AddComponent(fixtureType);
            fixtureType.GetField("_CE_SyncOnJoinPayload", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(destinationInstance, payload);
            fixtureType.GetMethod("_CE_SyncOnJoin_Deserialize_Trigger").Invoke(destinationInstance, null);

            object restoredState = fixtureType.GetField("state").GetValue(destinationInstance);
            Assert.That(restoredState, Is.Not.Null);
            Assert.That((int)roundType.GetField("phase").GetValue(restoredState), Is.EqualTo(5));

            Vector3 spawn = (Vector3)roundType.GetMethod("GetSpawn").Invoke(restoredState, null);
            Assert.That(spawn.x, Is.EqualTo(1.5f).Within(0.0001f));
            Assert.That(spawn.y, Is.EqualTo(2.5f).Within(0.0001f));
            Assert.That(spawn.z, Is.EqualTo(3.5f).Within(0.0001f));

            Array restoredPlayers = (Array)roundType.GetField("players").GetValue(restoredState);
            Assert.That(restoredPlayers.Length, Is.EqualTo(2));
            Assert.That((string)playerType.GetField("name").GetValue(restoredPlayers.GetValue(0)), Is.EqualTo("Ada"));
            Assert.That((int)playerType.GetField("score").GetValue(restoredPlayers.GetValue(1)), Is.EqualTo(20));

            object restoredHistory = roundType.GetField("history").GetValue(restoredState);
            Assert.That((int)historyType.GetProperty("Count").GetValue(restoredHistory), Is.EqualTo(1));
            object historyItem = historyType.GetProperty("Item").GetValue(restoredHistory, new object[] { 0 });
            Assert.That((string)playerType.GetField("name").GetValue(historyItem), Is.EqualTo("Cy"));
        }

        [Test]
        public void SyncOnJoinEmitter_CustomSerializer_SupportsManualOnlyFields()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

public class SyncOnJoinCustomSerializerFixture : UdonSharpBehaviour
{
    [SyncOnJoin]
    [SyncSerializer(nameof(SerializeTarget), nameof(DeserializeTarget))]
    public GameObject target;

    public bool restored;

    public string SerializeTarget()
    {
        return ""manual"";
    }

    public void DeserializeTarget(string data)
    {
        restored = data == ""manual"";
    }
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            // Assert that the author's serializer pair is CALLED, not that the emitter produced one exact
            // spelling of the surrounding code. Pinning generated syntax character-for-character made this
            // test fail the moment the decode path grew its TokenType guards -- a correctness improvement --
            // even though the observable behaviour, verified by the round trip below, was unchanged.
            Assert.That(transformed, Does.Contain("SerializeTarget()"));
            Assert.That(transformed, Does.Match(@"DeserializeTarget\(\s*[A-Za-z_][A-Za-z0-9_]*\s*\)"),
                "The generated deserializer must call the [SyncSerializer] method with a decoded string value.");

            Assembly assembly = CompileAssembly(transformed, "SyncOnJoinCustomSerializerTests");
            Type type = assembly.GetType("SyncOnJoinCustomSerializerFixture");
            GameObject sourceObject = new GameObject("SyncOnJoinCustomSource");
            GameObject destinationObject = new GameObject("SyncOnJoinCustomDestination");
            _createdObjects.Add(sourceObject);
            _createdObjects.Add(destinationObject);

            Component sourceInstance = sourceObject.AddComponent(type);
            type.GetMethod("_CE_SyncOnJoin_Serialize").Invoke(sourceInstance, null);
            string payload = (string)type.GetField("_CE_SyncOnJoinPayload", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(sourceInstance);

            Component destinationInstance = destinationObject.AddComponent(type);
            type.GetField("_CE_SyncOnJoinPayload", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(destinationInstance, payload);
            type.GetMethod("_CE_SyncOnJoin_Deserialize_Trigger").Invoke(destinationInstance, null);

            Assert.That((bool)type.GetField("restored").GetValue(destinationInstance), Is.True);
        }

        /// <summary>
        /// <c>SyncOnJoin.Group</c> at the emitter: one payload per group, the default group unsuffixed, and
        /// -- the whole point -- rewriting a field in one group leaves every other group's payload byte for
        /// byte what it was.
        /// </summary>
        /// <remarks>
        /// The optimizer runs over a single tree here, so <c>SyncOnJoinAttribute</c> binds to an error
        /// symbol and both Group and Priority are read off the written syntax rather than the semantic
        /// model. That is not a shortcut in the test: it is the same situation any real compile pass whose
        /// tree set excludes the CE runtime source is in, and if the fallback were missing this test would
        /// see one shared payload -- exactly the failure the fallback exists to prevent.
        /// </remarks>
        [Test]
        public void SyncOnJoinEmitter_Group_GivesEachGroupItsOwnPayloadAndResendsOnlyWhatChanged()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SyncOnJoinGroupFixture : UdonSharpBehaviour
{
    [SyncOnJoin(Group = ""terrain"", Priority = 20)]
    public int terrainVersion;

    [SyncOnJoin(Group = ""terrain"", Priority = 10)]
    public int[] heightmap;

    [SyncOnJoin(Group = ""score"")]
    public int score;

    [SyncOnJoin]
    public string label;
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            // The default group keeps the names it has always had, so nothing already working changes shape.
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinPayload ="));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Deserialize_Trigger()"));

            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinPayload_terrain"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinPayload_score"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinApplied_terrain"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Serialize_score"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Apply_terrain();"));

            // Every group's payload is a real synced variable, not just a local buffer.
            Assert.That(Regex.Matches(transformed, @"UdonSynced").Count, Is.EqualTo(3),
                "Each group needs its own [UdonSynced] string; one shared payload is the bundling Group removes.");

            // Priority still orders fields WITHIN a group: heightmap (10) is written before terrainVersion (20).
            Assert.That(transformed.IndexOf("\"heightmap\"", StringComparison.Ordinal),
                Is.LessThan(transformed.IndexOf("\"terrainVersion\"", StringComparison.Ordinal)),
                "Priority must still order fields inside their own group's payload.");

            Assembly assembly = CompileAssembly(transformed, "SyncOnJoinGroupEmitterTests");
            Type type = assembly.GetType("SyncOnJoinGroupFixture");

            GameObject sourceObject = new GameObject("SyncOnJoinGroupSource");
            GameObject destinationObject = new GameObject("SyncOnJoinGroupDestination");
            _createdObjects.Add(sourceObject);
            _createdObjects.Add(destinationObject);

            Component sourceInstance = sourceObject.AddComponent(type);
            type.GetField("heightmap").SetValue(sourceInstance, new[] { 3, 1, 4 });
            type.GetField("terrainVersion").SetValue(sourceInstance, 2);
            type.GetField("score").SetValue(sourceInstance, 7);
            type.GetField("label").SetValue(sourceInstance, "arena");

            CaptureEveryGroup(type, sourceInstance);

            string terrainPayload = ReadPayload(type, sourceInstance, "_terrain");
            string scorePayload = ReadPayload(type, sourceInstance, "_score");
            string defaultPayload = ReadPayload(type, sourceInstance, "");

            Assert.That(terrainPayload, Does.Contain("heightmap").And.Not.Contain("score"));
            Assert.That(scorePayload, Does.Contain("score").And.Not.Contain("heightmap"));
            Assert.That(defaultPayload, Does.Contain("label").And.Not.Contain("score"));

            // Move the hot field and re-capture everything.
            type.GetField("score").SetValue(sourceInstance, 8);
            CaptureEveryGroup(type, sourceInstance);

            Assert.That(ReadPayload(type, sourceInstance, "_score"), Is.Not.EqualTo(scorePayload));
            Assert.That(ReadPayload(type, sourceInstance, "_terrain"), Is.EqualTo(terrainPayload),
                "Writing a field in one group re-serialized another group's payload. That is the resend " +
                "Group exists to stop.");
            Assert.That(ReadPayload(type, sourceInstance, ""), Is.EqualTo(defaultPayload));

            // Deliver ONLY the score group, the way VRChat delivers one changed synced variable.
            Component destinationInstance = destinationObject.AddComponent(type);
            WritePayload(type, destinationInstance, "_score", ReadPayload(type, sourceInstance, "_score"));
            type.GetMethod("_CE_SyncOnJoin_Deserialize_Trigger_score").Invoke(destinationInstance, null);

            Assert.That((int)type.GetField("score").GetValue(destinationInstance), Is.EqualTo(8));
            Assert.That(type.GetField("heightmap").GetValue(destinationInstance), Is.Null,
                "Applying one group must not touch another group's fields.");
            Assert.That((int)type.GetField("terrainVersion").GetValue(destinationInstance), Is.EqualTo(0));
        }

        [Test]
        public void SyncOnJoinEmitter_Group_SanitisesNamesIntoIdentifiers()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SyncOnJoinGroupNameFixture : UdonSharpBehaviour
{
    [SyncOnJoin(Group = ""hot state"")]
    public int a;

    [SyncOnJoin(Group = ""9lives"")]
    public int b;
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            // Generated members become Udon symbol names, so a group name has to survive as an identifier.
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinPayload_hot_state"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinPayload__9lives"));

            CompileAssembly(transformed, "SyncOnJoinGroupNameTests");
        }

        [Test]
        public void SyncOnJoinEmitter_Group_RejectsTwoNamesThatSanitiseToOneIdentifier()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SyncOnJoinGroupCollisionFixture : UdonSharpBehaviour
{
    [SyncOnJoin(Group = ""hot state"")]
    public int a;

    [SyncOnJoin(Group = ""hot-state"")]
    public int b;
}";

            // Silently merging these would put both fields back in one payload while the source reads as
            // two groups -- the failure with no symptom that Group exists to remove.
            TargetInvocationException thrown = Assert.Throws<TargetInvocationException>(
                () => RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source));

            Assert.That(thrown.InnerException, Is.Not.Null);
            Assert.That(thrown.InnerException.Message, Does.Contain("hot state").And.Contain("hot-state"));
            Assert.That(thrown.InnerException.Message, Does.Contain("hot_state"),
                "The message has to name the identifier both group names collapsed to, or the author " +
                "cannot see why two different strings collided.");
        }

        /// <summary>
        /// Across a hierarchy, each group's transport is emitted exactly once -- on the base-most behaviour
        /// that declares a field of that group -- while a derived behaviour that adds fields to a group the
        /// base owns only overrides its serialize/deserialize pair.
        /// </summary>
        /// <remarks>
        /// Every way of getting this wrong is a compile error rather than a subtle one, which is why the
        /// fixture is compiled: a second payload field is CS0102, a second synthesized event handler on one
        /// type is CS0111, and an override of a member the base never emitted is CS0115.
        /// </remarks>
        [Test]
        public void SyncOnJoinEmitter_Group_EmitsEachGroupsTransportOnceAcrossAHierarchy()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SyncOnJoinGroupBase : UdonSharpBehaviour
{
    [SyncOnJoin(Group = ""shared"")]
    public int fromBase;
}

public class SyncOnJoinGroupDerived : SyncOnJoinGroupBase
{
    [SyncOnJoin(Group = ""shared"")]
    public int alsoShared;

    [SyncOnJoin(Group = ""derived"")]
    public int fromDerived;
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            Assert.That(Regex.Matches(transformed, @"protected string _CE_SyncOnJoinPayload_shared\b").Count,
                Is.EqualTo(1),
                "The shared group's payload belongs to the base alone; a second one is a duplicate synced variable.");
            Assert.That(Regex.Matches(transformed, @"protected string _CE_SyncOnJoinPayload_derived\b").Count,
                Is.EqualTo(1),
                "A group the derived behaviour introduced has to carry its own transport, on the derived type.");

            // The derived type contributes to 'shared', so it overrides that pair -- and it introduced
            // 'derived', so it declares that one.
            Assert.That(transformed, Does.Contain("override void _CE_SyncOnJoin_Serialize_shared"));
            Assert.That(transformed, Does.Contain("virtual void _CE_SyncOnJoin_Serialize_derived"));

            // Capture covers the whole hierarchy, so the derived type overrides the base's rather than
            // adding a second entry point an author would have to know to call.
            Assert.That(Regex.Matches(transformed, @"virtual void _CE_SyncOnJoin_RequestCapture").Count, Is.EqualTo(1));
            Assert.That(Regex.Matches(transformed, @"override void _CE_SyncOnJoin_RequestCapture").Count, Is.EqualTo(1));

            Assembly assembly = CompileAssembly(transformed, "SyncOnJoinGroupHierarchyTests");
            Type derivedType = assembly.GetType("SyncOnJoinGroupDerived");

            GameObject host = new GameObject("SyncOnJoinGroupHierarchy");
            _createdObjects.Add(host);

            Component instance = host.AddComponent(derivedType);
            derivedType.GetField("fromBase").SetValue(instance, 1);
            derivedType.GetField("alsoShared").SetValue(instance, 2);
            derivedType.GetField("fromDerived").SetValue(instance, 3);

            CaptureEveryGroup(derivedType, instance);

            string sharedPayload = ReadPayload(derivedType, instance, "_shared");

            Assert.That(sharedPayload, Does.Contain("fromBase").And.Contain("alsoShared"),
                "The derived override has to write every field of the group, base's included.");
            Assert.That(ReadPayload(derivedType, instance, "_derived"), Does.Contain("fromDerived"));
        }

        /// <summary>
        /// A partial behaviour has one symbol and several declaration parts, so a group named in a later
        /// part still gets exactly one transport, emitted into the canonical part.
        /// </summary>
        [Test]
        public void SyncOnJoinEmitter_Group_EmitsEachGroupsTransportOnceAcrossPartialParts()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public partial class SyncOnJoinGroupPartial : UdonSharpBehaviour
{
    [SyncOnJoin(Group = ""first"")]
    public int a;

    public int handlerRuns;

    public override void OnDeserialization()
    {
        handlerRuns++;
    }
}

public partial class SyncOnJoinGroupPartial
{
    [SyncOnJoin(Group = ""second"")]
    public int b;
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            Assert.That(Regex.Matches(transformed, @"protected string _CE_SyncOnJoinPayload_first\b").Count, Is.EqualTo(1));
            Assert.That(Regex.Matches(transformed, @"protected string _CE_SyncOnJoinPayload_second\b").Count, Is.EqualTo(1));
            Assert.That(Regex.Matches(transformed, @"void OnDeserialization").Count, Is.EqualTo(1),
                "A second OnDeserialization declaration on the same partial type is CS0111.");

            // Both groups have to be applied by the one handler the author wrote, whichever part it is in.
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Apply_first();"));
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoin_Apply_second();"));

            CompileAssembly(transformed, "SyncOnJoinGroupPartialTests");
        }

        /// <summary>
        /// The claim, proven as Udon rather than as .NET: two groups are two synced variables on one
        /// behaviour, capturing rewrites only the group whose fields moved, and a delivery carrying one
        /// group applies that group and leaves the rest alone.
        /// </summary>
        /// <remarks>
        /// Capture is driven one group at a time here rather than through
        /// <c>_CE_SyncOnJoin_RequestCapture</c>, because that method opens with
        /// <c>Networking.IsOwner(Networking.LocalPlayer, gameObject)</c> and the Udon EXTERN behind it
        /// cannot run outside play mode: it fails with "DontDestroyOnLoad can only be used in play mode",
        /// which halts the behaviour and leaves every payload empty. That is an EditMode limitation of the
        /// ownership extern, not of the glue -- the per-group capture the gate protects is exactly what
        /// runs below, and the gate's own change detection is covered by
        /// SyncOnJoinEmitter_Capture_AsksForSerializationOnlyWhenAGroupChanged, which executes the same
        /// generated method as plain .NET where that extern is not involved.
        /// </remarks>
        [Test]
        public void SyncOnJoinGroups_SendOnlyTheGroupThatChanged_AcrossTwoUdonHeaps()
        {
            var asset = (UdonSharpProgramAsset)CreateProgramAssetForBehaviourType(
                typeof(CESyncRuntimeTests.EditorSyncOnJoinGroupedTestBehaviour));

            CompileAllProgramsSync();
            Assert.That(GetAssemblyError(asset), Is.Null.Or.Empty);

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(asset))
            {
                IReadOnlyList<string> synced = pair.SyncedVariableNames();

                Assert.That(synced, Does.Contain("_CE_SyncOnJoinPayload"));
                Assert.That(synced, Does.Contain("_CE_SyncOnJoinPayload_terrain"));
                Assert.That(synced, Does.Contain("_CE_SyncOnJoinPayload_score"));

                pair.Owner.SetProgramVariable("heightmap", new[] { 3, 1, 4 });
                pair.Owner.SetProgramVariable("terrainVersion", 2);
                pair.Owner.SetProgramVariable("score", 7);
                pair.Owner.SetProgramVariable("label", "arena");

                CaptureEveryGroupOnHeap(pair.Owner, synced);

                string terrainPayload = (string)pair.Owner.GetProgramVariable("_CE_SyncOnJoinPayload_terrain");
                string defaultPayload = (string)pair.Owner.GetProgramVariable("_CE_SyncOnJoinPayload");
                string scorePayload = (string)pair.Owner.GetProgramVariable("_CE_SyncOnJoinPayload_score");

                Assert.That(terrainPayload, Does.Contain("heightmap"));
                Assert.That(scorePayload, Does.Contain("score").And.Not.Contain("heightmap"));

                pair.Owner.SetProgramVariable("score", 11);
                CaptureEveryGroupOnHeap(pair.Owner, synced);

                Assert.That((string)pair.Owner.GetProgramVariable("_CE_SyncOnJoinPayload_terrain"),
                    Is.EqualTo(terrainPayload),
                    "Moving a field in the score group rewrote the terrain payload, so VRChat would put the " +
                    "cold snapshot back on the wire for a change that had nothing to do with it.");
                Assert.That((string)pair.Owner.GetProgramVariable("_CE_SyncOnJoinPayload"),
                    Is.EqualTo(defaultPayload));
                Assert.That((string)pair.Owner.GetProgramVariable("_CE_SyncOnJoinPayload_score"),
                    Is.Not.EqualTo(scorePayload));

                // Deliver ONLY the score group and apply it through the real event, which on this behaviour
                // means the author's own OnDeserialization with the glue injected into it.
                pair.Remote.SetProgramVariable(
                    "_CE_SyncOnJoinPayload_score",
                    pair.Owner.GetProgramVariable("_CE_SyncOnJoinPayload_score"));

                pair.Remote.OnDeserialization(new DeserializationResult());

                Assert.That(pair.Remote.GetProgramVariable("score"), Is.EqualTo(11));
                Assert.That(pair.Remote.GetProgramVariable("terrainVersion"), Is.EqualTo(0),
                    "A delivery that carried only the score group applied the terrain group as well.");
                Assert.That(pair.Remote.GetProgramVariable("_CE_SyncOnJoinApplied_score"),
                    Is.EqualTo(pair.Owner.GetProgramVariable("_CE_SyncOnJoinPayload_score")));
                Assert.That(pair.Remote.GetProgramVariable("_CE_SyncOnJoinApplied_terrain"), Is.Null,
                    "The terrain group's guard must stay unset while its payload has never arrived.");
                Assert.That(pair.Remote.GetProgramVariable("deserializations"), Is.EqualTo(1),
                    "The handler the author wrote must still run after the glue was injected into it.");
            }
        }

        /// <summary>
        /// The generated capture asks VRChat to serialize only when a group's payload actually changed --
        /// the half of Group that decides whether anything reaches the wire at all.
        /// </summary>
        /// <remarks>
        /// Run as plain .NET rather than on an Udon heap because the method opens with
        /// <c>Networking.IsOwner</c>, whose Udon EXTERN throws outside play mode (see
        /// SyncOnJoinGroups_SendOnlyTheGroupThatChanged_AcrossTwoUdonHeaps). Everything after that gate is
        /// ordinary C# that the emitter wrote, and <c>UdonSharpBehaviour.RequestSerialization()</c> is
        /// virtual and empty off-platform, so a subclass in the fixture source can count the calls.
        /// </remarks>
        [Test]
        public void SyncOnJoinEmitter_Capture_AsksForSerializationOnlyWhenAGroupChanged()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SyncOnJoinCaptureFixture : UdonSharpBehaviour
{
    [SyncOnJoin(Group = ""terrain"")]
    public int[] heightmap;

    [SyncOnJoin(Group = ""score"")]
    public int score;
}

public class SyncOnJoinCaptureProbe : SyncOnJoinCaptureFixture
{
    public int serializationRequests;

    public override void RequestSerialization()
    {
        serializationRequests++;
    }
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", source);

            Assembly assembly = CompileAssembly(transformed, "SyncOnJoinCaptureTests");
            Type probeType = assembly.GetType("SyncOnJoinCaptureProbe");

            GameObject host = new GameObject("SyncOnJoinCaptureProbe");
            _createdObjects.Add(host);

            Component probe = host.AddComponent(probeType);
            MethodInfo capture = probeType.GetMethod("_CE_SyncOnJoin_RequestCapture");
            FieldInfo requests = probeType.GetField("serializationRequests");

            Assert.That(capture, Is.Not.Null, "The generated capture entry point is missing.");

            capture.Invoke(probe, null);
            Assert.That((int)requests.GetValue(probe), Is.EqualTo(1),
                "The first capture takes every payload from empty to JSON, which has to be sent.");

            capture.Invoke(probe, null);
            Assert.That((int)requests.GetValue(probe), Is.EqualTo(1),
                "Capturing an unchanged world asked VRChat to serialize anyway. Manual sync is rate " +
                "limited in proportion to size, so a no-op resend is bandwidth taken from everything " +
                "else in the instance.");

            string terrainBefore = ReadPayload(probeType, probe, "_terrain");

            probeType.GetField("score").SetValue(probe, 9);
            capture.Invoke(probe, null);

            Assert.That((int)requests.GetValue(probe), Is.EqualTo(2),
                "A changed group must still reach the network.");
            Assert.That(ReadPayload(probeType, probe, "_terrain"), Is.EqualTo(terrainBefore),
                "Capturing after a score write rewrote the terrain payload.");
        }

        /// <summary>Runs every generated per-group capture on a plain .NET instance of a transformed fixture.</summary>
        private static void CaptureEveryGroup(Type type, Component instance)
        {
            foreach (MethodInfo method in type.GetMethods())
            {
                if (method.Name.StartsWith("_CE_SyncOnJoin_Serialize", StringComparison.Ordinal))
                    method.Invoke(instance, null);
            }
        }

        /// <summary>Runs every generated per-group capture on a live Udon behaviour, one group at a time.</summary>
        private static void CaptureEveryGroupOnHeap(UdonBehaviour behaviour, IEnumerable<string> syncedVariableNames)
        {
            const string payloadPrefix = "_CE_SyncOnJoinPayload";

            foreach (string name in syncedVariableNames)
            {
                if (name.StartsWith(payloadPrefix, StringComparison.Ordinal))
                    behaviour.SendCustomEvent("_CE_SyncOnJoin_Serialize" + name.Substring(payloadPrefix.Length));
            }
        }

        private static string ReadPayload(Type type, Component instance, string groupSuffix)
        {
            return (string)PayloadField(type, groupSuffix).GetValue(instance);
        }

        private static void WritePayload(Type type, Component instance, string groupSuffix, string payload)
        {
            PayloadField(type, groupSuffix).SetValue(instance, payload);
        }

        /// <summary>
        /// The generated payload field, searched up the hierarchy. It is protected so a derived behaviour
        /// writes the same synced variable, and GetField does not return inherited non-public members.
        /// </summary>
        private static FieldInfo PayloadField(Type type, string groupSuffix)
        {
            string name = "_CE_SyncOnJoinPayload" + groupSuffix;

            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }

            Assert.Fail($"No generated payload field '{name}' on '{type.Name}' or any of its bases.");
            return null;
        }
        [Test]
        public void SyncOnJoinAnalyzer_ManualOnlyField_ProducesWarningWithoutCompileError()
        {
            UnityEngine.Object asset = CreateProgramAssetForBehaviourType(typeof(EditorSyncOnJoinManualOnlyWarningTestBehaviour));

            LogAssert.Expect(
                LogType.Warning,
                new Regex(".*CE0032.*custom serializer.*", RegexOptions.Singleline));
            CompileAllProgramsSync();

            Assert.That(GetAssemblyError(asset), Is.Null.Or.Empty);
            string warning = GetLastCompileDiagnosticsText(DiagnosticSeverity.Warning);
            Assert.That(warning, Does.Contain("CE0032"));
            Assert.That(warning, Does.Contain("SyncSerializer"));
        }

        [Test]
        public void SyncOnJoinAnalyzer_CustomSerializer_SuppressesManualOnlyWarning()
        {
            UnityEngine.Object asset = CreateProgramAssetForBehaviourType(typeof(EditorSyncOnJoinCustomSerializerTestBehaviour));

            CompileAllProgramsSync();

            Assert.That(GetAssemblyError(asset), Is.Null.Or.Empty);
            string warning = GetLastCompileDiagnosticsText(DiagnosticSeverity.Warning);
            Assert.That(warning, Does.Not.Contain("CE0032"));
        }

        [Test]
        public void RpcEmitter_MarksReceiverNetworkCallable_AndRewritesCallSites()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class RpcFixture : UdonSharpBehaviour
{
    public int received;

    public void Trigger()
    {
        PlaySound(3);
    }

    [Rpc(RpcTarget.All, RateLimit = 2f)]
    public void PlaySound(int soundId)
    {
        received = soundId;
    }

    [RpcOwnerOnly]
    public void StartGame()
    {
    }
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.CodeGen.RpcEmitter", source);

            // The receiver must be reachable as a network event, with the declared rate limit handed to
            // VRChat rather than merely documented.
            Assert.That(transformed, Does.Contain("NetworkCallable(2)"));

            // The call site must actually send. Before this, [Rpc] was analyzer-only and authors still had
            // to hand-write SendCustomNetworkEvent next to the attribute that claimed to do it.
            Assert.That(transformed, Does.Contain("SendCustomNetworkEvent"));
            Assert.That(transformed, Does.Contain("NetworkEventTarget.All"));
            Assert.That(transformed, Does.Contain("\"PlaySound\""));

            // OwnerOnly is enforced on the receiver via NetworkCalling.CallingPlayer, not just warned about.
            Assert.That(transformed, Does.Contain("__ce_rpcCaller"));
            Assert.That(transformed, Does.Contain("Networking.IsOwner(__ce_rpcCaller"));
        }

        [Test]
        public void QuantizeEmitter_RoundsQuantizedFieldsDuringOnPreSerialization()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class QuantizeFixture : UdonSharpBehaviour
{
    [Sync(Authority.Owner, Quantize = 0.1f)]
    public float value = 0.1234f;
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Optimizers.QuantizePreSerializeEmitter", source);

            Assert.That(transformed, Does.Contain("_CE_ApplyQuantizePreSerialization"));
            Assert.That(transformed, Does.Contain("OnPreSerialization"));

            Assembly assembly = CompileAssembly(transformed, "QuantizeEmitterTests");
            Type type = assembly.GetType("QuantizeFixture");
            GameObject go = new GameObject("QuantizeFixture");
            _createdObjects.Add(go);
            Component instance = go.AddComponent(type);

            type.GetMethod("OnPreSerialization").Invoke(instance, null);

            float value = (float)type.GetField("value").GetValue(instance);
            Assert.That(value, Is.EqualTo(0.1f).Within(0.0001f));
        }

        [Test]
        public void UdonVariableStorageInterface_ClearCache_IsThreadSafe()
        {
            GameObject go = new GameObject("UdonVariableStorageThreadTest");
            _createdObjects.Add(go);

            UdonBehaviour behaviour = go.AddComponent<UdonBehaviour>();
            UnityEngine.Object programAsset = CreateProgramAssetForBehaviourType(typeof(EditorSyncLinearIntegrationTestBehaviour));
            CompileAllProgramsSync();
            SetProgramSource(behaviour, programAsset);

            MethodInfo clearCache = typeof(UdonVariableStorageInterface).GetMethod("ClearCache", BindingFlags.Static | BindingFlags.NonPublic);
            ExceptionDispatchInfo capturedException = null;

            Parallel.For(0, 256, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
            {
                try
                {
                    if ((i & 7) == 0)
                    {
                        clearCache.Invoke(null, null);
                    }

                    var storage = new UdonVariableStorageInterface(behaviour);
                    if (storage.GetElementStorage("pos") == null)
                    {
                        throw new InvalidOperationException("Expected storage for 'pos'.");
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref capturedException, ExceptionDispatchInfo.Capture(ex), null);
                }
            });

            if (capturedException != null)
                capturedException.Throw();
        }

        [Test]
        public void VRCProjectSettingsRepair_DecodesPackedCollisionArrayBytes()
        {
            MethodInfo decodeMethod = typeof(VRCProjectSettingsRepair).GetMethod("DecodePackedBoolArray", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.That(decodeMethod, Is.Not.Null);

            bool[] decoded = (bool[])decodeMethod.Invoke(null, new object[] { "010001FF" });

            CollectionAssert.AreEqual(new[] { true, false, true, true }, decoded);
        }

        [Test]
        public void AsyncTransformer_AwaitedChildTask_InvokesContinuationStart()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Async;

public class AsyncChildFixture : UdonSharpBehaviour
{
    private int value;

    public async UdonTask Parent()
    {
        value = 1;
        await Child();
        value = 3;
    }

    private async UdonTask Child()
    {
        await UdonTask.Delay(0.1f);
        value = 2;
    }
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Async.AsyncMethodTransformOptimizer", source);

            Assert.That(transformed, Does.Contain("__Child_Start(\"__Parent_MoveNext\")"));
            Assert.That(transformed, Does.Contain("case 1:"));
            Assert.That(transformed, Does.Contain("value = 3;"));
            Assert.That(transformed, Does.Not.Contain("await Child"));
        }

        [Test]
        public void AsyncTransformer_WhileYield_PreservesLoopBody()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Async;
using UnityEngine;

public class AsyncLoopFixture : UdonSharpBehaviour
{
    private int value;

    public async UdonTask Loop(float duration)
    {
        float elapsed = 0f;
        while (elapsed < duration)
        {
            value++;
            await UdonTask.Yield();
            elapsed += Time.deltaTime;
        }
    }
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Async.AsyncMethodTransformOptimizer", source);

            Assert.That(transformed, Does.Contain("value++;"));
            Assert.That(transformed, Does.Contain("SendCustomEventDelayedFrames(\"__Loop_MoveNext\", 1, global::VRC.Udon.Common.Enums.EventTiming.PostLateUpdate);"));
            Assert.That(transformed, Does.Contain("__Loop_elapsed += Time.deltaTime;"));
            Assert.That(transformed, Does.Not.Contain("await UdonTask.Yield"));
        }

        [Test]
        public void AsyncTransformer_Cutscene_PreservesAnimationStatementsAndChildAwaits()
        {
            string source = File.ReadAllText("Assets/Scripts/CEShowcase/Station4_Cutscene/CutsceneController.cs");

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Async.AsyncMethodTransformOptimizer", source);

            Assert.That(transformed, Does.Contain("__FadeScreenAsync_Start"));
            Assert.That(transformed, Does.Contain("\"__PlayCutsceneAsync_MoveNext\""));
            Assert.That(transformed, Does.Contain("_fadeCanvasGroup.alpha = Mathf.Lerp"));
            Assert.That(transformed, Does.Contain("_curtainLeft.localPosition = Vector3.Lerp"));
            Assert.That(transformed, Does.Not.Contain("await FadeScreenAsync"));
        }

        /// <summary>
        /// A return nested inside an if has to complete the state machine, not just leave MoveNext.
        /// The lowering used to emit such a statement verbatim, which skipped __X_Complete(), left
        /// __X_running latched on and made the behaviour refuse every later start of that method --
        /// one cancelled cutscene bricked the station for the rest of the session. The shape below is
        /// exactly what CE's CancellationToken documentation tells authors to write.
        /// </summary>
        [Test]
        public void AsyncTransformer_ReturnNestedInIf_CompletesTheStateMachine()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Async;
using UnityEngine;

public class CancelFixture : UdonSharpBehaviour
{
    private CancellationTokenSource _cts;

    public async UdonTask RunAsync(CancellationToken token)
    {
        float elapsed = 0f;
        while (elapsed < 1f)
        {
            if (token.IsCancellationRequested) return;

            await UdonTask.Yield();
            elapsed += Time.deltaTime;
        }

        if (token.IsCancellationRequested) return;

        Debug.Log(""done"");
    }
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Async.AsyncMethodTransformOptimizer", source);

            // every early exit calls the completion before returning
            Assert.That(transformed, Does.Not.Match(@"if \(token\.IsCancellationRequested\)\s*return;"),
                "a nested return was emitted without a completion call");
            Assert.That(
                Regex.Matches(transformed, @"__RunAsync_Complete\(\);\s*return;").Count,
                Is.GreaterThanOrEqualTo(3),
                "expected a completion on both nested returns plus the fall-off-the-end path");
            Assert.That(transformed, Does.Contain("__RunAsync_running = false;"));
        }

        /// <summary>
        /// A return inside a lambda belongs to the lambda, not to the async method, so it must be left
        /// alone -- completing the state machine there would tear it down mid-flight.
        /// </summary>
        [Test]
        public void AsyncTransformer_ReturnInsideLambda_IsLeftAlone()
        {
            const string source = @"
using System;
using UdonSharp;
using UdonSharp.CE.Async;
using UnityEngine;

public class LambdaFixture : UdonSharpBehaviour
{
    public async UdonTask RunAsync()
    {
        Func<int, int> doubler = x => { return x * 2; };
        Debug.Log(doubler(2));

        await UdonTask.Yield();
    }
}";

            string transformed = RunOptimizer("UdonSharp.CE.Editor.Async.AsyncMethodTransformOptimizer", source);

            Assert.That(transformed, Does.Contain("return x * 2;"),
                "the lambda's own return was rewritten into a state-machine completion");
        }

        [Test]
        public void PersistenceAutoRegistration_GeneratedOverloadsUsePlayerDataJsonStorage()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Persistence;
using VRC.SDK3.Data;

[PlayerData(""test_inventory"", Version = 2)]
public class TestInventorySaveData
{
    public DataDictionary data;
}

public class PersistenceFixture : UdonSharpBehaviour
{
    public void SaveIt(TestInventorySaveData data)
    {
        CEPersistence.Save<TestInventorySaveData>(data);
        CEPersistence.Restore<TestInventorySaveData>(out data);
    }
}";

            string transformed = RunPersistenceAutoRegistration(source);

            Assert.That(transformed, Does.Contain("using VRC.SDK3.Persistence;"));
            Assert.That(transformed, Does.Contain("PlayerData.SetString(key, jsonToken.String);"));
            Assert.That(transformed, Does.Contain("PlayerData.TryGetString(localPlayer, key, out json)"));
            Assert.That(transformed, Does.Contain("RestoreResult result = FromJson(json, out model);"));
            Assert.That(transformed, Does.Contain("CEPersistence.Save(data);"));
            Assert.That(transformed, Does.Contain("CEPersistence.Restore(out data);"));
        }

        /// <summary>
        /// Path of the compiled Udon program a program asset points at, read BEFORE the program asset is
        /// deleted -- afterwards there is nothing left that knows which file in SerializedUdonPrograms
        /// belonged to it.
        /// </summary>
        private static string SerializedProgramPathOf(string programAssetPath)
        {
            var programAsset = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(programAssetPath);

            if (programAsset == null || programAsset.SerializedProgramAsset == null)
                return null;

            return AssetDatabase.GetAssetPath(programAsset.SerializedProgramAsset);
        }

        private static void CompileAllProgramsSync()
        {
            UdonSharp.Compiler.UdonSharpCompilerV1.CompileSync(
                new UdonSharp.Compiler.UdonSharpCompileOptions
                {
                    IsEditorBuild = true
                });
        }

        private UnityEngine.Object CreateProgramAssetForBehaviourType(Type behaviourType)
        {
            GameObject go = new GameObject("CE.ProgramAssetTest");
            _createdObjects.Add(go);

            UdonSharpBehaviour behaviour = (UdonSharpBehaviour)go.AddComponent(behaviourType);
            MethodInfo getProgramAssetMethod = typeof(UdonSharpEditorUtility).GetMethod(
                "GetUdonSharpProgramAsset",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(UdonSharpBehaviour) },
                null);
            UnityEngine.Object asset = (UnityEngine.Object)getProgramAssetMethod.Invoke(null, new object[] { behaviour });
            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(assetPath) && !_createdAssetPaths.Contains(assetPath))
            {
                _createdAssetPaths.Add(assetPath);
            }

            return asset;
        }

        /// <summary>
        /// Runs a single CE transform over one source string.
        ///
        /// The context is given a real semantic model. That is not optional any more: CE's generators
        /// resolve attributes, types and <c>nameof</c> arguments through symbols rather than syntax, so a
        /// context without a model makes them silently skip work (no call-site rewrite, no
        /// <c>[DataField]</c> key rename) or treat a perfectly valid attribute as malformed. Running them
        /// modelless tested a configuration the real compiler never produces -- exactly the "tests bypass
        /// the pipeline" gap this suite exists to close.
        /// </summary>
        private static string RunOptimizer(string optimizerTypeName, string source)
        {
            Assembly ceEditorAssembly = GetAssemblyByName("UdonSharp.CE.Editor");
            Type optimizerType = ceEditorAssembly.GetType(optimizerTypeName, throwOnError: true);
            object optimizer = Activator.CreateInstance(optimizerType, nonPublic: true);

            Type contextType = ceEditorAssembly.GetType("UdonSharp.CE.Editor.Optimizers.OptimizationContext", throwOnError: true);
            object context = Activator.CreateInstance(contextType, nonPublic: true);

            SyntaxTree inputTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));

            MethodInfo setTrees = contextType.GetMethod(
                "SetSemanticModelTrees",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(setTrees, Is.Not.Null,
                "OptimizationContext.SetSemanticModelTrees is gone; CE transforms cannot be given a semantic model.");

            setTrees.Invoke(context, new object[] { new[] { inputTree } });

            MethodInfo optimizeMethod = optimizerType.GetMethod("Optimize", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            SyntaxTree outputTree = (SyntaxTree)optimizeMethod.Invoke(optimizer, new object[] { inputTree, context });
            return outputTree.GetText().ToString();
        }

        private static string RunPersistenceAutoRegistration(string source)
        {
            SyntaxTree inputTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
            var input = new[] { (tree: inputTree, filePath: "PersistenceFixture.cs") };

            Assembly ceEditorAssembly = GetAssemblyByName("UdonSharp.CE.Editor");
            Type optimizerType = ceEditorAssembly.GetType("UdonSharp.CE.Editor.Persistence.CEPersistenceAutoRegistrationOptimizer", throwOnError: true);
            MethodInfo applyMethod = optimizerType.GetMethod("Generate", BindingFlags.Static | BindingFlags.NonPublic);
            var output = ((SyntaxTree tree, string filePath)[])applyMethod.Invoke(null, new object[] { input });

            return string.Join(
                Environment.NewLine,
                output.Select(item => item.tree.GetText().ToString()));
        }

        private static Assembly CompileAssembly(string source, string assemblyNamePrefix)
        {
            SyntaxTree syntaxTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
            var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                .Select(assembly => assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => MetadataReference.CreateFromFile(path))
                .Cast<MetadataReference>()
                .ToArray();

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyNamePrefix + "." + Guid.NewGuid().ToString("N"),
                new[] { syntaxTree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var stream = new MemoryStream())
            {
                EmitResult emitResult = compilation.Emit(stream);
                if (!emitResult.Success)
                {
                    string errors = string.Join(
                        Environment.NewLine,
                        emitResult.Diagnostics
                            .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                            .Select(diagnostic => diagnostic.ToString()));

                    Assert.Fail(errors);
                }

                stream.Seek(0, SeekOrigin.Begin);
                return Assembly.Load(stream.ToArray());
            }
        }

        private static object CreateDeepPlayer(Type playerType, string name, int score, Color color)
        {
            object player = Activator.CreateInstance(playerType);
            playerType.GetField("name").SetValue(player, name);
            playerType.GetField("score").SetValue(player, score);
            playerType.GetField("color").SetValue(player, color);
            return player;
        }

        private static IDictionary GetFieldDefinitions(UnityEngine.Object asset)
        {
            FieldInfo fieldDefinitionsField = asset.GetType().GetField("fieldDefinitions", BindingFlags.Instance | BindingFlags.Public);
            return (IDictionary)fieldDefinitionsField?.GetValue(asset);
        }

        private static bool HasFieldDefinition(UnityEngine.Object asset, string fieldName)
        {
            IDictionary fieldDefinitions = GetFieldDefinitions(asset);
            return fieldDefinitions != null && fieldDefinitions.Contains(fieldName);
        }

        private static UdonSyncMode? GetFieldDefinitionSyncMode(UnityEngine.Object asset, string fieldName)
        {
            IDictionary fieldDefinitions = GetFieldDefinitions(asset);
            object fieldDefinition = fieldDefinitions?[fieldName];
            if (fieldDefinition == null)
                return null;

            PropertyInfo syncModeProperty = fieldDefinition.GetType().GetProperty("SyncMode", BindingFlags.Instance | BindingFlags.Public);
            return (UdonSyncMode?)syncModeProperty?.GetValue(fieldDefinition);
        }

        private static void SetProgramSource(UdonBehaviour behaviour, UnityEngine.Object programAsset)
        {
            PropertyInfo programSourceProperty = typeof(UdonBehaviour).GetProperty("programSource", BindingFlags.Instance | BindingFlags.Public);
            if (programSourceProperty != null)
            {
                programSourceProperty.SetValue(behaviour, programAsset);
                return;
            }

            FieldInfo programSourceField = typeof(UdonBehaviour).GetField("programSource", BindingFlags.Instance | BindingFlags.Public);
            if (programSourceField != null)
            {
                programSourceField.SetValue(behaviour, programAsset);
            }
        }

        private static string GetAssemblyError(UnityEngine.Object asset)
        {
            FieldInfo assemblyErrorField = asset.GetType().BaseType.GetField("assemblyError", BindingFlags.Instance | BindingFlags.NonPublic);
            string assemblyError = (string)assemblyErrorField.GetValue(asset);
            return !string.IsNullOrEmpty(assemblyError)
                ? assemblyError
                : GetLastCompileDiagnosticsText(DiagnosticSeverity.Error);
        }

        private static string GetUasm(UnityEngine.Object asset)
        {
            Assembly editorAssembly = GetAssemblyByName("UdonSharp.Editor");
            Type cacheType = editorAssembly.GetType("UdonSharp.UdonSharpEditorCache", throwOnError: true);
            object instance = cacheType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            MethodInfo getUasmMethod = cacheType.GetMethod("GetUASMStr", BindingFlags.Instance | BindingFlags.Public);
            return (string)getUasmMethod.Invoke(instance, new object[] { asset });
        }

        private static string GetLastCompileDiagnosticsText(DiagnosticSeverity severity)
        {
            Assembly editorAssembly = GetAssemblyByName("UdonSharp.Editor");
            Type cacheType = editorAssembly.GetType("UdonSharp.UdonSharpEditorCache", throwOnError: true);
            object instance = cacheType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            PropertyInfo diagnosticsProperty = cacheType.GetProperty("LastCompileDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic);
            Array diagnostics = diagnosticsProperty?.GetValue(instance) as Array;
            if (diagnostics == null || diagnostics.Length == 0)
            {
                return string.Empty;
            }

            var messages = new List<string>();
            foreach (object diagnostic in diagnostics)
            {
                Type diagnosticType = diagnostic.GetType();
                FieldInfo severityField = diagnosticType.GetField("severity", BindingFlags.Instance | BindingFlags.Public);
                FieldInfo messageField = diagnosticType.GetField("message", BindingFlags.Instance | BindingFlags.Public);

                if (severityField == null || messageField == null)
                {
                    continue;
                }

                object rawSeverity = severityField.GetValue(diagnostic);
                if (string.Equals(rawSeverity?.ToString(), severity.ToString(), StringComparison.Ordinal))
                {
                    messages.Add((string)messageField.GetValue(diagnostic));
                }
            }

            return string.Join(Environment.NewLine, messages);
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
