using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;
using UdonSharp.CE.Editor.Async;
using UdonSharp.CE.Editor.Optimizers;
using UdonSharp.Compiler;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Pins the async lowering against the shapes it used to get wrong. Where control flow is the point
    /// -- a loop exit, which argument reaches which parameter, how a finished child hands back to its
    /// parent -- the lowered program is compiled and run on a stand-in scheduler, because the old output
    /// for several of these compiled cleanly and was only wrong when it ran.
    /// </summary>
    public class CEAsyncRegressionTests
    {
        private const string Usings = "using UdonSharp; using UdonSharp.CE.Async; using UnityEngine;\n";

        // ---- break and continue in an awaiting while loop ----

        /// <summary>
        /// The loop body is emitted into cases of the MoveNext switch. A 'break' there used to leave the
        /// switch instead of the loop, and a 'continue' had no loop to continue at all (CS0139).
        /// </summary>
        [Test]
        public void BreakAndContinue_InAwaitingWhile_LeaveOrRestartTheLoop()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringLoopFixture : UdonSharpBehaviour
{
    public int ticks;

    public async UdonTask Run()
    {
        while (true)
        {
            ticks++;
            if (ticks == 2) continue;
            await UdonTask.Yield();
            for (int i = 0; i < 3; i++) { if (i == 1) continue; if (i == 2) break; Debug.Log(""inner "" + i); }
            switch (ticks) { case 3: Debug.Log(""switch""); break; }
            if (ticks == 4) continue;
            Debug.Log(""tick "" + ticks);
            if (ticks >= 5) break;
        }

        Debug.Log(""after loop "" + ticks);
    }
}", "LoweringLoopFixture", "Run");

            CollectionAssert.AreEqual(
                new[] { "inner 0", "tick 1", "inner 0", "switch", "tick 3", "inner 0", "inner 0", "tick 5", "after loop 5" },
                run.Log,
                "A continue restarts the loop, a break leaves it, and a jump owned by an inner for or switch stays theirs.");
            Assert.That(!run.Flag("__Run_running"), "The machine has to finish, or every later call is refused.");
        }

        [Test]
        public void Break_BeforeTheAwait_FinishesTheMachine()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringEarlyBreakFixture : UdonSharpBehaviour
{
    public int ticks;

    public async UdonTask Run()
    {
        while (true)
        {
            ticks++;
            if (ticks > 3) break;
            await UdonTask.Yield();
        }

        Debug.Log(""after loop "" + ticks);
    }
}", "LoweringEarlyBreakFixture", "Run");

            CollectionAssert.AreEqual(new[] { "after loop 4" }, run.Log,
                "The break used to leave MoveNext with nothing scheduled, so the code after the loop never ran.");
            Assert.That(!run.Flag("__Run_running"), "A machine stopped by a break stayed running for the session.");
        }

        // ---- awaited child arguments ----

        [Test]
        public void ChildAwait_ReorderedNamedArguments_ReachTheirParameters()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringNamedArgumentFixture : UdonSharpBehaviour
{
    public async UdonTask Run()
    {
        await Fade(to: 1, from: 0);
        Debug.Log(""done"");
    }

    private async UdonTask Fade(int from, int to)
    {
        Debug.Log(""from "" + from + "" to "" + to);
        await UdonTask.Yield();
    }
}", "LoweringNamedArgumentFixture", "Run");

            CollectionAssert.AreEqual(new[] { "from 0 to 1", "done" }, run.Log,
                "Named arguments used to be passed in written order, which swapped from and to.");
        }

        [Test]
        public void ChildAwait_OmittedOptionalArgument_IsRejectedAtTheCall()
        {
            CodeGenerationException rejection = AsyncLoweringFixture.Rejection(Usings + @"
public class LoweringOptionalArgumentFixture : UdonSharpBehaviour
{
    public async UdonTask Run() { await Show(""hi""); }
    private async UdonTask Show(string text, string style = ""plain"") { Debug.Log(text + style); await UdonTask.Yield(); }
}");

            AsyncLoweringFixture.AssertRejectedInFixture(rejection, "CE0025", "style");
        }

        [Test]
        public void ChildAwait_OptionalArgumentWrittenOut_IsLowered()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringOptionalArgumentFixture : UdonSharpBehaviour
{
    public async UdonTask Run() { await Show(""hi"", ""plain""); }
    private async UdonTask Show(string text, string style = ""plain"") { Debug.Log(text + style); await UdonTask.Yield(); }
}", "LoweringOptionalArgumentFixture", "Run");

            CollectionAssert.AreEqual(new[] { "hiplain" }, run.Log);
        }

        [Test]
        public void ChildAwait_ParamsElementsListedOneByOne_AreRejected()
        {
            CodeGenerationException rejection = AsyncLoweringFixture.Rejection(Usings + @"
public class LoweringParamsFixture : UdonSharpBehaviour
{
    public async UdonTask Run() { await Count(""x"", ""y""); }
    private async UdonTask Count(params string[] parts) { Debug.Log(parts.Length.ToString()); await UdonTask.Yield(); }
}");

            AsyncLoweringFixture.AssertRejectedInFixture(rejection, "CE0025", "params");
        }

        [Test]
        public void ChildAwait_ParamsPassedAsAnArray_IsLowered()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringParamsFixture : UdonSharpBehaviour
{
    public async UdonTask Run() { await Count(new[] { ""x"", ""y"" }); }
    private async UdonTask Count(params string[] parts) { Debug.Log(parts.Length.ToString()); await UdonTask.Yield(); }
}", "LoweringParamsFixture", "Run");

            CollectionAssert.AreEqual(new[] { "2" }, run.Log);
        }

        // ---- preprocessor directives inside lowered statements ----

        /// <summary>
        /// Generated members used to be parsed with default options, so every symbol of the compile was
        /// undefined in them: the #else branch ran, and code fenced off from Udon was compiled into it.
        /// </summary>
        [Test]
        public void PreprocessorBranches_InsideLoweredStatements_KeepTheCompilesSymbols()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringDirectiveFixture : UdonSharpBehaviour
{
    public bool ready = true;

    public async UdonTask Run()
    {
        await UdonTask.Yield();
        if (ready)
        {
#if UNITY_EDITOR
            Debug.Log(""editor"");
#else
            Debug.Log(""player"");
#endif
#if !COMPILER_UDONSHARP
            Debug.Log(""not for Udon"");
#endif
        }
    }
}", "LoweringDirectiveFixture", "Run", "UNITY_EDITOR", "COMPILER_UDONSHARP");

            CollectionAssert.AreEqual(new[] { "editor" }, run.Log);
        }

        // ---- which awaits belong to CE ----

        /// <summary>
        /// The pass sees every file of Assembly-CSharp. An await in ordinary C# used to fail the whole U#
        /// compile with an error about UdonTask.
        /// </summary>
        [Test]
        public void AwaitsCEDoesNotOwn_AreLeftAsWritten()
        {
            const string source = @"
using System;
using System.Threading.Tasks;

public class LoweringPlainDownloader
{
    private readonly Func<Task> _retry = async () => await Task.Yield();

    public async Task<int> FetchAsync()
    {
        await Task.Delay(1);
        Func<Task> local = async () => { await Task.Yield(); };
        await local();
        return 1;
    }
}";

            SyntaxTree lowered = AsyncLoweringFixture.Lower(source);

            Assert.AreEqual(source, lowered.ToString());
            AsyncLoweringFixture.Compile(lowered);
        }

        [TestCase(Usings + "public class LoweringOwnedFixture : UdonSharpBehaviour { public async void Start() { await UdonTask.Yield(); } }")]
        [TestCase(Usings + "public class LoweringOwnedFixture : UdonSharpBehaviour { public async System.Threading.Tasks.Task Load() { await System.Threading.Tasks.Task.Yield(); } }")]
        [TestCase(Usings + "public class LoweringOwnedFixture : UdonSharpBehaviour { public async UdonTask Run() => await UdonTask.Yield(); }")]
        [TestCase(Usings + "public class LoweringOwnedPlain { public void Go() { System.Func<UdonTask> run = async () => { await UdonTask.Yield(); }; } }")]
        public void AwaitsCEOwns_OutsideALoweredMethod_AreStillRejected(string source)
        {
            AsyncLoweringFixture.AssertRejectedInFixture(AsyncLoweringFixture.Rejection(source), "CE0024", "await");
        }

        // ---- a child that finishes without suspending ----

        /// <summary>
        /// A child that finished inside its own Start used to raise its parent's continuation with
        /// SendCustomEvent while the parent was still on the stack, so a loop over such children nested one
        /// event per item. Udon halts a behaviour at a depth of 1000.
        /// </summary>
        [Test]
        public void ChildThatFinishesWithoutSuspending_HandsBackWithoutAnEvent()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringSyncChildFixture : UdonSharpBehaviour
{
    public int processed;

    public async UdonTask Run()
    {
        int i = 0;
        while (i < 50)
        {
            await Process(i);
            i++;
        }

        Debug.Log(""done "" + processed);
    }

    private async UdonTask Process(int item)
    {
        if (item >= 0)
        {
            processed++;
            return;
        }

        await UdonTask.Yield();
    }
}", "LoweringSyncChildFixture", "Run");

            CollectionAssert.AreEqual(new[] { "done 50" }, run.Log);
            Assert.AreEqual(0, run.MaxEventDepth,
                "Nothing here waits, so no event should be raised at all; the old lowering nested one per item.");
            Assert.That(!run.Flag("__Run_running") && !run.Flag("__Process_running"));
        }

        [Test]
        public void ChildThatSuspends_ResumesItsParentOnceItFinishes()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringSuspendingChildFixture : UdonSharpBehaviour
{
    public async UdonTask Run()
    {
        Debug.Log(""parent before"");
        await Child();
        Debug.Log(""parent after"");
    }

    private async UdonTask Child()
    {
        Debug.Log(""child before"");
        await UdonTask.DelayFrames(2);
        Debug.Log(""child after"");
    }
}", "LoweringSuspendingChildFixture", "Run");

            CollectionAssert.AreEqual(new[] { "parent before", "child before", "child after", "parent after" }, run.Log);
            Assert.That(run.EventsRun >= 1, "The child must actually have waited.");
            Assert.That(!run.Flag("__Run_running") && !run.Flag("__Child_running"));
        }

        [Test]
        public void RefusedReentrantStart_StillResumesEveryAwaitingCaller()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringReentryFixture : UdonSharpBehaviour
{
    public async UdonTask Run()
    {
        await Parent();
        Debug.Log(""run resumed"");
    }

    public async UdonTask Parent()
    {
        await Child();
        await UdonTask.Yield();
        Debug.Log(""parent done"");
    }

    public async UdonTask Child()
    {
        Parent();
        await UdonTask.Yield();
        Debug.Log(""child done"");
    }
}", "LoweringReentryFixture", "Run");

            Assert.AreEqual(1, run.Log.Count(line => line.StartsWith("ERROR ", StringComparison.Ordinal)),
                "The second start of Parent is refused and says so.");
            CollectionAssert.AreEqual(new[] { "child done", "parent done", "run resumed" },
                run.Log.Where(line => !line.StartsWith("ERROR ", StringComparison.Ordinal)).ToArray());
        }

        /// <summary>
        /// U# keeps a method's parameters in one slot per method, not per call, so a refused re-entrant call
        /// made while the first run of the body is still on the stack overwrites the continuation
        /// parameter. Start must have stored it before running the body and must not read it afterwards.
        /// C# gives every call its own parameters, so this can only be checked on the generated code.
        /// </summary>
        [Test]
        public void StartMethod_DoesNotReadItsContinuationParameterAfterRunningTheBody()
        {
            SyntaxTree lowered = AsyncLoweringFixture.Lower(Usings + @"
public class LoweringStartFixture : UdonSharpBehaviour
{
    public async UdonTask Run(int count) { await UdonTask.Yield(); Debug.Log(count.ToString()); }
}");

            MethodDeclarationSyntax start = lowered.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == "__Run_Start");
            string continuationParameter = start.ParameterList.Parameters.Last().Identifier.ValueText;

            List<StatementSyntax> statements = start.Body.Statements.ToList();
            int body = statements.FindIndex(statement => statement.ToString().Contains("__Run_MoveNext()"));
            Assert.That(body > 0, "Start no longer runs the body directly:\n" + start);

            bool readBefore = statements.Take(body).Any(statement => Mentions(statement, continuationParameter));
            bool readAfter = statements.Skip(body + 1).Any(statement => Mentions(statement, continuationParameter));

            Assert.That(readBefore, "Start has to store the continuation before running the body:\n" + start);
            Assert.That(!readAfter, "Start reads the continuation parameter after the body ran:\n" + start);
        }

        // ---- the entry point keeps the author's declaration ----

        [Test]
        public void EntryPoint_KeepsAttributesDocCommentAndModifiers()
        {
            SyntaxTree lowered = AsyncLoweringFixture.Lower(Usings + @"
public class KeptAttribute : System.Attribute { }

public class LoweringEntryFixture : UdonSharpBehaviour
{
    /// <summary>Plays the sequence.</summary>
    [Kept]
    public async UdonTask Run() { await UdonTask.Yield(); }

    /// <summary>Async written first.</summary>
    async protected UdonTask Other() { await UdonTask.Yield(); }
}");

            MethodDeclarationSyntax[] methods = lowered.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>().ToArray();
            MethodDeclarationSyntax run = methods.Single(method => method.Identifier.ValueText == "Run");
            MethodDeclarationSyntax other = methods.Single(method => method.Identifier.ValueText == "Other");

            Assert.AreEqual("Kept", run.AttributeLists.Single().Attributes.Single().ToString(),
                "The entry point used to be rebuilt from text, which dropped every attribute -- an [Rpc] among them.");
            StringAssert.Contains("Plays the sequence.", run.ToFullString());
            Assert.AreEqual("public", run.Modifiers.ToString());
            StringAssert.Contains("Async written first.", other.ToFullString(),
                "When 'async' is the first token it carries the declaration's leading trivia, which must survive it.");
            Assert.AreEqual("protected", other.Modifiers.ToString());
            Assert.That(!lowered.GetRoot().DescendantTokens().Any(token => token.IsKind(SyntaxKind.AsyncKeyword)));
            AsyncLoweringFixture.Compile(lowered);
        }

        // ---- locals kept across an await ----

        /// <summary>
        /// An array initializer used to become '__x = { ... };', which does not parse. An out variable was
        /// never hoisted and read as unassigned after the await (CS0165). A const local became a plain field,
        /// which a case label cannot name (CS0150). A 'using' declaration is a plain local to UdonSharp,
        /// which never disposes it, so it hoists like one rather than being refused.
        /// </summary>
        [Test]
        public void Locals_DeclaredEveryWayAtBodyLevel_SurviveAnAwait()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringHoistScope : System.IDisposable
{
    public int size = 7;
    public void Dispose() { }
}

public class LoweringHoistFixture : UdonSharpBehaviour
{
    public void Go() { Run(""2""); }

    public async UdonTask Run(string text)
    {
        int[] steps = { 5, 15 };
        const int Two = 2;
        using var scope = new LoweringHoistScope();
        if (!int.TryParse(text, out int parsed)) return;
        await UdonTask.Yield();
        switch (parsed) { case Two: Debug.Log(""two""); break; }
        Debug.Log(steps[1] + "" "" + parsed + "" "" + scope.size);
    }
}", "LoweringHoistFixture", "Go");

            CollectionAssert.AreEqual(new[] { "two", "15 2 7" }, run.Log);
        }

        [Test]
        public void Nameof_OfAHoistedVariable_KeepsTheWrittenName()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringNameofFixture : UdonSharpBehaviour
{
    public void Go() { Run(2); }

    public async UdonTask Run(int duration)
    {
        await UdonTask.Yield();
        Debug.Log(nameof(duration) + ""="" + duration);
    }
}", "LoweringNameofFixture", "Go");

            CollectionAssert.AreEqual(new[] { "duration=2" }, run.Log,
                "nameof used to name the generated field: '__Run_duration'.");
        }

        /// <summary>
        /// Code after a return at body level is unreachable, but C# and UdonSharp still compile it, so it is
        /// kept instead of dropped -- and an await in it is lowered, not reported as one CE could not lower.
        /// </summary>
        [Test]
        public void AwaitAfterABodyLevelReturn_IsKeptRatherThanMisreported()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(Usings + @"
public class LoweringUnreachableFixture : UdonSharpBehaviour
{
    public async UdonTask Run()
    {
        await UdonTask.Yield();
        Debug.Log(""ran"");
        return;
        await UdonTask.Yield();
        Debug.Log(""unreachable"");
    }
}", "LoweringUnreachableFixture", "Run");

            CollectionAssert.AreEqual(new[] { "ran" }, run.Log);
            Assert.That(!run.Flag("__Run_running"));
        }

        // ---- await targets are recognised by symbol ----

        [Test]
        public void AwaitTarget_ThatOnlyLooksLikeUdonTask_IsRejected()
        {
            CodeGenerationException rejection = AsyncLoweringFixture.Rejection(Usings + @"
public static class MyUdonTask { public static UdonTask Delay(string label) { return UdonTask.CompletedTask; } }

public class LoweringLookalikeFixture : UdonSharpBehaviour
{
    public async UdonTask Run() { await MyUdonTask.Delay(""x""); }
}");

            AsyncLoweringFixture.AssertRejectedInFixture(rejection, "CE0025", "MyUdonTask.Delay");
        }

        [Test]
        public void AwaitTarget_ThroughAnAliasOrThis_IsLowered()
        {
            LoweredRun run = AsyncLoweringFixture.Execute(@"
using UdonSharp; using UnityEngine;
using Wait = UdonSharp.CE.Async.UdonTask;

public class LoweringAliasFixture : UdonSharpBehaviour
{
    public async Wait Run()
    {
        await Wait.DelayFrames(2);
        await this.Child(3);
        Debug.Log(""resumed"");
    }

    private void Child() { Debug.Log(""wrong overload""); }

    private async Wait Child(int value)
    {
        await Wait.Yield();
        Debug.Log(""child "" + value);
    }
}", "LoweringAliasFixture", "Run");

            CollectionAssert.AreEqual(new[] { "child 3", "resumed" }, run.Log);
            Assert.That(run.EventsRun >= 2, "Both waits have to go through the scheduler.");
        }

        // ---- declarations CE refuses, each at the author's own line ----

        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public async UdonTask Fade() { await UdonTask.Yield(); } public async UdonTask Fade(float seconds) { await UdonTask.Delay(seconds); } }", "more than once")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public static async UdonTask Run() { await UdonTask.Yield(); } }", "static")]
        [TestCase(Usings + "public class LoweringShapeHelper { public async UdonTask Run() { await UdonTask.Yield(); } }", "not an UdonSharpBehaviour")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public struct Inner { public async UdonTask Run() { await UdonTask.Yield(); } } }", "outside a class")]
        [TestCase(Usings + "public class LoweringShapeBase : UdonSharpBehaviour { public virtual async UdonTask Run() { await UdonTask.Yield(); } } public class LoweringShapeFixture : LoweringShapeBase { public override async UdonTask Run() { await UdonTask.Delay(1f); } }", "base class")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public async UdonTask Run(ref int value) { await UdonTask.Yield(); value = 1; } }", "'ref'")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { private int __Run_state; public async UdonTask Run() { await UdonTask.Yield(); } }", "__Run_state")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public object boxed; public async UdonTask Run() { if (!(boxed is int count)) return; await UdonTask.Yield(); Debug.Log(count.ToString()); } }", "node type 'IsPatternExpression'")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public int i; public async UdonTask Run() { while (i < 3) { await UdonTask.Yield(); i++; await (UdonTask.Yield()); } } }", "while loop body can hold only one")]
        [TestCase(Usings + "public class LoweringShapeBase : UdonSharpBehaviour { protected async UdonTask Child() { await UdonTask.Yield(); } } public class LoweringShapeFixture : LoweringShapeBase { public async UdonTask Run() { await Child(); } }", "declared on 'LoweringShapeBase'")]
        public void UnsupportedShape_IsRejectedAtTheAuthorsSource(string source, string cause)
        {
            AsyncLoweringFixture.AssertRejectedInFixture(AsyncLoweringFixture.Rejection(source), "CE0025", cause);
        }

        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public async UdonTask Fade(float seconds) { await UdonTask.Delay(seconds); } public void Fade() { } }")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public class Inner : UdonSharpBehaviour { public async UdonTask Run() { await UdonTask.Yield(); } } }")]
        [TestCase(Usings + "public class LoweringShapeBase : UdonSharpBehaviour { public virtual UdonTask Run() { return UdonTask.CompletedTask; } } public class LoweringShapeFixture : LoweringShapeBase { public override async UdonTask Run() { await UdonTask.Yield(); } }")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public async UdonTask Run(int state, string continuation) { int Start = state; int MoveNext = 1; int running = 2; await UdonTask.Yield(); Debug.Log(continuation + Start + MoveNext + running); } }")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public object boxed; public async UdonTask Run() { await UdonTask.Yield(); } public void Check() { if (boxed is int count) Debug.Log(count.ToString()); } }")]
        [TestCase(Usings + "public class LoweringShapeDisposable : System.IDisposable { public void Dispose() { } } public class LoweringShapeFixture : UdonSharpBehaviour { public async UdonTask Run() { using var scope = new LoweringShapeDisposable(); await UdonTask.Yield(); } }")]
        [TestCase(Usings + "public class LoweringShapeFixture : UdonSharpBehaviour { public int i; public async UdonTask Run() { while (i < 3) { await Step(); i++; } } private async UdonTask Step() { await UdonTask.Yield(); await UdonTask.Yield(); } }")]
        public void SupportedNeighbourOfARejectedShape_IsLowered(string source)
        {
            AsyncLoweringFixture.Compile(AsyncLoweringFixture.Lower(source));
        }

        // ---- syntax UdonSharp refuses in any method ----

        private const string SyntaxFixture = Usings + @"using System.Linq;
public class LoweringSyntaxScope : System.IDisposable { public void Dispose() { } }
public class LoweringSyntaxInner { public int Count; }
public class LoweringSyntaxHolder { public LoweringSyntaxInner Inner = new LoweringSyntaxInner(); }

public class LoweringSyntaxFixture : UdonSharpBehaviour
{
    public object boxed;
    public int count;
    public int[] values = new int[2];
    public System.Action handler;
    public LoweringSyntaxFixture other;

    public async UdonTask Run()
    {
        await UdonTask.Yield();
        /*ASYNC*/
    }

    public void Other()
    {
        /*PLAIN*/
    }
}";

        /// <summary>
        /// UdonSharp's binder refuses each of these wherever it meets one. Inside an async method the body is
        /// re-emitted in the generated MoveNext, so its error used to point into generated text; CE now
        /// reports it first, at the author's line and in the binder's own words. The same code in an
        /// ordinary method is not CE's to report, and is left exactly as written for UdonSharp.
        /// </summary>
        [TestCase("int Twice(int v) { return v * 2; } count = Twice(count);", "Local method declarations are not currently supported by UdonSharp")]
        [TestCase("try { count++; } finally { count--; }", "Try/Catch/Finally is not supported by UdonSharp")]
        [TestCase("if (count < 0) throw new System.InvalidOperationException();", "UdonSharp does not support throwing exceptions")]
        [TestCase("boxed = boxed ?? throw new System.InvalidOperationException();", "UdonSharp does not support throwing exceptions")]
        [TestCase("if (count > 3) goto done; count++; done: count--;", "node type 'GotoStatement'")]
        [TestCase("again: count++;", "node type 'LabeledStatement'")]
        [TestCase("switch (count) { case 0: goto case 1; case 1: count++; break; }", "node type 'GotoCaseStatement'")]
        [TestCase("lock (boxed) { count++; }", "node type 'LockStatement'")]
        [TestCase("using (new LoweringSyntaxScope()) { count++; }", "node type 'UsingStatement'")]
        [TestCase("checked { count++; }", "node type 'CheckedStatement'")]
        [TestCase("foreach (var (a, b) in new[] { (1, 2) }) count += a;", "node type 'ForEachVariableStatement'")]
        [TestCase("var pair = (count, count); count = pair.Item1;", "node type 'TupleExpression'")]
        [TestCase("var (low, high) = (count, count);", "deconstruction")]
        [TestCase("if (boxed is int n) count = n;", "node type 'IsPatternExpression'")]
        [TestCase("if (boxed is null) count = 0;", "node type 'IsPatternExpression'")]
        [TestCase("if (boxed is string) count = 1;", "The 'is' operator is not yet supported in UdonSharp")]
        [TestCase("if (other is LoweringSyntaxFixture) count = 1;", "The 'is' operator with user-defined types is not supported in UdonSharp")]
        [TestCase("handler?.Invoke();", "node type 'ConditionalAccessExpression'")]
        [TestCase("count = count switch { 0 => 1, _ => 2 };", "node type 'SwitchExpression'")]
        [TestCase("ref int first = ref values[0]; first = 1;", "node type 'RefExpression'")]
        [TestCase("var shape = new { Size = count }; count = shape.Size;", "node type 'AnonymousObjectCreationExpression'")]
        [TestCase("var big = from v in values where v > 1 select v;", "node type 'QueryExpression'")]
        [TestCase("boxed = typeof(LoweringSyntaxFixture);", "Cannot use typeof on user-defined types")]
        [TestCase("var list = new System.Collections.Generic.List<int> { 1, 2 };", "U# does not support collection initializers or nested initializers")]
        [TestCase("var holder = new LoweringSyntaxHolder { Inner = { Count = 1 } };", "node type 'ObjectInitializerExpression'")]
        [TestCase("var grid = new int[2, 2];", "Multidimensional arrays are not yet supported by UdonSharp")]
        [TestCase("var grid = new[,] { { 1, 2 }, { 3, 4 } };", "Multidimensional arrays are not yet supported by UdonSharp")]
        [TestCase("const bool Verbose = true; if (Verbose) { try { count++; } finally { count--; } }", "Try/Catch/Finally is not supported by UdonSharp")]
        [TestCase("await foreach (var v in values) count += v;", "CE cannot lower 'await foreach'")]
        [TestCase("await using var scope = new LoweringSyntaxScope();", "CE cannot lower 'await using'")]
        public void UnsupportedSyntax_IsRefusedInAnAsyncMethodAndLeftAloneElsewhere(string snippet, string cause)
        {
            string inAsync = SyntaxFixture.Replace("/*ASYNC*/", snippet).Replace("/*PLAIN*/", "");
            AsyncLoweringFixture.AssertRejectedInFixture(AsyncLoweringFixture.Rejection(inAsync), "CE0025", cause);

            string inPlainMethod = SyntaxFixture.Replace("/*ASYNC*/", "").Replace("/*PLAIN*/", snippet);
            SyntaxTree lowered = AsyncLoweringFixture.Lower(inPlainMethod);

            Assert.AreEqual(
                MethodText(CSharpSyntaxTree.ParseText(inPlainMethod), "Other"),
                MethodText(lowered, "Other"),
                "An ordinary method is UdonSharp's to report on; CE must not touch it.");
        }

        /// <summary>
        /// The binder binds only what it reaches, and code it never reaches compiles today however
        /// unsupported it is: the branch a constant condition does not take (including the conditions
        /// UdonSharp folds and C# does not, such as a value type compared with null), a lambda body, which
        /// CE's lambda passes and the binder decide, a pattern 'case' label, and the right side of '??' over
        /// a value type. Refusing any of these in an async method would fail a project that compiles.
        /// </summary>
        [TestCase("const bool Verbose = false; if (Verbose) { try { count++; } finally { count--; } }")]
        [TestCase("if (true) { count++; } else { lock (boxed) { count--; } }")]
        [TestCase("if (count == null) { handler?.Invoke(); }")]
        [TestCase("if (typeof(int) == typeof(string)) { try { count++; } finally { count--; } }")]
        [TestCase("System.Func<int> make = () => { try { return 1; } finally { count--; } }; count = make();")]
        [TestCase("switch (boxed) { case int n when other?.count > n: count = n; break; default: break; }")]
        [TestCase("int? maybe = null; count = maybe ?? (boxed is string ? 1 : 2);")]
        [TestCase("boxed = typeof(string);")]
        [TestCase("using var scope = new LoweringSyntaxScope(); count++;")]
        public void SyntaxUdonSharpNeverBinds_InAnAsyncMethod_IsLeftAlone(string snippet)
        {
            AsyncLoweringFixture.Compile(AsyncLoweringFixture.Lower(
                SyntaxFixture.Replace("/*ASYNC*/", snippet).Replace("/*PLAIN*/", "")));
        }

        private static string MethodText(SyntaxTree tree, string methodName)
        {
            return tree.GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Single(method => method.Identifier.ValueText == methodName)
                .ToString();
        }

        private static bool Mentions(SyntaxNode node, string identifier)
        {
            return node.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Any(name => name.Identifier.ValueText == identifier);
        }
    }

    /// <summary>The outcome of running a lowered fixture on the stand-in scheduler.</summary>
    internal sealed class LoweredRun
    {
        public List<string> Log;
        public int MaxEventDepth;
        public int EventsRun;
        public object Instance;

        public bool Flag(string fieldName)
        {
            FieldInfo field = Instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field != null, "The lowered fixture has no field named " + fieldName + ".");
            return (bool)field.GetValue(Instance);
        }
    }

    /// <summary>
    /// Runs the real async transformer over a source string the way the U# compile hands it a file, and
    /// compiles and runs what it produced.
    ///
    /// The prelude stands in for everything the lowered code touches outside itself. Udon's event
    /// dispatch is the part that matters: SendCustomEvent runs the event at once, inside whatever is
    /// running, which is what Udon counts against its RunProgram depth limit; the delayed forms wait a
    /// number of frames. The prelude's types share their names with the real UdonSharpBehaviour,
    /// UnityEngine.Debug and EventTiming in the referenced assemblies, and C# binds a name declared in
    /// source over an imported one, so the fixture runs against the stand-ins.
    /// </summary>
    internal static class AsyncLoweringFixture
    {
        private const string FixturePath = "AsyncLoweringFixture.cs";

        private const string Prelude = @"
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace VRC.Udon.Common.Enums
{
    public enum EventTiming { Update, LateUpdate, PostLateUpdate }
}

namespace UnityEngine
{
    public static class Debug
    {
        public static readonly List<string> Lines = new List<string>();
        public static void Log(object message) { Lines.Add(message == null ? ""null"" : message.ToString()); }
        public static void LogWarning(object message) { Lines.Add(""WARNING "" + message); }
        public static void LogError(object message) { Lines.Add(""ERROR "" + message); }
    }
}

namespace UdonSharp
{
    public class UdonSharpBehaviour
    {
        private sealed class Pending
        {
            public UdonSharpBehaviour Target;
            public string EventName;
            public int Frames;
        }

        private const float SecondsPerFrame = 0.25f;
        private static readonly List<Pending> PendingEvents = new List<Pending>();

        public static int EventDepth;
        public static int MaxEventDepth;
        public static int EventsRun;

        public void SendCustomEvent(string eventName)
        {
            RunEvent(this, eventName);
        }

        public void SendCustomEventDelayedFrames(string eventName, int delayFrames, VRC.Udon.Common.Enums.EventTiming eventTiming = VRC.Udon.Common.Enums.EventTiming.Update)
        {
            PendingEvents.Add(new Pending { Target = this, EventName = eventName, Frames = Math.Max(1, delayFrames) });
        }

        public void SendCustomEventDelayedSeconds(string eventName, float delaySeconds, VRC.Udon.Common.Enums.EventTiming eventTiming = VRC.Udon.Common.Enums.EventTiming.Update)
        {
            PendingEvents.Add(new Pending { Target = this, EventName = eventName, Frames = Math.Max(1, (int)Math.Ceiling(delaySeconds / SecondsPerFrame)) });
        }

        public static void Pump(int frames)
        {
            for (int frame = 0; frame < frames && PendingEvents.Count > 0; frame++)
            {
                var due = new List<Pending>();
                foreach (Pending pending in PendingEvents)
                {
                    pending.Frames--;
                    if (pending.Frames <= 0)
                        due.Add(pending);
                }

                PendingEvents.RemoveAll(pending => pending.Frames <= 0);

                foreach (Pending pending in due)
                    RunEvent(pending.Target, pending.EventName);
            }
        }

        private static void RunEvent(UdonSharpBehaviour target, string eventName)
        {
            MethodInfo method = target.GetType().GetMethod(eventName, BindingFlags.Instance | BindingFlags.Public);
            if (method == null)
            {
                UnityEngine.Debug.LogError(""no public event named "" + eventName);
                return;
            }

            EventDepth++;
            EventsRun++;
            if (EventDepth > MaxEventDepth)
                MaxEventDepth = EventDepth;

            try
            {
                method.Invoke(target, null);
            }
            finally
            {
                EventDepth--;
            }
        }
    }
}

namespace UdonSharp.CE.Async
{
    public enum TaskStatus { Created, WaitingForActivation, WaitingToRun, Running, RanToCompletion, Canceled, Faulted }

    [AsyncMethodBuilder(typeof(AsyncUdonTaskMethodBuilder))]
    public struct UdonTask
    {
        internal TaskStatus _status;
        public TaskStatus Status { get { return _status; } }
        public static UdonTask CompletedTask { get { return new UdonTask { _status = TaskStatus.RanToCompletion }; } }
        public static UdonTask Started { get { return new UdonTask { _status = TaskStatus.Running }; } }
        public static UdonTask Delay(float seconds) { return new UdonTask(); }
        public static UdonTask DelayFrames(int frames) { return new UdonTask(); }
        public static UdonTask Yield() { return new UdonTask(); }
        public UdonTaskAwaiter GetAwaiter() { return new UdonTaskAwaiter(); }
    }

    public struct UdonTaskAwaiter : INotifyCompletion
    {
        public bool IsCompleted { get { return true; } }
        public void GetResult() { }
        public void OnCompleted(Action continuation) { continuation(); }
    }

    public struct AsyncUdonTaskMethodBuilder
    {
        public static AsyncUdonTaskMethodBuilder Create() { return new AsyncUdonTaskMethodBuilder(); }
        public UdonTask Task { get { return UdonTask.CompletedTask; } }
        public void Start<TStateMachine>(ref TStateMachine stateMachine) where TStateMachine : IAsyncStateMachine { stateMachine.MoveNext(); }
        public void SetStateMachine(IAsyncStateMachine stateMachine) { }
        public void SetResult() { }
        public void SetException(Exception exception) { }
        public void AwaitOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : INotifyCompletion where TStateMachine : IAsyncStateMachine { stateMachine.MoveNext(); }
        public void AwaitUnsafeOnCompleted<TAwaiter, TStateMachine>(ref TAwaiter awaiter, ref TStateMachine stateMachine)
            where TAwaiter : ICriticalNotifyCompletion where TStateMachine : IAsyncStateMachine { stateMachine.MoveNext(); }
    }
}
";

        internal static SyntaxTree Lower(string source, params string[] defines)
        {
            CSharpParseOptions options = CSharpParseOptions.Default
                .WithLanguageVersion(LanguageVersion.CSharp9)
                .WithDocumentationMode(DocumentationMode.None)
                .WithPreprocessorSymbols(defines);

            SyntaxTree prelude = CSharpSyntaxTree.ParseText(Prelude, options, "AsyncLoweringPrelude.cs");
            SyntaxTree fixture = CSharpSyntaxTree.ParseText(source, options, FixturePath);

            var context = new OptimizationContext();
            context.SetSemanticModelTrees(new[] { prelude, fixture });

            return new AsyncMethodTransformOptimizer().Optimize(fixture, context);
        }

        internal static CodeGenerationException Rejection(string source)
        {
            return Assert.Throws<CodeGenerationException>(() => Lower(source));
        }

        /// <summary>
        /// A rejection is only useful at the author's own line, naming the cause. One raised against
        /// generated code, or with a message about something else, is the failure this suite exists for.
        /// </summary>
        internal static void AssertRejectedInFixture(CodeGenerationException rejection, string code, string cause)
        {
            Assert.AreEqual(code, rejection.GeneratorId, rejection.Message);
            Assert.That(rejection.Location != null && rejection.Location.SourceTree != null &&
                        rejection.Location.SourceTree.FilePath == FixturePath,
                "The diagnostic does not point into the author's file: " + rejection.Message);
            StringAssert.Contains(cause, rejection.Message);
        }

        /// <summary>Compiles the lowered file next to the prelude, failing with every error if it does not compile.</summary>
        internal static Assembly Compile(SyntaxTree lowered)
        {
            SyntaxTree prelude = CSharpSyntaxTree.ParseText(Prelude, (CSharpParseOptions)lowered.Options, "AsyncLoweringPrelude.cs");

            CSharpCompilation compilation = CSharpCompilation.Create(
                "CEAsyncLowering_" + Guid.NewGuid().ToString("N"),
                new[] { prelude, lowered },
                CompilationContext.GetMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var stream = new MemoryStream())
            {
                EmitResult emitted = compilation.Emit(stream);
                string errors = string.Join("\n", emitted.Diagnostics
                    .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                    .Select(diagnostic => diagnostic.ToString()));

                Assert.That(emitted.Success, "The lowered program does not compile:\n" + errors + "\n\n" + lowered);
                return Assembly.Load(stream.ToArray());
            }
        }

        internal static LoweredRun Execute(string source, string typeName, string entryMethod, params string[] defines)
        {
            Assembly assembly = Compile(Lower(source, defines));

            Type behaviour = assembly.GetType("UdonSharp.UdonSharpBehaviour", true);
            Type debug = assembly.GetType("UnityEngine.Debug", true);
            Type fixture = assembly.GetType(typeName, true);
            object instance = Activator.CreateInstance(fixture);

            MethodInfo entry = fixture.GetMethod(entryMethod, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(entry != null, "The lowered fixture has no method named " + entryMethod + ".");

            Invoke(entry, instance);
            Invoke(behaviour.GetMethod("Pump"), null, 200);

            return new LoweredRun
            {
                Log = new List<string>((List<string>)debug.GetField("Lines").GetValue(null)),
                MaxEventDepth = (int)behaviour.GetField("MaxEventDepth").GetValue(null),
                EventsRun = (int)behaviour.GetField("EventsRun").GetValue(null),
                Instance = instance,
            };
        }

        private static void Invoke(MethodInfo method, object instance, params object[] arguments)
        {
            try
            {
                method.Invoke(instance, arguments);
            }
            catch (TargetInvocationException failure)
            {
                throw failure.InnerException ?? failure;
            }
        }
    }
}
