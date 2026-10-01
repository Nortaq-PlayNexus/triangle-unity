using Triangle.Core;
using UnityEngine;

namespace Triangle.Audio
{
    /// <summary>
    /// The hum, synthesised entirely at runtime - no audio assets.
    ///
    /// Two detuned partials around 43/47 Hz breathing in and out of phase: a
    /// vibration the mind files as "wrong" long before it consciously hears it. The
    /// envelope is a slow LFO multiplied into the sum.
    ///
    /// Port of ATriangleProceduralHumSource. Unreal streamed PCM into a
    /// USoundWaveProcedural from a 10 ms feed timer; Unity's equivalent generates
    /// samples on demand from an AudioClip PCM reader callback, which removes the
    /// queueing and low-water bookkeeping entirely.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleProceduralHum : MonoBehaviour
    {
        private const int SampleRate = 48000;
        private const int Channels = 1;
        private const float FreqA = 43.2f;  // a sleeping-room vibration, not quite a note
        private const float FreqB = 46.7f;
        private const float BufferSeconds = 4f;

        /// <summary>Clip name for the generated fallback bed, used to identify our own assets.</summary>
        public const string GeneratedBedName = "TriangleDroneBed";

        private AudioClip _clip;
        private AudioSource _source;

        private double _phaseA;
        private double _phaseB;
        private double _detunePhase;
        private double _slowLfoPhase;

        private float _targetPresence;
        private float _fadeSeconds = 1f;
        private float _volume = 1f;

        // The PCM reader callback runs on the audio thread, not the main thread, and
        // reads the envelope while Update writes it. A plain field is not a safe
        // cross-thread handoff: the audio thread can observe a torn or stale value and
        // produce a discontinuity mid-buffer. Volatile gives the release semantics the
        // handoff needs, and a float store/load is atomic on the platforms this ships
        // to, so the read is always one of the two written values.
        private volatile float _currentEnvelope;

        public float HumPresence => _currentEnvelope;
        public bool IsHumming => _source != null && _source.isPlaying;

        /// <summary>
        /// Player volume multiplier, independent of the dream bed's.
        ///
        /// The hum is a sustained tone around 43/47 Hz, which is exactly the register
        /// people find uncomfortable, so it gets its own control rather than sharing one
        /// with the music. 0 mutes it without silencing the dream.
        /// </summary>
        public float Volume
        {
            get => _volume;
            set
            {
                if (float.IsNaN(value) || float.IsInfinity(value))
                {
                    return;
                }

                _volume = Mathf.Clamp01(value);
            }
        }

        private void Awake()
        {
            _clip = AudioClip.Create("TriangleHum", (int)(SampleRate * BufferSeconds), Channels, SampleRate, true, OnHumRead);

            var sourceObject = new GameObject("HumSource");
            sourceObject.transform.SetParent(transform, false);
            _source = sourceObject.AddComponent<AudioSource>();
            _source.clip = _clip;
            _source.playOnAwake = false;
            _source.loop = true;
            _source.spatialBlend = 0f;   // undirectional: the hum is inside the ears
            _source.volume = 0f;
        }

        /// <summary>Presence target (0..1) and the time to reach it.</summary>
        public void SetPresence(float target01, float fadeSeconds)
        {
            _targetPresence = float.IsNaN(target01) || float.IsInfinity(target01) ? 0f : Mathf.Clamp01(target01);
            _fadeSeconds = float.IsNaN(fadeSeconds) || float.IsInfinity(fadeSeconds) ? 1f : Mathf.Max(0.05f, fadeSeconds);
        }

        private void Update()
        {
            if (_source == null)
            {
                return;
            }

            // Unscaled on purpose. The pause menu sets Time.timeScale to zero, and on
            // scaled time that froze the envelope mid-fade: the hum stayed stuck at
            // whatever level it had reached and stopped responding to presence for as
            // long as the game was paused. Audio fades are presentation, not simulation.
            float step = Mathf.Min(1f, Time.unscaledDeltaTime / _fadeSeconds);
            _currentEnvelope = Mathf.Clamp01(_currentEnvelope + (_targetPresence - _currentEnvelope) * step);

            // At zero volume the clip stops entirely rather than playing silently, so a
            // player who has muted the hum is not paying for a live PCM callback that
            // nobody can hear.
            bool audible = _volume > 0.001f;

            if (_currentEnvelope > 0.01f && audible)
            {
                if (!_source.isPlaying)
                {
                    _source.time = 0f;
                    _source.Play();
                }

                // Perceptual curve: the square root keeps the hum from feeling linear.
                _source.volume = Mathf.Sqrt(_currentEnvelope) * 0.55f * _volume;
            }
            else if (_source.isPlaying)
            {
                _source.Stop();
            }
        }

        private void OnHumRead(float[] data)
        {
            const double twoPi = System.Math.PI * 2.0;
            double rate = SampleRate;

            // Snapshot the envelope once per buffer rather than per sample. It is the
            // only piece of shared state here, and hoisting the volatile read out of
            // the inner loop also guarantees every sample in a buffer uses one value,
            // which is what keeps the waveform free of a step at the buffer boundary.
            double envelope = _currentEnvelope;
            if (envelope <= 0.0001)
            {
                // Silence requested: emit it and do not burn the phase accumulators
                // forward, so resuming does not jump the waveform to an arbitrary point.
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = 0f;
                }

                return;
            }

            for (int i = 0; i < data.Length; i++)
            {
                _detunePhase += twoPi * 0.09 / rate;
                double freqANow = FreqA + System.Math.Sin(_detunePhase) * 0.55;
                double freqBNow = FreqB + System.Math.Sin(_detunePhase + 1.7) * 0.55;

                _phaseA += twoPi * freqANow / rate;
                _phaseB += twoPi * freqBNow / rate;
                _slowLfoPhase += twoPi * 0.17 / rate;

                // A beat: the two partials slow-dance in and out of phase.
                double breath = 0.72 + 0.28 * System.Math.Sin(_slowLfoPhase);
                double sample = (System.Math.Sin(_phaseA) + System.Math.Sin(_phaseB)) * 0.5 * breath * envelope * 0.60;

                data[i] = (float)(System.Math.Max(-1.0, System.Math.Min(1.0, sample)));
            }
        }

        /// <summary>
        /// Builds a seamless looping bed: two low sines a fifth apart under a very
        /// slow tremolo. Generated once, shared by every source that asks for it.
        ///
        /// Timed in development builds, because this is a few hundred thousand
        /// <c>Math.Sin</c> calls on the main thread during the bootstrap's Start, and
        /// the real number on the target machine is worth knowing rather than guessing.
        /// </summary>
        public static AudioClip CreateDroneBed(float seconds)
        {
            const int rate = 48000;
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(rate * seconds));

            bool timed = Debug.isDebugBuild;
            long started = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0L;

            AudioClip clip = AudioClip.Create(GeneratedBedName, sampleCount, 1, rate, false);
            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / rate;
                double tremolo = 0.85 + 0.15 * System.Math.Sin(2.0 * System.Math.PI * 0.07 * t);
                double body = System.Math.Sin(2.0 * System.Math.PI * 55.0 * t) * 0.6
                              + System.Math.Sin(2.0 * System.Math.PI * 82.5 * t) * 0.3;

                samples[i] = (float)(body * tremolo * 0.5);
            }

            // Cross-fade the tail into the head so the loop point is inaudible. The
            // tail is zeroed afterwards: without that the seam region plays twice,
            // because the head now begins with a copy of it.
            int crossfade = Mathf.Min(sampleCount / 8, rate / 2);
            for (int i = 0; i < crossfade; i++)
            {
                float t = i / (float)crossfade;
                samples[i] = Mathf.Lerp(samples[sampleCount - crossfade + i], samples[i], t);
                samples[sampleCount - crossfade + i] = 0f;
            }

            clip.SetData(samples, 0);

            if (timed)
            {
                double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;
                TriangleLog.Log($"Generated the {sampleCount:0} sample drone bed in {ms:0.0} ms.");
            }

            return clip;
        }

        private void OnDestroy()
        {
            if (_source != null)
            {
                _source.Stop();
            }

            if (_clip != null)
            {
                Destroy(_clip);
            }
        }
    }
}
