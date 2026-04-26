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
                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetDatabase.DeleteAsset(assetPath);
                }
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
            Assert.That(transformed, Does.Contain("_CE_SyncOnJoinInbound"));

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

            string payload = (string)type.GetField("_CE_SyncOnJoinPayload").GetValue(sourceInstance);
            Assert.That(payload, Is.Not.Null.And.Not.Empty);

            Component destinationInstance = destinationObject.AddComponent(type);
            type.GetField("_CE_SyncOnJoinInbound").SetValue(destinationInstance, payload);
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

            string payload = (string)fixtureType.GetField("_CE_SyncOnJoinPayload").GetValue(sourceInstance);
            Assert.That(payload, Does.Contain("phase_id"));
            Assert.That(payload, Does.Contain("players"));

            Component destinationInstance = destinationObject.AddComponent(fixtureType);
            fixtureType.GetField("_CE_SyncOnJoinInbound").SetValue(destinationInstance, payload);
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

            Assert.That(transformed, Does.Contain("SerializeTarget()"));
            Assert.That(transformed, Does.Contain("DeserializeTarget(__ce_value.IsNull ? null : __ce_value.String)"));

            Assembly assembly = CompileAssembly(transformed, "SyncOnJoinCustomSerializerTests");
            Type type = assembly.GetType("SyncOnJoinCustomSerializerFixture");
            GameObject sourceObject = new GameObject("SyncOnJoinCustomSource");
            GameObject destinationObject = new GameObject("SyncOnJoinCustomDestination");
            _createdObjects.Add(sourceObject);
            _createdObjects.Add(destinationObject);

            Component sourceInstance = sourceObject.AddComponent(type);
            type.GetMethod("_CE_SyncOnJoin_Serialize").Invoke(sourceInstance, null);
            string payload = (string)type.GetField("_CE_SyncOnJoinPayload").GetValue(sourceInstance);

            Component destinationInstance = destinationObject.AddComponent(type);
            type.GetField("_CE_SyncOnJoinInbound").SetValue(destinationInstance, payload);
            type.GetMethod("_CE_SyncOnJoin_Deserialize_Trigger").Invoke(destinationInstance, null);

            Assert.That((bool)type.GetField("restored").GetValue(destinationInstance), Is.True);
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
        public void QuantizeEmitter_RoundsQuantizedFieldsDuringOnPreSerialization()
        {
            const string source = @"
using UdonSharp;
using UdonSharp.CE.Net;

public class QuantizeFixture : UdonSharpBehaviour
{
    [Sync(Quantize = 0.1f)]
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
            Assert.That(transformed, Does.Contain("SendCustomEventDelayedFrames(\"__Loop_MoveNext\", 1);"));
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

        private static string RunOptimizer(string optimizerTypeName, string source)
        {
            Assembly ceEditorAssembly = GetAssemblyByName("UdonSharp.CE.Editor");
            Type optimizerType = ceEditorAssembly.GetType(optimizerTypeName, throwOnError: true);
            object optimizer = Activator.CreateInstance(optimizerType, nonPublic: true);

            Type contextType = ceEditorAssembly.GetType("UdonSharp.CE.Editor.Optimizers.OptimizationContext", throwOnError: true);
            object context = Activator.CreateInstance(contextType, nonPublic: true);

            SyntaxTree inputTree = CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest));
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
            MethodInfo applyMethod = optimizerType.GetMethod("Apply", BindingFlags.Static | BindingFlags.NonPublic);
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
