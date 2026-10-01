using Triangle.Core;
using Triangle.Interaction;
using Triangle.Player;
using UnityEngine;

namespace Triangle.World
{
    /// <summary>
    /// An object the player can examine for a realization.
    ///
    /// This is the slice's third verb, alongside interacting and waking. The design
    /// says Lucidity rises through "a watched object, an interaction, a realization" -
    /// but the bedroom only shipped two interactables, so there was nothing to
    /// examine and progression stalled well short of the door's threshold.
    ///
    /// The first examination of an object pays out Lucidity and Recognition and records
    /// a pattern id. Re-examining it only re-reads the line, so it stays a
    /// realization rather than a grind.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(TriangleInteractable))]
    public class TriangleInspectable : MonoBehaviour
    {
        [Tooltip("Name shown in the interaction prompt, e.g. \"Clock\".")]
        public string InspectionName = "Look";

        [TextArea]
        [Tooltip("What the player notices. This is the payoff, so write it like the notebook pages.")]
        public string InspectionText = string.Empty;

        [Tooltip("Lucidity granted the first time this is examined.")]
        public float LucidityGain = 10f;

        [Tooltip("Recognition granted the first time this is examined.")]
        public float RecognitionGain = 3f;

        [Tooltip("Pattern id recorded in the Recognition catalogue when witnessed.")]
        public string PatternId = string.Empty;

        private TriangleInteractable _interactable;
        private bool _examined;

        /// <summary>True once the player has examined this and been paid.</summary>
        public bool HasBeenExamined => _examined;

        /// <summary>
        /// Applies the inspector-authored values and pushes the prompt name through
        /// to the interactable.
        ///
        /// The environment builders create these components with
        /// <c>AddComponent</c>, which runs <c>Awake</c> synchronously, and then assign
        /// the fields above. Anything Awake derives from those fields therefore sees
        /// the class defaults, not the authored values - which is how the window, bed
        /// and clock all ended up prompting as "Look". Builders must call this
        /// instead of writing the fields directly.
        /// </summary>
        public void Configure(string inspectionName, string inspectionText, float lucidityGain,
            float recognitionGain, string patternId = null)
        {
            InspectionName = inspectionName;
            InspectionText = inspectionText;
            LucidityGain = lucidityGain;
            RecognitionGain = recognitionGain;
            if (patternId != null)
            {
                PatternId = patternId;
            }

            if (_interactable != null && !string.IsNullOrEmpty(InspectionName))
            {
                _interactable.InteractionName = InspectionName;
            }
        }

        private void Awake()
        {
            _interactable = GetComponent<TriangleInteractable>();
            if (_interactable == null)
            {
                // RequireComponent guarantees this, but a null here would take the
                // subscription below with it and silently disable the whole object.
                TriangleLog.Error($"Inspectable on '{name}' has no interactable and will be inert.");
                return;
            }

            _interactable.OnInteracted += HandleInteracted;
        }

        private void HandleInteracted(TriangleInteractable interactable, GameObject interactor)
        {
            TriangleRuntime.Hud?.PushSubtitle(InspectionText, 4f);

            if (_examined)
            {
                // Already understood. Re-reading it is free, but it pays nothing.
                TriangleLog.Dream($"Re-examined {InspectionName}; nothing new.");
                return;
            }

            _examined = true;

            TrianglePlayer player = TriangleRuntime.Player;
            if (player != null)
            {
                player.Lucidity.AddLucidity(LucidityGain);
                player.Recognition.AddRecognition(RecognitionGain,
                    string.IsNullOrEmpty(PatternId) ? InspectionName : PatternId);
            }

            TriangleLog.Dream($"Examined {InspectionName} (+{LucidityGain:0} lucidity, +{RecognitionGain:0} recognition).");
        }

        private void OnDestroy()
        {
            if (_interactable != null)
            {
                _interactable.OnInteracted -= HandleInteracted;
            }
        }
    }
}
