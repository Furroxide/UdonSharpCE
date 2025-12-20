using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using UdonSharp.CE;

namespace RocketLeague
{
    /// <summary>
    /// Teleports players to the Rocket League arena when interacted with.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ArenaTeleporter : UdonSharpBehaviour
    {
        [Header("Teleport Destination")]
        public Transform ArenaSpawnPoint;

        [Header("Arena Control")]
        public GameObject ArenaRoot;
        public RocketLeagueManager Manager;

        [Header("Settings")]
        public string InteractionText = "Play Rocket League";

        public override void Interact()
        {
            TeleportToArena();
        }

        public void TeleportToArena()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || !player.IsValid())
                return;

            // Enable arena if it was disabled
            if (ArenaRoot != null && !ArenaRoot.activeSelf)
            {
                ArenaRoot.SetActive(true);
            }

            // Teleport the player
            if (ArenaSpawnPoint != null)
            {
                player.TeleportTo(
                    ArenaSpawnPoint.position,
                    ArenaSpawnPoint.rotation,
                    VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint,
                    false
                );
            }

            Debug.Log("[ArenaTeleporter] Teleported player to Rocket League arena");
        }

        /// <summary>
        /// Called to teleport back to the hub.
        /// </summary>
        public void TeleportToHub(Transform hubSpawnPoint)
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || !player.IsValid() || hubSpawnPoint == null)
                return;

            player.TeleportTo(
                hubSpawnPoint.position,
                hubSpawnPoint.rotation,
                VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint,
                false
            );
        }
    }
}

