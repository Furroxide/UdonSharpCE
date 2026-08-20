using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common.Interfaces;

namespace RocketLeague
{
    /// <summary>
    /// Simple trigger or interact-based match starter.
    /// Walk into the trigger zone or interact with this object to start/join the match.
    /// </summary>
    // NoVariableSync so a non-master can forward the request over the network. With None, the
    // behaviour could not send network events at all, so a non-master pressing this button got nothing
    // but a local log line and had no way to ask the master.
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class MatchStarter : UdonSharpBehaviour
    {
        [Header("References")]
        public RocketLeagueManager Manager;

        [Header("Settings")]
        [Tooltip("If true, entering the trigger starts/joins the match")]
        public bool UseColliderTrigger = true;

        [Tooltip("If true, interacting with this object starts the match (non-masters ask the master)")]
        public bool UseInteract = true;

        public override void Interact()
        {
            if (!UseInteract)
                return;

            RequestStartOrReset();
        }

        /// <summary>
        /// The SDK's own player-trigger callback, which reports the player rather than a collider.
        /// Replaces an OnTriggerEnter that matched anything on the player layer: that fired on EVERY
        /// client for EVERY player capsule that entered, so with the request now going over the network
        /// a single player walking in produced one broadcast per remote client, i.e. N*N events for N
        /// players milling around the zone. Reacting only to the local player leaves exactly one sender.
        /// </summary>
        public override void OnPlayerTriggerEnter(VRCPlayerApi player)
        {
            if (!UseColliderTrigger)
                return;

            if (player == null || !player.IsValid() || !player.isLocal)
                return;

            RequestStartOrReset();
        }

        /// <summary>
        /// Starts or resets the match locally when we are master, and otherwise asks the master to do it.
        /// </summary>
        private void RequestStartOrReset()
        {
            if (Manager == null)
            {
                Debug.LogWarning("[MatchStarter] No RocketLeagueManager assigned!");
                return;
            }

            if (Networking.IsMaster)
            {
                StartOrResetAsMaster();
                return;
            }

            // Nothing to ask for unless the match is in a state that can actually consume the request.
            // Without this gate every re-entry of the trigger zone mid-match cost the shared ~11 KB/s
            // instance budget a broadcast that the master would only drop again.
            if (!CanStart() && !CanReset())
                return;

            // Broadcast rather than target the owner: whoever owns this object need not be the master,
            // and only the master drives the match, so an Owner-targeted request could dead-end at a
            // client that immediately drops it. Every client runs RequestMatchStart and the master check
            // inside it means exactly one of them acts. Requires NoVariableSync (see above).
            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(RequestMatchStart));
        }

        private bool CanStart()
        {
            return Manager != null && Manager.State == GameState.Lobby;
        }

        private bool CanReset()
        {
            return Manager != null && Manager.State == GameState.GameOver;
        }

        private void StartOrResetAsMaster()
        {
            if (CanStart())
            {
                Debug.Log("[MatchStarter] Master starting match...");
                Manager.StartMatch();
            }
            else if (CanReset())
            {
                Debug.Log("[MatchStarter] Resetting for rematch...");
                Manager.ResetMatch();
            }
        }

        /// <summary>
        /// Runs on every client after a non-master asks for the match to start; only the master acts.
        /// </summary>
        public void RequestMatchStart()
        {
            if (!Networking.IsMaster || Manager == null)
                return;

            StartOrResetAsMaster();
        }

        /// <summary>
        /// Public method to start match via SendCustomEvent. Routed like Interact: a non-master calling
        /// this used to hit the IsMaster guard and silently do nothing, so any UI button wired to it was
        /// dead for everyone but one player.
        /// </summary>
        public void TriggerStartMatch()
        {
            if (Manager == null)
            {
                Debug.LogWarning("[MatchStarter] No RocketLeagueManager assigned!");
                return;
            }

            // Deliberately not gated on CanStart(): unlike the trigger zone, this is an explicit one-shot
            // call, so there is no re-entry storm to suppress and callers that force a restart mid-match
            // keep working exactly as before.
            if (Networking.IsMaster)
            {
                Manager.StartMatch();
                return;
            }

            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(RequestStartMatch));
        }

        /// <summary>
        /// Public method to reset match via SendCustomEvent. Routed like TriggerStartMatch.
        /// </summary>
        public void TriggerResetMatch()
        {
            if (Manager == null)
            {
                Debug.LogWarning("[MatchStarter] No RocketLeagueManager assigned!");
                return;
            }

            // Also ungated on purpose -- see TriggerStartMatch. An "abort match" button has to work from
            // any state, so narrowing this to GameOver would break existing wiring.
            if (Networking.IsMaster)
            {
                Manager.ResetMatch();
                return;
            }

            SendCustomNetworkEvent(NetworkEventTarget.All, nameof(RequestResetMatch));
        }

        /// <summary>Runs on every client; only the master acts. See RequestMatchStart.</summary>
        public void RequestStartMatch()
        {
            if (!Networking.IsMaster || Manager == null)
                return;

            Manager.StartMatch();
        }

        /// <summary>Runs on every client; only the master acts. See RequestMatchStart.</summary>
        public void RequestResetMatch()
        {
            if (!Networking.IsMaster || Manager == null)
                return;

            Manager.ResetMatch();
        }
    }
}

