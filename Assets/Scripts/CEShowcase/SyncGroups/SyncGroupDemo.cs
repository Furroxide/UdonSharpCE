using UdonSharp;
using UdonSharp.CE.Net;
using UnityEngine;
using VRC.SDKBase;

namespace CEShowcase.SyncGroups
{
    /// <summary>
    /// The in-world subject for <c>[SyncOnJoin(Group = ...)]</c>.
    ///
    /// It deliberately mixes the two shapes Group exists to separate: a large snapshot that changes once
    /// in a while, and a small counter that changes constantly. Without groups both share one payload, so
    /// every counter bump re-serializes and re-sends the whole snapshot. With groups they are two
    /// independent <c>[UdonSynced]</c> strings, and bumping the counter leaves the snapshot's string
    /// untouched.
    /// </summary>
    /// <remarks>
    /// Publishing goes through <c>SendCustomEvent("_CE_SyncOnJoin_RequestCapture")</c> rather than
    /// <c>RequestSerialization()</c>, because that is the whole point of the change gate: capture
    /// re-serializes every group but only asks VRChat to serialize when one of them actually changed.
    /// String dispatch is the only way to reach it — generated members are invisible to Unity-compiled
    /// user C#, so <c>_CE_SyncOnJoin_RequestCapture()</c> written literally here would not compile.
    ///
    /// Calling <c>RequestSerialization()</c> instead would also work and would also publish, but it goes
    /// out unconditionally and so proves nothing about the gate.
    /// </remarks>
    public class SyncGroupDemo : UdonSharpBehaviour
    {
        /// <summary>Cold group: big, rewritten only when the snapshot is regenerated.</summary>
        [SyncOnJoin(Group = "cold", Priority = 10)]
        public int[] coldSnapshot = new int[256];

        [SyncOnJoin(Group = "cold", Priority = 20)]
        public int coldVersion;

        /// <summary>Hot group: tiny, rewritten on every press. Bundling this with the snapshot is the cost.</summary>
        [SyncOnJoin(Group = "hot")]
        public int hotCounter;

        /// <summary>No group named, so this rides the default payload alongside the two named ones.</summary>
        [SyncOnJoin]
        public string label = "sync-group-demo";

        /// <summary>Regenerates the cold snapshot. Deterministic, so any client can check the values agree.</summary>
        public void BumpCold()
        {
            if (!TakeOwnership())
                return;

            coldVersion++;

            for (int i = 0; i < coldSnapshot.Length; i++)
                coldSnapshot[i] = (coldVersion * 31 + i * 17) % 1000;

            Publish();
        }

        /// <summary>Moves the hot group only. The cold payload must not change as a result of this.</summary>
        public void BumpHot()
        {
            if (!TakeOwnership())
                return;

            hotCounter++;
            Publish();
        }

        /// <summary>Moves the default (ungrouped) payload, so all three can be told apart in the readout.</summary>
        public void BumpLabel()
        {
            if (!TakeOwnership())
                return;

            label = "label-" + hotCounter + "-" + coldVersion;
            Publish();
        }

        /// <summary>
        /// Publishes without re-sending groups that did not move.
        /// </summary>
        private void Publish()
        {
            SendCustomEvent("_CE_SyncOnJoin_RequestCapture");
        }

        /// <summary>
        /// Capture is owner-only, so a client that has not taken the object first would mutate its fields
        /// locally and publish nothing — which looks exactly like the feature being broken.
        /// </summary>
        private bool TakeOwnership()
        {
            VRCPlayerApi localPlayer = Networking.LocalPlayer;

            if (localPlayer == null)
                return false;

            if (!Networking.IsOwner(localPlayer, gameObject))
                Networking.SetOwner(localPlayer, gameObject);

            return Networking.IsOwner(localPlayer, gameObject);
        }
    }
}
