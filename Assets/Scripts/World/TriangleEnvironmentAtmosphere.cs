using UnityEngine;
using UnityEngine.Rendering;

namespace Triangle.World
{
    /// <summary>
    /// One environment's contribution to the global render settings.
    ///
    /// Ambient light and fog are process-wide in Unity - there is no per-camera or
    /// per-volume equivalent short of building a custom shader. An environment
    /// therefore cannot own them in the ordinary sense, and the two builders used to
    /// simply write them and walk away.
    ///
    /// That left the project render settings permanently mutated after the dream
    /// ended, and it made the order of teardown matter in a way nothing documented.
    /// This type makes the ownership explicit: the most recently applied environment
    /// holds the settings, and the project defaults come back when none does.
    ///
    /// Teardown ordering is the reason this is a component rather than a helper on the
    /// builder. The flow destroys the outgoing builder and creates the incoming one in
    /// the same statement pair, but Object.Destroy is deferred to the end of the frame
    /// while the new builder's Awake runs immediately. The outgoing builder's OnDestroy
    /// therefore lands *after* the incoming environment has already applied its own
    /// settings. A naive save-and-restore in OnDestroy would restore the stale values
    /// and leave the station lit like the bedroom. Tracking the current owner makes the
    /// stale component decline to restore.
    /// </summary>
    [DisallowMultipleComponent]
    public class TriangleEnvironmentAtmosphere : MonoBehaviour
    {
        /// <summary>
        /// The environment that currently owns the global render settings, or null when
        /// none does and the project defaults apply.
        /// </summary>
        public static TriangleEnvironmentAtmosphere Owner { get; private set; }

        [Header("Ambient")]
        public AmbientMode Ambient = AmbientMode.Trilight;
        public Color SkyColor = Color.grey;
        public Color EquatorColor = Color.grey;
        public Color GroundColor = Color.black;
        [Range(0f, 1f)] public float Intensity = 1f;

        [Header("Fog")]
        public bool FogEnabled = true;
        public FogMode Fog = FogMode.ExponentialSquared;
        [Range(0f, 0.2f)] public float FogDensity = 0.01f;
        public Color FogColor = Color.grey;

        /// <summary>
        /// The settings in force before any environment claimed them. Captured once,
        /// lazily, because a component's Awake may run after something else has already
        /// written the render settings.
        /// </summary>
        private static bool _defaultsCaptured;
        private static AmbientMode _defaultAmbient;
        private static Color _defaultSky;
        private static Color _defaultEquator;
        private static Color _defaultGround;
        private static float _defaultIntensity;
        private static bool _defaultFogEnabled;
        private static FogMode _defaultFogMode;
        private static float _defaultFogDensity;
        private static Color _defaultFogColor;

        private void Awake()
        {
            CaptureDefaultsOnce();

            // Claim before applying, so a component that inspects Owner during its own
            // Awake sees the environment that is actually in force.
            Owner = this;
            Apply();
        }

        private void OnEnable()
        {
            // Awake already covers the normal path. This is only for a component that
            // was disabled and re-enabled, where Awake does not re-run: it has to
            // out-rank whatever claimed the settings while it was off, or re-enabling
            // it would leave the previous owner in charge of an atmosphere that this
            // component still believes it owns.
            if (isActiveAndEnabled && Owner != this)
            {
                Owner = this;
                Apply();
            }
        }

        private void OnDisable()
        {
            if (Owner != this)
            {
                // A newer environment already owns the settings. Restoring now would
                // undo it, which is exactly the bug this type exists to prevent.
                return;
            }

            Owner = null;
            RestoreDefaults();
        }

        /// <summary>Writes this environment's settings to the global render settings.</summary>
        public void Apply()
        {
            RenderSettings.ambientMode = Ambient;
            RenderSettings.ambientSkyColor = SkyColor;
            RenderSettings.ambientEquatorColor = EquatorColor;
            RenderSettings.ambientGroundColor = GroundColor;
            RenderSettings.ambientIntensity = Mathf.Clamp01(Intensity);

            RenderSettings.fog = FogEnabled;
            RenderSettings.fogMode = Fog;
            RenderSettings.fogDensity = Mathf.Max(0f, FogDensity);
            RenderSettings.fogColor = FogColor;
        }

        /// <summary>
        /// Gives up ownership because a replacement environment is taking over.
        ///
        /// Called by the flow immediately before destroying the outgoing builder.
        ///
        /// This is the whole reason the deferred-destroy ordering is safe. Object.Destroy
        /// is deferred to the end of the frame, so without this the sequence would be:
        /// the incoming builder's Awake applies its atmosphere and becomes Owner, then
        /// the outgoing builder is destroyed and its OnDisable sees Owner != this, so it
        /// declines to restore - correct by accident.
        ///
        /// Being explicit removes the dependence on that accident. If the ordering ever
        /// changed, or the incoming environment were built before this call, the
        /// outgoing component would still correctly decline to restore, because Owner
        /// has already been cleared rather than merely reassigned.
        /// </summary>
        public void MarkReplaced()
        {
            if (Owner == this)
            {
                Owner = null;
            }
        }

        private static void CaptureDefaultsOnce()
        {
            if (_defaultsCaptured)
            {
                return;
            }

            _defaultAmbient = RenderSettings.ambientMode;
            _defaultSky = RenderSettings.ambientSkyColor;
            _defaultEquator = RenderSettings.ambientEquatorColor;
            _defaultGround = RenderSettings.ambientGroundColor;
            _defaultIntensity = RenderSettings.ambientIntensity;
            _defaultFogEnabled = RenderSettings.fog;
            _defaultFogMode = RenderSettings.fogMode;
            _defaultFogDensity = RenderSettings.fogDensity;
            _defaultFogColor = RenderSettings.fogColor;
            _defaultsCaptured = true;
        }

        private static void RestoreDefaults()
        {
            if (!_defaultsCaptured)
            {
                return;
            }

            RenderSettings.ambientMode = _defaultAmbient;
            RenderSettings.ambientSkyColor = _defaultSky;
            RenderSettings.ambientEquatorColor = _defaultEquator;
            RenderSettings.ambientGroundColor = _defaultGround;
            RenderSettings.ambientIntensity = _defaultIntensity;
            RenderSettings.fog = _defaultFogEnabled;
            RenderSettings.fogMode = _defaultFogMode;
            RenderSettings.fogDensity = _defaultFogDensity;
            RenderSettings.fogColor = _defaultFogColor;
        }

        /// <summary>
        /// Clears the captured defaults.
        ///
        /// Called on subsystem registration so a play session entered without a domain
        /// reload does not carry last session's "defaults" - which would be whatever
        /// the bedroom happened to leave behind.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            _defaultsCaptured = false;
            Owner = null;
        }
    }
}
