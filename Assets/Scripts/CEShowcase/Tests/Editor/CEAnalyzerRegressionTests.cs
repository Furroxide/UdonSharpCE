using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UdonSharp;
using UdonSharp.CE.Editor.Analyzers;
using UdonSharp.CE.Editor.DevTools.Analysis;
using UdonSharp.CE.Editor.DevTools.Validation;
using UdonSharp.CE.Editor.DevTools.Validation.Validators;
using UdonSharp.CE.Net;
using UnityEngine;
using VRC.SDKBase;
using CompilerSeverity = UdonSharp.Compiler.DiagnosticSeverity;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Pins the fixes from the audit of CE's compile-time diagnostics and the World Validator, each from
    /// both sides: the shape the check exists for still reports, and the correct shape it used to report
    /// does not. Every check here describes a bug with no runtime symptom, so a diagnostic that drifts in
    /// either direction is found by an author in a live world or not at all.
    ///
    /// Analyzer fixtures
    /// are bound through CENetCodegenRegressionTests.RunAnalyzer, which runs the compiler's real Bind
    /// phase over one source string; validator fixtures are real types in this assembly, read by
    /// reflection or attached to throwaway GameObjects.
    /// </summary>
    public class CEAnalyzerRegressionTests
    {
        private const string OwnershipAnalyzer = "UdonSharp.CE.Editor.Analyzers.SyncedFieldOwnershipAnalyzer";
        private const string RateAnalyzer = "UdonSharp.CE.Editor.Analyzers.RequestSerializationRateAnalyzer";
        private const string EmptyPayloadAnalyzer = "UdonSharp.CE.Editor.Analyzers.ManualSyncWithoutSyncedFieldsAnalyzer";
        private const string PayloadSizeAnalyzer = "UdonSharp.CE.Editor.Analyzers.SyncPayloadSizeAnalyzer";
        private const string PlayerDataAnalyzer = "UdonSharp.CE.Editor.Analyzers.PlayerDataSizeAnalyzer";
        private const string SyncArrayAnalyzer = "UdonSharp.CE.Editor.Analyzers.UninitializedSyncArrayAnalyzer";
        private const string GetComponentAnalyzer = "UdonSharp.CE.Editor.Analyzers.GetComponentInUpdateAnalyzer";

        private readonly List<GameObject> _hosts = new List<GameObject>();

        [TearDown]
        public void TearDown()
        {
            new CENetCodegenRegressionTests().UnregisterFixtureAssemblies();

            foreach (GameObject host in _hosts)
            {
                if (host != null)
                    UnityEngine.Object.DestroyImmediate(host);
            }

            _hosts.Clear();
        }

        // ------------------------------------------------------------------------------------------
        // CE0003 -- payload size
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The size table is keyed by CLR name and was looked up by the C# keyword, so no primitive ever
        /// matched and every one was counted as four bytes. Fifty-two bools (52 bytes) were reported as
        /// 208 and over the limit; thirty doubles (240 bytes) were counted as 120 and passed.
        /// </summary>
        [TestCase("bool", 52, false)]
        [TestCase("double", 30, true)]
        public void PayloadSize_CountsEachPrimitiveAtItsOwnWidth(string fieldType, int count, bool expectReport)
        {
            string fields = string.Join("\n",
                Enumerable.Range(0, count).Select(i => $"    [UdonSynced] public {fieldType} value{i};"));

            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(PayloadSizeAnalyzer, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
public class DiagPayloadFixture : UdonSharpBehaviour
{
" + fields + @"
}", "DiagPayloadFixture");

            Assert.That(diagnostics.Any(d => d.Code == "CE0003"), Is.EqualTo(expectReport),
                $"{count} x {fieldType} is {(expectReport ? "over" : "well under")} the 200-byte Continuous " +
                "limit at that type's real width.\n" + Render(diagnostics));
        }

        // ------------------------------------------------------------------------------------------
        // CE0014 -- writes reached through Udon's string-addressed dispatch
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The handler of an event this behaviour only ever sends to the owner runs on the owner, which
        /// is the guarantee [Rpc(Target = Owner)] carries and was already exempt for. Reporting it was
        /// reporting the one pattern that exists to avoid the bug CE0014 describes.
        /// </summary>
        [Test]
        public void Ownership_HandlerOfAnOwnerTargetedEvent_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagOwnerEventFixture : UdonSharpBehaviour
{
    [UdonSynced] private bool _open;

    public override void Interact()
    {
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ToggleOnOwner));
    }

    public void ToggleOnOwner()
    {
        _open = !_open;
        RequestSerialization();
    }
}", "DiagOwnerEventFixture");

            AssertNone(diagnostics, "CE0014",
                "ToggleOnOwner is only ever sent to NetworkEventTarget.Owner, so its body only runs where the " +
                "write is kept.");
        }

        /// <summary>
        /// The same handler once anything also sends it to everyone: the body now runs on every client and
        /// needs a guard again. The exemption is per handler, and one non-owner send withdraws it.
        /// </summary>
        [Test]
        public void Ownership_HandlerAlsoSentToEveryone_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagBroadcastEventFixture : UdonSharpBehaviour
{
    [UdonSynced] private bool _open;

    public override void Interact()
    {
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ToggleOnOwner));
    }

    public void Broadcast()
    {
        SendCustomNetworkEvent(NetworkEventTarget.All, nameof(ToggleOnOwner));
    }

    public void ToggleOnOwner()
    {
        _open = !_open;
        RequestSerialization();
    }
}", "DiagBroadcastEventFixture");

            AnalyzerDiagnostic report = AssertSingle(diagnostics, "CE0014",
                "Broadcast() runs ToggleOnOwner on every client, so the write is discarded everywhere but the " +
                "owner.");

            Assert.That(report.Message, Does.Contain("ToggleOnOwner"));
        }

        /// <summary>
        /// The owner-targeted send covers the way in it is: a network event. A direct call is not one, and
        /// runs the write on whoever made it, so the handler's callers inside the behaviour still have to
        /// carry their own check.
        /// </summary>
        [Test]
        public void Ownership_HandlerAlsoCalledDirectly_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;
using VRC.Udon.Common.Interfaces;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagDirectHandlerCallFixture : UdonSharpBehaviour
{
    [UdonSynced] private bool _open;

    public override void Interact()
    {
        ToggleOnOwner();
    }

    public void RequestToggle()
    {
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(ToggleOnOwner));
    }

    public void ToggleOnOwner()
    {
        _open = !_open;
        RequestSerialization();
    }
}", "DiagDirectHandlerCallFixture");

            AssertSingle(diagnostics, "CE0014",
                "Interact calls ToggleOnOwner directly on whoever clicked; only the network send reaches the owner.");
        }

        /// <summary>
        /// A timer armed only on the owner. The handler is reached through SendCustomEventDelayedSeconds,
        /// which the call graph did not record, so it looked like an entry point anyone could invoke and
        /// its write was reported -- in one of the commonest correct shapes a world has.
        /// </summary>
        [Test]
        public void Ownership_TimerArmedOnlyOnTheOwner_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagOwnerTimerFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _timeLeft;

    private void Start()
    {
        if (Networking.IsOwner(gameObject))
            SendCustomEventDelayedSeconds(nameof(_Tick), 1f);
    }

    public void _Tick()
    {
        _timeLeft--;
        RequestSerialization();
        SendCustomEventDelayedSeconds(nameof(_Tick), 1f);
    }
}", "DiagOwnerTimerFixture");

            AssertNone(diagnostics, "CE0014",
                "Only the owner arms the timer, and the timer re-arms itself, so _Tick only ever runs on the owner.");
        }

        /// <summary>
        /// The same timer armed in Start on every client. Start is exempt because seeding defaults before
        /// ownership means anything is not a lost write; that excuse covers what Start does, not what it
        /// schedules a second later.
        /// </summary>
        [Test]
        public void Ownership_TimerArmedByStartOnEveryClient_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagEveryoneTimerFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _timeLeft;

    private void Start()
    {
        SendCustomEventDelayedSeconds(nameof(_Tick), 1f);
    }

    public void _Tick()
    {
        _timeLeft--;
        SendCustomEventDelayedSeconds(nameof(_Tick), 1f);
    }
}", "DiagEveryoneTimerFixture");

            AnalyzerDiagnostic report = AssertSingle(diagnostics, "CE0014",
                "Every client runs Start, so every client runs _Tick, and the write is lost on all but the owner.");

            Assert.That(report.Message, Does.Contain("_Tick"));
        }

        /// <summary>
        /// A guard in a handler the writer only schedules gates nothing the writer did: it runs after the
        /// writer has returned. Following the delayed edge into that handler would have read its check as
        /// the writer's.
        /// </summary>
        [Test]
        public void Ownership_GuardInAHandlerTheWriterSchedules_DoesNotCoverTheWrite()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagScheduledGuardFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _score;

    public override void Interact()
    {
        _score++;
        SendCustomEventDelayedSeconds(nameof(_SyncIfOwner), 0.1f);
    }

    public void _SyncIfOwner()
    {
        if (Networking.IsOwner(gameObject))
            RequestSerialization();
    }
}", "DiagScheduledGuardFixture");

            AnalyzerDiagnostic report = AssertSingle(diagnostics, "CE0014",
                "Interact writes on whoever clicked; the ownership check runs a tenth of a second later in another " +
                "method.");

            Assert.That(report.Message, Does.Contain("Interact"));
        }

        /// <summary>
        /// SendCustomEvent runs its handler before it returns, so a handler reached only that way from a
        /// guarded method is as guarded as one called directly.
        /// </summary>
        [Test]
        public void Ownership_HandlerReachedBySendCustomEventFromAGuardedMethod_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagLocalEventFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _score;

    public override void Interact()
    {
        if (!Networking.IsOwner(gameObject))
            return;

        SendCustomEvent(nameof(AddPoint));
    }

    public void AddPoint()
    {
        _score++;
        RequestSerialization();
    }
}", "DiagLocalEventFixture");

            AssertNone(diagnostics, "CE0014",
                "AddPoint's only caller returns early on every non-owner before dispatching to it.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0014 -- the exemption belongs to the entry point
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// One exempt caller used to excuse every way into a method: the write in ResetScore went
        /// unreported because Start also calls it, although Interact runs on whoever clicked.
        /// </summary>
        [Test]
        public void Ownership_HelperCalledFromStartAndFromInteract_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagSharedHelperFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _score;

    private void Start()
    {
        ResetScore();
    }

    public override void Interact()
    {
        ResetScore();
    }

    private void ResetScore()
    {
        _score = 0;
        RequestSerialization();
    }
}", "DiagSharedHelperFixture");

            AnalyzerDiagnostic report = AssertSingle(diagnostics, "CE0014",
                "Interact reaches ResetScore with no ownership check, whatever Start does.");

            Assert.That(report.Message, Does.Contain("ResetScore"));
        }

        [Test]
        public void Ownership_HelperCalledOnlyFromStart_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagStartHelperFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _score;

    private void Start()
    {
        ResetScore();
    }

    private void ResetScore()
    {
        _score = 0;
    }
}", "DiagStartHelperFixture");

            AssertNone(diagnostics, "CE0014",
                "Every way into ResetScore is Start, where seeding a default is not a lost network write.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0014 -- writes through a struct member
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// '_target.y += 1f' rewrites the synced Vector3 as surely as assigning a new one, and was read as
        /// a write to some other object's 'y'.
        /// </summary>
        [Test]
        public void Ownership_WriteToAMemberOfASyncedStruct_IsReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagStructWriteFixture : UdonSharpBehaviour
{
    [UdonSynced] private Vector3 _target;

    public override void Interact()
    {
        _target.y += 1f;
        RequestSerialization();
    }
}", "DiagStructWriteFixture");

            AnalyzerDiagnostic report = AssertSingle(diagnostics, "CE0014",
                "The write lands in the synced field _target.");

            Assert.That(report.Message, Does.Contain("_target"));
        }

        [Test]
        public void Ownership_GuardedWriteToAMemberOfASyncedStruct_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(OwnershipAnalyzer, @"
using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagGuardedStructWriteFixture : UdonSharpBehaviour
{
    [UdonSynced] private Vector3 _target;

    public override void Interact()
    {
        if (!Networking.IsOwner(gameObject))
            return;

        _target.y += 1f;
        RequestSerialization();
    }
}", "DiagGuardedStructWriteFixture");

            AssertNone(diagnostics, "CE0014", "The write is behind an ownership check.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0016 -- empty payload
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The emitter serializes inherited fields, so a subclass of a base that declares one has a payload
        /// whatever its own declarations say. Reading only the subclass told its author the payload was empty.
        /// </summary>
        [Test]
        public void EmptyPayload_SubclassOfABaseWithSyncedFields_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(EmptyPayloadAnalyzer, @"
using UdonSharp;
using VRC.SDKBase;

public abstract class DiagSyncedHealthBase : UdonSharpBehaviour
{
    [UdonSynced] protected int _health;
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagInheritedPayloadFixture : DiagSyncedHealthBase
{
    public override void Interact()
    {
        if (!Networking.IsOwner(gameObject))
            return;

        _health--;
        RequestSerialization();
    }
}", "DiagInheritedPayloadFixture");

            AssertNone(diagnostics, "CE0016", "_health is declared on the base and serialized with the subclass.");
        }

        [Test]
        public void EmptyPayload_SubclassWithNoSyncedFieldAnywhere_IsStillReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(EmptyPayloadAnalyzer, @"
using UdonSharp;

public abstract class DiagPlainHealthBase : UdonSharpBehaviour
{
    protected int _health;
}

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagEmptyPayloadFixture : DiagPlainHealthBase
{
    public override void Interact()
    {
        _health--;
        RequestSerialization();
    }
}", "DiagEmptyPayloadFixture");

            AnalyzerDiagnostic report = AssertSingle(diagnostics, "CE0016",
                "Nothing in the chain is synced, so the RequestSerialization() call has nothing to send.");

            Assert.That(report.Severity, Is.EqualTo(CompilerSeverity.Warning));
        }

        // ------------------------------------------------------------------------------------------
        // CE0017 -- what counts as a rate gate
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The dirty flag is tested in Update and cleared in OnPostSerialization, the one place that knows
        /// the send happened. The check only looked for the clear in the method holding the call and
        /// reported a once-per-change send as "every frame".
        /// </summary>
        [Test]
        public void SerializationRate_FlagClearedAfterTheSend_IsAGate()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(RateAnalyzer, @"
using UdonSharp;
using VRC.Udon.Common;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagDirtyFlagFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _value;
    private bool _dirty;

    public void Bump()
    {
        _value++;
        _dirty = true;
    }

    private void Update()
    {
        if (!_dirty)
            return;

        RequestSerialization();
    }

    public override void OnPostSerialization(SerializationResult result)
    {
        _dirty = false;
    }
}", "DiagDirtyFlagFixture");

            AssertNone(diagnostics, "CE0017", "The call runs once per Bump, not once per frame.");
        }

        /// <summary>
        /// A flag nothing on the per-frame path or in a serialization callback ever clears is not a latch:
        /// once set it lets every frame through.
        /// </summary>
        [Test]
        public void SerializationRate_FlagNothingClears_IsNotAGate()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(RateAnalyzer, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagReadyFlagFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _value;
    private bool _ready;

    private void Start()
    {
        _ready = true;
    }

    private void Update()
    {
        if (!_ready)
            return;

        RequestSerialization();
    }
}", "DiagReadyFlagFixture");

            AssertSingle(diagnostics, "CE0017", "After Start, Update serializes on every frame.");
        }

        /// <summary>
        /// Input.GetKeyDown is true for the one frame a key goes down, so the call behind it runs once per
        /// press. It holds no comparison and no flag, and was reported as running every frame.
        /// </summary>
        [Test]
        public void SerializationRate_InputEdge_IsAGate()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(RateAnalyzer, @"
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagKeyDownFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _resets;

    private void Update()
    {
        if (Input.GetKeyDown(KeyCode.R))
        {
            _resets++;
            RequestSerialization();
        }
    }
}", "DiagKeyDownFixture");

            AssertNone(diagnostics, "CE0017", "GetKeyDown is true for one frame per press.");
        }

        [Test]
        public void SerializationRate_HeldInput_IsNotAGate()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(RateAnalyzer, @"
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagKeyHeldFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _resets;

    private void Update()
    {
        if (Input.GetKey(KeyCode.R))
        {
            _resets++;
            RequestSerialization();
        }
    }
}", "DiagKeyHeldFixture");

            AssertSingle(diagnostics, "CE0017", "GetKey is true on every frame the key is held.");
        }

        /// <summary>
        /// A null test holds or fails for as long as the reference stays put, and every frame it lets
        /// through still serializes. Reading any '==' as a change gate hid this Update entirely.
        /// </summary>
        [Test]
        public void SerializationRate_NullCheck_IsNotAGate()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(RateAnalyzer, @"
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagNullCheckFixture : UdonSharpBehaviour
{
    [UdonSynced] private Vector3 _position;
    public Transform target;

    private void Update()
    {
        if (target == null)
            return;

        _position = target.position;
        RequestSerialization();
    }
}", "DiagNullCheckFixture");

            AssertSingle(diagnostics, "CE0017", "Once target is set, this serializes on every frame.");
        }

        /// <summary>
        /// The reference found on the first frame is assigned on the per-frame path, which made the name a
        /// dirty-flag candidate and brought the refused null test back through that rule. A flag is a bool.
        /// </summary>
        [Test]
        public void SerializationRate_NullCheckOnAReferenceTheUpdateAssigns_IsNotAGate()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(RateAnalyzer, @"
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagLazyTargetFixture : UdonSharpBehaviour
{
    [UdonSynced] private Vector3 _position;
    private Transform _target;

    private void Update()
    {
        if (_target == null)
        {
            _target = transform.parent;
            return;
        }

        _position = _target.position;
        RequestSerialization();
    }
}", "DiagLazyTargetFixture");

            AssertSingle(diagnostics, "CE0017", "After the first frame finds _target, every frame serializes.");
        }

        /// <summary>
        /// An ownership comparison is the guard the worked example opens with, spelled with '!=' instead of
        /// a call. It narrows the send to the owner and bounds nothing.
        /// </summary>
        [Test]
        public void SerializationRate_OwnershipComparison_IsNotAGate()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(RateAnalyzer, @"
using UdonSharp;
using VRC.SDKBase;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagOwnerComparisonFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _ticks;

    private void Update()
    {
        if (Networking.GetOwner(gameObject).playerId != Networking.LocalPlayer.playerId)
            return;

        _ticks++;
        RequestSerialization();
    }
}", "DiagOwnerComparisonFixture");

            AssertSingle(diagnostics, "CE0017", "The owner serializes on every frame.");
        }

        [Test]
        public void SerializationRate_ComparisonOfTwoValues_IsStillAGate()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(RateAnalyzer, @"
using UdonSharp;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagValueComparisonFixture : UdonSharpBehaviour
{
    [UdonSynced] private int _value;
    private int _lastSent;

    private void Update()
    {
        if (_value == _lastSent)
            return;

        _lastSent = _value;
        RequestSerialization();
    }
}", "DiagValueComparisonFixture");

            AssertNone(diagnostics, "CE0017", "The call runs once per change of _value.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0030 -- PlayerData size
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The model is over the quota once its array length is read through the constant, and the report
        /// is a Warning: an estimate taken at every number's longest spelling must not fail a build. The
        /// string array has no knowable length and has to be named as uncounted, not guessed at.
        /// </summary>
        [Test]
        public void PlayerDataSize_OverTheQuota_WarnsAndNamesWhatItCouldNotCount()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(PlayerDataAnalyzer, @"
using UdonSharp;
using UdonSharp.CE.Persistence;

[PlayerData(""diag_big"")]
public class DiagConstSizedModel
{
    private const int Slots = 10000;

    [PersistKey(""values"")] public int[] values = new int[Slots];
    [PersistKey(""lines"")] public string[] lines;
}

public class DiagPersistenceHost : UdonSharpBehaviour
{
}", "DiagPersistenceHost");

            AnalyzerDiagnostic report = AssertSingle(diagnostics, "CE0030",
                "10000 ints at eleven characters each is over the 102400-byte quota.");

            Assert.That(report.Severity, Is.EqualTo(CompilerSeverity.Warning),
                "CE0030 is a worst-case estimate and may only warn.");
            Assert.That(report.Message, Does.Contain("DiagConstSizedModel"));
            Assert.That(report.Message, Does.Contain("lines"),
                "The field whose length could not be read has to be named as left out of the figure.");
        }

        [Test]
        public void PlayerDataSize_ModelWellInsideTheQuota_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(PlayerDataAnalyzer, @"
using UdonSharp;
using UdonSharp.CE.Persistence;

[PlayerData(""diag_small"")]
public class DiagSmallModel
{
    [PersistKey(""xp"")] public int experience;
    [PersistKey(""log"")] public int[] log;
}

public class DiagSmallPersistenceHost : UdonSharpBehaviour
{
}", "DiagSmallPersistenceHost");

            AssertNone(diagnostics, "CE0030",
                "An int and an array of unknown length: nothing measurable is near the quota, and the unknown " +
                "array is not guessed into one.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0001 -- uninitialized synced array
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// The suggestion was a fixed 'private int[] name = new int[SIZE];' whatever the field was, and the
        /// message claimed VRChat needs a fixed size known at compile time, which the package's own
        /// NetPhysicsWorld disproves by resizing its synced buffer on every send.
        /// </summary>
        [Test]
        public void UninitializedSyncArray_SuggestsTheFieldsOwnTypeAndAccessibility()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(SyncArrayAnalyzer, @"
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagNullPointsFixture : UdonSharpBehaviour
{
    [UdonSynced] public Vector3[] points;
}", "DiagNullPointsFixture");

            AnalyzerDiagnostic report = AssertSingle(diagnostics, "CE0001", "The synced array has no initializer.");

            Assert.That(report.Severity, Is.EqualTo(CompilerSeverity.Error));
            Assert.That(report.FixSuggestion, Does.Contain("public Vector3[] points = new Vector3[SIZE];"));
            Assert.That(report.Message, Does.Not.Contain("fixed size"));
        }

        [Test]
        public void UninitializedSyncArray_InitializedArray_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(SyncArrayAnalyzer, @"
using UdonSharp;
using UnityEngine;

[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class DiagPointsFixture : UdonSharpBehaviour
{
    [UdonSynced] public Vector3[] points = new Vector3[4];
}", "DiagPointsFixture");

            AssertNone(diagnostics, "CE0001", "The array exists from its declaration.");
        }

        // ------------------------------------------------------------------------------------------
        // CE0002 -- GetComponent in Update
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Filling an empty cache on first use is the fix this diagnostic asks for, written lazily: the
        /// lookup runs on one frame and never again.
        /// </summary>
        [Test]
        public void GetComponentInUpdate_LazyCacheFill_IsNotReported()
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(GetComponentAnalyzer, @"
using UdonSharp;
using UnityEngine;

public class DiagLazyCacheFixture : UdonSharpBehaviour
{
    private Renderer _renderer;

    private void Update()
    {
        if (_renderer == null)
            _renderer = GetComponent<Renderer>();

        _renderer.enabled = true;
    }
}", "DiagLazyCacheFixture");

            AssertNone(diagnostics, "CE0002", "The lookup only runs while the cache is empty.");
        }

        [TestCase("GetComponent<Renderer>().enabled = true;")]
        [TestCase("Renderer r = null; if (r == null) r = GetComponent<Renderer>(); r.enabled = true;")]
        public void GetComponentInUpdate_LookupOnEveryFrame_IsReported(string body)
        {
            IReadOnlyList<AnalyzerDiagnostic> diagnostics = Run(GetComponentAnalyzer, @"
using UdonSharp;
using UnityEngine;

public class DiagEveryFrameLookupFixture : UdonSharpBehaviour
{
    private void Update()
    {
        " + body + @"
    }
}", "DiagEveryFrameLookupFixture");

            AssertSingle(diagnostics, "CE0002",
                "A local is empty again on every call, so null-checking one caches nothing.");
        }

        // ------------------------------------------------------------------------------------------
        // Bandwidth Analyzer and the Bandwidth Limits validator
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// Quantize rounds the value and sends the same float; the analyzer used to take half a quantized
        /// quaternion off the payload before checking the Continuous limit.
        /// </summary>
        [Test]
        public void Bandwidth_QuantizeLeavesTheFieldAtFullWidth()
        {
            FieldAnalysisResult field = new BehaviourAnalyzer()
                .AnalyzeField(typeof(DiagQuantizedFixture).GetField(nameof(DiagQuantizedFixture.rotation)));

            Assert.That(field.Size.MaxBytes, Is.EqualTo(16), "A quaternion is sent as four floats either way.");
            Assert.That(field.Notes, Does.Contain("Quantize"), "The option is still named, just not discounted.");
        }

        /// <summary>
        /// A synced array's length is a property of the object, so the scene is where it is read from. The
        /// analyzer used to assume sixteen elements, and divided bytes by 1000 against a budget of 11264
        /// bytes quoted as 11 KB.
        /// </summary>
        [Test]
        public void Bandwidth_ReadsArrayLengthFromTheSceneAndQuotesKibibytes()
        {
            var behaviour = Attach<DiagArrayPayloadFixture>();

            WorldAnalysisResult result = new WorldAnalyzer().AnalyzeBehaviours(new UdonSharpBehaviour[] { behaviour });
            BehaviourAnalysisResult analysis = result.BehaviourResults.Single();

            Assert.That(analysis.MaxTotalBytes, Is.EqualTo(4 + 255 * 4),
                "255 ints plus the array's length prefix, read from the instance.");
            Assert.That(result.TotalEstimatedBandwidthKBps, Is.EqualTo(1f).Within(0.0001f),
                "1024 bytes once a second is 1 KB/s against an 11 KB/s budget measured in 1024-byte kilobytes.");
        }

        /// <summary>
        /// With no instance to read, an array's length is unknown: it is counted as its framing and the
        /// total is flagged as a floor rather than padded with a guess.
        /// </summary>
        [Test]
        public void Bandwidth_ArrayWithNoKnownLength_IsALowerBound()
        {
            BehaviourAnalysisResult analysis = new BehaviourAnalyzer().Analyze(typeof(DiagArrayPayloadFixture));
            FieldAnalysisResult field = analysis.Fields.Single();

            Assert.That(field.Size.MaxBytes, Is.EqualTo(4), "Only the length prefix is certain.");
            Assert.That(field.IsLowerBound, Is.True);
            Assert.That(analysis.Violations.Any(violation => violation.Message.Contains("LOWER BOUND")), Is.True,
                "The total has to say it is a floor:\n" + string.Join("\n", analysis.Violations.Select(v => v.Message)));
        }

        /// <summary>
        /// Type.GetFields on the subclass leaves out its base's private fields; the emitter does not, so the
        /// subclass's payload was under-counted by everything the base declared privately.
        /// </summary>
        [Test]
        public void Bandwidth_CountsPrivateSyncedFieldsOfBaseClasses()
        {
            BehaviourAnalysisResult analysis = new BehaviourAnalyzer().Analyze(typeof(DiagDerivedPayloadFixture));

            Assert.That(analysis.Fields.Select(field => field.FieldName), Does.Contain("_health"));
        }

        /// <summary>
        /// WorldAnalyzer copies each behaviour's errors to the world list, and the validator also walked
        /// the behaviour list, so one oversized payload was two errors.
        /// </summary>
        [Test]
        public void BandwidthValidator_ReportsAnOversizedPayloadOnce()
        {
            var behaviour = Attach<DiagOversizedContinuousFixture>();

            List<ValidationIssue> issues = new BandwidthValidator()
                .Validate(new ValidationContext { Behaviours = new List<UdonSharpBehaviour> { behaviour } })
                .ToList();

            List<ValidationIssue> errors = issues.Where(issue => issue.Severity == ValidationSeverity.Error).ToList();

            Assert.That(errors, Has.Count.EqualTo(1),
                "Thirteen quaternions are 208 bytes, over the Continuous limit once:\n" +
                string.Join("\n", issues.Select(issue => issue.Severity + " " + issue.Message)));
            Assert.That(errors[0].RelatedType, Is.EqualTo(typeof(DiagOversizedContinuousFixture)));
        }

        // ------------------------------------------------------------------------------------------
        // World Validator -- source and scene checks
        // ------------------------------------------------------------------------------------------

        /// <summary>
        /// A base class's private synced array is null on the subclass in the scene. Reflection over the
        /// subclass never saw it, and the regex looked for its initializer in the subclass's file; the
        /// report belongs to the type that declares the field.
        /// </summary>
        [Test]
        public void UninitializedSyncedArrayValidator_ReportsAnInheritedNullArrayAgainstItsDeclaringType()
        {
            var behaviour = Attach<DiagSyncedArrayDerivedFixture>();

            List<ValidationIssue> issues = new UninitializedSyncedArrayValidator()
                .Validate(new ValidationContext { Behaviours = new List<UdonSharpBehaviour> { behaviour } })
                .ToList();

            Assert.That(issues, Has.Count.EqualTo(1));
            Assert.That(issues[0].RelatedType, Is.EqualTo(typeof(DiagSyncedArrayBaseFixture)));
            Assert.That(issues[0].RelatedField.Name, Is.EqualTo("_slots"));

            behaviour.Slots = new int[4];

            Assert.That(new UninitializedSyncedArrayValidator()
                    .Validate(new ValidationContext { Behaviours = new List<UdonSharpBehaviour> { behaviour } }),
                Is.Empty, "An array that exists is not reported, however it got there.");
        }

        /// <summary>
        /// An expression-bodied Update has no braces, and the body scan walked into the next method and
        /// reported Start's cached lookup as a call made in Update. The Update overload next to it made
        /// Type.GetMethod throw AmbiguousMatchException, which ended the whole validator.
        /// </summary>
        [Test]
        public void GetComponentValidator_ReadsOnlyTheUpdateBody()
        {
            var validator = new GetComponentInUpdateValidator();

            List<ValidationIssue> issues = validator.ValidateSource(typeof(DiagUpdateOverloadFixture), @"
public class DiagUpdateOverloadFixture : UdonSharpBehaviour
{
    public Renderer cached;
    private void Update() => Tick();
    private void Update(float deltaTime) { }
    private void Start() { cached = GetComponent<Renderer>(); }
    private void Tick() { }
}");

            Assert.That(issues, Is.Empty, "Update calls Tick; the GetComponent is in Start.");

            issues = validator.ValidateSource(typeof(DiagUpdateOverloadFixture), @"
public class DiagUpdateOverloadFixture : UdonSharpBehaviour
{
    private void Update() { GetComponent<Renderer>().enabled = true; }
}");

            Assert.That(issues, Has.Count.EqualTo(1));
        }

        /// <summary>
        /// Two reports on correct OnPlayerLeft code: a local copy of the player read as "storing it in a
        /// field", and any use of the player after SendCustomEvent read as "after an async operation" --
        /// SendCustomEvent runs its handler before it returns. Storing the player in a real field is still
        /// reported.
        /// </summary>
        [Test]
        public void PlayerApiValidator_ReportsOnlyStoresIntoFields()
        {
            var validator = new PlayerApiAfterLeaveValidator();

            List<ValidationIssue> issues = validator.ValidateSource(typeof(DiagPlayerLeftFixture), @"
public override void OnPlayerLeft(VRCPlayerApi player)
{
    VRCPlayerApi leaving = player;
    SendCustomEvent(nameof(Refresh));
    Debug.Log(leaving.displayName + player.displayName);
}");

            Assert.That(issues, Is.Empty, string.Join("\n", issues.Select(issue => issue.Message)));

            issues = validator.ValidateSource(typeof(DiagPlayerLeftFixture), @"
public override void OnPlayerLeft(VRCPlayerApi player)
{
    lastLeft = player;
}");

            Assert.That(issues, Has.Count.EqualTo(1));
            Assert.That(issues[0].Message, Does.Contain("lastLeft"));
        }

        /// <summary>
        /// A network event sent to another behaviour runs there; this type having a [LocalOnly] method of
        /// the same name is a coincidence, and was reported as an error.
        /// </summary>
        [Test]
        public void LocalOnlyValidator_IgnoresEventsSentToOtherBehaviours()
        {
            var validator = new LocalOnlyNetworkCallValidator();

            Assert.That(validator.ValidateSource(typeof(DiagLocalOnlyFixture),
                    "other.SendCustomNetworkEvent(NetworkEventTarget.All, \"Refresh\");"),
                Is.Empty);

            Assert.That(validator.ValidateSource(typeof(DiagLocalOnlyFixture),
                    "SendCustomNetworkEvent(NetworkEventTarget.All, nameof(Refresh));"),
                Has.Count.EqualTo(1));

            Assert.That(validator.ValidateSource(typeof(DiagLocalOnlyFixture),
                    "this.SendCustomNetworkEvent(NetworkEventTarget.All, \"Refresh\");"),
                Has.Count.EqualTo(1));
        }

        /// <summary>
        /// A position moved every frame through '.x' was recorded only at its initializer, and reported as
        /// "only set during initialization".
        /// </summary>
        [Test]
        public void SyncModeValidator_SeesWritesThroughAStructMember()
        {
            var validator = new SyncModeValidator();

            List<ValidationIssue> issues = validator.ValidateSource(typeof(DiagContinuousPositionFixture), @"
using UdonSharp;
using UnityEngine;

public class DiagContinuousPositionFixture : UdonSharpBehaviour
{
    [UdonSynced] public Vector3 position = Vector3.zero;

    private void Update()
    {
        position.x += Time.deltaTime;
    }
}");

            Assert.That(issues, Is.Empty, string.Join("\n", issues.Select(issue => issue.Message)));

            issues = validator.ValidateSource(typeof(DiagContinuousPositionFixture), @"
using UdonSharp;
using UnityEngine;

public class DiagContinuousPositionFixture : UdonSharpBehaviour
{
    [UdonSynced] public Vector3 position = Vector3.zero;

    private void Start()
    {
        position.x = 1f;
    }
}");

            Assert.That(issues.Any(issue => issue.Message.Contains("only set during initialization")), Is.True,
                "Written only in Start and its initializer, the field never changes after setup.");
        }

        // ------------------------------------------------------------------------------------------
        // Fixture types for the reflection-driven tools
        // ------------------------------------------------------------------------------------------

        [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
        private class DiagQuantizedFixture : UdonSharpBehaviour
        {
            [Sync(Authority.Owner, Quantize = 0.01f)] public Quaternion rotation;
        }

        [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
        private class DiagArrayPayloadFixture : UdonSharpBehaviour
        {
            [UdonSynced] public int[] values = new int[255];
        }

        [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
        private class DiagPayloadBaseFixture : UdonSharpBehaviour
        {
            [UdonSynced] private int _health;

            public int Health
            {
                get => _health;
                set => _health = value;
            }
        }

        [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
        private class DiagDerivedPayloadFixture : DiagPayloadBaseFixture
        {
            [UdonSynced] public int armor;
        }

        [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
        private class DiagOversizedContinuousFixture : UdonSharpBehaviour
        {
            [UdonSynced] public Quaternion q0, q1, q2, q3, q4, q5, q6, q7, q8, q9, q10, q11, q12;
        }

        [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
        private class DiagSyncedArrayBaseFixture : UdonSharpBehaviour
        {
            [UdonSynced] private int[] _slots;

            public int[] Slots
            {
                get => _slots;
                set => _slots = value;
            }
        }

        private class DiagSyncedArrayDerivedFixture : DiagSyncedArrayBaseFixture
        {
        }

        // The source validators read a type only for its method and field names, so these three are plain
        // classes: nothing about them needs to be a component, and an Update overload on a real
        // MonoBehaviour is a signature Unity would have opinions about.

        private class DiagUpdateOverloadFixture
        {
            private void Update()
            {
            }

            private void Update(float deltaTime)
            {
            }

            private void Start()
            {
            }
        }

        private class DiagPlayerLeftFixture
        {
            public VRCPlayerApi lastLeft;

            public void OnPlayerLeft(VRCPlayerApi player)
            {
            }

            public void OnPlayerLeft(int playerId)
            {
            }
        }

        private class DiagLocalOnlyFixture
        {
            [LocalOnly]
            public void Refresh()
            {
            }
        }

        [UdonBehaviourSyncMode(BehaviourSyncMode.Continuous)]
        private class DiagContinuousPositionFixture : UdonSharpBehaviour
        {
            [UdonSynced] public Vector3 position = Vector3.zero;
        }

        // ------------------------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------------------------

        private static IReadOnlyList<AnalyzerDiagnostic> Run(string analyzerTypeName, string source, string behaviourName)
        {
            return CENetCodegenRegressionTests.RunAnalyzer(analyzerTypeName, source, behaviourName);
        }

        private T Attach<T>() where T : UdonSharpBehaviour
        {
            var host = new GameObject(typeof(T).Name);
            _hosts.Add(host);
            return host.AddComponent<T>();
        }

        private static AnalyzerDiagnostic AssertSingle(
            IReadOnlyList<AnalyzerDiagnostic> diagnostics,
            string code,
            string because)
        {
            AnalyzerDiagnostic[] matches = diagnostics.Where(diagnostic => diagnostic.Code == code).ToArray();

            Assert.That(matches.Length, Is.EqualTo(1),
                because + $"\nExpected exactly one {code}.\n" + Render(diagnostics));

            return matches[0];
        }

        private static void AssertNone(IReadOnlyList<AnalyzerDiagnostic> diagnostics, string code, string because)
        {
            Assert.That(diagnostics.Select(diagnostic => diagnostic.Code), Does.Not.Contain(code),
                because + "\n" + Render(diagnostics));
        }

        private static string Render(IReadOnlyList<AnalyzerDiagnostic> diagnostics)
        {
            if (diagnostics.Count == 0)
                return "Diagnostics: (none)";

            return "Diagnostics:\n" + string.Join("\n",
                diagnostics.Select(diagnostic => $"  [{diagnostic.Severity}] {diagnostic.GetFormattedMessage()}"));
        }
    }
}
