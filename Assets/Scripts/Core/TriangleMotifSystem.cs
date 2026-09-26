using System;
using System.Collections.Generic;
using Triangle.Core;
using Triangle.Player;
using UnityEngine;

// Both System and UnityEngine are imported, so pin the RNG the slice actually uses.
using Random = UnityEngine.Random;

namespace Triangle.Core
{
    /// <summary>Per-motif scheduling configuration.</summary>
    public struct TriangleMotifConfig
    {
        public float MinCooldown;
        public float MaxCooldown;
        public float RecognitionRequirement;

        public TriangleMotifConfig(float minCooldown, float maxCooldown, float recognitionRequirement)
        {
            MinCooldown = minCooldown;
            MaxCooldown = maxCooldown;
            RecognitionRequirement = recognitionRequirement;
        }
    }

    /// <summary>
    /// Owns the dream clock and schedules recurring motifs with cooldowns and
    /// recognition gates.
    ///
    /// Port of UTriangleMotifSubsystem. The UE subsystem ran a 1 s repeating world
    /// timer; here the director's Update calls <see cref="Tick"/> and this class
    /// accumulates to the same one-second cadence.
    /// </summary>
    public class TriangleMotifSystem
    {
        private struct MotifEntry
        {
            public TriangleMotifConfig Config;
            public bool Enabled;
            public float NextScheduledTime;
        }

        private readonly Dictionary<string, MotifEntry> _motifs = new Dictionary<string, MotifEntry>();
        private bool _schedulingEnabled;
        private float _clockAccumulator;

        /// <summary>Seconds of dream time elapsed since the system was created.</summary>
        public float DreamClock { get; private set; }

        /// <summary>Raised when a motif occurs, whether scheduled or requested.</summary>
        public event Action<string> OnMotif;

        public bool IsSchedulingEnabled => _schedulingEnabled;

        public void RegisterMotif(string id, TriangleMotifConfig config)
        {
            MotifEntry entry = _motifs.TryGetValue(id, out MotifEntry existing) ? existing : default;
            entry.Config = config;
            entry.Config.MinCooldown = Mathf.Max(0.1f, entry.Config.MinCooldown);
            entry.Config.MaxCooldown = Mathf.Max(entry.Config.MinCooldown, entry.Config.MaxCooldown);
            entry.Config.RecognitionRequirement = Mathf.Clamp(entry.Config.RecognitionRequirement, 0f, 100f);
            entry.Enabled = true;
            entry.NextScheduledTime = DreamClock + Random.Range(entry.Config.MinCooldown, entry.Config.MaxCooldown);
            _motifs[id] = entry;
            TriangleLog.DreamVerbose($"Motif '{id}' registered.");
        }

        public void SetMotifEnabled(string id, bool enabled)
        {
            if (_motifs.TryGetValue(id, out MotifEntry entry))
            {
                entry.Enabled = enabled;
                _motifs[id] = entry;
            }
        }

        public void EnableAutomaticScheduling(bool on)
        {
            _schedulingEnabled = on;
            if (!on)
            {
                _clockAccumulator = 0f;
            }
        }

        /// <summary>Reschedules a motif inside its normal window without firing it.</summary>
        public void QueueAutomaticMotif(string id)
        {
            if (!_motifs.TryGetValue(id, out MotifEntry entry))
            {
                return;
            }

            entry.NextScheduledTime = DreamClock + Random.Range(
                Mathf.Max(0.1f, entry.Config.MinCooldown),
                Mathf.Max(entry.Config.MinCooldown, entry.Config.MaxCooldown));
            _motifs[id] = entry;
        }

        /// <summary>Fires a motif immediately and reschedules it.</summary>
        public bool RequestMotif(string id)
        {
            if (!_motifs.ContainsKey(id))
            {
                TriangleLog.Dream($"RequestMotif('{id}') but that motif is not registered.");
                return false;
            }

            OnMotif?.Invoke(id);
            QueueAutomaticMotif(id);
            TriangleLog.DreamVerbose($"Motif '{id}' occurred.");
            return true;
        }

        /// <summary>Advances the dream clock. Called once per frame by the director.</summary>
        public void Tick(float deltaSeconds)
        {
            if (!_schedulingEnabled)
            {
                return;
            }

            _clockAccumulator += deltaSeconds;
            if (_clockAccumulator < 1f)
            {
                return;
            }

            // Drain whole seconds so a hitch does not skip a scheduling window.
            int steps = Mathf.FloorToInt(_clockAccumulator);
            _clockAccumulator -= steps;
            for (int i = 0; i < steps; i++)
            {
                ClockTick();
            }
        }

        private void ClockTick()
        {
            DreamClock += 1f;

            // Snapshot the ids before rescheduling anything. Writing back into the
            // dictionary while enumerating it throws, and the Unreal original got away
            // with it only because C++ permits mutating the current element in a
            // range-for. Both the reschedule path and the handler dispatch below can
            // also add or remove entries mid-tick.
            _sweep.Clear();
            foreach (KeyValuePair<string, MotifEntry> pair in _motifs)
            {
                _sweep.Add(pair.Key);
            }

            var due = new List<string>();
            for (int i = 0; i < _sweep.Count; i++)
            {
                string id = _sweep[i];
                if (!_motifs.TryGetValue(id, out MotifEntry entry))
                {
                    continue; // removed by an earlier iteration this tick
                }

                if (!entry.Enabled || DreamClock < entry.NextScheduledTime)
                {
                    continue;
                }

                float recognitionRequirement = entry.Config.RecognitionRequirement;
                if (recognitionRequirement > 0f)
                {
                    TrianglePlayer player = TriangleRuntime.Player;
                    float playerRecognition = player != null ? player.Recognition.GetRecognition() : 0f;

                    // Motifs gate on the player's actual pattern knowledge. Until then
                    // the motif reschedules inside its window and interleaves naturally.
                    if (playerRecognition < recognitionRequirement)
                    {
                        entry.NextScheduledTime = DreamClock + Random.Range(entry.Config.MinCooldown, entry.Config.MaxCooldown);
                        _motifs[id] = entry;
                        continue;
                    }
                }

                due.Add(id);
                entry.NextScheduledTime = DreamClock + Random.Range(entry.Config.MinCooldown, entry.Config.MaxCooldown);
                _motifs[id] = entry;
            }

            _sweep.Clear();

            // Broadcast after the sweep so handlers can safely RequestMotif, register
            // new motifs, or disable existing ones without invalidating an enumerator.
            for (int i = 0; i < due.Count; i++)
            {
                OnMotif?.Invoke(due[i]);
            }
        }

        private readonly List<string> _sweep = new List<string>();
    }
}
