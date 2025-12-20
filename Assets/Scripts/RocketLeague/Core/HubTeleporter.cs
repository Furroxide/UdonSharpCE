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

        public override void Interact()
        {
            TeleportToHub();
        }

        public void TeleportToHub()
        {
            VRCPlayerApi player = Networking.LocalPlayer;
            if (player == null || !player.IsValid())
                return;

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
    }
}

