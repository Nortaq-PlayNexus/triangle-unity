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
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleScheduler : MonoBehaviour
    {
        private struct Pending
        {
            public float Remaining;
            public Action Callback;
        }

        private readonly Dictionary<string, Pending> _pending = new Dictionary<string, Pending>();
        private readonly List<string> _keys = new List<string>();

        /// <summary>Number of callbacks currently waiting to fire.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>
        /// Schedules <paramref name="callback"/> to run after
        /// <paramref name="delaySeconds"/>. An existing entry under the same key is
        /// replaced.
        /// </summary>
        public void Schedule(string key, float delaySeconds, Action callback)
        {
            if (string.IsNullOrEmpty(key) || callback == null)
            {
                return;
            }

            if (float.IsNaN(delaySeconds) || float.IsInfinity(delaySeconds))
            {
                delaySeconds = 0f;
            }

            _pending[key] = new Pending { Remaining = Mathf.Max(0f, delaySeconds), Callback = callback };
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

            float deltaSeconds = Time.deltaTime;

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

                float remaining = entry.Remaining - deltaSeconds;
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
