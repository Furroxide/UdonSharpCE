using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using UdonSharp.CE;

namespace RocketLeague
{
    /// <summary>
    /// Teleports players to the Rocket League arena.
    /// </summary>
    /// <remarks>
    /// Interaction is driven by the InteractButton sharing this GameObject, which routes the click here
    /// through SendCustomEvent -- the same wiring every other hub station button uses. This behaviour
    /// deliberately does NOT override Interact: VRChat delivers the interact event to every
    /// UdonBehaviour on a GameObject, so an override here fired TeleportToArena a second time on every
    /// click. For the same reason the hover label lives on the InteractButton rather than here.
    /// </remarks>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ArenaTeleporter : UdonSharpBehaviour
    {
        [Header("Teleport Destination")]
        public Transform ArenaSpawnPoint;

        [Header("Arena Control")]
        public GameObject ArenaRoot;
        public RocketLeagueManager Manager;

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
    }
}
