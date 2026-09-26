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

        private AudioClip _clip;
        private AudioSource _source;

        private double _phaseA;
        private double _phaseB;
        private double _detunePhase;
        private double _slowLfoPhase;

        private float _targetPresence;
        private float _currentEnvelope;
        private float _fadeSeconds = 1f;

        public float HumPresence => _currentEnvelope;
        public bool IsHumming => _source != null && _source.isPlaying;

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

            float step = Mathf.Min(1f, Time.deltaTime / _fadeSeconds);
            _currentEnvelope += (_targetPresence - _currentEnvelope) * step;
            _currentEnvelope = Mathf.Clamp01(_currentEnvelope);

            if (_currentEnvelope > 0.01f)
            {
                if (!_source.isPlaying)
                {
                    _source.time = 0f;
                    _source.Play();
                }

                // Perceptual curve: the square root keeps the hum from feeling linear.
                _source.volume = Mathf.Sqrt(_currentEnvelope) * 0.55f;
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
                double sample = (System.Math.Sin(_phaseA) + System.Math.Sin(_phaseB)) * 0.5 * breath * _currentEnvelope * 0.60;

                data[i] = (float)(System.Math.Max(-1.0, System.Math.Min(1.0, sample)));
            }
        }

        /// <summary>
        /// Builds a seamless looping bed: two low sines a fifth apart under a very
        /// slow tremolo. Generated once, shared by every source that asks for it.
        /// </summary>
        public static AudioClip CreateDroneBed(float seconds)
        {
            const int rate = 48000;
            int sampleCount = Mathf.Max(1, Mathf.RoundToInt(rate * seconds));

            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
            {
                double t = (double)i / rate;
                double tremolo = 0.85 + 0.15 * System.Math.Sin(2.0 * System.Math.PI * 0.07 * t);
                double body = System.Math.Sin(2.0 * System.Math.PI * 55.0 * t) * 0.6
                              + System.Math.Sin(2.0 * System.Math.PI * 82.5 * t) * 0.3;

                samples[i] = (float)(body * tremolo * 0.5);
            }

            // Cross-fade the tail into the head so the loop point is inaudible.
            int crossfade = Mathf.Min(sampleCount / 8, rate / 2);
            for (int i = 0; i < crossfade; i++)
            {
                float t = i / (float)crossfade;
                samples[i] = Mathf.Lerp(samples[sampleCount - crossfade + i], samples[i], t);
            }

            AudioClip clip = AudioClip.Create("TriangleDroneBed", sampleCount, 1, rate, false);
            clip.SetData(samples, 0);
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
