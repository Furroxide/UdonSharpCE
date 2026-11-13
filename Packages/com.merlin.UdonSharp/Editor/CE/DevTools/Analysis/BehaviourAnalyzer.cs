using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UdonSharp;
using UdonSharp.CE.Net;
using UdonSharpEditor;
using UnityEngine;
using VRC.Udon;

namespace UdonSharp.CE.Editor.DevTools.Analysis
{
    /// <summary>
    /// Analyzes UdonSharpBehaviour types for network bandwidth usage.
    /// Calculates totals, checks limits, and generates optimization recommendations.
    /// </summary>
    public class BehaviourAnalyzer
    {
        private readonly FieldSizeCalculator _sizeCalculator = new FieldSizeCalculator();

        #region Constants

        /// <summary>
        /// Maximum bytes for continuous sync mode.
        /// </summary>
        public const int CONTINUOUS_SYNC_LIMIT = NetworkLimits.ContinuousSyncMaxBytes;

        /// <summary>
        /// Warning threshold as percentage of continuous limit.
        /// </summary>
        public const float WARNING_THRESHOLD = NetworkLimits.WarningThreshold;

        /// <summary>
        /// Sync updates per second for continuous mode.
        /// </summary>
        public const int CONTINUOUS_SYNCS_PER_SECOND = 5;

        /// <summary>
        /// Estimated sync updates per second for manual mode.
        /// </summary>
        public const int MANUAL_SYNCS_PER_SECOND = 1;

        /// <summary>
        /// Bytes in the kilobyte every KB/s figure here is quoted in. The budget is 11264 bytes, which is
        /// 11 KB only at 1024 to the kilobyte, so bandwidth has to be divided by the same number or a
        /// behaviour sitting exactly on the budget reads as 2.4% over it.
        /// </summary>
        public const float BYTES_PER_KILOBYTE = 1024f;

        /// <summary>
        /// Network budget in KB/s.
        /// </summary>
        public const float NETWORK_BUDGET_KBPS = NetworkLimits.NetworkBudgetBytesPerSecond / BYTES_PER_KILOBYTE;

        /// <summary>
        /// Bytes of framing a synced string or array always costs on top of its contents (the serialized
        /// length prefix). For an unbounded string this is the only part of the cost that is knowable at
        /// compile time.
        /// </summary>
        public const int FRAMING_BYTES = FieldSizeCalculator.FRAMING_BYTES;

        #endregion

        #region Public Methods

        /// <summary>
        /// Analyzes a behaviour type for bandwidth usage.
        /// </summary>
        /// <param name="behaviourType">The type to analyze.</param>
        /// <returns>Analysis result with fields, totals, violations, and recommendations.</returns>
        public BehaviourAnalysisResult Analyze(Type behaviourType)
        {
            return Analyze(behaviourType, null);
        }

        /// <summary>
        /// Analyzes a behaviour type for bandwidth usage against a specific scene instance.
        /// </summary>
        /// <param name="behaviourType">The type to analyze.</param>
        /// <param name="instance">
        /// The UdonBehaviour that runs this type, when one exists. Required to get the sync mode right for an
        /// Any program: that mode is chosen per instance in the inspector, not by the type.
        /// </param>
        /// <returns>Analysis result with fields, totals, violations, and recommendations.</returns>
        public BehaviourAnalysisResult Analyze(Type behaviourType, UdonBehaviour instance)
        {
            return Analyze(behaviourType, instance, null);
        }

        /// <summary>
        /// Analyzes a behaviour type for bandwidth usage, sizing its synced arrays from the behaviours
        /// that actually sit in the scene.
        /// </summary>
        /// <param name="behaviourType">The type to analyze.</param>
        /// <param name="instance">The UdonBehaviour that runs this type, when one exists.</param>
        /// <param name="sceneInstances">
        /// The scene's behaviours of this type, or null. An array's length is a property of the object, not
        /// of the type, so this is the only place a real length can come from: without it every synced
        /// array is reported as a floor of its framing bytes.
        /// </param>
        /// <returns>Analysis result with fields, totals, violations, and recommendations.</returns>
        public BehaviourAnalysisResult Analyze(
            Type behaviourType,
            UdonBehaviour instance,
            IReadOnlyList<UdonSharpBehaviour> sceneInstances)
        {
            if (behaviourType == null)
                throw new ArgumentNullException(nameof(behaviourType));

            var result = new BehaviourAnalysisResult
            {
                BehaviourType = behaviourType,
                SyncMode = ResolveSyncMode(behaviourType, instance)
            };

            // Find all synced fields
            foreach (var field in GetInstanceFieldsIncludingBases(behaviourType))
            {
                if (!IsSyncedField(field))
                    continue;

                var fieldResult = AnalyzeField(field, sceneInstances);
                result.Fields.Add(fieldResult);
            }

            // Calculate totals
            result.MinTotalBytes = result.Fields.Sum(f => f.Size.MinBytes);
            result.MaxTotalBytes = result.Fields.Sum(f => f.Size.MaxBytes);

            // Check continuous sync limit
            if (result.SyncMode == "Continuous")
            {
                result.ExceedsContinuousLimit = result.MaxTotalBytes > CONTINUOUS_SYNC_LIMIT;
            }

            // Estimate bandwidth
            result.EstimatedBandwidthKBps = EstimateBandwidth(result);

            // Generate violations and recommendations
            CheckViolations(result);
            GenerateRecommendations(result);

            return result;
        }

        /// <summary>
        /// Analyzes a single field.
        /// </summary>
        public FieldAnalysisResult AnalyzeField(FieldInfo field)
        {
            return AnalyzeField(field, null);
        }

        /// <summary>
        /// Analyzes a single field, reading an array's length off the given scene instances.
        /// </summary>
        public FieldAnalysisResult AnalyzeField(FieldInfo field, IReadOnlyList<UdonSharpBehaviour> sceneInstances)
        {
            var result = new FieldAnalysisResult
            {
                Field = field,
                TypeName = field.FieldType.Name,
                IsSynced = IsSyncedField(field),
                IsArray = field.FieldType.IsArray
            };

            int instanceArrayLength = result.IsArray ? GetLongestInstanceArray(field, sceneInstances) : -1;

            // Calculate size
            result.Size = CalculateSyncedFieldSize(field, instanceArrayLength);

            // Check for CE.Net [Sync] attribute properties
            var syncAttr = field.GetCustomAttribute<SyncAttribute>();
            if (syncAttr != null)
            {
                result.HasQuantization = syncAttr.Quantize > 0;
                result.QuantizeValue = syncAttr.Quantize;
                result.HasDeltaEncoding = syncAttr.DeltaEncodeHint;

                // Named, never subtracted. See DescribeTuningOptions for why the size is left alone.
                if (result.HasQuantization || result.HasDeltaEncoding)
                {
                    result.Notes = DescribeTuningOptions(result);
                }
            }

            // Try to get array length
            if (result.IsArray)
            {
                result.ArrayLength = instanceArrayLength > 0 ? instanceArrayLength : TryGetArrayLength(field);
            }

            return result;
        }

        /// <summary>
        /// Every instance field a behaviour carries, including the private ones its base classes declare.
        ///
        /// <c>Type.GetFields</c> on the derived type returns inherited public and protected fields and
        /// silently leaves out a base class's private ones. The emitter does not: a
        /// '[UdonSynced] private int _health;' on a shared entity base is serialized by every subclass, and
        /// since the abstract base is never in a scene itself, a tool reading only GetFields counted those
        /// bytes nowhere at all.
        /// </summary>
        public static List<FieldInfo> GetInstanceFieldsIncludingBases(Type behaviourType)
        {
            var fields = new List<FieldInfo>();

            for (Type current = behaviourType;
                 current != null && current != typeof(UdonSharpBehaviour) && current != typeof(object);
                 current = current.BaseType)
            {
                fields.AddRange(current.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
            }

            return fields;
        }

        #endregion

        #region Sync Mode Resolution

        /// <summary>
        /// Resolves the sync mode a behaviour type will run with when no scene instance is available.
        ///
        /// Every CE tool that reports a sync mode goes through here so they cannot drift apart, and so the
        /// two ways of getting this wrong stay fixed in one place: an absent attribute is
        /// <see cref="BehaviourSyncMode.Any"/>, not <see cref="BehaviourSyncMode.None"/>, and reading absent
        /// as "None" hid exactly the behaviours most likely to blow the ~200 byte continuous budget.
        ///
        /// For a program whose mode is Any the Continuous answer is an ASSUMPTION, not a fact --
        /// <see cref="DescribeSyncModeSource(Type)"/> says so, and callers that have the actual
        /// <see cref="UdonBehaviour"/> must use <see cref="ResolveSyncMode(UdonSharpBehaviour)"/> instead.
        /// </summary>
        public static string ResolveSyncMode(Type behaviourType)
        {
            return ResolveSyncMode(behaviourType, null);
        }

        /// <summary>
        /// Resolves the sync mode a specific behaviour instance actually runs with.
        ///
        /// This is the only correct answer for an Any program. UdonSharpEditorManager.UpdateSyncModes
        /// explicitly SKIPS Any (and NoVariableSync) when it reconciles <c>UdonBehaviour.SyncMethod</c>, and
        /// UdonSharpGUI draws a per-instance "Sync Method" dropdown, so two instances of the same Any
        /// behaviour can be running Manual and Continuous side by side. Answering from the type alone
        /// reported Continuous for both and silently mis-sized every payload budget built on top of it.
        /// </summary>
        public static string ResolveSyncMode(UdonSharpBehaviour behaviour)
        {
            if (behaviour == null)
                return BehaviourSyncMode.Continuous.ToString();

            return ResolveSyncMode(behaviour.GetType(), UdonSharpEditorUtility.GetBackingUdonBehaviour(behaviour));
        }

        /// <summary>
        /// Resolves the sync mode from a type plus, when one exists, the UdonBehaviour that runs it.
        /// A null <paramref name="instance"/> falls back to the type and the result is an assumption.
        /// </summary>
        public static string ResolveSyncMode(Type behaviourType, UdonBehaviour instance)
        {
            BehaviourSyncMode declaredMode = GetDeclaredSyncMode(behaviourType);

            // Anything other than Any is forced onto every instance: UpdateSyncModes writes
            // Continuous/Manual/None straight onto UdonBehaviour.SyncMethod. NoVariableSync is skipped there
            // only because SyncMethod cannot express it -- variables still never replicate, so the program's
            // answer is the real one either way.
            if (declaredMode != BehaviourSyncMode.Any)
                return declaredMode.ToString();

            if (instance == null)
                return BehaviourSyncMode.Continuous.ToString();

            switch (instance.SyncMethod)
            {
                case VRC.SDKBase.Networking.SyncType.Manual:
                    return BehaviourSyncMode.Manual.ToString();
                case VRC.SDKBase.Networking.SyncType.None:
                    return BehaviourSyncMode.None.ToString();
                default:
                    return BehaviourSyncMode.Continuous.ToString();
            }
        }

        /// <summary>
        /// Describes where <see cref="ResolveSyncMode(Type)"/> got its answer, or null when the behaviour
        /// declares a mode outright. Callers surface this so a mode the author chose is never displayed as
        /// though it were identical to one CE only assumed.
        /// </summary>
        public static string DescribeSyncModeSource(Type behaviourType)
        {
            return DescribeSyncModeSource(behaviourType, null);
        }

        /// <summary>
        /// As <see cref="DescribeSyncModeSource(Type)"/>, but reports the instance's own Sync Method when the
        /// program is Any and an UdonBehaviour is available -- that value is a fact, not an assumption.
        /// </summary>
        public static string DescribeSyncModeSource(Type behaviourType, UdonBehaviour instance)
        {
            if (behaviourType == null)
                return null;

            if (GetDeclaredSyncMode(behaviourType) != BehaviourSyncMode.Any)
                return null;

            if (instance != null)
                return $"program is Any; this UdonBehaviour's Sync Method is {instance.SyncMethod}";

            bool declaredAny = behaviourType.GetCustomAttribute<UdonBehaviourSyncModeAttribute>(true) != null;

            return declaredAny
                ? "declared Any; ASSUMED Continuous -- each UdonBehaviour's own Sync Method dropdown decides"
                : "no [UdonBehaviourSyncMode]; ASSUMED Continuous -- each UdonBehaviour's own Sync Method dropdown decides";
        }

        /// <summary>
        /// Whether the mode transmits synced variables at all. None disables networking entirely and
        /// NoVariableSync keeps events but drops variable sync, so a behaviour in either mode costs zero
        /// bandwidth no matter how many [UdonSynced] fields it declares.
        /// </summary>
        public static bool ModeSyncsVariables(string syncMode)
        {
            return syncMode != BehaviourSyncMode.None.ToString() &&
                   syncMode != BehaviourSyncMode.NoVariableSync.ToString();
        }

        /// <summary>
        /// The mode the compiled program declares, before any per-instance override.
        ///
        /// CE injects [UdonBehaviourSyncMode] at compile time and UdonSharpCompilerV1 copies the result onto
        /// the program asset, so the asset -- not the source attribute -- is what the built program and every
        /// scene UdonBehaviour actually run with. A still-Any asset means it has not been compiled since the
        /// mode was decided, so fall through to the source instead of trusting it.
        /// </summary>
        private static BehaviourSyncMode GetDeclaredSyncMode(Type behaviourType)
        {
            if (behaviourType == null)
                return BehaviourSyncMode.Any;

            UdonSharpProgramAsset programAsset = UdonSharpEditorUtility.GetUdonSharpProgramAsset(behaviourType);
            if (programAsset != null && programAsset.behaviourSyncMode != BehaviourSyncMode.Any)
                return programAsset.behaviourSyncMode;

            var syncModeAttribute = behaviourType.GetCustomAttribute<UdonBehaviourSyncModeAttribute>(true);

            return syncModeAttribute?.behaviourSyncMode ?? BehaviourSyncMode.Any;
        }

        #endregion

        #region Private Methods

        private bool IsSyncedField(FieldInfo field)
        {
            return field.GetCustomAttribute<UdonSyncedAttribute>(true) != null;
        }

        /// <summary>
        /// Names the [Sync] tuning options a field carries, without touching its size.
        ///
        /// This used to subtract. A quantized field lost 30-75% of its bytes and a field with
        /// DeltaEncodeHint lost half, on the reasoning that those options make the payload smaller. Neither
        /// does: Quantize rounds the value in a generated OnPreSerialization and the field goes out at its
        /// full width, and DeltaEncodeHint is an advisory flag VRChat has no wire format for. The discount
        /// was applied before the Continuous limit was checked, so fourteen quantized quaternions -- 224
        /// bytes on the wire -- were reported as 112 and passed.
        /// </summary>
        private static string DescribeTuningOptions(FieldAnalysisResult result)
        {
            var notes = new List<string>();

            if (result.HasQuantization)
                notes.Add($"Quantize {result.QuantizeValue} (rounds the value; bytes sent are unchanged)");

            if (result.HasDeltaEncoding)
                notes.Add("DeltaEncodeHint (advisory; the whole field is still sent)");

            return string.Join("; ", notes);
        }

        /// <summary>
        /// Sizes one synced field.
        ///
        /// Strings are sized here rather than by <see cref="FieldSizeCalculator"/> because that path invents
        /// a 50-character default which VRChat publishes nowhere -- pinning every string to it under-reported
        /// string-heavy behaviours by orders of magnitude -- and because [MaxLength] does not bound a synced
        /// string at all. That attribute lives in UdonSharp.CE.Persistence and is only read by CEPersistence
        /// when it writes PlayerData, so on the sync path it is an unenforced hint and is reported as one.
        ///
        /// Arrays are sized from <paramref name="instanceArrayLength"/> when a scene instance supplied one,
        /// because that is the only length there is: reflection over the type cannot see
        /// 'new int[MAX_PLAYERS]', and assuming sixteen elements in its place reported a hundred-element
        /// table and a sixty-thousand-byte buffer as the same small field.
        /// </summary>
        private SizeResult CalculateSyncedFieldSize(FieldInfo field, int instanceArrayLength)
        {
            Type fieldType = field.FieldType;

            if (fieldType == typeof(string))
                return CalculateStringSize(field);

            if (fieldType.IsArray && fieldType.GetElementType() == typeof(string))
                return CalculateStringArraySize(field);

            if (fieldType.IsArray && instanceArrayLength > 0)
            {
                Type elementType = fieldType.GetElementType();
                int elementSize = _sizeCalculator.GetTypeSize(elementType);

                return SizeResult.Fixed(
                    FRAMING_BYTES + elementSize * instanceArrayLength,
                    $"{elementType?.Name}[{instanceArrayLength}] ({elementSize}B x {instanceArrayLength} + " +
                    $"{FRAMING_BYTES}B framing; length read from the scene)");
            }

            return _sizeCalculator.CalculateSize(field);
        }

        /// <summary>
        /// The longest non-empty array any of the given scene instances holds in this field, or -1 when
        /// none of them holds one.
        ///
        /// The longest, because the figure feeds a limit check and one instance over the limit is a
        /// problem whatever the others hold. An empty array does not count as a length: 'new byte[0]' is
        /// how a buffer that is refilled on every send is declared, and calling that a zero-byte field
        /// would be the sixteen-element guess with a different wrong number.
        /// </summary>
        private static int GetLongestInstanceArray(FieldInfo field, IReadOnlyList<UdonSharpBehaviour> sceneInstances)
        {
            int longest = -1;

            if (sceneInstances == null || field.DeclaringType == null)
                return longest;

            foreach (UdonSharpBehaviour sceneInstance in sceneInstances)
            {
                // Unity's null: a destroyed behaviour still in the list has no fields worth reading.
                if (sceneInstance == null || !field.DeclaringType.IsInstanceOfType(sceneInstance))
                    continue;

                if (field.GetValue(sceneInstance) is Array array && array.Length > longest && array.Length > 0)
                    longest = array.Length;
            }

            return longest;
        }

        private SizeResult CalculateStringSize(FieldInfo field)
        {
            int hintedCharacters = GetMaxLengthHint(field);

            if (hintedCharacters <= 0)
            {
                return SizeResult.LowerBound(
                    FRAMING_BYTES,
                    $"string, length unknown at compile time ({FRAMING_BYTES}B framing + " +
                    $"{NetworkLimits.SyncedStringBytesPerCharacter}B per character; only the framing is counted)");
            }

            SizeResult hinted = SizeResult.Variable(
                FRAMING_BYTES,
                FRAMING_BYTES + hintedCharacters * NetworkLimits.SyncedStringBytesPerCharacter,
                $"string, [MaxLength({hintedCharacters})] hint ({FRAMING_BYTES}B framing + {hintedCharacters} x " +
                $"{NetworkLimits.SyncedStringBytesPerCharacter}B; the hint is not enforced on the sync path)");

            // Still a floor: nothing on the sync path stops the string outgrowing the hint.
            hinted.IsLowerBound = true;
            return hinted;
        }

        /// <summary>
        /// [MaxLength] on an array bounds the ELEMENT COUNT, never the characters of an element, so a bound
        /// string[] still has unbounded element contents and only the per-element framing can be counted.
        /// </summary>
        private SizeResult CalculateStringArraySize(FieldInfo field)
        {
            int hintedElements = GetMaxLengthHint(field);

            if (hintedElements <= 0)
            {
                return SizeResult.LowerBound(
                    FRAMING_BYTES,
                    $"string[], element count and element lengths both unknown (only the {FRAMING_BYTES}B array " +
                    "framing is counted)");
            }

            SizeResult hinted = SizeResult.Variable(
                FRAMING_BYTES,
                FRAMING_BYTES + hintedElements * FRAMING_BYTES,
                $"string[], [MaxLength({hintedElements})] hint bounds the element count only ({FRAMING_BYTES}B " +
                $"array framing + {FRAMING_BYTES}B framing per element; element characters unbounded)");

            hinted.IsLowerBound = true;
            return hinted;
        }

        /// <summary>
        /// Whether any synced field's size is not knowable ahead of time, which makes the reported totals
        /// lower bounds instead of estimates. Strings always are; an array is when no scene instance and no
        /// [MaxLength] gave it a length.
        /// </summary>
        private static bool HasLowerBoundFields(BehaviourAnalysisResult result)
        {
            foreach (var field in result.Fields)
            {
                if (field.IsLowerBound)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Reads the [MaxLength] cap off a field. On an array it caps the element count; on a string it caps
        /// characters. Either way it is a persistence-time cap that nothing enforces on the sync path, so
        /// every caller has to present it as a hint rather than as a measured bound.
        /// </summary>
        private static int GetMaxLengthHint(FieldInfo field)
        {
            var maxLengthAttr = field.GetCustomAttribute<CE.Persistence.MaxLengthAttribute>();
            if (maxLengthAttr != null)
            {
                return maxLengthAttr.Length;
            }

            return -1;
        }

        private int TryGetArrayLength(FieldInfo field)
        {
            return GetMaxLengthHint(field);
        }

        private float EstimateBandwidth(BehaviourAnalysisResult result)
        {
            int syncsPerSecond = result.SyncMode switch
            {
                "Continuous" => CONTINUOUS_SYNCS_PER_SECOND,
                "Manual" => MANUAL_SYNCS_PER_SECOND,
                _ => 0
            };

            if (syncsPerSecond == 0)
                return 0f;

            // Use max bytes for conservative estimate
            return (result.MaxTotalBytes * syncsPerSecond) / BYTES_PER_KILOBYTE;
        }

        private void CheckViolations(BehaviourAnalysisResult result)
        {
            // A declared [UdonSynced] field on a behaviour that never transmits variables is dead weight the
            // author almost certainly did not intend; before the Any/None mix-up was fixed this case was
            // indistinguishable from the (very common) undeclared default and so could never be reported.
            if (result.Fields.Count > 0 && !ModeSyncsVariables(result.SyncMode))
            {
                result.Violations.Add(LimitViolation.Warning(
                    $"{result.Fields.Count} [UdonSynced] field(s) on a {result.SyncMode} behaviour never replicate",
                    result.SyncMode == BehaviourSyncMode.None.ToString()
                        ? "BehaviourSyncMode.None disables networking entirely. Remove the synced fields or choose Manual/Continuous."
                        : "BehaviourSyncMode.NoVariableSync keeps network events but drops variable sync. Send the state over an event, or choose Manual/Continuous."
                ));
            }

            // Without this the reported total reads as a measurement. A behaviour syncing one unbounded
            // string counts as 4 bytes here and would otherwise be indistinguishable from one syncing a short
            // fixed record -- the continuous check below can prove a payload too big, but it can never prove
            // one small enough once a field has no compile-time length.
            //
            // The same holds for an array nothing gave a length to: no scene instance holding a non-empty
            // one, and no [MaxLength]. It is counted as its framing and flagged, where it used to be
            // counted as sixteen elements and not flagged at all.
            if (HasLowerBoundFields(result))
            {
                result.Violations.Add(LimitViolation.Warning(
                    $"Payload size for {result.BehaviourType?.Name ?? "this behaviour"} is a LOWER BOUND: " +
                    "at least one synced string or array has no length known ahead of time",
                    result.SyncMode == "Continuous"
                        ? $"Only the {FRAMING_BYTES}B framing of each such field is counted, so a total under " +
                          $"{CONTINUOUS_SYNC_LIMIT} bytes does NOT confirm the payload fits continuous sync. " +
                          "Keep them short by construction, or move to Manual sync."
                        : $"Only the {FRAMING_BYTES}B framing of each such field is counted; a string really costs " +
                          $"{NetworkLimits.SyncedStringBytesPerCharacter}B per character and an array its element " +
                          "size per element, at whatever length they have at runtime."));
            }

            // Check continuous sync limit
            if (result.SyncMode == "Continuous")
            {
                if (result.MaxTotalBytes > CONTINUOUS_SYNC_LIMIT)
                {
                    result.Violations.Add(LimitViolation.Error(
                        $"Continuous sync payload ({result.MaxTotalBytes} bytes) exceeds {CONTINUOUS_SYNC_LIMIT}-byte limit",
                        "Switch to Manual sync mode or reduce synced data"
                    ));
                }
                else if (result.MaxTotalBytes > CONTINUOUS_SYNC_LIMIT * WARNING_THRESHOLD)
                {
                    result.Violations.Add(LimitViolation.Warning(
                        $"Continuous sync payload ({result.MaxTotalBytes} bytes) approaching {CONTINUOUS_SYNC_LIMIT}-byte limit",
                        "Consider Manual sync mode if you need to add more synced fields"
                    ));
                }
            }

            // Check for bandwidth hogs
            if (result.EstimatedBandwidthKBps > NETWORK_BUDGET_KBPS * 0.5f)
            {
                result.Violations.Add(LimitViolation.Warning(
                    $"Behaviour uses {result.EstimatedBandwidthKBps:F1} KB/s ({result.EstimatedBandwidthKBps / NETWORK_BUDGET_KBPS * 100:F0}% of budget)",
                    "Consider reducing sync frequency or data size"
                ));
            }
        }

        private void GenerateRecommendations(BehaviourAnalysisResult result)
        {
            foreach (var field in result.Fields)
            {
                // Large arrays in continuous sync
                if (field.IsArray &&
                    result.SyncMode == "Continuous" &&
                    field.Size.MaxBytes > 50)
                {
                    result.Recommendations.Add(new Recommendation
                    {
                        Field = field.Field,
                        Message = "Large array in continuous sync",
                        // No EstimatedSavings figure, here or on the delta recommendation below. Both used
                        // to quote half the field, and neither had anything behind the number: how much a
                        // rewrite saves depends on how often the array changes, which nothing here can see.
                        Suggestion = "Move to Manual sync and send it only when it changes, or split into multiple behaviours",
                        EstimatedSavings = 0
                    });
                }

                // There is deliberately no "float without quantization" recommendation. It used to promise
                // half the field back for adding Quantize, and Quantize saves no bytes: the value is
                // rounded and then sent at full width. Advice whose only stated benefit does not exist
                // sends authors off to tune fields for nothing.

                // String fields that might be IDs
                if (field.Field.FieldType == typeof(string))
                {
                    string fieldName = field.Field.Name.ToLowerInvariant();
                    if (fieldName.Contains("id") || fieldName.Contains("key") || fieldName.Contains("name"))
                    {
                        // No EstimatedSavings figure: the string's runtime length is unknown, so any number
                        // here would be invented. The saving is stated in terms the reader can check.
                        result.Recommendations.Add(new Recommendation
                        {
                            Field = field.Field,
                            Message = "String field that might be an ID or key",
                            Suggestion = $"An int index is a fixed 4 bytes; this string costs {FRAMING_BYTES}B " +
                                         $"+ {NetworkLimits.SyncedStringBytesPerCharacter}B per character, with " +
                                         "no compile-time bound.",
                            EstimatedSavings = 0
                        });
                    }
                }

                // Large arrays without delta encoding
                if (field.IsArray && !field.HasDeltaEncoding && field.Size.MaxBytes > 100)
                {
                    result.Recommendations.Add(new Recommendation
                    {
                        Field = field.Field,
                        Message = "Large array without a delta-sync strategy",
                        // Named as a property rather than shown as attribute syntax. The old wording spelled
                        // out "[Sync(DeltaEncode = true)]", which no longer compiles now that Authority is a
                        // required positional argument -- and pasting it was never the advice anyway, since
                        // the whole point of the sentence is that the flag changes nothing on the wire.
                        Suggestion = "Use Manual sync and serialize only the changed elements; [Sync]'s DeltaEncodeHint " +
                                     "is an advisory flag read by this report and by CE0013, not a wire format.",
                        EstimatedSavings = 0
                    });
                }
            }

            // Behaviour-level recommendations
            if (result.SyncMode == "Continuous" && result.Fields.Count > 5)
            {
                result.Recommendations.Add(new Recommendation
                {
                    Field = null,
                    Message = "Many fields in continuous sync",
                    Suggestion = "Consider splitting into multiple behaviours to reduce per-behaviour payload",
                    EstimatedSavings = 0
                });
            }
        }

        #endregion
    }
}

