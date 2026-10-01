using System;
using Triangle.Core;
using UnityEngine;

namespace Triangle.Interaction
{
    /// <summary>
    /// Marks a GameObject (or a child anchor) as interactable. The player's
    /// interaction finder locates these each scan, shows a prompt, and fires the
    /// interaction event when the player presses interact.
    ///
    /// Attach one to a door, note, object, or invisible trigger. The owning
    /// transform (or InteractionAnchor, if set) is used as the interaction anchor.
    ///
    /// Port of UTriangleInteractableComponent.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleInteractable : MonoBehaviour
    {
        /// <summary>Friendly name shown in the HUD prompt, e.g. "Notebook".</summary>
        public string InteractionName = string.Empty;

        /// <summary>When false the interaction finder skips this component.</summary>
        public bool InteractionEnabled = true;

        /// <summary>Alternative anchor point; when set, its position is used for range checks.</summary>
        public Transform InteractionAnchor;

        /// <summary>How far an interactor may be. Leave &lt;= 0 to use the default finder range (metres).</summary>
        public float MaxInteractionRange = 0f;

        /// <summary>Fired when the player presses interact while this is focused.</summary>
        public event Action<TriangleInteractable, GameObject> OnInteracted;

        /// <summary>Fired when the finder starts focusing this interaction.</summary>
        public event Action OnFocusBegin;

        /// <summary>Fired when the finder stops focusing this interaction.</summary>
        public event Action OnFocusEnd;

        public bool IsUsable => isActiveAndEnabled && InteractionEnabled;

        public void Interact(GameObject interactor)
        {
            // Gate on the same predicate the focus finder uses. Checking only
            // InteractionEnabled here let a disabled component still fire, which
            // meant an interaction could land on something the player could not see
            // or focus in the first place.
            if (!IsUsable)
            {
                return;
            }

            string interactorName = interactor != null ? interactor.name : "unknown";
            TriangleLog.Interaction($"Interact: {interactorName} interacted with \"{InteractionName}\".");
            OnInteracted?.Invoke(this, interactor);
        }

        public void NotifyFocusBegin()
        {
            if (InteractionEnabled)
            {
                OnFocusBegin?.Invoke();
            }
        }

        public void NotifyFocusEnd()
        {
            OnFocusEnd?.Invoke();
        }

        public Vector3 GetInteractionWorldLocation()
        {
            if (InteractionAnchor != null)
            {
                return InteractionAnchor.position;
            }

            return transform.position;
        }
    }
}
