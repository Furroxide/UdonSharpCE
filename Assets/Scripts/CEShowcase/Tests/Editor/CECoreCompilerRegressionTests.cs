using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CESyncRuntimeTests;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using UdonSharp.Compiler.Udon;
using UdonSharp.Serialization;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using VRC.Udon;
using VRC.Udon.Common.Interfaces;
using CompilerException = UdonSharp.Core.CompilerException;
using CompilerNotSupportedException = UdonSharp.Core.NotSupportedException;
using CompilerSeverity = UdonSharp.Compiler.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Regression tests for the core U# compiler, the U# collection library and the editor plumbing around them.
    ///
    /// Three kinds of test live here. The Udon VM test compiles CECoreCompilerRegressionTestBehaviour and runs it,
    /// because the defects it covers (a method's single set of parameter slots, a call's shared return slot) do
    /// not exist under the CLR. The binder tests run the real Bind phase over a fixture in its own compilation, for
    /// constructs U# has to reject with an error on the user's code. The rest call the editor code directly.
    ///
    /// Defects the shared RunAudit fixture already pins are listed in CEPerfDataRegressionTests and not repeated.
    /// </summary>
    public class CECoreCompilerRegressionTests
    {
        private const string ThisTestFilePath = "Assets/Scripts/CEShowcase/Tests/Editor/CECoreCompilerRegressionTests.cs";

        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();
        private readonly List<string> _createdAssetPaths = new List<string>();
        private bool _compiledWithFixture;

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                if (_createdObjects[i] != null)
                    UnityEngine.Object.DestroyImmediate(_createdObjects[i]);
            }

            _createdObjects.Clear();

            if (_createdAssetPaths.Count > 0)
            {
                for (int i = _createdAssetPaths.Count - 1; i >= 0; i--)
                {
                    string assetPath = _createdAssetPaths[i];

                    // Read the compiled program's path before the program asset that names it is gone.
                    string serializedProgramPath = SerializedProgramPathOf(assetPath);

                    AssetDatabase.DeleteAsset(assetPath);

                    if (!string.IsNullOrEmpty(serializedProgramPath))
                        AssetDatabase.DeleteAsset(serializedProgramPath);
                }

                _createdAssetPaths.Clear();
                AssetDatabase.Refresh();
            }

            // Leave the project compiled without the fixture, the way the next test expects to find it.
            if (_compiledWithFixture)
            {
                _compiledWithFixture = false;
                UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
            }
        }

        // -----------------------------------------------------------------------------------------
        // Udon VM
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Runs RunCoreAudit in the Udon VM. Each check in it names the defect it pins:
        ///
        /// static-initializer-dependency -- a method called only from a static field initializer was never emitted,
        /// so the compile died with "Cannot jump to uninitialized jump label". recursive-receiver -- a [RecursiveMethod]
        /// call on the caller's own parameter ran on the argument that overwrote that parameter's slot. nested-stack,
        /// nested-hashset -- a foreach kept its enumerator in GetEnumerator's shared return slot (and Stack handed out
        /// one cached enumerator), so the inner loop ended the outer one. pending-remove -- Remove(key, out value)
        /// ignored entries still parked in a serialized initializer. as -- 'as' was a plain copy and never produced
        /// null. sizeof-char -- folded to 1.
        ///
        /// It then reads a Queue and a Stack back off the heap through the editor serializers: QueueSerializer invoked
        /// the U# queue's Enqueue on a System queue (TargetException), and StackSerializer, trusting a ToArray that
        /// was bottom-first, handed back the stack upside down.
        /// </summary>
        [Test]
        public void CoreCompilerFixture_ExecutesCorrectlyInUdon()
        {
            UdonSharpProgramAsset asset = CreateProgramAssetForFixture();
            CompileAll();

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (UdonSyncHarness.BehaviourPair pair = UdonSyncHarness.CreatePair(asset))
            {
                UdonBehaviour behaviour = pair.Owner;
                behaviour.SendCustomEvent(nameof(CECoreCompilerRegressionTestBehaviour.RunCoreAudit));

                Assert.That(behaviour.GetProgramVariable("auditRan"), Is.EqualTo(true),
                    "The Udon VM halted during check '" + behaviour.GetProgramVariable("lastCheck") + "'.");

                Assert.That(behaviour.GetProgramVariable("auditFailures"), Is.EqualTo(""),
                    "Checks that produced the wrong answer, by name.");

                IUdonProgram program = pair.ProgramOf(behaviour);

                Assert.That(ReadThroughSerializer<Queue<int>>(program, "_serialQueue").ToArray(), Is.EqualTo(new[] { 2, 3, 4 }),
                    "A Queue read back from the Udon heap lost its contents or their order.");

                Assert.That(ReadThroughSerializer<Stack<int>>(program, "_serialStack").ToArray(), Is.EqualTo(new[] { 3, 2, 1 }),
                    "A Stack read back from the Udon heap came back in the wrong order; ToArray lists the top first.");
            }
        }

        // -----------------------------------------------------------------------------------------
        // Binder: constructs U# rejects at the user's source
        // -----------------------------------------------------------------------------------------

        private const string ConstructorFixture = @"
public class BinderFixtureChainedConstructor
{
    public int value;

    public BinderFixtureChainedConstructor() : this(3) { }

    public BinderFixtureChainedConstructor(int initial) { value = initial; }
}

public class BinderFixtureBaseConstructor
{
    public int value;

    public BinderFixtureBaseConstructor() : base() { value = 1; }
}
";

        /// <summary>
        /// U# binds a constructor's body and nothing else, so ': this(...)' silently never ran: CERandom() chained
        /// to CERandom(seed), left its state all zero, and every draw returned 0.
        /// </summary>
        [Test]
        public void ConstructorChaining_IsRejectedAtTheInitializer()
        {
            Exception failure = BindFixture(ConstructorFixture, "BinderFixtureChainedConstructor",
                type => type.InstanceConstructors.Single(constructor => constructor.Parameters.Length == 0));

            AssertRejectedAt(failure, "Constructor chaining", ": this(3)");
        }

        [Test]
        public void ConstructorsWithoutChaining_Bind()
        {
            Assert.That(BindFixture(ConstructorFixture, "BinderFixtureBaseConstructor",
                type => type.InstanceConstructors.Single()), Is.Null);

            Assert.That(BindFixture(ConstructorFixture, "BinderFixtureChainedConstructor",
                type => type.InstanceConstructors.Single(constructor => constructor.Parameters.Length == 1)), Is.Null);
        }

        private const string AsFixture = @"
using System.Collections.Generic;
using UnityEngine;

public class BinderFixtureAsTarget { }

public class BinderFixtureAs
{
    public object boxed;
    public Transform transformField;

    public void UserClassTarget()
    {
        BinderFixtureAsTarget target = boxed as BinderFixtureAsTarget;
    }

    public void CollectionTarget()
    {
        List<int> target = boxed as List<int>;
    }

    public void ExposedTargets()
    {
        Transform asTransform = boxed as Transform;
        Vector3[] asArray = boxed as Vector3[];
        Component asComponent = transformField as Component;
        object asObject = transformField as object;
    }
}
";

        /// <summary>
        /// 'as' used to be a plain copy, so it never produced null and the caller's null check never fired. U# can
        /// only test a runtime type that Udon represents directly: a user class has no Udon type, and a U# List is
        /// an object[] like every other U# object, so a test against either would answer the wrong question.
        /// </summary>
        [TestCase("UserClassTarget", "boxed as BinderFixtureAsTarget")]
        [TestCase("CollectionTarget", "boxed as List<int>")]
        public void As_ToATypeUdonCannotTest_IsRejected(string method, string expression)
        {
            Exception failure = BindFixture(AsFixture, "BinderFixtureAs", type => type.GetMembers(method).Single());

            AssertRejectedAt(failure, "runtime type test", expression);
        }

        /// <summary>
        /// The accepted half: conversions C# can prove, and runtime tests against types Udon exposes. The latter is
        /// what UdonSharp.CE.Perf.CEWorldExtensions does ('GetComponentArray(slot) as Vector3[]'), so rejecting it
        /// would stop every caller of those helpers compiling.
        /// </summary>
        [Test]
        public void As_ToExposedTypes_Binds()
        {
            Assert.That(BindFixture(AsFixture, "BinderFixtureAs", type => type.GetMembers("ExposedTargets").Single()), Is.Null);
        }

        private const string StaticConstructorFixture = @"
public static class BinderFixtureStaticConstructor
{
    public static int value;

    static BinderFixtureStaticConstructor() { value = 3; }
}

public static class BinderFixtureStaticInitializers
{
    public static int value = 3;
    public static int twice = value * 2;
}
";

        /// <summary>
        /// U# runs static field initializers on first use but never an explicit static constructor's body, so
        /// whatever it assigned silently stayed at its default.
        /// </summary>
        [Test]
        public void ExplicitStaticConstructor_IsRejected()
        {
            Exception failure = BindFixture(StaticConstructorFixture, "BinderFixtureStaticConstructor");

            AssertRejectedAt(failure, "static constructors", "static BinderFixtureStaticConstructor()");
        }

        [Test]
        public void StaticFieldInitializers_Bind()
        {
            Assert.That(BindFixture(StaticConstructorFixture, "BinderFixtureStaticInitializers"), Is.Null);
        }

        private const string ForEachFixture = @"
using UdonSharp.CE.Data;

public class BinderFixtureForEach
{
    public CEList<int> list;
    public int[] array;

    public int OverCEList()
    {
        int sum = 0;
        foreach (int value in list)
            sum += value;
        return sum;
    }

    public int OverArray()
    {
        int sum = 0;
        foreach (int value in array)
            sum += value;
        return sum;
    }
}
";

        /// <summary>
        /// A foreach over anything enumerable that U# has no lowering for -- CEList, CEDictionary, an IEnumerable --
        /// fell through to the array loop and failed in the middle of emit with an internal "Cannot get element type
        /// on non-array types" exception instead of an error on the loop.
        /// </summary>
        [Test]
        public void ForEach_OverACECollection_IsRejectedAtTheLoop()
        {
            Exception failure = BindFixture(ForEachFixture, "BinderFixtureForEach", type => type.GetMembers("OverCEList").Single());

            AssertRejectedAt(failure, "foreach over", "list");
        }

        [Test]
        public void ForEach_OverAnArray_Binds()
        {
            Assert.That(BindFixture(ForEachFixture, "BinderFixtureForEach", type => type.GetMembers("OverArray").Single()), Is.Null);
        }

        // -----------------------------------------------------------------------------------------
        // Editor
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// Looking up a script's program asset auto-creates one next to the script. It used to adopt whatever
        /// UdonSharpProgramAsset already sat at that path, whichever script it belonged to, and create over the path
        /// otherwise -- replacing an unrelated asset that merely shared the script's name.
        /// </summary>
        [Test]
        public void ProgramAssetAutoCreate_DoesNotTakeAnAssetBelongingToAnotherScript()
        {
            CECoreCompilerRegressionTestBehaviour fixture = CreateFixtureComponent();
            MonoScript fixtureScript = MonoScript.FromMonoBehaviour(fixture);
            string sidecarPath = Path.ChangeExtension(AssetDatabase.GetAssetPath(fixtureScript), ".asset");

            Assert.That(AssetDatabase.LoadMainAssetAtPath(sidecarPath), Is.Null,
                $"'{sidecarPath}' already exists, so this test cannot place its own asset there.");

            MonoScript otherScript = AssetDatabase.LoadAssetAtPath<MonoScript>(ThisTestFilePath);
            Assert.That(otherScript, Is.Not.Null, $"No MonoScript at '{ThisTestFilePath}'.");

            // Current versions, so importing it queues no upgrade pass over the script it names.
            UdonSharpProgramAsset blocker = ScriptableObject.CreateInstance<UdonSharpProgramAsset>();
            blocker.sourceCsScript = otherScript;
            blocker.ScriptVersion = UdonSharpProgramVersion.CurrentVersion;
            blocker.CompiledVersion = UdonSharpProgramVersion.CurrentVersion;

            AssetDatabase.CreateAsset(blocker, sidecarPath);
            _createdAssetPaths.Add(sidecarPath);

            UdonSharpProgramAsset created = UdonSharpEditorUtility.GetUdonSharpProgramAsset(fixture);
            string createdPath = created != null ? AssetDatabase.GetAssetPath(created) : null;

            if (!string.IsNullOrEmpty(createdPath) && createdPath != sidecarPath)
                _createdAssetPaths.Add(createdPath);

            Assert.That(created, Is.Not.Null, "No program asset was found or created for the fixture script.");
            Assert.That(created.sourceCsScript, Is.EqualTo(fixtureScript),
                "The fixture got a program asset that belongs to another script.");
            Assert.That(createdPath, Is.Not.EqualTo(sidecarPath));

            UdonSharpProgramAsset survivor = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(sidecarPath);
            Assert.That(survivor, Is.Not.Null, "The asset that was already at the script's path was replaced.");
            Assert.That(survivor.sourceCsScript, Is.EqualTo(otherScript), "The asset that was already there was rebound.");
        }

        /// <summary>
        /// Only C# script compile errors may stop a U# compile. Asset import errors used to count too, so one bad
        /// texture refused every compile and a world build went on to upload the programs from before it.
        /// </summary>
        [TestCase(1 << 11, true)]
        [TestCase((1 << 11) | 1, true)]
        [TestCase(1 << 6, false)]
        [TestCase((1 << 6) | 1, false)]
        [TestCase(1, false)]
        public void ConsoleEntry_OnlyScriptCompileErrorsBlockTheCompile(int consoleMode, bool blocks)
        {
            Assert.That(UdonSharpUtils.IsScriptCompileErrorMode(consoleMode), Is.EqualTo(blocks));
        }

        // -----------------------------------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------------------------------

        private CECoreCompilerRegressionTestBehaviour CreateFixtureComponent()
        {
            var host = new GameObject("CE.CoreCompilerRegressionFixture") { hideFlags = HideFlags.HideAndDontSave };
            _createdObjects.Add(host);

            return host.AddComponent<CECoreCompilerRegressionTestBehaviour>();
        }

        /// <summary>
        /// Gives the fixture a program asset so the Udon compiler picks it up; TearDown deletes it again together
        /// with the serialized program it produces.
        /// </summary>
        private UdonSharpProgramAsset CreateProgramAssetForFixture()
        {
            CECoreCompilerRegressionTestBehaviour fixture = CreateFixtureComponent();

            UdonSharpProgramAsset asset = UdonSharpEditorUtility.GetUdonSharpProgramAsset(fixture);
            Assert.That(asset, Is.Not.Null, "No program asset could be created for the Udon VM fixture.");

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(assetPath) && !_createdAssetPaths.Contains(assetPath))
                _createdAssetPaths.Add(assetPath);

            return asset;
        }

        private void CompileAll()
        {
            _compiledWithFixture = true;
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });

            string[] errors = UdonSharpEditorCache.Instance.LastCompileDiagnostics?
                .Where(diagnostic => diagnostic.severity == CompilerSeverity.Error)
                .Select(diagnostic => diagnostic.file + ": " + diagnostic.message)
                .ToArray() ?? Array.Empty<string>();

            Assert.That(errors, Is.Empty, "Project failed to compile:\n" + string.Join("\n", errors));
        }

        /// <summary>
        /// The serialized program's path, worked out from the program asset's GUID the way the SDK names it. Reading
        /// SerializedProgramAsset instead would create that asset, and refresh the program, for a program asset that
        /// was never compiled -- which is every asset the program-asset test makes.
        /// </summary>
        private static string SerializedProgramPathOf(string programAssetPath)
        {
            string guid = AssetDatabase.AssetPathToGUID(programAssetPath);

            if (string.IsNullOrEmpty(guid))
                return null;

            string serializedProgramPath = "Assets/SerializedUdonPrograms/" + guid + ".asset";

            return AssetDatabase.LoadMainAssetAtPath(serializedProgramPath) != null ? serializedProgramPath : null;
        }

        /// <summary>
        /// Reads a U# collection field off the Udon heap into its System.Collections.Generic type, the way the
        /// inspector does in play mode.
        /// </summary>
        private static T ReadThroughSerializer<T>(IUdonProgram program, string fieldName) where T : class
        {
            Assert.That(program.SymbolTable.TryGetAddressFromSymbol(fieldName, out uint address), Is.True,
                $"The program has no heap symbol '{fieldName}'.");

            object[] heapValue = program.Heap.GetHeapVariable<object[]>(address);
            Assert.That(heapValue, Is.Not.Null, $"'{fieldName}' is null on the heap; RunCoreAudit did not set it.");

            object target = null;

            lock (UsbSerializationContext.UsbLock)
            {
                UsbSerializationContext.SerializedObjectMap.Clear();
                Serializer.CreatePooled(typeof(T)).ReadWeak(ref target, new SimpleValueStorage<object[]>(heapValue));
            }

            Assert.That(target, Is.InstanceOf<T>());
            return (T)target;
        }

        private static IReadOnlyList<MetadataReference> _fixtureReferences;

        /// <summary>
        /// The compiler's own references plus the U# runtime assemblies it leaves out, because a real compile feeds
        /// their sources in instead and a fixture compilation has none.
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

            foreach (string assemblyName in new[] { "UdonSharp.Runtime", "UdonSharp.Lib", "UdonSharp.CE.Runtime" })
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => candidate.GetName().Name == assemblyName);

                if (assembly == null || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                    continue;

                if (locations.Add(assembly.Location))
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }

            _fixtureReferences = references;
            return _fixtureReferences;
        }

        /// <summary>
        /// Runs the real Bind phase over one type of a fixture compiled on its own, the way BindAllPrograms does: the
        /// type first, then the member selected (if any). Returns whatever the binder threw, or null.
        /// </summary>
        private static Exception BindFixture(string source, string typeName, Func<INamedTypeSymbol, ISymbol> selectMember = null)
        {
            CompilerUdonInterface.CacheInit();

            SyntaxTree tree = CSharpSyntaxTree.ParseText(source,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9), "CECoreCompilerBinderFixture.cs");

            CSharpCompilation compilation = CSharpCompilation.Create(
                "CECoreCompilerBinderFixture." + Guid.NewGuid().ToString("N"),
                new[] { tree },
                FixtureReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            string[] errors = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == RoslynSeverity.Error)
                .Select(diagnostic => diagnostic.ToString())
                .ToArray();

            Assert.That(errors, Is.Empty, "The fixture is not valid C#, so nothing here tests the binder:\n" + string.Join("\n", errors));

            INamedTypeSymbol type = compilation.GetTypeByMetadataName(typeName);
            Assert.That(type, Is.Not.Null, $"The fixture declares no type '{typeName}'.");

            var compilationContext = new CompilationContext(new UdonSharpCompileOptions { IsEditorBuild = true })
            {
                RoslynCompilation = compilation
            };

            Symbol[] members = Array.Empty<Symbol>();

            if (selectMember != null)
                members = new[] { new BindContext(compilationContext, type, Array.Empty<Symbol>()).GetSymbol(selectMember(type)) };

            try
            {
                new BindContext(compilationContext, type, members).Bind();
            }
            catch (Exception exception)
            {
                return exception;
            }

            return null;
        }

        private static void AssertRejectedAt(Exception failure, string messageFragment, string sourceFragment)
        {
            Assert.That(failure, Is.InstanceOf<CompilerNotSupportedException>(),
                "Expected the binder to reject the fixture with a NotSupportedException, got: " + failure);

            Assert.That(failure.Message, Does.Contain(messageFragment));

            Location location = ((CompilerException)failure).Location;
            Assert.That(location, Is.Not.Null, "The rejection carries no source location, so it cannot point at the user's code.");
            Assert.That(location.SourceTree, Is.Not.Null);
            Assert.That(location.SourceTree.GetText().ToString(location.SourceSpan), Does.Contain(sourceFragment),
                "The rejection points somewhere other than the construct it rejects.");
        }
    }
}
