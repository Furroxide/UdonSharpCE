using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;
using UdonSharp.Compiler;

namespace CEShowcase.Tests.Editor
{
    internal static class CEAuditCompilerFixture
    {
        internal static SyntaxTree[] Sources(params string[] fixtures)
        {
            var options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9);
            return fixtures.Select((source, i) => CSharpSyntaxTree.ParseText(source, options, "AuditFixture" + i + ".cs"))
                .Concat(Directory.GetFiles("Packages/com.merlin.UdonSharp/Runtime/Libraries/CE", "*.cs", SearchOption.AllDirectories)
                    .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), options, path)))
                .ToArray();
        }

        internal static Type EditorType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Single(a => a.GetName().Name == "UdonSharp.CE.Editor").GetType(name, true);

        internal static SyntaxTree[] Transform(string name, params string[] fixtures)
        {
            SyntaxTree[] trees = Sources(fixtures);
            Type contextType = EditorType("UdonSharp.CE.Editor.Optimizers.OptimizationContext");
            object context = Activator.CreateInstance(contextType, true);
            contextType.GetMethod("SetSemanticModelTrees").Invoke(context, new object[] { trees });
            Type passType = EditorType(name);
            object pass = Activator.CreateInstance(passType, true);
            for (int i = 0; i < fixtures.Length; i++)
                trees[i] = (SyntaxTree)Invoke(passType.GetMethod("Optimize"), pass, trees[i], context);
            return trees;
        }

        internal static SyntaxTree[] Persistence(params string[] fixtures)
        {
            SyntaxTree[] trees = Sources(fixtures);
            var input = trees.Select(tree => (tree, tree.FilePath)).ToArray();
            var output = ((SyntaxTree tree, string filePath)[])Invoke(EditorType(
                "UdonSharp.CE.Editor.Persistence.CEPersistenceAutoRegistrationOptimizer")
                .GetMethod("Generate", BindingFlags.Static | BindingFlags.NonPublic), null, new object[] { input });
            return output.Select(pair => pair.tree).ToArray();
        }

        internal static object Invoke(MethodInfo method, object instance, params object[] arguments)
        {
            try { return method.Invoke(instance, arguments); }
            catch (TargetInvocationException failure) { throw failure.InnerException ?? failure; }
        }

        internal static CSharpCompilation Compile(SyntaxTree[] trees)
        {
            var refs = new List<MetadataReference>(CompilationContext.GetMetadataReferences());
            foreach (string name in new[] { "UdonSharp.Runtime", "UdonSharp.Lib" })
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().Single(a => a.GetName().Name == name);
                if (!refs.OfType<PortableExecutableReference>().Any(r => r.FilePath == assembly.Location))
                    refs.Add(MetadataReference.CreateFromFile(assembly.Location));
            }
            refs.RemoveAll(r => r is PortableExecutableReference pe &&
                Path.GetFileNameWithoutExtension(pe.FilePath) == "UdonSharp.CE.Runtime");
            var compilation = CSharpCompilation.Create("CEAudit_" + Guid.NewGuid().ToString("N"), trees, refs,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            var errors = compilation.GetDiagnostics().Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error).ToArray();
            Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(d => d.ToString())));
            return compilation;
        }

        internal static object Run(SyntaxTree[] trees, string method = "Run")
        {
            var compilation = Compile(trees);
            using (var stream = new MemoryStream())
            {
                var emitted = compilation.Emit(stream);
                Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
                Assembly assembly = Assembly.Load(stream.ToArray());
                return Invoke(assembly.GetType("AuditEntry").GetMethod(method), null);
            }
        }
    }

    public class CEAuditTransformRegressionTests
    {
        private const string Inliner = "UdonSharp.CE.Editor.Optimizers.LambdaInliningOptimizer";
        private const string Callbacks = "UdonSharp.CE.Editor.Optimizers.ActionToCallbackTransformer";
        private const string Logger = "UdonSharp.CE.Editor.Optimizers.CELoggerTransformOptimizer";

        [TestCase("System.Collections.Generic.List<int>")]
        [TestCase("UdonSharp.CE.Data.CEList<int>")]
        public void ForEach_BlockReturn_SkipsOnlyThatElement(string type)
        {
            string source = @"public static class AuditEntry { public static int Run() {
                var values = new TYPE(); values.Add(1); values.Add(2); values.Add(3);
                int sum=0; values.ForEach(value => { if(value==2) return; sum+=value; });
                return sum+10; }}".Replace("TYPE", type);
            Assert.That(CEAuditCompilerFixture.Run(CEAuditCompilerFixture.Transform(Inliner, source)), Is.EqualTo(14));
        }

        [Test]
        public void ForEach_TemporaryNames_DoNotShadowUserLocals()
        {
            const string source = "public static class AuditEntry { public static int Run(){ int __ce_each_i0=5; int sum=0; var list=new System.Collections.Generic.List<int>{1,2}; list.ForEach(x=>sum+=x); return sum+__ce_each_i0; } }";
            Assert.That(CEAuditCompilerFixture.Run(CEAuditCompilerFixture.Transform(Inliner, source)), Is.EqualTo(8));
        }

        [Test]
        public void ForEach_Substitution_PreservesNamesAndNestedReferences()
        {
            const string source = @"public static class AuditEntry {
                static int Echo(int value) { return value; }
                public static int Run() { int sum=0;
                var values=new System.Collections.Generic.List<int>{2,3};
                values.ForEach(value => { System.Func<int> nested=()=>value;
                    sum+=Echo(value:nested()); }); return sum; }}";
            Assert.That(CEAuditCompilerFixture.Run(CEAuditCompilerFixture.Transform(Inliner, source)), Is.EqualTo(5));
        }

        [Test]
        public void Action_CrossFileCallerAndDeclaration_ConvertTogether()
        {
            var trees = CEAuditCompilerFixture.Transform(Callbacks,
                "using UdonSharp; public class Sink : UdonSharpBehaviour { public void Register(System.Action action) { action.Invoke(); } }",
                "using UdonSharp; public class Caller : UdonSharpBehaviour { public Sink sink; public void Done(){} public void Go(){sink.Register(()=>Done());} }");
            CEAuditCompilerFixture.Compile(trees);
            Assert.That(trees[0].ToString(), Does.Contain("CECallback"));
            Assert.That(trees[1].ToString(), Does.Contain("CECallback"));
        }

        [Test]
        public void Action_ExplicitConstruction_UsesExportedEventName()
        {
            var trees = CEAuditCompilerFixture.Transform(Callbacks,
                "using UdonSharp; public class Fixture : UdonSharpBehaviour { public void Go(){System.Action a=new System.Action(Interact); a.Invoke();} public override void Interact(){} }");
            CEAuditCompilerFixture.Compile(trees);
            Assert.That(trees[0].ToString(), Does.Contain("\"_interact\""));
        }

        [Test]
        public void Action_InheritedExternEventWithoutOverride_IsRejected()
        {
            Assert.Throws<CodeGenerationException>(() => CEAuditCompilerFixture.Transform(Callbacks,
                "using UdonSharp; public class Fixture : UdonSharpBehaviour { public void Go(){System.Action action=()=>Interact(); action.Invoke();} }"));
        }

        [TestCase("System.Action a = () => {};")]
        [TestCase("void A(){System.Action a=()=>{};}")]
        public void Action_PlainClass_IsLeftAsWritten(string member)
        {
            string source = "public class Ordinary {" + member + "}";
            var trees = CEAuditCompilerFixture.Transform(Callbacks, source);
            Assert.That(trees[0].ToString(), Is.EqualTo(source));
            CEAuditCompilerFixture.Compile(trees);
        }

        [TestCase("Warning", "LogWarning")]
        [TestCase("Error", "LogError")]
        [TestCase("Info", "Log")]
        public void Logger_ConstantLevel_RoutesSeverity(string level, string target)
        {
            var trees = CEAuditCompilerFixture.Transform(Logger,
                "using UdonSharp.CE.DevTools; public static class AuditEntry { public static void Run(){ CELogger.Log(\"msg\",LogLevel." + level + "); } }");
            CEAuditCompilerFixture.Compile(trees);
            Assert.That(trees[0].ToString(), Does.Contain("UnityEngine.Debug." + target + "("));
        }

        [Test]
        public void Logger_ReorderedNamedArguments_ArePreserved()
        {
            const string source = "using UdonSharp.CE.DevTools; public static class AuditEntry { public static void Run(){ CELogger.Log(level:LogLevel.Error,message:\"msg\"); } }";
            var trees = CEAuditCompilerFixture.Transform(Logger, source);
            Assert.That(trees[0].ToString(), Is.EqualTo(source));
        }
    }

    public class CEAuditSnapshotGeneratorRegressionTests
    {
        [Test]
        public void SyncOnJoin_SealedAndInheritedHandlers_Compile()
        {
            var trees = CEAuditCompilerFixture.Transform("UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter", @"
                using UdonSharp; using UdonSharp.CE.Net; using VRC.SDKBase;
                public class BaseState : UdonSharpBehaviour { [SyncOnJoin] public int score;
                public override void OnPlayerJoined(VRCPlayerApi player){ if(player==null) return; } }
                public sealed class DerivedState : BaseState {
                public override void OnPlayerJoined(VRCPlayerApi player){ if(player==null) return; }
                public override void OnDeserialization(){ if(score<0) return; }
                }");
            CEAuditCompilerFixture.Compile(trees);
        }

        [Test]
        public void Quantize_InheritedEarlyReturn_StillCallsInheritedHelper()
        {
            var trees = CEAuditCompilerFixture.Transform("UdonSharp.CE.Editor.Optimizers.QuantizePreSerializeEmitter", @"
                using UdonSharp; using UdonSharp.CE.Net;
                public class BaseState : UdonSharpBehaviour { [Sync(Authority.Owner,Quantize=0.1f)] public float value; }
                public class DerivedState : BaseState { public bool skip;
                public override void OnPreSerialization(){ if(skip) return; base.OnPreSerialization(); } }");
            CEAuditCompilerFixture.Compile(trees);
            var derived = trees[0].GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax>()
                .Single(c => c.Identifier.ValueText == "DerivedState");
            var earlyReturn = derived.DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.ReturnStatementSyntax>().Single();
            Assert.That(earlyReturn.Parent.ToString(), Does.Contain("Quantize"));
        }
    }

    public class CEAuditAsyncRegressionTests
    {
        private const string Async = "UdonSharp.CE.Editor.Async.AsyncMethodTransformOptimizer";

        [Test]
        public void OrdinaryTaskAwait_IsLeftAsWritten()
        {
            const string source = "public class Ordinary { public async System.Threading.Tasks.Task Go(){await System.Threading.Tasks.Task.Delay(1);} }";
            var trees = CEAuditCompilerFixture.Transform(Async, source);
            Assert.That(trees[0].ToString(), Is.EqualTo(source));
            CEAuditCompilerFixture.Compile(trees);
        }

        [Test]
        public void Lowering_PreservesAttributesAndHoistsArrayInitializer()
        {
            var trees = CEAuditCompilerFixture.Transform(Async, @"using UdonSharp; using UdonSharp.CE.Async;
                public class Fixture : UdonSharpBehaviour { [System.Obsolete] public async UdonTask Go(){
                int[] values={1,2}; await UdonTask.Yield(); int result=values[0]; } }");
            CEAuditCompilerFixture.Compile(trees);
            Assert.That(trees[0].ToString(), Does.Contain("System.Obsolete").And.Contain("new int[]"));
        }

        [Test]
        public void ChildAwait_NamedArguments_AreBoundToParameters()
        {
            var trees = CEAuditCompilerFixture.Transform(Async, @"using UdonSharp; using UdonSharp.CE.Async;
                public class Fixture : UdonSharpBehaviour {
                public async UdonTask Parent(){ await Child(second:2, first:1); }
                public async UdonTask Child(int first,int second){ await UdonTask.Yield(); } }");
            CEAuditCompilerFixture.Compile(trees);
            var childStart = trees[0].GetRoot().DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.InvocationExpressionSyntax>()
                .Single(call => call.Expression.ToString() == "__Child_Start" &&
                    call.ArgumentList.Arguments.Last().Expression.ToString() == "\"__Parent_MoveNext\"");
            Assert.That(childStart.ArgumentList.Arguments.Take(2).Select(argument => argument.Expression.ToString()),
                Is.EqualTo(new[] { "1", "2" }));
        }

        [TestCase("public async UdonTask Go<T>() { await UdonTask.Yield(); }")]
        [TestCase("public async UdonTask Go() { try { await UdonTask.Yield(); } finally {} }")]
        public void UnsupportedAsyncShape_FailsAtUserSource(string method)
        {
            Exception failure = Assert.Throws<CodeGenerationException>(() => CEAuditCompilerFixture.Transform(Async,
                "using UdonSharp; using UdonSharp.CE.Async; public class Fixture:UdonSharpBehaviour {" + method + "}"));
            Assert.That(((CodeGenerationException)failure).GeneratorId, Is.EqualTo("CE0025"));
        }
    }

    public class CEAuditPersistenceGeneratorRegressionTests
    {
        [Test]
        public void Converters_RoundTripNullsArraysAndIntegerLimits()
        {
            var trees = CEAuditCompilerFixture.Persistence(@"using UdonSharp.CE.Persistence;
                [PlayerData(""audit"")] public class Model {
                [PersistKey(""name"")] public string name;
                [PersistKey(""numbers"")] public int[] numbers;
                [PersistKey(""large"")] public long large;
                [PersistKey(""unsigned"")] public ulong unsigned;
                }
                public static class AuditEntry { public static bool Run(){
                var model=new Model{ numbers=new[]{1,2}, large=long.MinValue, unsigned=ulong.MaxValue};
                var json=CEPersistence.ToJson(model);
                return CEPersistence.FromJson<Model>(json,out var restored)==RestoreResult.Success &&
                    restored.name==null && restored.numbers[1]==2 && restored.large==long.MinValue && restored.unsigned==ulong.MaxValue;
                }}");
            Assert.That(CEAuditCompilerFixture.Run(trees), Is.EqualTo(true));
        }

        [TestCase("\"text\"")]
        [TestCase("1e100")]
        [TestCase("true")]
        public void CorruptNumericField_ReturnsParseError(string value)
        {
            string source = @"using UdonSharp.CE.Persistence;
                [PlayerData(""audit"")] public class Model { [PersistKey(""n"")] public int n; }
                public static class AuditEntry { public static bool Run(){
                return CEPersistence.FromJson<Model>(JSON,out var model)==RestoreResult.ParseError && model==null;
                }}".Replace("JSON", Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral("{\"n\":" + value + "}", true));
            Assert.That(CEAuditCompilerFixture.Run(CEAuditCompilerFixture.Persistence(source)), Is.EqualTo(true));
        }

        [Test]
        public void ValidateOutAndOptionalFloatDefault_CompileTogether()
        {
            Assert.That(CEAuditCompilerFixture.Run(CEAuditCompilerFixture.Persistence(@"using UdonSharp.CE.Persistence;
                [PlayerData(""a"")] public class Model {
                private VRC.SDK3.Data.DataDictionary scratch;
                [PersistKey(""f"",Optional=true,DefaultValue=1.5)] public float f; }
                public static class AuditEntry { public static bool Run(){
                var result=CEPersistence.FromJson<Model>(""{}"",out var model);
                return result==RestoreResult.Success && model.f==1.5f && CEPersistence.Validate<Model>(model,out var errors);
                } }")), Is.EqualTo(true));
        }

        [Test]
        public void ExplicitModelOutVar_RemainsUnambiguousWithTwoModels()
        {
            CEAuditCompilerFixture.Compile(CEAuditCompilerFixture.Persistence(@"using UdonSharp.CE.Persistence;
                [PlayerData(""a"")] public class A { [PersistKey(""n"")] public int n; }
                [PlayerData(""b"")] public class B { [PersistKey(""n"")] public int n; }
                public static class AuditEntry { public static void Run(){ CEPersistence.Restore<A>(out var a); CEPersistence.FromJson<B>(""{}"",out _); } }"));
        }

        [Test]
        public void AttributeConstants_UseInvariantCultureAndSignedValues()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                var trees = CEAuditCompilerFixture.Persistence(@"using UdonSharp.CE.Persistence;
                    [PlayerData(""a"",Version=Version)] public class Model { public const int Version=2;
                    [PersistKey(""n"",Optional=true,DefaultValue=-1), UdonSharp.CE.Persistence.Range(-0.5,10.5)] public double n; }");
                CEAuditCompilerFixture.Compile(trees);
                Assert.That(trees.Last().ToString(), Does.Contain("-0.5").And.Contain("10.5"));
            }
            finally { CultureInfo.CurrentCulture = original; }
        }

        [TestCase("[PersistKey(\"x\")] public int a; [PersistKey(\"x\")] public int b;")]
        [TestCase("[PersistKey(\"__ce_version\")] public int a;")]
        public void DuplicateOrReservedFieldKey_IsRejected(string fields)
        {
            Assert.Throws<CodeGenerationException>(() => CEAuditCompilerFixture.Persistence(
                "using UdonSharp.CE.Persistence; [PlayerData(\"a\")] public class Model {" + fields + "}"));
        }
    }
}
