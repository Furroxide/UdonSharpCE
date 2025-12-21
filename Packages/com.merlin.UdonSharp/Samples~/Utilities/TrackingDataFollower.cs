
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.Examples.Utilities
{
    /// <summary>
    /// Follows VR tracking data for the local player
    /// </summary>
    [AddComponentMenu("Udon Sharp/Utilities/Tracking Data Follower")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class TrackingDataFollower : UdonSharpBehaviour
    {
        public VRCPlayerApi.TrackingDataType trackingTarget;

        VRCPlayerApi playerApi;
        bool isInEditor;

        void Start()
        {
            playerApi = Networking.LocalPlayer;
            isInEditor = playerApi == null;
        }

        void Update()
        {
            if (isInEditor)
                return;

            VRCPlayerApi.TrackingData trackingData = playerApi.GetTrackingData(trackingTarget);
            transform.SetPositionAndRotation(trackingData.position, trackingData.rotation);
        }
    }
}


























