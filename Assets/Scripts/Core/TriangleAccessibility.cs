using System;
using UnityEngine;

namespace Triangle.Core
{
    /// <summary>
    /// Player-facing comfort and accessibility settings, persisted across sessions.
    ///
    /// A dreamcore horror game is unplayable for some people without these. The
    /// subtitle scale and opacity fields already existed on the HUD as inspector-only
    /// values, which meant the settings could be authored but never changed by a player
    /// at runtime - the accessiblity affordance existed on paper and nowhere else.
    ///
    /// Backed by <see cref="PlayerPrefs"/> rather than the save file deliberately. These
    /// are device and comfort preferences, not dream state: a player who raises the
    /// subtitle size to read the game should not have that undone by loading a save
    /// from before they found the setting, and wiping the dream should not reset how the
    /// game looks.
    ///
    /// Values are clamped on both read and write, because PlayerPrefs is a plain string
    /// store that survives across builds and a hand-edited or truncated value must not
    /// be able to produce an unreadable UI.
    /// </summary>
    public static class TriangleAccessibility
    {
        private const string KeySubtitleScale = "Triangle.SubtitleScale";
        private const string KeySubtitleOpacity = "Triangle.SubtitleOpacity";
        private const string KeySubtitleBackground = "Triangle.SubtitleBackdrop";
        private const string KeyMouseSensitivity = "Triangle.MouseSensitivity";
        private const string KeyGamepadSensitivity = "Triangle.GamepadSensitivity";
        private const string KeyInvertLookY = "Triangle.InvertLookY";
        private const string KeyHeadBob = "Triangle.HeadBob";
        private const string KeyMouseSmoothing = "Triangle.MouseSmoothing";
        private const string KeyMusicVolume = "Triangle.MusicVolume";
        private const string KeyHumVolume = "Triangle.HumVolume";
        private const string KeyFilmGrain = "Triangle.FilmGrain";
        private const string KeyChromaticAberration = "Triangle.ChromaticAberration";
        private const string KeyMotionBlur = "Triangle.MotionBlur";
        private const string KeyBloom = "Triangle.Bloom";

        /// <summary>Raised when any setting changes, so listeners can re-apply.</summary>
        public static event Action OnSettingsChanged;

        // Bounds. Deliberately wide enough to be useful and narrow enough that no
        // combination of settings can make the game unusable or invisible.
        public const float MinSubtitleScale = 0.5f;
        public const float MaxSubtitleScale = 3f;
        public const float MinSensitivity = 0.2f;
        public const float MaxSensitivity = 5f;
        public const float MinVolume = 0f;
        public const float MaxVolume = 1f;

        public static float SubtitleScale
        {
            get => ReadFloat(KeySubtitleScale, 1f, MinSubtitleScale, MaxSubtitleScale);
            set => WriteFloat(KeySubtitleScale, value, MinSubtitleScale, MaxSubtitleScale);
        }

        public static float SubtitleOpacity
        {
            get => ReadFloat(KeySubtitleOpacity, 1f, 0f, 1f);
            set => WriteFloat(KeySubtitleOpacity, value, 0f, 1f);
        }

        /// <summary>
        /// Draws a dark plate behind the subtitle.
        ///
        /// The bed is deliberately near-black, so an opacity-reduced subtitle can become
        /// unreadable against it. This gives the player a way to keep the text subtle
        /// and still legible, which is the combination the other two settings cannot
        /// reach on their own.
        /// </summary>
        public static bool SubtitleBackdrop
        {
            get => ReadBool(KeySubtitleBackground, false);
            set => WriteBool(KeySubtitleBackground, value);
        }

        public static float MouseSensitivity
        {
            get => ReadFloat(KeyMouseSensitivity, 1.4f, MinSensitivity, MaxSensitivity);
            set => WriteFloat(KeyMouseSensitivity, value, MinSensitivity, MaxSensitivity);
        }

        public static float GamepadSensitivity
        {
            get => ReadFloat(KeyGamepadSensitivity, 120f, MinSensitivity, MaxSensitivity * 100f);
            set => WriteFloat(KeyGamepadSensitivity, value, MinSensitivity, MaxSensitivity * 100f);
        }

        public static bool InvertLookY
        {
            get => ReadBool(KeyInvertLookY, false);
            set => WriteBool(KeyInvertLookY, value);
        }

        /// <summary>
        /// Camera bob while walking.
        ///
        /// Off by default. The setting exists mainly for players who get motion sick, and
        /// the slice's bob is subtle enough that the default should be the still one.
        /// </summary>
        public static bool HeadBob
        {
            get => ReadBool(KeyHeadBob, false);
            set => WriteBool(KeyHeadBob, value);
        }

        public static float MouseSmoothing
        {
            get => ReadFloat(KeyMouseSmoothing, 0f, 0f, 0.95f);
            set => WriteFloat(KeyMouseSmoothing, value, 0f, 0.95f);
        }

        public static float MusicVolume
        {
            get => ReadFloat(KeyMusicVolume, 1f, MinVolume, MaxVolume);
            set => WriteFloat(KeyMusicVolume, value, MinVolume, MaxVolume);
        }

        public static float HumVolume
        {
            get => ReadFloat(KeyHumVolume, 1f, MinVolume, MaxVolume);
            set => WriteFloat(KeyHumVolume, value, MinVolume, MaxVolume);
        }

        /// <summary>
        /// Scales the post-processing film grain, 0 to 1.
        ///
        /// Grain is the most commonly reported trigger for visual discomfort, and this game
        /// is almost entirely dark low-contrast imagery - exactly the content where grain is
        /// most visible and least welcome. A separate control rather than part of a single
        /// "effects" slider because it is the one players ask for by name.
        ///
        /// Defaults to full rather than off. The grain also breaks up banding in the large
        /// flat gradients this palette produces, so removing it entirely makes the image
        /// worse for everyone, and a control that reaches zero is still available.
        /// </summary>
        public static float FilmGrain
        {
            get => ReadFloat(KeyFilmGrain, 1f, MinVolume, MaxVolume);
            set => WriteFloat(KeyFilmGrain, value, MinVolume, MaxVolume);
        }

        /// <summary>
        /// Scales the lens chromatic aberration, 0 to 1.
        ///
        /// Separately adjustable from grain because it is a different kind of problem: it
        /// displaces colour channels relative to each other, which some people find
        /// genuinely disorienting and which a flash or a strobing light can amplify. It is
        /// the one effect here that should default lower.
        /// </summary>
        public static float ChromaticAberration
        {
            get => ReadFloat(KeyChromaticAberration, 0.35f, MinVolume, MaxVolume);
            set => WriteFloat(KeyChromaticAberration, value, MinVolume, MaxVolume);
        }

        /// <summary>
        /// Scales camera motion blur, 0 to 1.
        ///
        /// Blur is a third distinct discomfort category - it provokes motion sickness rather
        /// than eye strain - so it does not share a control with either of the others. Held
        /// low by default for the same reason head bob is: the game already leans on camera
        /// motion for its effect, and adding blur on top compounds it.
        /// </summary>
        public static float MotionBlur
        {
            get => ReadFloat(KeyMotionBlur, 0.5f, MinVolume, MaxVolume);
            set => WriteFloat(KeyMotionBlur, value, MinVolume, MaxVolume);
        }

        /// <summary>
        /// Scales bloom, 0 to 1.
        ///
        /// Not primarily a comfort control: bloom is what makes the window and the
        /// fluorescent tubes read as light sources, so turning it off costs a real part of
        /// the image. It is exposed because the pulsing bloom some players describe during
        /// the presence ramps is this effect, and they should be able to remove it.
        /// </summary>
        public static float Bloom
        {
            get => ReadFloat(KeyBloom, 1f, MinVolume, MaxVolume);
            set => WriteFloat(KeyBloom, value, MinVolume, MaxVolume);
        }

        /// <summary>Restores every setting to its shipped default.</summary>
        public static void ResetToDefaults()
        {
            PlayerPrefs.DeleteKey(KeySubtitleScale);
            PlayerPrefs.DeleteKey(KeySubtitleOpacity);
            PlayerPrefs.DeleteKey(KeySubtitleBackground);
            PlayerPrefs.DeleteKey(KeyMouseSensitivity);
            PlayerPrefs.DeleteKey(KeyGamepadSensitivity);
            PlayerPrefs.DeleteKey(KeyInvertLookY);
            PlayerPrefs.DeleteKey(KeyHeadBob);
            PlayerPrefs.DeleteKey(KeyMouseSmoothing);
            PlayerPrefs.DeleteKey(KeyMusicVolume);
            PlayerPrefs.DeleteKey(KeyHumVolume);
            PlayerPrefs.DeleteKey(KeyFilmGrain);
            PlayerPrefs.DeleteKey(KeyChromaticAberration);
            PlayerPrefs.DeleteKey(KeyMotionBlur);
            PlayerPrefs.DeleteKey(KeyBloom);
            PlayerPrefs.Save();
            OnSettingsChanged?.Invoke();
        }

        private static float ReadFloat(string key, float fallback, float low, float high)
        {
            // A stored value that is non-finite falls back rather than propagating.
            // PlayerPrefs outlives the build that wrote it, so the value can be stale,
            // hand-edited or written by an older version.
            float raw = PlayerPrefs.GetFloat(key, fallback);
            return Sanitise(raw, fallback, low, high);
        }

        private static void WriteFloat(string key, float value, float low, float high)
        {
            PlayerPrefs.SetFloat(key, Sanitise(value, low, low, high));
            PlayerPrefs.Save();
            OnSettingsChanged?.Invoke();
        }

        private static bool ReadBool(string key, bool fallback)
        {
            return PlayerPrefs.GetInt(key, fallback ? 1 : 0) != 0;
        }

        private static void WriteBool(string key, bool value)
        {
            PlayerPrefs.SetInt(key, value ? 1 : 0);
            PlayerPrefs.Save();
            OnSettingsChanged?.Invoke();
        }

        private static float Sanitise(float value, float fallback, float low, float high)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return Mathf.Clamp(fallback, low, high);
            }

            return Mathf.Clamp(value, low, high);
        }
    }
}
