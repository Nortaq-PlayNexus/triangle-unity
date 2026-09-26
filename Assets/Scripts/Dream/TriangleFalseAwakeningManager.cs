using System;
using System.Collections.Generic;
using Triangle.Core;
using Triangle.Player;
using UnityEngine;

namespace Triangle.Dream
{
    /// <summary>
    /// The unreliable wake. Waking is not always real: the dream lets go, then a
    /// small wrongness arrives a beat later, and the player is asked to notice it.
    ///
    /// Port of UTriangleFalseAwakeningManager.
    /// </summary>
    public class TriangleFalseAwakeningManager
    {
        private const int MaxLayer = 32;
        private const float WakeCooldownSeconds = 4f;
        private const float DifferenceDelaySeconds = 2.2f;
        private const float RecognitionRequiredToWake = 75f;

        private const string KeyResolveDifference = "TriangleWake.ResolveDifference";

        // The first wakes are nearly identical to being awake - only one small
        // wrongness separates them. Every difference line is a "how does the player
        // recognise it" beat, not a jumpscare.
        private static readonly string[] DifferenceLines =
        {
            "The clock still says twelve.",
            "The moon is on the wrong side of the window.",
            "The hallway is longer than it was.",
            "The hum is still here.",
            "You are still ten years old."
        };

        private readonly TriangleScheduler _host;

        private int _layerIndex;
        private int _completionCount;
        private float _nextWakeAllowedTime;

        /// <summary>Number of completed false awakenings.</summary>
        public int CompletionCount => _completionCount;

        /// <summary>Index of the layer the player is currently inside.</summary>
        public int CurrentLayer => _layerIndex;

        /// <summary>Raised with the layer index once a wake resolves.</summary>
        public event Action<int> OnFalseAwakening;

        /// <summary>
        /// Raised as soon as a wake is accepted. The Dream Director listens to this
        /// to withdraw every manifestation: the release, the dream's breath, and the
        /// start over.
        /// </summary>
        public event Action OnWakeResetRequested;

        public TriangleFalseAwakeningManager(TriangleScheduler host)
        {
            _host = host;
        }

        public bool TryWake()
        {
            if (Time.time < _nextWakeAllowedTime)
            {
                return false;
            }

            TriangleRuntime.Scheduler?.Cancel(KeyResolveDifference);

            TrianglePlayer player = TriangleRuntime.Player;
            float playerRecognition = 0f;
            bool hasRecognition = false;
            if (player != null)
            {
                playerRecognition = player.Recognition.GetRecognition();
                hasRecognition = true;
            }

            // The dream refuses to let go of players who cannot tell they are in it.
            if (!hasRecognition || playerRecognition < RecognitionRequiredToWake)
            {
                TriangleRuntime.Hud?.PushSubtitle("You close your eyes. The dream does not let go.", 3f);
                return false;
            }

            if (_layerIndex >= MaxLayer)
            {
                TriangleRuntime.Hud?.PushSubtitle("There is no deeper layer to find.", 3f);
                return false;
            }

            // Layer 0: the player's first wake is the dream pretending for a single beat.
            int layer = _layerIndex;
            _layerIndex = Mathf.Min(_layerIndex + 1, MaxLayer);

            TriangleRuntime.Hud?.PushSubtitle("You wake up.", 2f);

            // Then a small wrongness, deferred long enough to feel like the open eye.
            if (DifferenceLines.Length > 0)
            {
                int index = layer < DifferenceLines.Length ? layer : DifferenceLines.Length - 1;
                _pendingDifference = DifferenceLines[index];
                _pendingLayer = layer;
                _host?.Schedule(KeyResolveDifference, DifferenceDelaySeconds, ResolvePendingDifference);
            }
            else
            {
                OnFalseAwakening?.Invoke(layer);
            }

            _completionCount++;
            _nextWakeAllowedTime = Time.time + WakeCooldownSeconds;

            // The release: the dream withdraws, gets its breath back, and starts again.
            OnWakeResetRequested?.Invoke();

            TriangleLog.Dream($"False wake {layer} ('{DescriptorFor(layer)}').");
            return true;
        }

        private string _pendingDifference;
        private int _pendingLayer;

        private void ResolvePendingDifference()
        {
            if (string.IsNullOrEmpty(_pendingDifference))
            {
                return;
            }

            string difference = _pendingDifference;
            int layer = _pendingLayer;
            _pendingDifference = null;

            TriangleRuntime.Hud?.PushSubtitle(difference, 4f);
            OnFalseAwakening?.Invoke(layer);
        }

        /// <summary>Restores wake depth from a save and cancels any stale callback.</summary>
        public void RestoreState(int savedLayer)
        {
            _host?.Cancel(KeyResolveDifference);
            _pendingDifference = null;
            _layerIndex = Mathf.Clamp(savedLayer, 0, MaxLayer);
            _completionCount = _layerIndex;
            _nextWakeAllowedTime = 0f;
        }

        public static string DescriptorFor(int layer)
        {
            switch (layer)
            {
                case 0: return "first layer: almost nothing";
                case 1: return "second layer: one thing wrong";
                case 2: return "third layer: several things wrong";
                case 3: return "fourth layer: the love is wrong";
                default: return "deeper: the shapes are sibling-shaped";
            }
        }
    }
}
