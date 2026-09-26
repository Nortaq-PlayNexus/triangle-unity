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
        private readonly Transform _host;

        private AudioSource _music;
        private TriangleProceduralHum _hum;
        private AudioClip _boundMusic;
        private string _currentState = StateCalm;
        private bool _silenced;
        private bool _warnedNoMusic;

        [Tooltip("Master multiplier applied to every dream-bed state.")]
        public float MusicBaseVolume = 1f;

        public float CurrentVolume { get; private set; }
        public string CurrentState => _currentState;
        public bool IsSilenced => _silenced;

        public TriangleAudioDirector(Transform host)
        {
            _host = host;

            _stateVolume[StateCalm] = 0.40f;
            _stateVolume[StateWatch] = 0.22f;
            _stateVolume[StateManifest] = 0.08f;
            _stateVolume[StateNearSilence] = 0f;

            CurrentVolume = _stateVolume[StateCalm];

            var musicObject = new GameObject("DreamMusicBed");
            musicObject.transform.SetParent(host, false);
            _music = musicObject.AddComponent<AudioSource>();
            _music.playOnAwake = false;
            _music.loop = true;
            _music.spatialBlend = 0f; // keeps the bed audible from any camera
            _music.volume = 0f;
        }

        /// <summary>The music bed to play. The slice runs silent until content lands.</summary>
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

        public void SetMasterMusic(AudioClip music)
        {
            _boundMusic = music;

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

        private Coroutine _fadeRoutine;

        private void StartFade(float target, float seconds)
        {
            if (_music == null)
            {
                return;
            }

            if (_fadeRoutine != null && _host != null)
            {
                _host.GetComponent<MonoBehaviour>()?.StopCoroutine(_fadeRoutine);
            }

            _fadeRoutine = _host != null
                ? _host.GetComponent<MonoBehaviour>()?.StartCoroutine(FadeRoutine(target, seconds))
                : null;
        }

        private System.Collections.IEnumerator FadeRoutine(float target, float seconds)
        {
            float start = _music.volume;
            float elapsed = 0f;
            while (elapsed < seconds)
            {
                elapsed += Time.unscaledDeltaTime;
                float t = Mathf.Clamp01(elapsed / seconds);
                _music.volume = Mathf.Lerp(start, target, t);
                yield return null;
            }

            _music.volume = target;
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
            _hum?.SetPresence(target01, fadeSeconds);
        }

        private void EnsureHum()
        {
            if (_hum != null)
            {
                return;
            }

            var humObject = new GameObject("ProceduralHum");
            humObject.transform.SetParent(_host, false);
            _hum = humObject.AddComponent<TriangleProceduralHum>();
        }

        public bool IsHumActive() => _hum != null && _hum.IsHumming;

        public float GetHumPresence() => _hum != null ? _hum.HumPresence : 0f;

        public void Shutdown()
        {
            if (_hum != null)
            {
                _hum.SetPresence(0f, 0.05f);
                Object.Destroy(_hum.gameObject);
                _hum = null;
            }

            if (_music != null)
            {
                _music.Stop();
            }
        }
    }
}
