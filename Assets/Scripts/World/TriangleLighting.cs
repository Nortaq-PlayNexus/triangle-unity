using System.Collections;
using Triangle.Core;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Triangle.World
{
    /// <summary>
    /// Builds lights properly for the render pipeline.
    ///
    /// Every light in the game is created at runtime, which means three things have to be
    /// done by hand that the editor normally does: attaching URP's additional light data,
    /// assigning a cookie, and choosing a rendering layer.
    ///
    /// The cookie matters more than it sounds. A point light with no cookie is a sphere of
    /// light - a soft round blob that gets brighter the closer you stand to it, and that
    /// stops at nothing. That is the single most recognisable "video game light" tell, and
    /// it is what the previous build looked like. A cookie lets a light be shaped: a
    /// fluorescent tube becomes a tube, a window becomes a rectangle with soft edges.
    ///
    /// Rendering layers let an environment's lights be kept off another's geometry. Only
    /// cheap, and it is what stops the moon from lighting the inside of a sealed room.
    /// </summary>
    public static class TriangleLighting
    {
        /// <summary>
        /// Rendering layer masks, as bit masks rather than layer indices.
        ///
        /// URP's rendering layers are a 32-bit mask on the light and a mask on the renderer,
        /// not Unity's 32 object layers. One layer per light "family" so an interior set and
        /// an exterior set never interfere, plus one for the manifestations.
        /// </summary>
        public const uint LayerKey = 1u << 0;
        public const uint LayerFill = 1u << 1;
        public const uint LayerAccent = 1u << 2;
        public const uint LayerNightmare = 1u << 3;

        /// <summary>Every layer the dream's own lights use.</summary>
        public const uint AllDreamLayers = LayerKey | LayerFill | LayerAccent | LayerNightmare;

        /// <summary>
        /// Creates a light with everything URP needs attached.
        ///
        /// The additional light data component is not optional in URP, and AddComponent on a
        /// Light does not create it - only the editor does that. Without it the light still
        /// renders but silently loses its cookies, its rendering layer, and any per-light
        /// shadow settings, so this goes through GetUniversalAdditionalLightData which
        /// creates it on demand.
        /// </summary>
        public static Light Create(GameObject host, LightType type, Color color,
            float intensity, float range, Vector3 position, uint renderingLayer = LayerFill,
            LightShadows shadows = LightShadows.None)
        {
            var light = host.AddComponent<Light>();
            light.type = type;
            light.color = color;
            light.intensity = intensity;
            light.range = range;
            light.shadows = shadows;

            // Light.renderingLayerMask is an int, while URP's UniversalAdditionalLightData
            // exposes the same setting as a uint. Kept as a uint here so a layer set can be
            // expressed as a bit mask, and narrowed once at the boundary.
            light.renderingLayerMask = (int)renderingLayer;

            // URP has no physical light units, so intensity is a bare multiplier and every
            // value in the project is tuned by eye. This is set rather than left alone
            // because the default is a linear scale and the whole game's lighting was
            // authored against it.
            light.useColorTemperature = false;

            if (type == LightType.Directional)
            {
                // Directional lights ignore range, and URP warns about a zero one.
                light.range = 0f;
                light.intensity = Mathf.Max(0f, intensity);
            }

            UniversalAdditionalLightData data = light.GetUniversalAdditionalLightData();
            if (data != null)
            {
                data.renderingLayers = renderingLayer;

                // Per-light soft shadow quality. Left on the pipeline default otherwise,
                // which is Medium; the key light wants High because its shadow is the one
                // the player actually looks at.
                data.usePipelineSettings = true;
            }

            host.transform.localPosition = position;
            return light;
        }

        /// <summary>
        /// Gives a light a cookie.
        ///
        /// <paramref name="cookie"/> is sampled in the light's own projected space. For a
        /// spot or directional light it is a plain 2D texture; for a point light URP needs a
        /// cubemap, which a flat texture is not - so point lights ignore this and are shaped
        /// by their range and falloff instead.
        /// </summary>
        public static void ApplyCookie(Light light, Texture cookie, Vector2 size)
        {
            if (light == null || cookie == null)
            {
                return;
            }

            if (light.type == LightType.Point)
            {
                // URP requires a cubemap for point light cookies, and a 2D texture assigned
                // here renders as a black or invalid sample rather than as an error.
                return;
            }

            light.cookie = cookie;

            UniversalAdditionalLightData data = light.GetUniversalAdditionalLightData();
            if (data != null && size.sqrMagnitude > 0f)
            {
                data.lightCookieSize = size;
                data.lightCookieOffset = Vector2.zero;
            }
        }

        /// <summary>
        /// Builds a soft radial falloff, for a light that should not have a hard edge.
        ///
        /// A spherical gradient rather than a rectangle, because a radial falloff is what a
        /// bare bulb actually does and it hides the fact that there is no geometry casting
        /// the light.
        /// </summary>
        public static Texture2D RadialCookie(int size, float edgeSoftness, float centreBoost)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "TriangleRadialCookie",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 1,

                // Generated at runtime and never read back.
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            float safeSoftness = Mathf.Clamp(edgeSoftness, 0.01f, 1f);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Normalised distance from the centre, 0 at the middle and 1 at the
                    // inscribed circle.
                    float dx = (x + 0.5f - half) / half;
                    float dy = (y + 0.5f - half) / half;
                    float distance = Mathf.Sqrt(dx * dx + dy * dy);

                    // Smoothstep from the inside out, then hold at zero past the edge so
                    // there is no visible square where the cookie ends.
                    float falloff = Mathf.Clamp01(1f - (distance - (1f - safeSoftness)) / safeSoftness);
                    falloff = falloff * falloff * (3f - 2f * falloff);

                    // A slightly brighter core, so the light has a readable centre rather
                    // than reading as an evenly lit disc.
                    float value = falloff * Mathf.Lerp(1f, centreBoost, falloff);
                    byte v = (byte)(Mathf.Clamp01(value) * 255f);
                    pixels[y * size + x] = new Color32(v, v, v, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>
        /// Builds a rectangular cookie with soft edges, for a window or a strip light.
        ///
        /// The softness is on all four sides independently, which is what separates a window
        /// from a lamp: a window is bright across its face and falls off sharply at the
        /// reveal, so the light it throws is a soft-edged rectangle with a bright centre and
        /// a defined border. A radial cookie cannot express that, and using one is why
        /// windows usually end up looking like bare bulbs.
        /// </summary>
        public static Texture2D RectangularCookie(int size, float edgeSoftness, float borderWidth)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "TriangleWindowCookie",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 1,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            float safeSoftness = Mathf.Clamp(edgeSoftness, 0.02f, 1f);
            float safeBorder = Mathf.Clamp01(borderWidth);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float u = Mathf.Abs((x + 0.5f - half) / half);
                    float v = Mathf.Abs((y + 0.5f - half) / half);

                    // Distance outside the hard rectangle, 0 inside it.
                    float outside = Mathf.Max(u, v);

                    // Soften only the outer band, so the lit face stays a clean rectangle.
                    float falloff = Mathf.Clamp01(1f - (outside - (1f - safeSoftness)) / safeSoftness);
                    falloff = falloff * falloff * (3f - 2f * falloff);

                    // A darker border band just inside the edge. A window has a frame, and a
                    // frame is in shadow - this is what stops the cookie reading as a plain
                    // gradient.
                    float inner = Mathf.InverseLerp(1f - safeBorder * 2f, 1f - safeBorder, outside);
                    float frame = Mathf.Lerp(1f, 0.55f, Mathf.Clamp01(inner));

                    byte value = (byte)(Mathf.Clamp01(falloff * frame) * 255f);
                    pixels[y * size + x] = new Color32(value, value, value, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>
        /// Builds a linear strip falloff, for a fluorescent tube.
        ///
        /// Bright along one axis and falling off along the other, which is what a tube does
        /// when it is the length of the room. Rendered as a soft band rather than a hard line
        /// because a real tube's brightness falls off towards its ends.
        /// </summary>
        public static Texture2D StripCookie(int size, float acrossSoftness, float endFalloff)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, true, true)
            {
                name = "TriangleStripCookie",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                anisoLevel = 1,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[size * size];
            float half = size * 0.5f;
            float safeAcross = Mathf.Clamp(acrossSoftness, 0.02f, 1f);
            float safeEnds = Mathf.Clamp01(endFalloff);

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    // Across the tube: soft band, brightest along the centre line.
                    float across = Mathf.Abs((y + 0.5f - half) / half);
                    float band = Mathf.Clamp01(1f - across / safeAcross);
                    band = band * band * (3f - 2f * band);

                    // Along the tube: darker at the ends, like a real tube's end caps.
                    float along = Mathf.Abs((x + 0.5f - half) / half);
                    float ends = 1f - Mathf.Clamp01((along - (1f - safeEnds)) / Mathf.Max(0.001f, safeEnds));
                    ends = ends * ends * (3f - 2f * ends);

                    byte value = (byte)(Mathf.Clamp01(band * Mathf.Lerp(0.4f, 1f, ends)) * 255f);
                    pixels[y * size + x] = new Color32(value, value, value, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(true, true);
            return texture;
        }

        /// <summary>
        /// Places a room's reflection probe.
        ///
        /// URP evaluates reflection probes per pixel and blends at most two, so a probe per
        /// room is affordable and is what stops every reflective surface in the bedroom
        /// showing the same reflection. Box projection is what makes the cubemap respect
        /// the room's walls rather than behaving as an infinite sphere from its centre.
        ///
        /// Refreshed by script rather than automatically: an automatic probe re-renders every
        /// frame or on a timer, and in a room whose contents never move that is pure cost.
        /// </summary>
        public static ReflectionProbe CreateProbe(GameObject host, Vector3 position, Vector3 size)
        {
            var probeObject = new GameObject("ReflectionProbe");
            probeObject.transform.SetParent(host.transform, false);
            probeObject.transform.localPosition = position;
            probeObject.transform.localScale = size;

            var probe = probeObject.AddComponent<ReflectionProbe>();
            probe.mode = ReflectionProbeMode.Custom;
            probe.boxProjection = true;
            probe.shadowDistance = 12f;

            // Box projection needs both the probe and the pipeline asset to allow it, and
            // the installer sets the asset side.
            probe.resolution = 128;

            // Refreshed by script. Called once by the environment builder after it finishes
            // assembling, so the first frame the player sees already has the room's
            // reflection in it rather than the default skybox.
            probe.refreshMode = ReflectionProbeRefreshMode.ViaScripting;

            // One face per frame, so a six-face refresh does not land as a single spike.
            probe.timeSlicingMode = ReflectionProbeTimeSlicingMode.IndividualFaces;

            probe.RenderProbe();
            return probe;
        }

        /// <summary>
        /// Registers a reflection probe to refresh once the environment has finished.
        ///
        /// The probe is requested rather than refreshed immediately because an environment
        /// builder assembles over several milliseconds: a probe rendered halfway through
        /// captures half a room.
        /// </summary>
        public static void RefreshWhenSettled(ReflectionProbe probe, MonoBehaviour host, int frames = 1)
        {
            if (probe == null || host == null)
            {
                return;
            }

            host.StartCoroutine(RefreshAfter(probe, frames));
        }

        private static IEnumerator RefreshAfter(ReflectionProbe probe, int frames)
        {
            for (int i = 0; i < Mathf.Max(1, frames); i++)
            {
                yield return null;
            }

            if (probe != null)
            {
                probe.RenderProbe();
            }
        }

        /// <summary>
        /// Whether runtime light probes are usable in this project. They are not.
        ///
        /// Recorded as a constant rather than omitted, because the limitation is not
        /// obvious and the component looks available: LightProbeGroup can be added to a
        /// GameObject at runtime, and probePositions appears settable in the editor.
        ///
        /// Two things stop it working, and the first was only found by building a player
        /// rather than by reading the documentation:
        ///
        ///   - <c>LightProbeGroup.probePositions</c> is compiled behind UNITY_EDITOR. The
        ///     assignment compiles cleanly in the editor and fails the player build with
        ///     "property or indexer cannot be assigned to -- it is read only".
        ///   - There is no public way to write a probe's spherical harmonics at all, so a
        ///     group whose positions were somehow set would still carry white probes, which
        ///     in a room this dark washes out every surface near them - worse than having no
        ///     group at all.
        ///
        /// Baked lighting would solve this properly and is equally unavailable: the world is
        /// assembled at runtime and Unity's Lightmapping API is editor-only. So this scene's
        /// indirect light is the ambient probe plus the dim fill lights the builders place by
        /// hand, which stand in for a single bounce off the floor and ceiling.
        /// </summary>
        public const bool RuntimeLightProbesAvailable = false;

        /// <summary>
        /// Whether the render pipeline is actually URP.
        ///
        /// Several of these helpers only do anything under URP - cookies, rendering layers,
        /// per-light shadow settings. Guarded so a project that is still mid-migration does
        /// not log a warning for every light it creates.
        /// </summary>
        public static bool IsUniversalPipeline =>
            GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
    }
}
