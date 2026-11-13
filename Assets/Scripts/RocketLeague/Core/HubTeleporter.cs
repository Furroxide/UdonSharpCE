using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace RocketLeague
{
    /// <summary>
    /// Teleports players back to the central hub.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class HubTeleporter : UdonSharpBehaviour
    {
        [Header("Hub Destination")]
        public Transform HubSpawnPoint;

        [Header("Arena Control")]
        [Tooltip("Roster the player's seat and car go back to. Found beside this teleporter when left empty.")]
        public PlayerSpawner Spawner;

        public override void Interact()
        {
            TeleportToHub();
        }

        public void TeleportToHub()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || !player.IsValid())
                return;

            // Leaving the arena takes the player's hands off the car and gives it back to the pool, for
            // the next player waiting. Without this the car went on reading their movement input for as
            // long as they walked around the hub, and nobody else could have it.
            PlayerSpawner spawner = ResolveSpawner();
            if (spawner != null)
                spawner.RequestLeaveArena();

            if (HubSpawnPoint != null)
            {
                player.TeleportTo(
                    HubSpawnPoint.position,
                    HubSpawnPoint.rotation,
                    VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint,
                    false
                );
            }
            else
            {
                // Fallback to origin
                player.TeleportTo(
                    Vector3.up * 0.5f,
                    Quaternion.identity,
                    VRC_SceneDescriptor.SpawnOrientation.AlignPlayerWithSpawnPoint,
                    false
                );
            }

            Debug.Log("[HubTeleporter] Teleported player back to hub");
        }

        /// <summary>
        /// The arena's spawner, wired or found. The return pad sits beside the spawner under the arena's
        /// gameplay root, so an unwired teleporter looks there rather than doing nothing at all.
        /// </summary>
        private PlayerSpawner ResolveSpawner()
        {
            if (Spawner == null && transform.parent != null)
                Spawner = transform.parent.GetComponentInChildren<PlayerSpawner>(true);

            return Spawner;
        }
    }
}

