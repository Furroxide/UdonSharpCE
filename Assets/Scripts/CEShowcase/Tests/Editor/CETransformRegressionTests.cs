using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;
using UdonSharp.CE.Editor.Optimizers;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Regression tests for CE's lambda inlining, Action-to-CECallback and CELogger rewrites.
    ///
    /// All three run before Roslyn sees the program, so a wrong rewrite is either a compile error on code
    /// the author never wrote or, worse, a program that compiles and does something else. Every test here
    /// runs the passes the way <c>CEGeneratorRunner</c> does -- inliner, then Action transformer, then
    /// logger, each against a semantic model of the whole tree set including the CE runtime sources -- and
    /// then compiles, and where behaviour is the point runs, what came out.
    /// </summary>
    public class CETransformRegressionTests
    {
        private const string CERuntimeFolder = "Packages/com.merlin.UdonSharp/Runtime/Libraries/CE";

        private static readonly CSharpParseOptions ParseOptions =
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9);

        private static readonly Lazy<SyntaxTree[]> CERuntimeTrees = new Lazy<SyntaxTree[]>(() =>
            Directory.GetFiles(CERuntimeFolder, "*.cs", SearchOption.AllDirectories)
                .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), ParseOptions, path))
                .ToArray());

        // ---------------------------------------------------------------------------------------------
        // ForEach inlining
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// The loop counter and the count were declared with an identifier spelled 'int' rather than the
        /// keyword, so every inlined CEList helper was a CS0246 on code the author never wrote.
        /// </summary>
        [Test]
        public void CEListHelpers_LambdasAndMethodGroups_InlineToLoopsThatRun()
        {
            const string source = @"using UdonSharp.CE.Data;
public static class TransformEntry
{
    private static int _acc;
    private static void Acc(int value) { _acc += value; }
    private static bool Small(int value) { return value < 3; }

    public static string Run()
    {
        var list = new CEList<int>(); list.Add(1); list.Add(2); list.Add(3); list.Add(4);
        int sum = 0, weighted = 0, seen = 0, until = 0;
        list.ForEach(x => sum += x);
        list.ForEachWithIndex((x, i) => weighted += x * (i + 1));
        list.ForEachUntil(x => { return ++seen > 0 && x < 3; });
        list.ForEach(Acc);
        list.ForEachUntil(Small);
        list.ForEachUntil(v => ++until < 10 && Small(v));
        return sum + "" "" + weighted + "" "" + seen + "" "" + _acc + "" "" + until;
    }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Does.Not.Contain(".ForEach"),
                "Every helper call in the fixture is a supported shape and must have become a loop.");
            Assert.That(Execute(lowered), Is.EqualTo("10 30 3 10 3"));
        }

        /// <summary>
        /// A lambda's <c>return;</c> ends the current element. Spliced verbatim it ended the method the
        /// ForEach was written in, so in a void method everything after the first early-out silently never
        /// ran.
        /// </summary>
        [Test]
        public void ForEach_BareReturnInAVoidMethod_SkipsOneElementAndTheMethodCarriesOn()
        {
            const string source = @"using UdonSharp.CE.Data;
public static class TransformEntry
{
    private static string _log = ""never reached"";

    private static void Work()
    {
        var list = new CEList<int>(); list.Add(-1); list.Add(2); list.Add(3);
        int sum = 0;
        list.ForEach(x =>
        {
            if (x < 0) return;
            sum += x;
        });
        _log = ""sum="" + sum;
    }

    public static string Run() { Work(); return _log; }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Does.Not.Contain(".ForEach"));
            Assert.That(Execute(lowered), Is.EqualTo("sum=5"));
        }

        /// <summary>
        /// Inside a loop of the lambda's own, neither <c>return;</c> nor <c>continue;</c> means "next
        /// element", so the call has to stay a lambda for CE0021 to report rather than be inlined wrongly.
        /// </summary>
        [Test]
        public void ForEach_ReturnFromInsideTheLambdasOwnLoop_IsLeftStanding()
        {
            const string source = @"using System.Collections.Generic;
public static class TransformEntry
{
    public static string Run()
    {
        var list = new List<int>(); list.Add(1); list.Add(2);
        int sum = 0;
        list.ForEach(x => { for (int k = 0; k < 3; k++) { if (k == x) return; sum += k; } });
        return sum.ToString();
    }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
            Assert.That(Execute(lowered), Is.EqualTo("1"));
        }

        /// <summary>
        /// The receiver was recognised by the spelling of its type, so a BCL List -- including one in a
        /// plain MonoBehaviour that U# compiles but never binds -- was rewritten to GetUnchecked, which only
        /// CEList has. A BCL list is read through its indexer.
        /// </summary>
        [Test]
        public void ListForEach_InAPlainMonoBehaviour_IsInlinedThroughTheIndexer()
        {
            const string source = @"using System.Collections.Generic;
using UnityEngine;
public class Roster : MonoBehaviour
{
    private List<string> _names = new List<string>();
    public string Join() { string joined = """"; _names.ForEach(name => joined += name); return joined; }
}
public static class TransformEntry
{
    public static string Run()
    {
        var names = new List<string>(); names.Add(""a""); names.Add(""b"");
        string joined = """";
        names.ForEach(name => joined += name);
        return joined;
    }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Does.Not.Contain(".ForEach"));
            Assert.That(lowered[0].ToString(), Does.Not.Contain("GetUnchecked"));
            Assert.That(Execute(lowered), Is.EqualTo("ab"));
        }

        /// <summary>
        /// A parameter that shadows a CEList field was resolved as the field, and the call was inlined
        /// against a type with no Count or GetUnchecked. The bound method decides now.
        /// </summary>
        [Test]
        public void ForEach_OnATypeThatOnlySharesANameWithACEListField_IsLeftAlone()
        {
            const string source = @"using UdonSharp.CE.Data;
public class Queue7 { public void ForEach(System.Action<int> action) { action(7); } }
public static class TransformEntry
{
    private static CEList<int> items = new CEList<int>();
    private static int _sum;
    private static void Work(Queue7 items) { items.ForEach(x => _sum += x); }
    public static string Run() { Work(new Queue7()); return _sum.ToString(); }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
            Assert.That(Execute(lowered), Is.EqualTo("7"));
        }

        /// <summary>
        /// Parameters used to be renamed by substitution: a nested lambda kept the old name (CS0103) while
        /// named-argument labels and initializer member names were renamed along with the references
        /// (CS1739, CS0117). The element is now declared under the parameter's own name.
        /// </summary>
        [Test]
        public void ForEach_NestedLambdasAndNamesThatAreNotReferences_KeepTheirMeaning()
        {
            const string source = @"using UdonSharp.CE.Data;
public class Scored { public int score; }
public static class TransformEntry
{
    private static int _total;
    private static void Take(int score) { _total += score; }

    public static string Run()
    {
        var outer = new CEList<int>(); outer.Add(1); outer.Add(2);
        var inner = new CEList<int>(); inner.Add(10); inner.Add(20);
        int sum = 0;
        outer.ForEach(a => inner.ForEach(b => sum += a * b));
        outer.ForEach(score => Take(score: score));
        outer.ForEach(score => _total += new Scored { score = score }.score);
        return sum + "" "" + _total;
    }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Does.Not.Contain(".ForEach"));
            Assert.That(Execute(lowered), Is.EqualTo("90 6"));
        }

        /// <summary>
        /// A lambda may declare a name the enclosing method also declares; a nested block may not. Such a
        /// lambda stays standing instead of being inlined into a CS0136.
        /// </summary>
        [Test]
        public void ForEach_DeclaringANameAnEnclosingLocalHas_IsLeftStanding()
        {
            const string source = @"using UdonSharp.CE.Data;
public static class TransformEntry
{
    public static string Run()
    {
        var list = new CEList<int>(); list.Add(1); list.Add(2);
        int sum = 0;
        list.ForEach(y => { int k = y; sum += k; });
        int k = 100;
        return (sum + k).ToString();
    }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
            Assert.That(Execute(lowered), Is.EqualTo("103"));
        }

        /// <summary>
        /// 'x => throw ...' is a lambda body only; spliced in as a statement it is CS8115.
        /// </summary>
        [Test]
        public void ForEach_ThrowExpressionBody_IsLeftStanding()
        {
            const string source = @"using UdonSharp.CE.Data;
public static class TransformEntry
{
    public static string Run()
    {
        var list = new CEList<int>();
        list.ForEach(v => throw new System.InvalidOperationException());
        return ""ok"";
    }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
            Assert.That(Execute(lowered), Is.EqualTo("ok"));
        }

        // ---------------------------------------------------------------------------------------------
        // Action to CECallback
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// U# exports a built-in event under its Udon name, whatever its accessibility. The callback used
        /// to carry the written name ("Interact"), which SendCustomEvent never finds, and a private Start
        /// was rejected with advice to make it public -- which still would not have fired.
        /// </summary>
        [Test]
        public void Callback_ToUdonEvents_CarriesTheExportedEventNames()
        {
            const string source = @"using System;
using UdonSharp;
public class Fixture : UdonSharpBehaviour
{
    private Action _a, _b, _c;
    public override void Interact() { }
    public override void OnDeserialization() { }
    void Start() { }
    public void Arm()
    {
        _a = () => Interact();
        _b = () => { this.OnDeserialization(); };
        _c = delegate { Start(); };
        _a.Invoke(); _b.Invoke(); _c.Invoke();
    }
}";

            SyntaxTree[] lowered = Lower(source);
            string text = lowered[0].ToString();

            Assert.That(text, Does.Contain("\"_interact\""));
            Assert.That(text, Does.Contain("\"_onDeserialization\""));
            Assert.That(text, Does.Contain("\"_start\""));
            Assert.That(text, Does.Not.Contain("=>"));
            Compile(lowered);
        }

        /// <summary>
        /// A method UdonSharpBehaviour declares without the behaviour overriding it is an extern with no
        /// entry point, and a delegate value is not a method of the program at all. Both used to become a
        /// callback that compiled and never fired.
        /// </summary>
        [TestCase("RequestSerialization()", "outside U# source")]
        [TestCase("Interact()", "outside U# source")]
        [TestCase("_compute()", "delegate value")]
        public void Callback_ToATargetWithNoEntryPoint_IsRejected(string call, string reason)
        {
            string source = @"using System;
using UdonSharp;
public class Fixture : UdonSharpBehaviour
{
    private Func<int> _compute;
    public void Arm() { Action action = () => CALL; action.Invoke(); }
}".Replace("CALL", call);

            var failure = Assert.Throws<UdonSharp.Compiler.CodeGenerationException>(() => Lower(source));

            Assert.That(failure.GeneratorId, Is.EqualTo("CE0062"));
            Assert.That(failure.Message, Does.Contain(reason));
        }

        /// <summary>
        /// CE0062 says the callback "would never fire". In a file CE keeps as written for another reason
        /// there is no callback at all, and whether the error appeared used to depend on whether the lambda
        /// sat above or below that other usage.
        /// </summary>
        [Test]
        public void Callback_UnreachableTargetInAFileKeptAsWritten_IsNotReported()
        {
            const string source = @"using System;
using UdonSharp;
public class Fixture : UdonSharpBehaviour
{
    private void Hidden() { }
    public void Arm() { Action action = () => Hidden(); action?.Invoke(); }
}";

            SyntaxTree[] lowered = null;
            Assert.DoesNotThrow(() => lowered = Lower(source));
            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
        }

        /// <summary>
        /// CECallback captures 'this'. Outside a behaviour, or in a static member, there is none, and that
        /// used to be a CE0061 compile error -- over plain MonoBehaviours and static utilities U# never
        /// binds, and only when the lambda sat above the file's first unsupported usage. Both member orders
        /// are here: the outcome must not depend on which comes first.
        /// </summary>
        [TestCase(@"using System;
using UnityEngine;
public class Menu : MonoBehaviour
{
    public void Open() { Later(() => Refresh()); }
    private void Refresh() { }
    private void Later(Action action) { action(); }
}", TestName = "Callback_WithNoBehaviourInstanceToCapture_LeavesTheFileAsWritten(MonoBehaviour, lambda first)")]
        [TestCase(@"using System;
using UnityEngine;
public class Menu : MonoBehaviour
{
    private void Later(Action action) { action(); }
    private void Refresh() { }
    public void Open() { Later(() => Refresh()); }
}", TestName = "Callback_WithNoBehaviourInstanceToCapture_LeavesTheFileAsWritten(MonoBehaviour, lambda last)")]
        [TestCase(@"using System;
using UdonSharp;
public class Fixture : UdonSharpBehaviour
{
    public static void Ping() { }
    public static void Arm() { Action action = () => Ping(); action.Invoke(); }
}", TestName = "Callback_WithNoBehaviourInstanceToCapture_LeavesTheFileAsWritten(static member of a behaviour)")]
        [TestCase(@"using System;
public static class Scheduler
{
    public static void Tick() { }
    public static void Schedule() { Action action = () => Tick(); action.Invoke(); }
}", TestName = "Callback_WithNoBehaviourInstanceToCapture_LeavesTheFileAsWritten(static utility class)")]
        public void Callback_WithNoBehaviourInstanceToCapture_LeavesTheFileAsWritten(string source)
        {
            SyntaxTree[] lowered = null;
            Assert.DoesNotThrow(() => lowered = Lower(source));
            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
            Compile(lowered);
        }

        /// <summary>
        /// The declaring file and a caller are converted together or not at all. A caller passing a method
        /// group cannot be converted, so the declaring file has to keep System.Action too; it used to
        /// become CECallback on its own, and the caller was a CS1503.
        /// </summary>
        [Test]
        public void Action_CallerThatCannotBeConverted_KeepsTheDeclaringFileAsWritten()
        {
            const string sink = @"using System;
using UdonSharp;
public class Sink : UdonSharpBehaviour
{
    private Action _callback;
    public void Register(Action callback) { _callback = callback; }
    public void Fire() { _callback.Invoke(); }
}";
            const string caller = @"using UdonSharp;
public class Caller : UdonSharpBehaviour
{
    public Sink sink;
    public void Done() { }
    public void Go() { sink.Register(Done); }
}";

            SyntaxTree[] lowered = Lower(sink, caller);

            Assert.That(lowered[0].ToString(), Is.EqualTo(sink));
            Assert.That(lowered[1].ToString(), Is.EqualTo(caller));
            Compile(lowered);
        }

        /// <summary>
        /// An override keeps the signature of what it overrides, and neither declaration names the other
        /// in an expression. Converting only one side was CS0508 (override converted, virtual not) or
        /// CS0115 (virtual converted, override not).
        /// </summary>
        [TestCase(@"using System;
using UdonSharp;
public class BaseBehaviour : UdonSharpBehaviour
{
    public virtual Action GetHandler() { return null; }
}", @"using System;
public class DerivedBehaviour : BaseBehaviour
{
    public void Ping() { }
    public override Action GetHandler() { return () => Ping(); }
}", TestName = "Action_OverrideAndTheMemberItOverrides_ConvertTogether(virtual kept, override convertible)")]
        [TestCase(@"using System;
using UdonSharp;
public class BaseBehaviour : UdonSharpBehaviour
{
    public virtual void Register(Action callback) { callback.Invoke(); }
}", @"using System;
public class DerivedBehaviour : BaseBehaviour
{
    public override void Register(Action callback) { callback?.Invoke(); }
}", TestName = "Action_OverrideAndTheMemberItOverrides_ConvertTogether(virtual convertible, override kept)")]
        public void Action_OverrideAndTheMemberItOverrides_ConvertTogether(string baseSource, string derivedSource)
        {
            SyntaxTree[] lowered = Lower(baseSource, derivedSource);

            Assert.That(lowered[0].ToString(), Is.EqualTo(baseSource));
            Assert.That(lowered[1].ToString(), Is.EqualTo(derivedSource));
            Compile(lowered);
        }

        /// <summary>
        /// A CECallback is a struct: it cannot be null, boxing it makes every null test false, it is not a
        /// Delegate, and APIs outside U# source still take System.Action. Each of these used to be
        /// rewritten anyway -- silently in the first two cases, a compile error in the rest.
        /// </summary>
        [TestCase("if (ReferenceEquals(_handler, null)) _handler = () => Done();")]
        [TestCase("_handler = () => Done(); object boxed = _handler;")]
        [TestCase("_handler = () => Done(); System.Delegate asDelegate = _handler;")]
        [TestCase("_handler = () => Done(); System.Threading.Tasks.Task.Run(_handler);")]
        [TestCase("_handler = () => Done(); int length = _handler.Method.Name.Length;")]
        public void Action_ValueThatLeavesTheActionWorld_KeepsTheFileAsWritten(string body)
        {
            string source = @"using System;
using UdonSharp;
public class Fixture : UdonSharpBehaviour
{
    private Action _handler;
    public void Done() { }
    public void Go() { BODY }
}".Replace("BODY", body);

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
            Compile(lowered);
        }

        /// <summary>An event has to be of a delegate type; 'event CECallback' is CS0066.</summary>
        [Test]
        public void Action_EventField_KeepsTheFileAsWritten()
        {
            const string source = @"using System;
using UdonSharp;
public class Fixture : UdonSharpBehaviour
{
    public event Action Done;
    public void Fire() { Done.Invoke(); }
}";

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
            Compile(lowered);
        }

        /// <summary>
        /// The documented 'new Action(Method)' conversion never happened: the method group inside it was
        /// rejected on its own first, and the replacement it would have emitted built 'nameof' from a
        /// plain identifier, which binds as a call to a method named nameof.
        /// </summary>
        [Test]
        public void Action_ConstructionFromAMethodGroup_BecomesACallbackWithALiteralName()
        {
            const string source = @"using System;
using UdonSharp;
public class Fixture : UdonSharpBehaviour
{
    private Action _a;
    private System.Action _b;
    public void Done() { }
    public void B() { }
    public void B(int value) { }
    public void Arm() { _a = new Action(Done); _b = new System.Action(B); _a.Invoke(); _b.Invoke(); }
}";

            SyntaxTree[] lowered = Lower(source);
            string text = lowered[0].ToString();

            Assert.That(text, Does.Contain("\"Done\""));
            Assert.That(text, Does.Contain("\"B\""));
            Assert.That(text, Does.Not.Contain("nameof"));
            Compile(lowered);
        }

        // ---------------------------------------------------------------------------------------------
        // CELogger
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// CELogger.Log(tag, message, level) was always a plain Debug.Log, so an error logged that way lost
        /// its severity.
        /// </summary>
        [Test]
        public void Logger_TaggedLogWithAConstantLevel_RoutesBySeverityAndKeepsTheTag()
        {
            const string source = @"using UdonSharp.CE.DevTools;
public static class TransformEntry
{
    public static void Run() { CELogger.Log(""Net"", ""connection lost"", LogLevel.Error); }
}";

            SyntaxTree[] lowered = Lower(source);
            string text = lowered[0].ToString();

            Assert.That(text, Does.Contain("UnityEngine.Debug.LogError("));
            Assert.That(text, Does.Contain("\"Net\""));
            Assert.That(text, Does.Contain("[Error] "));
            Compile(lowered);
        }

        /// <summary>
        /// A level that is not a constant cannot pick a Debug method, and named arguments were read by
        /// position (Info(message: m, tag: t) logged the message as the tag). Both stay a real CELogger
        /// call.
        /// </summary>
        [TestCase("CELogger.Info(message: \"the message\", tag: \"TheTag\");")]
        [TestCase("CELogger.Log(\"dynamic\", level);")]
        public void Logger_CallItCannotRouteExactly_IsLeftAsWritten(string call)
        {
            string source = @"using UdonSharp.CE.DevTools;
public static class TransformEntry
{
    public static void Run(LogLevel level) { CALL }
}".Replace("CALL", call);

            SyntaxTree[] lowered = Lower(source);

            Assert.That(lowered[0].ToString(), Is.EqualTo(source));
        }

        /// <summary>
        /// The concatenation is built as a tree, so it bound correctly, but its text read
        /// <c>"[CE][Info] " + a ?? b</c> -- which parses as <c>("[CE][Info] " + a) ?? b</c>. That text is what
        /// the optimization report shows and what U# publishes as the module source.
        /// </summary>
        [Test]
        public void Logger_RewrittenText_ParsesBackToTheSameTree()
        {
            const string source = @"using UdonSharp.CE.DevTools;
public static class TransformEntry
{
    public static void Run(string a, string b, bool flag)
    {
        CELogger.Info(a ?? b);
        CELogger.Debug(""Flock"", flag ? ""on"" : ""off"");
        CELogger.Warning(flag ? ""tag"" : null, a + b);
    }
}";

            SyntaxTree[] lowered = Lower(source);
            SyntaxTree reparsed = CSharpSyntaxTree.ParseText(lowered[0].GetText(), ParseOptions);

            Assert.That(lowered[0].ToString(), Does.Not.Contain("CELogger."));
            Assert.That(Shape(reparsed.GetRoot()), Is.EqualTo(Shape(lowered[0].GetRoot())),
                "The rewritten text no longer says what the rewritten tree does:\n" + lowered[0]);
            Compile(lowered);
        }

        /// <summary>
        /// Every node kind and token, in order. SyntaxFactory.AreEquivalent is not used: the parser may
        /// store a one-statement block as a one-element list where a rewritten block stores the statement
        /// itself, and that difference in representation means nothing.
        /// </summary>
        private static string Shape(SyntaxNode root)
        {
            return string.Join(" ", root.DescendantNodesAndTokensAndSelf()
                .Select(item => item.IsToken ? item.AsToken().Text : item.Kind().ToString()));
        }

        // ---------------------------------------------------------------------------------------------
        // Source positions
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Diagnostics and the debug-info line map are read off the rewritten tree. The inliner and the
        /// logger dropped the line breaks carried by the tokens they removed, and the Action pass appended
        /// a using directive, so every later line was reported somewhere else.
        /// </summary>
        [Test]
        public void Lowering_LeavesEveryLaterLineWhereItWas()
        {
            const string source = @"using System;
using System.Collections.Generic;
using UdonSharp;
using UdonSharp.CE.Data;
using UdonSharp.CE.DevTools;
public class Fixture : UdonSharpBehaviour
{
    private Action _onDone;
    public void Done() { }
    public void Go(CEList<int> list, List<int> other)
    {
        int sum = 0;
        list.ForEach(x =>
        {
            int LINE_15 = x;
            sum += LINE_15;
        });
        int LINE_18 = 0;
        list
            .ForEachWithIndex((x, i) =>
                sum += x * i);
        int LINE_22 = 0;
        list.ForEachUntil(x =>
            x < 3 &&
            x > 0);
        int LINE_26 = 0;
        list.ForEach(a =>
            other.ForEach(b =>
            {
                sum += a * b; int LINE_30 = 0;
            }));
        int LINE_32 = 0;
        CELogger.Log(
            ""Tag"",
            ""message"",
            LogLevel.Warning);
        int LINE_37 = 0;
        _onDone = () =>
        {
            Done();
        };
        int LINE_42 = 0;
        _onDone.Invoke();
    }
}";

            SyntaxTree[] lowered = Lower(source);
            SyntaxToken[] markers = lowered[0].GetRoot().DescendantTokens()
                .Where(token => token.IsKind(SyntaxKind.IdentifierToken) &&
                                token.ValueText.StartsWith("LINE_", StringComparison.Ordinal))
                .GroupBy(token => token.ValueText)
                .Select(group => group.First())
                .ToArray();

            Assert.That(lowered[0].ToString(), Does.Not.Contain(".ForEach"));
            Assert.That(lowered[0].ToString(), Does.Not.Contain("CELogger."));
            Assert.That(lowered[0].ToString(), Does.Not.Contain("() =>"));
            Assert.That(markers.Length, Is.EqualTo(8));

            foreach (SyntaxToken marker in markers)
            {
                int expected = int.Parse(marker.ValueText.Substring("LINE_".Length), CultureInfo.InvariantCulture);
                int actual = marker.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Assert.That(actual, Is.EqualTo(expected),
                    marker.ValueText + " moved to line " + actual + " in:\n" + lowered[0]);
            }

            Compile(lowered);
        }

        // ---------------------------------------------------------------------------------------------
        // Fixture
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Runs the three passes over <paramref name="fixtures"/> in CEGeneratorRunner's order, each against
        /// a semantic model of every tree, and returns the fixtures followed by the CE runtime trees.
        /// </summary>
        private static SyntaxTree[] Lower(params string[] fixtures)
        {
            SyntaxTree[] trees = fixtures
                .Select((source, i) => CSharpSyntaxTree.ParseText(source, ParseOptions, "TransformFixture" + i + ".cs"))
                .Concat(CERuntimeTrees.Value)
                .ToArray();

            var context = new OptimizationContext();
            var passes = new ICompileTimeOptimizer[]
            {
                new LambdaInliningOptimizer(),
                new ActionToCallbackTransformer(),
                new CELoggerTransformOptimizer(),
            };

            foreach (ICompileTimeOptimizer pass in passes)
            {
                context.SetSemanticModelTrees(trees);

                for (int i = 0; i < fixtures.Length; i++)
                    trees[i] = pass.Optimize(trees[i], context);
            }

            return trees;
        }

        private static CSharpCompilation Compile(SyntaxTree[] trees)
        {
            var references = new List<MetadataReference>(UdonSharp.Compiler.CompilationContext.GetMetadataReferences());

            foreach (string name in new[] { "UdonSharp.Runtime", "UdonSharp.Lib" })
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
                if (assembly != null && !references.OfType<PortableExecutableReference>().Any(r => r.FilePath == assembly.Location))
                    references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }

            // The CE runtime is compiled from source next to the fixtures, as U# compiles it, so its own
            // assembly must not be referenced as well.
            references.RemoveAll(r => r is PortableExecutableReference pe &&
                                      Path.GetFileNameWithoutExtension(pe.FilePath) == "UdonSharp.CE.Runtime");

            CSharpCompilation compilation = CSharpCompilation.Create(
                "CETransformLowering_" + Guid.NewGuid().ToString("N"),
                trees,
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            Diagnostic[] errors = compilation.GetDiagnostics()
                .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                .ToArray();

            Assert.That(errors, Is.Empty, string.Join("\n", errors.Select(d => d.ToString())));
            return compilation;
        }

        private static string Execute(SyntaxTree[] trees)
        {
            CSharpCompilation compilation = Compile(trees);

            using (var stream = new MemoryStream())
            {
                EmitResult emitted = compilation.Emit(stream);
                Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));

                Assembly assembly = Assembly.Load(stream.ToArray());
                MethodInfo run = assembly.GetType("TransformEntry", true).GetMethod("Run");

                try
                {
                    return (string)run.Invoke(null, null);
                }
                catch (TargetInvocationException failure)
                {
                    throw failure.InnerException ?? failure;
                }
            }
        }
    }
}
