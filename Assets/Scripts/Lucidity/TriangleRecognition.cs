using System;
using System.Collections.Generic;
using Triangle.Core;
using UnityEngine;

namespace Triangle.Lucidity
{
    /// <summary>
    /// The hidden Recognition stat - deliberately separate from Lucidity.
    ///
    /// Lucidity is "I know I am dreaming." Recognition is "I know what is here."
    /// The Triangle is an intrusion the player learns to recognise before they can
    /// understand it: recognition climbs through observing the same pattern in
    /// different bodies - the corner, a gap in the wallpaper, a clock stopped at
    /// twelve - until the player can sense its presence before anything is visible.
    ///
    ///  - Unknowing    (0..25)   : strangeness passes without note.
    ///  - Noticing     (25..50)  : the same shape keeps returning; it feels arranged.
    ///  - Recognizing  (50..75)  : the pattern is recognised before it completes.
    ///  - Anticipating (75..95)  : the presence is felt behind surfaces; the hum has meaning.
    ///  - Master       (95..100) : "it's here" - known without seeing.
    ///
    /// Like Lucidity, Recognition only advances through explicit dream events
    /// (witnessed manifestations, recurring motifs, sensed absences) - never by existing.
    ///
    /// Port of ULTriangleRecognitionComponent.
    /// </summary>
    [Serializable]
    public class TriangleRecognition
    {
        public const float Min = 0f;
        public const float Max = 100f;

        /// <summary>Current recognition, clamped to 0..100.</summary>
        public float Value { get; private set; }

        /// <summary>Recognition gained from witnessing one manifestation resolve.</summary>
        public float BaseGainPerSighting = 6f;

        /// <summary>Recognition gained from a recurring motif (clock, train horn, ...).</summary>
        public float BaseGainPerMotif = 2f;

        public event Action<float> OnRecognitionChanged;

        /// <summary>Fired only when the level band is crossed by normal progression.</summary>
        public event Action<TriangleRecognitionLevel> OnRecognitionLevelChanged;

        /// <summary>Broadcast when the player senses presence before a visible form ("it's here").</summary>
        public event Action<string> OnSensePresence;

        /// <summary>The pattern catalogue: ids the player has already watched resolve.</summary>
        private readonly List<string> _recognisedIds = new List<string>();

        public float GetRecognition() => Value;

        public TriangleRecognitionLevel GetRecognitionLevel() => LevelFor(Value);

        /// <summary>Returns a defensive copy of the patterns the player has already recognised.</summary>
        public List<string> GetRecognizedPatternIds() => new List<string>(_recognisedIds);

        public int CatalogueCount => _recognisedIds.Count;

        /// <summary>Player can recognise the Triangle behind a surface before any visual.</summary>
        public bool CanSensePresenceBeforeVisual() => GetRecognitionLevel() >= TriangleRecognitionLevel.Anticipating;

        /// <summary>Player can know "it's here" with no visible manifestation at all.</summary>
        public bool HasMasterSense() => GetRecognitionLevel() >= TriangleRecognitionLevel.Master;

        /// <summary>Whether this recognition allows a wake attempt that can resolve.</summary>
        public bool CanAttemptWake() => GetRecognitionLevel() >= TriangleRecognitionLevel.Anticipating;

        /// <summary>Whether the given motif has already been recognised.</summary>
        public bool HasRecognised(string manifestId) => !Name.IsNone(manifestId) && _recognisedIds.Contains(manifestId);

        public void AddRecognition(float amount, string reason = null)
        {
            if (amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount))
            {
                return;
            }

            SetRecognition(Value + amount);

            if (!Name.IsNone(reason) && !_recognisedIds.Contains(reason))
            {
                _recognisedIds.Add(reason);
                TriangleLog.Dream($"Encounter '{reason}' recorded in the pattern catalogue.");
            }
        }

        public void SetRecognition(float newValue)
        {
            if (float.IsNaN(newValue) || float.IsInfinity(newValue))
            {
                TriangleLog.Dream("Ignoring non-finite recognition value.");
                return;
            }

            float clamped = Mathf.Clamp(newValue, Min, Max);
            if (Mathf.Abs(clamped - Value) < 0.0001f)
            {
                return;
            }

            TriangleRecognitionLevel previousLevel = GetRecognitionLevel();
            Value = clamped;

            TriangleLog.Dream($"Recognition changed to {Value:0.0}");
            OnRecognitionChanged?.Invoke(Value);

            TriangleRecognitionLevel newLevel = GetRecognitionLevel();
            if (newLevel != previousLevel)
            {
                TriangleLog.Dream($"Recognition level -> {newLevel}");
                OnRecognitionLevelChanged?.Invoke(newLevel);

                if (newLevel >= TriangleRecognitionLevel.Anticipating)
                {
                    // Nearing the top, the sense comes before the sight.
                    OnSensePresence?.Invoke("SenseHum");
                }
            }
        }

        /// <summary>Records a witnessed manifestation by id (deduplicates the encounter log).</summary>
        public void RecordSighting(string manifestId)
        {
            if (Name.IsNone(manifestId) || _recognisedIds.Contains(manifestId))
            {
                return;
            }

            _recognisedIds.Add(manifestId);
            TriangleLog.Dream($"Sighting '{manifestId}' recorded.");
        }

        /// <summary>Legacy scalar-only restore; retains the current pattern catalogue.</summary>
        public void RestoreFromSave(float savedRecognition)
        {
            // Never use SetRecognition here: its level event drives progression.
            Value = TriangleLucidity.Sanitize(savedRecognition, Min, Max);
            TriangleLog.Dream($"Recognition restored to {Value:0.0}.");
            OnRecognitionChanged?.Invoke(Value);
        }

        /// <summary>
        /// Restores the scalar and pattern IDs without replaying recognition milestones
        /// or the pre-visual sense. The value-only event refreshes UI immediately.
        /// </summary>
        public void RestoreFromSaveWithCatalogue(float savedRecognition, IReadOnlyList<string> savedRecognizedPatternIds)
        {
            Value = TriangleLucidity.Sanitize(savedRecognition, Min, Max);

            _recognisedIds.Clear();
            if (savedRecognizedPatternIds != null)
            {
                for (int i = 0; i < savedRecognizedPatternIds.Count; i++)
                {
                    string patternId = savedRecognizedPatternIds[i];
                    if (!Name.IsNone(patternId) && !_recognisedIds.Contains(patternId))
                    {
                        _recognisedIds.Add(patternId);
                    }
                }
            }

            // OnRecognitionChanged is the value/UI notification. Level and presence
            // events intentionally stay silent because this is a load, not progress.
            TriangleLog.Dream($"Recognition restored to {Value:0.0} ({_recognisedIds.Count} catalogue entries).");
            OnRecognitionChanged?.Invoke(Value);
        }

        public static TriangleRecognitionLevel LevelFor(float value)
        {
            if (value >= 95f) return TriangleRecognitionLevel.Master;
            if (value >= 75f) return TriangleRecognitionLevel.Anticipating;
            if (value >= 50f) return TriangleRecognitionLevel.Recognizing;
            if (value >= 25f) return TriangleRecognitionLevel.Noticing;
            return TriangleRecognitionLevel.Unknowing;
        }
    }
}
