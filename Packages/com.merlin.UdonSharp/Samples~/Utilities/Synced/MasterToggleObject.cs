
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.Examples.Utilities
{
    /// <summary>
    /// Synced toggle that only the instance master can interact with
    /// </summary>
    [AddComponentMenu("Udon Sharp/Utilities/Synced/Master Toggle Object")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class MasterToggleObject : UdonSharpBehaviour
    {
        public GameObject toggleObject;

        [UdonSynced]
        bool isObjectEnabled;

        public override void Interact()
        {
            if (!Networking.IsMaster) return;
            
            isObjectEnabled = !isObjectEnabled;
            RequestSerialization();
            UpdateToggleState();
        }

        public override void OnDeserialization()
        {
            UpdateToggleState();
        }

        void UpdateToggleState()
        {
            if (toggleObject != null)
                toggleObject.SetActive(isObjectEnabled);
        }
    }
}


























