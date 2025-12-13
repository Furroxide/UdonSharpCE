
using UnityEngine;
using VRC.SDKBase;

namespace UdonSharp.Examples.Utilities
{
    /// <summary>
    /// Globally synced toggle that any player can interact with
    /// </summary>
    [AddComponentMenu("Udon Sharp/Utilities/Synced/Global Toggle Object")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class GlobalToggleObject : UdonSharpBehaviour
    {
        public GameObject toggleObject;

        [UdonSynced]
        bool isEnabled;

        public override void Interact()
        {
            Networking.SetOwner(Networking.LocalPlayer, gameObject);
            isEnabled = !isEnabled;
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
                toggleObject.SetActive(isEnabled);
        }
    }
}


















