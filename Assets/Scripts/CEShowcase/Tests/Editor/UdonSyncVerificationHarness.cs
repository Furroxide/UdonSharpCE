using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;
using VRC.Udon.Common;
using VRC.Udon.Common.Interfaces;

namespace CEShowcase.Tests.Editor
{
    /// <summary>
    /// Infrastructure for verifying <c>[Sync]</c> and <c>[SyncOnJoin]</c> <b>as Udon</b> rather than as .NET.
    ///
    /// The suite this replaces compiled generated glue with plain Roslyn and ran it as a .NET
    /// <see cref="MonoBehaviour"/>. That proves .NET semantics and nothing else: it never checked that a
    /// field ends up in the emitted program's sync metadata, never checked the resolved behaviour sync
    /// mode, and never moved a value from one Udon heap to another. Every defect that actually shipped --
    /// an unsynced payload field, a synced array on a Continuous behaviour, glue stripped by a failed
    /// generator -- is invisible to that style of test and visible to this one.
    ///
    /// Two facts make an in-editor two-heap round trip possible, and both are asserted by
    /// <c>UdonSyncVerificationTests</c> rather than assumed, because a future SDK change would silently
    /// invert the meaning of every round-trip assertion here:
    ///
    ///  1. <c>SerializedUdonProgramAsset._serializationCache</c> is only populated under
    ///     <c>#if VRC_CLIENT</c>, so outside the client <c>RetrieveProgram()</c> performs a fresh Odin
    ///     deserialization and hands back an <b>independent</b> <see cref="IUdonHeap"/> every call. Two
    ///     UdonBehaviours pointed at the same asset therefore get genuinely separate heaps.
    ///  2. Program signature verification returns true unconditionally outside <c>VRC_CLIENT</c>, so a
    ///     program will load and run in the editor.
    ///
    /// Never use <c>UdonSharpProgramAsset.GetRealProgram()</c> to build the pair: it returns the shared
    /// instance and both "clients" would alias one heap, turning every assertion into a tautology.
    /// </summary>
    internal static class UdonSyncHarness
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Makes Udon programs loadable and executable outside play mode.
        ///
        /// <see cref="UdonManager.Instance"/> cannot simply be touched from an editor test: its getter
        /// calls <c>DontDestroyOnLoad</c>, which throws outside play mode. Pre-seeding the private
        /// <c>_instance</c> field makes the getter return early and never reach that call, while
        /// <c>Awake()</c> still builds the client interface the VM needs.
        /// </summary>
        public sealed class RuntimeBootstrap : IDisposable
        {
            private readonly GameObject _managerObject;
            private readonly bool _seededInstance;
            private static readonly FieldInfo InstanceField = typeof(UdonManager).GetField("_instance", Static);

            public RuntimeBootstrap()
            {
                if (InstanceField == null)
                    throw new InvalidOperationException(
                        "UdonManager._instance no longer exists. The editor-mode Udon bootstrap needs it to " +
                        "avoid DontDestroyOnLoad; re-derive the bootstrap against the current SDK.");

                if (InstanceField.GetValue(null) != null)
                    return;

                _managerObject = new GameObject("CE.UdonRuntimeBootstrap") { hideFlags = HideFlags.HideAndDontSave };
                UdonManager manager = _managerObject.AddComponent<UdonManager>();
                InstanceField.SetValue(null, manager);
                _seededInstance = true;

                typeof(UdonManager).GetMethod("Awake", Instance)?.Invoke(manager, null);
            }

            public void Dispose()
            {
                if (!_seededInstance)
                    return;

                InstanceField.SetValue(null, null);

                if (_managerObject != null)
                    UnityEngine.Object.DestroyImmediate(_managerObject);
            }
        }

        /// <summary>
        /// An owner and a remote client running the same compiled program on two independent heaps.
        /// </summary>
        public sealed class BehaviourPair : IDisposable
        {
            public UdonBehaviour Owner { get; private set; }
            public UdonBehaviour Remote { get; private set; }
            public UdonSharp.UdonSharpProgramAsset ProgramAsset { get; private set; }

            private readonly GameObject _ownerObject;
            private readonly GameObject _remoteObject;

            internal BehaviourPair(UdonSharp.UdonSharpProgramAsset programAsset)
            {
                ProgramAsset = programAsset;

                _ownerObject = new GameObject("CE.SyncHarness.Owner") { hideFlags = HideFlags.HideAndDontSave };
                _remoteObject = new GameObject("CE.SyncHarness.Remote") { hideFlags = HideFlags.HideAndDontSave };

                Owner = Create(_ownerObject, programAsset);
                Remote = Create(_remoteObject, programAsset);
            }

            private static UdonBehaviour Create(GameObject host, UdonSharp.UdonSharpProgramAsset programAsset)
            {
                UdonBehaviour behaviour = host.AddComponent<UdonBehaviour>();
                behaviour.AssignProgramAndVariables(programAsset.SerializedProgramAsset, new UdonVariableTable());

                // The compiler never sets SyncMethod -- UdonSharpEditorManager.UpdateSyncModes does, from
                // programAsset.behaviourSyncMode. Skipping this would silently test Continuous, because an
                // unset _syncMethod lazily resolves to Continuous inside UdonBehaviour.
                behaviour.SyncMethod = ResolveSyncType(programAsset.behaviourSyncMode);

                typeof(UdonBehaviour).GetMethod("InitializeUdonContent", Instance)?.Invoke(behaviour, null);

                // Mark the behaviour as started, because the parameterised event path refuses to run before
                // Start: UdonBehaviour.RunEvent<T0> forwards canRunBeforeStart:false, so
                // OnDeserialization(DeserializationResult) returns immediately on a behaviour that has not
                // started and the program is never entered.
                //
                // Without this the harness reports a total sync failure for every behaviour -- it looks
                // exactly like generated glue that was never wired up, and it is purely an artefact of the
                // test never having run Start. Set it so the harness models a client that is actually live.
                typeof(UdonBehaviour).GetField("_hasDoneStart", Instance)?.SetValue(behaviour, true);

                return behaviour;
            }

            public IUdonProgram ProgramOf(UdonBehaviour behaviour)
            {
                return (IUdonProgram)typeof(UdonBehaviour).GetField("_program", Instance)?.GetValue(behaviour);
            }

            /// <summary>Names of every variable the emitted program will actually sync.</summary>
            public IReadOnlyList<string> SyncedVariableNames()
            {
                IUdonSyncMetadataTable table = Owner.SyncMetadataTable;
                if (table == null)
                    return Array.Empty<string>();

                return table.GetAllSyncMetadata().Select(entry => entry.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();
            }

            public void Dispose()
            {
                if (_ownerObject != null) UnityEngine.Object.DestroyImmediate(_ownerObject);
                if (_remoteObject != null) UnityEngine.Object.DestroyImmediate(_remoteObject);
            }
        }

        public static BehaviourPair CreatePair(UdonSharp.UdonSharpProgramAsset programAsset)
        {
            if (programAsset == null)
                throw new ArgumentNullException(nameof(programAsset));

            if (programAsset.SerializedProgramAsset == null)
                throw new InvalidOperationException(
                    $"'{programAsset.name}' has no SerializedProgramAsset, so it never emitted an Udon program.");

            return new BehaviourPair(programAsset);
        }

        private static Networking.SyncType ResolveSyncType(UdonSharp.BehaviourSyncMode mode)
        {
            switch (mode)
            {
                case UdonSharp.BehaviourSyncMode.Manual: return Networking.SyncType.Manual;
                case UdonSharp.BehaviourSyncMode.Continuous: return Networking.SyncType.Continuous;
                case UdonSharp.BehaviourSyncMode.None: return Networking.SyncType.None;
                case UdonSharp.BehaviourSyncMode.NoVariableSync: return Networking.SyncType.None;

                // 'Any' means the author declared nothing. UdonBehaviour resolves that to Continuous at
                // runtime, so the harness must too or it would test a mode the world never uses.
                default: return Networking.SyncType.Continuous;
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Transports
        // ---------------------------------------------------------------------------------------------

        public interface ITransport
        {
            string Name { get; }

            /// <summary>Moves every synced variable from owner to remote. Returns the encoded byte count.</summary>
            int Send(BehaviourPair pair);
        }

        /// <summary>
        /// Copies each synced variable straight across the heaps, bypassing any codec.
        ///
        /// This is the strict transport: it isolates CE's own behaviour from simulator limitations. A
        /// round trip that fails under the codec but passes here is a ClientSim gap, not a CE bug -- and
        /// keeping that distinction is what stops the suite filling up with tolerated failures.
        /// </summary>
        public sealed class HeapCopyTransport : ITransport
        {
            public string Name => "heap-copy";

            public int Send(BehaviourPair pair)
            {
                int bytes = 0;

                foreach (string name in pair.SyncedVariableNames())
                {
                    object value = pair.Owner.GetProgramVariable(name);

                    // Deep-copy rather than aliasing. Handing the remote the owner's own array reference
                    // would make every subsequent equality assertion compare an object with itself, so the
                    // round trip would pass even if nothing was transmitted -- the exact tautology this
                    // harness exists to avoid. A real serialization always produces a separate instance.
                    pair.Remote.SetProgramVariable(name, Detach(value));
                    bytes += EstimateBytes(value);
                }

                return bytes;
            }

            /// <summary>Copies value semantics across the boundary the way a wire transfer would.</summary>
            private static object Detach(object value)
            {
                if (value is Array array)
                {
                    Array copy = Array.CreateInstance(array.GetType().GetElementType() ?? typeof(object), array.Length);
                    Array.Copy(array, copy, array.Length);
                    return copy;
                }

                // Strings are immutable and every other Udon-syncable type is a value type, so sharing is safe.
                return value;
            }
        }

        /// <summary>
        /// Routes the round trip through ClientSim's own Udon encoder/decoder, which walks the real
        /// <see cref="IUdonSyncMetadataTable"/>. Authoritative about what VRChat would actually carry,
        /// but lossy in documented ways -- see <see cref="KnownCodecLimitations"/>.
        /// </summary>
        public sealed class ClientSimCodecTransport : ITransport
        {
            public string Name => "clientsim-codec";

            private static readonly Type CodecType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => { try { return a.GetType("VRC.SDK3.ClientSim.EncodeDecoders.ClientSimUdonEncodeDecode"); } catch { return null; } })
                .FirstOrDefault(t => t != null);

            public static bool IsAvailable => CodecType != null;

            public int Send(BehaviourPair pair)
            {
                if (!IsAvailable)
                    throw new InvalidOperationException("ClientSim's Udon codec is not present in this project.");

                object codec = Activator.CreateInstance(CodecType);

                CodecType.GetMethod("PreEncode").Invoke(codec, new object[] { pair.Owner });
                object data = CodecType.GetMethod("Encode").Invoke(codec, new object[] { pair.Owner });
                CodecType.GetMethod("PostEncode").Invoke(codec, new object[] { pair.Owner, data });
                CodecType.GetMethod("Decode").Invoke(codec, new object[] { pair.Remote, data });

                int count = 0;
                foreach (string name in pair.SyncedVariableNames())
                    count += EstimateBytes(pair.Owner.GetProgramVariable(name));

                return count;
            }
        }

        /// <summary>
        /// Losses in ClientSim's codec that are simulator artefacts, not CE defects. Documented here so a
        /// codec-only failure can be triaged instead of silently tolerated.
        /// </summary>
        public static readonly string KnownCodecLimitations = string.Join("\n", new[]
        {
            "byteCount is fabricated as data.Count * 4 rather than measured.",
            "Types outside its switch decode to empty tokens: jagged and 2-D arrays, Vector2Int, Vector3Int, Rect, Bounds, DateTime.",
            "char handling is asymmetric between encode and decode.",
            "float is routed through double, so exact float equality can fail.",
        });

        // ---------------------------------------------------------------------------------------------
        // Size accounting
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Conservative on-wire size estimate. Deliberately over- rather than under-counts strings,
        /// because the failure this guards against is a payload that silently exceeds its budget.
        /// </summary>
        public static int EstimateBytes(object value)
        {
            switch (value)
            {
                case null: return 0;
                case string s: return s.Length * UdonSharp.CE.Net.NetworkLimits.SyncedStringBytesPerCharacter + 4;
                case bool _: return 1;
                case byte _: case sbyte _: return 1;
                case short _: case ushort _: case char _: return 2;
                case int _: case uint _: case float _: return 4;
                case long _: case ulong _: case double _: return 8;
                case Vector2 _: return 8;
                case Vector3 _: return 12;
                case Vector4 _: case Quaternion _: case Color _: return 16;
            }

            if (value is Array array)
            {
                int total = 4;
                foreach (object element in array)
                    total += EstimateBytes(element);
                return total;
            }

            return 4;
        }

        /// <summary>The per-mode wire budget a behaviour's full synced state must fit inside.</summary>
        public static int BudgetFor(Networking.SyncType syncMethod)
        {
            return syncMethod == Networking.SyncType.Manual
                ? UdonSharp.CE.Net.NetworkLimits.ManualSyncMaxBytes
                : UdonSharp.CE.Net.NetworkLimits.ContinuousSyncMaxBytes;
        }

        // ---------------------------------------------------------------------------------------------
        // UASM ground truth
        // ---------------------------------------------------------------------------------------------

        /// <summary>
        /// Parses the real <c>.sync &lt;symbol&gt;, &lt;none|linear|smooth&gt;</c> tags emitted by
        /// AssemblyModule.
        ///
        /// This replaces an assertion of the form <c>Does.Contain("sync").IgnoreCase</c>, which passes on
        /// any program containing an <c>OnDeserialization</c> entry point and therefore asserted nothing.
        /// </summary>
        public static readonly System.Text.RegularExpressions.Regex SyncTagPattern =
            new System.Text.RegularExpressions.Regex(
                @"^\s*\.sync\s+(?<symbol>\S+?),\s*(?<mode>none|linear|smooth)\s*$",
                System.Text.RegularExpressions.RegexOptions.Multiline |
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public static Dictionary<string, string> ParseSyncTags(string uasm)
        {
            var tags = new Dictionary<string, string>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(uasm))
                return tags;

            foreach (System.Text.RegularExpressions.Match match in SyncTagPattern.Matches(uasm))
                tags[match.Groups["symbol"].Value] = match.Groups["mode"].Value;

            return tags;
        }
    }
}
