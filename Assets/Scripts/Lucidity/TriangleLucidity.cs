using System;
using Triangle.Core;
using UnityEngine;

namespace Triangle.Lucidity
{
    /// <summary>
    /// The hidden/visible Lucidity meter.
    ///
    /// Lucidity represents how aware the player is that they are dreaming.
    ///
    ///  - Low      (0..30)   : the dream is accepted; impossible things pass by.
    ///  - Medium   (30..60)  : repeated places get noticed; doors misbehave.
    ///  - High     (60..90)  : the player can manipulate dream elements.
    ///  - Maximum  (90..100) : dream rules break; The Triangle draws near.
    ///
    /// Lucidity is only ever raised through explicit dream events (a watched
    /// object, an interaction, a realization) - never by merely existing, which
    /// keeps it a progression tool rather than a passive timer.
    ///
    /// Port of ULTriangleLucidityComponent. C# events replace UE dynamic
    /// multicast delegates.
    /// </summary>
    [Serializable]
    public class TriangleLucidity
    {
        public const float Min = 0f;
        public const float Max = 100f;

        /// <summary>Current lucidity, clamped to 0..100.</summary>
        public float Value { get; private set; }

        /// <summary>Amount of lucidity a meaningful realization provides.</summary>
        public float BaseGainPerRealization = 8f;

        /// <summary>Fired on every value change. Also broadcast by restore paths so meters refresh.</summary>
        public event Action<float> OnLucidityChanged;

        /// <summary>Fired only when the level band is crossed by normal progression.</summary>
        public event Action<TriangleLucidityLevel> OnLucidityLevelChanged;

        /// <summary>Fired once when lucidity first reaches 100.</summary>
        public event Action OnLucidityMax;

        private bool _maxReached;

        public float GetLucidity() => Value;

        public TriangleLucidityLevel GetLucidityLevel() => LevelFor(Value);

        /// <summary>Whether the player is lucid enough to deliberately break dream rules.</summary>
        public bool CanManipulateDream() => GetLucidityLevel() >= TriangleLucidityLevel.High;

        public void AddLucidity(float amount)
        {
            if (float.IsNaN(amount) || float.IsInfinity(amount))
            {
                return;
            }

            SetLucidity(Value + amount);
        }

        public void SetLucidity(float newValue)
        {
            if (float.IsNaN(newValue) || float.IsInfinity(newValue))
            {
                TriangleLog.Dream("Ignoring non-finite lucidity value.");
                return;
            }

            float clamped = Mathf.Clamp(newValue, Min, Max);
            if (Mathf.Abs(clamped - Value) < 0.0001f)
            {
                return;
            }

            TriangleLucidityLevel previousLevel = GetLucidityLevel();
            Value = clamped;

            TriangleLog.Dream($"Lucidity changed to {Value:0.0}");
            OnLucidityChanged?.Invoke(Value);

            TriangleLucidityLevel newLevel = GetLucidityLevel();
            if (newLevel != previousLevel)
            {
                TriangleLog.Dream($"Lucidity level -> {newLevel}");
                OnLucidityLevelChanged?.Invoke(newLevel);
            }

            if (!_maxReached && Value >= Max)
            {
                _maxReached = true;
                OnLucidityMax?.Invoke();
            }
        }

        /// <summary>
        /// Restores a saved value without replaying level or maximum-lucidity milestones.
        /// The value-only delegate is still broadcast so meters can refresh immediately.
        /// </summary>
        public void RestoreFromSave(float savedLucidity)
        {
            // Loading is state synchronisation, not progression. Assign directly so the
            // level and max events cannot replay story beats or awards.
            Value = Sanitize(savedLucidity, Min, Max);
            _maxReached = Value >= Max;

            TriangleLog.Dream($"Lucidity restored to {Value:0.0}.");
            OnLucidityChanged?.Invoke(Value);
        }

        internal static float Sanitize(float value, float low, float high)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return low;
            }

            return Mathf.Clamp(value, low, high);
        }

        public static TriangleLucidityLevel LevelFor(float value)
        {
            if (value >= 90f) return TriangleLucidityLevel.Maximum;
            if (value >= 60f) return TriangleLucidityLevel.High;
            if (value >= 30f) return TriangleLucidityLevel.Medium;
            return TriangleLucidityLevel.Low;
        }
    }
}
