using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Editor.Net;
using UdonSharp.Compiler;
using UdonSharpEditor;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using VRC.Udon.Common;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Regression tests for the [SyncOnJoin] glue emitter and the [Sync(Quantize)] emitter, run as .NET:
    /// the real emitter over source strings, its output compiled with Roslyn and exercised on live
    /// components.
    /// </summary>
    /// <remarks>
    /// The emitters resolve [SyncOnJoin] and [Sync] through the semantic model, and UdonSharp compiles the CE
    /// runtime from source -- so its CE.Net files are trees in the pass, never a reference. Every transform
    /// here is given those files as trees too, so the attributes bind the way they do in a real compile
    /// rather than through the emitters' unbound fallback.
    ///
    /// The shapes UdonSharp itself rejects although plain C# accepts them live in
    /// <see cref="CESyncOnJoinUdonRegressionTests"/>, which compiles real fixtures as Udon.
    /// </remarks>
    public class CESyncOnJoinGlueRegressionTests
    {
        private const string GlueEmitter = "UdonSharp.CE.Editor.Optimizers.SyncOnJoinGlueEmitter";
        private const string QuantizeEmitter = "UdonSharp.CE.Editor.Optimizers.QuantizePreSerializeEmitter";
        private const string GlueEmitterId = "CEGEN001";
        private const string CeNetSourceDirectory = "Packages/com.merlin.UdonSharp/Runtime/Libraries/CE/Net";

        private readonly List<UnityEngine.Object> _created = new List<UnityEngine.Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (UnityEngine.Object created in _created)
            {
                if (created != null)
                    UnityEngine.Object.DestroyImmediate(created);
            }

            _created.Clear();
        }

        // ------------------------------------------------------------------------------------------
        // An early return in a handler the author wrote
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The capture used to be appended after the author's last statement only, so an early return left
        /// around it. 'if (player == null) return;' -- or the more usual 'if (!player.isLocal) return;' --
        /// meant the owner captured at its own join and never again, and every later joiner got the
        /// world-load snapshot.
        /// </summary>
        [Test]
        public void AuthorsHandler_LeftThroughAnEarlyReturn_StillCapturesAndSerializes()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;
using VRC.SDKBase;

public class SojEarlyReturnFixture : UdonSharpBehaviour
{
    [SyncOnJoin] public int phase;
    public bool skip;

    public override void OnPlayerJoined(VRCPlayerApi player)
    {
        if (player == null)
            return;
    }

    public override void OnPreSerialization()
    {
        if (skip)
            return;
    }
}

public class SojEarlyReturnProbe : SojEarlyReturnFixture
{
    public int captures;

    public override void _CE_SyncOnJoin_RequestCapture()
    {
        captures++;
    }
}");

            Component probe = Spawn(assembly.GetType("SojEarlyReturnProbe"));

            Call(probe, "OnPlayerJoined", new object[] { null });

            Assert.That(Get(probe, "captures"), Is.EqualTo(1),
                "OnPlayerJoined left through its early return without capturing.");

            Set(probe, "phase", 7);
            Set(probe, "skip", true);
            Call(probe, "OnPreSerialization");

            Assert.That((string)Get(probe, "_CE_SyncOnJoinPayload"), Does.Contain("7"),
                "OnPreSerialization left through its early return without writing the payload, so this " +
                "serialization carried the previous snapshot.");
        }

        // ------------------------------------------------------------------------------------------
        // The owner refreshing itself through its own OnDeserialization
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The applied marker used to be set by decoding alone, so on the owner it never matched the payload
        /// it had just written. 'RequestSerialization(); OnDeserialization();' -- the usual way to refresh
        /// the owner the way remotes are refreshed -- then decoded the previous snapshot over the change.
        /// </summary>
        [Test]
        public void Owner_CallingItsOwnOnDeserialization_DoesNotRevertTheChangeItJustMade()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojOwnerRefreshFixture : UdonSharpBehaviour
{
    [SyncOnJoin] public int phase;
    public int refreshes;

    public void Advance()
    {
        phase++;
        RequestSerialization();
        OnDeserialization();
    }

    public override void OnDeserialization()
    {
        refreshes++;
    }
}");

            Component owner = Spawn(assembly.GetType("SojOwnerRefreshFixture"));

            Set(owner, "phase", 1);
            Call(owner, "_CE_SyncOnJoin_Serialize");
            Call(owner, "Advance");

            Assert.That(Get(owner, "phase"), Is.EqualTo(2),
                "The owner's own handler re-applied the snapshot it had captured before the change.");
            Assert.That(Get(owner, "refreshes"), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------------
        // A derived behaviour with no [SyncOnJoin] field of its own
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Only a behaviour that declared a [SyncOnJoin] field was woven. A derived behaviour that declared
        /// none and overrode OnDeserialization -- without calling base, because nothing in the source says
        /// the base has a handler -- replaced the generated restore, and its instances never restored.
        /// </summary>
        [Test]
        public void DerivedBehaviourWithoutFieldsOfItsOwn_StillRestoresThroughItsHandler()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojInheritedBase : UdonSharpBehaviour
{
    [SyncOnJoin] public int phase;
}

public class SojInheritedRedraw : SojInheritedBase
{
    public int redraws;

    public override void OnDeserialization()
    {
        redraws++;
    }
}");

            Type type = assembly.GetType("SojInheritedRedraw");
            Component owner = Spawn(type);
            Component remote = Spawn(type);

            Set(owner, "phase", 5);
            Call(owner, "_CE_SyncOnJoin_Serialize");
            Set(remote, "_CE_SyncOnJoinPayload", Get(owner, "_CE_SyncOnJoinPayload"));

            Call(remote, "OnDeserialization");

            Assert.That(Get(remote, "phase"), Is.EqualTo(5),
                "The derived behaviour's own OnDeserialization replaced the base's generated restore.");
            Assert.That(Get(remote, "redraws"), Is.EqualTo(1));
        }

        // ------------------------------------------------------------------------------------------
        // Decoded numbers that do not fit their field
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Only the token's type was checked. In Udon '(byte)300.0' is System.Convert.ToByte, which throws
        /// and halts the behaviour for the session; in .NET it silently wraps. Either way the payload was
        /// applied as something the sender never had. Now a value that does not fit rejects the group like
        /// any other malformed entry.
        /// </summary>
        [Test]
        public void NumberThatDoesNotFitItsField_RejectsThePayloadInsteadOfApplyingIt()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojNarrowFixture : UdonSharpBehaviour
{
    [SyncOnJoin] public byte lives;
    [SyncOnJoin] public int score;
}");

            Component remote = Spawn(assembly.GetType("SojNarrowFixture"));
            Set(remote, "lives", (byte)3);
            Set(remote, "score", 1);

            Set(remote, "_CE_SyncOnJoinPayload", "{\"lives\":300,\"score\":9}");
            LogAssert.Expect(LogType.Error, new Regex("could not decode field 'lives'"));
            Call(remote, "_CE_SyncOnJoin_Apply");

            Assert.That(Get(remote, "lives"), Is.EqualTo((byte)3), "An out-of-range byte was applied.");
            Assert.That(Get(remote, "score"), Is.EqualTo(1), "Part of a rejected payload was applied.");

            Set(remote, "_CE_SyncOnJoinPayload", "{\"lives\":200,\"score\":9}");
            Call(remote, "_CE_SyncOnJoin_Apply");

            Assert.That(Get(remote, "lives"), Is.EqualTo((byte)200), "A value that fits must still be applied.");
            Assert.That(Get(remote, "score"), Is.EqualTo(9));
        }

        /// <summary>
        /// A long- or ulong-backed enum was written as '(long)value', a JSON number, and came back as a
        /// double: any flag combination with a bit above 2^53 lost its low bits on the way.
        /// </summary>
        [Test]
        public void WideEnum_RoundTripsEveryBit()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;

public enum SojWideFlags : long { None = 0, Low = 1, High = 1L << 62 }
public enum SojTopFlags : ulong { None = 0, Low = 1, Top = 1UL << 63 }

public class SojWideEnumFixture : UdonSharpBehaviour
{
    [SyncOnJoin] public SojWideFlags wide;
    [SyncOnJoin] public SojTopFlags top;
}");

            Type type = assembly.GetType("SojWideEnumFixture");
            object wide = Enum.ToObject(assembly.GetType("SojWideFlags"), (1L << 62) | 1L);
            object top = Enum.ToObject(assembly.GetType("SojTopFlags"), (1UL << 63) | 1UL);

            Component owner = Spawn(type);
            Component remote = Spawn(type);

            Set(owner, "wide", wide);
            Set(owner, "top", top);
            Call(owner, "_CE_SyncOnJoin_Serialize");

            Set(remote, "_CE_SyncOnJoinPayload", Get(owner, "_CE_SyncOnJoinPayload"));
            Call(remote, "_CE_SyncOnJoin_Apply");

            Assert.That(Get(remote, "wide"), Is.EqualTo(wide), "A long-backed enum lost bits in the payload.");
            Assert.That(Get(remote, "top"), Is.EqualTo(top), "A ulong-backed enum lost bits in the payload.");
        }

        /// <summary>
        /// UdonSharp turns a number back into a UnityEngine, VRChat or System enum through a lookup table of
        /// the enum's values. A negative value, a value past 2048, or a backing type other than int cannot go
        /// through it: the compiler threw, or the cast indexed out of range at runtime. Such enums are now
        /// unsupported for generated glue, which CE0032 reports.
        /// </summary>
        [Test]
        public void ExternEnumsTheLookupTableCannotHold_AreUnsupported()
        {
            CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9);
            MetadataReference corlib = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

            CSharpCompilation library = CSharpCompilation.Create(
                "SojExternEnums." + Guid.NewGuid().ToString("N"),
                new[]
                {
                    CSharpSyntaxTree.ParseText(@"
public enum SojExtNegative { Minus = -1, Zero = 0 }
public enum SojExtHuge { Small = 0, Big = 4096 }
public enum SojExtByte : byte { A, B }
public enum SojExtFine { A = 0, B = 3 }", parseOptions)
                },
                new[] { corlib },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            ImmutableArray<byte> image;

            using (var stream = new MemoryStream())
            {
                EmitResult emitted = library.Emit(stream);
                Assert.That(emitted.Success, Is.True, string.Join("\n", emitted.Diagnostics));
                image = ImmutableArray.Create(stream.ToArray());
            }

            // Referenced as metadata, so the enums have no declaration in source -- which is what makes one
            // an enum UdonSharp does not compile itself.
            CSharpCompilation consumer = CSharpCompilation.Create(
                "SojExternEnumConsumer." + Guid.NewGuid().ToString("N"),
                Array.Empty<SyntaxTree>(),
                new[] { corlib, MetadataReference.CreateFromImage(image) },
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            AssertUnsupported(consumer, "SojExtNegative", "negative");
            AssertUnsupported(consumer, "SojExtHuge", "2048");
            AssertUnsupported(consumer, "SojExtByte", "not backed by int");

            INamedTypeSymbol fine = consumer.GetTypeByMetadataName("SojExtFine");
            Assert.That(SyncOnJoinTypeSupport.TryCreateSpec(fine, out SyncOnJoinTypeSpec spec, out string reason), Is.True, reason);

            // Values up to 3 make UdonSharp build a seven-entry table, so 6 is the last index a decode may use.
            Assert.That(spec.NumberMinLiteral, Is.EqualTo("0d"));
            Assert.That(spec.NumberMaxLiteral, Is.EqualTo("6d"));
        }

        private static void AssertUnsupported(CSharpCompilation compilation, string enumName, string because)
        {
            INamedTypeSymbol symbol = compilation.GetTypeByMetadataName(enumName);
            Assert.That(symbol, Is.Not.Null, enumName);

            bool supported = SyncOnJoinTypeSupport.TryCreateSpec(symbol, out _, out string reason);

            Assert.That(supported, Is.False, $"'{enumName}' cannot be converted back by UdonSharp and must be unsupported.");
            Assert.That(reason, Does.Contain(because));
        }

        // ------------------------------------------------------------------------------------------
        // A payload VRChat will not send
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// An oversized serialization is not delivered and nothing retries it, and nothing used to say so.
        /// The glue now reports a group whose payload alone is over the limit -- once, not on every capture,
        /// and again only after it has come back under and gone over again.
        /// </summary>
        [Test]
        public void OversizedPayload_IsReportedOnceUntilItShrinks()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojOversizeFixture : UdonSharpBehaviour
{
    [SyncOnJoin] public string text;
}");

            Component owner = Spawn(assembly.GetType("SojOversizeFixture"));
            var oversize = new Regex("payload is larger than the 280496 bytes");

            Set(owner, "text", new string('a', 150000));
            LogAssert.Expect(LogType.Error, oversize);
            List<string> logs = CaptureLogs(() =>
            {
                Call(owner, "_CE_SyncOnJoin_Serialize");
                Call(owner, "_CE_SyncOnJoin_Serialize");
            });
            Assert.That(logs.Count(log => oversize.IsMatch(log)), Is.EqualTo(1),
                "An oversized payload has to be reported, and once rather than on every capture.");

            Set(owner, "text", "small");
            logs = CaptureLogs(() => Call(owner, "_CE_SyncOnJoin_Serialize"));
            Assert.That(logs, Is.Empty);

            Set(owner, "text", new string('b', 150000));
            LogAssert.Expect(LogType.Error, oversize);
            logs = CaptureLogs(() => Call(owner, "_CE_SyncOnJoin_Serialize"));
            Assert.That(logs.Count(log => oversize.IsMatch(log)), Is.EqualTo(1),
                "A payload that went back under the limit and over it again has to be reported again.");
        }

        // ------------------------------------------------------------------------------------------
        // The generator tripwire
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// USCE0001 fails the compile when '[SyncOnJoin' is written and the emitter changed nothing. A
        /// behaviour whose only [SyncOnJoin] field needs a [SyncSerializer] is exactly that, correctly -- and
        /// the tripwire failed the compile in the generator phase, before CE0032 could explain it. The
        /// emitter now says where it saw the attribute and deliberately emitted nothing.
        /// </summary>
        [Test]
        public void FieldsTheEmitterDeliberatelyLeavesAlone_AreReportedToTheTripwire()
        {
            const string unsupportedOnly = @"
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

public class SojRejectedOnly : UdonSharpBehaviour
{
    [SyncOnJoin] public GameObject target;
}";

            string[] output = Transform(GlueEmitter, out int discharged, unsupportedOnly);
            Assert.That(output[0], Is.EqualTo(unsupportedOnly), "Nothing should have been generated.");
            Assert.That(discharged, Is.EqualTo(1), "A behaviour whose every field was rejected.");

            Transform(GlueEmitter, out discharged, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojConstOnly : UdonSharpBehaviour
{
    [SyncOnJoin] public const int Version = 2;
}");
            Assert.That(discharged, Is.EqualTo(1), "A behaviour whose only field is const.");

            Transform(GlueEmitter, out discharged, @"
using UdonSharp.CE.Net;

public class SojPlainModel
{
    [SyncOnJoin] public int x;
}

public struct SojPlainStruct
{
    [SyncOnJoin] public int y;
}");
            Assert.That(discharged, Is.EqualTo(2), "A plain class and a struct cannot carry the payload.");

            Transform(GlueEmitter, out discharged, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojOrdinaryCarrier : UdonSharpBehaviour
{
    [SyncOnJoin] public int x;
}");
            Assert.That(discharged, Is.EqualTo(0),
                "A behaviour that gets glue must not also report a no-op, or the tripwire goes blind.");
        }

        // ------------------------------------------------------------------------------------------
        // long text and the client's culture
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// long and ulong ride as text, and both halves used the culture of the client they ran on. The
        /// negative sign is culture data, so text one client wrote could fail to parse on another and reject
        /// the whole group. Both halves are invariant now.
        /// </summary>
        [Test]
        public void LongText_DoesNotDependOnTheClientsCulture()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojCultureFixture : UdonSharpBehaviour
{
    [SyncOnJoin] public long balance;
    [SyncOnJoin] public ulong total;
}");

            Type type = assembly.GetType("SojCultureFixture");
            CultureInfo original = Thread.CurrentThread.CurrentCulture;

            var writerCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            writerCulture.NumberFormat.NegativeSign = "~";

            var readerCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            readerCulture.NumberFormat.NegativeSign = "_";

            try
            {
                Thread.CurrentThread.CurrentCulture = writerCulture;

                Component owner = Spawn(type);
                Set(owner, "balance", -42L);
                Set(owner, "total", 7UL);
                Call(owner, "_CE_SyncOnJoin_Serialize");

                string payload = (string)Get(owner, "_CE_SyncOnJoinPayload");
                Assert.That(payload, Does.Contain("\"-42\""), "The payload was written in the owner's culture: " + payload);

                Thread.CurrentThread.CurrentCulture = readerCulture;

                Component remote = Spawn(type);
                Set(remote, "_CE_SyncOnJoinPayload", payload);
                Call(remote, "_CE_SyncOnJoin_Apply");

                Assert.That(Get(remote, "balance"), Is.EqualTo(-42L));
                Assert.That(Get(remote, "total"), Is.EqualTo(7UL));
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = original;
            }
        }

        // ------------------------------------------------------------------------------------------
        // A payload that arrived before Start
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The restore runs in Start, ahead of the author's own Start body, and a behaviour with no Start of
        /// its own gets one. A base behaviour cannot get one when a behaviour derived from it declares Start
        /// -- UdonSharp would reject the pair -- and a sibling of that derived behaviour must not be left
        /// without one because of it.
        /// </summary>
        [Test]
        public void PayloadInPlaceBeforeStart_IsRestoredWhenStartRuns_AcrossAHierarchy()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojDoor : UdonSharpBehaviour
{
    [SyncOnJoin] public bool open;
}

public class SojSlidingDoor : SojDoor
{
    public bool openAtStart;

    void Start()
    {
        openAtStart = open;
    }
}

public class SojSwingDoor : SojDoor
{
}");

            const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            Assert.That(assembly.GetType("SojDoor").GetMethod("Start", Declared), Is.Null,
                "SojSlidingDoor declares Start, so a Start on the base would be a hidden base method to UdonSharp.");
            Assert.That(assembly.GetType("SojSwingDoor").GetMethod("Start", Declared), Is.Not.Null,
                "The sibling of a behaviour with its own Start was left without one.");

            Component sliding = Spawn(assembly.GetType("SojSlidingDoor"));
            Set(sliding, "_CE_SyncOnJoinPayload", "{\"open\":true}");
            Call(sliding, "Start");

            Assert.That(Get(sliding, "open"), Is.True);
            Assert.That(Get(sliding, "openAtStart"), Is.True, "The author's Start ran before the restore.");

            Component swing = Spawn(assembly.GetType("SojSwingDoor"));
            Set(swing, "_CE_SyncOnJoinPayload", "{\"open\":true}");
            Call(swing, "Start");

            Assert.That(Get(swing, "open"), Is.True);

            // The owner at world load: nothing captured yet, so Start must do nothing and say nothing.
            Component fresh = Spawn(assembly.GetType("SojSwingDoor"));
            List<string> logs = CaptureLogs(() => Call(fresh, "Start"));

            Assert.That(logs, Is.Empty, "Start with nothing to restore logged.");
            Assert.That(Get(fresh, "open"), Is.False);
        }

        // ------------------------------------------------------------------------------------------
        // Smaller shapes
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// A symbol's name carries no '@', so a field declared '@event' was emitted as the keyword 'event',
        /// which does not parse -- in both emitters.
        /// </summary>
        [Test]
        public void KeywordNamedFields_AreEscapedInBothEmitters()
        {
            string glued = Transform(GlueEmitter, out _, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojKeywordFixture : UdonSharpBehaviour
{
    [SyncOnJoin] public int @event;
    [Sync(Authority.Owner, Quantize = 0.5f)] public float @fixed;
}")[0];

            string quantized = Transform(QuantizeEmitter, out _, glued)[0];
            Assembly assembly = CompileAssembly(quantized);

            Type type = assembly.GetType("SojKeywordFixture");
            Component owner = Spawn(type);
            Component remote = Spawn(type);

            Set(owner, "event", 4);
            Set(owner, "fixed", 0.7f);
            Call(owner, "OnPreSerialization");

            Assert.That(Get(owner, "fixed"), Is.EqualTo(0.5f));

            Set(remote, "_CE_SyncOnJoinPayload", Get(owner, "_CE_SyncOnJoinPayload"));
            Call(remote, "_CE_SyncOnJoin_Apply");

            Assert.That(Get(remote, "event"), Is.EqualTo(4));
        }

        /// <summary>
        /// A private [SyncOnJoin] field on a base used to fail the compile of every derived behaviour, even
        /// one whose own fields ride in a separate Group and one with no [SyncOnJoin] field at all. Only a
        /// derived behaviour that adds to the private field's own group has to re-emit code that reads it.
        /// </summary>
        [Test]
        public void PrivateBaseField_OnlyFailsADerivedBehaviourThatSharesItsGroup()
        {
            Assembly assembly = Build(GlueEmitter, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojPrivateBase : UdonSharpBehaviour
{
    [SyncOnJoin] private int secret;

    public int Secret { get { return secret; } }
    public void SetSecret(int value) { secret = value; }
}

public class SojPrivateOwnGroup : SojPrivateBase
{
    [SyncOnJoin(Group = ""mine"")] public int mine;
}

public class SojPrivateListener : SojPrivateBase
{
    public int heard;

    public override void OnDeserialization()
    {
        heard++;
    }
}");

            Type listener = assembly.GetType("SojPrivateListener");
            Component owner = Spawn(listener);
            Component remote = Spawn(listener);

            Call(owner, "SetSecret", 6);
            Call(owner, "_CE_SyncOnJoin_Serialize");
            Set(remote, "_CE_SyncOnJoinPayload", Get(owner, "_CE_SyncOnJoinPayload"));
            Call(remote, "OnDeserialization");

            Assert.That(listener.GetProperty("Secret").GetValue(remote), Is.EqualTo(6));
            Assert.That(Get(remote, "heard"), Is.EqualTo(1));

            CodeGenerationException sameGroup = Assert.Throws<CodeGenerationException>(() => Transform(GlueEmitter, out _, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojPrivateBase2 : UdonSharpBehaviour
{
    [SyncOnJoin] private int secret;
}

public class SojPrivateSameGroup : SojPrivateBase2
{
    [SyncOnJoin] public int alsoDefault;
}"));

            Assert.That(sameGroup.Message, Does.Contain("inherits the private"));
        }

        /// <summary>
        /// Model helpers are generated onto every qualifying [Serializable] class, whether anything syncs it
        /// or not, and a derived model's helper read its base's private [SerializeField] fields: CS0122 in
        /// generated code, which failed every program in the project over two classes nothing used. Such a
        /// model is now unsupported for generated glue instead, and its base still round-trips its own
        /// private field.
        /// </summary>
        [Test]
        public void DerivedModelOverAPrivateBaseField_DoesNotBreakTheCompile()
        {
            Assembly assembly = Build(GlueEmitter, @"
using System;
using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;

[Serializable]
public class SojModelBase
{
    [SerializeField] private int hidden;
    public int shown;

    public int Hidden { get { return hidden; } }
    public void SetHidden(int value) { hidden = value; }
}

[Serializable]
public class SojModelDerived : SojModelBase
{
    public int extra;
}

public class SojModelCarrier : UdonSharpBehaviour
{
    [SyncOnJoin] public SojModelBase model;
}");

            Type carrier = assembly.GetType("SojModelCarrier");
            Type modelType = assembly.GetType("SojModelBase");

            object model = Activator.CreateInstance(modelType);
            modelType.GetMethod("SetHidden").Invoke(model, new object[] { 8 });
            modelType.GetField("shown").SetValue(model, 2);

            Component owner = Spawn(carrier);
            Component remote = Spawn(carrier);

            Set(owner, "model", model);
            Call(owner, "_CE_SyncOnJoin_Serialize");
            Set(remote, "_CE_SyncOnJoinPayload", Get(owner, "_CE_SyncOnJoinPayload"));
            Call(remote, "_CE_SyncOnJoin_Apply");

            object restored = Get(remote, "model");
            Assert.That(restored, Is.Not.Null);
            Assert.That(modelType.GetProperty("Hidden").GetValue(restored), Is.EqualTo(8));
            Assert.That(modelType.GetField("shown").GetValue(restored), Is.EqualTo(2));
        }

        /// <summary>
        /// A field the author declared under the generated payload's name used to be taken over as the
        /// transport whatever it was -- not [UdonSynced], say -- and the state in it never reached anyone.
        /// </summary>
        [Test]
        public void FieldDeclaredUnderTheGeneratedPayloadName_IsRefused()
        {
            CodeGenerationException refused = Assert.Throws<CodeGenerationException>(() => Transform(GlueEmitter, out _, @"
using UdonSharp;
using UdonSharp.CE.Net;

public class SojReservedName : UdonSharpBehaviour
{
    [SyncOnJoin] public int x;
    public string _CE_SyncOnJoinPayload;
}"));

            Assert.That(refused.Message, Does.Contain("_CE_SyncOnJoinPayload").And.Contain("reserves"));
        }

        // ------------------------------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------------------------------

        /// <summary>Runs one emitter over the fixture and compiles what it produced.</summary>
        private static Assembly Build(string emitterTypeName, string source)
        {
            return CompileAssembly(Transform(emitterTypeName, out _, source)[0]);
        }

        /// <summary>
        /// Runs one emitter over the fixtures, in one compilation that also holds the CE.Net runtime sources,
        /// and returns the rewritten fixtures. What the emitter throws is rethrown as itself.
        /// </summary>
        private static string[] Transform(string emitterTypeName, out int obligationsDischarged, params string[] fixtures)
        {
            Assembly ceEditor = AppDomain.CurrentDomain.GetAssemblies()
                .Single(assembly => assembly.GetName().Name == "UdonSharp.CE.Editor");

            Type emitterType = ceEditor.GetType(emitterTypeName, throwOnError: true);
            Type contextType = ceEditor.GetType("UdonSharp.CE.Editor.Optimizers.OptimizationContext", throwOnError: true);

            object emitter = Activator.CreateInstance(emitterType, nonPublic: true);
            object context = Activator.CreateInstance(contextType, nonPublic: true);

            CSharpParseOptions parseOptions = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9);

            var trees = new List<SyntaxTree>();

            for (int i = 0; i < fixtures.Length; i++)
                trees.Add(CSharpSyntaxTree.ParseText(fixtures[i], parseOptions, "SojFixture" + i + ".cs"));

            foreach (string path in Directory.GetFiles(CeNetSourceDirectory, "*.cs"))
                trees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(path), parseOptions, path));

            contextType.GetMethod("SetSemanticModelTrees").Invoke(context, new object[] { trees.ToArray() });

            MethodInfo optimize = emitterType.GetMethod(
                "Optimize",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            var output = new string[fixtures.Length];

            for (int i = 0; i < fixtures.Length; i++)
            {
                try
                {
                    output[i] = ((SyntaxTree)optimize.Invoke(emitter, new object[] { trees[i], context })).GetText().ToString();
                }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            }

            obligationsDischarged = (int)contextType.GetMethod("GetDischargedObligationCount")
                .Invoke(context, new object[] { GlueEmitterId });

            return output;
        }

        private static Assembly CompileAssembly(string source)
        {
            SyntaxTree tree = CSharpSyntaxTree.ParseText(
                source,
                CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.CSharp9));

            MetadataReference[] references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(assembly => !assembly.IsDynamic && !string.IsNullOrEmpty(assembly.Location))
                .Select(assembly => assembly.Location)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
                .ToArray();

            CSharpCompilation compilation = CSharpCompilation.Create(
                "CESyncOnJoinRegression." + Guid.NewGuid().ToString("N"),
                new[] { tree },
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            using (var stream = new MemoryStream())
            {
                EmitResult emitted = compilation.Emit(stream);

                if (!emitted.Success)
                {
                    Assert.Fail("The generated glue does not compile:\n" + string.Join("\n",
                        emitted.Diagnostics
                            .Where(diagnostic => diagnostic.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
                            .Select(diagnostic => diagnostic.ToString())) + "\n\n" + source);
                }

                return Assembly.Load(stream.ToArray());
            }
        }

        private Component Spawn(Type type)
        {
            var host = new GameObject("CE.SyncOnJoinRegression." + type.Name);
            _created.Add(host);
            return host.AddComponent(type);
        }

        /// <summary>
        /// Every message logged while <paramref name="action"/> runs, and nothing logged around it -- so an
        /// assertion about what the glue says is not also an assertion about the editor's own logging.
        /// </summary>
        internal static List<string> CaptureLogs(Action action)
        {
            var logs = new List<string>();
            Application.LogCallback capture = (condition, stackTrace, type) => logs.Add(type + ": " + condition);

            Application.logMessageReceived += capture;

            try
            {
                action();
            }
            finally
            {
                Application.logMessageReceived -= capture;
            }

            return logs;
        }

        /// <summary>
        /// A field anywhere in the type's hierarchy. The generated transport is protected, and GetField does
        /// not return a base class's non-public members.
        /// </summary>
        private static FieldInfo FindField(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                if (field != null)
                    return field;
            }

            Assert.Fail($"'{type.Name}' has no field '{name}'.");
            return null;
        }

        private static object Get(Component component, string field)
        {
            return FindField(component.GetType(), field).GetValue(component);
        }

        private static void Set(Component component, string field, object value)
        {
            FindField(component.GetType(), field).SetValue(component, value);
        }

        private static object Call(Component component, string method, params object[] arguments)
        {
            for (Type current = component.GetType(); current != null; current = current.BaseType)
            {
                MethodInfo found = current
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length);

                if (found == null)
                    continue;

                try
                {
                    return found.Invoke(component, arguments);
                }
                catch (TargetInvocationException exception) when (exception.InnerException != null)
                {
                    ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                    throw;
                }
            }

            Assert.Fail($"'{component.GetType().Name}' has no method '{method}' taking {arguments.Length} argument(s).");
            return null;
        }
    }

    /// <summary>
    /// The [SyncOnJoin] and [Sync(Quantize)] shapes plain C# accepts and UdonSharp does not, compiled as Udon
    /// from fixture behaviours under TestsRuntime and run on two Udon heaps.
    /// </summary>
    /// <remarks>
    /// Every fixture gets its program asset once and the whole project compiles once, in
    /// <see cref="CompileFixtures"/>; each test then reads its own program. A fixture with no program asset is
    /// never bound by UdonSharp, so none of these shapes is checked by any other compile.
    /// </remarks>
    public class CESyncOnJoinUdonRegressionTests
    {
        private readonly List<string> _createdAssetPaths = new List<string>();
        private readonly List<UnityEngine.Object> _createdObjects = new List<UnityEngine.Object>();

        private UdonSharpProgramAsset _sealed;
        private UdonSharpProgramAsset _quantizedDerived;
        private UdonSharpProgramAsset _resultOnBase;
        private UdonSharpProgramAsset _resultOnDerived;
        private UdonSharpProgramAsset _grouped;

        [OneTimeSetUp]
        public void CompileFixtures()
        {
            _sealed = CreateProgramAsset(typeof(CESyncRuntimeTests.EditorSyncOnJoinSealedTestBehaviour));
            _quantizedDerived = CreateProgramAsset(typeof(CESyncRuntimeTests.EditorQuantizeDerivedTestBehaviour));
            _resultOnBase = CreateProgramAsset(typeof(CESyncRuntimeTests.EditorSyncOnJoinResultHandlerDerivedTestBehaviour));
            _resultOnDerived = CreateProgramAsset(typeof(CESyncRuntimeTests.EditorSyncOnJoinResultOverrideTestBehaviour));
            _grouped = CreateProgramAsset(typeof(CESyncRuntimeTests.EditorSyncOnJoinGroupedTestBehaviour));

            CompileAllProgramsSync();
        }

        [OneTimeTearDown]
        public void DeleteFixturePrograms()
        {
            foreach (UnityEngine.Object created in _createdObjects)
            {
                if (created != null)
                    UnityEngine.Object.DestroyImmediate(created);
            }

            _createdObjects.Clear();

            foreach (string assetPath in _createdAssetPaths)
            {
                // The compiled program is a separate asset that deleting the program asset leaves behind.
                var programAsset = AssetDatabase.LoadAssetAtPath<UdonSharpProgramAsset>(assetPath);
                string serializedProgramPath = programAsset != null && programAsset.SerializedProgramAsset != null
                    ? AssetDatabase.GetAssetPath(programAsset.SerializedProgramAsset)
                    : null;

                AssetDatabase.DeleteAsset(assetPath);

                if (!string.IsNullOrEmpty(serializedProgramPath))
                    AssetDatabase.DeleteAsset(serializedProgramPath);
            }

            _createdAssetPaths.Clear();
            AssetDatabase.Refresh();
            CompileAllProgramsSync();
        }

        /// <summary>
        /// The glue declared its members 'virtual' and its transport 'protected' whatever the behaviour was.
        /// On a sealed behaviour that is CS0549, and generated code that does not compile fails every program
        /// in the project. The fixture's const field is the CE0032 half: it is left out of the payload, and
        /// the warning now says so.
        /// </summary>
        [Test]
        public void SealedCarrier_CompilesAsUdonAndRoundTrips()
        {
            Assert.That(GetAssemblyError(_sealed), Is.Null.Or.Empty);

            string warnings = GetLastCompileDiagnosticsText("Warning");
            Assert.That(warnings, Does.Contain("CE0032").And.Contain("SchemaVersion"),
                "The const [SyncOnJoin] field is left out of the payload without a word.");

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(_sealed))
            {
                pair.Owner.SetProgramVariable("score", 9);
                pair.Owner.SetProgramVariable("label", "sealed");
                pair.Owner.SendCustomEvent("_CE_SyncOnJoin_Serialize");
                pair.Owner.SendCustomEvent("_CE_SyncOnJoin_Serialize_detail");

                CopyVariable(pair, "_CE_SyncOnJoinPayload");
                CopyVariable(pair, "_CE_SyncOnJoinPayload_detail");
                pair.Remote.OnDeserialization(new DeserializationResult());

                Assert.That(pair.Remote.GetProgramVariable("score"), Is.EqualTo(9));
                Assert.That(pair.Remote.GetProgramVariable("label"), Is.EqualTo("sealed"));
            }
        }

        /// <summary>
        /// A base and a derived behaviour that both quantize used to get one helper name each, which
        /// UdonSharp rejects as a hidden base method. Both helpers have to exist and both have to run.
        /// </summary>
        [Test]
        public void QuantizedBaseAndDerived_CompileAsUdonAndBothRound()
        {
            Assert.That(GetAssemblyError(_quantizedDerived), Is.Null.Or.Empty);

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(_quantizedDerived))
            {
                pair.Owner.SetProgramVariable("baseValue", 0.123f);
                pair.Owner.SetProgramVariable("derivedValue", 0.3f);
                pair.Owner.OnPreSerialization();

                Assert.That((float)pair.Owner.GetProgramVariable("baseValue"), Is.EqualTo(0.1f).Within(0.0001f),
                    "The base class's quantized field was not rounded.");
                Assert.That((float)pair.Owner.GetProgramVariable("derivedValue"), Is.EqualTo(0.5f).Within(0.0001f),
                    "The derived class's quantized field was not rounded.");
            }
        }

        /// <summary>
        /// The base handles OnDeserialization(DeserializationResult) and the derived behaviour carries the
        /// state. The glue gave it a parameterless OnDeserialization(), and the program exported
        /// '_onDeserialization' twice. Now the base's overload is overridden -- and the base's handler still
        /// runs.
        /// </summary>
        [Test]
        public void ResultOverloadOnTheBase_CompilesAsUdonAndBothHandlersRun()
        {
            Assert.That(GetAssemblyError(_resultOnBase), Is.Null.Or.Empty);

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(_resultOnBase))
            {
                pair.Owner.SetProgramVariable("phase", 4);
                pair.Owner.SendCustomEvent("_CE_SyncOnJoin_Serialize");
                CopyVariable(pair, "_CE_SyncOnJoinPayload");

                pair.Remote.OnDeserialization(new DeserializationResult());

                Assert.That(pair.Remote.GetProgramVariable("phase"), Is.EqualTo(4));
                Assert.That(pair.Remote.GetProgramVariable("baseHandlerRuns"), Is.EqualTo(1),
                    "The handler the base's author wrote was skipped.");
            }
        }

        /// <summary>
        /// The mirror image: the base carries the state and the derived behaviour's author handles the
        /// DeserializationResult overload. The glue gave the base a parameterless OnDeserialization(), so the
        /// derived program exported '_onDeserialization' twice.
        /// </summary>
        [Test]
        public void ResultOverloadOnTheDerived_CompilesAsUdonAndBothRun()
        {
            Assert.That(GetAssemblyError(_resultOnDerived), Is.Null.Or.Empty);

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(_resultOnDerived))
            {
                pair.Owner.SetProgramVariable("phase", 6);
                pair.Owner.SendCustomEvent("_CE_SyncOnJoin_Serialize");
                CopyVariable(pair, "_CE_SyncOnJoinPayload");

                pair.Remote.OnDeserialization(new DeserializationResult());

                Assert.That(pair.Remote.GetProgramVariable("phase"), Is.EqualTo(6));
                Assert.That(pair.Remote.GetProgramVariable("derivedHandlerRuns"), Is.EqualTo(1));
            }
        }

        /// <summary>
        /// VRChat drops an OnDeserialization that arrives before the behaviour has started -- UdonBehaviour
        /// runs it through RunEvent with canRunBeforeStart false -- while the synced payload is already in the
        /// heap. The only thing that decoded it was that event, so a behaviour on an object that was inactive
        /// when a player joined kept its scene defaults. The restore now runs in Start too: silently when
        /// there is nothing to restore, and without the author's OnDeserialization having run at all.
        /// </summary>
        [Test]
        public void PayloadInPlaceBeforeStart_IsRestoredWhenStartRuns()
        {
            Assert.That(GetAssemblyError(_grouped), Is.Null.Or.Empty);

            using (new UdonSyncHarness.RuntimeBootstrap())
            using (var pair = UdonSyncHarness.CreatePair(_grouped))
            {
                // The owner at world load: every payload still empty.
                bool ownerStarted = false;
                List<string> logs = CESyncOnJoinGlueRegressionTests.CaptureLogs(() => ownerStarted = pair.Owner.RunEvent("_start"));

                Assert.That(ownerStarted, Is.True, "The program has no Start entry point.");
                Assert.That(logs, Is.Empty, "Start with nothing to restore logged.");

                pair.Owner.SetProgramVariable("terrainVersion", 3);
                pair.Owner.SetProgramVariable("score", 11);
                pair.Owner.SetProgramVariable("label", "arena");

                foreach (string suffix in new[] { "_terrain", "_score", "" })
                {
                    pair.Owner.SendCustomEvent("_CE_SyncOnJoin_Serialize" + suffix);
                    CopyVariable(pair, "_CE_SyncOnJoinPayload" + suffix);
                }

                Assert.That(pair.Remote.RunEvent("_start"), Is.True);

                Assert.That(pair.Remote.GetProgramVariable("terrainVersion"), Is.EqualTo(3));
                Assert.That(pair.Remote.GetProgramVariable("score"), Is.EqualTo(11));
                Assert.That(pair.Remote.GetProgramVariable("label"), Is.EqualTo("arena"));
                Assert.That(pair.Remote.GetProgramVariable("deserializations"), Is.EqualTo(0),
                    "Start restores without running the author's OnDeserialization.");

                // Already applied, on both sides: Start has nothing left to do and must not say anything.
                logs = CESyncOnJoinGlueRegressionTests.CaptureLogs(() =>
                {
                    pair.Owner.RunEvent("_start");
                    pair.Remote.RunEvent("_start");
                });

                Assert.That(logs, Is.Empty, "Start over a payload already applied logged.");
            }
        }

        // ------------------------------------------------------------------------------------------
        // Harness
        // ------------------------------------------------------------------------------------------

        /// <summary>Moves one synced variable to the remote the way a delivery would, as a separate copy.</summary>
        private static void CopyVariable(UdonSyncHarness.BehaviourPair pair, string name)
        {
            pair.Remote.SetProgramVariable(name, pair.Owner.GetProgramVariable(name));
        }

        private UdonSharpProgramAsset CreateProgramAsset(Type behaviourType)
        {
            var host = new GameObject("CE.SyncOnJoinUdonRegression." + behaviourType.Name);
            _createdObjects.Add(host);

            var behaviour = (UdonSharpBehaviour)host.AddComponent(behaviourType);

            MethodInfo getProgramAsset = typeof(UdonSharpEditorUtility).GetMethod(
                "GetUdonSharpProgramAsset",
                BindingFlags.Static | BindingFlags.Public,
                null,
                new[] { typeof(UdonSharpBehaviour) },
                null);

            var asset = (UdonSharpProgramAsset)getProgramAsset.Invoke(null, new object[] { behaviour });
            Assert.That(asset, Is.Not.Null, $"No program asset could be created for '{behaviourType.Name}'.");

            string assetPath = AssetDatabase.GetAssetPath(asset);
            if (!string.IsNullOrEmpty(assetPath) && !_createdAssetPaths.Contains(assetPath))
                _createdAssetPaths.Add(assetPath);

            return asset;
        }

        private static void CompileAllProgramsSync()
        {
            UdonSharpCompilerV1.CompileSync(new UdonSharpCompileOptions { IsEditorBuild = true });
        }

        private static string GetAssemblyError(UdonSharpProgramAsset asset)
        {
            FieldInfo assemblyErrorField = asset.GetType().BaseType.GetField("assemblyError", BindingFlags.Instance | BindingFlags.NonPublic);
            string assemblyError = (string)assemblyErrorField.GetValue(asset);

            return !string.IsNullOrEmpty(assemblyError)
                ? assemblyError
                : GetLastCompileDiagnosticsText("Error");
        }

        private static string GetLastCompileDiagnosticsText(string severity)
        {
            Assembly editorAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .Single(assembly => assembly.GetName().Name == "UdonSharp.Editor");
            Type cacheType = editorAssembly.GetType("UdonSharp.UdonSharpEditorCache", throwOnError: true);
            object instance = cacheType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public).GetValue(null);
            PropertyInfo diagnosticsProperty = cacheType.GetProperty("LastCompileDiagnostics", BindingFlags.Instance | BindingFlags.NonPublic);

            if (!(diagnosticsProperty?.GetValue(instance) is Array diagnostics))
                return string.Empty;

            var messages = new List<string>();

            foreach (object diagnostic in diagnostics)
            {
                FieldInfo severityField = diagnostic.GetType().GetField("severity", BindingFlags.Instance | BindingFlags.Public);
                FieldInfo messageField = diagnostic.GetType().GetField("message", BindingFlags.Instance | BindingFlags.Public);

                if (severityField == null || messageField == null)
                    continue;

                if (string.Equals(severityField.GetValue(diagnostic)?.ToString(), severity, StringComparison.Ordinal))
                    messages.Add((string)messageField.GetValue(diagnostic));
            }

            return string.Join(Environment.NewLine, messages);
        }
    }
}
