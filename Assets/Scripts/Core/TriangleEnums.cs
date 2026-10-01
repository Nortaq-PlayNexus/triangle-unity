using UnityEngine;

namespace Triangle.Core
{
    /// <summary>
    /// Unit and axis conversion between Unreal-style centimetre design values and
    /// Unity metres.
    ///
    /// The Unreal slice authored every environment in centimetres, Z-up and with
    /// X forward / Y right. Unity is Y-up with Z forward / X right. The basis
    /// mapping used here is
    ///
    ///     UE  X (forward) -> Unity Z
    ///     UE  Y (right)   -> Unity X
    ///     UE  Z (up)      -> Unity Y
    ///
    /// which is a pure rotation (an even permutation), so the dream rooms keep
    /// their authored proportions, handedness and facing directions and the ported
    /// builder numbers can stay verbatim.
    /// </summary>
    public static class Cm
    {
        public const float ToMetres = 0.01f;

        /// <summary>Centimetres to metres, for scalars (distances, radii, intensities of scale).</summary>
        public static float M(float centimetres) => centimetres * ToMetres;

        /// <summary>Converts an Unreal centimetre position to a Unity metre position.</summary>
        public static Vector3 Pos(float x, float y, float z) => new Vector3(M(y), M(z), M(x));

        /// <summary>Converts an Unreal centimetre position to a Unity metre position.</summary>
        public static Vector3 Pos(Vector3 ueCentimetres) => new Vector3(M(ueCentimetres.y), M(ueCentimetres.z), M(ueCentimetres.x));

        /// <summary>Converts an Unreal direction to a Unity direction (no unit change).</summary>
        public static Vector3 Dir(float x, float y, float z) => new Vector3(y, z, x);

        /// <summary>Converts an Unreal direction to a Unity direction (no unit change).</summary>
        public static Vector3 Dir(Vector3 ue) => new Vector3(ue.y, ue.z, ue.x);

        /// <summary>
        /// Converts an Unreal rotator (pitch, yaw, roll) to a Unity rotation.
        ///
        /// The forward axis is built by hand rather than handed to
        /// <c>Quaternion.LookRotation</c> because that call is degenerate at the poles:
        /// at +/-90 degrees of pitch the forward vector becomes parallel to the up
        /// vector, Unity falls back to identity, and the requested pitch is silently
        /// discarded. The anomaly builder rotates parts by 90 degrees on purpose (the
        /// razor gap lying in the plane of a wall, the three doorway blades), so that
        /// fallback was losing real authored orientation.
        ///
        /// The basis is built explicitly instead, and roll is applied about the
        /// resulting forward axis so it is honoured rather than dropped.
        /// </summary>
        public static Quaternion Rot(float pitchDegrees, float yawDegrees, float rollDegrees = 0f)
        {
            if (float.IsNaN(pitchDegrees) || float.IsInfinity(pitchDegrees)) pitchDegrees = 0f;
            if (float.IsNaN(yawDegrees) || float.IsInfinity(yawDegrees)) yawDegrees = 0f;
            if (float.IsNaN(rollDegrees) || float.IsInfinity(rollDegrees)) rollDegrees = 0f;

            float pitch = pitchDegrees * Mathf.Deg2Rad;
            float yaw = yawDegrees * Mathf.Deg2Rad;

            // Unreal FRotator: X forward, Y right, Z up.
            float cosPitch = Mathf.Cos(pitch);
            var forwardUe = new Vector3(cosPitch * Mathf.Cos(yaw), cosPitch * Mathf.Sin(yaw), Mathf.Sin(pitch));
            var rightUe = new Vector3(-Mathf.Sin(yaw), Mathf.Cos(yaw), 0f);
            var upUe = Vector3.Cross(forwardUe, rightUe);

            Vector3 forward = Dir(forwardUe);
            Vector3 right = Dir(rightUe);
            Vector3 up = Dir(upUe);

            if (rollDegrees != 0f)
            {
                Quaternion roll = Quaternion.AngleAxis(rollDegrees, forward);
                forward = roll * forward;
                right = roll * right;
                up = roll * up;
            }

            return Quaternion.LookRotation(forward, up);
        }

        /// <summary>Converts a zero-rotation placeholder.</summary>
        public static Quaternion Identity => Quaternion.identity;

        /// <summary>
        /// Local scale for a mesh authored on Unreal's 100 cm unit cube. A UE
        /// relative scale of S therefore becomes a Unity local scale of S, which
        /// keeps every authored scale in the builders unchanged.
        /// </summary>
        public static Vector3 Scale(float x, float y, float z) => new Vector3(x, y, z);
    }

    /// <summary>The environments included in the current playable vertical slice.</summary>
    public enum TriangleDreamEnvironment
    {
        ChildhoodBedroom,
        UndergroundStation,

        /// <summary>The yard outside the bedroom. The first environment with weather.</summary>
        YardOutside
    }

    /// <summary>
    /// Shared states for nightmare entities. Interpreted by each entity's own
    /// behaviour logic; loose enough that entities that are "not really chasing"
    /// (like The Floating Man) can map states to ambivalent behaviour.
    /// </summary>
    public enum TriangleNightmareEntityState
    {
        Idle,
        Follow,
        Observe,
        Disappear,
        Reappear,
        LeadPlayer,
        Wait
    }

    /// <summary>Lucidity stages. Hidden/visible 0..100, event-gated, never a passive timer.</summary>
    public enum TriangleLucidityLevel
    {
        Low,
        Medium,
        High,
        Maximum
    }

    /// <summary>
    /// Recognition stages - deliberately separate from Lucidity.
    /// Lucidity is "I know I am dreaming". Recognition is "I know what is here".
    /// </summary>
    public enum TriangleRecognitionLevel
    {
        Unknowing,
        Noticing,
        Recognizing,
        Anticipating,
        Master
    }

    /// <summary>
    /// The possible bodies The Triangle chooses to appear in. The Triangle has no
    /// single body - it manifests as intrusions into the dream.
    /// </summary>
    public enum TriangleManifestationKind
    {
        /// <summary>Existing corner point - the first learned shape.</summary>
        Point,

        /// <summary>Presence with no visible form at all (the hum, an absence).</summary>
        HumOnly,

        /// <summary>Razor-thin triangular gap cut into a surface.</summary>
        TriangularGap,

        /// <summary>A vertical corner pushed slightly apart from itself.</summary>
        CornerSplit,

        /// <summary>A tiny black point hanging in the distance.</summary>
        DistantBlackPoint,

        /// <summary>An impossible triangular doorway cut into a wall.</summary>
        TriangularDoorway,

        /// <summary>Reserved for the human form (mother) - not spawned by the slice.</summary>
        HumanForm
    }

    /// <summary>Which visual the anomaly system should raise for a manifestation.</summary>
    public enum TriangleAnomalyKind
    {
        None,
        TriangularGap,
        CornerSplit,
        DistantBlackPoint,
        TriangularDoorway
    }

    /// <summary>Result of applying a save snapshot to the live dream.</summary>
    public enum TriangleSaveApplyResult
    {
        Applied,
        Queued,
        Failed
    }

    /// <summary>
    /// Unity's equivalent of an FName. The Unreal source used FName for pattern ids
    /// and motif ids; a string with a "none = empty" convention maps directly.
    /// </summary>
    public static class Name
    {
        public static readonly string None = string.Empty;

        public static bool IsNone(string value) => string.IsNullOrEmpty(value);
    }
}
