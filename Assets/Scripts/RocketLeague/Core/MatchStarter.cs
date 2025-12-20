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
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MatchStarter : UdonSharpBehaviour
    {
        [Header("References")]
        public RocketLeagueManager Manager;

        [Header("Settings")]
        [Tooltip("If true, entering the trigger starts/joins the match")]
        public bool UseColliderTrigger = true;

        [Tooltip("If true, interacting with this object starts the match (master only)")]
        public bool UseInteract = true;

        public override void Interact()
        {
            if (!UseInteract)
                return;

            if (Manager == null)
            {
                Debug.LogWarning("[MatchStarter] No RocketLeagueManager assigned!");
                return;
            }

            // Only master can start the match
            if (Networking.IsMaster)
            {
                if (Manager.State == GameState.Lobby)
                {
                    Debug.Log("[MatchStarter] Master starting match...");
                    Manager.StartMatch();
                }
                else if (Manager.State == GameState.GameOver)
                {
                    Debug.Log("[MatchStarter] Resetting for rematch...");
                    Manager.ResetMatch();
                }
            }
            else
            {
                Debug.Log("[MatchStarter] Waiting for master to start the match...");
            }
        }

        private void OnTriggerEnter(Collider other)
        {
            if (!UseColliderTrigger)
                return;

            // Check if a player entered
            if (other == null)
                return;

            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || !player.IsValid())
                return;

            // Check if it's the local player's collider
            // In VRChat, we check if the collider belongs to a player
            if (other.gameObject.layer == 10) // VRChat player layer
            {
                OnPlayerEnterZone();
            }
        }

        private void OnPlayerEnterZone()
        {
            if (Manager == null)
                return;

            // Master can start the match by entering the zone
            if (Networking.IsMaster && Manager.State == GameState.Lobby)
            {
                Debug.Log("[MatchStarter] Master entered start zone, beginning match...");
                Manager.StartMatch();
            }
        }

        /// <summary>
        /// Public method to start match via SendCustomEvent.
        /// </summary>
        public void TriggerStartMatch()
        {
            if (Manager != null && Networking.IsMaster)
            {
                Manager.StartMatch();
            }
        }

        /// <summary>
        /// Public method to reset match via SendCustomEvent.
        /// </summary>
        public void TriggerResetMatch()
        {
            if (Manager != null && Networking.IsMaster)
            {
                Manager.ResetMatch();
            }
        }
    }
}

