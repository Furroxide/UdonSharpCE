using UdonSharp;
using UnityEngine;
using VRC.Udon;

namespace CEShowcase.SyncGroups
{
    /// <summary>
    /// Makes per-group sync visible in world: for each <c>[SyncOnJoin]</c> group on a target behaviour it
    /// shows the payload's length, a short prefix, whether that group has been applied on this client, and
    /// how many times the payload string has changed since this client loaded.
    ///
    /// The change counters are the actual test. Pressing "bump hot" ten times must advance the hot
    /// counter ten times and leave the cold counter where it was, with the cold length and prefix
    /// unchanged. If cold tracks hot, the groups are not isolated.
    /// </summary>
    /// <remarks>
    /// It reads the generated transport through <c>UdonBehaviour.GetProgramVariable</c> because there is
    /// no other way in: CE's glue is emitted into the Udon program only, so no source-level reference to
    /// <c>_CE_SyncOnJoinPayload_cold</c> would compile. Reading a generated, non-public heap symbol by
    /// name from U# is the same thing UdonSharp's own GetComponentShim does for its type-id symbol.
    ///
    /// Sync mode None: this is a local display, it holds no shared state, and every client computes it
    /// from the target's own synced variables.
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class SyncGroupReadout : UdonSharpBehaviour
    {
        [Header("Target")]
        [Tooltip("The UdonBehaviour carrying the [SyncOnJoin] groups (the hidden component, not the proxy)")]
        [SerializeField] private UdonBehaviour _targetBehaviour;

        [Header("Display")]
        [SerializeField] private TMPro.TextMeshProUGUI _readoutText;

        [Tooltip("Seconds between refreshes. This polls, so keep it slow enough to be free.")]
        [SerializeField] private float _refreshSeconds = 0.25f;

        /// <summary>
        /// Group id suffixes to display. Empty string is the default (ungrouped) payload, which keeps the
        /// unsuffixed member names.
        /// </summary>
        private string[] _groupSuffixes;

        private string[] _lastPayloads;
        private int[] _changeCounts;
        private float _nextRefresh;

        void Start()
        {
            _groupSuffixes = new string[3];
            _groupSuffixes[0] = "";
            _groupSuffixes[1] = "_cold";
            _groupSuffixes[2] = "_hot";

            _lastPayloads = new string[_groupSuffixes.Length];
            _changeCounts = new int[_groupSuffixes.Length];

            // Seed from whatever has already arrived, so a late joiner's first observation is not counted
            // as a change. Otherwise every client starts at 1 and the counters cannot be compared.
            for (int i = 0; i < _groupSuffixes.Length; i++)
                _lastPayloads[i] = ReadPayload(i);

            Refresh();
        }

        void Update()
        {
            if (Time.time < _nextRefresh)
                return;

            _nextRefresh = Time.time + _refreshSeconds;
            Refresh();
        }

        private void Refresh()
        {
            if (_readoutText == null)
                return;

            if (_targetBehaviour == null)
            {
                _readoutText.text = "SyncGroupReadout: no target assigned";
                return;
            }

            string text = "[SyncOnJoin] groups\n";

            for (int i = 0; i < _groupSuffixes.Length; i++)
            {
                string payload = ReadPayload(i);

                if (payload != _lastPayloads[i])
                {
                    _changeCounts[i] = _changeCounts[i] + 1;
                    _lastPayloads[i] = payload;
                }

                text += GroupName(i) + "  len " + payload.Length +
                        "  chg " + _changeCounts[i] +
                        (IsApplied(i) ? "  applied" : "  --") + "\n" +
                        "   " + Prefix(payload) + "\n";
            }

            text += "\ncold v" + ReadInt("coldVersion") + "   hot " + ReadInt("hotCounter");

            _readoutText.text = text;
        }

        private string GroupName(int index)
        {
            string suffix = _groupSuffixes[index];
            return suffix.Length == 0 ? "default" : suffix.Substring(1);
        }

        private string ReadPayload(int index)
        {
            object value = _targetBehaviour == null
                ? null
                : _targetBehaviour.GetProgramVariable("_CE_SyncOnJoinPayload" + _groupSuffixes[index]);

            return value == null ? "" : (string)value;
        }

        /// <summary>
        /// Whether this client has committed that group's payload. The guard is set only after every field
        /// in the group decoded, so "applied" genuinely means the state landed.
        /// </summary>
        private bool IsApplied(int index)
        {
            object value = _targetBehaviour == null
                ? null
                : _targetBehaviour.GetProgramVariable("_CE_SyncOnJoinApplied" + _groupSuffixes[index]);

            if (value == null)
                return false;

            string applied = (string)value;
            return applied.Length > 0 && applied == _lastPayloads[index];
        }

        private int ReadInt(string symbolName)
        {
            object value = _targetBehaviour == null ? null : _targetBehaviour.GetProgramVariable(symbolName);
            return value == null ? 0 : (int)value;
        }

        private string Prefix(string payload)
        {
            if (payload.Length == 0)
                return "(empty)";

            return payload.Length <= 28 ? payload : payload.Substring(0, 28) + "...";
        }
    }
}
