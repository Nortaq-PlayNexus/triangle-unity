using System.Collections.Generic;
using Triangle.Core;
using Triangle.Player;
using UnityEngine;

namespace Triangle.Nightmare
{
    /// <summary>
    /// Places and tracks the "cuts into the dream". Owns nothing visual itself; it
    /// spawns TriangleAnomalyActors, keeps at most a handful alive at once (oldest
    /// first), and can clear everything when the dream resets.
    ///
    /// Port of UTriangleAnomalySubsystem. The UE WorldSubsystem becomes a plain
    /// class owned by the bootstrap and handed to the director.
    /// </summary>
    public class TriangleAnomalySystem
    {
        private readonly List<TriangleAnomalyActor> _active = new List<TriangleAnomalyActor>();

        /// <summary>Maximum simultaneous intrusions; the oldest is dropped beyond this.</summary>
        public int MaxConcurrent = 2;

        public int ActiveAnomalyCount
        {
            get
            {
                PruneGhosts();
                return _active.Count;
            }
        }

        /// <summary>Spawns a manifestation cut of the given kind near the supplied world point.</summary>
        public TriangleAnomalyActor Spawn(TriangleAnomalyKind kind, float duration, Vector3 aroundLocation)
        {
            if (kind == TriangleAnomalyKind.None)
            {
                return null;
            }

            PruneGhosts();

            // Enforce the concurrent budget before spawning. A malformed zero budget
            // must never turn the eviction loop into an out-of-bounds access.
            int budget = Mathf.Max(1, MaxConcurrent);
            while (_active.Count >= budget)
            {
                TriangleAnomalyActor oldest = _active[0];
                _active.RemoveAt(0);
                if (oldest != null)
                {
                    oldest.ShutDownNow();
                }
            }

            Vector3 renderPoint = ChooseRenderPoint(kind, aroundLocation);
            Quaternion renderRotation = Cm.Rot(0f, Random.Range(0f, 360f));

            var host = new GameObject("TriangleAnomaly");
            var anomaly = host.AddComponent<TriangleAnomalyActor>();
            anomaly.Configure(kind, duration, renderPoint, renderRotation);
            _active.Add(anomaly);

            TriangleLog.DreamVerbose($"Anomaly spawned (alive={_active.Count}).");
            return anomaly;
        }

        /// <summary>Removes every active anomaly (dream reset / wake).</summary>
        public void DespawnAll()
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i] != null)
                {
                    _active[i].ShutDownNow();
                }
            }

            _active.Clear();
            TriangleLog.DreamVerbose("All anomalies withdrawn.");
        }

        private void PruneGhosts()
        {
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                if (_active[i] == null)
                {
                    _active.RemoveAt(i);
                }
            }
        }

        /// <summary>Samples an appropriate spawn point near the player for a cut kind.</summary>
        private Vector3 ChooseRenderPoint(TriangleAnomalyKind kind, Vector3 around)
        {
            // Respect the camera's facing so intrusions land where the player is likely
            // to be looking without ever appearing on top of them.
            TrianglePlayer player = TriangleRuntime.Player;
            Vector3 facing = player != null && player.Camera != null
                ? player.Camera.transform.forward
                : Vector3.forward;

            Vector3 facingFlat = new Vector3(facing.x, 0f, facing.z);
            if (facingFlat.sqrMagnitude < 0.0001f)
            {
                facingFlat = Vector3.forward;
            }

            facingFlat.Normalize();

            if (kind == TriangleAnomalyKind.DistantBlackPoint)
            {
                // Far, faint, unapproachable: a point of the dream hovering past reach.
                float distance = Random.Range(Cm.M(1800f), Cm.M(3200f));
                Vector3 dir = (facingFlat * Random.Range(0.55f, 1f) +
                               new Vector3(Random.Range(-0.4f, 0.4f), 0f, Random.Range(-0.4f, 0.4f))).normalized;
                return around + dir * distance + Vector3.up * Random.Range(Cm.M(-100f), Cm.M(180f));
            }

            // Wall-ish sampling: a short step from the player. Keep the cut in the same
            // world-height band as the active environment.
            float fraction = Random.Range(0.4f, 0.95f);
            float side = Random.Range(-1f, 1f);
            float directionSign = Random.value < 0.5f ? -1f : 1f;
            Vector3 lateral = new Vector3(-facingFlat.z, 0f, facingFlat.x);
            Vector3 dir2 = (facingFlat * (fraction * directionSign) + lateral * side).normalized;
            Vector3 point = around + dir2 * Random.Range(Cm.M(70f), Cm.M(230f));
            Vector3 candidate = new Vector3(point.x, point.y + Random.Range(Cm.M(-40f), Cm.M(140f)), point.z);

            if (player != null && player.Camera != null && Physics.Raycast(
                    around + Vector3.up * Cm.M(80f),
                    (candidate + Vector3.up * Cm.M(80f) - around).normalized,
                    out RaycastHit surfaceHit,
                    Vector3.Distance(around, candidate) + Cm.M(80f),
                    ~0,
                    QueryTriggerInteraction.Ignore))
            {
                return surfaceHit.point + surfaceHit.normal * Cm.M(4f);
            }

            return candidate;
        }
    }
}
