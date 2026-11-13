using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;
using UdonSharp.CE.Editor.Analyzers;
using UdonSharp.CE.Net;
using UdonSharp.Compiler;
using UdonSharp.Compiler.Binder;
using UdonSharp.Compiler.Symbols;
using UdonSharp.Compiler.Udon;
using VRC.SDK3.UdonNetworkCalling;
using CompilerSeverity = UdonSharp.Compiler.DiagnosticSeverity;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Regression tests for CE.Net code generation and its analyzers: the [Rpc] emitter, sync-mode
    /// inference, the generator tripwire, CE0010/CE0011/CE0012, and ConflictResolver's array merges.
    ///
    /// Every test here pins a shape that used to be wrong while the suite stayed green, and most of them
    /// were wrong for the same reason: the one existing [Rpc] test ran the emitter over a single tree in
    /// which the attribute cannot bind, and nothing compiled what it emitted. So the emitter's fallback
    /// path was covered and the path a real compile takes was not.
    ///
    /// HOW THE GENERATORS ARE DRIVEN. <see cref="RunGenerator"/> gives the generator what the compiler
    /// gives it: several trees in one compilation, with the CE.Net attribute sources among them. UdonSharp
    /// compiles the CE runtime library from source, so it is deliberately absent from the reference set a
    /// generator's semantic model is built on, and [Rpc] / [Sync] / [SyncOnJoin] only bind when their own
    /// .cs files are trees in the pass -- which they always are in a real compile and never were in a
    /// single-tree test. Fixtures that have to span files are passed as separate trees for the same
    /// reason: "the declaration is in another file" is the normal case, not an edge.
    ///
    /// HOW THE ANALYZERS ARE DRIVEN. The same way CESyncAuthorityTests drives them, and for the reasons
    /// given at length there: the compiler's real Bind phase over one fixture in its own compilation,
    /// handed to the real analyzer instance, so every diagnostic a test sees came from the fixture in
    /// front of it. The harness is repeated here rather than shared because it is private to that class.
    /// </summary>
    public class CENetCodegenRegressionTests
    {
        private const string RpcEmitterTypeName = "UdonSharp.CE.Editor.CodeGen.RpcEmitter";
        private const string SyncModeEmitterTypeName = "UdonSharp.CE.Editor.CodeGen.BehaviourSyncModeEmitter";

        private const string RpcEmitterId = "CEGEN009";

        private const string RpcAnalyzerTypeName = "UdonSharp.CE.Editor.Analyzers.RpcMethodAnalyzer";
        private const string SyncFieldAnalyzerTypeName = "UdonSharp.CE.Editor.Analyzers.SyncFieldAnalyzer";
        private const string LocalOnlyAnalyzerTypeName = "UdonSharp.CE.Editor.Analyzers.LocalOnlyAnalyzer";

        private const string CE0010 = "CE0010";
        private const string CE0011 = "CE0011";
        private const string CE0012 = "CE0012";

        // ------------------------------------------------------------------------------------------
        // [Rpc]: the rate handed to [NetworkCallable]
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The most ordinary declaration there is. It used to be emitted as [NetworkCallable(0)], on the
        /// belief that a parameterless event is unthrottled, and the SDK rejects any network-callable
        /// method with a rate below 1 -- so '[Rpc] public void StartGame()' did not compile.
        ///
        /// The emitted rate is taken out of the generated source and put through the SDK's own metadata
        /// constructor, which is the validation UdonSharpCompilerV1.BuildNetworkMetadata runs. Asserting
        /// on the number alone would pin "5" without pinning the reason 0 was wrong.
        /// </summary>
        [Test]
        public void RpcEmitter_RpcWithoutRateLimit_EmitsARateTheSdkAccepts()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class CeNetRateFixture : UdonSharpBehaviour
{
    [Rpc]
    public void StartGame() { }

    [RpcOwnerOnly]
    public void ResetScore() { }

    [Rpc]
    public void WithArgument(int value) { }

    [Rpc(RateLimit = 20f)]
    public void Declared() { }
}");

            AssertRan(run);

            int[] rates = Regex.Matches(run.Output[0], @"NetworkCallable\((\d+)\)")
                .Cast<Match>()
                .Select(match => int.Parse(match.Groups[1].Value))
                .ToArray();

            Assert.That(rates.Length, Is.EqualTo(4),
                "Every [Rpc] must be marked [NetworkCallable] with an explicit rate.\n" + run.Output[0]);

            foreach (int rate in rates)
            {
                Assert.DoesNotThrow(
                    () => new NetworkCallingEntrypointMetadata("CeNetRateFixtureEvent", new NetworkCallableAttribute(rate)),
                    $"The emitter produced [NetworkCallable({rate})], which the SDK refuses when UdonSharp " +
                    "builds the method's network metadata. That is a compile error on a correct [Rpc].");
            }

            Assert.That(rates.Count(rate => rate == 5), Is.EqualTo(3),
                "An [Rpc] that declares no RateLimit gets the [NetworkCallable] default, with or without " +
                "parameters.\n" + run.Output[0]);

            Assert.That(rates, Does.Contain(20), "A declared RateLimit must still be the rate that is emitted.");

            AssertOutputCompiles(run);
        }

        /// <summary>
        /// The premise of the test above, pinned on its own: if the SDK ever starts accepting 0 this fails
        /// and says the emitter's floor can be revisited, instead of leaving a rule nobody can explain.
        /// </summary>
        [Test]
        public void NetworkCallable_WithARateBelowOne_IsRejectedByTheSdk()
        {
            Assert.Throws<ArgumentException>(
                () => new NetworkCallingEntrypointMetadata("CeNetRateFixtureEvent", new NetworkCallableAttribute(0)));
        }

        // ------------------------------------------------------------------------------------------
        // [Rpc]: call-site arguments
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// A direct call converts its arguments to the parameter types. The send cannot, because every
        /// SendCustomNetworkEvent parameter is 'object' -- so 'SetVolume(1)' boxed an Int32 for a float
        /// parameter, and VRChat refuses an event whose argument types do not match the entry point.
        /// </summary>
        [Test]
        public void RpcEmitter_CallSite_SendsArgumentsAsTheDeclaredParameterTypes()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

public enum CeNetPhase { Lobby, Playing }

public class CeNetConversionFixture : UdonSharpBehaviour
{
    public bool flag;
    public int someInt;
    public float someFloat;
    public Vector2 planar;
    public int[] ids;

    [Rpc] public void SetVolume(float volume) { }
    [Rpc] public void SetBig(long id) { }
    [Rpc] public void SetPhase(CeNetPhase phase) { }
    [Rpc] public void SetName(string name) { }
    [Rpc] public void SetPosition(Vector3 position) { }
    [Rpc] public void SetIds(int[] values) { }
    [Rpc] public void Two(int a, float b) { }

    public void Go()
    {
        SetVolume(1);
        SetVolume(someInt);
        SetVolume(someFloat);
        SetVolume(0.5f);
        SetVolume(default);
        SetBig(5);
        SetPhase(CeNetPhase.Playing);
        SetName(null);
        SetName(""x"");
        SetPosition(planar);
        SetIds(ids);
        Two(b: 2, a: 1);
        SetPosition(new(1, 2, 3));
        SetVolume((default));
        SetVolume(flag ? default : default);
    }
}");

            AssertRan(run);
            string output = run.Output[0];

            Assert.That(output, Does.Contain("\"SetVolume\", (float)(1))"),
                "An int literal passed to a float parameter must be sent as a float.\n" + output);
            Assert.That(output, Does.Contain("\"SetVolume\", (float)(someInt))"),
                "An int variable passed to a float parameter must be sent as a float.");
            Assert.That(output, Does.Contain("\"SetBig\", (long)(5))"));
            Assert.That(output, Does.Contain("\"SetPosition\", (global::UnityEngine.Vector3)(planar))"),
                "A user-visible implicit conversion (Vector2 to Vector3) is a conversion like any other.");
            Assert.That(output, Does.Contain("\"SetVolume\", default(float))"),
                "A bare 'default' takes its type from where it is written, so forwarded untouched it is " +
                "default(object) -- null -- rather than the parameter's default.");
            Assert.That(output, Does.Contain("\"Two\", 1, (float)(2))"),
                "Named arguments are still put back in parameter order, and still converted.");

            // Target-typed arguments. Roslyn reports the parameter's type as theirs, so an emitter that only
            // compares that type with the parameter's forwards them untouched -- and in an 'object' slot
            // 'new(1, 2, 3)' no longer compiles, while '(default)' and a typeless conditional become null.
            Assert.That(output, Does.Contain("\"SetPosition\", (global::UnityEngine.Vector3)(new(1, 2, 3)))"),
                "A target-typed 'new' has to keep the type the direct call gave it.\n" + output);
            Assert.That(Regex.Matches(output, Regex.Escape("\"SetVolume\", default(float))")).Count, Is.EqualTo(2),
                "Both the bare and the parenthesized 'default' must be sent as the parameter's default.\n" + output);
            Assert.That(output, Does.Contain("\"SetVolume\", (float)(flag ? default : default))"),
                "A conditional with no type of its own takes the parameter's type only from the call.");

            // The other half: nothing is added where the direct call converted nothing. A cast on every
            // argument would also "work", and would bury the ones that matter in the generated source.
            Assert.That(output, Does.Contain("\"SetVolume\", someFloat)"));
            Assert.That(output, Does.Contain("\"SetVolume\", 0.5f)"));
            Assert.That(output, Does.Contain("\"SetPhase\", CeNetPhase.Playing)"));
            Assert.That(output, Does.Contain("\"SetName\", null)"));
            Assert.That(output, Does.Contain("\"SetName\", \"x\")"));
            Assert.That(output, Does.Contain("\"SetIds\", ids)"));

            AssertOutputCompiles(run);
        }

        // ------------------------------------------------------------------------------------------
        // [Rpc]: recognising the declaration
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The declaration half used to match the attribute's written name while the call-site half
        /// resolved its bound type. Under an alias the two disagreed: the call became a send and the
        /// receiver got no [NetworkCallable], no rate limit and no owner guard.
        /// </summary>
        [Test]
        public void RpcEmitter_AliasedAttribute_IsAnRpcAtTheDeclarationToo()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;
using NetCall = UdonSharp.CE.Net.RpcAttribute;

public class CeNetAliasFixture : UdonSharpBehaviour
{
    [NetCall(OwnerOnly = true)]
    public void Kick(int id) { }

    public void Go() { Kick(3); }
}");

            AssertRan(run);
            string output = run.Output[0];

            Assert.That(output, Does.Contain("SendCustomNetworkEvent"), "The call site is rewritten either way.");
            Assert.That(output, Does.Contain("NetworkCallable(5)"),
                "The call is sent, so the receiver has to be network callable. Without this the send " +
                "names an entry point with no network metadata.\n" + output);
            Assert.That(output, Does.Contain("__ce_rpcCaller"),
                "OwnerOnly = true was declared. A receiver without the guard is callable by any player " +
                "while its attribute says otherwise.");

            AssertOutputCompiles(run);
        }

        /// <summary>
        /// The same disagreement through a derived attribute type, in the shape that hid it completely:
        /// neither file contains the letters "Rpc", so the old per-file substring gate never walked them.
        /// </summary>
        [Test]
        public void RpcEmitter_DerivedAttributeInFilesThatNeverSpellRpc_IsStillAnRpc()
        {
            const string declaration = @"
using UdonSharp;

public class CeNetGate : UdonSharpBehaviour
{
    [CeNetServerCommand]
    public void Open(int id) { }
}";

            const string caller = @"
using UdonSharp;

public class CeNetLever : UdonSharpBehaviour
{
    public CeNetGate gate;

    public void Pull() { gate.Open(1); }
}";

            const string attribute = @"
public class CeNetServerCommandAttribute : UdonSharp.CE.Net.RpcAttribute { }";

            Assert.That(declaration + caller, Does.Not.Contain("Rpc"),
                "This fixture only tests what it claims to while neither file spells the attribute.");

            GeneratorRun run = RunGenerator(RpcEmitterTypeName, declaration, caller, attribute);

            AssertRan(run);

            Assert.That(run.Output[0], Does.Contain("NetworkCallable(5)"),
                "The declaring file was not walked, or the derived attribute was not recognised.\n" + run.Output[0]);
            Assert.That(run.Output[1], Does.Contain("gate.SendCustomNetworkEvent("),
                "The cross-file call site was not rewritten.\n" + run.Output[1]);

            AssertOutputCompiles(run);
        }

        /// <summary>
        /// CE0045 has to see an aliased [Rpc] as the enclosing method as well, or a call made from inside
        /// one is quietly rewritten into the re-broadcast the check exists to refuse.
        /// </summary>
        [Test]
        public void RpcEmitter_CallFromInsideAnAliasedRpc_IsStillCE0045()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using NetCall = UdonSharp.CE.Net.RpcAttribute;

public class CeNetRelayFixture : UdonSharpBehaviour
{
    [NetCall]
    public void First() { Second(); }

    [Rpc]
    public void Second() { }
}");

            AssertFailedWith(run, "CE0045");
        }

        // ------------------------------------------------------------------------------------------
        // [Rpc]: static methods
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// UdonSharp exports no static method as an event, so a static [Rpc] compiled cleanly and then ran
        /// on no client at all -- the call site was still rewritten into a send naming an event that does
        /// not exist.
        /// </summary>
        [Test]
        public void RpcEmitter_StaticRpc_IsRejectedDuringCodegen()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class CeNetStaticFixture : UdonSharpBehaviour
{
    [Rpc]
    public static void Announce(int id) { }

    public void Go() { Announce(3); }
}");

            AssertFailedWith(run, CE0011);
            Assert.That(run.Failure.Message, Does.Contain("Announce").And.Contain("static"));
        }

        [Test]
        public void CE0011_StaticRpc_IsReported_AndAnInstanceRpcIsNot()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(RpcAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class CeNetStaticAnalyzerFixture : UdonSharpBehaviour
{
    [Rpc(RateLimit = 5f)]
    public static void Announce(int id) { }

    [Rpc(RateLimit = 5f)]
    public void Instance(int id) { }
}", "CeNetStaticAnalyzerFixture");

            AnalyzerDiagnostic[] errors = diagnostics
                .Where(diagnostic => diagnostic.Code == CE0011 && diagnostic.Severity == CompilerSeverity.Error)
                .ToArray();

            Assert.That(errors.Length, Is.EqualTo(1),
                "Exactly the static [Rpc] is an error.\n" + Render(diagnostics));
            Assert.That(errors[0].Message, Does.Contain("Announce").And.Contain("static"));
        }

        // ------------------------------------------------------------------------------------------
        // [Rpc]: declarations that need nothing added
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// An [Rpc] already completed by hand and sent explicitly changes no tree. The USCE0001 tripwire
        /// reads "wrote [Rpc], transformed nothing" as the emitter having stopped working, so the emitter
        /// has to say that it recognised the declaration and correctly left it alone.
        ///
        /// '[NetworkCallableAttribute(10)]' is in here on purpose: read as "no [NetworkCallable]", it got
        /// a second one added beside it, which is CS0579.
        /// </summary>
        [Test]
        public void RpcEmitter_AlreadyCompleteRpc_IsLeftAloneAndSaysSo()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDK3.UdonNetworkCalling;

public class CeNetCompleteFixture : UdonSharpBehaviour
{
    [Rpc]
    [NetworkCallableAttribute(10)]
    public void Ping(int value) { }

    [Rpc, NetworkCallable(7)]
    public void Pong(int value) { }

    public void Go() { CENet.Rpc(this, RpcTarget.All, nameof(Ping), 1); }
}");

            AssertRan(run);

            Assert.That(run.TreeChanged[0], Is.False,
                "Nothing here needed adding, so nothing may be added -- a second [NetworkCallable] is a " +
                "duplicate attribute.\n" + run.Output[0]);

            Assert.That(run.ObligationsDischarged, Is.EqualTo(2),
                "Each recognised declaration that needed no change has to be reported as handled, or a " +
                "project made only of these fails its compile with USCE0001.");

            AssertOutputCompiles(run);
        }

        /// <summary>
        /// The other direction, and the one that keeps the tripwire meaningful: a file with no [Rpc] must
        /// never report a discharged obligation, however much its identifiers look like one.
        /// </summary>
        [Test]
        public void RpcEmitter_FileWithoutAnRpc_DischargesNothing()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;

public class CeNetPlainFixture : UdonSharpBehaviour
{
    public int[] counts;
    public int RpcKind;

    public void Go() { counts[RpcKind]++; }
}");

            AssertRan(run);

            Assert.That(run.TreeChanged[0], Is.False);
            Assert.That(run.ObligationsDischarged, Is.EqualTo(0),
                "Reporting an obligation as discharged where no [Rpc] was recognised blinds USCE0001 to " +
                "the failure it exists to catch.");
        }

        // ------------------------------------------------------------------------------------------
        // [Rpc]: the owner guard
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The guard is spliced into the author's method body, so a bare 'gameObject' in it binds to
        /// whatever the method itself calls gameObject.
        /// </summary>
        [Test]
        public void RpcEmitter_OwnerGuard_NamesThisGameObject()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

public class CeNetGuardFixture : UdonSharpBehaviour
{
    public GameObject prefab;

    [RpcOwnerOnly]
    public void Spawn()
    {
        GameObject gameObject = prefab;
        gameObject.SetActive(true);
    }
}");

            AssertRan(run);

            Assert.That(run.Output[0], Does.Contain("IsOwner(__ce_rpcCaller, this.gameObject)"),
                "A bare 'gameObject' here is captured by the local of the same name declared below it.\n" +
                run.Output[0]);

            AssertOutputCompiles(run);
        }

        // ------------------------------------------------------------------------------------------
        // The generator tripwire
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// A trigger written with a leading '[' names an attribute. "Not a comment, not a string" let an
        /// array index that starts with the same letters count as a use of it, so a project with no [Rpc]
        /// at all failed its compile with USCE0001 over 'counts[RpcKind]'.
        /// </summary>
        [Test]
        public void Tripwire_BracketedTrigger_CountsOnlyWhereItOpensAnAttributeList()
        {
            Assert.That(DeclaresTrigger(@"
public class A
{
    public int[] counts;
    public int RpcKind;
    public int[] PlayerDataSlots = new int[4];

    public void Go() { counts[RpcKind]++; int[] local = new int[PlayerDataSlots.Length]; }
}", "[Rpc"), Is.False, "An element access is not an attribute.");

            Assert.That(DeclaresTrigger(@"
public class A
{
    public int[] PlayerDataSlots = new int[4];

    public int First() { return PlayerDataSlots[PlayerDataSlots.Length - 1]; }
}", "[PlayerData"), Is.False, "An array index is not an attribute.");

            Assert.That(DeclaresTrigger(@"
public class A
{
    // [Rpc] is mentioned here,
    public string text = ""and [Rpc] here"";
}", "[Rpc"), Is.False, "Comments and strings were already excluded and must stay excluded.");

            Assert.That(DeclaresTrigger(@"
public class A
{
    [Rpc]
    public void Go() { }
}", "[Rpc"), Is.True, "The real thing still has to trip it.");
        }

        /// <summary>
        /// "[X" stands for [X] or [XAttribute], never for an attribute whose name merely starts with X. A
        /// project's own [RpcLogger], [PlayerDataCache] or [SyncOnJoinHelper] is a different attribute, and
        /// in a project with no real use of the generator's own the prefix match failed a correct compile
        /// with USCE0001.
        /// </summary>
        [Test]
        public void Tripwire_BracketedTrigger_CountsOnlyTheWholeAttributeName()
        {
            Assert.That(DeclaresTrigger(@"
public class A
{
    [RpcLogger] public void Go() { }
}", "[Rpc"), Is.False, "[RpcLogger] is not an [Rpc].");

            Assert.That(DeclaresTrigger(@"
[PlayerDataCache]
public class A { }", "[PlayerData"), Is.False, "[PlayerDataCache] is not a [PlayerData].");

            Assert.That(DeclaresTrigger(@"
public class A
{
    [SyncOnJoinHelper] public int value;
}", "[SyncOnJoin"), Is.False, "[SyncOnJoinHelper] is not a [SyncOnJoin].");

            Assert.That(DeclaresTrigger(@"
public class A
{
    [Rpc.Inner] public void Go() { }
}", "[Rpc"), Is.False, "A qualified name that starts with the trigger names some other attribute.");

            Assert.That(DeclaresTrigger(@"
public class A
{
    [RpcOwnerOnly] public void Go() { }
}", "[Rpc"), Is.False,
                "[RpcOwnerOnly] is found by a token of its own, not as a prefix of '[Rpc'.");

            // The other half: every whole spelling still counts -- with the suffix, with arguments, and
            // first in a list that carries more than one attribute.
            Assert.That(DeclaresTrigger(@"
public class A
{
    [Rpc(RpcTarget.All, RateLimit = 2f)] public void Go(int value) { }
}", "[Rpc"), Is.True);

            Assert.That(DeclaresTrigger(@"
public class A
{
    [RpcAttribute] public void Go() { }
}", "[Rpc"), Is.True);

            Assert.That(DeclaresTrigger(@"
public class A
{
    [Rpc, NetworkCallable(5)] public void Go() { }
}", "[Rpc"), Is.True);

            Assert.That(DeclaresTrigger(@"
public class A
{
    [RpcOwnerOnly] public void Go() { }
}", "[RpcOwnerOnly"), Is.True);

            Assert.That(DeclaresTrigger(@"
public class A
{
    [SyncOnJoin(Group = ""cold"")] public int value;
}", "[SyncOnJoin"), Is.True);

            Assert.That(DeclaresTrigger(@"
[PlayerData(""key"", Version = 2)]
public class A { }", "[PlayerData"), Is.True);
        }

        /// <summary>
        /// With whole-name matching, an attribute spelling the emitter is obliged by but no token names is a
        /// spelling the tripwire can no longer see. [RpcOwnerOnly] is that spelling for the [Rpc] emitter, so
        /// the tokens it actually registers are checked here rather than assumed.
        /// </summary>
        [Test]
        public void Tripwire_RpcEmitterTokens_CoverEverySpellingOfRpc()
        {
            IReadOnlyList<string> tokens = TriggerTokensOf(RpcEmitterId);

            Assert.That(DeclaredByAnyToken(tokens, @"
public class A
{
    [Rpc] public void Go() { }
}"), Is.True);

            Assert.That(DeclaredByAnyToken(tokens, @"
public class A
{
    [RpcOwnerOnly] public void Go() { }
}"), Is.True,
                "A project whose only [Rpc] is the shorthand has to keep the tripwire.");

            Assert.That(DeclaredByAnyToken(tokens, @"
public class A
{
    [RpcLogger] public void Go() { }
}"), Is.False,
                "A project with no [Rpc] at all must not be failed over an attribute of its own.");
        }

        /// <summary>
        /// A trigger with no bracket keeps the rule it always had. CESourceGeneratorContractTests relies on
        /// exactly this with the token "SyncOnJoin".
        /// </summary>
        [Test]
        public void Tripwire_UnbracketedTrigger_StillMatchesAnywhereInCode()
        {
            Assert.That(DeclaresTrigger(@"
public class A
{
    public int SyncOnJoinCount;
}", "SyncOnJoin"), Is.True);

            Assert.That(DeclaresTrigger(@"
public class A
{
    // SyncOnJoin
    public int count;
}", "SyncOnJoin"), Is.False);
        }

        // ------------------------------------------------------------------------------------------
        // Sync-mode inference: the whole type, not one declaration
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// A partial behaviour is one type. Deciding per declaration injected an attribute into every part
        /// that held a synced field -- CS0579 on a class whose author wrote none -- and the part that
        /// decides has to see the other part's array or it infers the wrong mode.
        /// </summary>
        [Test]
        public void SyncModeEmitter_PartialBehaviour_GetsOneModeDecidedFromEveryPart()
        {
            GeneratorRun run = RunGenerator(SyncModeEmitterTypeName, @"
using UdonSharp;

public partial class CeNetPartialFixture : UdonSharpBehaviour
{
    [UdonSynced] public int red;
}", @"
using UdonSharp;

public partial class CeNetPartialFixture
{
    [UdonSynced] public int[] history;
}");

            AssertRan(run);
            string both = run.Output[0] + run.Output[1];

            Assert.That(Regex.Matches(both, @"UdonBehaviourSyncMode\(").Count, Is.EqualTo(1),
                "Two injected attributes on one partial type is CS0579.\n" + both);
            Assert.That(both, Does.Contain("BehaviourSyncMode.Manual"),
                "The array is in the other part, and it is this type's state all the same.");

            AssertOutputCompiles(run);
        }

        /// <summary>
        /// A derived behaviour's program carries its base's synced fields. Reading only the derived
        /// declaration made it Continuous over an inherited array, which the compiler then rejects on a
        /// class that declared nothing.
        /// </summary>
        [Test]
        public void SyncModeEmitter_DerivedBehaviour_InheritsTheBasesArrayRequirement()
        {
            GeneratorRun run = RunGenerator(SyncModeEmitterTypeName, @"
using UdonSharp;

public abstract class CeNetInventoryBase : UdonSharpBehaviour
{
    [UdonSynced] protected int[] slots = new int[8];
}", @"
using UdonSharp;

public class CeNetPlayerInventory : CeNetInventoryBase
{
    [UdonSynced] public int gold;
}", @"
using UdonSharp;

public class CeNetEmptyInventory : CeNetInventoryBase
{
    public int notSynced;
}");

            AssertRan(run);

            Assert.That(run.Output[0], Does.Contain("BehaviourSyncMode.Manual"));
            Assert.That(run.Output[1], Does.Contain("BehaviourSyncMode.Manual").And.Not.Contain("BehaviourSyncMode.Continuous"),
                "The derived behaviour syncs 'slots' too. Continuous here is a compile error about a field " +
                "its author never mentioned.\n" + run.Output[1]);

            Assert.That(run.TreeChanged[2], Is.False,
                "A class that adds no synced state of its own is left to resolve to its base's mode; " +
                "injecting here would only be a second place for the two to drift apart.");

            AssertOutputCompiles(run);
        }

        /// <summary>
        /// The silent version of the same bug. Base is given Manual for its [SyncOnJoin] payload, Derived
        /// looked only at 'speed' and was given Continuous, and the compiler stops at the first attribute
        /// climbing from the emitted type -- so Derived ran Continuous while carrying the inherited
        /// payload, with nothing reported because a synced string is legal under Continuous.
        /// </summary>
        [Test]
        public void SyncModeEmitter_InheritedSyncOnJoin_ForcesManualOnTheDerivedBehaviour()
        {
            GeneratorRun run = RunGenerator(SyncModeEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class CeNetJoinBase : UdonSharpBehaviour
{
    [SyncOnJoin] public int phase;
}

public class CeNetJoinDerived : CeNetJoinBase
{
    [UdonSynced] public float speed;
}");

            AssertRan(run);
            string output = run.Output[0];

            Assert.That(ModeInjectedOn(output, "CeNetJoinBase"), Is.EqualTo("Manual"), output);
            Assert.That(ModeInjectedOn(output, "CeNetJoinDerived"), Is.EqualTo("Manual"),
                "Inherited [SyncOnJoin] state must force Manual exactly as an own field does.\n" + output);

            AssertOutputCompiles(run);
        }

        // ------------------------------------------------------------------------------------------
        // Sync-mode inference: a string prefers Manual, it does not require it
        // ------------------------------------------------------------------------------------------

        [Test]
        public void SyncModeEmitter_SyncedString_InfersManualOnlyWhenNothingIsInterpolated()
        {
            GeneratorRun run = RunGenerator(SyncModeEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

public class CeNetOnlyString : UdonSharpBehaviour
{
    [UdonSynced] public string label;
}

public class CeNetSmoothAndString : UdonSharpBehaviour
{
    [UdonSynced(UdonSyncMode.Smooth)] public Vector3 position;
    [UdonSynced] public string label;
}

public class CeNetSyncLinearAndString : UdonSharpBehaviour
{
    [Sync(Authority.Owner, InterpolationMode.Linear)] public Vector3 position;
    [Sync(Authority.Owner)] public string label;
}

public class CeNetSyncNamedSmoothAndString : UdonSharpBehaviour
{
    [Sync(Authority.Owner, Interpolation = InterpolationMode.Smooth)] public Vector3 position;
    [UdonSynced] public string label;
}

public class CeNetExplicitNoneAndString : UdonSharpBehaviour
{
    [UdonSynced(UdonSyncMode.None)] public Vector3 position;
    [UdonSynced] public string label;
}

public class CeNetOnlyScalar : UdonSharpBehaviour
{
    [UdonSynced] public int value;
}");

            AssertRan(run);
            string output = run.Output[0];

            Assert.That(ModeInjectedOn(output, "CeNetOnlyString"), Is.EqualTo("Manual"),
                "A string still asks for Manual when nothing argues against it.\n" + output);

            // Manual here is the one mode the compiler then rejects the interpolation under.
            Assert.That(ModeInjectedOn(output, "CeNetSmoothAndString"), Is.EqualTo("Continuous"), output);
            Assert.That(ModeInjectedOn(output, "CeNetSyncLinearAndString"), Is.EqualTo("Continuous"), output);
            Assert.That(ModeInjectedOn(output, "CeNetSyncNamedSmoothAndString"), Is.EqualTo("Continuous"), output);

            Assert.That(ModeInjectedOn(output, "CeNetExplicitNoneAndString"), Is.EqualTo("Manual"),
                "UdonSyncMode.None interpolates nothing, so it does not hold the string's preference back.");
            Assert.That(ModeInjectedOn(output, "CeNetOnlyScalar"), Is.EqualTo("Continuous"), output);

            AssertOutputCompiles(run);
        }

        /// <summary>
        /// CE0034 is for state Continuous cannot carry at all. It used to fire for a string, which
        /// Continuous does sync, and that rejected a working, declared configuration.
        /// </summary>
        [Test]
        public void CE0034_DeclaredContinuous_AcceptsAStringAndStillRejectsAnArray()
        {
            GeneratorRun withString = RunGenerator(SyncModeEmitterTypeName, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class CeNetContinuousString : UdonSharpBehaviour
{
    [UdonSynced] public string label;
}");

            AssertRan(withString);
            Assert.That(withString.TreeChanged[0], Is.False, "A declared mode is validated, never rewritten.");

            GeneratorRun withArray = RunGenerator(SyncModeEmitterTypeName, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class CeNetContinuousArray : UdonSharpBehaviour
{
    [UdonSynced] public int[] slots;
}");

            AssertFailedWith(withArray, "CE0034");

            GeneratorRun withInheritedArray = RunGenerator(SyncModeEmitterTypeName, @"
using UdonSharp;

public class CeNetArrayBase : UdonSharpBehaviour
{
    [UdonSynced] public int[] slots;
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class CeNetArrayDerived : CeNetArrayBase
{
    [UdonSynced] public int gold;
}");

            AssertFailedWith(withInheritedArray, "CE0034");
            Assert.That(withInheritedArray.Failure.Message, Does.Contain("slots").And.Contain("CeNetArrayBase"),
                "The report has to say which inherited field it is about; the derived class does not " +
                "mention it anywhere.");
        }

        // ------------------------------------------------------------------------------------------
        // Sync-mode inference: what the injection does to the file
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The injected attribute used to take a COPY of the class's leading trivia, so every comment
        /// above the class was printed twice and each later diagnostic in the file -- reported by line in
        /// the rewritten tree -- pointed that many lines below the code it was about.
        /// </summary>
        [Test]
        public void SyncModeEmitter_InjectedAttribute_AddsNoLinesAndRepeatsNoComments()
        {
            const string source =
                "using UdonSharp;\r\n" +
                "\r\n" +
                "namespace CeNetTrivia\r\n" +
                "{\r\n" +
                "    // Keeps the score.\r\n" +
                "    // Second line.\r\n" +
                "    public class CeNetScore : UdonSharpBehaviour\r\n" +
                "    {\r\n" +
                "        [UdonSynced] public int value;\r\n" +
                "    }\r\n" +
                "\r\n" +
                "    [UnityEngine.DefaultExecutionOrder(5)]\r\n" +
                "    public class CeNetTagged : UdonSharpBehaviour\r\n" +
                "    {\r\n" +
                "        [UdonSynced] public int value;\r\n" +
                "    }\r\n" +
                "}\r\n";

            GeneratorRun run = RunGenerator(SyncModeEmitterTypeName, source);

            AssertRan(run);
            string output = run.Output[0];

            Assert.That(Regex.Matches(output, @"UdonBehaviourSyncMode\(").Count, Is.EqualTo(2), output);
            Assert.That(Regex.Matches(output, Regex.Escape("// Keeps the score.")).Count, Is.EqualTo(1), output);
            Assert.That(Regex.Matches(output, Regex.Escape("// Second line.")).Count, Is.EqualTo(1), output);
            Assert.That(output.Split('\n').Length, Is.EqualTo(source.Split('\n').Length),
                "The rewritten file must have the author's line count, or every diagnostic below the " +
                "class points at the wrong line.\n" + output);

            AssertOutputCompiles(run);
        }

        // ------------------------------------------------------------------------------------------
        // CE0010: Quantize on a float is the documented use
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The type test compared against "System.Single" a name that printed as "float", so CE0010 told
        /// the author of '[Sync(Authority.Owner, Quantize = 0.1f)] public float speed;' that Quantize is
        /// only effective on float types.
        /// </summary>
        [Test]
        public void CE0010_QuantizeOnFloatingPointFields_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(SyncFieldAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CeNetQuantizedFloats : UdonSharpBehaviour
{
    [Sync(Authority.Owner, Quantize = 0.1f)] public float speed;
    [Sync(Authority.Owner, Quantize = 0.1f)] public double precise;
    [Sync(Authority.Owner, Quantize = 0.1f)] public float[] samples;
    [Sync(Authority.Owner, Quantize = 0.1f)] public Vector3 position;
}", "CeNetQuantizedFloats");

            Assert.That(diagnostics.Where(diagnostic => diagnostic.Code == CE0010), Is.Empty,
                "Every one of these is a float type Quantize does apply to.\n" + Render(diagnostics));
        }

        [Test]
        public void CE0010_QuantizeOnAnInteger_IsStillReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(SyncFieldAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class CeNetQuantizedInt : UdonSharpBehaviour
{
    [Sync(Authority.Owner, Quantize = 0.1f)] public int count;
}", "CeNetQuantizedInt");

            AnalyzerDiagnostic[] reported = diagnostics.Where(diagnostic => diagnostic.Code == CE0010).ToArray();

            Assert.That(reported.Length, Is.EqualTo(1), Render(diagnostics));
            Assert.That(reported[0].Message, Does.Contain("Quantize").And.Contain("count"));
        }

        // ------------------------------------------------------------------------------------------
        // CE0011: OwnerOnly and Target are independent
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Target chooses who receives, OwnerOnly whose send is honoured. The owner announcing to everyone
        /// else is an ordinary working shape, and it used to be called contradictory.
        /// </summary>
        [Test]
        public void CE0011_OwnerOnlyWithTargetOthers_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(RpcAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class CeNetOwnerAnnounce : UdonSharpBehaviour
{
    [Rpc(Target = RpcTarget.Others, OwnerOnly = true, RateLimit = 5f)]
    public void ShowHit(int id) { }

    [Rpc(Target = RpcTarget.All, OwnerOnly = true, RateLimit = 5f)]
    public void StartRound(int id) { }
}", "CeNetOwnerAnnounce");

            Assert.That(
                diagnostics.Where(diagnostic => diagnostic.Severity != CompilerSeverity.Log),
                Is.Empty,
                "Neither combination is a mistake.\n" + Render(diagnostics));
        }

        /// <summary>
        /// The pairing that really does nothing: delivered to the owner alone, then dropped unless the
        /// owner sent it. The old warning recommended it as the fix.
        /// </summary>
        [Test]
        public void CE0011_OwnerOnlyWithTargetOwner_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(RpcAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class CeNetOwnerEcho : UdonSharpBehaviour
{
    [Rpc(Target = RpcTarget.Owner, OwnerOnly = true, RateLimit = 5f)]
    public void Echo(int id) { }

    [Rpc(Target = RpcTarget.Owner, RateLimit = 5f)]
    public void Request(int id) { }
}", "CeNetOwnerEcho");

            AnalyzerDiagnostic[] warnings = diagnostics
                .Where(diagnostic => diagnostic.Code == CE0011 && diagnostic.Severity == CompilerSeverity.Warning)
                .ToArray();

            Assert.That(warnings.Length, Is.EqualTo(1),
                "Only the OwnerOnly one is degenerate; a plain request to the owner is the normal use of " +
                "Target = Owner.\n" + Render(diagnostics));
            Assert.That(warnings[0].Message, Does.Contain("Echo").And.Contain("Target=Owner"));
        }

        // ------------------------------------------------------------------------------------------
        // CE0011: RateLimit beside a hand-written [NetworkCallable]
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The emitter does not add a second [NetworkCallable] to an [Rpc] that already carries one, so the
        /// attribute the author wrote sets the rate and RateLimit is read by nothing. A RateLimit that says
        /// otherwise was a declared rate that never ran, with nothing reporting it.
        /// </summary>
        [Test]
        public void CE0011_RateLimitBesideADifferentHandWrittenNetworkCallable_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(RpcAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDK3.UdonNetworkCalling;

public class CeNetRateConflict : UdonSharpBehaviour
{
    [Rpc(RateLimit = 10f)]
    [NetworkCallable(3)]
    public void Explicit(int id) { }

    [Rpc(RateLimit = 10f)]
    [NetworkCallable]
    public void Bare(int id) { }
}", "CeNetRateConflict");

            AnalyzerDiagnostic[] warnings = diagnostics
                .Where(diagnostic => diagnostic.Code == CE0011 && diagnostic.Severity == CompilerSeverity.Warning)
                .ToArray();

            Assert.That(warnings.Length, Is.EqualTo(2), Render(diagnostics));

            Assert.That(
                warnings.Any(warning => warning.Message.Contains("'Explicit'") &&
                                        warning.Message.Contains("[NetworkCallable(3)]") &&
                                        warning.Message.Contains("RateLimit = 10")),
                Is.True,
                "The report has to name the rate that is in effect, the hand-written 3.\n" + Render(diagnostics));

            Assert.That(
                warnings.Any(warning => warning.Message.Contains("'Bare'") &&
                                        warning.Message.Contains("[NetworkCallable(5)]")),
                Is.True,
                "A bare [NetworkCallable] enforces its own default of 5, not RateLimit.\n" + Render(diagnostics));
        }

        /// <summary>
        /// The other half: no RateLimit, or one the [NetworkCallable] agrees with, contradicts nothing. Nor
        /// does the [NetworkCallable] the emitter itself added -- analysis sees the rewritten tree, and that
        /// attribute carries the rate the emitter derived from RateLimit; for an inexpressible RateLimit
        /// like 2.5 the emitter has already reported the rounding on its own.
        /// </summary>
        [Test]
        public void CE0011_RateLimitThatAgreesIsUnsetOrWasEmitted_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(RpcAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDK3.UdonNetworkCalling;

public class CeNetRateAgreement : UdonSharpBehaviour
{
    [Rpc(RateLimit = 3f)]
    [NetworkCallable(3)]
    public void Agrees(int id) { }

    [Rpc]
    [NetworkCallable(3)]
    public void Unset(int id) { }

    [Rpc(RateLimit = 2.5f)]
    [global::VRC.SDK3.UdonNetworkCalling.NetworkCallable(3)]
    public void Emitted(int id) { }

    [Rpc(RateLimit = 10f)]
    public void NoNetworkCallable(int id) { }
}", "CeNetRateAgreement");

            Assert.That(
                diagnostics.Where(diagnostic => diagnostic.Code == CE0011 && diagnostic.Severity == CompilerSeverity.Warning),
                Is.Empty,
                Render(diagnostics));
        }

        /// <summary>
        /// The analyzer tells the emitter's [NetworkCallable] from the author's by its spelling, so the
        /// spelling has to be the one the emitter really writes.
        /// </summary>
        [Test]
        public void RpcEmitter_InjectsNetworkCallableUnderTheSpellingTheAnalyzerRecognises()
        {
            GeneratorRun run = RunGenerator(RpcEmitterTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class CeNetInjectedSpelling : UdonSharpBehaviour
{
    [Rpc(RateLimit = 7f)]
    public void Ping(int id) { }
}");

            AssertRan(run);

            FieldInfo spelling = GetAssemblyByName("UdonSharp.CE.Editor")
                .GetType(RpcEmitterTypeName, throwOnError: true)
                .GetField("InjectedNetworkCallableName", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            Assert.That(spelling, Is.Not.Null, "RpcEmitter.InjectedNetworkCallableName is gone.");
            Assert.That(run.Output[0], Does.Contain("[" + spelling.GetValue(null) + "(7)]"), run.Output[0]);
        }

        // ------------------------------------------------------------------------------------------
        // CE0012: which behaviour the event is sent to
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The event name matching one of this type's [LocalOnly] methods says nothing until the event is
        /// known to be sent to this type. CE0012 is an Error, so this false positive failed the compile.
        /// </summary>
        [Test]
        public void CE0012_EventSentToAnotherBehaviourType_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(LocalOnlyAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.Udon.Common.Interfaces;

public class CeNetDoorFx : UdonSharpBehaviour
{
    public void Open() { }
}

public class CeNetDoor : UdonSharpBehaviour
{
    public CeNetDoorFx fx;

    [LocalOnly(""Plays the hinge animation on this client only"")]
    private void Open() { }

    public void Use()
    {
        Open();
        fx.SendCustomNetworkEvent(NetworkEventTarget.All, ""Open"");
    }
}", "CeNetDoor");

            Assert.That(diagnostics.Where(diagnostic => diagnostic.Code == CE0012), Is.Empty,
                "'Open' was sent to a CeNetDoorFx. That is not CeNetDoor's [LocalOnly] Open().\n" +
                Render(diagnostics));
        }

        [Test]
        public void CE0012_EventSentToThisBehaviourType_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(LocalOnlyAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.Udon.Common.Interfaces;

public class CeNetVfxManager : UdonSharpBehaviour
{
    public CeNetVfxManager other;

    [LocalOnly(""VFX render locally"")]
    private void PlayVfx() { }

    public void ToSelf()
    {
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(PlayVfx));
    }

    public void ToAnotherInstance()
    {
        other.SendCustomNetworkEvent(NetworkEventTarget.All, ""PlayVfx"");
    }
}", "CeNetVfxManager");

            AnalyzerDiagnostic[] reported = diagnostics.Where(diagnostic => diagnostic.Code == CE0012).ToArray();

            Assert.That(reported.Length, Is.EqualTo(2),
                "Both sends reach a CeNetVfxManager: one with no receiver, one through a reference of this " +
                "same type.\n" + Render(diagnostics));
            Assert.That(reported[0].Severity, Is.EqualTo(CompilerSeverity.Error));
            Assert.That(reported[0].Message, Does.Contain("PlayVfx").And.Contain("VFX render locally"));
        }

        /// <summary>
        /// CENet.Rpc is the send CE recommends for a call the [Rpc] rewrite refuses, and it forwards
        /// straight to SendCustomNetworkEvent -- so leaving it out made [LocalOnly] enforceable only
        /// against the spelling authors were being steered away from.
        /// </summary>
        [Test]
        public void CE0012_CENetRpc_IsCheckedLikeSendCustomNetworkEvent()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = RunAnalyzer(LocalOnlyAnalyzerTypeName, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class CeNetExplicitFx : UdonSharpBehaviour
{
    public void PlayVfx() { }
}

public class CeNetExplicitSender : UdonSharpBehaviour
{
    public CeNetExplicitFx fx;

    [LocalOnly]
    private void PlayVfx() { }

    public void Bad()
    {
        CENet.Rpc(this, RpcTarget.All, nameof(PlayVfx));
    }

    public void Fine()
    {
        CENet.Rpc(fx, RpcTarget.All, ""PlayVfx"");
    }
}", "CeNetExplicitSender");

            AnalyzerDiagnostic[] reported = diagnostics.Where(diagnostic => diagnostic.Code == CE0012).ToArray();

            Assert.That(reported.Length, Is.EqualTo(1),
                "The send to 'this' is this type's [LocalOnly] method; the send to 'fx' is not.\n" +
                Render(diagnostics));
            Assert.That(reported[0].Message, Does.Contain("PlayVfx").And.Contain("CENet.Rpc"));
        }

        // ------------------------------------------------------------------------------------------
        // ConflictResolver: arrays of different lengths
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The shorter array used to be padded with zeros and the padding resolved like data, which
        /// invented values neither side ever held.
        /// </summary>
        [Test]
        public void ConflictResolver_ArraysOfDifferentLengths_CarryOneSidedElementsThrough()
        {
            Assert.That(
                ConflictResolver.ResolveIntArray(new[] { 31, 45 }, new[] { 30 }, MergeStrategy.LowerWins),
                Is.EqualTo(new[] { 30, 45 }),
                "LowerWins must not turn the element only one side has into a fabricated 0.");

            Assert.That(
                ConflictResolver.ResolveIntArray(new[] { -1 }, new[] { -5, -3 }, MergeStrategy.HigherWins),
                Is.EqualTo(new[] { -1, -3 }),
                "HigherWins must not let padding beat a real negative value.");

            Assert.That(
                ConflictResolver.ResolveIntArray(new[] { 1, 2, 3 }, new[] { 10 }, MergeStrategy.Additive),
                Is.EqualTo(new[] { 11, 2, 3 }));

            Assert.That(
                ConflictResolver.ResolveFloatArray(new[] { 2f, 9f }, new[] { 1f }, MergeStrategy.LowerWins),
                Is.EqualTo(new[] { 1f, 9f }));

            Assert.That(
                ConflictResolver.ResolveBoolArray(new[] { true, true }, new[] { true }, MergeStrategy.LowerWins),
                Is.EqualTo(new[] { true, true }),
                "LowerWins must not clear a flag the shorter array never had an opinion about.");

            // Where both sides do have the element, the merge is what it always was.
            Assert.That(
                ConflictResolver.ResolveIntArray(new[] { 1, 9 }, new[] { 5, 2 }, MergeStrategy.HigherWins),
                Is.EqualTo(new[] { 5, 9 }));
            Assert.That(
                ConflictResolver.ResolveBoolArray(new[] { true, false }, new[] { false, false }, MergeStrategy.HigherWins),
                Is.EqualTo(new[] { true, false }));
        }

        /// <summary>
        /// A strategy that picks a whole value picks a whole array. LastWriteWins used to return neither
        /// array, but the remote one stretched to the local length and filled out with zeros.
        /// </summary>
        [Test]
        public void ConflictResolver_StrategiesThatDoNotMerge_ReturnTheRemoteArrayItself()
        {
            int[] localInts = { 1, 2, 3 };
            int[] remoteInts = { 9 };

            foreach (MergeStrategy strategy in new[]
                     {
                         MergeStrategy.LastWriteWins, MergeStrategy.OwnerWins, MergeStrategy.MasterWins,
                         MergeStrategy.Custom
                     })
            {
                Assert.That(ConflictResolver.ResolveIntArray(localInts, remoteInts, strategy), Is.SameAs(remoteInts),
                    $"{strategy} resolves a single int to the remote value, so it resolves an array to the " +
                    "remote array -- as it is, not padded to the local length.");
            }

            float[] remoteFloats = { 9f };
            Assert.That(
                ConflictResolver.ResolveFloatArray(new[] { 1f, 2f }, remoteFloats, MergeStrategy.LastWriteWins),
                Is.SameAs(remoteFloats));

            // ResolveBool has no additive meaning for a flag and answers with the remote value, so for a
            // bool array Additive is one of the strategies that picks rather than merges.
            bool[] remoteBools = { false };
            Assert.That(
                ConflictResolver.ResolveBoolArray(new[] { true, true }, remoteBools, MergeStrategy.Additive),
                Is.SameAs(remoteBools));
        }

        // ------------------------------------------------------------------------------------------
        // Generator harness
        // ------------------------------------------------------------------------------------------

        private const string CeNetSourceDirectory = "Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Net";

        private const string FixturePathPrefix = "CeNetFixture";

        private static readonly CSharpParseOptions FixtureParseOptions =
            CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest);

        /// <summary>The CE.Net runtime sources as trees, the shape a real compile hands the generators.</summary>
        private static IEnumerable<SyntaxTree> ParseCeNetSources()
        {
            string[] runtimeSources = Directory.GetFiles(CeNetSourceDirectory, "*.cs");

            Assert.That(runtimeSources, Is.Not.Empty,
                $"No CE.Net runtime sources under '{CeNetSourceDirectory}', so [Rpc] and [Sync] cannot bind " +
                "and these tests would only exercise the generators' unbound fallback.");

            return runtimeSources
                .Select(path => CSharpSyntaxTree.ParseText(File.ReadAllText(path), FixtureParseOptions, path))
                .ToArray();
        }

        /// <summary>
        /// Compiles what the generator emitted, together with the CE.Net runtime sources and against the
        /// compiler's own reference set, and fails on any error that points into a fixture.
        /// </summary>
        /// <remarks>
        /// Text assertions pin what a rewrite looks like; this pins that it is C# at all. Several of the
        /// failures these tests exist for were only ever visible here: a second [NetworkCallable] beside
        /// one already written (CS0579), an owner guard whose bare 'gameObject' met a local of the same
        /// name (CS0841), an injected sync mode on two parts of one partial class (CS0579), and a
        /// target-typed 'new(1, 2, 3)' forwarded into an 'object' slot (CS1729). In a real compile each of
        /// them breaks the whole project, reported against generated code the author never wrote.
        /// </remarks>
        private static void AssertOutputCompiles(GeneratorRun run)
        {
            AssertRan(run);

            var trees = new List<SyntaxTree>();

            for (int i = 0; i < run.Output.Length; i++)
                trees.Add(CSharpSyntaxTree.ParseText(run.Output[i], FixtureParseOptions, FixturePathPrefix + i + ".cs"));

            trees.AddRange(ParseCeNetSources());

            CSharpCompilation compilation = CSharpCompilation.Create(
                "CENetCodegenOutput." + Guid.NewGuid().ToString("N"),
                trees,
                CompilationContext.GetMetadataReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            string[] errors = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == RoslynSeverity.Error)
                .Where(diagnostic => diagnostic.Location.SourceTree != null &&
                                     diagnostic.Location.SourceTree.FilePath.StartsWith(FixturePathPrefix, StringComparison.Ordinal))
                .Select(diagnostic => diagnostic.ToString())
                .ToArray();

            Assert.That(errors, Is.Empty,
                "The generated code does not compile, so a real compile of this project would fail on it:\n" +
                string.Join("\n", errors) + "\n\n" + string.Join("\n\n", run.Output));
        }

        /// <summary>What one generator did with a set of fixture files.</summary>
        private sealed class GeneratorRun
        {
            /// <summary>The rewritten source of each fixture, in the order the fixtures were passed.</summary>
            public string[] Output;

            /// <summary>Whether the generator returned a different tree for each fixture.</summary>
            public bool[] TreeChanged;

            /// <summary>What the generator threw, unwrapped from the reflection call, or null.</summary>
            public Exception Failure;

            /// <summary>How many times the generator reported a recognised construct it left alone.</summary>
            public int ObligationsDischarged;
        }

        /// <summary>
        /// Runs one CE generator over the fixtures, in one compilation that also holds the CE.Net runtime
        /// sources -- see the class comment for why those have to be trees rather than a reference.
        /// </summary>
        private static GeneratorRun RunGenerator(string generatorTypeName, params string[] fixtures)
        {
            Assembly ceEditorAssembly = GetAssemblyByName("UdonSharp.CE.Editor");

            Type generatorType = ceEditorAssembly.GetType(generatorTypeName, throwOnError: false);
            Assert.That(generatorType, Is.Not.Null,
                $"Generator type '{generatorTypeName}' does not exist in UdonSharp.CE.Editor.");

            Type contextType = ceEditorAssembly.GetType(
                "UdonSharp.CE.Editor.Optimizers.OptimizationContext", throwOnError: true);

            object generator = Activator.CreateInstance(generatorType, nonPublic: true);
            object context = Activator.CreateInstance(contextType, nonPublic: true);

            var trees = new List<SyntaxTree>();

            for (int i = 0; i < fixtures.Length; i++)
                trees.Add(CSharpSyntaxTree.ParseText(fixtures[i], FixtureParseOptions, FixturePathPrefix + i + ".cs"));

            trees.AddRange(ParseCeNetSources());

            MethodInfo setTrees = contextType.GetMethod(
                "SetSemanticModelTrees",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(setTrees, Is.Not.Null,
                "OptimizationContext.SetSemanticModelTrees is gone; CE transforms cannot be given a semantic model.");

            setTrees.Invoke(context, new object[] { trees.ToArray() });

            MethodInfo optimize = generatorType.GetMethod(
                "Optimize",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var run = new GeneratorRun
            {
                Output = new string[fixtures.Length],
                TreeChanged = new bool[fixtures.Length]
            };

            try
            {
                for (int i = 0; i < fixtures.Length; i++)
                {
                    SyntaxTree output = (SyntaxTree)optimize.Invoke(generator, new object[] { trees[i], context });

                    Assert.That(output, Is.Not.Null, "A generator must return the input tree, never null.");

                    run.TreeChanged[i] = !ReferenceEquals(output, trees[i]);
                    run.Output[i] = output.GetText().ToString();
                }
            }
            catch (TargetInvocationException exception)
            {
                run.Failure = exception.InnerException ?? exception;
            }

            MethodInfo dischargedCount = contextType.GetMethod(
                "GetDischargedObligationCount",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(dischargedCount, Is.Not.Null,
                "OptimizationContext.GetDischargedObligationCount is gone, so a generator's legitimate " +
                "no-op can no longer be told apart from one that silently stopped emitting.");

            run.ObligationsDischarged = (int)dischargedCount.Invoke(context, new object[] { RpcEmitterId });

            return run;
        }

        private static void AssertRan(GeneratorRun run)
        {
            Assert.That(run.Failure, Is.Null,
                "The generator threw on a fixture it should accept:\n" + run.Failure);
        }

        /// <summary>
        /// Asserts the generator refused the fixture with a CodeGenerationException carrying this id. The
        /// exception type is internal to UdonSharp.Editor, so it is read by name and property.
        /// </summary>
        private static void AssertFailedWith(GeneratorRun run, string expectedId)
        {
            Assert.That(run.Failure, Is.Not.Null,
                $"Expected the generator to refuse this fixture with {expectedId}, and it accepted it:\n" +
                string.Join("\n", run.Output.Where(output => output != null)));

            Assert.That(run.Failure.GetType().Name, Is.EqualTo("CodeGenerationException"),
                "A generator must refuse a declaration with a CodeGenerationException, which carries a " +
                "location. Anything else reaches the author as 'Unknown File(1,1)'.\n" + run.Failure);

            object id = run.Failure.GetType().GetProperty("GeneratorId")?.GetValue(run.Failure);

            Assert.That(id, Is.EqualTo(expectedId), run.Failure.Message);
        }

        /// <summary>
        /// The mode the emitter injected in front of a class declaration, or null when it injected none.
        /// The attribute is written on the same line as the declaration it applies to.
        /// </summary>
        private static string ModeInjectedOn(string output, string className)
        {
            Match match = Regex.Match(
                output,
                @"UdonBehaviourSyncMode\(global::UdonSharp\.BehaviourSyncMode\.(\w+)\)\][^\r\n]*\bclass " +
                Regex.Escape(className) + @"\b");

            return match.Success ? match.Groups[1].Value : null;
        }

        // ------------------------------------------------------------------------------------------
        // Tripwire harness
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Asks the compiler's own tripwire scan whether a source file uses a trigger token. The scan is a
        /// private helper of UdonSharpCompilerV1, reached by name so its absence fails these tests with a
        /// message rather than failing the test assembly's compile.
        /// </summary>
        private static bool DeclaresTrigger(string source, string trigger)
        {
            MethodInfo sourceDeclaresTrigger = typeof(UdonSharpCompilerV1).GetMethod(
                "SourceDeclaresTrigger",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(sourceDeclaresTrigger, Is.Not.Null,
                "UdonSharpCompilerV1.SourceDeclaresTrigger is gone. Find where the USCE0001 tripwire now " +
                "decides whether a module uses a trigger token.");

            var binding = new ModuleBinding
            {
                tree = CSharpSyntaxTree.ParseText(source),
                filePath = "CeNetTripwireFixture.cs",
                sourceText = source
            };

            return (bool)sourceDeclaresTrigger.Invoke(null, new object[] { binding, trigger });
        }

        /// <summary>
        /// The trigger tokens a generator actually registers, read from CEGeneratorRunner's own tripwire
        /// list rather than restated here, so a token dropped or renamed at the entry is what gets tested.
        /// </summary>
        private static IReadOnlyList<string> TriggerTokensOf(string generatorId)
        {
            MethodInfo buildTripwires = GetAssemblyByName("UdonSharp.CE.Editor")
                .GetType("UdonSharp.CE.Editor.CodeGen.CEGeneratorRunner", throwOnError: true)
                .GetMethod("BuildTripwires", BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(buildTripwires, Is.Not.Null,
                "CEGeneratorRunner.BuildTripwires is gone. Find where the generator tripwires are now declared.");

            var tripwires = (IEnumerable<SourceGeneratorTripwire>)buildTripwires.Invoke(null, null);

            SourceGeneratorTripwire tripwire = tripwires
                .FirstOrDefault(candidate => candidate.GeneratorId == generatorId);

            Assert.That(tripwire, Is.Not.Null, $"No tripwire is declared for {generatorId}.");

            return tripwire.TriggerTokens;
        }

        /// <summary>Whether the tripwire would count this source as a use of any of these tokens.</summary>
        private static bool DeclaredByAnyToken(IReadOnlyList<string> tokens, string source)
        {
            return tokens.Any(token => DeclaresTrigger(source, token));
        }

        // ------------------------------------------------------------------------------------------
        // Analyzer harness
        // ------------------------------------------------------------------------------------------

        private sealed class BoundFixture
        {
            public TypeSymbol Type;
            public BindContext BindContext;
            public CompilationContext CompilationContext;
        }

        internal static IReadOnlyList<AnalyzerDiagnostic> RunAnalyzer(
            string analyzerTypeName,
            string source,
            string behaviourName)
        {
            Type analyzerType = GetAssemblyByName("UdonSharp.CE.Editor")
                .GetType(analyzerTypeName, throwOnError: false);

            Assert.That(analyzerType, Is.Not.Null,
                $"Analyzer type '{analyzerTypeName}' does not exist in UdonSharp.CE.Editor, so the check it " +
                "owns cannot run.");

            var analyzer = (ICompileTimeAnalyzer)Activator.CreateInstance(analyzerType, nonPublic: true);
            BoundFixture fixture = BindFixture(source, behaviourName);

            IEnumerable<AnalyzerDiagnostic> diagnostics =
                analyzer.Analyze(fixture.Type, fixture.BindContext, fixture.CompilationContext);

            return (diagnostics ?? Enumerable.Empty<AnalyzerDiagnostic>())
                .Where(diagnostic => diagnostic != null)
                .ToList();
        }

        private static readonly FieldInfo UdonSharpAssembliesField = typeof(CompilerUdonInterface)
            .GetField("_udonSharpAssemblies", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>The assembly set as it was before any fixture was appended to it.</summary>
        private static ImmutableArray<Assembly>? _pristineUdonSharpAssemblies;

        /// <summary>
        /// Drops every fixture assembly the test that just ran registered, so one test's fixture types can
        /// never answer for another's.
        /// </summary>
        [TearDown]
        public void UnregisterFixtureAssemblies()
        {
            if (_pristineUdonSharpAssemblies.HasValue && UdonSharpAssembliesField != null)
                UdonSharpAssembliesField.SetValue(null, _pristineUdonSharpAssemblies.Value);
        }

        /// <summary>
        /// Runs the compiler's real Bind phase over one fixture in its own compilation. This is
        /// CESyncAuthorityTests.BindFixture, and the reasoning for each step is written out there: the
        /// fixture has to be emitted and registered as a real assembly before the binder will treat a
        /// class parsed from a string as an UdonSharpBehaviour at all.
        /// </summary>
        private static BoundFixture BindFixture(string source, string behaviourName)
        {
            CompilerUdonInterface.CacheInit();

            SyntaxTree tree = CSharpSyntaxTree.ParseText(
                source,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest),
                "CeNetAnalyzerFixture.cs");

            var trees = new List<SyntaxTree> { tree };
            if (source.Contains("CENet.Rpc("))
            {
                // A real U# compile sees CE.Net as source. Referencing only its DLL makes its
                // static methods look like SDK externs, so the binder correctly refuses the call.
                foreach (string file in new[] { "CENet.cs", "RpcTarget.cs" })
                {
                    string path = Path.Combine(CeNetSourceDirectory, file);
                    trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(path), (CSharpParseOptions)tree.Options, path));
                }
            }

            CSharpCompilation compilation = CSharpCompilation.Create(
                "CENetCodegenFixture." + Guid.NewGuid().ToString("N"),
                trees,
                FixtureReferences(),
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            string[] errors = compilation.GetDiagnostics()
                .Where(diagnostic => diagnostic.Severity == RoslynSeverity.Error)
                .Select(diagnostic => diagnostic.Id + ": " + diagnostic.GetMessage())
                .ToArray();

            Assert.That(errors, Is.Empty,
                $"Fixture '{behaviourName}' does not compile, so nothing below it is testing an analyzer. " +
                "Errors:\n" + string.Join("\n", errors));

            RegisterFixtureAssembly(compilation, behaviourName);

            SemanticModel model = compilation.GetSemanticModel(tree);

            INamedTypeSymbol behaviourSymbol = tree.GetRoot()
                .DescendantNodes()
                .OfType<ClassDeclarationSyntax>()
                .Where(declaration => declaration.Identifier.Text == behaviourName)
                .Select(declaration => model.GetDeclaredSymbol(declaration))
                .FirstOrDefault();

            Assert.That(behaviourSymbol, Is.Not.Null,
                $"Fixture source declares no class named '{behaviourName}'.");

            var compilationContext = new CompilationContext(new UdonSharpCompileOptions { IsEditorBuild = true })
            {
                RoslynCompilation = compilation
            };

            var bindContext = new BindContext(compilationContext, behaviourSymbol, Array.Empty<Symbol>());

            try
            {
                bindContext.Bind();
            }
            catch (Exception exception)
            {
                Assert.Fail(
                    $"Binding fixture '{behaviourName}' threw, so the analyzer never ran. Read this as a " +
                    "harness or fixture problem, not as the check under test passing or failing.\n" +
                    exception);
            }

            string[] bindErrors = compilationContext.Diagnostics
                .Where(diagnostic => diagnostic.Severity == CompilerSeverity.Error)
                .Select(diagnostic => diagnostic.Message)
                .ToArray();

            Assert.That(bindErrors, Is.Empty,
                $"Binding fixture '{behaviourName}' produced compiler errors:\n" +
                string.Join("\n", bindErrors));

            return new BoundFixture
            {
                Type = bindContext.GetTypeSymbol(behaviourSymbol),
                BindContext = bindContext,
                CompilationContext = compilationContext
            };
        }

        private static void RegisterFixtureAssembly(CSharpCompilation compilation, string behaviourName)
        {
            Assert.That(UdonSharpAssembliesField, Is.Not.Null,
                "CompilerUdonInterface._udonSharpAssemblies is gone, so fixtures can no longer be registered " +
                "and every analyzer test in this file would fail to bind.");

            // Read through the property first: its getter runs AssemblyCacheInit, so this is the populated
            // set. Writing the field before init would be silently overwritten by the first later read.
            ImmutableArray<Assembly> current = CompilerUdonInterface.UdonSharpAssemblies;

            if (!_pristineUdonSharpAssemblies.HasValue)
                _pristineUdonSharpAssemblies = current;

            byte[] image;

            using (var stream = new MemoryStream())
            {
                EmitResult emitResult = compilation.Emit(stream);

                Assert.That(emitResult.Success, Is.True,
                    $"Fixture '{behaviourName}' compiles but does not emit, so it cannot be registered.");

                image = stream.ToArray();
            }

            UdonSharpAssembliesField.SetValue(null, current.Add(Assembly.Load(image)));
        }

        private static IReadOnlyList<MetadataReference> _fixtureReferences;

        /// <summary>
        /// The compiler's own reference set, plus the U# source assemblies it deliberately leaves out. A
        /// fixture-only compilation has none of their source, so without these [Rpc], [Sync] and
        /// [LocalOnly] would not resolve and every analyzer test would fail as CS0246.
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

            foreach (string assemblyName in new[] { "UdonSharp.Runtime", "UdonSharp.CE.Runtime", "UdonSharp.Lib" })
            {
                Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(candidate => candidate.GetName().Name == assemblyName);

                if (assembly == null || assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location))
                    continue;

                if (!locations.Add(assembly.Location))
                    continue;

                references.Add(MetadataReference.CreateFromFile(assembly.Location));
            }

            _fixtureReferences = references;
            return _fixtureReferences;
        }

        private static string Render(IReadOnlyList<AnalyzerDiagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
                return "  (none)";

            return string.Join(
                "\n",
                diagnostics.Select(diagnostic => $"  [{diagnostic.Severity}] {diagnostic.GetFormattedMessage()}"));
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
