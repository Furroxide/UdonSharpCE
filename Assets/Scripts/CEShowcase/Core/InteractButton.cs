using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon;

namespace CEShowcase.Core
{
    /// <summary>
    /// Generic interact button that calls a method on a target UdonBehaviour when clicked.
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class InteractButton : UdonSharpBehaviour
    {
        [Header("Target")]
        [Tooltip("The UdonBehaviour to send the event to")]
        [SerializeField] private UdonBehaviour _targetBehaviour;
        
        [Tooltip("The name of the method to call on the target")]
        [SerializeField] private string _methodName;
        
        [Header("Interaction Settings")]
        [Tooltip("Text shown when hovering over the button")]
        [SerializeField] private string _interactionText = "Press";
        
        void Start()
        {
            // Set the interaction text that appears on hover
            if (!string.IsNullOrEmpty(_interactionText))
            {
                InteractionText = _interactionText;
            }
        }
        
        public override void Interact()
        {
            if (_targetBehaviour != null && !string.IsNullOrEmpty(_methodName))
            {
                _targetBehaviour.SendCustomEvent(_methodName);
            }
        }
    }
}
