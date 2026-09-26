using Triangle.Core;
using UnityEngine;

namespace Triangle.Dream
{
    /// <summary>
    /// Data asset describing one possible manifestation of The Triangle.
    ///
    /// The Dream Director holds a pool of these and picks from them as recognition
    /// rises. Each definition states which body The Triangle takes, whether the
    /// hum accompanies it and how loud, how long it lasts, and how much recognition
    /// a witnessed appearance is worth. Sometimes the hum starts and nothing is ever
    /// visible - that is a valid manifestation too.
    ///
    /// Port of UTriangleManifestationDefinition. Definitions are also constructible
    /// in code so the six slice defaults need no .asset files on disk.
    /// </summary>
    [CreateAssetMenu(fileName = "Manifestation", menuName = "Triangle/Manifestation Definition")]
    public class TriangleManifestationDefinition : ScriptableObject
    {
        [Tooltip("Stable id recorded in the player's pattern catalogue when witnessed.")]
        public string ManifestId = string.Empty;

        public TriangleManifestationKind Kind = TriangleManifestationKind.Point;

        public TriangleAnomalyKind AnomalyKind = TriangleAnomalyKind.TriangularGap;

        /// <summary>Whether the hum is part of this manifestation.</summary>
        public bool UseHum = true;

        /// <summary>Hum presence (0..1) while this manifestation is active.</summary>
        [Range(0f, 1f)] public float HumPresence = 0.55f;

        [Min(0.5f)] public float MinDuration = 4f;

        [Min(0.5f)] public float MaxDuration = 8f;

        /// <summary>Recognition gained when this appearance resolves while being witnessed.</summary>
        [Range(0f, 30f)] public float RecognitionGainOnSight = 6f;

        /// <summary>This body cannot be selected until the player has noticed the earlier pattern.</summary>
        [Range(0f, 100f)] public float MinimumRecognition = 0f;

        /// <summary>Relative selection weight after the recognition gate is satisfied.</summary>
        [Range(0f, 100f)] public float SelectionWeight = 1f;

        /// <summary>Minimum gap between consecutive appearances of this body.</summary>
        [Min(3f)] public float CooldownSeconds = 18f;

        /// <summary>Design note for the authoring team (never shown in game).</summary>
        [TextArea] public string ObserverNote = string.Empty;

        /// <summary>A random duration within the configured window. Inverted bounds are normalised.</summary>
        public float GetRandomDuration()
        {
            float min = float.IsNaN(MinDuration) || float.IsInfinity(MinDuration) ? 0.5f : Mathf.Max(0.5f, MinDuration);
            float max = float.IsNaN(MaxDuration) || float.IsInfinity(MaxDuration) ? min : Mathf.Max(min, MaxDuration);
            return Random.Range(min, max);
        }
    }
}
