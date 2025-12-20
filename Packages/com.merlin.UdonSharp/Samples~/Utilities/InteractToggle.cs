
using UnityEngine;

namespace UdonSharp.Examples.Utilities
{
    /// <summary>
    /// Toggles a list of game objects on and off when interacted with
    /// </summary>
    [AddComponentMenu("Udon Sharp/Utilities/Interact Toggle")]
    [UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
    public class InteractToggle : UdonSharpBehaviour
    {
        [Tooltip("List of objects to toggle on and off")]
        public GameObject[] toggleObjects;

        public override void Interact()
        {
            foreach (GameObject toggleObject in toggleObjects)
            {
                if (toggleObject != null)
                    toggleObject.SetActive(!toggleObject.activeSelf);
            }
        }
    }
}

























