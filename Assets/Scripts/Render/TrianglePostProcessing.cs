using System.Collections.Generic;
using Triangle.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Triangle.Render
{
    /// <summary>
    /// Owns the game's post-processing stack.
    ///
    /// Nothing in this project is an authored asset, and that includes the volume profile.
    /// A VolumeProfile is a ScriptableObject and can be built in code with
    /// <see cref="VolumeProfile.Add{T}"/>, so the whole stack is assembled here at runtime
    /// rather than shipped as a .asset file.
    ///
    /// The reason this is the single largest visual upgrade after the pipeline migration:
    /// without it the frame is raw linear radiance written straight to the screen. Every
    /// surface in the game is a very dark blue-grey, and unwritten linear values in that
    /// range read as flat, chalky and crushed - the exact "painted cardboard" quality the
    /// previous build had. Tonemapping gives the image a filmic roll-off, grading sets the
    /// palette, and bloom is what makes the window and the fluorescent tubes read as light
    /// sources rather than as pale rectangles.
    ///
    /// Everything here is also driven from the accessibility settings, so a player who finds
    /// grain or chromatic aberration uncomfortable can turn it off without leaving the game.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Volume))]
    public class TrianglePostProcessing : MonoBehaviour
    {
        [Header("Base look")]
        [Tooltip("Overall exposure in stops. Negative sits the image in the shadows.")]
        [Range(-3f, 3f)] [SerializeField] private float postExposure = -0.35f;

        [Tooltip("Contrast around mid grey.")]
        [Range(-40f, 40f)] [SerializeField] private float contrast = 12f;

        [Tooltip("Saturation adjustment. Negative is what a dark interior needs.")]
        [Range(-40f, 40f)] [SerializeField] private float saturation = -8f;

        [Tooltip("Pushes the whole image towards its own cyan-blue.")]
        [Range(-10f, 10f)] [SerializeField] private float coldness = -6f;

        [Header("Bloom")]
        [Tooltip("Brightness above which pixels bloom. Thresholds in gamma space, so this needs to sit low for a dark scene.")]
        [Range(0f, 3f)] [SerializeField] private float bloomThreshold = 0.85f;

        [Range(0f, 3f)] [SerializeField] private float bloomIntensity = 0.55f;

        [Range(0f, 1f)] [SerializeField] private float bloomScatter = 0.72f;

        [Header("Grain and aberration")]
        [Range(0f, 1f)] [SerializeField] private float filmGrainIntensity = 0.22f;
        [Range(0f, 1f)] [SerializeField] private float chromaticAberration = 0.08f;

        [Header("Vignette")]
        [Range(0f, 1f)] [SerializeField] private float vignetteIntensity = 0.34f;

        [Range(0.01f, 1f)] [SerializeField] private float vignetteSmoothness = 0.42f;

        [Header("Lens distortion")]
        [Tooltip("Barrel distortion. Small values read as a lens rather than as an effect.")]
        [Range(-0.5f, 0.5f)] [SerializeField] private float lensDistortion = -0.06f;

        private Volume _volume;
        private VolumeProfile _profile;

        // Retained so accessibility can scale them without rebuilding the profile.
        private Bloom _bloom;
        private FilmGrain _filmGrain;
        private ChromaticAberration _chromatic;
        private LensDistortion _lensDistortion;
        private MotionBlur _motionBlur;

        /// <summary>The live global volume, so other systems can add overrides to it.</summary>
        public Volume Volume => _volume;

        /// <summary>
        /// Builds in Awake rather than lazily.
        ///
        /// The volume must exist before the first frame is rendered, and the first frame is
        /// the frame the player sees the dream assemble. Waiting for the first Update would
        /// show one un-tonemapped frame.
        /// </summary>
        private void Awake()
        {
            _volume = GetComponent<Volume>();
            if (_volume == null)
            {
                _volume = gameObject.AddComponent<Volume>();
            }

            // Global, not per-camera: there is one camera and it is created later, by the
            // player. A local volume would need a camera reference that does not exist
            // yet, and would silently do nothing until one was assigned.
            _volume.isGlobal = true;
            _volume.weight = 1f;
            _volume.priority = 0f;

            BuildProfile();
            ApplyAccessibility();
        }

        private void OnEnable()
        {
            TriangleAccessibility.OnSettingsChanged += ApplyAccessibility;
        }

        private void OnDisable()
        {
            TriangleAccessibility.OnSettingsChanged -= ApplyAccessibility;
        }

        private void OnDestroy()
        {
            // The profile is a runtime object this component created. Nothing else holds a
            // reference to it, so without this it survives for the rest of the session.
            if (_profile != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_profile);
                }
                else
                {
                    DestroyImmediate(_profile);
                }

                _profile = null;
            }
        }

        /// <summary>
        /// Assembles the profile.
        ///
        /// Every parameter is written through <c>Override(value)</c> rather than by assigning
        /// to the parameter's value. That is not a style choice: a VolumeComponent only
        /// participates in the stack for parameters whose overrideState is set, so a plain
        /// assignment produces a component that exists, serialises correctly, and does
        /// absolutely nothing.
        /// </summary>
        private void BuildProfile()
        {
            _profile = ScriptableObject.CreateInstance<VolumeProfile>();
            _profile.name = "TrianglePostProcessProfile";

            // Tonemapping first in the stack, which is also where it runs: ACES, because it
            // desaturates and rolls off highlights rather than clipping them, and this scene
            // is lit by one cold source against near-black. That is exactly the case ACES
            // was built for - Neutral preserves hue at the cost of the filmic shadow roll-off
            // the image needs.
            var tonemapping = _profile.Add<Tonemapping>(true);
            tonemapping.mode.Override(TonemappingMode.ACES);

            // Post exposure. The scene is lit for a very dark look, and without pulling the
            // exposure down the mid-tones sit where the ACES curve compresses them and the
            // whole room flattens.
            var adjustments = _profile.Add<ColorAdjustments>(true);
            adjustments.postExposure.Override(postExposure);
            adjustments.contrast.Override(contrast);
            adjustments.saturation.Override(saturation);

            // A cyan-blue filter. The lighting is already cold, so this reinforces the
            // palette rather than introducing it, and it keeps the interior from reading as
            // neutral grey.
            adjustments.colorFilter.Override(new Color(1f + coldness * 0.01f, 1f, 1f - coldness * 0.012f, 1f));

            // Bloom. This is what makes the window read as moonlight and the tubes read as
            // fluorescent: without a glow, a bright emissive surface is just a bright
            // rectangle, and the eye has no cue that it is emitting.
            _bloom = _profile.Add<Bloom>(true);
            _bloom.threshold.Override(bloomThreshold);
            _bloom.intensity.Override(bloomIntensity * TriangleAccessibility.Bloom);
            _bloom.scatter.Override(bloomScatter);

            // High-quality filtering, which upscales the blur with a bicubic filter. The
            // cheap path scatters visible blocking artefacts around bright edges, which on
            // thin geometry like the fluorescent tubes is worse than no bloom at all.
            _bloom.highQualityFiltering.Override(true);
            _bloom.downscale.Override(BloomDownscaleMode.Quarter);
            _bloom.tint.Override(new Color(0.86f, 0.92f, 1f));

            // Vignette. Not decoration: it is what concentrates a very dark scene's limited
            // contrast around the centre of frame, and it stops the corners of a room from
            // competing with the thing the player is meant to be looking at.
            var vignette = _profile.Add<Vignette>(true);
            vignette.intensity.Override(vignetteIntensity);
            vignette.smoothness.Override(vignetteSmoothness);
            vignette.rounded.Override(true);
            vignette.color.Override(new Color(0f, 0f, 0f));

            // Film grain. Applied last of the additive-looking effects so it lands on the
            // tonemapped image, which is where real grain lives. Its purpose here is to break
            // up the large flat gradients that band badly in dark scenes.
            _filmGrain = _profile.Add<FilmGrain>(true);
            _filmGrain.type.Override(FilmGrainLookup.Thin1);
            _filmGrain.intensity.Override(filmGrainIntensity);

            // Response below 1 pushes grain into the shadows, which is where banding is worst
            // and where real film grain is strongest.
            _filmGrain.response.Override(0.55f);

            // Chromatic aberration. Kept very small. It is a dream cue rather than a look
            // cue, and at anything above about 0.1 it reads as a broken lens.
            _chromatic = _profile.Add<ChromaticAberration>(true);
            _chromatic.intensity.Override(chromaticAberration);

            // Lens distortion. Small barrel distortion is what makes a rendered frame read
            // as having been photographed through glass.
            _lensDistortion = _profile.Add<LensDistortion>(true);
            _lensDistortion.intensity.Override(lensDistortion);
            _lensDistortion.scale.Override(1f);

            // Camera motion blur. URP only blurs camera motion, not object motion, which is
            // precisely what a first-person game wants: the player's own head sway picks up
            // a smear while the dream itself stays sharp and still.
            _motionBlur = _profile.Add<MotionBlur>(true);
            _motionBlur.quality.Override(MotionBlurQuality.Medium);
            _motionBlur.intensity.Override(0.18f);
            _motionBlur.clamp.Override(0.06f);

            // Depth of field, configured but left to the accessibility settings.
            //
            // A very tight aperture focused at arm's length, so only the far corners of the
            // room soften. Held this weak because a first-person game lives or dies on being
            // able to read the whole frame, and depth of field is the easiest effect here to
            // make the player unable to do that.
            var dof = _profile.Add<DepthOfField>(true);
            dof.mode.Override(DepthOfFieldMode.Gaussian);
            dof.gaussianStart.Override(2.2f);
            dof.gaussianEnd.Override(7.5f);
            dof.gaussianMaxRadius.Override(0.6f);
            dof.highQualitySampling.Override(true);

            // Shadows/midtones/highlights, which gives independent control over the three
            // bands. The darkening of shadows is doing most of the work: it deepens the
            // corners without touching the moonlit wall.
            var smh = _profile.Add<ShadowsMidtonesHighlights>(true);
            smh.shadows.Override(new Vector4(0.94f, 0.97f, 1.06f, 0f));
            smh.highlights.Override(new Vector4(1f, 1f, 1f, 0f));

            _volume.sharedProfile = _profile;
        }

        /// <summary>
        /// Applies the comfort settings.
        ///
        /// Grain and chromatic aberration are the two effects here that can genuinely make a
        /// player feel ill - one is visual noise, the other is a lens effect some people
        /// cannot tolerate - so both are scaled by the same accessibility control rather than
        /// being unconditional.
        /// </summary>
        private void ApplyAccessibility()
        {
            if (_profile == null)
            {
                return;
            }

            float filmGrainScale = TriangleAccessibility.FilmGrain;
            float aberrationScale = TriangleAccessibility.ChromaticAberration;

            _filmGrain.intensity.Override(filmGrainIntensity * filmGrainScale);
            _chromatic.intensity.Override(chromaticAberration * aberrationScale);
            _motionBlur.intensity.Override(0.18f * TriangleAccessibility.MotionBlur);
        }

        /// <summary>
        /// Rebuilds the profile after the inspector values change in play mode.
        ///
        /// Only reachable from the editor, and only there because it is the reason a designer
        /// can iterate on the look without rebuilding.
        /// </summary>
        public void RebuildProfile()
        {
            if (_profile != null)
            {
                if (Application.isPlaying)
                {
                    Destroy(_profile);
                }
                else
                {
                    DestroyImmediate(_profile);
                }
            }

            BuildProfile();
            ApplyAccessibility();
        }
    }
}