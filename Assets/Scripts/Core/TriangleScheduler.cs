using System;
using System.Collections.Generic;
using UnityEngine;

namespace Triangle.Core
{
    /// <summary>
    /// Deferred callbacks for systems that are not MonoBehaviours.
    ///
    /// Unreal's subsystems scheduled work with <c>FTimerHandle</c> on the world's timer
    /// manager, which any class could reach. Unity has no equivalent, and
    /// <c>MonoBehaviour.Invoke</c> only works for methods that actually exist on that
    /// component - calling it with a plain class's method name fails silently, which is
    /// exactly the bug this type exists to prevent.
    ///
    /// Callers register a string key instead of a handle. Re-scheduling the same key
    /// replaces the pending callback rather than queueing a second one, which mirrors
    /// Unreal's "clear this timer, then set it again" idiom and is what the door
    /// transition, the wake difference line and the manifestation resolver all rely on.
    ///
    /// Timing is unscaled by default. Everything scheduled through here is dream flow
    /// rather than simulation - a fade-and-swap, a subtitle beat, the half-second gap
    /// before a wrongness arrives - and the pause menu sets <c>Time.timeScale</c> to
    /// zero. On scaled time that froze every pending callback for as long as the game
    /// was paused, so a wake's difference line would stall mid-sentence and a queued
    /// load would sit unresolved behind a pause screen. Callers that genuinely want
    /// simulation time opt in per entry.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleScheduler : MonoBehaviour
    {
        private struct Pending
        {
            public float Remaining;
            public Action Callback;
            public bool Scaled;
        }

        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private readonly List<string> _keys = new List<string>();

        /// <summary>Number of callbacks currently waiting to fire.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Schedules <paramref name="callback"/> to run after <paramref name="delaySeconds"/>
        /// of unscaled time. An existing entry under the same key is replaced.
        /// </summary>
        public void Schedule(string key, float delaySeconds, Action callback)
        {
            Schedule(key, delaySeconds, callback, scaled: false);
        }

        /// <summary>
        /// Schedules <paramref name="callback"/>, choosing explicitly whether the delay
        /// is measured in scaled or unscaled time.
        /// </summary>
        /// <param name="scaled">
        /// True to have the delay respect <c>Time.timeScale</c>, so pausing holds the
        /// callback. Only correct for work that is part of the simulation; story beats
        /// and transitions want the default unscaled behaviour.
        /// </param>
        public void Schedule(string key, float delaySeconds, Action callback, bool scaled)
        {
            if (string.IsNullOrEmpty(key) || callback == null)
            {
                return;
            }

            if (float.IsNaN(delaySeconds) || float.IsInfinity(delaySeconds))
            {
                delaySeconds = 0f;
            }

            _pending[key] = new Pending
            {
                Remaining = Mathf.Max(0f, delaySeconds),
                Callback = callback,
                Scaled = scaled
            };
        }

        /// <summary>Cancels a pending callback. Safe to call for a key that is not set.</summary>
        public void Cancel(string key)
        {
            if (!string.IsNullOrEmpty(key))
            {
                _pending.Remove(key);
            }
        }

        public void CancelAll()
        {
            _pending.Clear();
        }

        private void Update()
        {
            if (_pending.Count == 0)
            {
                return;
            }

            float unscaledDelta = Time.unscaledDeltaTime;
            float scaledDelta = Time.deltaTime;

            // Snapshot the keys first. Writing back to the dictionary while
            // enumerating it throws, and a callback firing mid-sweep would add or
            // remove entries underneath the enumerator.
            _keys.Clear();
            foreach (KeyValuePair<string, Pending> pair in _pending)
            {
                _keys.Add(pair.Key);
            }

            for (int i = 0; i < _keys.Count; i++)
            {
                string key = _keys[i];
                if (!_pending.TryGetValue(key, out Pending entry))
                {
                    continue; // cancelled by an earlier callback this frame
                }

                float remaining = entry.Remaining - (entry.Scaled ? scaledDelta : unscaledDelta);
                if (remaining <= 0f)
                {
                    _pending.Remove(key);
                    entry.Callback?.Invoke();
                }
                else
                {
                    entry.Remaining = remaining;
                    _pending[key] = entry;
                }
            }

            _keys.Clear();
        }

        private void OnDestroy()
        {
            _pending.Clear();
        }
    }
}
