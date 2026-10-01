using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using Triangle.Core;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace Triangle.EditorTools
{
    /// <summary>
    /// Creates and wires the project's Universal Render Pipeline assets.
    ///
    /// Everything else in the project is assembled from code at runtime, but the render
    /// pipeline asset cannot be: Unity loads it before any scene or script runs, so it has
    /// to exist as a real asset on disk. This class writes that asset from the editor and
    /// points Graphics and Quality settings at it.
    ///
    /// The settings are chosen for one specific target - a dark, interior-led dreamcore
    /// horror game on a GTX 1080 - rather than for general use, and the reasoning is
    /// recorded per field because several of the defaults are wrong for this project.
    ///
    /// Nearly every field on the URP asset is a private SerializeField with a read-only
    /// public property, and the SSAO feature's type is internal altogether. All of them are
    /// therefore written through the serialized representation, which means a typo looks
    /// exactly like a successful write. Every write is checked, and every miss is reported:
    /// the alternative was an installer that exits 0 while quietly leaving MSAA off.
    ///
    /// Callable from the menu, or from a terminal:
    /// <c>Unity.exe -batchmode -quit -projectPath &lt;project&gt;
    /// -executeMethod Triangle.EditorTools.TriangleRenderPipelineInstaller.Install</c>
    /// </summary>
    public static class TriangleRenderPipelineInstaller
    {
        private const string SettingsFolder = "Assets/Settings";
        private const string UrpAssetPath = SettingsFolder + "/TriangleUniversalRenderPipeline.asset";
        private const string RendererDataPath = SettingsFolder + "/TriangleUniversalRendererData.asset";
        private const string UrpAssetName = "TriangleUniversalRenderPipeline";

        private static readonly List<string> _misses = new List<string>();

        [MenuItem("Triangle/Install Render Pipeline")]
        public static void Install()
        {
            _misses.Clear();
            EnsureFolder(SettingsFolder);

            UniversalRendererData rendererData = LoadOrCreateRendererData();
            ConfigureRendererData(rendererData);
            EditorUtility.SetDirty(rendererData);

            UniversalRenderPipelineAsset urpAsset = LoadOrCreateUrpAsset(rendererData);
            ConfigureUrpAsset(urpAsset);

            AssignToGraphicsSettings(urpAsset);
            AssignToQualitySettings(urpAsset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // Reported as a group as well as individually, because a run that wrote three
            // of thirty settings must not look like a clean one.
            if (_misses.Count > 0)
            {
                TriangleLog.Warn(
                    $"Render pipeline installed, but {_misses.Count} setting(s) kept their " +
                    "defaults. See the individual warnings above.");
            }

            TriangleLog.Log("Universal Render Pipeline installed and assigned.");
        }

        [MenuItem("Triangle/Install Render Pipeline", true)]
        private static bool ValidateInstall()
        {
            return !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        // --- Asset creation ---------------------------------------------------------

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path))
            {
                return;
            }

            string parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            string leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent))
            {
                EnsureFolder(parent);
            }

            AssetDatabase.CreateFolder(parent, leaf);
        }

        private static UniversalRendererData LoadOrCreateRendererData()
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRendererData>(RendererDataPath);
            if (existing != null)
            {
                return existing;
            }

            var created = ScriptableObject.CreateInstance<UniversalRendererData>();
            created.name = "TriangleUniversalRendererData";
            AssetDatabase.CreateAsset(created, RendererDataPath);
            return created;
        }

        private static UniversalRenderPipelineAsset LoadOrCreateUrpAsset(UniversalRendererData rendererData)
        {
            var existing = AssetDatabase.LoadAssetAtPath<UniversalRenderPipelineAsset>(UrpAssetPath);
            if (existing != null)
            {
                // Re-point the asset at the renderer data. Without this, a re-run leaves
                // the asset holding whichever renderer it was created with, so later edits
                // to the renderer are silently ignored.
                var so = new SerializedObject(existing);
                SerializedProperty list = so.FindProperty("m_RendererDataList");
                if (list != null && list.arraySize > 0)
                {
                    list.GetArrayElementAtIndex(0).objectReferenceValue = rendererData;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }

                return existing;
            }

            // Create(rendererData) is the documented constructor for a URP asset built in
            // code. The parameterless path leaves the asset with no renderer at all, which
            // renders nothing and reports no error.
            UniversalRenderPipelineAsset created = UniversalRenderPipelineAsset.Create(rendererData);
            created.name = UrpAssetName;
            AssetDatabase.CreateAsset(created, UrpAssetPath);
            return created;
        }

        // --- Renderer ----------------------------------------------------------------

        private static void ConfigureRendererData(UniversalRendererData data)
        {
            // Forward, not Deferred.
            //
            // Deferred requires MSAA off and costs a full G-buffer pass, which buys nothing
            // here: the scene is a few hundred boxes and under a dozen lights. Forward also
            // keeps the translucent anomaly fades blending correctly, which Deferred would
            // force through a different path.
            data.renderingMode = RenderingMode.Forward;

            // Depth priming reuses the previous frame's depth buffer to reject overdraw,
            // which produces visible swimming on thin geometry - and the doorway cut and
            // the razor-thin wall gap are exactly that.
            data.depthPrimingMode = DepthPrimingMode.Disabled;

            // Octahedral G-buffer normals only matter in Deferred.
            data.accurateGbufferNormals = false;

            AddScreenSpaceOcclusion(data);
        }

        /// <summary>
        /// Adds screen-space ambient occlusion, the largest single readability win for an
        /// interior made of untextured boxes.
        ///
        /// Without it, contact points carry no darkness. A bed leg meeting the floor, a
        /// desk against a wall, the reveal of a window - each reads as two shapes meeting
        /// rather than as one object resting on another. That is a large part of why the
        /// build looks like untextured geometry rather than a room.
        /// </summary>
        private static void AddScreenSpaceOcclusion(UniversalRendererData data)
        {
            // Clear the list rather than appending, so re-running is idempotent.
            ClearRendererFeatures(data);

            System.Type ssaoType = FindType("UnityEngine.Rendering.Universal.ScreenSpaceAmbientOcclusion");
            if (ssaoType == null)
            {
                WarnMissing("type", "ScreenSpaceAmbientOcclusion",
                    "the pipeline is installed without screen-space occlusion");
                return;
            }

            var ssao = (ScriptableRendererFeature)ScriptableObject.CreateInstance(ssaoType);
            ssao.name = "TriangleScreenSpaceOcclusion";
            AssetDatabase.AddObjectToAsset(ssao, data);

            ConfigureSsaoSettings(ssao);

            data.rendererFeatures.Add(ssao);
            data.SetDirty();
        }

        private static void ConfigureSsaoSettings(ScriptableRendererFeature feature)
        {
            // The settings type is internal, as is the feature, so this goes through the
            // serialized view. Field names are the public-style names URP 14 declares on
            // ScreenSpaceAmbientOcclusionSettings - no m_ prefix.
            var so = new SerializedObject(feature);
            SerializedProperty settings = so.FindProperty("m_Settings");
            if (settings == null)
            {
                WarnMissing("object", "m_Settings", "ScreenSpaceAmbientOcclusion settings");
                return;
            }

            // DepthNormals rather than Depth: normals give far cleaner contact shading on
            // the large flat wall quads that make up every room here.
            SetEnum(settings, "Source", "DepthNormals");

            // Blue noise rather than the default interleaved gradient noise. The difference
            // is invisible in a still frame, but interleaved noise is tied to screen
            // position and visibly crawls as a first-person camera moves through a room -
            // which is exactly this game's camera.
            SetEnum(settings, "AOMethod", "BlueNoise");

            SetFloat(settings, "Intensity", 1f);

            // 0.35 m of contact darkening. URP's default radius is character-scale and at
            // room scale produces a hairline shadow no camera can resolve.
            SetFloat(settings, "Radius", 0.35f);

            // Skip geometry further than this from the camera. Distant clutter costs the
            // same per-sample work and contributes nothing at this fog density.
            SetFloat(settings, "Falloff", 3.5f);

            SetEnum(settings, "Samples", "Medium");
            // High is a bilateral blur: it respects depth edges, so contact shadows stay
            // tight against the object casting them. The cheaper Gaussian and Kawase
            // options bleed across the hard box edges this whole scene is made of.
            SetEnum(settings, "BlurQuality", "High");

            // Half-resolution occlusion, upsampled. Quarter is cheaper but the upsample
            // bleeds contact shadows across the hard box edges that make up every object in
            // the room, which reads as a blur rather than as contact.
            SetBool(settings, "Downsample", true);

            // After opaque, not before it.
            //
            // "After Opaque" applies occlusion as a composite pass after lighting.
            // "Before Opaque" feeds it into the lit pass as _SCREEN_SPACE_OCCLUSION, which
            // additionally darkens surfaces that are already dark - and these surfaces are
            // all very dark to begin with, so that would crush the room to black.
            //
            // A boolean rather than a render pass event: URP 14's SSAO feature derives its
            // pass event from this, and the pass it creates is internal, so there is no
            // event field on the feature to write.
            SetBool(settings, "AfterOpaque", true);

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        /// Removes every renderer feature and its backing sub-asset.
        ///
        /// Clearing only the list is not enough. A renderer feature added through
        /// <c>AddObjectToAsset</c> is a sub-asset of the renderer data, so each run that
        /// cleared the list without destroying it left a full orphaned copy in the file -
        /// and after four installer runs the asset held four SSAO features, one of them
        /// live and three of them silently consuming nothing but disk and confusion.
        /// </summary>
        private static void ClearRendererFeatures(UniversalRendererData data)
        {
            for (int i = data.rendererFeatures.Count - 1; i >= 0; i--)
            {
                ScriptableRendererFeature feature = data.rendererFeatures[i];
                if (feature == null)
                {
                    continue;
                }

                data.rendererFeatures.RemoveAt(i);
                DestroyFeature(feature);
            }

            data.rendererFeatures.Clear();

            // Then sweep the sub-assets.
            //
            // Clearing the list is not sufficient on its own. A feature added through
            // AddObjectToAsset becomes a sub-asset of the renderer data file, and the list
            // is only a reference to it - so a feature that is already unlinked but still
            // serialized survives every run and accumulates. After four installer runs the
            // file held four complete SSAO features, one live and three orphaned but still
            // occupying space and still readable as if configured.
            foreach (UnityEngine.Object sub in AssetDatabase.LoadAllAssetsAtPath(RendererDataPath))
            {
                if (sub is ScriptableRendererFeature orphan)
                {
                    DestroyFeature(orphan);
                }
            }
        }

        /// <summary>
        /// Destroys a renderer feature and removes it from the asset file.
        ///
        /// DestroyImmediate is required rather than Destroy because the installer runs in
        /// the editor outside play mode, and the sub-asset has to be gone before the file
        /// is saved - a deferred Destroy would land after the save and leave the orphan in
        /// the asset on disk.
        /// </summary>
        private static void DestroyFeature(ScriptableRendererFeature feature)
        {
            AssetDatabase.RemoveObjectFromAsset(feature);
            UnityEngine.Object.DestroyImmediate(feature, true);
        }

        // --- Pipeline asset ------------------------------------------------------------

        private static void ConfigureUrpAsset(UniversalRenderPipelineAsset asset)
        {
            var so = new SerializedObject(asset);

            // Depth and opaque textures: required by SSAO and by depth of field.
            SetBool(so, "m_RequireDepthTexture", true);
            SetBool(so, "m_RequireOpaqueTexture", true);

            // HDR on, grading in HDR.
            //
            // Bloom in URP thresholds in gamma space so it works either way, but colour
            // grading applied in LDR after tonemapping clips the deep blue-grey shadows this
            // game is built from into flat bands. Grading first, tonemapping last is the
            // film-accurate order.
            SetBool(so, "m_SupportsHDR", true);
            SetEnum(so, "m_ColorGradingMode", "HighDynamicRange");
            SetInt(so, "m_ColorGradingLutSize", 32);

            // 64-bit HDR buffer.
            //
            // This scene is almost entirely dark blue-grey gradients, which is precisely
            // the content that bands in a 32-bit buffer. At these levels the banding is not
            // subtle, and it lands on the large wall surfaces that fill most of the frame.
            SetEnum(so, "m_HDRColorBufferPrecision", "_64Bits");

            // 4x MSAA. The geometry is all hard-edged boxes - the worst case for aliasing
            // and the best case for it. TAA is not an option: it cannot be combined with
            // MSAA, and it smears on the slow camera motion this game relies on.
            SetEnum(so, "m_MSAA", "_4x");

            // Full resolution, no upscaling. FSR is deliberately left off: it would blur
            // the fine plaster grain the texture work is specifically making visible, which
            // trades away exactly the detail being added.
            SetFloat(so, "m_RenderScale", 1f);
            SetEnum(so, "m_UpscalingFilter", "Auto");

            // Main light: the moon, and the only directional shadow caster worth the cost.
            //
            // Pixel lighting rather than per-vertex, which would band across the large wall
            // quads that make up every room here.
            SetEnum(so, "m_MainLightRenderingMode", "PerPixel");
            SetBool(so, "m_MainLightShadowsSupported", true);
            SetEnum(so, "m_MainLightShadowmapResolution", "_2048");

            // Additional lights per pixel, up to 8 per object.
            //
            // URP's Forward renderer shades only this many additional lights per object
            // before falling back to the light probe or ambient. The bedroom uses about two
            // and the station about six, so 8 covers every case without the fallback
            // visibly darkening the far fluorescents.
            SetEnum(so, "m_AdditionalLightsRenderingMode", "PerPixel");
            SetInt(so, "m_AdditionalLightsPerObjectLimit", 8);

            // Additional light shadows on. A fluorescent tube that casts no shadow is the
            // clearest single tell that a light is a video game light.
            SetBool(so, "m_AdditionalLightShadowsSupported", true);
            SetEnum(so, "m_AdditionalLightsShadowmapResolution", "_1024");
            SetInt(so, "m_AdditionalLightsShadowResolutionTierHigh", 1024);
            SetInt(so, "m_AdditionalLightsShadowResolutionTierMedium", 512);
            SetInt(so, "m_AdditionalLightsShadowResolutionTierLow", 256);

            // Soft shadows. High is a 7x7 tent filter, which is what turns a hard
            // point-light shadow into something that reads as a real shadow.
            SetBool(so, "m_SoftShadowsSupported", true);
            SetEnum(so, "m_SoftShadowQuality", "High");

            // Four cascades over a short distance.
            //
            // The rooms are 6-14 m and the yard is the only open space. A long shadow
            // distance with four cascades wastes three of them on empty sky; 60 m keeps all
            // four working on the terrain.
            SetFloat(so, "m_ShadowDistance", 60f);
            SetInt(so, "m_ShadowCascadeCount", 4);
            SetFloat(so, "m_Cascade2Split", 0.18f);
            SetFloat(so, "m_CascadeBorder", 6f);

            // Bias. The default normal bias lightens shadow terminators badly on large flat
            // walls, and large flat walls are most of what this game looks at.
            SetFloat(so, "m_ShadowDepthBias", 1f);
            SetFloat(so, "m_ShadowNormalBias", 1.2f);

            // Probe blending and box projection.
            //
            // Box projection is what makes a small room's reflection probe project its
            // cubemap using the room's bounds rather than as an infinite sphere from the
            // probe's centre. Without it every reflective surface shows a reflection that
            // is only correct at the probe's own position.
            SetBool(so, "m_ReflectionProbeBlending", true);
            SetBool(so, "m_ReflectionProbeBoxProjection", true);

            // Light cookies. This is how the fluorescent tubes get their shape and the
            // window gets a soft rectangular falloff instead of a round point-light blob.
            // With the feature off, the _LIGHT_COOKIES variants are stripped from the Lit
            // shader and a cookie assigned to a light silently does nothing.
            SetBool(so, "m_SupportsLightCookies", true);
            SetEnum(so, "m_AdditionalLightsCookieFormat", "ColorHDR");

            // Rendering layers, so interior and exterior lights stay apart: the moon and
            // the window key light the yard, the fluorescents light the station, and
            // neither bleeds into the other.
            //
            // One field, not two. In URP 14 useRenderingLayers is a read-only alias for
            // supportsLightLayers over the same backing field, so the second write looked
            // plausible and did nothing.
            SetBool(so, "m_SupportsLightLayers", true);

            // SRP batcher on, dynamic batching off.
            //
            // SRP Batcher is the right default for "many materials, one shader", which is
            // exactly this project: every material is built at runtime from the same URP
            // Lit shader. Dynamic batching would compete with it over the same geometry.
            SetBool(so, "m_UseSRPBatcher", true);
            SetBool(so, "m_SupportsDynamicBatching", false);

            // No baked lighting. It cannot exist here: the world is assembled at runtime
            // and Unity's Lightmapping API is editor-only, so there is no point in the
            // player paying for the code path.
            SetBool(so, "m_MixedLightingSupported", false);

            // Adaptive performance measures frame time against a target and rescales
            // resolution underneath the player, which fights the fixed render scale above
            // and makes image quality drift during play.
            SetBool(so, "m_UseAdaptivePerformance", false);

            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // --- Assignment ---------------------------------------------------------------

        private static void AssignToGraphicsSettings(UniversalRenderPipelineAsset asset)
        {
            GraphicsSettings.defaultRenderPipeline = asset;
            GraphicsSettings.useScriptableRenderPipelineBatching = true;

            ConfigureShaderStripping();
            ConfigureGraphicsStrips();
        }

        /// <summary>
        /// Turns off automatic shader variant stripping.
        ///
        /// Automatic stripping assumes the scenes in a build describe which variants are
        /// used. This project has no authored materials at all - they are created at runtime
        /// - so automatic stripping removes every keyword combination the material factory
        /// can produce. Surfaces then render untextured, or magenta, in a player while
        /// looking perfectly correct in the editor.
        ///
        /// In 2022.3 these live on the URP Global Settings asset rather than on
        /// GraphicsSettings, and that asset's type is internal, so it is reached through
        /// its own internal Ensure() factory.
        /// </summary>
        private static void ConfigureShaderStripping()
        {
            MethodInfo ensure = FindMethod(
                "UnityEngine.Rendering.Universal.UniversalRenderPipelineGlobalSettings", "Ensure");

            var global = ensure != null
                ? ensure.Invoke(null, new object[] { string.Empty, true }) as ScriptableObject
                : null;

            if (global == null)
            {
                WarnMissing("type", "UniversalRenderPipelineGlobalSettings",
                    "shader variant stripping keeps its default behaviour");
                return;
            }

            var so = new SerializedObject(global);

            // Materials and their keywords are created at runtime, so "unused" is
            // undecidable at build time.
            SetBool(so, "m_StripUnusedVariants", false);

            // The post-processing profiles are built at runtime too. This flag assumes the
            // player never creates a profile, which is exactly what this game does - and
            // leaving it on strips _TONEMAP_ACES, _BLOOM_HQ, _FILM_GRAIN and the rest, so
            // post-processing silently does nothing in a build while looking correct in the
            // editor.
            SetBool(so, "m_StripUnusedPostProcessingVariants", false);

            // Nothing in this project declares which LOD cross-fade variants are in use, and
            // the default here is a guess that does not match the generated geometry.
            SetBool(so, "m_StripUnusedLODCrossFadeVariants", false);

            // Cheap, and it keeps the frame debugger readable in a shipped build.
            SetBool(so, "m_StripDebugVariants", false);

            so.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(global);
        }

        /// <summary>
        /// Reaches the Graphics settings fields that have no public setter.
        ///
        /// These are inspector-only in 2022.3, like most render settings. SerializedObject
        /// reaches them without reflection, and a missing field means a future Unity
        /// renamed it - in which case the default is left alone rather than written blind.
        /// </summary>
        private static void ConfigureGraphicsStrips()
        {
            var so = LoadGraphicsSettings();

            // The default light probe selection is "find closest probe", which walks the
            // probe list per renderer. With hundreds of runtime-built renderers and no
            // authored LightProbeGroups, that is pure cost; the ambient probe already
            // describes what the atmosphere component wants.
            SetGraphicsInt(so, "m_LightProbeOutsideHullStrategy", 1);

            // Every fog mode is chosen at runtime by TriangleEnvironmentAtmosphere, so the
            // exponential-squared variants have to survive even though no scene in the
            // build uses fog at all. Automatic stripping reads the scenes and finds none.
            //
            // 1 is Custom in these enums, which keeps every mode rather than the subset the
            // build's scenes happen to mention.
            SetGraphicsEnum(so, "m_FogStripping", "Custom");
            SetGraphicsEnum(so, "m_LightmapStripping", "Custom");

            // Automatic here would strip every instancing variant for the Lit shader,
            // because no material asset in the build has instancing enabled - they are all
            // created later. The terrain's 900 instanced trees depend on it.
            //
            // Member names carry spaces ("Keep All"), not the camel case the API
            // documentation suggests. Probed rather than guessed.
            SetGraphicsEnum(so, "m_InstancingStripping", "Keep All");

            if (so != null)
            {
                so.ApplyModifiedPropertiesWithoutUndo();
            }
        }

        /// <summary>
        /// Loads the project's Graphics settings as a serializable object.
        ///
        /// <c>LoadAllAssetsAtPath</c> is used rather than
        /// <c>LoadAssetAtPath</c> because GraphicsSettings lives in ProjectSettings, which
        /// is outside the asset database's normal object model - the first entry is the
        /// settings object itself.
        /// </summary>
        private static SerializedObject LoadGraphicsSettings()
        {
            UnityEngine.Object[] loaded = AssetDatabase.LoadAllAssetsAtPath(
                "ProjectSettings/GraphicsSettings.asset");
            if (loaded == null || loaded.Length == 0)
            {
                WarnMissing("asset", "ProjectSettings/GraphicsSettings.asset", "shader strips");
                return null;
            }

            for (int i = 0; i < loaded.Length; i++)
            {
                if (loaded[i] is not null)
                {
                    return new SerializedObject(loaded[i]);
                }
            }

            WarnMissing("asset", "ProjectSettings/GraphicsSettings.asset", "shader strips");
            return null;
        }

        private static void SetGraphicsInt(SerializedObject so, string field, int value)
        {
            if (so == null)
            {
                return;
            }

            SerializedProperty p = so.FindProperty(field);
            if (p != null && p.propertyType == SerializedPropertyType.Integer)
            {
                p.intValue = value;
                return;
            }

            WarnMissing("int", field, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Selects an enum member on the Graphics settings object.
        ///
        /// These three fields are enums rather than plain integers, so writing 1 into them
        /// blind is how "Custom" was previously requested and never applied.
        /// </summary>
        private static void SetGraphicsEnum(SerializedObject so, string field, string memberName)
        {
            if (so == null)
            {
                return;
            }

            SerializedProperty p = so.FindProperty(field);
            if (p == null || p.propertyType != SerializedPropertyType.Enum)
            {
                WarnMissing("enum", field, memberName);
                return;
            }

            // Matched by name against the property's own lists, not resolved through the
            // CLR field type. These fields belong to a native-backed settings type whose
            // fields are not reflectable, so the type walk finds nothing - but the
            // property itself still reports its members correctly.
            if (SelectEnumByName(p, memberName))
            {
                return;
            }

            WarnMissing("enum member", field, memberName);
        }

        /// <summary>
        /// Selects an enum member from a property's own member lists.
        ///
        /// Safe here because it matches on name and never on position. The reason the rest
        /// of this class resolves enum values through Enum.Parse is that every URP enum
        /// carries explicit values that do not match its ordinal positions, so an index
        /// lookup lands on the wrong member. Writing the parsed integer instead of the
        /// index avoids that - this helper is only used where the declaring type is not
        /// reflectable.
        /// </summary>
        private static bool SelectEnumByName(SerializedProperty property, string memberName)
        {
            string[] displayNames = property.enumDisplayNames;
            for (int i = 0; i < displayNames.Length; i++)
            {
                if (displayNames[i] == memberName)
                {
                    property.enumValueIndex = i;
                    return true;
                }
            }

            string[] names = property.enumNames;
            for (int i = 0; i < names.Length; i++)
            {
                if (names[i] == memberName)
                {
                    property.enumValueIndex = i;
                    return true;
                }
            }

            return false;
        }

        private static void AssignToQualitySettings(UniversalRenderPipelineAsset asset)
        {
            // Assigned to every tier.
            //
            // A per-tier override here is the classic URP setup failure: the tier switches
            // back to Built-in and the whole game renders with a broken shader while the
            // editor still looks correct. The project has six tiers.
            //
            // The active tier is captured before the loop, because reading it afterwards
            // returns whatever the loop last set - so the project would come back on the
            // top tier no matter which one was in use.
            int active = QualitySettings.GetQualityLevel();

            QualitySettings.renderPipeline = asset;

            string[] names = QualitySettings.names;
            for (int i = 0; i < names.Length; i++)
            {
                QualitySettings.SetQualityLevel(i, false);
                QualitySettings.renderPipeline = asset;
            }

            QualitySettings.SetQualityLevel(Mathf.Clamp(active, 0, names.Length - 1), false);
        }

        // --- Serialized field writes ------------------------------------------------------

        private static void SetBool(SerializedObject so, string field, bool value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null && p.propertyType == SerializedPropertyType.Boolean)
            {
                p.boolValue = value;
                return;
            }

            WarnMissing("bool", field, value.ToString());
        }

        private static void SetInt(SerializedObject so, string field, int value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null && (p.propertyType == SerializedPropertyType.Integer ||
                              p.propertyType == SerializedPropertyType.Enum))
            {
                p.intValue = value;
                return;
            }

            WarnMissing("int", field, value.ToString(CultureInfo.InvariantCulture));
        }

        private static void SetFloat(SerializedObject so, string field, float value)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p != null && p.propertyType == SerializedPropertyType.Float)
            {
                p.floatValue = value;
                return;
            }

            WarnMissing("float", field, value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Sets an enum field by its C# member name.
        ///
        /// Resolved through the declared field type and Enum.Parse rather than through
        /// SerializedProperty.enumValueIndex, because the index is the enum's ordinal
        /// position and every enum here has explicit values that do not match their
        /// ordinals - MsaaQuality.Disabled is 1, and matching on display names alone
        /// silently wrote "Disabled" where "4x" was asked for, leaving MSAA off.
        /// </summary>
        private static void SetEnum(SerializedObject so, string field, string memberName)
        {
            SerializedProperty p = so.FindProperty(field);
            if (p == null || p.propertyType != SerializedPropertyType.Enum)
            {
                WarnMissing("enum", field, memberName);
                return;
            }

            SetEnumValue(p, so != null ? so.targetObject?.GetType() : null, field, memberName);
        }

        private static void SetEnum(SerializedProperty container, string field, string memberName)
        {
            SerializedProperty p = container.FindPropertyRelative(field);
            if (p == null || p.propertyType != SerializedPropertyType.Enum)
            {
                WarnMissing("enum", field, memberName);
                return;
            }

            SetEnumValue(p, RootTypeOf(container), field, memberName);
        }

        /// <summary>
        /// The type of the object a serialized property tree belongs to.
        ///
        /// SerializedProperty has no parent accessor in 2022.3, so the tree cannot be walked
        /// upward from a nested property. Its serializedObject reference resolves the owning
        /// object in every case this class writes - the URP asset for pipeline settings, the
        /// SSAO feature for its own nested settings block.
        /// </summary>
        private static System.Type RootTypeOf(SerializedProperty property)
        {
            return property != null ? property.serializedObject.targetObject?.GetType() : null;
        }

        private static void SetEnumValue(
            SerializedProperty property, System.Type targetType, string field, string memberName)
        {
            // The enum is resolved from the target object's declared field, not from
            // SerializedProperty. enumTypeName is a serialized name that does not always
            // match the CLR type, and enumValueIndex is an ordinal position that every enum
            // here disagrees with because they all carry explicit values.
            System.Type enumType = ResolveEnumType(property, targetType);
            if (enumType == null || !enumType.IsEnum)
            {
                WarnMissing("enum type", field, memberName);
                return;
            }

            if (!Enum.TryParse(enumType, memberName, false, out object parsed))
            {
                WarnMissing("enum member", field, memberName);
                return;
            }

            property.intValue = Convert.ToInt32(parsed, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Finds the CLR enum type that declares a serialized enum property.
        ///
        /// Descends <c>propertyPath</c> - the full dotted location - from the root object's
        /// type. <c>propertyPath</c> rather than <c>name</c>, because <c>name</c> is only the
        /// leaf: for the SSAO feature's nested settings it returns "Source", which resolves
        /// against the feature's own type and finds no such field.
        /// </summary>
        private static System.Type ResolveEnumType(SerializedProperty property, System.Type targetType)
        {
            if (targetType == null)
            {
                return null;
            }

            string path = property.propertyPath;
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            Type current = targetType;
            foreach (string segment in path.Split('.'))
            {
                if (segment.Length == 0 || segment[0] == '$')
                {
                    // A managed reference names its target rather than a field, so the walk
                    // stops here. None of this project's enum fields are managed references,
                    // so this only guards against a shape change.
                    return current != targetType ? current : null;
                }

                FieldInfo field = current.GetField(segment,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                {
                    return null;
                }

                current = field.FieldType;
            }

            return current;
        }

        private static void SetBool(SerializedProperty container, string field, bool value)
        {
            SerializedProperty p = container.FindPropertyRelative(field);
            if (p != null && p.propertyType == SerializedPropertyType.Boolean)
            {
                p.boolValue = value;
                return;
            }

            WarnMissing("bool", field, value.ToString());
        }

        private static void SetFloat(SerializedProperty container, string field, float value)
        {
            SerializedProperty p = container.FindPropertyRelative(field);
            if (p != null && p.propertyType == SerializedPropertyType.Float)
            {
                p.floatValue = value;
                return;
            }

            WarnMissing("float", field, value.ToString(CultureInfo.InvariantCulture));
        }

        // --- Helpers ----------------------------------------------------------------------

        private static System.Type FindType(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type found = assembly.GetType(name);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>Finds a static method by type and name, internal or not.</summary>
        private static MethodInfo FindMethod(string typeName, string methodName)
        {
            System.Type type = FindType(typeName);
            if (type == null)
            {
                return null;
            }

            foreach (MethodInfo method in type.GetMethods(
                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == methodName)
                {
                    return method;
                }
            }

            return null;
        }

        /// <summary>
        /// Records a settings field that could not be written.
        ///
        /// Every write in this class is best-effort, because the targets are private
        /// SerializeFields rather than properties. Without this, a rename or a typo is
        /// indistinguishable from success at runtime.
        /// </summary>
        private static void WarnMissing(string kind, string field, string wanted)
        {
            string entry =
                $"URP setting '{field}' ({kind} '{wanted}') was not found; the pipeline " +
                "keeps its default for it.";
            if (_misses.Contains(entry))
            {
                return;
            }

            _misses.Add(entry);
            TriangleLog.Warn(entry);
        }
    }
}