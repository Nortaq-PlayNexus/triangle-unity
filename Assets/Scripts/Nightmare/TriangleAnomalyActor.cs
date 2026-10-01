using Triangle.Core;
using Triangle.World;
using UnityEngine;

namespace Triangle.Nightmare
{
    /// <summary>
    /// A short-lived non-interactive intrusion "cut into the dream": razor gaps,
    /// corners pushed slightly apart, distant points, impossible doorways.
    ///
    /// Port of ATriangleAnomalyActor. The three UE static mesh components (primary
    /// plus two auxiliaries used by the three-blade doorway) become three child
    /// shapes under one root, and the actor's fade envelope is driven from Update
    /// exactly as the UE tick did it.
    /// </summary>
    public class TriangleAnomalyActor : MonoBehaviour
    {
        /// <summary>Wrong colour for the dream - almost black, faintly blue.</summary>
        private static readonly Color IntrusionColor = new Color(0.02f, 0.03f, 0.045f);

        /// <summary>Near-zero scale the envelope starts and ends at, so nothing pops.</summary>
        private const float MinScale = 0.001f;

        [SerializeField] private float fadeInTime = 0.35f;
        [SerializeField] private float fadeOutTime = 0.6f;

        private Transform _primary;
        private Transform _auxA;
        private Transform _auxB;

        private float _lifespan;
        private float _fadeIn;
        private float _fadeOut;
        private float _ageSeconds;
        private float _buildScale = 1f;
        private bool _active;

        public TriangleAnomalyKind ActiveKind { get; private set; }
        public bool IsActive => _active;
        public Vector3 Location => transform.position;

        private void Awake()
        {
            _primary = CreatePart("Primary");
            _auxA = CreatePart("AuxA");
            _auxB = CreatePart("AuxB");

            gameObject.SetActive(false);
        }

        private Transform CreatePart(string partName)
        {
            GameObject shape = TriangleShapes.CreateShape(partName, TriangleShapes.Cube, transform);
            shape.transform.localScale = Vector3.zero;
            MeshRenderer meshRenderer = shape.GetComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = TriangleShapes.GetUnlitMaterial(IntrusionColor);
            meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return shape.transform;
        }

        public void Configure(TriangleAnomalyKind kind, float duration, Vector3 spawnLocation, Quaternion spawnRotation)
        {
            ActiveKind = kind;

            // Keep the actor alive through its full fade-in/out envelope. The Director
            // uses the same effective minimum when scheduling recognition resolution.
            _lifespan = float.IsNaN(duration) || float.IsInfinity(duration) ? 5f : Mathf.Max(2.5f, duration);
            _ageSeconds = 0f;
            _active = true;

            // The envelope is resolved once, here, rather than re-derived per frame.
            //
            // Both lengths are inspector-tunable and both were used as divisors, so a
            // zero produced a NaN scale on the fade-in and an anomaly that never
            // finished on the fade-out. A fade-in longer than the whole lifespan had
            // the same class of problem from the other direction: the body never
            // reached full size and never started its exit. Normalising the pair
            // against the lifespan here means the envelope is always well formed,
            // whatever the inspector says.
            _fadeIn = Mathf.Clamp(Sanitise(fadeInTime), 0.01f, _lifespan * 0.5f);
            _fadeOut = Mathf.Clamp(Sanitise(fadeOutTime), 0.01f, _lifespan - _fadeIn);

            BuildForKind(kind);

            transform.SetPositionAndRotation(spawnLocation, spawnRotation);
            gameObject.SetActive(true);
            ApplyAnimatedScale(MinScale);

            TriangleLog.Dream($"Anomaly '{kind}' cut into the dream for {_lifespan:0.0}s.");
        }

        private static float Sanitise(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? 0f : value;
        }

        private void BuildForKind(TriangleAnomalyKind kind)
        {
            // Hide every part first; each branch reuses the three children.
            SetPart(_primary, TriangleShapes.ConeMesh, Vector3.zero, false);
            SetPart(_auxA, TriangleShapes.Cube, Vector3.zero, false);
            SetPart(_auxB, TriangleShapes.Cube, Vector3.zero, false);
            _buildScale = 1f;

            switch (kind)
            {
                case TriangleAnomalyKind.TriangularGap:
                    // A razor-thin dark wedge standing in the plane of a surface, like
                    // the wall has been asked what is behind it and answered with a cut.
                    SetPart(_primary, TriangleShapes.ConeMesh, new Vector3(0.04f, 0.04f, 1.7f), true, 90f, 25f);
                    break;

                case TriangleAnomalyKind.CornerSplit:
                    // A tall thin seam: the corner holds its breath, slightly apart.
                    SetPart(_primary, TriangleShapes.Cube, new Vector3(0.03f, 0.03f, 1.0f), true, 0f, 45f);
                    break;

                case TriangleAnomalyKind.DistantBlackPoint:
                    // A tiny black point far away. The oldest shape - a point.
                    SetPart(_primary, TriangleShapes.Sphere, new Vector3(0.5f, 0.5f, 0.5f), true);
                    _buildScale = 0.6f;
                    break;

                case TriangleAnomalyKind.TriangularDoorway:
                    // Three dark blades forming a downward triangle against the wall:
                    // an impossible door cut into the dream.
                    SetPart(_primary, TriangleShapes.ConeMesh, new Vector3(2.4f, 0.03f, 0.03f), true, 90f, 0f);
                    SetPart(_auxA, TriangleShapes.ConeMesh, new Vector3(2.4f, 0.03f, 0.03f), true, 90f, 120f);
                    SetPart(_auxB, TriangleShapes.ConeMesh, new Vector3(2.4f, 0.03f, 0.03f), true, 90f, 240f);
                    break;
            }
        }

        private void SetPart(Transform part, Mesh mesh, Vector3 localScale, bool visible, float pitch = 0f, float yaw = 0f)
        {
            if (part == null)
            {
                return;
            }

            part.GetComponent<MeshFilter>().sharedMesh = mesh;
            part.localScale = localScale;
            part.localRotation = Cm.Rot(pitch, yaw);
            SetPartVisible(part, visible);
        }

        private static void SetPartVisible(Transform part, bool visible)
        {
            if (part == null)
            {
                return;
            }

            // MeshFilter is not a Behaviour, so visibility is driven purely by the
            // renderer. Leaving the mesh assigned keeps the draw call batchable.
            MeshRenderer meshRenderer = part.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                meshRenderer.enabled = visible;
            }
        }

        private void Update()
        {
            if (!_active)
            {
                return;
            }

            _ageSeconds += Time.deltaTime;

            // _fadeIn and _fadeOut were normalised against the lifespan in Configure,
            // so the two windows cannot overlap and the divisors here are non-zero.
            float outStart = _lifespan - _fadeOut;

            if (_ageSeconds < _fadeIn)
            {
                ApplyAnimatedScale(Mathf.Lerp(MinScale, 1f, _ageSeconds / _fadeIn));
            }
            else if (_ageSeconds >= outStart)
            {
                float exitT = Mathf.Clamp01((_lifespan - _ageSeconds) / _fadeOut);
                ApplyAnimatedScale(Mathf.Lerp(MinScale, 1f, exitT));
                if (exitT <= 0.005f)
                {
                    Finish();
                }
            }
        }

        private void ApplyAnimatedScale(float scaleFactor)
        {
            transform.localScale = new Vector3(scaleFactor * _buildScale, scaleFactor * _buildScale, scaleFactor * _buildScale);
        }

        private void Finish()
        {
            Despawn();
        }

        /// <summary>Immediate removal used by wake / environment-change withdrawal.</summary>
        public void ShutDownNow()
        {
            Despawn();
        }

        private void Despawn()
        {
            if (!_active)
            {
                return;
            }

            _active = false;
            TriangleLog.DreamVerbose("Anomaly receded.");
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            _active = false;
        }
    }
}
