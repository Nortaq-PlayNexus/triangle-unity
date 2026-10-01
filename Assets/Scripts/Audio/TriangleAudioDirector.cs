using System;
using System.Collections.Generic;
using Triangle.Core;
using UnityEngine;

namespace Triangle.Audio
{
    /// <summary>
    /// Owns the dream's audio state: a persistent music bed whose volume tracks a
    /// named state, plus the procedurally synthesised hum that follows The Triangle's
    /// presence.
    ///
    /// Port of UTriangleAudioSubsystem. The UE WorldSubsystem becomes a plain class
    /// hosted by the bootstrap, since it owns no state that must outlive the scene.
    /// </summary>
    public class TriangleAudioDirector
    {
        public const string StateCalm = "Calm";
        public const string StateWatch = "Watch";
        public const string StateManifest = "Manifest";
        public const string StateNearSilence = "NearSilence";

        private readonly Dictionary<string, float> _stateVolume = new Dictionary<string, float>();

        /// <summary>
        /// The component the fade coroutine and the generated child objects hang off.
        /// Held as a MonoBehaviour rather than a Transform so the director never has to
        /// recover it with GetComponent at call time.
        /// </summary>
        private readonly MonoBehaviour _host;

        private AudioSource _music;
        private TriangleProceduralHum _hum;
        private AudioClip _boundMusic;
        private bool _ownsBoundMusic;
        private string _currentState = StateCalm;
        private bool _silenced;
        private bool _warnedNoMusic;

        private Coroutine _fadeRoutine;

        /// <summary>Master multiplier applied to every dream-bed state.</summary>
        public float MusicBaseVolume { get; set; } = 1f;

        /// <summary>
        /// Multiplier applied to the hum only.
        ///
        /// Separate from <see cref="MusicBaseVolume"/> because the hum is a sustained
        /// low tone and the most likely thing for a player to want quieter on its own.
        /// Forcing one control over both would make that impossible.
        /// </summary>
        public float HumVolume { get; set; } = 1f;

        public float CurrentVolume { get; private set; }
        public string CurrentState => _currentState;
        public bool IsSilenced => _silenced;

        public TriangleAudioDirector(MonoBehaviour host)
        {
            _host = host;

            _stateVolume[StateCalm] = 0.40f;
            _stateVolume[StateWatch] = 0.22f;
            _stateVolume[StateManifest] = 0.08f;
            _stateVolume[StateNearSilence] = 0f;

            CurrentVolume = _stateVolume[StateCalm];

            var musicObject = new GameObject("DreamMusicBed");
            musicObject.transform.SetParent(host != null ? host.transform : null, false);
            _music = musicObject.AddComponent<AudioSource>();
            _music.playOnAwake = false;
            _music.loop = true;
            _music.spatialBlend = 0f; // keeps the bed audible from any camera
            _music.volume = 0f;
        }

        /// <summary>
        /// A runtime-generated bed for when no authored music exists yet.
        ///
        /// The Unreal slice left the bed silent and warned once; that is the correct
        /// behaviour for a real project, but it means a fresh checkout plays nothing
        /// but the hum. This synthesises a slow, cold drone so the state machine is
        /// audible. Assign <c>masterMusic</c> on the bootstrap to replace it.
        /// </summary>
        public void EnsureFallbackBed()
        {
            if (_boundMusic != null)
            {
                return;
            }

            SetMasterMusic(TriangleProceduralHum.CreateDroneBed(8f));
        }

        /// <summary>The music bed to play. Null stops the bed and warns once.</summary>
        public void SetMasterMusic(AudioClip music)
        {
            ReleaseOwnedMusic();

            if (music == null)
            {
                _boundMusic = null;
                if (_music != null)
                {
                    _music.Stop();
                }

                if (!_warnedNoMusic)
                {
                    TriangleLog.Warn("SetMasterMusic called with no clip - the dream runs silent until audio content lands.");
                    _warnedNoMusic = true;
                }

                return;
            }

            // The generated fallback bed is ours to reclaim; an authored clip is the
            // caller's asset and must survive being replaced.
            _ownsBoundMusic = music.name == TriangleProceduralHum.GeneratedBedName;
            _boundMusic = music;

            _music.clip = music;
            float stateTarget = _stateVolume.TryGetValue(_currentState, out float v) ? v : 0f;
            float targetVolume = (_silenced ? _stateVolume[StateNearSilence] : stateTarget) * MusicBaseVolume;
            CurrentVolume = targetVolume;
            _music.volume = 0f;
            _music.Play();
            FadeTo(targetVolume, 2f);
            TriangleLog.Log($"Master dream bed bound to '{music.name}'.");
        }

        public void SetMusicState(string state, float fadeSeconds)
        {
            if (!_stateVolume.ContainsKey(state))
            {
                TriangleLog.Warn($"Ignoring unknown dream-bed state '{state}'.");
                return;
            }

            _currentState = state;
            if (_boundMusic == null)
            {
                return;
            }

            ApplyStateVolume(state, fadeSeconds);
        }

        public void SetNearSilence(bool silence, float fadeSeconds)
        {
            _silenced = silence;
            if (_boundMusic == null)
            {
                return;
            }

            ApplyStateVolume(silence ? StateNearSilence : _currentState, fadeSeconds);
        }

        /// <summary>
        /// Holds the bed silent for a fixed span, then releases it.
        ///
        /// Exists because the false-awakening beat is "everything stops, then one small
        /// wrongness arrives", and expressing that as silence-then-restore needs the
        /// release to be timed from a single call site.
        ///
        /// A single tracked coroutine rather than a keyed scheduler entry: the audio
        /// director is a plain class with no scheduler of its own, and only one silence
        /// can be in effect at a time. Starting a second hold cancels the first, so a
        /// rapid second wake cannot have the earlier release cut its silence short.
        ///
        /// Unscaled, matching the rest of the dream's presentation timing, so pausing
        /// during the silent beat does not strand the bed at zero volume.
        /// </summary>
        public void PushSilenceRelease(float holdSeconds, Action onRelease)
        {
            if (_host == null || onRelease == null)
            {
                return;
            }

            if (_silenceHold != null)
            {
                _host.StopCoroutine(_silenceHold);
                _silenceHold = null;
            }

            float safeHold = float.IsNaN(holdSeconds) || float.IsInfinity(holdSeconds) ? 1f : Mathf.Max(0f, holdSeconds);
            _silenceHold = _host.StartCoroutine(SilenceHold(safeHold, onRelease));
        }

        private Coroutine _silenceHold;

        private System.Collections.IEnumerator SilenceHold(float holdSeconds, Action onRelease)
        {
            float elapsed = 0f;
            while (elapsed < holdSeconds)
            {
                elapsed += Time.unscaledDeltaTime;
                yield return null;
            }

            _silenceHold = null;
            onRelease?.Invoke();
        }

        private void ApplyStateVolume(string state, float fadeSeconds)
        {
            _stateVolume.TryGetValue(state, out float target);
            FadeTo(_silenced ? _stateVolume[StateNearSilence] : target, fadeSeconds);
        }

        private void FadeTo(float targetVolume, float fadeSeconds)
        {
            if (_music == null)
            {
                CurrentVolume = targetVolume;
                return;
            }

            CurrentVolume = targetVolume * MusicBaseVolume;
            float safeFade = float.IsNaN(fadeSeconds) || float.IsInfinity(fadeSeconds) ? 1f : Mathf.Max(0.05f, fadeSeconds);

            // Unity's AudioSource has no linear fade parameter, so the ramp is driven
            // from Update via a small tween on the source.
            StartFade(CurrentVolume, safeFade);
        }

        private void StartFade(float target, float seconds)
        {
            if (_music == null || _host == null)
            {
                return;
            }

            if (_fadeRoutine != null)
            {
                _host.StopCoroutine(_fadeRoutine);
                _fadeRoutine = null;
            }

            _fadeRoutine = _host.StartCoroutine(FadeRoutine(target, seconds));
        }

        private System.Collections.IEnumerator FadeRoutine(float target, float seconds)
        {
            float start = _music != null ? _music.volume : target;
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / seconds);

                // The host can be torn down mid-fade (scene unload, play-mode exit),
                // which destroys the AudioSource out from under this coroutine.
                if (_music == null)
                {
                    _fadeRoutine = null;
                    yield break;
                }

                _music.volume = Mathf.Lerp(start, target, t);
                yield return null;
            }

            if (_music != null)
            {
                _music.volume = target;
            }

            _fadeRoutine = null;
        }

        public void PlayOneShot(AudioClip clip, Vector3 location, float volume = 1f)
        {
            if (clip == null)
            {
                return;
            }

            AudioSource.PlayClipAtPoint(clip, location, volume);
        }

        /// <summary>
        /// Hysteresis: tiny presences are not worth waking the hum for, matching the
        /// design beat where the hum is sometimes simply not there.
        /// </summary>
        public void SetHumPresence(float target01, float fadeSeconds)
        {
            if (target01 < 0.04f)
            {
                target01 = 0f;
            }

            EnsureHum();
            if (_hum == null)
            {
                return;
            }

            _hum.Volume = HumVolume;
            _hum.SetPresence(target01, fadeSeconds);
        }

        private void EnsureHum()
        {
            if (_hum != null)
            {
                return;
            }

            var humObject = new GameObject("ProceduralHum");
            humObject.transform.SetParent(_host != null ? _host.transform : null, false);
            _hum = humObject.AddComponent<TriangleProceduralHum>();
        }

        public bool IsHumActive() => _hum != null && _hum.IsHumming;

        public float GetHumPresence() => _hum != null ? _hum.HumPresence : 0f;

        /// <summary>Destroys the generated bed, the hum and the music source's fade.</summary>
        public void Shutdown()
        {
            if (_fadeRoutine != null && _host != null)
            {
                _host.StopCoroutine(_fadeRoutine);
                _fadeRoutine = null;
            }

            if (_silenceHold != null && _host != null)
            {
                _host.StopCoroutine(_silenceHold);
                _silenceHold = null;
            }

            if (_hum != null)
            {
                _hum.SetPresence(0f, 0.05f);
                UnityEngine.Object.Destroy(_hum.gameObject);
                _hum = null;
            }

            if (_music != null)
            {
                _music.Stop();
            }

            ReleaseOwnedMusic();
        }

        /// <summary>
        /// Frees the runtime-generated bed if this director created it.
        ///
        /// The fallback drone is several megabytes of PCM that nothing else references.
        /// Nothing reclaimed it, so every environment rebuild leaked another copy for
        /// the lifetime of the session.
        /// </summary>
        private void ReleaseOwnedMusic()
        {
            if (!_ownsBoundMusic || _boundMusic == null)
            {
                _ownsBoundMusic = false;
                return;
            }

            UnityEngine.Object.Destroy(_boundMusic);
            _boundMusic = null;
            _ownsBoundMusic = false;
        }
    }
}
